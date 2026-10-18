using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace VibeDoze.Core;

public enum UpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Ready,
    Error,
}

/// <summary>
/// Self-update from GitHub Releases: checks the latest release, downloads VibeDoze.zip (or VibeDoze.exe),
/// verifies it against VibeDoze.exe.sha256 and hands the swap to VibeDoze.Hook.exe, which replaces the exe
/// once this process has exited and starts the new version.
/// </summary>
internal sealed class Updater
{
    // Declared before Http: static initializers run in textual order and the client's User-Agent needs it.
    public static Version CurrentVersion { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version);

    private static readonly HttpClient Http = CreateClient();
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Updater(AppSettings settings)
    {
        _settings = settings;
        CleanupLeftovers();
    }

    public UpdateStatus Status { get; private set; }
    public Version? LatestVersion { get; private set; }
    public string? ReleaseUrl { get; private set; }
    public string? ReadyExe { get; private set; }
    public string? Error { get; private set; }
    public int? DownloadPercent { get; private set; }
    public DateTime? LastCheckUtc { get; private set; }

    public bool Busy => Status is UpdateStatus.Checking or UpdateStatus.Downloading;

    /// <summary>Checks GitHub; when <paramref name="download"/> is true also downloads and verifies a newer version.</summary>
    public async Task CheckAsync(bool download)
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            LastCheckUtc = DateTime.UtcNow;
            Error = null;
            Status = UpdateStatus.Checking;

            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{_settings.UpdateRepository}/releases/latest");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"GitHub respondió {(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            ReleaseUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() : null;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed))
                throw new InvalidOperationException($"Versión no reconocida: {tag}");
            var latest = Normalize(parsed);
            LatestVersion = latest;

            if (latest <= CurrentVersion)
            {
                Status = UpdateStatus.UpToDate;
                return;
            }

            string dir = Path.Combine(AppPaths.LocalDir, "update");
            string target = Path.Combine(dir, $"VibeDoze-{latest}.exe");
            if (ReadyExe == target && File.Exists(target))
            {
                Status = UpdateStatus.Ready;
                return;
            }
            if (!download)
            {
                Status = UpdateStatus.Available;
                return;
            }

            var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() is { } name &&
                    asset.GetProperty("browser_download_url").GetString() is { } link)
                    assets[name] = link;
            }
            if (!assets.TryGetValue("VibeDoze.exe.sha256", out var shaUrl))
                throw new InvalidOperationException("La versión publicada no trae VibeDoze.exe.sha256; no se instala sin verificar");

            string expected = (await Http.GetStringAsync(shaUrl).ConfigureAwait(false))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0].Trim().ToLowerInvariant();

            // Already downloaded by a previous run (e.g. the app restarted before installing it)?
            if (File.Exists(target) && await HashAsync(target).ConfigureAwait(false) == expected)
            {
                ReadyExe = target;
                Status = UpdateStatus.Ready;
                return;
            }

            Status = UpdateStatus.Downloading;
            DownloadPercent = 0;
            Directory.CreateDirectory(dir);
            string part = target + ".part";
            if (assets.TryGetValue("VibeDoze.zip", out var zipUrl))
            {
                string zip = Path.Combine(dir, $"VibeDoze-{latest}.zip");
                await DownloadAsync(zipUrl, zip).ConfigureAwait(false);
                using (var archive = ZipFile.OpenRead(zip))
                {
                    var entry = archive.GetEntry("VibeDoze.exe") ?? throw new InvalidDataException("VibeDoze.zip no contiene VibeDoze.exe");
                    entry.ExtractToFile(part, overwrite: true);
                }
                File.Delete(zip);
            }
            else if (assets.TryGetValue("VibeDoze.exe", out var exeUrl))
            {
                await DownloadAsync(exeUrl, part).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException("La versión publicada no trae VibeDoze.exe");
            }

            if (await HashAsync(part).ConfigureAwait(false) != expected)
            {
                File.Delete(part);
                throw new InvalidDataException("La descarga no coincide con su SHA-256; se descartó");
            }

            File.Move(part, target, overwrite: true);
            ReadyExe = target;
            Status = UpdateStatus.Ready;
            Log.Info($"Actualización {latest} descargada y verificada");
        }
        catch (Exception ex)
        {
            Error = ex is HttpRequestException or TaskCanceledException ? "sin conexión con GitHub" : ex.Message;
            Status = UpdateStatus.Error;
            Log.Warn($"Actualización: {ex.Message}");
        }
        finally
        {
            DownloadPercent = null;
            _gate.Release();
        }
    }

    /// <summary>
    /// Starts VibeDoze.Hook.exe to swap the exe after we exit. The caller must quit right after a true return.
    /// </summary>
    public bool TryStartInstall(out string? error)
    {
        error = null;
        if (Status != UpdateStatus.Ready || ReadyExe is null || !File.Exists(ReadyExe))
        {
            error = "No hay una actualización lista";
            return false;
        }
        if (!HookRelay.IsLightweight)
        {
            error = "Falta VibeDoze.Hook.exe para instalar la actualización";
            return false;
        }
        if (!IsWritable(Path.GetDirectoryName(AppPaths.ExePath)!))
        {
            error = "No hay permiso para reemplazar el ejecutable en su carpeta; descarga la versión nueva a mano";
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(HookRelay.Path,
                $"update {Environment.ProcessId} \"{ReadyExe}\" \"{AppPaths.ExePath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Log.Info($"Instalando la versión {LatestVersion}; VibeDoze se reiniciará");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private async Task DownloadAsync(string url, string path)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var file = File.Create(path);
        var buffer = new byte[128 * 1024];
        long done = 0;
        int n;
        while ((n = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
            done += n;
            if (total > 0) DownloadPercent = (int)(done * 100 / total.Value);
        }
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static void CleanupLeftovers()
    {
        try
        {
            var dir = Path.Combine(AppPaths.LocalDir, "update");
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir))
            {
                var name = Path.GetFileNameWithoutExtension(file).Replace(".exe", string.Empty);
                bool stale = file.EndsWith(".part") || file.EndsWith(".zip")
                             || (Version.TryParse(name.Replace("VibeDoze-", string.Empty), out var v) && Normalize(v) <= CurrentVersion);
                if (stale) File.Delete(file);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".vibedoze-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Version Normalize(Version? v) =>
        v is null ? new Version(0, 0, 0) : new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VibeDoze", CurrentVersion.ToString()));
        return client;
    }
}

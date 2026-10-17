using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace VibeDoze.Core;

/// <summary>One hook event forwarded from an agent (Claude Code / Codex) to the running VibeDoze.</summary>
public sealed class HookMessage
{
    public string Agent { get; set; } = "claude";
    public string Event { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string? Cwd { get; set; }
    public string? NotificationType { get; set; }
    public int AgentPid { get; set; }
    public long AgentCreation { get; set; }
    /// <summary>When the hook process started (FILETIME). Async hooks can arrive out of order; this sorts them.</summary>
    public long ClientTicks { get; set; }

    /// <summary>
    /// Wire format (one line): "v2\tagent\tevent\tsession\tcwd\tnotificationType".
    /// Written by VibeDoze.Hook.exe (.NET Framework, no JSON library) and by the fallback "VibeDoze.exe hook".
    /// </summary>
    public static HookMessage? Parse(string line)
    {
        var parts = line.TrimEnd('\r', '\n').Split('\t');
        if (parts.Length < 3 || parts[0] != "v2") return null;
        static string? At(string[] p, int i) => i < p.Length && p[i].Length > 0 ? p[i] : null;
        return new HookMessage
        {
            Agent = At(parts, 1) == "codex" ? "codex" : "claude",
            Event = At(parts, 2) ?? "unknown",
            SessionId = At(parts, 3),
            Cwd = At(parts, 4),
            NotificationType = At(parts, 5),
        };
    }

    public static string Format(string agent, string evt, string? session, string? cwd, string? notificationType)
    {
        static string Clean(string? v) => (v ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        return string.Join('\t', "v2", Clean(agent), Clean(evt), Clean(session), Clean(cwd), Clean(notificationType)) + "\n";
    }
}

/// <summary>
/// Fallback relay "VibeDoze.exe hook claude|codex" (same job as VibeDoze.Hook.exe, slower to start).
/// Reads the hook JSON from stdin, forwards one line over the pipe and exits 0 no matter what.
/// </summary>
internal static class HookClient
{
    private const int KeepBytes = 256 * 1024;

    public static int Run(string[] args)
    {
        try
        {
            string agent = args.Length > 0 && args[0].Equals("codex", StringComparison.OrdinalIgnoreCase) ? "codex" : "claude";
            byte[] payload = ReadStdin();
            if (payload.Length == 0 && args.Length > 1 && args[^1].TrimStart().StartsWith('{'))
                payload = Encoding.UTF8.GetBytes(args[^1]);

            var f = ReadTopLevelStrings(payload);
            string line = HookMessage.Format(agent,
                f.GetValueOrDefault("hook_event_name") ?? f.GetValueOrDefault("type") ?? "unknown",
                f.GetValueOrDefault("session_id") ?? f.GetValueOrDefault("thread-id"),
                f.GetValueOrDefault("cwd"),
                f.GetValueOrDefault("notification_type"));

            using var pipe = new NamedPipeClientStream(".", AppPaths.PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(300);
            var bytes = Encoding.UTF8.GetBytes(line);
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            var ack = pipe.ReadAsync(new byte[1], 0, 1);
            ack.Wait(1500); // stay alive while VibeDoze inspects our parent processes
        }
        catch
        {
            // VibeDoze not running, or anything else: never disturb the agent.
        }
        return 0;
    }

    private static byte[] ReadStdin()
    {
        IntPtr handle = Native.GetStdInputHandle();
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return Array.Empty<byte>();
        uint type = Native.GetFileType(handle);
        if (type != Native.FILE_TYPE_PIPE && type != Native.FILE_TYPE_DISK) return Array.Empty<byte>();

        var read = Task.Run(() =>
        {
            using var stdin = Console.OpenStandardInput();
            using var kept = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = stdin.Read(buffer, 0, buffer.Length)) > 0)
            {
                // keep the head, drain the rest so the agent never sees a broken pipe
                if (kept.Length < KeepBytes) kept.Write(buffer, 0, (int)Math.Min(n, KeepBytes - kept.Length));
            }
            return kept.ToArray();
        });
        return read.Wait(TimeSpan.FromSeconds(3)) ? read.Result : Array.Empty<byte>();
    }

    /// <summary>Reads top-level string properties; tolerates truncated JSON.</summary>
    internal static Dictionary<string, string> ReadTopLevelStrings(byte[] json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (json.Length == 0) return result;
        try
        {
            var reader = new Utf8JsonReader(json, isFinalBlock: false, state: default);
            string? property = null;
            while (reader.Read())
            {
                if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName)
                {
                    property = reader.GetString();
                    continue;
                }
                if (reader.CurrentDepth == 1 && property is not null && reader.TokenType == JsonTokenType.String)
                {
                    var value = reader.GetString();
                    if (value is not null && value.Length < 4096) result[property] = value;
                }
                property = null;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray
                    && reader.CurrentDepth >= 1 && !reader.TrySkip())
                    break; // nested value cut off by truncation
            }
        }
        catch (JsonException)
        {
            // truncated / partial payload — keep what we got
        }
        return result;
    }
}

/// <summary>
/// Named-pipe server inside the running app. For each message it identifies the relay process
/// (GetNamedPipeClientProcessId), walks up its parents to find the agent that ran the hook, then acks.
/// </summary>
internal sealed class HookServer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<HookMessage> _onMessage;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    /// <summary>
    /// Owner gets full control, plus the RESTRICTED SID: Codex's Windows sandbox runs hooks with a restricted
    /// token, and "current user only" would silently drop every Codex event.
    /// </summary>
    private static readonly Lazy<PipeSecurity> PipeAcl = new(() =>
    {
        var security = new PipeSecurity();
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        foreach (var sid in new[] { System.Security.Principal.WellKnownSidType.RestrictedCodeSid })
        {
            security.AddAccessRule(new PipeAccessRule(new System.Security.Principal.SecurityIdentifier(sid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, System.Security.AccessControl.AccessControlType.Allow));
        }
        return security;
    });

    public HookServer(Action<HookMessage> onMessage)
    {
        _onMessage = onMessage;
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(AppPaths.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, PipeAcl.Value);
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                var connected = server;
                server = null;
                _ = Task.Run(() => Handle(connected));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Pipe de hooks", ex);
                await Task.Delay(1000).ConfigureAwait(false);
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private async Task Handle(NamedPipeServerStream stream)
    {
        try
        {
            using (stream)
            {
                string? line = await ReadLine(stream).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) return;
                var message = HookMessage.Parse(line);
                if (message is null) return;

                if (GetNamedPipeClientProcessId(stream.SafePipeHandle, out uint clientPid) && clientPid != 0)
                {
                    if (ProcessTable.TryGetTimes((int)clientPid, out long started, out _)) message.ClientTicks = started;
                    var wanted = message.Agent == "codex" ? AgentKind.Codex : AgentKind.Claude;
                    var (pid, creation) = AgentLocator.FindAncestor((int)clientPid, wanted);
                    message.AgentPid = pid;
                    message.AgentCreation = creation;
                }

                try
                {
                    await stream.WriteAsync(new byte[] { 1 }).ConfigureAwait(false); // let the relay exit
                }
                catch (IOException)
                {
                    // relay already gone
                }
                _onMessage(message);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Mensaje de hook inválido: {ex.Message}");
        }
    }

    private static async Task<string?> ReadLine(Stream stream)
    {
        var buffer = new byte[8192];
        int total = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
            if (Array.IndexOf(buffer, (byte)'\n', 0, total) >= 0) break;
        }
        return total == 0 ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }

    public void Dispose() => _cts.Cancel();
}

/// <summary>Extracts the embedded VibeDoze.Hook.exe relay to %LOCALAPPDATA%\VibeDoze\bin.</summary>
internal static class HookRelay
{
    private const string ResourceName = "VibeDoze.Hook.exe";

    /// <summary>Path the hooks should run. Falls back to the main exe if the relay can't be extracted.</summary>
    public static string Path { get; } = Extract();

    public static bool IsLightweight => !string.Equals(Path, AppPaths.ExePath, StringComparison.OrdinalIgnoreCase);

    private static string Extract()
    {
        try
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (resource is null) return AppPaths.ExePath;

            using var memory = new MemoryStream();
            resource.CopyTo(memory);
            var bytes = memory.ToArray();

            var dir = System.IO.Path.Combine(AppPaths.LocalDir, "bin");
            Directory.CreateDirectory(dir);
            var target = System.IO.Path.Combine(dir, ResourceName);
            if (File.Exists(target) && new FileInfo(target).Length == bytes.Length && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
                return target;

            try
            {
                var tmp = target + ".new";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, target, overwrite: true);
            }
            catch (IOException) when (File.Exists(target))
            {
                // An older relay is running right now; it is still compatible — keep using it.
            }
            return target;
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo preparar VibeDoze.Hook.exe", ex);
            return AppPaths.ExePath;
        }
    }
}

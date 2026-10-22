using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace VibeDoze.Core;

/// <summary>
/// Hooks from agents on another machine reached over SSH (an EC2 instance, a server). The SSH connection forwards
/// a port of the server back to this laptop (RemoteForward 47811 127.0.0.1:47811) and a small script on the server
/// posts each hook event with curl. Loopback only, and every event carries a token: any local process, or another
/// user of the server, can reach the port.
/// </summary>
internal sealed class RemoteHookServer : IDisposable
{
    private static readonly TimeSpan BindRetry = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private const int MaxHeaderBytes = 16 * 1024;
    /// <summary>The server script already cuts the payload; the fields we read are at its start.</summary>
    private const int MaxBodyBytes = 256 * 1024;

    private readonly Action<HookMessage> _onMessage;
    private CancellationTokenSource? _cts;
    private int _port;
    private volatile string _token = string.Empty;
    private readonly HashSet<string> _hostsSeen = new();

    public RemoteHookServer(Action<HookMessage> onMessage) => _onMessage = onMessage;

    /// <summary>Port it listens on right now (null: off, or not bound yet).</summary>
    public int? ListeningPort { get; private set; }
    /// <summary>Why it can't listen (the port is taken…), or null.</summary>
    public string? Error { get; private set; }
    /// <summary>Last server that reached us with the right token, and when.</summary>
    public string? LastHost { get; private set; }
    public DateTime? LastContactUtc { get; private set; }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Starts, stops or moves the listener. Called on the UI thread.</summary>
    /// <param name="port">0 turns it off.</param>
    public void Configure(int port, string token)
    {
        _token = token;
        if (port == _port) return;
        _cts?.Cancel(); // not disposed: its listener task may still read the token
        _cts = null;
        _port = port;
        ListeningPort = null;
        Error = null;
        if (port == 0) return;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(() => Listen(port, cts.Token));
    }

    private async Task Listen(int port, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            try
            {
                listener.Start();
            }
            catch (SocketException ex)
            {
                string error = ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied
                    ? $"El puerto {port} está en uso"
                    : ex.Message;
                if (Error != error) Log.Warn($"Sesiones por SSH: no se pudo escuchar en 127.0.0.1:{port} ({ex.SocketErrorCode})");
                if (!ct.IsCancellationRequested) Error = error;
                try
                {
                    await Task.Delay(BindRetry, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            using var stop = ct.Register(listener.Stop); // Configure's Cancel frees the port at once
            if (ct.IsCancellationRequested) return;
            try
            {
                Error = null;
                ListeningPort = port;
                Log.Info($"Sesiones por SSH: escuchando en 127.0.0.1:{port}");
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    _ = Task.Run(() => Handle(client));
                }
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Sesiones por SSH", ex);
                ListeningPort = null;
                await Task.Delay(1000).ConfigureAwait(false);
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = new CancellationTokenSource(RequestTimeout);
                var stream = client.GetStream();
                var request = await Request.Read(stream, timeout.Token).ConfigureAwait(false);
                if (request is null) return;
                var (status, text) = Respond(request);
                await Reply(stream, status, text, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // client gone, or too slow
            }
            catch (Exception ex)
            {
                Log.Warn($"Sesiones por SSH: petición inválida ({ex.Message})");
            }
        }
    }

    private (int Status, string? Text) Respond(Request r)
    {
        var segments = r.Path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string route = segments.Length > 0 ? segments[0].ToLowerInvariant() : string.Empty;

        if (r.Method == "GET" && route is "" or "ping")
            return (200, "VibeDoze: el túnel funciona.\n");

        if (r.Method == "GET" && route == "setup")
        {
            if (!Authorized(r.Query.GetValueOrDefault("token")))
                return (403, "VibeDoze: token inválido. Copia el comando otra vez desde Ajustes › Agentes.\n");
            return (200, RemoteSetup.Script(_port, _token));
        }

        if (r.Method != "POST" || route is not ("hook" or "ping")) return (404, null);
        var auth = r.Headers.GetValueOrDefault("authorization");
        if (!Authorized(auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null))
            return (403, null);

        string host = CleanHost(r.Headers.GetValueOrDefault("x-vibedoze-host"));
        LastHost = host;
        LastContactUtc = DateTime.UtcNow;
        lock (_hostsSeen)
        {
            if (_hostsSeen.Add(host)) Log.Info($"Sesiones por SSH: primer contacto de {host}");
        }
        if (route == "ping") return (204, null);

        string agent = segments.Length > 1 && segments[1].Equals("codex", StringComparison.OrdinalIgnoreCase) ? "codex" : "claude";
        var f = HookClient.ReadTopLevelStrings(r.Body);
        _onMessage(new HookMessage
        {
            Agent = agent,
            // The script names the event in the path: the payload may come cut.
            Event = segments.Length > 2 ? segments[2] : f.GetValueOrDefault("hook_event_name") ?? f.GetValueOrDefault("type") ?? "unknown",
            SessionId = f.GetValueOrDefault("session_id") ?? f.GetValueOrDefault("thread-id"),
            Cwd = f.GetValueOrDefault("cwd"),
            NotificationType = f.GetValueOrDefault("notification_type"),
            ClientTicks = UnixNanosToFileTime(r.Headers.GetValueOrDefault("x-vibedoze-time")),
            RemoteHost = host,
        });
        return (204, null);
    }

    private bool Authorized(string? token)
    {
        string expected = _token;
        return token is not null && expected.Length > 0
               && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
    }

    private static string CleanHost(string? host)
    {
        var clean = new string((host ?? string.Empty).Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').Take(48).ToArray());
        return clean.Length > 0 ? clean : "servidor";
    }

    /// <summary>When the hook started on the server (date +%s%N), as FILETIME: async hooks can arrive out of order.</summary>
    private static long UnixNanosToFileTime(string? nanos) =>
        long.TryParse(nanos, out long ns) && ns > 0 ? ns / 100 + 116444736000000000L : 0;

    private static async Task Reply(Stream stream, int status, string? text, CancellationToken ct)
    {
        string reason = status switch
        {
            200 => "OK",
            204 => "No Content",
            403 => "Forbidden",
            404 => "Not Found",
            _ => "Bad Request",
        };
        var body = text is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(text);
        var head = new StringBuilder($"HTTP/1.1 {status} {reason}\r\nConnection: close\r\n");
        if (status != 204) head.Append($"Content-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>Just enough HTTP/1.1 for curl: request line, headers, a Content-Length body.</summary>
    private sealed record Request(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers, byte[] Body)
    {
        public static async Task<Request?> Read(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[MaxHeaderBytes];
            int total = 0, headerEnd = -1;
            while (headerEnd < 0)
            {
                if (total == buffer.Length) return null;
                int n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
                if (n == 0) return null;
                total += n;
                headerEnd = buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8);
            }

            var lines = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            string target = first[1];
            int q = target.IndexOf('?');
            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (q >= 0)
            {
                foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0) query[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
                }
                target = target[..q];
            }

            int length = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out int v) ? Math.Max(0, v) : 0;
            int already = total - (headerEnd + 4);
            if (length > already && headers.TryGetValue("expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);

            var body = new byte[Math.Min(length, MaxBodyBytes)];
            int got = Math.Min(already, body.Length);
            Array.Copy(buffer, headerEnd + 4, body, 0, got);
            while (got < body.Length)
            {
                int n = await stream.ReadAsync(body.AsMemory(got), ct).ConfigureAwait(false);
                if (n == 0) break;
                got += n;
            }
            return new Request(first[0].ToUpperInvariant(), target, query, headers, body.AsSpan(0, got).ToArray());
        }
    }
}

/// <summary>What the user copies: the SSH tunnel line and the one-line install for the server.</summary>
internal static class RemoteSetup
{
    public const int DefaultPort = 47811;

    public static string TunnelLine(int port) => $"RemoteForward {port} 127.0.0.1:{port}";

    public static string InstallCommand(int port, string token) =>
        $"curl -fsS \"http://127.0.0.1:{port}/setup?token={token}\" | sh";

    /// <summary>
    /// POSIX sh served at /setup: writes ~/.vibedoze (config + hook.sh) and adds the hooks to Claude Code
    /// (~/.claude/settings.json) and, if it's there, Codex (~/.codex/hooks.json). "sh -s -- remove" undoes it.
    /// Python edits the JSON: every EC2 image has it (cloud-init runs on it).
    /// </summary>
    public static string Script(int port, string token) => Template
        .Replace("\r\n", "\n")
        .Replace("__PORT__", port.ToString())
        .Replace("__TOKEN__", token);

    private const string Template = """
        #!/bin/sh
        # VibeDoze: hooks so Claude Code and Codex on this server tell your laptop when they work.
        # The events travel through the SSH tunnel (RemoteForward __PORT__ 127.0.0.1:__PORT__) and never block the agent.
        #   Install:  curl -fsS "http://127.0.0.1:__PORT__/setup?token=__TOKEN__" | sh
        #   Name:     curl -fsS "http://127.0.0.1:__PORT__/setup?token=__TOKEN__" | VIBEDOZE_NAME=my-server sh
        #   Remove:   curl -fsS "http://127.0.0.1:__PORT__/setup?token=__TOKEN__" | sh -s -- remove
        set -e
        DIR="$HOME/.vibedoze"
        ACTION="${1:-install}"

        if command -v python3 >/dev/null 2>&1; then PY=python3
        elif command -v python >/dev/null 2>&1; then PY=python
        else
          echo "VibeDoze: hace falta python3 para editar la configuración de los agentes." >&2
          exit 1
        fi

        if [ "$ACTION" != remove ]; then
          NAME=$(printf '%s' "${VIBEDOZE_NAME:-$(hostname -s 2>/dev/null || hostname)}" | tr -cd 'A-Za-z0-9._-')
          mkdir -p "$DIR"
          chmod 700 "$DIR"
          cat > "$DIR/config" <<EOF
        VIBEDOZE_PORT=__PORT__
        VIBEDOZE_TOKEN=__TOKEN__
        VIBEDOZE_NAME=${NAME:-servidor}
        EOF
          chmod 600 "$DIR/config"
          cat > "$DIR/hook.sh" <<'EOF'
        #!/bin/sh
        # VibeDoze: sends one agent event to the laptop through the SSH tunnel. Never blocks or fails the agent.
        # Arguments: agent (claude|codex) and event. Without the tunnel (SSH closed) curl fails at once.
        . "$HOME/.vibedoze/config" 2>/dev/null || exit 0
        { head -c 65536; cat >/dev/null; } | curl -s -m 3 -o /dev/null -H 'Expect:' \
          -H "Authorization: Bearer $VIBEDOZE_TOKEN" -H "X-VibeDoze-Host: $VIBEDOZE_NAME" \
          -H "X-VibeDoze-Time: $(date +%s%N)" \
          --data-binary @- "http://127.0.0.1:$VIBEDOZE_PORT/hook/$1/$2" >/dev/null 2>&1
        exit 0
        EOF
          chmod 700 "$DIR/hook.sh"
        fi

        VD_ACTION="$ACTION" VD_HOOK="$DIR/hook.sh" "$PY" - <<'EOF'
        import collections, json, os, shutil, sys

        action = os.environ["VD_ACTION"]
        hook = os.environ["VD_HOOK"]
        home = os.path.expanduser("~")
        agents = [
            ("claude", os.environ.get("CLAUDE_CONFIG_DIR") or os.path.join(home, ".claude"), "settings.json",
             ["UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest",
              "Notification", "Stop", "StopFailure", "SessionEnd"]),
            ("codex", os.environ.get("CODEX_HOME") or os.path.join(home, ".codex"), "hooks.json",
             ["UserPromptSubmit", "PostToolUse", "PermissionRequest", "Stop", "Interrupt", "SessionEnd"]),
        ]

        def ours(handler):
            return isinstance(handler, dict) and "vibedoze" in str(handler.get("command", "")).lower()

        failed = False
        for agent, folder, name, events in agents:
            path = os.path.join(folder, name)
            if not os.path.isdir(folder):
                if agent == "codex" or action == "remove":
                    continue
                os.makedirs(folder)
            data = collections.OrderedDict()
            if os.path.exists(path):
                try:
                    with open(path) as f:
                        text = f.read()
                    if text.strip():
                        data = json.loads(text, object_pairs_hook=collections.OrderedDict)
                except ValueError as e:
                    print("VibeDoze: no se pudo leer %s (%s); no se tocó." % (path, e))
                    failed = True
                    continue
                if not os.path.exists(path + ".vibedoze-backup"):
                    shutil.copyfile(path, path + ".vibedoze-backup")
            elif action == "remove":
                continue

            hooks = data.get("hooks")
            if not isinstance(hooks, dict):
                hooks = collections.OrderedDict()
            for event in list(hooks):
                groups = hooks[event]
                if not isinstance(groups, list):
                    continue
                for group in groups:
                    if isinstance(group, dict) and isinstance(group.get("hooks"), list):
                        group["hooks"] = [h for h in group["hooks"] if not ours(h)]
                groups[:] = [g for g in groups if not (isinstance(g, dict) and g.get("hooks") == [])]
                if not groups:
                    del hooks[event]
            if action != "remove":
                for event in events:
                    handler = collections.OrderedDict([
                        ("type", "command"), ("command", "'%s' %s %s" % (hook, agent, event)), ("async", True)])
                    if agent == "codex":
                        handler["timeout"] = 10
                    hooks.setdefault(event, []).append({"hooks": [handler]})
            if hooks:
                data["hooks"] = hooks
            else:
                data.pop("hooks", None)

            if not data and agent == "codex":
                if os.path.exists(path):
                    os.remove(path)
            else:
                tmp = path + ".vibedoze-tmp"
                with open(tmp, "w") as f:
                    json.dump(data, f, indent=2)
                    f.write("\n")
                if os.path.exists(path):
                    shutil.copymode(path, tmp)
                os.rename(tmp, path)
            if action == "remove":
                print("VibeDoze: hooks quitados de %s" % path)
            else:
                print("VibeDoze: hooks instalados en %s" % path)
                if agent == "codex":
                    print("VibeDoze: en Codex apruébalos una vez con /hooks.")
        sys.exit(1 if failed else 0)
        EOF

        if [ "$ACTION" = remove ]; then
          rm -rf "$DIR"
          echo "VibeDoze: listo, este servidor ya no avisa a tu laptop."
        else
          . "$DIR/config"
          curl -s -m 3 -o /dev/null -H 'Expect:' -H "Authorization: Bearer $VIBEDOZE_TOKEN" \
            -H "X-VibeDoze-Host: $VIBEDOZE_NAME" --data '' "http://127.0.0.1:$VIBEDOZE_PORT/ping" || true
          echo "VibeDoze: listo. Las sesiones nuevas de Claude Code en $VIBEDOZE_NAME avisarán a tu laptop."
        fi

        """;
}

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace VibeDoze.Hook
{
    /// <summary>
    /// "VibeDoze.Hook.exe claude|codex" — started by the agent's hook system with the event JSON on stdin.
    /// Forwards a one-line summary to the running VibeDoze over a named pipe, then exits.
    /// Never fails loudly: if VibeDoze isn't running it just exits 0.
    /// </summary>
    internal static class Program
    {
        private const int ParseBytes = 64 * 1024;

        private static int Main(string[] args)
        {
            if (args.Length >= 3 && args[0] == "watchdog") return Watchdog(args[1], args[2]);
            try
            {
                string agent = args.Length > 0 ? args[0].ToLowerInvariant() : "claude";
                string payload = ReadStdin();
                // Codex legacy "notify" passes the JSON as the last argument instead of stdin.
                if (payload.Length == 0 && args.Length > 1 && args[args.Length - 1].TrimStart().StartsWith("{"))
                    payload = args[args.Length - 1];

                string line = string.Join("\t",
                    "v2",
                    Clean(agent),
                    Clean(Field(payload, "hook_event_name") ?? Field(payload, "type") ?? "unknown"),
                    Clean(Field(payload, "session_id") ?? Field(payload, "thread-id")),
                    Clean(Field(payload, "cwd")),
                    Clean(Field(payload, "notification_type"))) + "\n";

                // Codex's elevated Windows sandbox runs hooks as a separate sandbox account but keeps the
                // session's environment, so USERNAME still names the real user (and VibeDoze's pipe).
                string user = Environment.GetEnvironmentVariable("USERNAME");
                if (string.IsNullOrEmpty(user)) user = Environment.UserName;
                string pipeName = "VibeDoze-" + user.ToLowerInvariant();
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    pipe.Connect(300);
                    byte[] bytes = Encoding.UTF8.GetBytes(line);
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();
                    // Stay alive until VibeDoze has looked at our parent processes (it answers with one byte).
                    var ack = pipe.BeginRead(new byte[1], 0, 1, null, null);
                    ack.AsyncWaitHandle.WaitOne(1500);
                }
            }
            catch
            {
                // VibeDoze not running, or anything else: never disturb the agent.
            }
            return 0;
        }

        /// <summary>
        /// "VibeDoze.Hook.exe watchdog &lt;pid&gt; &lt;VibeDoze.exe&gt;" — while VibeDoze overrides the lid action,
        /// wait for it; if it dies without restoring (crash / killed), run "VibeDoze.exe restore-lid".
        /// Lives in this tiny exe so it costs a few MB instead of a second copy of the app.
        /// </summary>
        private static int Watchdog(string pidText, string mainExe)
        {
            try
            {
                int pid = int.Parse(pidText);
                using (var released = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\VibeDoze.OverrideReleased"))
                using (var parent = System.Diagnostics.Process.GetProcessById(pid))
                using (var parentExited = new ManualResetEvent(false))
                {
                    parentExited.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(parent.Handle, false);
                    int signaled = WaitHandle.WaitAny(new WaitHandle[] { released, parentExited });
                    if (signaled == 0) return 0; // override released normally
                }
            }
            catch (ArgumentException)
            {
                // parent already gone → restore below
            }
            catch
            {
                return 1;
            }

            try
            {
                var restore = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(mainExe, "restore-lid")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                restore?.WaitForExit(60000);
            }
            catch
            {
                return 1;
            }
            return 0;
        }

        private static string ReadStdin()
        {
            string result = string.Empty;
            var reader = new Thread(() =>
            {
                try
                {
                    using (var stdin = Console.OpenStandardInput())
                    {
                        var kept = new MemoryStream();
                        var buffer = new byte[32 * 1024];
                        int n;
                        while ((n = stdin.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            // Keep the head (top-level fields come first); drain the rest so the agent never sees a broken pipe.
                            if (kept.Length < ParseBytes) kept.Write(buffer, 0, (int)Math.Min(n, ParseBytes - kept.Length));
                        }
                        result = Encoding.UTF8.GetString(kept.ToArray());
                    }
                }
                catch
                {
                    // no stdin
                }
            }) { IsBackground = true };
            reader.Start();
            reader.Join(2000);
            return result;
        }

        private static string Field(string json, string name)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var match = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!match.Success) return null;
            try
            {
                return Regex.Unescape(match.Groups[1].Value);
            }
            catch
            {
                return match.Groups[1].Value;
            }
        }

        private static string Clean(string value) =>
            value == null ? string.Empty : value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}

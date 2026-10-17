using VibeDoze.Core;

namespace VibeDoze;

internal static class Program
{
    private const string InstanceMutex = @"Local\VibeDoze.Instance";
    private const string ShowEvent = @"Local\VibeDoze.Show";
    internal const string QuitEvent = @"Local\VibeDoze.Quit";

    [STAThread]
    private static int Main(string[] args)
    {
        // Helper modes: run and exit without any UI.
        switch (args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty)
        {
            case "hook":
                return HookClient.Run(args[1..]);
            case "watchdog":
                return Watchdog.Run(args[1..]);
            case "restore-lid":
                return PowerController.RestoreAllFromState() ? 0 : 1;
            case "install-hooks":
                return Hooks(install: true, args.Length > 1 ? args[1] : "all");
            case "uninstall-hooks":
                return Hooks(install: false, args.Length > 1 ? args[1] : "all");
            case "--quit":
                // Ask the running instance to exit cleanly (restores the lid action itself).
                if (EventWaitHandle.TryOpenExisting(QuitEvent, out var quit))
                    using (quit) quit.Set();
                return 0;
        }

        // An agent that ignored our hook "args" would start us bare with the event JSON piped in.
        if (args.Length == 0 && IsStdinRedirected()) return HookClient.Run(Array.Empty<string>());

        using var mutex = new Mutex(true, InstanceMutex, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running: ask it to open its panel.
            if (EventWaitHandle.TryOpenExisting(ShowEvent, out var show))
                using (show) show.Set();
            return 0;
        }

        using var showRequests = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        var app = new App(startHidden: args.Contains("--background", StringComparer.OrdinalIgnoreCase), showRequests);
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>"install-hooks|uninstall-hooks [claude|codex|all]" — used by install.ps1.</summary>
    private static int Hooks(bool install, string which)
    {
        int failures = 0;
        foreach (HookIntegration integration in new HookIntegration[] { new ClaudeIntegration(), new CodexIntegration() })
        {
            bool selected = which.Equals("all", StringComparison.OrdinalIgnoreCase)
                            || integration.Name.StartsWith(which, StringComparison.OrdinalIgnoreCase);
            if (!selected) continue;
            try
            {
                if (install) integration.Install();
                else integration.Uninstall();
            }
            catch (Exception ex)
            {
                Log.Error($"Hooks de {integration.Name}", ex);
                failures++;
            }
        }
        return failures;
    }

    private static bool IsStdinRedirected()
    {
        IntPtr handle = Native.GetStdInputHandle();
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
        uint type = Native.GetFileType(handle);
        return type is Native.FILE_TYPE_PIPE or Native.FILE_TYPE_DISK;
    }
}

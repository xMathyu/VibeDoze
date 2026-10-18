using System.Windows;
using System.Windows.Threading;
using VibeDoze.Core;
using VibeDoze.UI;

namespace VibeDoze;

public partial class App : Application
{
    private static readonly TimeSpan FirstUpdateCheck = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);
    /// <summary>The app must have been quiet (no agent working, nothing applied) this long before an update restarts it.</summary>
    private static readonly TimeSpan QuietBeforeUpdate = TimeSpan.FromMinutes(2);

    private readonly bool _startHidden;
    private readonly string[] _args;
    private readonly EventWaitHandle _showRequests;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private RegisteredWaitHandle? _showWait;
    private RegisteredWaitHandle? _quitWait;
    private EventWaitHandle? _quitRequests;
    private Engine? _engine;
    private Updater? _updater;
    private DispatcherTimer? _updateTimer;
    private DateTime? _quietSinceUtc;
    private TrayIcon? _tray;
    private MainWindow? _panel;
    private SettingsWindow? _settings;
    private CountdownWindow? _countdown;
    private bool _quitting;

    internal App(bool startHidden, EventWaitHandle showRequests, string[] args)
    {
        _startHidden = startHidden;
        _showRequests = showRequests;
        _args = args;
    }

    internal bool IsQuitting => _quitting;
    internal Updater? Updater => _updater;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("Excepción no controlada", args.ExceptionObject as Exception);
            PowerController.RestoreAllFromState(); // never leave the lid override behind
        };

        ThemeManager.Initialize();
        if (_args.Length > 0 && _args[0].Equals("snapshot", StringComparison.OrdinalIgnoreCase))
        {
            StartSnapshot();
            return;
        }
        _engine = new Engine();
        _updater = new Updater(_engine.Settings);
        _engine.ActionPending += ShowCountdown;
        _tray = new TrayIcon(_engine, this);
        _engine.Notify += (title, message) => _tray.ShowBalloon(title, message);
        _engine.Start(); // only now: a countdown or warning raised on the first tick has someone listening

        _showWait = ThreadPool.RegisterWaitForSingleObject(_showRequests,
            (_, _) => Dispatcher.BeginInvoke(ShowPanel), null, Timeout.Infinite, executeOnlyOnce: false);
        _quitRequests = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEvent);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitRequests,
            (_, _) => Dispatcher.BeginInvoke(Quit), null, Timeout.Infinite, executeOnlyOnce: true);

        _updateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _updateTimer.Tick += (_, _) => UpdateTick();
        _updateTimer.Start();

        if (_args.Contains("--updated", StringComparer.OrdinalIgnoreCase))
        {
            Log.Info($"Actualizado a la versión {Updater.CurrentVersion}");
            _tray.ShowBalloon("VibeDoze se actualizó", $"Ahora tienes la versión {Updater.CurrentVersion}.");
        }
        else if (_args.Contains("--update-failed", StringComparer.OrdinalIgnoreCase))
        {
            _tray.ShowBalloon("VibeDoze", "No se pudo instalar la actualización; sigues con la versión anterior.");
        }

        bool firstRun = !_engine.Settings.FirstRunDone;
        if (firstRun)
        {
            _engine.Settings.FirstRunDone = true;
            _engine.SaveSettings();
        }
        if (!_startHidden || firstRun) ShowPanel();
    }

    /// <summary>"snapshot [folder] [light|dark]": run briefly, render the windows to PNG, restore everything, exit.</summary>
    private void StartSnapshot()
    {
        string dir = _args.Length > 1 ? _args[1] : Path.Combine(AppPaths.LocalDir, "snapshots");
        if (_args.Length > 2 && _args[2] is "dark" or "light") ThemeManager.Force(_args[2] == "dark");
        _engine = new Engine();
        _updater = new Updater(_engine.Settings);
        _engine.Start();
        var wait = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            try
            {
                Snapshot.Render(_engine, this, dir);
            }
            catch (Exception ex)
            {
                Log.Error("No se pudieron generar las capturas", ex);
            }
            _quitting = true;
            _engine.Dispose(); // restores anything the short run applied
            Shutdown();
        };
        wait.Start();
    }

    /// <summary>Checks GitHub now and then; installs a downloaded update once the app has been quiet for a while.</summary>
    private void UpdateTick()
    {
        if (_engine is null || _updater is null || _quitting || !_engine.Settings.AutoUpdate) return;
        var now = DateTime.UtcNow;

        bool due = _updater.LastCheckUtc is { } last
            ? now - last > UpdateCheckInterval
            : now - _startedUtc > FirstUpdateCheck;
        if (due && !_updater.Busy) _ = _updater.CheckAsync(download: true);

        if (_updater.Status == UpdateStatus.Ready && _engine.CanRestartSafely)
        {
            _quietSinceUtc ??= now;
            if (now - _quietSinceUtc > QuietBeforeUpdate) InstallUpdate();
        }
        else
        {
            _quietSinceUtc = null;
        }
    }

    internal void CheckForUpdates() => _ = _updater?.CheckAsync(download: true);

    internal void InstallUpdate()
    {
        if (_updater is null || _quitting) return;
        if (_updater.TryStartInstall(out var error))
        {
            Quit(); // restores everything; VibeDoze.Hook.exe swaps the exe and starts the new version
        }
        else
        {
            Log.Warn($"No se pudo instalar la actualización: {error}");
            _tray?.ShowBalloon("VibeDoze", $"No se pudo instalar la actualización: {error}");
        }
    }

    internal void ShowPanel()
    {
        if (_engine is null || _quitting) return;
        _panel ??= new MainWindow(_engine, this);
        _panel.ShowNearTray();
    }

    internal void ShowSettings(string? section = null)
    {
        if (_engine is null || _quitting) return;
        if (_settings is null)
        {
            _settings = new SettingsWindow(_engine, this);
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        if (section is not null) _settings.ShowSection(section);
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    internal void TogglePanel()
    {
        if (_panel is { IsVisible: true })
        {
            _panel.Hide();
            return;
        }
        // Clicking the tray icon deactivates (and hides) the panel first; don't reopen it immediately.
        if (_panel is not null && DateTime.UtcNow - _panel.HiddenAtUtc < TimeSpan.FromMilliseconds(300)) return;
        ShowPanel();
    }

    private void ShowCountdown(PendingAction pending)
    {
        if (_engine is null) return;
        _countdown?.Close();
        _countdown = new CountdownWindow(_engine, pending);
        _countdown.Closed += (_, _) => _countdown = null;
        _countdown.Show();
        _countdown.Activate();
    }

    internal void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _updateTimer?.Stop();
        _countdown?.Close();
        _settings?.Close();
        _panel?.Close();
        _tray?.Dispose();
        _engine?.Dispose();
        _showWait?.Unregister(null);
        _quitWait?.Unregister(null);
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Log.Info($"Windows cierra la sesión ({e.ReasonSessionEnding})");
        // Restore the lid / idle settings, but keep running: the shutdown may still be cancelled.
        _engine?.PrepareForSessionEnd();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _engine?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Error en la interfaz", e.Exception);
        e.Handled = true;
    }
}

using System.Windows;
using System.Windows.Threading;
using VibeDoze.Core;
using VibeDoze.UI;

namespace VibeDoze;

public partial class App : Application
{
    private readonly bool _startHidden;
    private readonly EventWaitHandle _showRequests;
    private RegisteredWaitHandle? _showWait;
    private RegisteredWaitHandle? _quitWait;
    private EventWaitHandle? _quitRequests;
    private Engine? _engine;
    private TrayIcon? _tray;
    private MainWindow? _panel;
    private CountdownWindow? _countdown;
    private bool _quitting;

    internal App(bool startHidden, EventWaitHandle showRequests)
    {
        _startHidden = startHidden;
        _showRequests = showRequests;
    }

    internal bool IsQuitting => _quitting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("Excepción no controlada", args.ExceptionObject as Exception);
            PowerController.RestoreAllFromState(); // never leave the lid override behind
        };

        _engine = new Engine();
        _engine.ActionPending += ShowCountdown;
        _tray = new TrayIcon(_engine, this);
        _engine.Notify += (title, message) => _tray.ShowBalloon(title, message);
        _engine.Start(); // only now: a countdown or warning raised on the first tick has someone listening

        _showWait = ThreadPool.RegisterWaitForSingleObject(_showRequests,
            (_, _) => Dispatcher.BeginInvoke(ShowPanel), null, Timeout.Infinite, executeOnlyOnce: false);
        _quitRequests = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEvent);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitRequests,
            (_, _) => Dispatcher.BeginInvoke(Quit), null, Timeout.Infinite, executeOnlyOnce: true);

        bool firstRun = !_engine.Settings.FirstRunDone;
        if (firstRun)
        {
            _engine.Settings.FirstRunDone = true;
            _engine.SaveSettings();
        }
        if (!_startHidden || firstRun) ShowPanel();
    }

    internal void ShowPanel()
    {
        if (_engine is null || _quitting) return;
        _panel ??= new MainWindow(_engine, this);
        _panel.ShowNearTray();
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
        _countdown?.Close();
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

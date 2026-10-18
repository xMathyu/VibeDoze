using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeDoze.Core;

namespace VibeDoze.UI;

public sealed class SessionRow
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string State { get; init; }
    public required Brush Dot { get; init; }
}

/// <summary>Flyout panel shown above the notification area (Control Center style).</summary>
public partial class MainWindow : Window
{
    private readonly Engine _engine;
    private readonly App _app;
    private bool _rendering;
    private string _sessionSignature = string.Empty;

    internal MainWindow(Engine engine, App app)
    {
        InitializeComponent();
        _engine = engine;
        _app = app;
        Logo.Source = LoadLogo();

        _engine.Updated += OnEngineUpdated;
        ThemeManager.Changed += OnThemeChanged;
        Deactivated += (_, _) => HideFlyout();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HideFlyout();
        };
        SizeChanged += (_, _) => Reposition();
    }

    public DateTime HiddenAtUtc { get; private set; } = DateTime.MinValue;

    internal void ShowNearTray()
    {
        Render();
        Show();
        Reposition();
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }

    internal void HideFlyout()
    {
        if (!IsVisible) return;
        HiddenAtUtc = DateTime.UtcNow;
        Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_app.IsQuitting)
        {
            e.Cancel = true;
            HideFlyout();
            return;
        }
        _engine.Updated -= OnEngineUpdated;
        ThemeManager.Changed -= OnThemeChanged;
        base.OnClosing(e);
    }

    /// <summary>Bottom-right of the work area; the window's 16 px margin is the shadow.</summary>
    private void Reposition()
    {
        const double shadow = 16, gap = 10;
        var area = SystemParameters.WorkArea;
        MaxHeight = Math.Max(320, area.Height - 2 * gap + 2 * shadow);
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : 700;
        Left = area.Right - width + shadow - gap;
        Top = Math.Max(area.Top + gap - shadow, area.Bottom - height + shadow - gap);
    }

    private void OnEngineUpdated()
    {
        if (IsVisible) Render();
    }

    private void OnThemeChanged()
    {
        _sessionSignature = string.Empty; // dots are concrete brushes: rebuild with the new palette
        if (IsVisible) Render();
    }

    // ───────────── Rendering ─────────────

    internal void RenderNow() => Render();

    private void Render()
    {
        _rendering = true;
        try
        {
            var s = _engine.Settings;
            EnabledSwitch.IsChecked = s.Enabled;
            EnabledText.Text = s.Enabled ? "Activo" : "En pausa";

            RenderStatus();
            RenderTiles();
            RenderSessions();
            RenderTimer();
            RenderProtection();
            RenderUpdate();
        }
        finally
        {
            _rendering = false;
        }
    }

    private void RenderStatus()
    {
        HeadlineText.Text = _engine.Headline;
        DetailText.Text = _engine.Detail;

        var (fg, soft) = _engine.Mode switch
        {
            EngineMode.Awake => ("VD.Green", "VD.GreenSoft"),
            EngineMode.Held => ("VD.Orange", "VD.OrangeSoft"),
            EngineMode.Countdown => _engine.Pending?.Source == ActionSource.Thermal
                ? ("VD.Red", "VD.RedSoft")
                : ("VD.Orange", "VD.OrangeSoft"),
            EngineMode.Paused => ("VD.Sub", "VD.Fill"),
            _ => ("VD.Accent", "VD.AccentSoft"),
        };
        Orb.SetResourceReference(Border.BackgroundProperty, soft);
        OrbCup.SetResourceReference(CupIcon.ForegroundProperty, fg);
        OrbCup.Steaming = _engine.Mode is EngineMode.Awake or EngineMode.Countdown;

        LidChip.Text = _engine.LidClosed ? "Tapa cerrada" : "Tapa abierta";
        TempChip.Text = _engine.Thermal.TemperatureC is { } t ? $"{t:0} °C" : "Sin sensor";
        if (_engine.BatteryPercent is { } pct)
        {
            BatteryChipBorder.Visibility = Visibility.Visible;
            BatteryChip.Text = $"{pct} %" + (_engine.OnBattery ? string.Empty : " · enchufada");
        }
        else
        {
            BatteryChipBorder.Visibility = Visibility.Collapsed;
        }

        bool held = _engine.Mode == EngineMode.Held;
        bool countdown = _engine.Mode == EngineMode.Countdown;
        StatusActions.Visibility = held || countdown ? Visibility.Visible : Visibility.Collapsed;
        ResumeButton.Visibility = held ? Visibility.Visible : Visibility.Collapsed;
        CancelActionButton.Visibility = countdown ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderTiles()
    {
        var s = _engine.Settings;
        var agents = _engine.Agents;
        ClaudeTile.IsChecked = s.WatchClaude;
        CodexTile.IsChecked = s.WatchCodex;
        LidTile.IsChecked = s.KeepRunningWithLidClosed;
        ManualTile.IsChecked = _engine.ManualAwake;

        ClaudeTileSub.Text = TileSummary(AgentKind.Claude, agents, s.WatchClaude);
        CodexTileSub.Text = TileSummary(AgentKind.Codex, agents, s.WatchCodex);
        LidTileSub.Text = s.KeepRunningWithLidClosed ? "Sigue trabajando" : "Suspende normal";
        ManualTileSub.Text = _engine.ManualAwake ? "Activado" : "Manual";
    }

    private static string TileSummary(AgentKind kind, IReadOnlyList<AgentProcess> agents, bool watched)
    {
        if (!watched) return "Ignorado";
        var mine = agents.Where(a => a.Kind == kind).ToList();
        if (mine.Count == 0) return "Sin abrir";
        int working = mine.Count(a => a.Working);
        if (working > 0) return working == 1 ? "1 trabajando" : $"{working} trabajando";
        return mine.Count == 1 ? "1 sesión" : $"{mine.Count} sesiones";
    }

    private void RenderSessions()
    {
        const int maxRows = 3; // the busiest first (Agents is sorted); the rest live in Ajustes › Agentes
        var agents = _engine.Agents;
        var now = DateTime.UtcNow;
        var rows = agents.Take(maxRows).Select(a => Row(a, now)).ToList();
        var signature = string.Join("|", rows.Select(r => $"{r.Title}/{r.Subtitle}/{r.State}/{r.Dot}"));
        if (signature != _sessionSignature)
        {
            SessionList.ItemsSource = rows;
            _sessionSignature = signature;
        }
        int hidden = agents.Count - rows.Count;
        SessionCount.Text = agents.Count switch
        {
            0 => string.Empty,
            1 => "1 abierta",
            _ when hidden > 0 => $"{agents.Count} abiertas · {hidden} más",
            _ => $"{agents.Count} abiertas",
        };
        NoSessions.Visibility = agents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private SessionRow Row(AgentProcess a, DateTime now)
    {
        bool watched = _engine.IsWatched(a.Kind);
        string state;
        string dot;
        if (!watched)
        {
            state = "Ignorado";
            dot = "VD.Dim";
        }
        else if (a.Working)
        {
            state = a.BusySinceUtc is { } since ? $"Trabajando · {Format.Remaining(now - since)}" : "Trabajando";
            dot = "VD.GreenDot";
        }
        else if (a.Waiting && a.Busy)
        {
            state = "Espera tu permiso";
            dot = "VD.Orange";
        }
        else if (a.Busy)
        {
            state = $"Terminó · {Format.Remaining(a.Grace - (now - a.LastWorkUtc))}";
            dot = "VD.Accent";
        }
        else
        {
            state = a.Waiting ? "Espera permiso" : "Inactivo";
            dot = "VD.Dim";
        }

        string subtitle = a.Project ?? AgentLocation(a);
        subtitle += a.HasHooks ? " · hooks" : " · CPU";
        return new SessionRow
        {
            Title = $"{AgentRules.DisplayName(a.Kind)} · {a.Host}",
            Subtitle = subtitle,
            State = state,
            Dot = Res(dot),
        };
    }

    private static string AgentLocation(AgentProcess a)
    {
        if (a.ExePath is null) return $"PID {a.Pid}";
        var path = a.ExePath.ToLowerInvariant();
        if (path.Contains(@"\extensions\")) return "extensión";
        if (path.Contains("node.exe")) return "CLI (npm)";
        return "CLI";
    }

    private void RenderTimer()
    {
        var s = _engine.Settings;
        int selected = _engine.TimerDeadlineUtc is null ? 0 : _engine.TimerMinutes ?? -1;
        foreach (var child in TimerSegments.Children.OfType<RadioButton>())
            child.IsChecked = child.Tag is string tag && int.TryParse(tag, out int m) && m == selected;

        if (_engine.TimerDeadlineUtc is { } deadline)
        {
            TimerRemaining.Text = $"Quedan {Format.Remaining(deadline - DateTime.UtcNow)}";
            TimerStatus.Text = $"{Engine.ActionName(s.TimerAction)} a las {deadline.ToLocalTime():HH:mm}";
        }
        else
        {
            TimerRemaining.Text = string.Empty;
            TimerStatus.Text = $"Sin límite · al llegar: {Engine.ActionName(s.TimerAction).ToLowerInvariant()}";
        }
    }

    private void RenderProtection()
    {
        var s = _engine.Settings;
        ThermalSwitch.IsChecked = s.ThermalGuard;
        BatterySwitch.IsChecked = s.BatteryGuard;

        string when = s.ThermalOnlyOnBattery ? "Con batería" : "Siempre";
        string sustain = s.ThermalSustainSeconds < 60 ? $"{s.ThermalSustainSeconds} s" : $"{s.ThermalSustainSeconds / 60} min";
        ThermalSub.Text = !_engine.Thermal.Available
            ? "Sin sensor de temperatura en este equipo"
            : s.ThermalGuard && s.ThermalOnlyOnBattery && !_engine.OnBattery
                ? $"En pausa: enchufada · límite {s.ThermalThresholdC} °C"
                : $"{when} · más de {s.ThermalThresholdC} °C por {sustain}";
        BatterySub.Text = $"Deja de mantenerla despierta al {s.BatteryMinPercent} %";
    }

    private void RenderUpdate()
    {
        var u = _app.Updater;
        bool ready = u is { Status: UpdateStatus.Ready, LatestVersion: not null };
        UpdateLink.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        if (ready) UpdateLinkText.Text = $"Actualizar a v{u!.LatestVersion}";
    }

    // ───────────── Input ─────────────

    private void OnEnabledClick(object sender, RoutedEventArgs e)
    {
        if (_rendering) return;
        _engine.Settings.Enabled = EnabledSwitch.IsChecked == true;
        _engine.SaveSettings();
    }

    private void OnTileClick(object sender, RoutedEventArgs e)
    {
        if (_rendering || sender is not ToggleButton tile) return;
        var s = _engine.Settings;
        bool on = tile.IsChecked == true;
        if (tile == ClaudeTile) s.WatchClaude = on;
        else if (tile == CodexTile) s.WatchCodex = on;
        else if (tile == LidTile) s.KeepRunningWithLidClosed = on;
        _engine.SaveSettings();
    }

    private void OnManualClick(object sender, RoutedEventArgs e)
    {
        if (!_rendering) _engine.SetManualAwake(ManualTile.IsChecked == true);
    }

    private void OnTimerSegment(object sender, RoutedEventArgs e)
    {
        if (_rendering || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out int minutes)) return;
        if (minutes == 0) _engine.ClearTimer();
        else _engine.SetTimer(minutes);
    }

    private void OnProtectionClick(object sender, RoutedEventArgs e)
    {
        if (_rendering) return;
        _engine.Settings.ThermalGuard = ThermalSwitch.IsChecked == true;
        _engine.Settings.BatteryGuard = BatterySwitch.IsChecked == true;
        _engine.SaveSettings();
    }

    private void OnResumeClick(object sender, RoutedEventArgs e) => _engine.ResumeFromHold();

    private void OnCancelActionClick(object sender, RoutedEventArgs e) => _engine.CancelPending();

    private void OnInstallUpdate(object sender, RoutedEventArgs e) => _app.InstallUpdate();

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        _app.ShowSettings();
    }

    private void OnSleepNow(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        _engine.SleepNow();
    }

    private void OnQuit(object sender, RoutedEventArgs e) => _app.Quit();

    // ───────────── Helpers ─────────────

    private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    internal static ImageSource? LoadLogo(int maxSize = 128)
    {
        try
        {
            var decoder = new IconBitmapDecoder(new Uri("pack://application:,,,/VibeDoze;component/Assets/VibeDoze.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault(f => f.PixelWidth <= maxSize)
                   ?? decoder.Frames.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}

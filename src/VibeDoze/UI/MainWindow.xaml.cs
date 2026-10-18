using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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

/// <summary>Flyout panel shown above the notification area.</summary>
public partial class MainWindow : Window
{
    private static readonly int[] GraceOptions = { 0, 1, 2, 5, 10, 15 };
    private static readonly int[] SustainOptions = { 30, 60, 120, 300 };
    private static readonly int[] BatteryOptions = { 5, 10, 15, 20, 30 };

    private static readonly string AppVersion =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    private readonly Engine _engine;
    private readonly App _app;
    private bool _rendering;
    private string _sessionSignature = string.Empty;
    private DateTime _integrationsCheckedUtc = DateTime.MinValue;
    private IntegrationStatus _claudeStatus, _codexStatus;
    private string? _integrationMessage;

    internal MainWindow(Engine engine, App app)
    {
        InitializeComponent();
        _engine = engine;
        _app = app;
        Logo.Source = LoadLogo();
        FillCombos();

        _engine.Updated += OnEngineUpdated;
        Deactivated += (_, _) => HideFlyout();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HideFlyout();
        };
        SourceInitialized += (_, _) => ApplyChrome();
        SizeChanged += (_, _) => Reposition();
    }

    public DateTime HiddenAtUtc { get; private set; } = DateTime.MinValue;

    internal void ShowNearTray()
    {
        _integrationsCheckedUtc = DateTime.MinValue;
        Render();
        Show();
        Reposition();
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }

    private void HideFlyout()
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
        base.OnClosing(e);
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        MaxHeight = Math.Max(300, area.Height - 24);
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : 600;
        Left = area.Right - width - 12;
        Top = Math.Max(area.Top + 12, area.Bottom - height - 12);
    }

    private void ApplyChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int dark = TrayIcon.IsDarkTheme() ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private void OnEngineUpdated()
    {
        if (IsVisible) Render();
    }

    // ───────────── Rendering ─────────────

    private void Render()
    {
        _rendering = true;
        try
        {
            var s = _engine.Settings;
            EnabledSwitch.IsChecked = s.Enabled;
            ClaudeSwitch.IsChecked = s.WatchClaude;
            CodexSwitch.IsChecked = s.WatchCodex;
            ManualSwitch.IsChecked = _engine.ManualAwake;
            LidSwitch.IsChecked = s.KeepRunningWithLidClosed;
            DisplaySwitch.IsChecked = s.KeepDisplayOn;
            ThermalSwitch.IsChecked = s.ThermalGuard;
            ThermalBatterySwitch.IsChecked = s.ThermalOnlyOnBattery;
            BatterySwitch.IsChecked = s.BatteryGuard;
            NotifySwitch.IsChecked = s.ShowNotifications;
            AutoUpdateSwitch.IsChecked = s.AutoUpdate;
            StartupSwitch.IsChecked = StartupManager.IsEnabled();

            Select(LidDoneAction, s.LidClosedDoneAction);
            Select(TimerAction, s.TimerAction);
            Select(ThermalAction, s.ThermalAction);
            Select(GraceCombo, s.FinishGraceMinutes, m => $"{m} min");
            Select(SustainCombo, s.ThermalSustainSeconds, sec => $"{sec} s");
            Select(BatteryCombo, s.BatteryMinPercent, pct => $"{pct} %");
            if (!ThresholdSlider.IsMouseCaptureWithin && !ThresholdSlider.IsKeyboardFocusWithin)
                ThresholdSlider.Value = s.ThermalThresholdC;
            ThresholdText.Text = $"{(int)ThresholdSlider.Value} °C";
            if (!CustomMinutes.IsKeyboardFocusWithin) CustomMinutes.Text = s.CustomTimerMinutes.ToString();

            RenderStatus();
            RenderAgents();
            RenderTimer();
            RenderThermal();
            RenderIntegrations();
            RenderUpdates();

            var caps = PowerController.Capabilities;
            FooterInfo.Text = $"VibeDoze {AppVersion} · Acción de la tapa ahora: {PowerController.DescribeCurrentLidAction()}"
                              + (caps.ModernStandby ? " · Modern Standby" : string.Empty)
                              + (_engine.LastAction is { } last ? $"\nÚltima acción: {last}" : string.Empty);
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
        var brush = Res(_engine.Mode switch
        {
            EngineMode.Awake => "SystemFillColorSuccessBrush",
            EngineMode.Held => "SystemFillColorCautionBrush",
            EngineMode.Countdown => _engine.Pending?.Source == ActionSource.Thermal
                ? "SystemFillColorCriticalBrush"
                : "SystemFillColorCautionBrush",
            EngineMode.Paused => "SystemFillColorNeutralBrush",
            _ => "AccentFillColorDefaultBrush",
        });
        StatusDot.Fill = brush;
        StatusHalo.Fill = brush;

        LidPill.Text = (_engine.LidClosed ? "Tapa cerrada" : "Tapa abierta")
                       + (_engine.LidOverrideActive ? " · no suspende" : string.Empty);
        TempPill.Text = _engine.Thermal.TemperatureC is { } t ? $"{t:0} °C" : "Sin sensor térmico";
        if (_engine.BatteryPercent is { } pct)
        {
            BatteryPillBorder.Visibility = Visibility.Visible;
            BatteryPill.Text = $"Batería {pct} %" + (_engine.OnBattery ? string.Empty : " · enchufada");
        }
        else
        {
            BatteryPillBorder.Visibility = Visibility.Collapsed;
        }

        bool held = _engine.Mode == EngineMode.Held;
        bool countdown = _engine.Mode == EngineMode.Countdown;
        StatusActions.Visibility = held || countdown ? Visibility.Visible : Visibility.Collapsed;
        ResumeButton.Visibility = held ? Visibility.Visible : Visibility.Collapsed;
        CancelActionButton.Visibility = countdown ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderAgents()
    {
        var agents = _engine.Agents;
        ClaudeInfo.Text = AgentSummary(AgentKind.Claude, agents);
        CodexInfo.Text = AgentSummary(AgentKind.Codex, agents);

        var now = DateTime.UtcNow;
        var rows = agents.Select(a => Row(a, now)).ToList();
        var signature = string.Join("|", rows.Select(r => $"{r.Title}/{r.Subtitle}/{r.State}/{r.Dot}"));
        if (signature != _sessionSignature)
        {
            SessionList.ItemsSource = rows;
            _sessionSignature = signature;
        }
        NoSessions.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string AgentSummary(AgentKind kind, IReadOnlyList<AgentProcess> agents)
    {
        var mine = agents.Where(a => a.Kind == kind).ToList();
        string detection = _engine.HookSeen(kind) ? "hooks" : "CPU";
        if (mine.Count == 0) return "No está abierto";
        int working = mine.Count(a => a.Working);
        string open = mine.Count == 1 ? "1 abierto" : $"{mine.Count} abiertos";
        return working > 0 ? $"{open} · {working} trabajando · detección por {detection}" : $"{open} · detección por {detection}";
    }

    private SessionRow Row(AgentProcess a, DateTime now)
    {
        bool watched = _engine.IsWatched(a.Kind);
        string state;
        string dot;
        if (!watched)
        {
            state = "Ignorado";
            dot = "SystemFillColorNeutralBrush";
        }
        else if (a.Working)
        {
            state = a.BusySinceUtc is { } since ? $"Trabajando · {Format.Remaining(now - since)}" : "Trabajando";
            dot = "SystemFillColorSuccessBrush";
        }
        else if (a.Waiting && a.Busy)
        {
            state = "Esperando tu permiso";
            dot = "SystemFillColorCautionBrush";
        }
        else if (a.Busy)
        {
            state = $"Terminó · suelta en {Format.Remaining(a.Grace - (now - a.LastWorkUtc))}";
            dot = "AccentFillColorDefaultBrush";
        }
        else
        {
            state = a.Waiting ? "Esperando permiso" : "Inactivo";
            dot = "SystemFillColorNeutralBrush";
        }

        string subtitle = a.Project is { } project ? project : AgentLocation(a);
        subtitle += a.HasHooks ? " · hooks" : $" · CPU {a.CpuPercent:0.#} %";
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
        if (path.Contains(@"\.vscode\extensions\") || path.Contains(@"\.cursor\extensions\") || path.Contains(@"\extensions\"))
            return "extensión";
        if (path.Contains("node.exe")) return "CLI (npm)";
        return "CLI";
    }

    private void RenderTimer()
    {
        var s = _engine.Settings;
        if (_engine.TimerDeadlineUtc is { } deadline)
        {
            var local = deadline.ToLocalTime();
            TimerStatus.Text = $"{Engine.ActionName(s.TimerAction)} a las {local:HH:mm} · quedan {Format.Remaining(deadline - DateTime.UtcNow)}";
            TimerStatus.Foreground = Res("TextFillColorPrimaryBrush");
            ClearTimerButton.Visibility = Visibility.Visible;
        }
        else
        {
            TimerStatus.Text = "Sin límite. Elige cuánto tiempo más puede seguir encendida la laptop.";
            TimerStatus.Foreground = Res("TextFillColorSecondaryBrush");
            ClearTimerButton.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderThermal()
    {
        var thermal = _engine.Thermal;
        if (thermal.Available)
        {
            string text = $"Ahora {thermal.TemperatureC:0} °C · máximo {thermal.MaxSeenC:0} °C · sensor {thermal.SensorName}";
            if (thermal.PassiveLimitPercent is < 100) text += $"\nWindows está bajando la velocidad por calor ({thermal.PassiveLimitPercent:0} %)";
            if (_engine.Settings.ThermalGuard && _engine.Settings.ThermalOnlyOnBattery && !_engine.OnBattery)
                text += "\nEn pausa: la laptop está enchufada";
            ThermalInfo.Text = text;
        }
        else
        {
            ThermalInfo.Text = "No se encontró un sensor de temperatura accesible en este equipo.";
        }
        ThermalOptions.IsEnabled = _engine.Settings.ThermalGuard;
        ThermalOptions.Opacity = _engine.Settings.ThermalGuard ? 1 : 0.5;
    }

    private void RenderIntegrations()
    {
        if (DateTime.UtcNow - _integrationsCheckedUtc > TimeSpan.FromSeconds(5))
        {
            _claudeStatus = _engine.ClaudeHooks.GetStatus();
            _codexStatus = _engine.CodexHooks.GetStatus();
            _integrationsCheckedUtc = DateTime.UtcNow;
        }
        var s = _engine.Settings;

        (ClaudeHookStatus.Text, ClaudeHookButton.Content) = _claudeStatus switch
        {
            IntegrationStatus.Installed when s.ClaudeHooksVerified => ("Activo · funcionando", "Quitar"),
            IntegrationStatus.Installed => ("Activo · se usará en las sesiones nuevas de Claude Code", "Quitar"),
            IntegrationStatus.Outdated => ("Apunta a otra ubicación de VibeDoze", "Reparar"),
            IntegrationStatus.Error => ("No se pudo leer ~/.claude/settings.json", "Reintentar"),
            _ => ("No instalado · detección por CPU", "Activar"),
        };

        (CodexHookStatus.Text, CodexHookButton.Content) = _codexStatus switch
        {
            IntegrationStatus.Installed when s.CodexHooksVerified => ("Activo · funcionando", "Quitar"),
            IntegrationStatus.Installed => ("Instalado · apruébalo en Codex con /hooks (o en Ajustes → Hooks de la extensión)", "Quitar"),
            IntegrationStatus.Outdated => ("Apunta a otra ubicación de VibeDoze", "Reparar"),
            IntegrationStatus.Error => ("No se pudo leer ~/.codex/hooks.json", "Reintentar"),
            _ => ("No instalado · detección por CPU", "Activar"),
        };

        if (_integrationMessage is not null) CodexHookStatus.Text = _integrationMessage;
    }

    private void RenderUpdates()
    {
        var u = _app.Updater;
        if (u is null) return;
        string latest = u.LatestVersion is { } v ? $"v{v}" : "la versión nueva";
        string checkedAgo = u.LastCheckUtc is { } t ? $" · revisado {Format.Since(t)}" : string.Empty;
        UpdateStatusText.Text = u.Status switch
        {
            UpdateStatus.Checking => "Buscando actualizaciones…",
            UpdateStatus.Downloading => $"Descargando {latest}… {u.DownloadPercent ?? 0} %",
            UpdateStatus.Ready when _engine.Settings.AutoUpdate =>
                $"{latest} lista · se instala sola cuando ningún agente esté trabajando",
            UpdateStatus.Ready => $"{latest} lista para instalar",
            UpdateStatus.Available => $"Hay una versión nueva: {latest}",
            UpdateStatus.UpToDate => $"Tienes la última versión (v{Updater.CurrentVersion}){checkedAgo}",
            UpdateStatus.Error => $"No se pudo actualizar: {u.Error}",
            _ => $"Versión actual v{Updater.CurrentVersion} · revisa GitHub cada 6 h",
        };
        UpdateButton.Content = u.Status == UpdateStatus.Ready ? $"Actualizar a {latest} ahora" : "Buscar actualizaciones";
        UpdateButton.IsEnabled = !u.Busy;
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_app.Updater?.Status == UpdateStatus.Ready) _app.InstallUpdate();
        else _app.CheckForUpdates();
    }

    // ───────────── Input ─────────────

    private void OnSettingClick(object sender, RoutedEventArgs e) => Commit();

    private void OnSettingChanged(object sender, SelectionChangedEventArgs e) => Commit();

    private void Commit()
    {
        if (_rendering) return;
        var s = _engine.Settings;
        s.Enabled = EnabledSwitch.IsChecked == true;
        s.WatchClaude = ClaudeSwitch.IsChecked == true;
        s.WatchCodex = CodexSwitch.IsChecked == true;
        s.KeepRunningWithLidClosed = LidSwitch.IsChecked == true;
        s.KeepDisplayOn = DisplaySwitch.IsChecked == true;
        s.ThermalGuard = ThermalSwitch.IsChecked == true;
        s.ThermalOnlyOnBattery = ThermalBatterySwitch.IsChecked == true;
        s.BatteryGuard = BatterySwitch.IsChecked == true;
        s.ShowNotifications = NotifySwitch.IsChecked == true;
        s.AutoUpdate = AutoUpdateSwitch.IsChecked == true;
        s.LidClosedDoneAction = Selected(LidDoneAction, s.LidClosedDoneAction);
        s.TimerAction = Selected(TimerAction, s.TimerAction);
        s.ThermalAction = Selected(ThermalAction, s.ThermalAction);
        s.FinishGraceMinutes = Selected(GraceCombo, s.FinishGraceMinutes);
        s.ThermalSustainSeconds = Selected(SustainCombo, s.ThermalSustainSeconds);
        s.BatteryMinPercent = Selected(BatteryCombo, s.BatteryMinPercent);
        s.ThermalThresholdC = (int)ThresholdSlider.Value;
        _engine.SaveSettings();
    }

    private void OnManualClick(object sender, RoutedEventArgs e)
    {
        if (!_rendering) _engine.SetManualAwake(ManualSwitch.IsChecked == true);
    }

    private void OnStartupClick(object sender, RoutedEventArgs e)
    {
        if (!_rendering) StartupManager.Set(StartupSwitch.IsChecked == true);
    }

    private void OnThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThresholdText is not null) ThresholdText.Text = $"{(int)e.NewValue} °C";
    }

    private void OnThresholdCommit(object sender, MouseButtonEventArgs e) => Commit();

    private void OnThresholdCommitKey(object sender, KeyEventArgs e) => Commit();

    private void OnTimerChip(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int minutes)) _engine.SetTimer(minutes);
    }

    private void OnCustomTimer(object sender, RoutedEventArgs e) => StartCustomTimer();

    private void OnCustomMinutesKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) StartCustomTimer();
    }

    private void StartCustomTimer()
    {
        if (!int.TryParse(CustomMinutes.Text.Trim(), out int minutes) || minutes < 1 || minutes > 24 * 60)
        {
            CustomMinutes.Text = _engine.Settings.CustomTimerMinutes.ToString();
            return;
        }
        _engine.Settings.CustomTimerMinutes = minutes;
        SettingsStore.Save(_engine.Settings);
        Keyboard.ClearFocus();
        _engine.SetTimer(minutes);
    }

    private void OnClearTimer(object sender, RoutedEventArgs e) => _engine.ClearTimer();

    private void OnResumeClick(object sender, RoutedEventArgs e) => _engine.ResumeFromHold();

    private void OnCancelActionClick(object sender, RoutedEventArgs e) => _engine.CancelPending();

    private void OnClaudeHookClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_claudeStatus == IntegrationStatus.Installed) _engine.ClaudeHooks.Uninstall();
            else _engine.ClaudeHooks.Install();
        }
        catch (Exception ex)
        {
            Log.Error("Hooks de Claude Code", ex);
            ClaudeHookStatus.Text = $"Error: {ex.Message}";
            return;
        }
        _integrationsCheckedUtc = DateTime.MinValue;
        Render();
    }

    private void OnCodexHookClick(object sender, RoutedEventArgs e)
    {
        _integrationMessage = null;
        try
        {
            if (_codexStatus == IntegrationStatus.Installed)
            {
                _engine.CodexHooks.Uninstall();
            }
            else
            {
                _engine.CodexHooks.Install();
                _engine.Settings.CodexHooksVerified = false;
                SettingsStore.Save(_engine.Settings);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Hooks de Codex", ex);
            _integrationMessage = $"Error: {ex.Message}";
        }
        _integrationsCheckedUtc = DateTime.MinValue;
        Render();
    }

    private void OnOpenLog(object sender, RoutedEventArgs e) => OpenFile(AppPaths.LogFile);

    private void OnOpenSettingsFile(object sender, RoutedEventArgs e)
    {
        SettingsStore.Save(_engine.Settings);
        OpenFile(AppPaths.SettingsFile);
    }

    private void OnSleepNow(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        _engine.SleepNow();
    }

    private void OnQuit(object sender, RoutedEventArgs e) => _app.Quit();

    private static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, string.Empty);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo abrir {path}", ex);
        }
    }

    // ───────────── Helpers ─────────────

    private void FillCombos()
    {
        foreach (var combo in new[] { LidDoneAction, TimerAction, ThermalAction })
            foreach (var action in Enum.GetValues<SystemAction>())
                combo.Items.Add(new ComboBoxItem { Content = Engine.ActionName(action), Tag = action });

        foreach (int m in GraceOptions)
            GraceCombo.Items.Add(new ComboBoxItem { Content = m == 0 ? "Sin margen" : $"{m} min", Tag = m });
        foreach (int sec in SustainOptions)
            SustainCombo.Items.Add(new ComboBoxItem { Content = sec < 60 ? $"{sec} s" : $"{sec / 60} min", Tag = sec });
        foreach (int pct in BatteryOptions)
            BatteryCombo.Items.Add(new ComboBoxItem { Content = $"{pct} %", Tag = pct });
    }

    /// <param name="label">Label for a value edited in settings.json that isn't one of the presets.</param>
    private static void Select<T>(ComboBox combo, T value, Func<T, string>? label = null)
    {
        if (combo.IsDropDownOpen) return;
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag is T tag && EqualityComparer<T>.Default.Equals(tag, value))
            {
                if (!ReferenceEquals(combo.SelectedItem, item)) combo.SelectedItem = item;
                return;
            }
        }
        if (label is null) return;
        var extra = new ComboBoxItem { Content = label(value), Tag = value };
        combo.Items.Add(extra);
        combo.SelectedItem = extra;
    }

    private static T Selected<T>(ComboBox combo, T fallback) =>
        combo.SelectedItem is ComboBoxItem { Tag: T value } ? value : fallback;

    private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private static ImageSource? LoadLogo()
    {
        try
        {
            var decoder = new IconBitmapDecoder(new Uri("pack://application:,,,/VibeDoze;component/Assets/VibeDoze.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault(f => f.PixelWidth <= 128)
                   ?? decoder.Frames.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}

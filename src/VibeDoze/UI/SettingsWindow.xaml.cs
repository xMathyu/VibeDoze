using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using VibeDoze.Core;

namespace VibeDoze.UI;

/// <summary>Settings window (System Settings style): sidebar sections, grouped rows.</summary>
public partial class SettingsWindow : Window
{
    private static readonly int[] GraceOptions = { 0, 1, 2, 5, 10, 15 };
    private static readonly int[] SustainOptions = { 30, 60, 120, 300 };
    private static readonly int[] BatteryOptions = { 5, 10, 15, 20, 30 };

    private readonly Engine _engine;
    private readonly App _app;
    private bool _rendering;
    private DateTime _integrationsCheckedUtc = DateTime.MinValue;
    private IntegrationStatus _claudeStatus, _codexStatus;
    private string? _integrationError;

    internal SettingsWindow(Engine engine, App app)
    {
        InitializeComponent();
        _engine = engine;
        _app = app;
        Logo.Source = MainWindow.LoadLogo();
        Icon = MainWindow.LoadLogo(64);
        FillCombos();
        NavGeneral.IsChecked = true;

        _engine.Updated += OnEngineUpdated;
        ThemeManager.Changed += ApplyTitleBar;
        SourceInitialized += (_, _) => ApplyTitleBar();
        Closed += (_, _) =>
        {
            _engine.Updated -= OnEngineUpdated;
            ThemeManager.Changed -= ApplyTitleBar;
        };
        Render();
    }

    internal void ShowSection(string section)
    {
        foreach (var nav in new[] { NavGeneral, NavAgents, NavLid, NavTimer, NavProtection, NavUpdates })
            if (nav.Tag as string == section) nav.IsChecked = true;
    }

    private void ApplyTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = ThemeManager.AppsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string target }) return;
        foreach (var section in new FrameworkElement[] { SecGeneral, SecAgents, SecLid, SecTimer, SecProtection, SecUpdates })
            section.Visibility = section.Name == target ? Visibility.Visible : Visibility.Collapsed;
        _integrationsCheckedUtc = DateTime.MinValue;
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
            StartupSwitch.IsChecked = StartupManager.IsEnabled();
            NotifySwitch.IsChecked = s.ShowNotifications;
            ClaudeSwitch.IsChecked = s.WatchClaude;
            CodexSwitch.IsChecked = s.WatchCodex;
            OthersSwitch.IsChecked = s.WatchOthers;
            LidSwitch.IsChecked = s.KeepRunningWithLidClosed;
            DisplaySwitch.IsChecked = s.KeepDisplayOn;
            ThermalSwitch.IsChecked = s.ThermalGuard;
            ThermalBatterySwitch.IsChecked = s.ThermalOnlyOnBattery;
            BatterySwitch.IsChecked = s.BatteryGuard;
            AutoUpdateSwitch.IsChecked = s.AutoUpdate;

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

            var agents = _engine.Agents;
            ClaudeInfo.Text = AgentSummary(AgentKind.Claude, agents);
            CodexInfo.Text = AgentSummary(AgentKind.Codex, agents);
            OthersInfo.Text = string.IsNullOrWhiteSpace(s.OtherAgents) ? "Agrega nombres en Ajustes avanzados" : s.OtherAgents;

            LidNowText.Text = PowerController.DescribeCurrentLidAction();
            LastActionText.Text = _engine.LastAction is { } last ? $"Última acción: {last}" : "Qué hizo VibeDoze y cuándo";
            VersionText.Text = $"Versión {Updater.CurrentVersion}";

            RenderTimer();
            RenderThermal();
            RenderIntegrations();
            RenderUpdates();
        }
        finally
        {
            _rendering = false;
        }
    }

    private string AgentSummary(AgentKind kind, IReadOnlyList<AgentProcess> agents)
    {
        var mine = agents.Where(a => a.Kind == kind).ToList();
        string detection = _engine.HookSeen(kind) ? "detección por hooks" : "detección por CPU";
        if (mine.Count == 0) return $"No está abierto · {detection}";
        int working = mine.Count(a => a.Working);
        string open = mine.Count == 1 ? "1 sesión" : $"{mine.Count} sesiones";
        return working > 0 ? $"{open} · {working} trabajando · {detection}" : $"{open} · {detection}";
    }

    private void RenderTimer()
    {
        var s = _engine.Settings;
        if (_engine.TimerDeadlineUtc is { } deadline)
        {
            TimerTitle.Text = $"Quedan {Format.Remaining(deadline - DateTime.UtcNow)}";
            TimerSub.Text = $"{Engine.ActionName(s.TimerAction)} a las {deadline.ToLocalTime():HH:mm}";
            ClearTimerButton.Visibility = Visibility.Visible;
        }
        else
        {
            TimerTitle.Text = "Sin límite";
            TimerSub.Text = "Elige cuánto tiempo más puede seguir encendida.";
            ClearTimerButton.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderThermal()
    {
        var thermal = _engine.Thermal;
        var s = _engine.Settings;
        if (thermal.Available)
        {
            string text = $"Ahora {thermal.TemperatureC:0} °C · máximo {thermal.MaxSeenC:0} °C · sensor {thermal.SensorName}";
            if (thermal.PassiveLimitPercent is < 100) text += $" · Windows baja la velocidad por calor ({thermal.PassiveLimitPercent:0} %)";
            if (s.ThermalGuard && s.ThermalOnlyOnBattery && !_engine.OnBattery) text += " · en pausa: enchufada";
            ThermalInfo.Text = text;
        }
        else
        {
            ThermalInfo.Text = "No se encontró un sensor de temperatura accesible en este equipo.";
        }
        ThermalOptions.IsEnabled = s.ThermalGuard;
        ThermalOptions.Opacity = s.ThermalGuard ? 1 : 0.45;
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

        var (claudeText, claudeButton, claudeDot) = _claudeStatus switch
        {
            IntegrationStatus.Installed when s.ClaudeHooksVerified => ("Funcionando", "Quitar", "VD.GreenDot"),
            IntegrationStatus.Installed => ("Instalado · se usa en las sesiones nuevas", "Quitar", "VD.GreenDot"),
            IntegrationStatus.Outdated => ("Apunta a otra ubicación de VibeDoze", "Reparar", "VD.Orange"),
            IntegrationStatus.Error => ("No se pudo leer ~/.claude/settings.json", "Reintentar", "VD.Red"),
            _ => ("No instalado · se usa la CPU", "Activar", "VD.Dim"),
        };
        var (codexText, codexButton, codexDot) = _codexStatus switch
        {
            IntegrationStatus.Installed when s.CodexHooksVerified => ("Funcionando", "Quitar", "VD.GreenDot"),
            IntegrationStatus.Installed => ("Apruébalos en Codex con /hooks", "Quitar", "VD.Orange"),
            IntegrationStatus.Outdated => ("Apunta a otra ubicación de VibeDoze", "Reparar", "VD.Orange"),
            IntegrationStatus.Error => ("No se pudo leer ~/.codex/hooks.json", "Reintentar", "VD.Red"),
            _ => ("No instalado · se usa la CPU", "Activar", "VD.Dim"),
        };

        ClaudeHookStatus.Text = claudeText;
        ClaudeHookButton.Content = claudeButton;
        ClaudeHookDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, claudeDot);
        CodexHookStatus.Text = _integrationError ?? codexText;
        CodexHookButton.Content = codexButton;
        CodexHookDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, codexDot);
    }

    private void RenderUpdates()
    {
        var u = _app.Updater;
        VersionTitle.Text = $"VibeDoze {Updater.CurrentVersion}";
        if (u is null) return;
        string latest = u.LatestVersion is { } v ? $"v{v}" : "la versión nueva";
        string checkedAgo = u.LastCheckUtc is { } t ? $" · revisado {Format.Since(t)}" : string.Empty;
        UpdateStatusText.Text = u.Status switch
        {
            UpdateStatus.Checking => "Buscando actualizaciones…",
            UpdateStatus.Downloading => $"Descargando {latest}… {u.DownloadPercent ?? 0} %",
            UpdateStatus.Ready when _engine.Settings.AutoUpdate => $"{latest} lista · se instala sola cuando ningún agente esté trabajando",
            UpdateStatus.Ready => $"{latest} lista para instalar",
            UpdateStatus.Available => $"Hay una versión nueva: {latest}",
            UpdateStatus.UpToDate => $"Tienes la última versión{checkedAgo}",
            UpdateStatus.Error => $"No se pudo actualizar: {u.Error}",
            _ => "Revisa GitHub cada 6 horas",
        };
        bool ready = u.Status == UpdateStatus.Ready;
        UpdateButton.Content = ready ? $"Actualizar a {latest}" : "Buscar ahora";
        UpdateButton.Style = (Style)FindResource(ready ? "VD.CapsuleAccent" : "VD.CapsuleFill");
        UpdateButton.Height = 30;
        UpdateButton.IsEnabled = !u.Busy;
    }

    // ───────────── Input ─────────────

    private void OnChanged(object sender, RoutedEventArgs e) => Commit();

    private void OnComboChanged(object sender, SelectionChangedEventArgs e) => Commit();

    private void Commit()
    {
        if (_rendering) return;
        var s = _engine.Settings;
        s.Enabled = EnabledSwitch.IsChecked == true;
        s.ShowNotifications = NotifySwitch.IsChecked == true;
        s.WatchClaude = ClaudeSwitch.IsChecked == true;
        s.WatchCodex = CodexSwitch.IsChecked == true;
        s.WatchOthers = OthersSwitch.IsChecked == true;
        s.KeepRunningWithLidClosed = LidSwitch.IsChecked == true;
        s.KeepDisplayOn = DisplaySwitch.IsChecked == true;
        s.ThermalGuard = ThermalSwitch.IsChecked == true;
        s.ThermalOnlyOnBattery = ThermalBatterySwitch.IsChecked == true;
        s.BatteryGuard = BatterySwitch.IsChecked == true;
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

    private void OnTimerPreset(object sender, RoutedEventArgs e)
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
        _integrationError = null;
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
            _integrationError = $"Error: {ex.Message}";
        }
        _integrationsCheckedUtc = DateTime.MinValue;
        Render();
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_app.Updater?.Status == UpdateStatus.Ready) _app.InstallUpdate();
        else _app.CheckForUpdates();
    }

    private void OnOpenLog(object sender, RoutedEventArgs e) => OpenFile(AppPaths.LogFile);

    private void OnOpenSettingsFile(object sender, RoutedEventArgs e)
    {
        SettingsStore.Save(_engine.Settings);
        OpenFile(AppPaths.SettingsFile);
    }

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
}

using System.Drawing;
using Microsoft.Win32;
using VibeDoze.Core;
using WinForms = System.Windows.Forms;

namespace VibeDoze.UI;

/// <summary>Notification-area icon: left click opens the panel, right click shows quick toggles.</summary>
internal sealed class TrayIcon : IDisposable
{
    private static readonly (int Minutes, string Label)[] TimerPresets =
    {
        (15, "15 minutos"), (30, "30 minutos"), (60, "1 hora"), (120, "2 horas"), (240, "4 horas"), (480, "8 horas"),
    };

    private readonly Engine _engine;
    private readonly App _app;
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly Dictionary<TrayState, Icon> _icons = new();
    private TrayState? _state;
    private string _tooltip = string.Empty;

    public TrayIcon(Engine engine, App app)
    {
        _engine = engine;
        _app = app;

        int size = Math.Max(16, WinForms.SystemInformation.SmallIconSize.Width);
        foreach (var state in Enum.GetValues<TrayState>()) _icons[state] = IconFactory.Create(state, size);

        _menu = new WinForms.ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
        _menu.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false;
        };
        _menu.Opened += (_, _) =>
        {
            int round = 3; // DWMWCP_ROUNDSMALL
            Native.DwmSetWindowAttribute(_menu.Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        };

        _icon = new WinForms.NotifyIcon
        {
            Icon = _icons[TrayState.Idle],
            Text = "VibeDoze",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) _app.TogglePanel();
        };
        _icon.BalloonTipClicked += (_, _) => _app.ShowPanel();

        _engine.Updated += Refresh;
        Refresh();
    }

    public void ShowBalloon(string title, string message) =>
        _icon.ShowBalloonTip(6000, title, message, WinForms.ToolTipIcon.None);

    private void Refresh()
    {
        var state = StateOf(_engine);
        if (state != _state)
        {
            _icon.Icon = _icons[state];
            _state = state;
        }

        var tip = "VibeDoze — " + _engine.Headline;
        if (_engine.TimerDeadlineUtc is { } deadline) tip += $"\nLímite: {Format.Remaining(deadline - DateTime.UtcNow)}";
        if (_engine.Thermal.TemperatureC is { } t) tip += $"\nTemperatura: {t:0} °C";
        if (tip.Length > 127) tip = tip[..127];
        if (tip != _tooltip)
        {
            _icon.Text = tip;
            _tooltip = tip;
        }
    }

    internal static TrayState StateOf(Engine engine) => engine.Mode switch
    {
        EngineMode.Paused => TrayState.Paused,
        EngineMode.Awake => TrayState.Awake,
        EngineMode.Held => TrayState.Held,
        EngineMode.Countdown => engine.Pending?.Source == ActionSource.Thermal ? TrayState.Alert : TrayState.Held,
        _ => TrayState.Idle,
    };

    private void BuildMenu()
    {
        bool dark = IsDarkTheme();
        _menu.Renderer = new MenuRenderer(dark);
        _menu.BackColor = dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(249, 249, 249);
        _menu.ForeColor = dark ? Color.White : Color.Black;
        _menu.Font = new Font("Segoe UI", 9.5f);
        var s = _engine.Settings;
        var items = _menu.Items;
        items.Clear();

        items.Add(new WinForms.ToolStripMenuItem(_engine.Headline)
        {
            Enabled = false,
            Font = new Font("Segoe UI Semibold", 9.5f),
        });
        if (!string.IsNullOrEmpty(_engine.Detail))
            items.Add(new WinForms.ToolStripMenuItem(Ellipsize(_engine.Detail, 64)) { Enabled = false });
        items.Add(new WinForms.ToolStripSeparator());

        items.Add(Item("Abrir VibeDoze", _app.ShowPanel, bold: true));
        if (_app.Updater is { Status: UpdateStatus.Ready, LatestVersion: { } latest })
            items.Add(Item($"Actualizar a v{latest}", _app.InstallUpdate));
        if (_engine.HoldReason is not null) items.Add(Item("Reanudar ahora", _engine.ResumeFromHold));
        items.Add(new WinForms.ToolStripSeparator());

        items.Add(Check("Activado", s.Enabled, v => s.Enabled = v));
        items.Add(Check("Claude Code", s.WatchClaude, v => s.WatchClaude = v));
        items.Add(Check("Codex", s.WatchCodex, v => s.WatchCodex = v));
        items.Add(Check("Seguir con la tapa cerrada", s.KeepRunningWithLidClosed, v => s.KeepRunningWithLidClosed = v));
        items.Add(Check("Mantener despierta ahora", _engine.ManualAwake, _engine.SetManualAwake, save: false));

        var timer = new WinForms.ToolStripMenuItem(_engine.TimerDeadlineUtc is { } deadline
            ? $"Límite de tiempo · quedan {Format.Remaining(deadline - DateTime.UtcNow)}"
            : "Límite de tiempo");
        foreach (var (minutes, label) in TimerPresets)
            timer.DropDownItems.Add(Item(label, () => _engine.SetTimer(minutes)));
        if (_engine.TimerDeadlineUtc is not null)
        {
            timer.DropDownItems.Add(new WinForms.ToolStripSeparator());
            timer.DropDownItems.Add(Item("Quitar límite", _engine.ClearTimer));
        }
        timer.DropDownItems.Add(new WinForms.ToolStripSeparator());
        foreach (var action in Enum.GetValues<SystemAction>())
        {
            var a = action;
            timer.DropDownItems.Add(Check($"Al llegar: {Engine.ActionName(a)}", s.TimerAction == a, _ => s.TimerAction = a));
        }
        timer.DropDown.Renderer = _menu.Renderer;
        timer.DropDown.BackColor = _menu.BackColor;
        items.Add(timer);

        string thermal = _engine.Thermal.TemperatureC is { } t ? $"Protección térmica ({t:0} °C)" : "Protección térmica";
        if (s.ThermalGuard && s.ThermalOnlyOnBattery && !_engine.OnBattery) thermal += " · en pausa, enchufada";
        items.Add(Check(thermal, s.ThermalGuard, v => s.ThermalGuard = v));
        items.Add(new WinForms.ToolStripSeparator());

        items.Add(Item("Suspender ahora", _engine.SleepNow));
        items.Add(Item("Salir", _app.Quit));

        foreach (WinForms.ToolStripItem item in items) item.ForeColor = _menu.ForeColor;
        foreach (WinForms.ToolStripItem item in timer.DropDownItems) item.ForeColor = _menu.ForeColor;
    }

    private static WinForms.ToolStripMenuItem Item(string text, Action onClick, bool bold = false)
    {
        var item = new WinForms.ToolStripMenuItem(text);
        if (bold) item.Font = new Font("Segoe UI Semibold", 9.5f);
        item.Click += (_, _) => onClick();
        return item;
    }

    private WinForms.ToolStripMenuItem Check(string text, bool value, Action<bool> set, bool save = true)
    {
        var item = new WinForms.ToolStripMenuItem(text) { Checked = value };
        item.Click += (_, _) =>
        {
            set(!value);
            if (save) _engine.SaveSettings();
        };
        return item;
    }

    private static string Ellipsize(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    internal static bool IsDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int light && light == 0;
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.Updated -= Refresh;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
    }

    /// <summary>Flat Windows 11-like menu that follows the light/dark taskbar theme.</summary>
    private sealed class MenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        private readonly bool _dark;

        public MenuRenderer(bool dark) : base(new Colors(dark))
        {
            _dark = dark;
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled
                ? (_dark ? Color.White : Color.Black)
                : (_dark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(110, 110, 110));
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            using var pen = new Pen(_dark ? Color.White : Color.Black, 1.6f);
            float x = r.Left + r.Width * 0.2f, y = r.Top + r.Height * 0.55f;
            g.DrawLines(pen, new[]
            {
                new PointF(x, y),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.75f),
                new PointF(r.Left + r.Width * 0.82f, r.Top + r.Height * 0.28f),
            });
        }

        protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = _dark ? Color.White : Color.Black;
            base.OnRenderArrow(e);
        }

        private sealed class Colors : WinForms.ProfessionalColorTable
        {
            private readonly Color _bg, _hover, _border, _separator;

            public Colors(bool dark)
            {
                UseSystemColors = false;
                _bg = dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(249, 249, 249);
                _hover = dark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(229, 229, 229);
                _border = dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(214, 214, 214);
                _separator = dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(220, 220, 220);
            }

            public override Color ToolStripDropDownBackground => _bg;
            public override Color ImageMarginGradientBegin => _bg;
            public override Color ImageMarginGradientMiddle => _bg;
            public override Color ImageMarginGradientEnd => _bg;
            public override Color MenuBorder => _border;
            public override Color MenuItemBorder => _hover;
            public override Color MenuItemSelected => _hover;
            public override Color MenuItemSelectedGradientBegin => _hover;
            public override Color MenuItemSelectedGradientEnd => _hover;
            public override Color MenuItemPressedGradientBegin => _hover;
            public override Color MenuItemPressedGradientEnd => _hover;
            public override Color CheckBackground => _bg;
            public override Color CheckSelectedBackground => _hover;
            public override Color CheckPressedBackground => _hover;
            public override Color SeparatorDark => _separator;
            public override Color SeparatorLight => _separator;
        }
    }
}

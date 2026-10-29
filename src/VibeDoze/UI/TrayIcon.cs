using System.Drawing;
using System.Drawing.Drawing2D;
using VibeDoze.Core;
using WinForms = System.Windows.Forms;

namespace VibeDoze.UI;

/// <summary>Notification-area icon: left click opens the panel, right click shows quick toggles.</summary>
internal sealed class TrayIcon : IDisposable
{
    private static readonly (int Minutes, string Label)[] KeepActivePresets =
    {
        (15, "15 minutos"), (30, "30 minutos"), (60, "1 hora"), (120, "2 horas"), (240, "4 horas"), (480, "8 horas"),
    };

    private static readonly Color Accent = Color.FromArgb(94, 92, 230);

    private readonly Engine _engine;
    private readonly App _app;
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly Dictionary<TrayState, Icon> _icons = new();
    private TrayState? _state;
    private string _tooltip = string.Empty;
    private bool _disposed;

    public TrayIcon(Engine engine, App app)
    {
        _engine = engine;
        _app = app;
        BuildIcons();

        _menu = new WinForms.ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false, Padding = new WinForms.Padding(0, 5, 0, 5) };
        _menu.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false;
        };
        _menu.Opened += (_, _) =>
        {
            int round = Native.DWMWCP_ROUND;
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
        ThemeManager.Changed += OnThemeChanged;
        Refresh();
    }

    public void ShowBalloon(string title, string message) =>
        _icon.ShowBalloonTip(6000, title, message, WinForms.ToolTipIcon.None);

    private void BuildIcons()
    {
        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();
        int size = Math.Max(16, WinForms.SystemInformation.SmallIconSize.Width);
        foreach (var state in Enum.GetValues<TrayState>())
            _icons[state] = IconFactory.Create(state, size, ThemeManager.TaskbarDark);
    }

    private void OnThemeChanged()
    {
        if (_disposed) return;
        BuildIcons();
        _state = null;
        Refresh();
    }

    private void Refresh()
    {
        var state = StateOf(_engine);
        if (state != _state)
        {
            _icon.Icon = _icons[state];
            _state = state;
        }

        var tip = "VibeDoze — " + _engine.Headline;
        if (_engine.KeepActiveUntilUtc is { } until) tip += $"\nMantener activa: {Format.Remaining(until - DateTime.UtcNow)}";
        if (_engine.Thermal.TemperatureC is { } t) tip += $"\nTemperatura: {ThermalMonitor.Format(t)}";
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

    private Color StatusColor() => StateOf(_engine) switch
    {
        TrayState.Awake => Color.FromArgb(48, 209, 88),
        TrayState.Held => Color.FromArgb(255, 159, 10),
        TrayState.Alert => Color.FromArgb(255, 69, 58),
        TrayState.Paused => Color.FromArgb(142, 142, 147),
        _ => Accent,
    };

    private void BuildMenu()
    {
        bool dark = ThemeManager.TaskbarDark;
        var renderer = new MenuRenderer(dark);
        _menu.Renderer = renderer;
        _menu.BackColor = renderer.Background;
        _menu.ForeColor = renderer.Text;
        _menu.Font = new Font("Segoe UI", 9.75f);
        var s = _engine.Settings;
        var items = _menu.Items;
        items.Clear();

        items.Add(new WinForms.ToolStripMenuItem(_engine.Headline)
        {
            Enabled = false,
            Font = new Font("Segoe UI Semibold", 9.75f),
            Tag = StatusColor(), // drawn as a dot in the check column
        });
        if (!string.IsNullOrEmpty(_engine.Detail))
            items.Add(new WinForms.ToolStripMenuItem(Ellipsize(_engine.Detail, 60)) { Enabled = false });
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
        items.Add(new WinForms.ToolStripSeparator());

        var active = new WinForms.ToolStripMenuItem("Mantener activa")
        {
            Checked = _engine.KeepActive,
            ShortcutKeyDisplayString = _engine.KeepActiveUntilUtc is { } until ? Format.Remaining(until - DateTime.UtcNow)
                : _engine.KeepActive ? "sin límite" : null,
        };
        active.DropDownItems.Add(Item("Sin límite (hasta que Claude termine)", () => _engine.StartKeepActive(null)));
        foreach (var (minutes, label) in KeepActivePresets)
            active.DropDownItems.Add(Item(label, () => _engine.StartKeepActive(minutes)));
        if (_engine.KeepActive)
        {
            active.DropDownItems.Add(new WinForms.ToolStripSeparator());
            active.DropDownItems.Add(Item("Desactivar", _engine.StopKeepActive));
        }
        active.DropDown.Renderer = renderer;
        active.DropDown.BackColor = renderer.Background;
        active.DropDown.Padding = new WinForms.Padding(0, 5, 0, 5);
        active.DropDown.Opened += (_, _) =>
        {
            int round = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(active.DropDown.Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        };
        items.Add(active);

        var thermal = Check("Protección térmica", s.ThermalGuard, v => s.ThermalGuard = v);
        thermal.ShortcutKeyDisplayString = s.ThermalGuard && s.ThermalOnlyOnBattery && !_engine.OnBattery
            ? "enchufada"
            : _engine.Thermal.TemperatureC is { } t ? ThermalMonitor.Format(t) : null;
        items.Add(thermal);
        items.Add(new WinForms.ToolStripSeparator());

        items.Add(Item("Suspender ahora", _engine.SleepNow));
        items.Add(Item("Ajustes…", () => _app.ShowSettings()));
        items.Add(Item("Salir de VibeDoze", _app.Quit));

        foreach (WinForms.ToolStripItem item in items) Style(item, renderer);
        foreach (WinForms.ToolStripItem item in active.DropDownItems) Style(item, renderer);
    }

    private static void Style(WinForms.ToolStripItem item, MenuRenderer renderer)
    {
        item.ForeColor = renderer.Text;
        if (item is WinForms.ToolStripMenuItem menuItem) menuItem.Padding = new WinForms.Padding(0, 4, 0, 4);
    }

    private static WinForms.ToolStripMenuItem Item(string text, Action onClick, bool bold = false)
    {
        var item = new WinForms.ToolStripMenuItem(text);
        if (bold) item.Font = new Font("Segoe UI Semibold", 9.75f);
        item.Click += (_, _) => onClick();
        return item;
    }

    private WinForms.ToolStripMenuItem Check(string text, bool value, Action<bool> set)
    {
        var item = new WinForms.ToolStripMenuItem(text) { Checked = value };
        item.Click += (_, _) =>
        {
            set(!value);
            _engine.SaveSettings();
        };
        return item;
    }

    private static string Ellipsize(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.Updated -= Refresh;
        ThemeManager.Changed -= OnThemeChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
    }

    /// <summary>macOS-like menu: flat, rounded, the hovered item highlighted in the accent colour.</summary>
    private sealed class MenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        private readonly bool _dark;

        public MenuRenderer(bool dark) : base(new Colors(dark))
        {
            _dark = dark;
            RoundedEdges = false;
        }

        public Color Background => _dark ? Color.FromArgb(40, 40, 43) : Color.FromArgb(246, 246, 248);
        public Color Text => _dark ? Color.FromArgb(245, 245, 247) : Color.FromArgb(29, 29, 31);
        private Color Disabled => _dark ? Color.FromArgb(152, 152, 157) : Color.FromArgb(110, 110, 115);

        protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(5, 1, e.Item.Width - 10, e.Item.Height - 2);
            using var path = Rounded(r, 6);
            using var brush = new SolidBrush(Accent);
            g.FillPath(brush, path);
        }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? Disabled : e.Item.Selected ? Color.White : Text;
            if (e.Item.Tag is Color dot)
            {
                e.TextColor = Text;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float d = 8 * e.Item.Height / 24f;
                using var brush = new SolidBrush(dot);
                g.FillEllipse(brush, 11, (e.Item.Height - d) / 2, d, d);
            }
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            using var pen = new Pen(e.Item.Selected ? Color.White : Text, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.55f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.75f),
                new PointF(r.Left + r.Width * 0.82f, r.Top + r.Height * 0.28f),
            });
        }

        protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Selected == true ? Color.White : Disabled;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(_dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0));
            e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class Colors : WinForms.ProfessionalColorTable
        {
            private readonly Color _bg, _border;

            public Colors(bool dark)
            {
                UseSystemColors = false;
                _bg = dark ? Color.FromArgb(40, 40, 43) : Color.FromArgb(246, 246, 248);
                _border = dark ? Color.FromArgb(70, 70, 74) : Color.FromArgb(214, 214, 219);
            }

            public override Color ToolStripDropDownBackground => _bg;
            public override Color ImageMarginGradientBegin => _bg;
            public override Color ImageMarginGradientMiddle => _bg;
            public override Color ImageMarginGradientEnd => _bg;
            public override Color MenuBorder => _border;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color MenuItemSelected => Color.Transparent;
            public override Color CheckBackground => _bg;
            public override Color CheckSelectedBackground => Color.Transparent;
            public override Color CheckPressedBackground => Color.Transparent;
        }
    }
}

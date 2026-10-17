using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VibeDoze.Core;

namespace VibeDoze.UI;

/// <summary>"Suspending in 30 s" dialog, shown only when someone may be in front of the screen.</summary>
public partial class CountdownWindow : Window
{
    private readonly Engine _engine;
    private readonly PendingAction _pending;
    private readonly double _total;

    internal CountdownWindow(Engine engine, PendingAction pending)
    {
        InitializeComponent();
        _engine = engine;
        _pending = pending;
        _total = Math.Max(1, (pending.ExecuteAtUtc - pending.CreatedUtc).TotalSeconds);

        bool thermal = pending.Source == ActionSource.Thermal;
        var accent = TryFindResource(thermal ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush") as Brush;
        Halo.Fill = accent;
        Progress.Foreground = accent;
        Reason.Text = pending.Reason;
        PostponeButton.Visibility = pending.Source == ActionSource.Timer ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = thermal ? "Ignorar 15 min" : "Cancelar";

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int round = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        };
        _engine.Updated += Tick;
        Closed += (_, _) => _engine.Updated -= Tick;
        Tick();
    }

    private void Tick()
    {
        if (!ReferenceEquals(_engine.Pending, _pending))
        {
            Close(); // executed, cancelled or replaced
            return;
        }
        double left = Math.Max(0, (_pending.ExecuteAtUtc - DateTime.UtcNow).TotalSeconds);
        Seconds.Text = ((int)Math.Ceiling(left)).ToString();
        TitleText.Text = $"{Engine.ActionName(_pending.Action)} en {(int)Math.Ceiling(left)} s";
        Progress.Value = left / _total;
    }

    private void OnPostpone(object sender, RoutedEventArgs e) => _engine.PostponeTimer(15);

    private void OnCancel(object sender, RoutedEventArgs e) => _engine.CancelPending();

    private void OnNow(object sender, RoutedEventArgs e) => _engine.ExecutePendingNow();
}

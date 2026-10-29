using System.Windows;
using System.Windows.Media;
using VibeDoze.Core;

namespace VibeDoze.UI;

/// <summary>"Suspending in 30 s" alert, shown only when someone may be in front of the screen.</summary>
public partial class CountdownWindow : Window
{
    private const double Radius = 39; // ring centre line inside the 84 px box (6 px stroke)

    private readonly Engine _engine;
    private readonly PendingAction _pending;
    private readonly double _total;
    private readonly bool _preview;

    /// <param name="preview">Render only (snapshot): don't follow the engine or close itself.</param>
    internal CountdownWindow(Engine engine, PendingAction pending, bool preview = false)
    {
        InitializeComponent();
        _engine = engine;
        _pending = pending;
        _preview = preview;
        _total = Math.Max(1, (pending.ExecuteAtUtc - pending.CreatedUtc).TotalSeconds);

        bool thermal = pending.Source == ActionSource.Thermal;
        Ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, thermal ? "VD.Red" : "VD.Orange");
        TitleText.Text = (thermal, pending.Action) switch
        {
            (true, SystemAction.Hibernate) => "Hibernar por temperatura",
            (true, SystemAction.Shutdown) => "Apagar por temperatura",
            (true, _) => "Suspender por temperatura",
            (_, SystemAction.Hibernate) => "La laptop va a hibernar",
            (_, SystemAction.Shutdown) => "La laptop se apagará",
            _ => "La laptop se suspenderá",
        };
        Reason.Text = pending.Reason;
        NowButton.Content = $"{Engine.ActionName(pending.Action)} ahora";
        CancelButton.Content = thermal ? "Ignorar 15 min" : "Cancelar";

        if (!preview)
        {
            _engine.Updated += Tick;
            Closed += (_, _) => _engine.Updated -= Tick;
        }
        Tick();
    }

    private void Tick()
    {
        if (!_preview && !ReferenceEquals(_engine.Pending, _pending))
        {
            Close(); // executed, cancelled or replaced
            return;
        }
        double left = Math.Max(0, (_pending.ExecuteAtUtc - DateTime.UtcNow).TotalSeconds);
        Seconds.Text = ((int)Math.Ceiling(left)).ToString();
        Ring.Data = Arc(Math.Clamp(left / _total, 0, 1));
    }

    private static Geometry Arc(double fraction)
    {
        const double c = 42;
        if (fraction >= 0.999) return new EllipseGeometry(new Point(c, c), Radius, Radius);
        double angle = fraction * 2 * Math.PI;
        var start = new Point(c, c - Radius);
        var end = new Point(c + Radius * Math.Sin(angle), c - Radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(Radius, Radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _engine.CancelPending();

    private void OnNow(object sender, RoutedEventArgs e) => _engine.ExecutePendingNow();
}

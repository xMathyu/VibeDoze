using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace VibeDoze.UI;

/// <summary>
/// A 24×24 stroke icon (SF Symbols-like) drawn from a path geometry, scaled to its size.
/// Colour comes from the inherited Foreground, so icons follow the button or text they sit in.
/// </summary>
public sealed class GlyphIcon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(GlyphIcon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(1.8, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Inherits, so a templated control (a tile) can ask for its icon solid: ui:GlyphIcon.Filled="True".</summary>
    public static readonly DependencyProperty FilledProperty = DependencyProperty.RegisterAttached(
        nameof(Filled), typeof(bool), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static bool GetFilled(DependencyObject element) => (bool)element.GetValue(FilledProperty);

    public static void SetFilled(DependencyObject element, bool value) => element.SetValue(FilledProperty, value);

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Data is null) return;
        double scale = Math.Min(ActualWidth, ActualHeight) / 24.0;
        dc.PushTransform(new ScaleTransform(scale, scale));
        if (Filled)
        {
            dc.DrawGeometry(Foreground, null, Data);
        }
        else
        {
            var pen = new Pen(Foreground, Thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            dc.DrawGeometry(null, pen, Data);
        }
        dc.Pop();
    }
}

/// <summary>The VibeDoze cup: a solid mug with a handle; it steams while an agent works.</summary>
public sealed class CupIcon : FrameworkElement
{
    internal static readonly Geometry Body = Freeze(Geometry.Parse(
        "M4.5 10.5 H15.5 V15.5 C15.5 17.7 13.7 19.5 11.5 19.5 H8.5 C6.3 19.5 4.5 17.7 4.5 15.5 Z"));
    internal static readonly Geometry Handle = Freeze(Geometry.Parse("M15.5 12 H16.8 A2.3 2.3 0 0 1 16.8 16.6 H15.5"));
    internal static readonly Geometry Steam = Freeze(Geometry.Parse(
        "M8 8.4 C7 7.4 9 6.5 8 5.5 C7 4.5 9 3.6 8 2.6 M12 8.4 C11 7.4 13 6.5 12 5.5 C11 4.5 13 3.6 12 2.6"));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(CupIcon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SteamingProperty = DependencyProperty.Register(
        nameof(Steaming), typeof(bool), typeof(CupIcon),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public bool Steaming
    {
        get => (bool)GetValue(SteamingProperty);
        set => SetValue(SteamingProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double scale = Math.Min(ActualWidth, ActualHeight) / 24.0;
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(Foreground, null, Body);
        dc.DrawGeometry(null, RoundPen(Foreground, 1.7), Handle);
        if (Steaming) dc.DrawGeometry(null, RoundPen(Foreground, 1.5), Steam);
        dc.Pop();
    }

    private static Pen RoundPen(Brush brush, double thickness) => new(brush, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    private static Geometry Freeze(Geometry g)
    {
        g.Freeze();
        return g;
    }
}

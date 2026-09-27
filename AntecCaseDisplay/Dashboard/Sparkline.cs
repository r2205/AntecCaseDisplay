using System.Windows;
using System.Windows.Media;

namespace AntecCaseDisplay.Dashboard;

/// <summary>
/// Minimal line-and-fill history chart. Deliberately does its own drawing in
/// OnRender with one frozen geometry rather than a Polyline + Points
/// collection, and only redraws when <see cref="Version"/> changes — cheap
/// enough to leave running next to a game.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(SampleBuffer), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Bumped by the owner after each <see cref="SampleBuffer.Add"/>
    /// to trigger a redraw (the buffer itself is mutated in place).</summary>
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender, OnStrokeChanged));

    /// <summary>Fixed bottom of the Y axis; NaN auto-scales to the data.</summary>
    public static readonly DependencyProperty FixedMinProperty = DependencyProperty.Register(
        nameof(FixedMin), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Fixed top of the Y axis; NaN auto-scales to the data.</summary>
    public static readonly DependencyProperty FixedMaxProperty = DependencyProperty.Register(
        nameof(FixedMax), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double StrokeThickness = 1.5;

    private Pen? _pen;
    private Brush? _fill;

    public SampleBuffer? Samples
    {
        get => (SampleBuffer?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public int Version
    {
        get => (int)GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double FixedMin
    {
        get => (double)GetValue(FixedMinProperty);
        set => SetValue(FixedMinProperty, value);
    }

    public double FixedMax
    {
        get => (double)GetValue(FixedMaxProperty);
        set => SetValue(FixedMaxProperty, value);
    }

    private static void OnStrokeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (Sparkline)d;
        s._pen = null;
        s._fill = null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var samples = Samples;
        double w = ActualWidth, h = ActualHeight;
        if (samples is null || samples.Count < 2 || w < 2 || h < 2) return;

        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int i = 0; i < samples.Count; i++)
        {
            var v = samples[i];
            if (double.IsNaN(v)) continue;
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        if (double.IsInfinity(lo)) return; // nothing but gaps

        if (!double.IsNaN(FixedMin)) lo = FixedMin;
        if (!double.IsNaN(FixedMax)) hi = FixedMax;
        // Keep a minimum span so sensor noise on a steady value doesn't get
        // stretched into a dramatic zig-zag.
        double minSpan = Math.Max(1e-6, Math.Max(Math.Abs(lo), Math.Abs(hi)) * 0.1);
        if (hi - lo < minSpan)
        {
            double mid = (hi + lo) / 2;
            lo = mid - minSpan / 2;
            hi = mid + minSpan / 2;
        }

        // Newest sample sits on the right edge; history scrolls in from the
        // right while the buffer fills.
        double pad = StrokeThickness / 2;
        double step = w / (samples.Capacity - 1);
        double x0 = w - step * (samples.Count - 1);
        double plotH = h - 2 * pad;
        double Y(double v) => pad + plotH * (1 - Math.Clamp((v - lo) / (hi - lo), 0, 1));

        _pen ??= CreatePen(Stroke);
        _fill ??= CreateFill(Stroke);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            bool inRun = false;
            double runStartX = 0, lastX = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                var v = samples[i];
                double x = x0 + i * step;
                if (double.IsNaN(v))
                {
                    if (inRun) CloseArea(ac, lastX, runStartX, h);
                    inRun = false;
                    continue;
                }
                var p = new Point(x, Y(v));
                if (!inRun)
                {
                    lc.BeginFigure(p, isFilled: false, isClosed: false);
                    ac.BeginFigure(new Point(x, h), isFilled: true, isClosed: true);
                    ac.LineTo(p, isStroked: false, isSmoothJoin: false);
                    runStartX = x;
                    inRun = true;
                }
                else
                {
                    lc.LineTo(p, isStroked: true, isSmoothJoin: true);
                    ac.LineTo(p, isStroked: false, isSmoothJoin: false);
                }
                lastX = x;
            }
            if (inRun) CloseArea(ac, lastX, runStartX, h);
        }
        line.Freeze();
        area.Freeze();

        dc.DrawGeometry(_fill, null, area);
        dc.DrawGeometry(null, _pen, line);
    }

    private static void CloseArea(StreamGeometryContext ac, double lastX, double runStartX, double h)
    {
        ac.LineTo(new Point(lastX, h), isStroked: false, isSmoothJoin: false);
        ac.LineTo(new Point(runStartX, h), isStroked: false, isSmoothJoin: false);
    }

    private static Pen CreatePen(Brush stroke)
    {
        // Clone rather than freeze a (possibly shared theme) brush in place.
        var brush = stroke.IsFrozen ? stroke : stroke.CloneCurrentValue();
        var pen = new Pen(brush, StrokeThickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private static Brush CreateFill(Brush stroke)
    {
        var fill = stroke.CloneCurrentValue();
        fill.Opacity = 0.14;
        fill.Freeze();
        return fill;
    }
}

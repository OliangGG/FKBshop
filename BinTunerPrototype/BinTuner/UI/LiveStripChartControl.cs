using System.Drawing.Drawing2D;
using System.Linq;

namespace BinTuner.UI;

/// <summary>Simple scrolling line chart (last N seconds) — used for the "กราฟไดโน" tab to plot
/// AFR/Lambda over time during a pull. No external charting library available for WinForms here,
/// so this is a small hand-rolled GDI+ control.</summary>
public class LiveStripChartControl : Control
{
    private readonly List<(double t, double v)> _samples = new();
    private double _windowSeconds = 30;

    public string Title { get; set; } = "";
    public double YMin { get; set; } = 10;
    public double YMax { get; set; } = 18;
    public Color LineColor { get; set; }

    public LiveStripChartControl()
    {
        DoubleBuffered = true;
        BackColor = Theme.Background;
        LineColor = Theme.Accent;
    }

    public void AddSample(double t, double v)
    {
        _samples.Add((t, v));
        double cutoff = t - _windowSeconds;
        int removeCount = 0;
        foreach (var s in _samples)
        {
            if (s.t < cutoff) removeCount++;
            else break;
        }
        if (removeCount > 0) _samples.RemoveRange(0, removeCount);
        Invalidate();
    }

    public void ClearSamples()
    {
        _samples.Clear();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Background);

        using var gridPen = new Pen(Theme.GridLine);
        using var textBrush = new SolidBrush(Theme.TextMuted);
        using var titleBrush = new SolidBrush(Theme.Accent);
        using var labelFont = new Font(Theme.UiFont.FontFamily, 8f);
        using var titleFont = new Font(Theme.UiFont, FontStyle.Bold);

        g.DrawString(Title, titleFont, titleBrush, 8, 4);

        const int marginLeft = 46, marginBottom = 6, marginTop = 26, marginRight = 10;
        int plotW = Width - marginLeft - marginRight;
        int plotH = Height - marginTop - marginBottom;
        if (plotW <= 0 || plotH <= 0) return;

        const int hLines = 4;
        for (int i = 0; i <= hLines; i++)
        {
            double frac = i / (double)hLines;
            float y = marginTop + (float)(plotH * frac);
            g.DrawLine(gridPen, marginLeft, y, marginLeft + plotW, y);
            double val = YMax - (YMax - YMin) * frac;
            g.DrawString(val.ToString("0.0"), labelFont, textBrush, 4, y - 6);
        }

        if (_samples.Count < 2) return;

        double tMax = _samples[^1].t;
        double tMin = tMax - _windowSeconds;

        PointF ToPoint((double t, double v) s)
        {
            float x = marginLeft + (float)((s.t - tMin) / _windowSeconds * plotW);
            double vClamped = Math.Clamp(s.v, YMin, YMax);
            float y = marginTop + (float)((YMax - vClamped) / (YMax - YMin) * plotH);
            return new PointF(x, y);
        }

        var points = _samples.Where(s => s.t >= tMin).Select(ToPoint).ToArray();
        if (points.Length < 2) return;

        using var linePen = new Pen(LineColor, 2f);
        g.DrawLines(linePen, points);
    }
}

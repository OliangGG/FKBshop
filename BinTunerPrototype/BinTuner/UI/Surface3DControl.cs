using System.Drawing.Drawing2D;
using System.Linq;

namespace BinTuner.UI;

/// <summary>
/// TunerPro-style rotatable 3D mesh/surface plot: RPM x TPS floor, table value as height,
/// green->yellow->orange->red heat coloring. Drag to orbit (azimuth/elevation), wheel to zoom.
/// All the actual 3D math lives in Surface3DMath (unit-tested); this class is purely GDI+ rendering.
/// </summary>
public class Surface3DControl : Control
{
    private double[,]? _values;
    private double[]? _rowAxis;
    private double[]? _colAxis;
    private string _rowLabel = "RPM";
    private string _colLabel = "TPS";
    private string _valueLabel = "";

    private double _azimuth = -55;
    private double _elevation = 25;
    private double _zoom = 1.0;
    private Point _lastMouse;
    private bool _dragging;

    public Surface3DControl()
    {
        DoubleBuffered = true;
        BackColor = Theme.Background;
        MouseDown += (_, e) => { _dragging = true; _lastMouse = e.Location; };
        MouseMove += OnMouseMove;
        MouseUp += (_, _) => _dragging = false;
        MouseLeave += (_, _) => _dragging = false;
        MouseWheel += OnMouseWheel;
    }

    public void SetData(double[,] values, double[] rowAxis, double[] colAxis, string rowLabel, string colLabel, string valueLabel)
    {
        _values = values;
        _rowAxis = rowAxis;
        _colAxis = colAxis;
        _rowLabel = rowLabel;
        _colLabel = colLabel;
        _valueLabel = valueLabel;
        Invalidate();
    }

    public void ResetView()
    {
        _azimuth = -55;
        _elevation = 25;
        _zoom = 1.0;
        Invalidate();
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        int dx = e.X - _lastMouse.X;
        int dy = e.Y - _lastMouse.Y;
        _lastMouse = e.Location;
        _azimuth += dx * 0.4;
        _elevation = Math.Clamp(_elevation - dy * 0.4, -85, 85);
        Invalidate();
    }

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.3, 4.0);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Background);

        using var mutedBrush = new SolidBrush(Theme.TextMuted);

        if (_values == null || _rowAxis == null || _colAxis == null)
        {
            g.DrawString("(ไม่มีข้อมูลตาราง)", Font, mutedBrush, 12, 12);
            return;
        }

        int rows = _values.GetLength(0);
        int cols = _values.GetLength(1);
        if (rows < 2 || cols < 2)
        {
            g.DrawString("ตารางเล็กเกินไปสำหรับกราฟ 3D (ต้องมีอย่างน้อย 2x2 ช่อง)", Font, mutedBrush, 12, 12);
            return;
        }

        double rowMin = _rowAxis.Min(), rowMax = _rowAxis.Max();
        double colMin = _colAxis.Min(), colMax = _colAxis.Max();
        double valMin = double.MaxValue, valMax = double.MinValue;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                double v = _values[r, c];
                if (v < valMin) valMin = v;
                if (v > valMax) valMax = v;
            }

        var rotated = new Vec3[rows, cols];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                double wx = Surface3DMath.NormalizeCentered(_colAxis[c], colMin, colMax);
                double wz = Surface3DMath.NormalizeCentered(_rowAxis[r], rowMin, rowMax);
                double wy = Surface3DMath.NormalizeHeight(_values[r, c], valMin, valMax) * 0.9;
                rotated[r, c] = Surface3DMath.Rotate(new Vec3(wx, wy, wz), _azimuth, _elevation);
            }
        }

        float scale = (float)(Math.Min(Width, Height) * 0.34 * _zoom);
        var center = new PointF(Width / 2f, Height / 2f + 20);

        PointF ToScreen(Vec3 rp)
        {
            var (sx, sy) = Surface3DMath.Project(rp);
            return new PointF(center.X + (float)(sx * scale), center.Y + (float)(sy * scale));
        }

        DrawFloorGrid(g, rowMin, rowMax, colMin, colMax, ToScreen);

        var quads = new List<(PointF[] pts, double depth, double avgVal)>();
        for (int r = 0; r < rows - 1; r++)
        {
            for (int c = 0; c < cols - 1; c++)
            {
                var p00 = rotated[r, c];
                var p01 = rotated[r, c + 1];
                var p11 = rotated[r + 1, c + 1];
                var p10 = rotated[r + 1, c];
                double avgVal = (_values[r, c] + _values[r, c + 1] + _values[r + 1, c + 1] + _values[r + 1, c]) / 4.0;
                double depth = (p00.Z + p01.Z + p11.Z + p10.Z) / 4.0;
                var pts = new[] { ToScreen(p00), ToScreen(p01), ToScreen(p11), ToScreen(p10) };
                quads.Add((pts, depth, avgVal));
            }
        }
        quads.Sort((a, b) => a.depth.CompareTo(b.depth));

        using var edgePen = new Pen(Color.FromArgb(70, 25, 25, 25), 1f);
        foreach (var (pts, _, avgVal) in quads)
        {
            double norm = valMax > valMin ? (avgVal - valMin) / (valMax - valMin) : 0.5;
            using var fillBrush = new SolidBrush(SurfaceHeatColor(norm));
            g.FillPolygon(fillBrush, pts);
            g.DrawPolygon(edgePen, pts);
        }

        DrawAxisLabels(g, rows, cols, rowMin, rowMax, colMin, colMax, ToScreen);
    }

    private void DrawFloorGrid(Graphics g, double rowMin, double rowMax, double colMin, double colMax, Func<Vec3, PointF> toScreen)
    {
        using var axisPen = new Pen(Theme.GridLine, 1f);
        var corners = new[] { new Vec3(-1, 0, -1), new Vec3(1, 0, -1), new Vec3(1, 0, 1), new Vec3(-1, 0, 1) };
        var screenCorners = corners.Select(c => toScreen(Surface3DMath.Rotate(c, _azimuth, _elevation))).ToArray();
        g.DrawPolygon(axisPen, screenCorners);
    }

    private void DrawAxisLabels(Graphics g, int rows, int cols, double rowMin, double rowMax, double colMin, double colMax, Func<Vec3, PointF> toScreen)
    {
        using var textBrush = new SolidBrush(Theme.Silver);
        using var headerBrush = new SolidBrush(Theme.Accent);
        using var smallFont = new Font(Theme.UiFont.FontFamily, 7.5f);
        using var headerFont = new Font(Theme.UiFont, FontStyle.Bold);

        int rowLabelStep = Math.Max(1, rows / 10);
        for (int r = 0; r < rows; r += rowLabelStep)
        {
            double wz = Surface3DMath.NormalizeCentered(_rowAxis![r], rowMin, rowMax);
            var p = toScreen(Surface3DMath.Rotate(new Vec3(-1, 0, wz), _azimuth, _elevation));
            g.DrawString(_rowAxis[r].ToString("0"), smallFont, textBrush, p.X - 32, p.Y - 6);
        }

        int colLabelStep = Math.Max(1, cols / 10);
        for (int c = 0; c < cols; c += colLabelStep)
        {
            double wx = Surface3DMath.NormalizeCentered(_colAxis![c], colMin, colMax);
            var p = toScreen(Surface3DMath.Rotate(new Vec3(wx, 0, -1), _azimuth, _elevation));
            g.DrawString(_colAxis[c].ToString("0.#"), smallFont, textBrush, p.X - 10, p.Y + 4);
        }

        g.DrawString($"{_rowLabel} \\ {_colLabel}    ({_valueLabel})", headerFont, headerBrush, 10, 8);
    }

    /// <summary>Green -> yellow -> orange -> red, matching TunerPro's own 3D surface coloring.</summary>
    private static Color SurfaceHeatColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        Color a, b;
        double localT;
        if (t < 0.33) { a = Color.FromArgb(0x2E, 0xB8, 0x6B); b = Color.FromArgb(0xD6, 0xD6, 0x3A); localT = t / 0.33; }
        else if (t < 0.66) { a = Color.FromArgb(0xD6, 0xD6, 0x3A); b = Color.FromArgb(0xE0, 0x9A, 0x2E); localT = (t - 0.33) / 0.33; }
        else { a = Color.FromArgb(0xE0, 0x9A, 0x2E); b = Color.FromArgb(0xD8, 0x3A, 0x2E); localT = (t - 0.66) / 0.34; }
        return Lerp(a, b, localT);
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }
}

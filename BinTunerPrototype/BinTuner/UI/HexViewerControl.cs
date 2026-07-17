namespace BinTuner.UI;

/// <summary>
/// Virtualized hex dump view: 16 bytes/line, offset gutter, ASCII column, grayscale
/// byte-value shading (the "candidate table" heuristic from the tuning spec), and an
/// optional highlighted address range for whichever table is selected in the tree.
/// </summary>
public class HexViewerControl : Control
{
    private byte[]? _data;
    private readonly VScrollBar _vScroll;
    private int _highlightStart = -1;
    private int _highlightLength;
    private const int BytesPerLine = 16;

    public HexViewerControl()
    {
        DoubleBuffered = true;
        BackColor = Theme.Background;
        ForeColor = Theme.Silver;
        Font = Theme.MonoFont;

        _vScroll = new VScrollBar { Dock = DockStyle.Right };
        _vScroll.Scroll += (_, _) => Invalidate();
        Controls.Add(_vScroll);

        MouseWheel += (_, e) =>
        {
            int delta = -(e.Delta / 120) * 3;
            SetScrollValue(_vScroll.Value + delta);
            Invalidate();
        };
        Resize += (_, _) => UpdateScrollRange();
    }

    public byte[]? Data
    {
        get => _data;
        set
        {
            _data = value;
            _vScroll.Value = 0;
            UpdateScrollRange();
            Invalidate();
        }
    }

    public void SetHighlight(int startAddress, int length)
    {
        _highlightStart = startAddress;
        _highlightLength = length;
        Invalidate();
    }

    public void ScrollToOffset(int address)
    {
        if (_data == null) return;
        int line = address / BytesPerLine;
        SetScrollValue(Math.Max(0, line - 2));
        Invalidate();
    }

    private int LineHeight => Font.Height + 2;
    private int TotalLines => _data == null ? 0 : (_data.Length + BytesPerLine - 1) / BytesPerLine;
    private int VisibleLines => Math.Max(1, ClientSize.Height / LineHeight);

    private void UpdateScrollRange()
    {
        int max = Math.Max(0, TotalLines - VisibleLines);
        _vScroll.Maximum = Math.Max(1, TotalLines - 1);
        _vScroll.LargeChange = Math.Max(1, VisibleLines);
        _vScroll.Enabled = TotalLines > VisibleLines;
        if (_vScroll.Value > max) SetScrollValue(max);
    }

    private void SetScrollValue(int value)
    {
        int max = Math.Max(0, TotalLines - 1);
        _vScroll.Value = Math.Clamp(value, 0, max);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(Theme.Background);

        if (_data == null || _data.Length == 0)
        {
            using var emptyBrush = new SolidBrush(Theme.TextMuted);
            g.DrawString("(no file loaded)", Font, emptyBrush, 8, 8);
            return;
        }

        const int gutterWidth = 90;
        const int hexStartX = gutterWidth;
        const int hexByteWidth = 24;
        const int asciiStartX = hexStartX + BytesPerLine * hexByteWidth + 16;

        using var gutterBrush = new SolidBrush(Theme.TextMuted);
        using var textBrush = new SolidBrush(Theme.Silver);
        using var highlightBrush = new SolidBrush(Color.FromArgb(90, Theme.Accent));
        using var gridPen = new Pen(Theme.GridLine);

        int firstLine = _vScroll.Value;
        int lastLine = Math.Min(TotalLines - 1, firstLine + VisibleLines);

        for (int line = firstLine; line <= lastLine; line++)
        {
            int y = (line - firstLine) * LineHeight;
            int baseAddr = line * BytesPerLine;

            g.DrawString($"{baseAddr:X8}", Font, gutterBrush, 4, y);

            var ascii = new System.Text.StringBuilder();
            for (int col = 0; col < BytesPerLine; col++)
            {
                int addr = baseAddr + col;
                if (addr >= _data.Length) break;
                byte val = _data[addr];
                int x = hexStartX + col * hexByteWidth;

                bool highlighted = _highlightStart >= 0 && addr >= _highlightStart && addr < _highlightStart + _highlightLength;
                if (highlighted)
                    g.FillRectangle(highlightBrush, x, y, hexByteWidth - 2, LineHeight);

                using var valueBrush = new SolidBrush(GrayscaleForValue(val));
                g.DrawString($"{val:X2}", Font, highlighted ? textBrush : valueBrush, x, y);

                ascii.Append(val is >= 32 and < 127 ? (char)val : '.');
            }
            g.DrawString(ascii.ToString(), Font, textBrush, asciiStartX, y);
        }

        g.DrawLine(gridPen, hexStartX - 4, 0, hexStartX - 4, Height);
        g.DrawLine(gridPen, asciiStartX - 8, 0, asciiStartX - 8, Height);
    }

    private static Color GrayscaleForValue(byte val) => Color.FromArgb(val, val, val) is var c && val < 40
        ? Color.FromArgb(90, 90, 95)   // keep near-black bytes legible on a dark background
        : c;

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
    }
}

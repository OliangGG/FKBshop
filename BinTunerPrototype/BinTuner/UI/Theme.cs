namespace BinTuner.UI;

/// <summary>BinTuner dark theme — matches ARTTUNER's navy + sky-blue accent look.</summary>
public static class Theme
{
    public static readonly Color Background = Color.FromArgb(10, 14, 20);
    public static readonly Color Panel = Color.FromArgb(17, 23, 32);
    public static readonly Color PanelBorder = Color.FromArgb(38, 48, 63);
    public static readonly Color HeaderBar = Color.FromArgb(14, 19, 27);

    public static readonly Color Accent = Color.FromArgb(0x2F, 0x9E, 0xF0);
    public static readonly Color AccentHover = Color.FromArgb(0x54, 0xB2, 0xF5);
    public static readonly Color Danger = Color.FromArgb(0xE0, 0x46, 0x4F);
    public static readonly Color Success = Color.FromArgb(0x3D, 0xB8, 0x72);
    public static readonly Color Warning = Color.FromArgb(0xE0, 0xAA, 0x3C);

    public static readonly Color Silver = Color.FromArgb(222, 227, 234);
    public static readonly Color TextMuted = Color.FromArgb(130, 140, 154);
    public static readonly Color GridLine = Color.FromArgb(45, 54, 68);

    public static readonly Color HeatLow = Color.FromArgb(40, 110, 200);   // cool blue
    public static readonly Color HeatMid = Color.FromArgb(60, 170, 90);   // green
    public static readonly Color HeatHigh = Color.FromArgb(210, 60, 50);   // red

    public static Font MonoFont { get; } = new Font("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point);
    public static Font UiFont { get; } = new Font("Tahoma", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
    public static Font HeaderFont { get; } = new Font("Tahoma", 11f, FontStyle.Bold, GraphicsUnit.Point);

    public static void Apply(Control control)
    {
        control.BackColor = Background;
        control.ForeColor = Silver;
        control.Font = UiFont;
    }

    /// <summary>Flat outlined button — secondary actions (matches ARTTUNER's left-toolbar buttons).</summary>
    public static Button StyledButton(string text)
    {
        var btn = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Panel,
            ForeColor = Silver,
            Font = UiFont,
            Height = 32,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 5, 12, 5),
            Cursor = Cursors.Hand,
        };
        btn.FlatAppearance.BorderColor = PanelBorder;
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(26, 34, 46);
        return btn;
    }

    /// <summary>Solid accent-blue button — primary actions (matches ARTTUNER's "เลือกไฟล์ BIN / AFT").</summary>
    public static Button PrimaryButton(string text)
    {
        var btn = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Font = new Font(UiFont, FontStyle.Bold),
            Height = 32,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 5, 14, 5),
            Cursor = Cursors.Hand,
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = AccentHover;
        return btn;
    }

    /// <summary>Solid danger-red button — destructive/write actions.</summary>
    public static Button DangerButton(string text)
    {
        var btn = PrimaryButton(text);
        btn.BackColor = Danger;
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xEB, 0x6B, 0x73);
        return btn;
    }

    /// <summary>A bordered "card" panel with a bold accent-colored header, like ARTTUNER's boxed sections.</summary>
    public static Panel CardPanel(string headerText, out Panel body)
    {
        var card = new BorderedPanel { BackColor = Panel, Padding = new Padding(1) };

        var header = new Label
        {
            Text = headerText,
            Dock = DockStyle.Top,
            Height = 30,
            ForeColor = Accent,
            Font = HeaderFont,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
        };

        body = new Panel { Dock = DockStyle.Fill, BackColor = Panel };

        card.Controls.Add(body);
        card.Controls.Add(header);
        return card;
    }

    /// <summary>Blue (low) -> green (mid) -> red (high) heatmap, like TunerPro/NM Tool.</summary>
    public static Color HeatColor(double normalized)
    {
        normalized = Math.Clamp(normalized, 0.0, 1.0);
        if (normalized < 0.5)
            return Lerp(HeatLow, HeatMid, normalized / 0.5);
        return Lerp(HeatMid, HeatHigh, (normalized - 0.5) / 0.5);
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }
}

/// <summary>Panel that paints a 1px border in Theme.PanelBorder — gives the ARTTUNER "boxed card" look.</summary>
public class BorderedPanel : Panel
{
    public BorderedPanel()
    {
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.PanelBorder);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

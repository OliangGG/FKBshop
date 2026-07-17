namespace BinTuner.UI;

/// <summary>FKB dark theme: black + accent blue #3078D8 + steel silver.</summary>
public static class Theme
{
    public static readonly Color Background = Color.FromArgb(18, 18, 18);
    public static readonly Color Panel = Color.FromArgb(28, 28, 30);
    public static readonly Color Accent = Color.FromArgb(0x30, 0x78, 0xD8);
    public static readonly Color Silver = Color.FromArgb(200, 200, 205);
    public static readonly Color TextMuted = Color.FromArgb(150, 150, 156);
    public static readonly Color GridLine = Color.FromArgb(55, 55, 60);

    public static readonly Color HeatLow = Color.FromArgb(40, 110, 200);   // cool blue
    public static readonly Color HeatMid = Color.FromArgb(60, 170, 90);   // green
    public static readonly Color HeatHigh = Color.FromArgb(210, 60, 50);   // red

    public static Font MonoFont { get; } = new Font("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point);
    public static Font UiFont { get; } = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

    public static void Apply(Control control)
    {
        control.BackColor = Background;
        control.ForeColor = Silver;
        control.Font = UiFont;
    }

    public static Button StyledButton(string text)
    {
        var btn = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Panel,
            ForeColor = Silver,
            Font = UiFont,
            Height = 30,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 4, 10, 4),
        };
        btn.FlatAppearance.BorderColor = Accent;
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 40, 44);
        return btn;
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

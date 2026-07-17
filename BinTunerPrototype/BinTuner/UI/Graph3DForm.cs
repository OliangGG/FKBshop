namespace BinTuner.UI;

/// <summary>Standalone window hosting a Surface3DControl for one table — opened non-modally
/// from EditorForm's "กราฟ 3D" button so it can stay open next to the grid while tuning.</summary>
public class Graph3DForm : Form
{
    private readonly Surface3DControl _surface;

    public Graph3DForm(string tableName, double[,] values, double[] rowAxis, double[] colAxis, string rowLabel, string colLabel, string valueLabel)
    {
        Text = $"FKBtuner — กราฟ 3D — {tableName}";
        Theme.Apply(this);
        BackColor = Theme.Background;
        Width = 900;
        Height = 720;
        StartPosition = FormStartPosition.CenterParent;

        var toolbar = new BorderedPanel { Dock = DockStyle.Top, Height = 46, BackColor = Theme.HeaderBar, Padding = new Padding(0, 0, 0, 1) };
        var btnReset = Theme.StyledButton("รีเซ็ตมุมมอง");
        btnReset.Location = new Point(10, 7);

        var lblHint = new Label
        {
            Text = "ลากเมาส์เพื่อหมุนมุมมอง • เลื่อนล้อเมาส์เพื่อซูมเข้า/ออก",
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Location = new Point(150, 15),
        };
        toolbar.Controls.AddRange(new Control[] { btnReset, lblHint });

        _surface = new Surface3DControl { Dock = DockStyle.Fill };
        _surface.SetData(values, rowAxis, colAxis, rowLabel, colLabel, valueLabel);
        btnReset.Click += (_, _) => _surface.ResetView();

        var card = Theme.CardPanel($"กราฟ 3D — {tableName}", out var body);
        card.Dock = DockStyle.Fill;
        body.Controls.Add(_surface);

        var wrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(8, 6, 8, 8) };
        wrap.Controls.Add(card);

        Controls.Add(wrap);
        Controls.Add(toolbar);
    }
}

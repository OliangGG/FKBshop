namespace BinTuner.UI;

public class NumericPromptDialog : Form
{
    private readonly TextBox _txt = new() { Width = 160 };
    public double Value { get; private set; }

    public NumericPromptDialog(string title, string prompt, double defaultValue = 0)
    {
        Text = title;
        Theme.Apply(this);
        Width = 320;
        Height = 160;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var lbl = new Label { Text = prompt, AutoSize = true, ForeColor = Theme.Silver, Location = new Point(16, 16) };
        _txt.Text = defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _txt.Location = new Point(16, 44);

        var btnOk = Theme.StyledButton("ตกลง");
        btnOk.Location = new Point(16, 80);
        btnOk.Click += (_, _) =>
        {
            if (double.TryParse(_txt.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                Value = v;
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                MessageBox.Show(this, "กรุณาป้อนตัวเลข", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        var btnCancel = Theme.StyledButton("ยกเลิก");
        btnCancel.Location = new Point(120, 80);
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange(new Control[] { lbl, _txt, btnOk, btnCancel });
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }
}

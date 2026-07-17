using BinTuner.Models;

namespace BinTuner.UI;

/// <summary>
/// Lets the user hand-enter a candidate table location when no .xdf is available yet —
/// the "ป้อน offset ที่คิดว่าใช่" workflow from the reverse-engineering spec.
/// </summary>
public class ManualTableDialog : Form
{
    private readonly TextBox _txtName = new() { Text = "Fuel Map (manual)" };
    private readonly TextBox _txtOffset = new() { Text = "0x0" };
    private readonly TextBox _txtRows = new() { Text = "16" };
    private readonly TextBox _txtCols = new() { Text = "24" };
    private readonly ComboBox _cmbSize = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _chkSigned = new() { Text = "Signed" };
    private readonly TextBox _txtScale = new() { Text = "1.0" };
    private readonly TextBox _txtAddOffset = new() { Text = "0.0" };
    private readonly TextBox _txtUnit = new() { Text = "" };

    public TableDef? Result { get; private set; }

    public ManualTableDialog()
    {
        Text = "เพิ่มตารางเอง (Manual offset)";
        Theme.Apply(this);
        Width = 380;
        Height = 380;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        _cmbSize.Items.AddRange(new object[] { "8-bit", "16-bit" });
        _cmbSize.SelectedIndex = 0;

        int y = 16;
        AddRow("ชื่อตาราง", _txtName, ref y);
        AddRow("Offset (hex, เช่น 0x1234)", _txtOffset, ref y);
        AddRow("Rows", _txtRows, ref y);
        AddRow("Cols", _txtCols, ref y);
        AddRow("ขนาด byte", _cmbSize, ref y);
        _chkSigned.Location = new Point(140, y);
        _chkSigned.AutoSize = true;
        _chkSigned.ForeColor = Theme.Silver;
        Controls.Add(_chkSigned);
        y += 32;
        AddRow("Scale (raw * scale + offset)", _txtScale, ref y);
        AddRow("Add offset", _txtAddOffset, ref y);
        AddRow("Unit", _txtUnit, ref y);

        var btnOk = Theme.StyledButton("เพิ่มตาราง");
        btnOk.Location = new Point(140, y + 8);
        btnOk.Click += (_, _) => TryAccept();
        Controls.Add(btnOk);

        var btnCancel = Theme.StyledButton("ยกเลิก");
        btnCancel.Location = new Point(240, y + 8);
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnCancel);
    }

    private void AddRow(string label, Control input, ref int y)
    {
        var lbl = new Label { Text = label, ForeColor = Theme.TextMuted, Location = new Point(16, y + 3), AutoSize = true };
        input.Location = new Point(140, y);
        input.Width = 200;
        Controls.Add(lbl);
        Controls.Add(input);
        y += 32;
    }

    private void TryAccept()
    {
        try
        {
            int offset = ParseOffset(_txtOffset.Text);
            int rows = int.Parse(_txtRows.Text);
            int cols = int.Parse(_txtCols.Text);
            int sizeBits = _cmbSize.SelectedIndex == 1 ? 16 : 8;
            double scale = double.Parse(_txtScale.Text, System.Globalization.CultureInfo.InvariantCulture);
            double addOffset = double.Parse(_txtAddOffset.Text, System.Globalization.CultureInfo.InvariantCulture);

            if (rows <= 0 || cols <= 0)
                throw new FormatException("Rows/Cols ต้องมากกว่า 0");

            string equation = addOffset == 0 ? $"X*{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                                              : $"X*{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}+{addOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

            Result = new TableDef
            {
                Name = string.IsNullOrWhiteSpace(_txtName.Text) ? "Manual Table" : _txtName.Text.Trim(),
                Category = "Manual",
                Offset = offset,
                Rows = rows,
                Cols = cols,
                ElementSizeBits = sizeBits,
                Signed = _chkSigned.Checked,
                MathEquation = equation,
                Unit = _txtUnit.Text.Trim(),
                DecimalPlaces = 2,
                Kind = ParamKind.Table,
            };
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"ข้อมูลไม่ถูกต้อง:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static int ParseOffset(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Convert.ToInt32(s[2..], 16);
        return int.Parse(s);
    }
}

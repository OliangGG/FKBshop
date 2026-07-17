using BinTuner.BinIO;
using BinTuner.Models;
using BinTuner.Xdf;

namespace BinTuner.UI;

public class MainForm : Form
{
    private byte[]? _binData;
    private string? _binPath;
    private ParsedXdf? _xdf;
    private string? _xdfPath;

    private readonly Label _lblBin;
    private readonly Label _lblXdf;
    private readonly Button _btnOpenEditor;

    public MainForm()
    {
        Text = "BinTuner (prototype) — Honda ECU .bin/.xdf editor";
        Theme.Apply(this);
        Width = 560;
        Height = 300;
        StartPosition = FormStartPosition.CenterScreen;

        var lblTitle = new Label
        {
            Text = "BinTuner — เปิด .bin + .xdf แล้วแก้ตารางแบบ TunerPro",
            ForeColor = Theme.Accent,
            Font = new Font(Theme.UiFont, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, 16),
        };

        var btnOpenBin = Theme.StyledButton("เปิดไฟล์ BIN...");
        btnOpenBin.Location = new Point(16, 56);
        btnOpenBin.Click += (_, _) => OpenBin();

        var btnOpenXdf = Theme.StyledButton("เปิดไฟล์ XDF...");
        btnOpenXdf.Location = new Point(16, 96);
        btnOpenXdf.Click += (_, _) => OpenXdf();

        _lblBin = new Label { Text = "BIN: (ยังไม่ได้เปิด)", ForeColor = Theme.TextMuted, AutoSize = true, Location = new Point(220, 62) };
        _lblXdf = new Label { Text = "XDF: (ยังไม่ได้เปิด)", ForeColor = Theme.TextMuted, AutoSize = true, Location = new Point(220, 102) };

        _btnOpenEditor = Theme.StyledButton("เปิดตัวแก้ตาราง (Editor)");
        _btnOpenEditor.Location = new Point(16, 150);
        _btnOpenEditor.Enabled = false;
        _btnOpenEditor.Click += (_, _) => OpenEditor();

        var lblWarn = new Label
        {
            Text = "หมายเหตุ: โปรแกรมนี้เป็น prototype แยกต่างหาก ยังไม่คำนวณ checksum\n" +
                   "ไฟล์ที่ Save As ออกมาจากที่นี่ \"ห้าม flash\" เข้า ECU จริง",
            ForeColor = Color.FromArgb(210, 170, 60),
            AutoSize = true,
            Location = new Point(16, 200),
        };

        Controls.AddRange(new Control[] { lblTitle, btnOpenBin, btnOpenXdf, _lblBin, _lblXdf, _btnOpenEditor, lblWarn });
    }

    private void OpenBin()
    {
        using var dlg = new OpenFileDialog { Filter = "ECU BIN files (*.bin)|*.bin|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _binData = BinFile.Load(dlg.FileName);
            _binPath = dlg.FileName;
            _lblBin.Text = $"BIN: {Path.GetFileName(_binPath)} ({_binData.Length:N0} bytes)";
            _lblBin.ForeColor = Theme.Silver;
            UpdateEditorButton();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"เปิดไฟล์ BIN ไม่สำเร็จ:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenXdf()
    {
        using var dlg = new OpenFileDialog { Filter = "XDF definition files (*.xdf)|*.xdf|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _xdf = XdfParser.Parse(dlg.FileName);
            _xdfPath = dlg.FileName;
            _lblXdf.Text = $"XDF: {Path.GetFileName(_xdfPath)} ({_xdf.Tables.Count} parameters)";
            _lblXdf.ForeColor = Theme.Silver;
            UpdateEditorButton();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"อ่านไฟล์ XDF ไม่สำเร็จ:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateEditorButton()
    {
        _btnOpenEditor.Enabled = _binData != null;
    }

    private void OpenEditor()
    {
        if (_binData == null || _binPath == null) return;
        var xdf = _xdf ?? new ParsedXdf();
        var editor = new EditorForm(_binData, _binPath, xdf);
        editor.ShowDialog(this);
    }
}

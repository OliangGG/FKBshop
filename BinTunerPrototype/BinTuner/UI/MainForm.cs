using BinTuner.BinIO;
using BinTuner.Models;
using BinTuner.Presets;
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
        Text = "FKBtuner — เครื่องมือแก้ไฟล์ ECU มอเตอร์ไซค์ฮอนด้า (.bin/.xdf)";
        Theme.Apply(this);
        BackColor = Theme.Background;
        Width = 640;
        Height = 500;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;

        var headerBar = new BorderedPanel { Dock = DockStyle.Top, Height = 56, BackColor = Theme.HeaderBar, Padding = new Padding(0, 0, 0, 1) };
        var lblTitle = new Label
        {
            Text = "FKBtuner",
            ForeColor = Theme.Accent,
            Font = new Font(Theme.UiFont.FontFamily, 15f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, 12),
        };
        var lblSubtitle = new Label
        {
            Text = "เปิด .bin + .xdf แล้วจูนตารางแบบ TunerPro",
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Location = new Point(150, 20),
        };
        headerBar.Controls.AddRange(new Control[] { lblTitle, lblSubtitle });

        var fileCard = Theme.CardPanel("การดำเนินการ", out var fileBody);
        fileCard.Location = new Point(16, 68);
        fileCard.Size = new Size(592, 190);
        fileCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        var btnOpenBin = Theme.PrimaryButton("เลือกไฟล์ BIN...");
        btnOpenBin.Location = new Point(16, 16);
        btnOpenBin.Click += (_, _) => OpenBin();

        var btnOpenXdf = Theme.StyledButton("เลือกไฟล์ XDF...");
        btnOpenXdf.Location = new Point(16, 62);
        btnOpenXdf.Click += (_, _) => OpenXdf();

        var btnLoadPreset = Theme.StyledButton("โหลด Preset ที่ยืนยันแล้ว...");
        btnLoadPreset.Location = new Point(16, 108);
        btnLoadPreset.Click += (_, _) => LoadPreset();

        _lblBin = new Label { Text = "ไฟล์ BIN: (ยังไม่ได้เลือก)", ForeColor = Theme.TextMuted, AutoSize = true, Location = new Point(230, 24) };
        _lblXdf = new Label { Text = "ไฟล์ XDF / Preset: (ยังไม่ได้เลือก)", ForeColor = Theme.TextMuted, AutoSize = true, Location = new Point(230, 70) };

        fileBody.Controls.AddRange(new Control[] { btnOpenBin, btnOpenXdf, btnLoadPreset, _lblBin, _lblXdf });

        _btnOpenEditor = Theme.PrimaryButton("เปิดตัวแก้ตาราง / จูน");
        _btnOpenEditor.Location = new Point(16, 270);
        _btnOpenEditor.Height = 42;
        _btnOpenEditor.Font = new Font(Theme.UiFont.FontFamily, 11f, FontStyle.Bold);
        _btnOpenEditor.Enabled = false;
        _btnOpenEditor.Click += (_, _) => OpenEditor();

        var warnCard = Theme.CardPanel("คำเตือนความปลอดภัย", out var warnBody);
        warnCard.Location = new Point(16, 326);
        warnCard.Size = new Size(592, 96);
        warnCard.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        var lblWarn = new Label
        {
            Text = "FKBtuner ยังไม่คำนวณ checksum ใหม่ให้ไฟล์ที่แก้ไขเอง\n" +
                   "ห้าม flash ไฟล์ที่บันทึกจากที่นี่ตรงๆ — ให้เปิดใน ARTTUNER แล้วใช้ \"เขียนไฟล์ (Write ECU)\"\n" +
                   "ซึ่งมีระบบ auto-checksum ในตัว เพื่อคำนวณ checksum ให้ถูกต้องก่อน flash เข้า ECU จริงเสมอ",
            ForeColor = Theme.Warning,
            AutoSize = true,
            Location = new Point(12, 8),
        };
        warnBody.Controls.Add(lblWarn);

        Controls.Add(warnCard);
        Controls.Add(_btnOpenEditor);
        Controls.Add(fileCard);
        Controls.Add(headerBar);
    }

    private void OpenBin()
    {
        using var dlg = new OpenFileDialog { Filter = "ไฟล์ ECU BIN (*.bin)|*.bin|ไฟล์ทั้งหมด (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _binData = BinFile.Load(dlg.FileName);
            _binPath = dlg.FileName;
            _lblBin.Text = $"ไฟล์ BIN: {Path.GetFileName(_binPath)} ({_binData.Length:N0} bytes)";
            _lblBin.ForeColor = Theme.Silver;
            UpdateEditorButton();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"เปิดไฟล์ BIN ไม่สำเร็จ:\n{ex.Message}", "ผิดพลาด", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenXdf()
    {
        using var dlg = new OpenFileDialog { Filter = "ไฟล์กำหนดตาราง XDF (*.xdf)|*.xdf|ไฟล์ทั้งหมด (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _xdf = XdfParser.Parse(dlg.FileName);
            _xdfPath = dlg.FileName;
            _lblXdf.Text = $"ไฟล์ XDF: {Path.GetFileName(_xdfPath)} ({_xdf.Tables.Count} พารามิเตอร์)";
            _lblXdf.ForeColor = Theme.Silver;
            UpdateEditorButton();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"อ่านไฟล์ XDF ไม่สำเร็จ:\n{ex.Message}", "ผิดพลาด", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadPreset()
    {
        string presetsDir = Path.Combine(AppContext.BaseDirectory, "Presets");
        using var dlg = new OpenFileDialog
        {
            Filter = "FKBtuner preset (*.json)|*.json|ไฟล์ทั้งหมด (*.*)|*.*",
            InitialDirectory = Directory.Exists(presetsDir) ? presetsDir : AppContext.BaseDirectory,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var preset = PresetLoader.Load(dlg.FileName);
            _xdf ??= new ParsedXdf();
            _xdf.EcuId = preset.EcuId;
            _xdf.PartNumber = preset.PartNumber;
            _xdf.Tables.AddRange(preset.Tables);
            _lblXdf.Text = $"XDF/Preset: {Path.GetFileName(dlg.FileName)} ({_xdf.Tables.Count} พารามิเตอร์รวม)";
            _lblXdf.ForeColor = Theme.Silver;
            UpdateEditorButton();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"โหลด preset ไม่สำเร็จ:\n{ex.Message}", "ผิดพลาด", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

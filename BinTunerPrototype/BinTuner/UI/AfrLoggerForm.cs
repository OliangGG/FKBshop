using System.IO;
using System.Linq;
using BinTuner.Afr;

namespace BinTuner.UI;

/// <summary>
/// โหมดจับ AFR สด: เชื่อมต่อ ECU จริง (หรือโหมดจำลอง) แล้วเก็บค่าเฉลี่ยแยกตามช่อง RPM x TPS
/// เป็น heatmap เหมือนตาราง fuel/ignition map — Export CSV ออกไปเทียบกับตารางที่จูนใน FKBtuner ได้
///
/// คำเตือนสำคัญ: ECU รุ่นที่ทดสอบ (Honda K3MH-T71) ไม่พบตำแหน่ง byte ของเซนเซอร์ O2 จริงในโปรโตคอล
/// K-line ที่ใช้ (สแกน+ดักฟังหลายรอบแล้วไม่เจอ) ค่า "AFR" ที่แสดงในนี้จึงเป็น "ค่าประมาณการ" ที่คำนวณ
/// จาก RPM/TPS เท่านั้น ไม่ใช่ค่าจริงจากเซนเซอร์วัดไอเสีย — ใช้เป็นตัวช่วยดูแนวโน้มคร่าวๆ ได้ แต่ไม่ควร
/// เชื่อเท่าการวัดด้วยเครื่อง wideband AFR จริงข้างรถ
/// </summary>
public class AfrLoggerForm : Form
{
    private const double RpmBinSize = 500;
    private static readonly double[] TpsBreakpoints = AfrLogger.TpsBreakpoints;
    private static readonly int MaxTpsBin = TpsBreakpoints.Length - 1;

    private const double HardMaxRpm = 13000;
    private const double DefaultRedlineRpm = 10000;
    private int _maxRpmBin = (int)(DefaultRedlineRpm / RpmBinSize);

    private readonly AfrLogger _logger = new()
    {
        RpmBinSize = RpmBinSize,
        AfrDelayMs = 0, // AFR เป็นค่าประมาณคำนวณทันที ไม่มี physical lag จริงแบบ O2 sensor
    };
    private readonly DataSimulator _simulator = new(sampleIntervalMs: 50);
    private readonly HondaEcuReader _ecuReader = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };

    private double _targetRpm = 0, _targetTps = 0, _targetAfr = 14.7;
    private double _displayRpm = 0, _displayTps = 0, _displayAfr = 14.7;

    private DataGridView _grid = null!;
    private Button _btnStartStop = null!;
    private CheckBox _liveModeCheckbox = null!;
    private Label _statusLabel = null!;
    private Label _liveLabel = null!;
    private Label _ecuIdLabel = null!;
    private NumericUpDown _redlineInput = null!;
    private bool _running = false;
    private (int row, int col)? _lastHighlighted = null;

    private readonly object _dataLock = new();
    private double _sharedRpm, _sharedTps, _sharedAfr;
    private bool _sharedHasData = false;
    private volatile bool _bgRunning = false;
    private System.Threading.Thread? _bgThread;

    private static readonly string KnownEcusFile = Path.Combine(AppContext.BaseDirectory, "Afr", "known_ecus.txt");
    private readonly Dictionary<string, string> _knownEcus = new();

    public AfrLoggerForm()
    {
        Text = "FKBtuner — โหมดจับ AFR (Live Log)";
        Theme.Apply(this);
        BackColor = Theme.Background;
        WindowState = FormWindowState.Maximized;

        var toolbar = BuildToolbar();

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Theme.Panel,
            GridColor = Theme.GridLine,
            BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.AutoSizeToAllHeaders,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            DefaultCellStyle = { Font = new Font("Consolas", 7.5f) },
            RowTemplate = { Height = 18 },
        };
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.HeaderBar;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Accent;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font(Theme.UiFont, FontStyle.Bold);
        _grid.RowHeadersDefaultCellStyle.BackColor = Theme.HeaderBar;
        _grid.RowHeadersDefaultCellStyle.ForeColor = Theme.Accent;
        _grid.RowHeadersDefaultCellStyle.Font = new Font(Theme.UiFont, FontStyle.Bold);
        _grid.EnableHeadersVisualStyles = false;
        _grid.CellToolTipTextNeeded += Grid_CellToolTipTextNeeded;
        _grid.CellPainting += Grid_CellPainting;

        var gridCard = Theme.CardPanel("ตาราง AFR เฉลี่ย (RPM x TPS)", out var gridBody);
        gridCard.Dock = DockStyle.Fill;
        gridBody.Controls.Add(_grid);

        var contentWrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(8, 6, 8, 8) };
        contentWrap.Controls.Add(gridCard);

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            BackColor = Theme.HeaderBar,
            ForeColor = Theme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
        };

        Controls.Add(contentWrap);
        Controls.Add(_statusLabel);
        Controls.Add(toolbar);

        BuildGridStructure();
        LoadKnownEcus();

        _timer.Tick += Timer_Tick;
        FormClosed += (_, _) => { _bgRunning = false; _timer.Stop(); _ecuReader.Dispose(); };
    }

    private Panel BuildToolbar()
    {
        var bar = new BorderedPanel { Dock = DockStyle.Top, Height = 130, BackColor = Theme.HeaderBar, Padding = new Padding(0, 0, 0, 1) };

        _btnStartStop = Theme.PrimaryButton("เริ่มจับข้อมูล (จำลอง)");
        _btnStartStop.Location = new Point(10, 8);
        _btnStartStop.Click += BtnStartStop_Click;

        var btnClear = Theme.StyledButton("ล้างตาราง");
        btnClear.Location = new Point(230, 8);
        btnClear.Click += (_, _) => { _logger.Clear(); _lastHighlighted = null; RefreshGridColors(); };

        var btnExport = Theme.StyledButton("Export CSV...");
        btnExport.Location = new Point(320, 8);
        btnExport.Click += BtnExport_Click;

        _liveModeCheckbox = new CheckBox
        {
            Text = "ต่อ ECU จริง (ไม่ใช่จำลอง)",
            AutoSize = true,
            ForeColor = Theme.Silver,
            Location = new Point(470, 15),
        };
        _liveModeCheckbox.CheckedChanged += LiveModeCheckbox_CheckedChanged;

        var lblRedline = new Label { Text = "รอบแดงจริงของรถ (RPM):", AutoSize = true, ForeColor = Theme.Silver, Location = new Point(680, 15) };
        _redlineInput = new NumericUpDown
        {
            Location = new Point(850, 11),
            Width = 90,
            Minimum = 3000,
            Maximum = (decimal)HardMaxRpm,
            Increment = 500,
            Value = (decimal)DefaultRedlineRpm,
        };
        _redlineInput.ValueChanged += RedlineInput_ValueChanged;

        var btnCalibIdle = Theme.StyledButton("Calibrate: ตอนนี้คือ TPS 0% (ไม่บิดคันเร่ง)");
        btnCalibIdle.Location = new Point(10, 48);
        btnCalibIdle.Click += BtnCalibIdle_Click;

        var btnCalibWot = Theme.StyledButton("Calibrate: ตอนนี้คือ TPS 100% (บิดสุด)");
        btnCalibWot.Location = new Point(300, 48);
        btnCalibWot.Click += BtnCalibWot_Click;

        _ecuIdLabel = new Label { Text = "", AutoSize = true, Font = Theme.MonoFont, ForeColor = Theme.Accent, Location = new Point(10, 88) };
        _liveLabel = new Label { Text = "", AutoSize = true, Font = new Font(Theme.MonoFont.FontFamily, 10f), ForeColor = Theme.Silver, Location = new Point(10, 108) };

        var lblWarn = new Label
        {
            Text = "AFR ที่แสดงเป็น \"ค่าประมาณการ\" คำนวณจาก RPM/TPS เท่านั้น ไม่ใช่ค่าจริงจากเซนเซอร์ O2 " +
                   "(ECU รุ่นนี้ไม่พบตำแหน่งค่า O2 ดิบในโปรโตคอล) ใช้ดูแนวโน้มได้ แต่ควรเทียบกับเครื่องวัด wideband จริงก่อนปรับจูนตาม",
            AutoSize = true,
            ForeColor = Theme.Warning,
            Location = new Point(590, 90),
            MaximumSize = new Size(650, 0),
        };

        bar.Controls.AddRange(new Control[]
        {
            _btnStartStop, btnClear, btnExport, _liveModeCheckbox, lblRedline, _redlineInput,
            btnCalibIdle, btnCalibWot, _ecuIdLabel, _liveLabel, lblWarn,
        });
        return bar;
    }

    private void BuildGridStructure()
    {
        _grid.Columns.Clear();
        _grid.Rows.Clear();
        _lastHighlighted = null;
        _grid.RowHeadersWidth = 55;

        for (int t = 0; t < TpsBreakpoints.Length; t++)
        {
            double tpsValue = TpsBreakpoints[t];
            var col = new DataGridViewTextBoxColumn
            {
                Name = $"tps_{t}",
                HeaderText = tpsValue.ToString("0.0") + "°",
                Width = 38,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            };
            _grid.Columns.Add(col);
        }

        for (int r = 0; r <= _maxRpmBin; r++)
        {
            double rpmValue = r * RpmBinSize;
            int rowIndex = _grid.Rows.Add();
            _grid.Rows[rowIndex].HeaderCell.Value = rpmValue.ToString("0");
            _grid.Rows[rowIndex].Tag = r;
        }

        RefreshGridColors();
    }

    private void RedlineInput_ValueChanged(object? sender, EventArgs e)
    {
        double rounded = Math.Round((double)_redlineInput.Value / RpmBinSize) * RpmBinSize;
        rounded = Math.Clamp(rounded, 3000, HardMaxRpm);

        if ((decimal)rounded != _redlineInput.Value)
        {
            _redlineInput.Value = (decimal)rounded;
            return;
        }

        _maxRpmBin = (int)(rounded / RpmBinSize);
        BuildGridStructure();
    }

    private void BtnCalibIdle_Click(object? sender, EventArgs e)
    {
        if (!_liveModeCheckbox.Checked || !_ecuReader.IsConnected)
        {
            MessageBox.Show(this, "ต้องต่อ ECU จริงและกำลังจับข้อมูลอยู่ก่อน ถึงจะ calibrate ได้",
                "ยังเชื่อมต่อไม่ได้", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _ecuReader.CalibrateIdle();
        MessageBox.Show(this, $"บันทึกจุด 0% แล้ว (raw = {_ecuReader.TpsCalibIdle:0})",
            "Calibrate สำเร็จ", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void BtnCalibWot_Click(object? sender, EventArgs e)
    {
        if (!_liveModeCheckbox.Checked || !_ecuReader.IsConnected)
        {
            MessageBox.Show(this, "ต้องต่อ ECU จริงและกำลังจับข้อมูลอยู่ก่อน ถึงจะ calibrate ได้",
                "ยังเชื่อมต่อไม่ได้", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _ecuReader.CalibrateWideOpen();
        MessageBox.Show(this, $"บันทึกจุด 100% แล้ว (raw = {_ecuReader.TpsCalibWideOpen:0})",
            "Calibrate สำเร็จ", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void LoadKnownEcus()
    {
        try
        {
            if (!File.Exists(KnownEcusFile)) return;

            foreach (var line in File.ReadAllLines(KnownEcusFile))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
                int eq = trimmed.IndexOf('=');
                if (eq < 0) continue;
                string id = trimmed[..eq].Trim().ToUpperInvariant();
                string name = trimmed[(eq + 1)..].Trim();
                _knownEcus[id] = name;
            }
        }
        catch
        {
            // ไฟล์อ่านไม่ได้ก็ไม่เป็นไร แค่จะไม่รู้จักรุ่นรถเฉยๆ ไม่กระทบการทำงานหลัก
        }
    }

    private void ReadAndDisplayEcuId()
    {
        string? ecmId = _ecuReader.ReadEcmId();
        if (ecmId == null)
        {
            _ecuIdLabel.Text = "ECM ID: (อ่านไม่สำเร็จ)";
            return;
        }

        _ecuIdLabel.Text = _knownEcus.TryGetValue(ecmId, out var modelName)
            ? $"ECM ID: {ecmId}  →  {modelName}"
            : $"ECM ID: {ecmId}  →  ไม่รู้จักรุ่นนี้ (เพิ่มลงไฟล์ Afr/known_ecus.txt ได้เลย)";
    }

    private void LiveModeCheckbox_CheckedChanged(object? sender, EventArgs e)
    {
        _btnStartStop.Text = _liveModeCheckbox.Checked ? "เชื่อมต่อ & เริ่ม (ECU จริง)" : "เริ่มจับข้อมูล (จำลอง)";
    }

    private void BtnStartStop_Click(object? sender, EventArgs e)
    {
        if (!_running && _liveModeCheckbox.Checked)
        {
            try
            {
                if (!_ecuReader.IsConnected)
                {
                    _btnStartStop.Enabled = false;
                    _btnStartStop.Text = "กำลังเชื่อมต่อ...";
                    Application.DoEvents();
                    _ecuReader.Open();

                    bool connected = false;
                    for (int attempt = 1; attempt <= 5 && !connected; attempt++)
                    {
                        _btnStartStop.Text = $"กำลังเชื่อมต่อ... (ลอง {attempt}/5)";
                        Application.DoEvents();
                        connected = _ecuReader.Init();
                        if (!connected) System.Threading.Thread.Sleep(300);
                    }

                    if (!connected)
                    {
                        MessageBox.Show(this,
                            "เชื่อมต่อ ECU ไม่สำเร็จหลังลอง 5 ครั้ง เช็คว่า:\n" +
                            "- บิดกุญแจรถไปตำแหน่ง ON แล้ว\n" +
                            "- ปิด ARTTUNER หรือโปรแกรมอื่นที่จับสาย K-line ค้างอยู่หรือยัง\n" +
                            "- ลง FTDI D2XX driver แล้วหรือยัง\n" +
                            "- ขั้วต่อสาย K-line แน่นดีไหม (ลองโยกดู ถ้าหลวมคือสาเหตุบ่อยสุด)",
                            "เชื่อมต่อไม่สำเร็จ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        _btnStartStop.Enabled = true;
                        _btnStartStop.Text = "เชื่อมต่อ & เริ่ม (ECU จริง)";
                        return;
                    }

                    ReadAndDisplayEcuId();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "เปิดอุปกรณ์ไม่สำเร็จ: " + ex.Message, "ผิดพลาด",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _btnStartStop.Enabled = true;
                _btnStartStop.Text = "เชื่อมต่อ & เริ่ม (ECU จริง)";
                return;
            }
            finally
            {
                _btnStartStop.Enabled = true;
            }
        }

        _running = !_running;
        if (_running)
        {
            _btnStartStop.Text = "หยุดจับข้อมูล";
            _liveModeCheckbox.Enabled = false;

            if (_liveModeCheckbox.Checked)
            {
                _bgRunning = true;
                _bgThread = new System.Threading.Thread(BackgroundReadLoop) { IsBackground = true };
                _bgThread.Start();
            }

            _timer.Start();
        }
        else
        {
            _btnStartStop.Text = _liveModeCheckbox.Checked ? "เชื่อมต่อ & เริ่ม (ECU จริง)" : "เริ่มจับข้อมูล (จำลอง)";
            _liveModeCheckbox.Enabled = true;
            _timer.Stop();
            _bgRunning = false;
        }
    }

    private void BackgroundReadLoop()
    {
        int consecutiveFailures = 0;

        while (_bgRunning)
        {
            var reading = _ecuReader.ReadLiveData();
            if (reading != null)
            {
                consecutiveFailures = 0;
                var (rpm, tps, afr) = reading.Value;
                lock (_dataLock)
                {
                    _sharedRpm = rpm;
                    _sharedTps = tps;
                    _sharedAfr = afr;
                    _sharedHasData = true;
                }
            }
            else
            {
                consecutiveFailures++;
                if (consecutiveFailures >= 20)
                {
                    _ecuReader.Init();
                    consecutiveFailures = 0;
                }
            }
        }
    }

    private void BtnExport_Click(object? sender, EventArgs e)
    {
        using var sfd = new SaveFileDialog
        {
            Filter = "ไฟล์ CSV (*.csv)|*.csv",
            FileName = "afr_table.csv",
        };
        if (sfd.ShowDialog(this) == DialogResult.OK)
        {
            _logger.ExportCsv(sfd.FileName, _maxRpmBin, MaxTpsBin);
            MessageBox.Show(this, "บันทึกไฟล์เรียบร้อย: " + sfd.FileName, "Export CSV",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;

        if (_liveModeCheckbox.Checked)
        {
            bool hasData;
            double rpm = 0, tps = 0, afr = 0;
            lock (_dataLock)
            {
                hasData = _sharedHasData;
                if (hasData)
                {
                    rpm = _sharedRpm;
                    tps = _sharedTps;
                    afr = _sharedAfr;
                    _sharedHasData = false;
                }
            }

            if (hasData)
            {
                _logger.AddEngineSample(now, rpm, tps);
                _logger.AddAfrSample(now, afr);

                _targetRpm = rpm;
                _targetTps = tps;
                _targetAfr = afr;

                int rpmBinReal = (int)(rpm / RpmBinSize);
                int tpsBinReal = AfrLogger.GetTpsBinIndex(tps);
                if (rpmBinReal >= 0 && rpmBinReal <= _maxRpmBin && rpmBinReal < _grid.Rows.Count
                    && tpsBinReal >= 0 && tpsBinReal < _grid.Columns.Count)
                {
                    UpdateCellDisplay(rpmBinReal, tpsBinReal);
                }
            }

            _statusLabel.Text = hasData
                ? $"บันทึกแล้ว: {_logger.AcceptedCount}    ทิ้ง (ไม่นิ่ง): {_logger.DiscardedTransientCount}    ทิ้ง (จับเวลาไม่ได้): {_logger.DiscardedNoAlignCount}"
                : $"สถานะ: {(_ecuReader.IsConnected ? "เชื่อมต่ออยู่ (รอข้อมูลรอบถัดไป)" : "หลุดการเชื่อมต่อ กำลังลองใหม่")}";
        }
        else
        {
            var (rpm, tps, afr) = _simulator.NextSample();
            _logger.AddEngineSample(now, rpm, tps);
            _logger.AddAfrSample(now, afr);
            _targetRpm = rpm;
            _targetTps = tps;
            _targetAfr = afr;

            int rpmBinReal = (int)(rpm / RpmBinSize);
            int tpsBinReal = AfrLogger.GetTpsBinIndex(tps);
            if (rpmBinReal >= 0 && rpmBinReal <= _maxRpmBin && rpmBinReal < _grid.Rows.Count
                && tpsBinReal >= 0 && tpsBinReal < _grid.Columns.Count)
            {
                UpdateCellDisplay(rpmBinReal, tpsBinReal);
            }

            _statusLabel.Text = $"บันทึกแล้ว: {_logger.AcceptedCount}    ทิ้ง (ไม่นิ่ง): {_logger.DiscardedTransientCount}    ทิ้ง (จับเวลาไม่ได้): {_logger.DiscardedNoAlignCount}";
        }

        const double smoothing = 0.75;
        _displayRpm += (_targetRpm - _displayRpm) * smoothing;
        _displayTps += (_targetTps - _displayTps) * smoothing;
        _displayAfr += (_targetAfr - _displayAfr) * smoothing;

        string afrLabel = "AFR(ประมาณ)";
        string rawTpsInfo = _liveModeCheckbox.Checked
            ? $"   [TPS raw: {_ecuReader.LastRawTps:0}  |  calib 0%={_ecuReader.TpsCalibIdle:0} 100%={_ecuReader.TpsCalibWideOpen:0}]"
            : "";
        _liveLabel.Text = $"RPM: {_displayRpm,6:0}   TPS: {_displayTps,5:0.0}°   {afrLabel}: {_displayAfr,5:0.00}{rawTpsInfo}";

        int rpmBin = (int)(_displayRpm / RpmBinSize);
        int tpsBin = AfrLogger.GetTpsBinIndex(_displayTps);
        bool inRange = rpmBin >= 0 && rpmBin <= _maxRpmBin && rpmBin < _grid.Rows.Count
                     && tpsBin >= 0 && tpsBin < _grid.Columns.Count;

        if (inRange)
        {
            HighlightLiveCell(rpmBin, tpsBin);
        }
    }

    private void HighlightLiveCell(int rpmBin, int tpsBin)
    {
        if (_lastHighlighted.HasValue && _lastHighlighted.Value == (rpmBin, tpsBin)) return;

        var prev = _lastHighlighted;
        _lastHighlighted = (rpmBin, tpsBin);

        if (prev.HasValue)
        {
            var (prevRow, prevCol) = prev.Value;
            if (prevRow < _grid.Rows.Count && prevCol < _grid.Columns.Count)
                _grid.InvalidateCell(_grid.Rows[prevRow].Cells[prevCol]);
        }

        _grid.InvalidateCell(_grid.Rows[rpmBin].Cells[tpsBin]);
    }

    private void Grid_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        e.Paint(e.ClipBounds, DataGridViewPaintParts.All);

        if (_lastHighlighted.HasValue &&
            _lastHighlighted.Value.row == e.RowIndex &&
            _lastHighlighted.Value.col == e.ColumnIndex)
        {
            using var pen = new Pen(Theme.Accent, 3);
            var rect = e.CellBounds;
            rect.Width -= 1;
            rect.Height -= 1;
            e.Graphics!.DrawRectangle(pen, rect);
        }

        e.Handled = true;
    }

    private void UpdateCellDisplay(int rpmBin, int tpsBin)
    {
        if (rpmBin < 0 || rpmBin >= _grid.Rows.Count) return;
        if (tpsBin < 0 || tpsBin >= _grid.Columns.Count) return;

        var cellCtl = _grid.Rows[rpmBin].Cells[tpsBin];
        if (_logger.Table.TryGetValue((rpmBin, tpsBin), out var cell) && cell.SampleCount > 0)
        {
            cellCtl.Value = cell.AvgAfr.ToString("0.00");
            cellCtl.Style.BackColor = AfrToColor(cell.AvgAfr, cell.Confidence);
            cellCtl.Style.ForeColor = Color.Black;
        }
        else
        {
            cellCtl.Value = "";
            cellCtl.Style.BackColor = Theme.Panel;
        }
        _grid.InvalidateCell(cellCtl);
    }

    private void RefreshGridColors()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            int rpmBin = (int)row.Tag!;
            for (int t = 0; t <= MaxTpsBin; t++)
            {
                var cellCtl = row.Cells[t];
                if (_logger.Table.TryGetValue((rpmBin, t), out var cell) && cell.SampleCount > 0)
                {
                    cellCtl.Value = cell.AvgAfr.ToString("0.00");
                    cellCtl.Style.BackColor = AfrToColor(cell.AvgAfr, cell.Confidence);
                    cellCtl.Style.ForeColor = Color.Black;
                }
                else
                {
                    cellCtl.Value = "";
                    cellCtl.Style.BackColor = Theme.Panel;
                }
            }
        }
    }

    private void Grid_CellToolTipTextNeeded(object? sender, DataGridViewCellToolTipTextNeededEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        var row = _grid.Rows[e.RowIndex];
        int rpmBin = (int)row.Tag!;
        if (_logger.Table.TryGetValue((rpmBin, e.ColumnIndex), out var cell) && cell.SampleCount > 0)
        {
            e.ToolTipText = $"AFR เฉลี่ย: {cell.AvgAfr:0.00}\n" +
                             $"Std Dev: {cell.StdDev:0.00}\n" +
                             $"จำนวน sample: {cell.SampleCount}";
        }
    }

    /// <summary>แปลงค่า AFR เป็นสี heatmap: แดง = รวย (AFR ต่ำ), เขียว = ใกล้ 14.7 (stoichiometric), ฟ้า = บาง (AFR สูง)</summary>
    private static Color AfrToColor(double afr, double confidence)
    {
        Color baseColor;
        const double stoich = 14.7;

        if (afr < stoich)
        {
            double t = Math.Clamp((stoich - afr) / 3.0, 0, 1);
            baseColor = Lerp(Color.LimeGreen, Color.Red, t);
        }
        else
        {
            double t = Math.Clamp((afr - stoich) / 3.0, 0, 1);
            baseColor = Lerp(Color.LimeGreen, Color.DeepSkyBlue, t);
        }

        return Lerp(Color.White, baseColor, 0.3 + 0.7 * confidence);
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        int r = (int)(a.R + (b.R - a.R) * t);
        int g = (int)(a.G + (b.G - a.G) * t);
        int bl = (int)(a.B + (b.B - a.B) * t);
        return Color.FromArgb(r, g, bl);
    }
}

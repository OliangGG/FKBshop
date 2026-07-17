using System.IO;
using System.Linq;
using BinTuner.Afr;

namespace BinTuner.UI;

/// <summary>
/// โหมดจับ AFR สด — จัดหน้าตาม ARTTUNER's "กราฟดาต้า (Data Graph) - Real-Time": 3 แท็บ
/// (กราฟสด / ตารางน้ำมัน / กราฟไดโน) + ตัวเลือกชนิดเชื้อเพลิง (กำหนด Stoich) + โหมดแสดงผลของตาราง
/// (AFR/Lambda เฉลี่ย, ส่วนต่างจาก Lambda เป้าหมาย, จำนวนตัวอย่าง) — Export CSV ออกไปเทียบกับตารางที่
/// จูนใน FKBtuner ได้ หรือกดปุ่ม "แนะนำการจูนจาก AFR..." ในหน้าแก้ตารางเพื่อขอคำแนะนำโดยตรง
///
/// คำเตือนสำคัญ: ECU รุ่นที่ทดสอบ (Honda K3MH-T71) ไม่พบตำแหน่ง byte ของเซนเซอร์ O2 จริงในโปรโตคอล
/// K-line ที่ใช้ (สแกน+ดักฟังหลายรอบแล้วไม่เจอ) ค่า "AFR" ที่แสดงในนี้จึงเป็น "ค่าประมาณการ" ที่คำนวณ
/// จาก RPM/TPS เท่านั้น ไม่ใช่ค่าจริงจากเซนเซอร์วัดไอเสีย — ใช้เป็นตัวช่วยดูแนวโน้มคร่าวๆ ได้ แต่ไม่ควร
/// เชื่อเท่าการวัดด้วยเครื่อง wideband AFR จริงข้างรถ
/// </summary>
public class AfrLoggerForm : Form
{
    private enum DisplayMode { AfrAvg, LambdaAvg, LambdaDeviationPct, SampleCount }

    private const double RpmBinSize = 500;
    private static readonly double[] TpsBreakpoints = AfrLogger.TpsBreakpoints;
    private static readonly int MaxTpsBin = TpsBreakpoints.Length - 1;

    private const double HardMaxRpm = 13000;
    private const double DefaultRedlineRpm = 10000;
    private int _maxRpmBin = (int)(DefaultRedlineRpm / RpmBinSize);

    private static readonly (string Name, double Stoich)[] FuelTypes =
    {
        ("แก๊สโซฮอล์ 91 (E10)", 14.1),
        ("แก๊สโซฮอล์ 95 (E10)", 14.1),
        ("E20", 13.7),
        ("E85", 9.8),
        ("เบนซิน 95 (E0)", 14.7),
        ("กำหนดเอง", 14.7),
    };
    private double _stoich = 14.7;
    private double _targetLambda = 1.0;
    private DisplayMode _displayMode = DisplayMode.AfrAvg;

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
    private DateTime _recordStartTime = DateTime.Now;

    private DataGridView _grid = null!;
    private LiveStripChartControl _stripChart = null!;
    private Button _btnStartStop = null!;
    private CheckBox _liveModeCheckbox = null!;
    private Label _statusLabel = null!;
    private Label _liveLabel = null!;
    private Label _ecuIdLabel = null!;
    private NumericUpDown _redlineInput = null!;
    private ComboBox _cmbFuelType = null!;
    private NumericUpDown _stoichInput = null!;
    private NumericUpDown _targetLambdaInput = null!;
    private ComboBox _cmbDisplayMode = null!;
    private bool _running = false;
    private (int row, int col)? _lastHighlighted = null;

    private Panel _tabLiveGraph = null!;
    private Panel _tabFuelMap = null!;
    private Panel _tabDyno = null!;
    private Button _tabBtnLive = null!;
    private Button _tabBtnFuelMap = null!;
    private Button _tabBtnDyno = null!;

    private readonly object _dataLock = new();
    private double _sharedRpm, _sharedTps, _sharedAfr;
    private bool _sharedHasData = false;
    private volatile bool _bgRunning = false;
    private System.Threading.Thread? _bgThread;

    private static readonly string KnownEcusFile = Path.Combine(AppContext.BaseDirectory, "Afr", "known_ecus.txt");
    private readonly Dictionary<string, string> _knownEcus = new();

    public AfrLoggerForm()
    {
        Text = "FKBtuner — กราฟดาต้า (Data Graph) - Real-Time";
        Theme.Apply(this);
        BackColor = Theme.Background;
        WindowState = FormWindowState.Maximized;

        var toolbar = BuildToolbar();
        var tabBar = BuildTabBar();

        BuildLiveGraphTab();
        BuildFuelMapTab();
        BuildDynoTab();

        var contentWrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(8, 6, 8, 8) };
        contentWrap.Controls.Add(_tabDyno);
        contentWrap.Controls.Add(_tabFuelMap);
        contentWrap.Controls.Add(_tabLiveGraph);

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
        Controls.Add(tabBar);
        Controls.Add(toolbar);

        SelectTab(_tabFuelMap, _tabBtnFuelMap);
        BuildGridStructure();
        LoadKnownEcus();

        _timer.Tick += Timer_Tick;
        FormClosed += (_, _) => { _bgRunning = false; _timer.Stop(); _ecuReader.Dispose(); };
    }

    private Panel BuildToolbar()
    {
        var bar = new BorderedPanel { Dock = DockStyle.Top, Height = 172, BackColor = Theme.HeaderBar, Padding = new Padding(0, 0, 0, 1) };

        _btnStartStop = Theme.PrimaryButton("เริ่มจับข้อมูล (จำลอง)");
        _btnStartStop.Location = new Point(10, 8);
        _btnStartStop.Click += BtnStartStop_Click;

        var btnClear = Theme.StyledButton("ล้างตาราง");
        btnClear.Location = new Point(230, 8);
        btnClear.Click += (_, _) => { _logger.Clear(); _lastHighlighted = null; _stripChart.ClearSamples(); RefreshGridColors(); };

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

        var lblFuel = new Label { Text = "เชื้อเพลิง:", AutoSize = true, ForeColor = Theme.Silver, Location = new Point(600, 55) };
        _cmbFuelType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, Location = new Point(668, 51) };
        _cmbFuelType.Items.AddRange(FuelTypes.Select(f => (object)f.Name).ToArray());
        _cmbFuelType.SelectedIndex = 4; // เบนซิน 95 (E0) as a neutral default
        _cmbFuelType.SelectedIndexChanged += CmbFuelType_SelectedIndexChanged;

        var lblStoich = new Label { Text = "Stoich:", AutoSize = true, ForeColor = Theme.Silver, Location = new Point(846, 55) };
        _stoichInput = new NumericUpDown
        {
            Location = new Point(902, 51),
            Width = 65,
            DecimalPlaces = 1,
            Increment = 0.1m,
            Minimum = 8.0m,
            Maximum = 16.0m,
            Value = (decimal)_stoich,
        };
        _stoichInput.ValueChanged += (_, _) => { _stoich = (double)_stoichInput.Value; RefreshGridColors(); };

        var lblTargetLambda = new Label { Text = "เป้า Lambda:", AutoSize = true, ForeColor = Theme.Silver, Location = new Point(980, 55) };
        _targetLambdaInput = new NumericUpDown
        {
            Location = new Point(1082, 51),
            Width = 65,
            DecimalPlaces = 2,
            Increment = 0.01m,
            Minimum = 0.70m,
            Maximum = 1.30m,
            Value = (decimal)_targetLambda,
        };
        _targetLambdaInput.ValueChanged += (_, _) => { _targetLambda = (double)_targetLambdaInput.Value; RefreshGridColors(); };

        var lblDisplayMode = new Label { Text = "แสดงผล:", AutoSize = true, ForeColor = Theme.Silver, Location = new Point(1160, 55) };
        _cmbDisplayMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Location = new Point(1225, 51) };
        _cmbDisplayMode.Items.AddRange(new object[] { "AFR เฉลี่ย", "Lambda เฉลี่ย", "ส่วนต่างจาก Lambda เป้าหมาย (%)", "จำนวนตัวอย่าง" });
        _cmbDisplayMode.SelectedIndex = 0;
        _cmbDisplayMode.SelectedIndexChanged += (_, _) => { _displayMode = (DisplayMode)_cmbDisplayMode.SelectedIndex; RefreshGridColors(); };

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
            btnCalibIdle, btnCalibWot, lblFuel, _cmbFuelType, lblStoich, _stoichInput,
            lblTargetLambda, _targetLambdaInput, lblDisplayMode, _cmbDisplayMode,
            _ecuIdLabel, _liveLabel, lblWarn,
        });
        return bar;
    }

    private void CmbFuelType_SelectedIndexChanged(object? sender, EventArgs e)
    {
        _stoich = FuelTypes[_cmbFuelType.SelectedIndex].Stoich;
        _stoichInput.Value = (decimal)_stoich;
        RefreshGridColors();
    }

    private Panel BuildTabBar()
    {
        var bar = new BorderedPanel { Dock = DockStyle.Top, Height = 44, BackColor = Theme.Panel, Padding = new Padding(0, 0, 0, 1) };

        _tabBtnLive = Theme.StyledButton("กราฟสด (Live Graph)");
        _tabBtnLive.Location = new Point(8, 6);
        _tabBtnLive.Click += (_, _) => SelectTab(_tabLiveGraph, _tabBtnLive);

        _tabBtnFuelMap = Theme.StyledButton("ตารางน้ำมัน (Fuel Map)");
        _tabBtnFuelMap.Location = new Point(190, 6);
        _tabBtnFuelMap.Click += (_, _) => SelectTab(_tabFuelMap, _tabBtnFuelMap);

        _tabBtnDyno = Theme.StyledButton("กราฟไดโน (Dyno Graph)");
        _tabBtnDyno.Location = new Point(380, 6);
        _tabBtnDyno.Click += (_, _) => SelectTab(_tabDyno, _tabBtnDyno);

        bar.Controls.AddRange(new Control[] { _tabBtnLive, _tabBtnFuelMap, _tabBtnDyno });
        return bar;
    }

    private void SelectTab(Panel activePanel, Button activeButton)
    {
        _tabLiveGraph.Visible = ReferenceEquals(activePanel, _tabLiveGraph);
        _tabFuelMap.Visible = ReferenceEquals(activePanel, _tabFuelMap);
        _tabDyno.Visible = ReferenceEquals(activePanel, _tabDyno);

        foreach (var (btn, isActive) in new[] { (_tabBtnLive, activeButton == _tabBtnLive), (_tabBtnFuelMap, activeButton == _tabBtnFuelMap), (_tabBtnDyno, activeButton == _tabBtnDyno) })
        {
            btn.BackColor = isActive ? Theme.Accent : Theme.Panel;
            btn.ForeColor = isActive ? Color.White : Theme.Silver;
        }
    }

    private void BuildLiveGraphTab()
    {
        var card = Theme.CardPanel("กราฟสด (Live Graph)", out var body);
        card.Dock = DockStyle.Fill;

        var lblBigRpm = new Label { Name = "lblBigLive", AutoSize = true, Font = new Font(Theme.MonoFont.FontFamily, 20f, FontStyle.Bold), ForeColor = Theme.Accent, Location = new Point(24, 24) };
        // Reuses _liveLabel's text via a shared updater in Timer_Tick — separate big display for the Live Graph tab.
        _bigLiveLabel = lblBigRpm;
        body.Controls.Add(lblBigRpm);

        var lblHint = new Label
        {
            Text = "กด \"เริ่มจับข้อมูล\" ที่แถบด้านบนเพื่อเริ่มดู RPM/TPS/AFR สด",
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Location = new Point(24, 90),
        };
        body.Controls.Add(lblHint);

        _tabLiveGraph = card;
    }

    private Label _bigLiveLabel = null!;

    private void BuildFuelMapTab()
    {
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
            DefaultCellStyle = { Font = new Font("Consolas", 9.5f), Alignment = DataGridViewContentAlignment.MiddleCenter },
            RowTemplate = { Height = 26 },
            ShowCellErrors = false, // avoids a known WinForms crash ("Cell is not in a DataGridView")
            ShowRowErrors = false,  // when the mouse hovers a cell right as Columns/Rows get rebuilt
            ShowEditingIcon = false,
        };
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.HeaderBar;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Accent;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font(Theme.UiFont, FontStyle.Bold);
        _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _grid.RowHeadersDefaultCellStyle.BackColor = Theme.HeaderBar;
        _grid.RowHeadersDefaultCellStyle.ForeColor = Theme.Accent;
        _grid.RowHeadersDefaultCellStyle.Font = new Font(Theme.UiFont, FontStyle.Bold);
        _grid.EnableHeadersVisualStyles = false;
        _grid.TopLeftHeaderCell.Value = "RPM ⟍ TPS";
        _grid.TopLeftHeaderCell.Style.Font = new Font(Theme.UiFont, FontStyle.Bold);
        _grid.TopLeftHeaderCell.Style.ForeColor = Theme.Accent;
        _grid.CellToolTipTextNeeded += Grid_CellToolTipTextNeeded;
        Theme.EnableDoubleBuffer(_grid);
        _grid.CellPainting += Grid_CellPainting;

        var card = Theme.CardPanel("ตารางน้ำมัน (Fuel Map) — RPM x TPS", out var body);
        card.Dock = DockStyle.Fill;
        body.Controls.Add(_grid);

        _tabFuelMap = card;
    }

    private void BuildDynoTab()
    {
        _stripChart = new LiveStripChartControl { Dock = DockStyle.Fill, Title = "AFR ตามเวลา (30 วินาทีล่าสุด) — ไม่ใช่แรงม้า/แรงบิด", YMin = 9, YMax = 16 };

        var card = Theme.CardPanel("กราฟไดโน (Dyno Graph)", out var body);
        card.Dock = DockStyle.Fill;
        body.Controls.Add(_stripChart);

        var lblNote = new Label
        {
            Text = "หมายเหตุ: กราฟนี้คือแนวโน้ม AFR ตามเวลาระหว่างการทดสอบ ไม่ใช่กราฟแรงม้า/แรงบิดจริง (โปรแกรมนี้ไม่มีข้อมูลจากไดโนมิเตอร์)",
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Dock = DockStyle.Bottom,
            Height = 22,
            Padding = new Padding(8, 2, 0, 0),
        };
        body.Controls.Add(lblNote);

        _tabDyno = card;
    }

    private void BuildGridStructure()
    {
        _grid.Columns.Clear();
        _grid.Rows.Clear();
        _lastHighlighted = null;
        _grid.RowHeadersWidth = 62;

        for (int t = 0; t < TpsBreakpoints.Length; t++)
        {
            double tpsValue = TpsBreakpoints[t];
            var col = new DataGridViewTextBoxColumn
            {
                Name = $"tps_{t}",
                HeaderText = tpsValue.ToString("0.0") + "°",
                Width = 52,
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
            _recordStartTime = DateTime.Now;

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

                _stripChart.AddSample((now - _recordStartTime).TotalSeconds, afr);
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

            _stripChart.AddSample((now - _recordStartTime).TotalSeconds, afr);

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
        double lambdaNow = _displayAfr / _stoich;
        string liveText = $"RPM: {_displayRpm,6:0}   TPS: {_displayTps,5:0.0}°   {afrLabel}: {_displayAfr,5:0.00}   Lambda: {lambdaNow,4:0.00}{rawTpsInfo}";
        _liveLabel.Text = liveText;
        _bigLiveLabel.Text = liveText;

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
        _logger.Table.TryGetValue((rpmBin, tpsBin), out var cell);
        ApplyCellDisplay(cellCtl, cell);
        _grid.InvalidateCell(cellCtl);
    }

    private void RefreshGridColors()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            int rpmBin = (int)row.Tag!;
            for (int t = 0; t <= MaxTpsBin; t++)
            {
                _logger.Table.TryGetValue((rpmBin, t), out var cell);
                ApplyCellDisplay(row.Cells[t], cell);
            }
        }
    }

    private void ApplyCellDisplay(DataGridViewCell cellCtl, AfrCell? cell)
    {
        if (cell == null || cell.SampleCount == 0)
        {
            cellCtl.Value = "";
            cellCtl.Style.BackColor = Theme.Panel;
            return;
        }

        switch (_displayMode)
        {
            case DisplayMode.AfrAvg:
                cellCtl.Value = cell.AvgAfr.ToString("0.00");
                cellCtl.Style.BackColor = AfrToColor(cell.AvgAfr, _stoich, cell.Confidence);
                break;
            case DisplayMode.LambdaAvg:
                double lambda = cell.AvgAfr / _stoich;
                cellCtl.Value = lambda.ToString("0.00");
                cellCtl.Style.BackColor = LambdaToColor(lambda, cell.Confidence);
                break;
            case DisplayMode.LambdaDeviationPct:
                double lam = cell.AvgAfr / _stoich;
                double devPct = (lam - _targetLambda) / _targetLambda * 100.0;
                cellCtl.Value = devPct.ToString("+0.0;-0.0;0.0") + "%";
                cellCtl.Style.BackColor = DivergingColor(devPct, cell.Confidence);
                break;
            case DisplayMode.SampleCount:
            default:
                cellCtl.Value = cell.SampleCount.ToString();
                cellCtl.Style.BackColor = SampleCountColor(cell.SampleCount);
                break;
        }
        cellCtl.Style.ForeColor = Color.Black;
    }

    private void Grid_CellToolTipTextNeeded(object? sender, DataGridViewCellToolTipTextNeededEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        var row = _grid.Rows[e.RowIndex];
        int rpmBin = (int)row.Tag!;
        if (_logger.Table.TryGetValue((rpmBin, e.ColumnIndex), out var cell) && cell.SampleCount > 0)
        {
            e.ToolTipText = $"AFR เฉลี่ย: {cell.AvgAfr:0.00}   Lambda: {cell.AvgAfr / _stoich:0.00}\n" +
                             $"Std Dev: {cell.StdDev:0.00}\n" +
                             $"จำนวน sample: {cell.SampleCount}";
        }
    }

    /// <summary>แปลงค่า AFR เป็นสี heatmap: แดง = รวย (AFR ต่ำกว่า stoich), เขียว = ใกล้ stoich, ฟ้า = บาง (AFR สูงกว่า stoich)</summary>
    private static Color AfrToColor(double afr, double stoich, double confidence)
    {
        Color baseColor;
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

    /// <summary>Same idea as AfrToColor but centered on Lambda=1.0 with a +-0.2 saturation window.</summary>
    private static Color LambdaToColor(double lambda, double confidence)
    {
        Color baseColor;
        if (lambda < 1.0)
        {
            double t = Math.Clamp((1.0 - lambda) / 0.2, 0, 1);
            baseColor = Lerp(Color.LimeGreen, Color.Red, t);
        }
        else
        {
            double t = Math.Clamp((lambda - 1.0) / 0.2, 0, 1);
            baseColor = Lerp(Color.LimeGreen, Color.DeepSkyBlue, t);
        }
        return Lerp(Color.White, baseColor, 0.3 + 0.7 * confidence);
    }

    /// <summary>Diverging color for a signed %, e.g. deviation from target lambda: blue = negative, gray = zero, red = positive.</summary>
    private static Color DivergingColor(double pct, double confidence)
    {
        double t = Math.Clamp(pct / 15.0, -1.0, 1.0);
        Color baseColor = t >= 0
            ? Lerp(Color.FromArgb(0xE8, 0xE8, 0xEC), Color.FromArgb(0xD8, 0x3A, 0x2E), t)
            : Lerp(Color.FromArgb(0xE8, 0xE8, 0xEC), Color.FromArgb(0x2F, 0x9E, 0xF0), -t);
        return Lerp(Color.White, baseColor, 0.3 + 0.7 * confidence);
    }

    private static Color SampleCountColor(int count)
    {
        double t = Math.Clamp(count / 30.0, 0, 1);
        return Lerp(Color.White, Theme.Accent, 0.2 + 0.8 * t);
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

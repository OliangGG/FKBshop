using BinTuner.Afr;

namespace BinTuner.UI;

/// <summary>
/// Records live RPM/TPS/AFR binned directly onto the CURRENTLY OPEN fuel table's real row/col
/// breakpoints (not a generic grid), then shows a suggested % fuel correction per cell
/// (standard "fuel trim" formula: (measured - target) / target * 100) that the user can review
/// and apply back into the table with one click, reusing EditorForm's own undo history.
///
/// IMPORTANT: the AFR fed into this is whatever HondaEcuReader currently provides, which — for
/// the K3MH-T71 ECU tested so far — is a TPS/RPM-based ESTIMATE, not a measured value from a
/// real O2/wideband sensor (see HondaEcuReader's own class comment). This form says so plainly
/// and never applies anything automatically; the shop always reviews the suggestion table first.
/// </summary>
public class AfrTuneAdvisorForm : Form
{
    private readonly double[] _rowAxis; // RPM value per row, same axis as the open table
    private readonly double[] _colAxis; // TPS value per col, same axis as the open table
    private readonly Action<Dictionary<(int r, int c), double>> _onApply;

    private readonly AfrCell[,] _cells;
    private readonly double[,] _targetAfr; // per-cell target AFR, editable before/after recording
    private const int MinSamplesForSuggestion = 5;
    private bool _editingTargets;

    private readonly DataSimulator _simulator = new(sampleIntervalMs: 50);
    private readonly HondaEcuReader _ecuReader = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 60 }; // no smoothed readout here, unlike AfrLoggerForm — doesn't need 60Hz

    private readonly object _dataLock = new();
    private double _sharedRpm, _sharedTps, _sharedAfr;
    private bool _sharedHasData;
    private volatile bool _bgRunning;
    private System.Threading.Thread? _bgThread;
    private bool _running;

    private DataGridView _grid = null!;
    private Button _btnStartStop = null!;
    private CheckBox _liveModeCheckbox = null!;
    private NumericUpDown _targetAfrInput = null!;
    private CheckBox _chkEditTargets = null!;
    private Button _btnFillTarget = null!;
    private Button _btnApply = null!;
    private Button _btnCalibIdle = null!;
    private Button _btnCalibWot = null!;
    private Label _statusLabel = null!;

    public AfrTuneAdvisorForm(string tableName, double[] rowAxis, double[] colAxis, Action<Dictionary<(int r, int c), double>> onApply)
    {
        _rowAxis = rowAxis;
        _colAxis = colAxis;
        _onApply = onApply;
        _cells = new AfrCell[rowAxis.Length, colAxis.Length];
        _targetAfr = new double[rowAxis.Length, colAxis.Length];
        for (int r = 0; r < rowAxis.Length; r++)
            for (int c = 0; c < colAxis.Length; c++)
            {
                _cells[r, c] = new AfrCell();
                _targetAfr[r, c] = 14.7;
            }

        Text = $"FKBtuner — แนะนำการจูนจาก AFR — {tableName}";
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
            ReadOnly = true,
            RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.AutoSizeToAllHeaders,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
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
        _grid.CellEndEdit += Grid_CellEndEdit;
        Theme.EnableDoubleBuffer(_grid);

        BuildGridStructure();

        var gridCard = Theme.CardPanel("คำแนะนำแก้ไขตาราง (% ที่แนะนำให้ปรับ)", out var gridBody);
        gridCard.Dock = DockStyle.Fill;
        gridBody.Controls.Add(_grid);

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            BackColor = Theme.HeaderBar,
            ForeColor = Theme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            Text = "ยังไม่เริ่มบันทึก",
        };

        var wrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(8, 6, 8, 8) };
        wrap.Controls.Add(gridCard);

        Controls.Add(wrap);
        Controls.Add(_statusLabel);
        Controls.Add(toolbar);

        _timer.Tick += Timer_Tick;
        FormClosed += (_, _) => { _bgRunning = false; _timer.Stop(); _ecuReader.Dispose(); };
    }

    private Panel BuildToolbar()
    {
        var bar = new BorderedPanel { Dock = DockStyle.Top, Height = 212, BackColor = Theme.HeaderBar, Padding = new Padding(0, 0, 0, 1) };
        const int row1 = 12, row2 = 56, row2b = 90, row3 = 130, row4 = 170;

        // แถว 1 — เริ่ม/หยุดบันทึก, ต่อ ECU จริง
        _btnStartStop = Theme.PrimaryButton("เริ่มบันทึก (จำลอง)");
        _btnStartStop.Location = new Point(12, row1);
        _btnStartStop.Click += BtnStartStop_Click;

        _liveModeCheckbox = new CheckBox
        {
            Text = "ต่อ ECU จริง (ไม่ใช่จำลอง)",
            AutoSize = true,
            ForeColor = Theme.Silver,
            Location = new Point(232, row1 + 7),
        };
        _liveModeCheckbox.CheckedChanged += (_, _) =>
            _btnStartStop.Text = _liveModeCheckbox.Checked ? "เชื่อมต่อ & เริ่มบันทึก (ECU จริง)" : "เริ่มบันทึก (จำลอง)";

        var btnCalibIdle = Theme.StyledButton("Calibrate: ตอนนี้คือ TPS 0% (ไม่บิดคันเร่ง)");
        btnCalibIdle.Location = new Point(470, row1);
        btnCalibIdle.Click += (_, _) =>
        {
            if (!_liveModeCheckbox.Checked || !_ecuReader.IsConnected)
            {
                MessageBox.Show(this, "ต้องต่อ ECU จริงและกำลังบันทึกอยู่ก่อน ถึงจะ calibrate ได้", "ยังเชื่อมต่อไม่ได้", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _ecuReader.CalibrateIdle();
        };
        _btnCalibIdle = btnCalibIdle;

        var btnCalibWot = Theme.StyledButton("Calibrate: ตอนนี้คือ TPS 100% (บิดสุด)");
        btnCalibWot.Location = new Point(760, row1);
        btnCalibWot.Click += (_, _) =>
        {
            if (!_liveModeCheckbox.Checked || !_ecuReader.IsConnected)
            {
                MessageBox.Show(this, "ต้องต่อ ECU จริงและกำลังบันทึกอยู่ก่อน ถึงจะ calibrate ได้", "ยังเชื่อมต่อไม่ได้", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _ecuReader.CalibrateWideOpen();
        };
        _btnCalibWot = btnCalibWot;

        // แถว 2 — เป้าหมาย AFR ต่อช่อง
        var lblTarget = new Label { Text = "ค่าที่จะเติม (AFR):", AutoSize = true, ForeColor = Theme.Silver, Location = new Point(12, row2 + 7) };
        _targetAfrInput = new NumericUpDown
        {
            Location = new Point(148, row2 + 3),
            Width = 75,
            DecimalPlaces = 1,
            Increment = 0.1m,
            Minimum = 10.0m,
            Maximum = 18.0m,
            Value = 14.7m,
        };

        _btnFillTarget = Theme.StyledButton("เติมเป้าหมายเดียวกันทั้งตาราง");
        _btnFillTarget.Location = new Point(238, row2);
        _btnFillTarget.Click += BtnFillTarget_Click;

        var btnAutoTarget = Theme.PrimaryButton("ตั้งเป้าอัตโนมัติตามโซน RPM/TPS");
        btnAutoTarget.Location = new Point(480, row2);
        btnAutoTarget.Click += BtnAutoTarget_Click;

        var lblAutoHint = new Label
        {
            Text = "(เดินเบา~1.00 → เร่งปานกลาง~0.90 → บิดสุด/รอบสูง~0.82 ตามค่าที่ใส่ด้านซ้ายเป็นฐาน)",
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Location = new Point(12, row2b),
        };

        _chkEditTargets = new CheckBox
        {
            Text = "ตั้ง AFR เป้าหมายทีละช่อง (พิมพ์ในตารางด้านล่างได้เลย)",
            AutoSize = true,
            ForeColor = Theme.Accent,
            Location = new Point(12, row2b + 20),
        };
        _chkEditTargets.CheckedChanged += ChkEditTargets_CheckedChanged;

        // แถว 3 — คำสั่งใช้ผล + คำอธิบายสี
        _btnApply = Theme.DangerButton("ใช้คำแนะนำนี้กับตารางจริง");
        _btnApply.Location = new Point(12, row3);
        _btnApply.Enabled = false;
        _btnApply.Click += BtnApply_Click;

        var legendSwatchRed = new Panel { BackColor = Color.FromArgb(0xD8, 0x3A, 0x2E), Size = new Size(16, 16), Location = new Point(300, row3 + 8) };
        var legendLabelRed = new Label { Text = "= แนะนำเพิ่มน้ำมัน (บาง/AFR สูงกว่าเป้า)", AutoSize = true, ForeColor = Theme.Silver, Location = new Point(322, row3 + 6) };
        var legendSwatchBlue = new Panel { BackColor = Color.FromArgb(0x2F, 0x9E, 0xF0), Size = new Size(16, 16), Location = new Point(620, row3 + 8) };
        var legendLabelBlue = new Label { Text = "= แนะนำลดน้ำมัน (เข้ม/AFR ต่ำกว่าเป้า)", AutoSize = true, ForeColor = Theme.Silver, Location = new Point(642, row3 + 6) };

        var lblWarn = new Label
        {
            Text = "คำแนะนำนี้คำนวณจาก AFR \"ประมาณการ\" (TPS/RPM) ไม่ใช่ค่าจริงจากเซนเซอร์วัดไอเสีย — ใช้เป็นแนวทางคร่าวๆ เท่านั้น\n" +
                   "ตรวจสอบด้วยเครื่องวัด wideband AFR จริงก่อนเชื่อผลเต็มที่ และดู % ที่แนะนำในตารางก่อนกด \"ใช้คำแนะนำนี้กับตารางจริง\" เสมอ",
            AutoSize = true,
            ForeColor = Theme.Warning,
            Location = new Point(12, row4),
        };

        bar.Controls.AddRange(new Control[]
        {
            _btnStartStop, _liveModeCheckbox, btnCalibIdle, btnCalibWot,
            lblTarget, _targetAfrInput, _btnFillTarget, btnAutoTarget, lblAutoHint, _chkEditTargets,
            _btnApply, legendSwatchRed, legendLabelRed, legendSwatchBlue, legendLabelBlue, lblWarn,
        });
        return bar;
    }

    private void BuildGridStructure()
    {
        _grid.Columns.Clear();
        _grid.Rows.Clear();
        _grid.RowHeadersWidth = 62;

        for (int c = 0; c < _colAxis.Length; c++)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = $"col{c}",
                HeaderText = _colAxis[c].ToString("0.##"),
                Width = 58,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            });
        }

        for (int r = 0; r < _rowAxis.Length; r++)
        {
            int rowIdx = _grid.Rows.Add();
            _grid.Rows[rowIdx].HeaderCell.Value = Math.Round(_rowAxis[r]).ToString("0");
        }
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
                            "เชื่อมต่อ ECU ไม่สำเร็จหลังลอง 5 ครั้ง เช็คว่าบิดกุญแจ ON, ปิดโปรแกรมอื่นที่จับสาย K-line ค้างอยู่, ลง FTDI driver แล้ว, และขั้วต่อสายแน่นดีไหม",
                            "เชื่อมต่อไม่สำเร็จ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        _btnStartStop.Enabled = true;
                        _btnStartStop.Text = "เชื่อมต่อ & เริ่มบันทึก (ECU จริง)";
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "เปิดอุปกรณ์ไม่สำเร็จ: " + ex.Message, "ผิดพลาด", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _btnStartStop.Enabled = true;
                _btnStartStop.Text = "เชื่อมต่อ & เริ่มบันทึก (ECU จริง)";
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
            _btnStartStop.Text = "หยุดบันทึก";
            _liveModeCheckbox.Enabled = false;
            _btnApply.Enabled = false;

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
            _btnStartStop.Text = _liveModeCheckbox.Checked ? "เชื่อมต่อ & เริ่มบันทึก (ECU จริง)" : "เริ่มบันทึก (จำลอง)";
            _liveModeCheckbox.Enabled = true;
            _timer.Stop();
            _bgRunning = false;
            RefreshSuggestions();
            _btnApply.Enabled = HasAnySuggestion();
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
                lock (_dataLock) { _sharedRpm = rpm; _sharedTps = tps; _sharedAfr = afr; _sharedHasData = true; }
            }
            else
            {
                consecutiveFailures++;
                if (consecutiveFailures >= 20) { _ecuReader.Init(); consecutiveFailures = 0; }
            }
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        double rpm, tps, afr;
        if (_liveModeCheckbox.Checked)
        {
            bool hasData;
            lock (_dataLock)
            {
                hasData = _sharedHasData;
                rpm = _sharedRpm; tps = _sharedTps; afr = _sharedAfr;
                _sharedHasData = false;
            }
            if (!hasData)
            {
                _statusLabel.Text = _ecuReader.IsConnected ? "เชื่อมต่ออยู่ (รอข้อมูลรอบถัดไป)" : "หลุดการเชื่อมต่อ กำลังลองใหม่";
                return;
            }
        }
        else
        {
            (rpm, tps, afr) = _simulator.NextSample();
        }

        int r = FindBinIndex(_rowAxis, rpm);
        int c = FindBinIndex(_colAxis, tps);
        _cells[r, c].AddSample(afr);

        int totalSamples = 0;
        foreach (var cell in _cells) totalSamples += cell.SampleCount;
        _statusLabel.Text = $"กำลังบันทึก... RPM={rpm:0} TPS={tps:0.0}° AFR(ประมาณ)={afr:0.00}    รวมตัวอย่างสะสม: {totalSamples}";

        // Only repaint the one cell that changed this tick — looping the whole grid here (60x/sec)
        // was the cause of the reported flicker/lag while scrolling during a recording session.
        if (!_editingTargets) ApplyCellSuggestion(r, c);
    }

    private static int FindBinIndex(double[] breakpoints, double value)
    {
        int idx = 0;
        for (int i = 0; i < breakpoints.Length; i++)
        {
            if (breakpoints[i] <= value) idx = i;
            else break;
        }
        return idx;
    }

    private bool HasAnySuggestion()
    {
        foreach (var cell in _cells)
            if (cell.SampleCount >= MinSamplesForSuggestion) return true;
        return false;
    }

    private void ChkEditTargets_CheckedChanged(object? sender, EventArgs e)
    {
        _editingTargets = _chkEditTargets.Checked;
        _grid.ReadOnly = !_editingTargets;
        if (_editingTargets) RefreshTargetEditGrid();
        else RefreshSuggestions();
    }

    private void BtnFillTarget_Click(object? sender, EventArgs e)
    {
        double value = (double)_targetAfrInput.Value;
        for (int r = 0; r < _rowAxis.Length; r++)
            for (int c = 0; c < _colAxis.Length; c++)
                _targetAfr[r, c] = value;

        if (_editingTargets) RefreshTargetEditGrid();
        else RefreshSuggestions();
    }

    /// <summary>Auto-fills per-cell target AFR from a TPS%-based lambda curve (idle~1.00 -> WOT~0.82),
    /// matching the idle/cruise/accel/full-throttle pattern common in real tuning (and in the user's
    /// own ARTTUNER screenshot: idle 0.98 / cruise 1.00 / accel 0.91 / full 0.85) — richens further at
    /// high RPM+high TPS combined, mirroring the enrichment logic already used for the AFR estimate.
    /// "ค่าที่จะเติม (AFR)" is treated as the stoichiometric/reference AFR the curve scales down from.</summary>
    private void BtnAutoTarget_Click(object? sender, EventArgs e)
    {
        double baseAfr = (double)_targetAfrInput.Value;
        for (int r = 0; r < _rowAxis.Length; r++)
        {
            for (int c = 0; c < _colAxis.Length; c++)
            {
                double lambda = AutoTargetLambda(_colAxis[c], _rowAxis[r]);
                _targetAfr[r, c] = baseAfr * lambda;
            }
        }

        if (_editingTargets) RefreshTargetEditGrid();
        else RefreshSuggestions();
    }

    private static double AutoTargetLambda(double tpsPercent, double rpm)
    {
        // Piecewise-linear anchor points: (TPS%, target lambda)
        var points = new (double tps, double lambda)[]
        {
            (0, 1.00), (15, 0.95), (40, 0.90), (70, 0.85), (100, 0.82),
        };

        double lambda = points[^1].lambda;
        for (int i = 0; i < points.Length - 1; i++)
        {
            var (t0, l0) = points[i];
            var (t1, l1) = points[i + 1];
            if (tpsPercent <= t1)
            {
                double frac = t1 > t0 ? (tpsPercent - t0) / (t1 - t0) : 0;
                lambda = l0 + (l1 - l0) * Math.Clamp(frac, 0, 1);
                break;
            }
        }

        // Extra richness margin at high RPM combined with high load (engine protection, matches the
        // "highRpmEnrichment" idea already used in HondaEcuReader's AFR estimate formula)
        if (rpm > 7000 && tpsPercent > 50)
            lambda -= 0.02 * Math.Min(1.0, (rpm - 7000) / 3000.0);

        return Math.Clamp(lambda, 0.75, 1.05);
    }

    /// <summary>Shows the per-cell target AFR table for editing (blue tint distinguishes it from the suggestion view).</summary>
    private void RefreshTargetEditGrid()
    {
        for (int r = 0; r < _rowAxis.Length; r++)
        {
            for (int c = 0; c < _colAxis.Length; c++)
            {
                var gridCell = _grid.Rows[r].Cells[c];
                gridCell.Value = _targetAfr[r, c].ToString("0.0");
                gridCell.Style.BackColor = Color.FromArgb(20, 30, 45);
                gridCell.Style.ForeColor = Theme.Silver;
            }
        }
    }

    private void Grid_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (!_editingTargets) return;
        var cell = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex];

        if (!double.TryParse(cell.Value?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value))
        {
            cell.Value = _targetAfr[e.RowIndex, e.ColumnIndex].ToString("0.0");
            return;
        }

        value = Math.Clamp(value, 9.0, 18.0);
        _targetAfr[e.RowIndex, e.ColumnIndex] = value;
        cell.Value = value.ToString("0.0");
    }

    /// <summary>Standard fuel-trim style correction: leaner-than-target -> positive % (add fuel); richer-than-target -> negative % (remove fuel).</summary>
    private void RefreshSuggestions()
    {
        if (_editingTargets) return;

        for (int r = 0; r < _rowAxis.Length; r++)
            for (int c = 0; c < _colAxis.Length; c++)
                ApplyCellSuggestion(r, c);
    }

    private void ApplyCellSuggestion(int r, int c)
    {
        var cell = _cells[r, c];
        var gridCell = _grid.Rows[r].Cells[c];

        if (cell.SampleCount < MinSamplesForSuggestion)
        {
            gridCell.Value = cell.SampleCount == 0 ? "" : $"({cell.SampleCount})";
            gridCell.Style.BackColor = Theme.Panel;
            gridCell.Style.ForeColor = Theme.TextMuted;
            return;
        }

        double target = _targetAfr[r, c];
        double pct = (cell.AvgAfr - target) / target * 100.0;
        gridCell.Value = pct.ToString("+0.0;-0.0;0.0") + "%";
        gridCell.Style.BackColor = DivergingColor(pct, cell.Confidence);
        gridCell.Style.ForeColor = Color.Black;
    }

    /// <summary>Diverging heat color for a signed correction %: blue = remove fuel, gray = no change, red/orange = add fuel.</summary>
    private static Color DivergingColor(double pct, double confidence)
    {
        double t = Math.Clamp(pct / 15.0, -1.0, 1.0); // +-15% treated as full saturation
        Color baseColor = t >= 0
            ? Lerp(Color.FromArgb(0xE8, 0xE8, 0xEC), Color.FromArgb(0xD8, 0x3A, 0x2E), t)
            : Lerp(Color.FromArgb(0xE8, 0xE8, 0xEC), Color.FromArgb(0x2F, 0x9E, 0xF0), -t);
        return Lerp(Color.White, baseColor, 0.3 + 0.7 * confidence);
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    private void BtnApply_Click(object? sender, EventArgs e)
    {
        var suggestions = new Dictionary<(int r, int c), double>();

        for (int r = 0; r < _rowAxis.Length; r++)
            for (int c = 0; c < _colAxis.Length; c++)
            {
                var cell = _cells[r, c];
                if (cell.SampleCount < MinSamplesForSuggestion) continue;
                double target = _targetAfr[r, c];
                suggestions[(r, c)] = (cell.AvgAfr - target) / target * 100.0;
            }

        if (suggestions.Count == 0)
        {
            MessageBox.Show(this, "ยังไม่มีช่องไหนมีข้อมูลพอจะแนะนำได้ (ต้องมีอย่างน้อย " + MinSamplesForSuggestion + " ตัวอย่างต่อช่อง)", "แจ้งเตือน", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"จะปรับ {suggestions.Count} ช่องในตารางจริงตามค่าที่แนะนำ (คำนวณจาก AFR ประมาณการ ไม่ใช่ค่าเซนเซอร์จริง)\n" +
            "แก้ไขนี้จะถูกบันทึกใน Undo History เดียว กด ย้อนกลับ ได้ถ้าไม่พอใจผล\n\nดำเนินการต่อหรือไม่?",
            "ยืนยันการใช้คำแนะนำ", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        _onApply(suggestions);
        MessageBox.Show(this, "ใช้คำแนะนำกับตารางแล้ว — กลับไปดูที่หน้าตารางเพื่อตรวจสอบ/Undo ได้", "เสร็จสิ้น", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

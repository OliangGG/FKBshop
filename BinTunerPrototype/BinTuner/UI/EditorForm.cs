using BinTuner.BinIO;
using BinTuner.Editing;
using BinTuner.Models;
using BinTuner.Util;

namespace BinTuner.UI;

public class EditorForm : Form
{
    private readonly byte[] _data;
    private readonly byte[] _original;
    private readonly string _binPath;
    private readonly ParsedXdf _xdf;
    private readonly EditHistory _history = new();

    private readonly TreeView _tree;
    private readonly DataGridView _grid;
    private readonly HexViewerControl _hex;
    private readonly SplitContainer _rightSplit;
    private readonly Label _lblTableInfo;
    private readonly Label _lblAxisInfo;
    private readonly Button _btnUndo;
    private readonly Button _btnRedo;
    private readonly Panel _flagPanel;
    private readonly CheckBox _chkFlag;
    private readonly Label _lblFlagInfo;
    private readonly ComboBox _cmbFunction;
    private readonly TextBox _txtFunctionValue;
    private readonly Label _lblCompare;

    private TableDef? _selected;
    private FlagDef? _selectedFlag;
    private bool _suppressGridEvents;
    private bool _suppressFlagEvents;
    private byte[]? _compareData;
    private string? _compareLabel;

    private const string FnOffset = "บวก/ลบค่า (Offset)";
    private const string FnMultiply = "คูณค่า (Multiply)";
    private const string FnDivide = "หารค่า (Divide)";
    private const string FnScaleByPercent = "ปรับเป็น % (Scale)";
    private const string FnFillWithValue = "เติมค่าเดียวกันทั้งหมด (Fill)";
    private const string FnSmooth = "ปรับให้เรียบ (Smooth)";
    private const string FnInterpolateX = "เชื่อมค่าแนวนอน (Interpolate X)";
    private const string FnInterpolateY = "เชื่อมค่าแนวตั้ง (Interpolate Y)";
    private const string FnInterpolateXY = "เชื่อมค่าแนวตั้ง+นอน (Interpolate XY)";
    private const string FnCopyFromCompare = "คัดลอกจากไฟล์เปรียบเทียบ";

    public EditorForm(byte[] binData, string binPath, ParsedXdf xdf)
    {
        _data = (byte[])binData.Clone();
        _original = (byte[])binData.Clone();
        _binPath = binPath;
        _xdf = xdf;

        Text = $"FKBtuner — แก้ตาราง ECU — {Path.GetFileName(binPath)}";
        Theme.Apply(this);
        BackColor = Theme.Background;
        WindowState = FormWindowState.Maximized;

        var toolbar = BuildToolbar();

        _tree = new TreeView
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ForeColor = Theme.Silver,
            BorderStyle = BorderStyle.None,
            HideSelection = false,
            Font = Theme.UiFont,
        };
        _tree.AfterSelect += (_, e) =>
        {
            if (e.Node?.Tag is FlagDef flag) SelectFlag(flag);
            else SelectTable(e.Node?.Tag as TableDef);
        };

        var treeCard = Theme.CardPanel("รายการพารามิเตอร์ (Parameter Tree)", out var treeBody);
        treeCard.Dock = DockStyle.Fill;
        treeBody.Padding = new Padding(2);
        treeBody.Controls.Add(_tree);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Theme.Panel,
            GridColor = Theme.GridLine,
            BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.AutoSizeToAllHeaders,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
            Font = Theme.UiFont,
        };
        _grid.DefaultCellStyle.BackColor = Theme.Background;
        _grid.DefaultCellStyle.ForeColor = Color.Black;
        _grid.DefaultCellStyle.SelectionBackColor = Theme.Accent;
        _grid.DefaultCellStyle.SelectionForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.HeaderBar;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Accent;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font(Theme.UiFont, FontStyle.Bold);
        _grid.RowHeadersDefaultCellStyle.BackColor = Theme.HeaderBar;
        _grid.RowHeadersDefaultCellStyle.ForeColor = Theme.Accent;
        _grid.RowHeadersDefaultCellStyle.Font = new Font(Theme.UiFont, FontStyle.Bold);
        _grid.EnableHeadersVisualStyles = false;
        _grid.CellEndEdit += Grid_CellEndEdit;

        _lblTableInfo = new Label { Dock = DockStyle.Top, Height = 26, BackColor = Theme.HeaderBar, ForeColor = Theme.Silver, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) };
        _lblAxisInfo = new Label { Dock = DockStyle.Top, Height = 22, BackColor = Theme.HeaderBar, ForeColor = Theme.Accent, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) };

        _chkFlag = new CheckBox
        {
            Text = "",
            AutoSize = true,
            Location = new Point(24, 24),
            Font = new Font(Theme.UiFont.FontFamily, 14f),
            ForeColor = Theme.Silver,
            BackColor = Color.Transparent,
        };
        _chkFlag.CheckedChanged += ChkFlag_CheckedChanged;

        _lblFlagInfo = new Label { AutoSize = true, Location = new Point(24, 60), ForeColor = Theme.TextMuted };

        var lblFlagWarning = new Label
        {
            Text = "คำเตือน: บาง flag เกี่ยวข้องกับความปลอดภัย/ระบบล็อกของรถ (เช่น เซนเซอร์นิรภัยขาตั้งข้าง, ระบบล็อกสตาร์ท)\n" +
                   "เปลี่ยนแล้วอาจกระทบพฤติกรรมของรถโดยตรง โปรดตรวจสอบให้แน่ใจก่อนบันทึกและนำไป flash จริง",
            AutoSize = true,
            Location = new Point(24, 100),
            ForeColor = Theme.Warning,
        };

        _flagPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Visible = false };
        _flagPanel.Controls.AddRange(new Control[] { _chkFlag, _lblFlagInfo, lblFlagWarning });

        var gridCard = Theme.CardPanel("ตารางค่า / จูน", out var gridBody);
        gridCard.Dock = DockStyle.Fill;
        gridBody.Controls.Add(_grid);
        gridBody.Controls.Add(_flagPanel);
        gridBody.Controls.Add(_lblAxisInfo);
        gridBody.Controls.Add(_lblTableInfo);

        _hex = new HexViewerControl { Dock = DockStyle.Fill, Data = _data };
        var hexCard = Theme.CardPanel("มุมมอง Hex (ไบต์ดิบ)", out var hexBody);
        hexCard.Dock = DockStyle.Fill;
        hexBody.Padding = new Padding(2);
        hexBody.Controls.Add(_hex);

        _rightSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 460, BackColor = Theme.Background, SplitterWidth = 6 };
        _rightSplit.Panel1.BackColor = Theme.Background;
        _rightSplit.Panel2.BackColor = Theme.Background;
        _rightSplit.Panel1.Controls.Add(gridCard);
        _rightSplit.Panel2.Controls.Add(hexCard);
        _rightSplit.Panel2Collapsed = true; // Hex view is off by default — optional, toggled from the toolbar

        var mainSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 360, BackColor = Theme.Background, SplitterWidth = 6 };
        mainSplit.Panel1.BackColor = Theme.Background;
        mainSplit.Panel2.BackColor = Theme.Background;
        mainSplit.Panel1.Controls.Add(treeCard);
        mainSplit.Panel2.Controls.Add(_rightSplit);

        var contentWrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(8, 6, 8, 8) };
        contentWrap.Controls.Add(mainSplit);

        Controls.Add(contentWrap);
        Controls.Add(toolbar);

        _btnUndo = (Button)toolbar.Controls["btnUndo"]!;
        _btnRedo = (Button)toolbar.Controls["btnRedo"]!;
        _cmbFunction = (ComboBox)toolbar.Controls["cmbFunction"]!;
        _txtFunctionValue = (TextBox)toolbar.Controls["txtFunctionValue"]!;
        _lblCompare = (Label)toolbar.Controls["lblCompare"]!;
        UpdateUndoRedoButtons();

        RebuildTree();
    }

    private Panel BuildToolbar()
    {
        var bar = new BorderedPanel { Dock = DockStyle.Top, Height = 94, BackColor = Theme.HeaderBar, Padding = new Padding(0, 0, 0, 1) };

        var btnSaveAs = Theme.PrimaryButton("บันทึกเป็น...");
        btnSaveAs.Name = "btnSaveAs";
        btnSaveAs.Location = new Point(10, 8);
        btnSaveAs.Click += (_, _) => SaveAs();

        var btnUndo = Theme.StyledButton("ย้อนกลับ");
        btnUndo.Name = "btnUndo";
        btnUndo.Location = new Point(150, 8);
        btnUndo.Click += (_, _) => { _history.Undo(_data); UpdateUndoRedoButtons(); RefreshSelectionFromData(); _hex.Invalidate(); };

        var btnRedo = Theme.StyledButton("ทำซ้ำ");
        btnRedo.Name = "btnRedo";
        btnRedo.Location = new Point(250, 8);
        btnRedo.Click += (_, _) => { _history.Redo(_data); UpdateUndoRedoButtons(); RefreshSelectionFromData(); _hex.Invalidate(); };

        var btnManual = Theme.StyledButton("เพิ่มตารางเอง...");
        btnManual.Location = new Point(340, 8);
        btnManual.Click += (_, _) => AddManualTable();

        var btnLoadCompare = Theme.StyledButton("โหลดไฟล์เปรียบเทียบ...");
        btnLoadCompare.Location = new Point(490, 8);
        btnLoadCompare.Click += (_, _) => LoadCompareFile();

        var lblCompare = new Label
        {
            Name = "lblCompare",
            Text = "ไฟล์เปรียบเทียบ: (ยังไม่โหลด)",
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Location = new Point(665, 16),
        };

        var chkShowHex = new CheckBox
        {
            Name = "chkShowHex",
            Text = "แสดงมุมมอง Hex (ไบต์ดิบ)",
            AutoSize = true,
            ForeColor = Theme.Silver,
            Checked = false,
            Location = new Point(900, 14),
        };
        chkShowHex.CheckedChanged += (_, _) => _rightSplit.Panel2Collapsed = !chkShowHex.Checked;

        // Row 2 — ปรับค่าตาราง (แบบเดียวกับ TunerPro): ฟังก์ชัน / ค่า / ทำงาน
        var lblFn = new Label { Text = "ฟังก์ชัน:", AutoSize = true, ForeColor = Theme.Silver, Location = new Point(10, 60) };

        var cmbFunction = new ComboBox
        {
            Name = "cmbFunction",
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 260,
            Location = new Point(80, 56),
            Font = Theme.UiFont,
        };
        cmbFunction.Items.AddRange(new object[]
        {
            FnOffset, FnMultiply, FnDivide, FnScaleByPercent, FnFillWithValue,
            FnSmooth, FnInterpolateX, FnInterpolateY, FnInterpolateXY, FnCopyFromCompare,
        });
        cmbFunction.SelectedIndex = 0;

        var lblVal = new Label { Text = "ค่า:", AutoSize = true, ForeColor = Theme.Silver, Location = new Point(352, 60) };
        var txtValue = new TextBox { Name = "txtFunctionValue", Width = 80, Location = new Point(384, 56), Text = "1", Font = Theme.UiFont };

        var btnExecute = Theme.PrimaryButton("ทำงาน");
        btnExecute.Location = new Point(478, 55);
        btnExecute.Click += (_, _) => ExecuteFunction();

        var lblHint = new Label
        {
            Text = "เลือกช่องในตารางก่อน แล้วเลือกฟังก์ชัน ใส่ค่า แล้วกด \"ทำงาน\"",
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Location = new Point(600, 60),
        };

        bar.Controls.AddRange(new Control[]
        {
            btnSaveAs, btnUndo, btnRedo, btnManual, btnLoadCompare, lblCompare, chkShowHex,
            lblFn, cmbFunction, lblVal, txtValue, btnExecute, lblHint,
        });
        return bar;
    }

    private void RebuildTree()
    {
        _tree.Nodes.Clear();
        var scalarsRoot = new TreeNode("ค่าคงที่ (Scalars)");
        var flagsRoot = new TreeNode("สวิตช์เปิด/ปิด (Flags)");
        var tablesRoot = new TreeNode("ตาราง (Tables)");

        foreach (var group in _xdf.Tables.GroupBy(t => t.Category))
        {
            var scalarCat = new TreeNode(group.Key);
            var tableCat = new TreeNode(group.Key);
            bool hasScalar = false, hasTable = false;

            foreach (var t in group.OrderBy(t => t.Name))
            {
                var node = new TreeNode(t.Name) { Tag = t };
                if (t.Kind == ParamKind.Scalar) { scalarCat.Nodes.Add(node); hasScalar = true; }
                else { tableCat.Nodes.Add(node); hasTable = true; }
            }
            if (hasScalar) scalarsRoot.Nodes.Add(scalarCat);
            if (hasTable) tablesRoot.Nodes.Add(tableCat);
        }

        foreach (var group in _xdf.Flags.GroupBy(f => f.Category))
        {
            var flagCat = new TreeNode(group.Key);
            foreach (var f in group.OrderBy(f => f.Name))
                flagCat.Nodes.Add(new TreeNode(f.Name) { Tag = f });
            flagsRoot.Nodes.Add(flagCat);
        }

        if (scalarsRoot.Nodes.Count > 0) _tree.Nodes.Add(scalarsRoot);
        if (flagsRoot.Nodes.Count > 0) _tree.Nodes.Add(flagsRoot);
        if (tablesRoot.Nodes.Count > 0) _tree.Nodes.Add(tablesRoot);
        _tree.ExpandAll();
    }

    private void AddManualTable()
    {
        using var dlg = new ManualTableDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;

        if (dlg.Result.Offset < 0 || dlg.Result.Offset + dlg.Result.TotalByteLength > _data.Length)
        {
            MessageBox.Show(this, "Offset + ขนาดตาราง เกินขอบเขตไฟล์ .bin", "ผิดพลาด", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _xdf.Tables.Add(dlg.Result);
        RebuildTree();
    }

    private void SelectTable(TableDef? table)
    {
        _selected = table;
        _selectedFlag = null;
        _flagPanel.Visible = false;
        _grid.Visible = true;
        _lblTableInfo.Visible = true;

        if (table == null)
        {
            _lblTableInfo.Text = "";
            _lblAxisInfo.Text = "";
            _grid.Rows.Clear();
            _grid.Columns.Clear();
            _hex.SetHighlight(-1, 0);
            return;
        }

        _lblTableInfo.Text = $"{table.Name}    ตำแหน่ง=0x{table.Offset:X}    ขนาด {table.Rows}x{table.Cols}    {table.ElementSizeBits}-bit {(table.Signed ? "มีเครื่องหมาย" : "ไม่มีเครื่องหมาย")}    หน่วย={table.Unit}    สมการ: ค่าดิบ -> {table.MathEquation}";
        _lblAxisInfo.Text = DescribeAxes(table);
        _hex.SetHighlight(table.Offset, table.TotalByteLength);
        _hex.ScrollToOffset(table.Offset);
        RefreshGridFromData();
    }

    private void SelectFlag(FlagDef? flag)
    {
        _selectedFlag = flag;
        _selected = null;
        _grid.Visible = false;
        _lblTableInfo.Visible = false;
        _flagPanel.Visible = flag != null;
        if (flag == null) return;

        _hex.SetHighlight(flag.Offset, 1);
        _hex.ScrollToOffset(flag.Offset);
        RefreshFlagFromData();
    }

    private void RefreshFlagFromData()
    {
        if (_selectedFlag == null) return;
        var flag = _selectedFlag;

        _suppressFlagEvents = true;
        _chkFlag.Text = flag.Name;
        _chkFlag.Checked = BinFile.ReadFlag(_data, flag.Offset, flag.Mask);
        _lblFlagInfo.Text = $"ตำแหน่ง=0x{flag.Offset:X}    mask=0x{flag.Mask:X2}    หมวด={flag.Category}";
        _suppressFlagEvents = false;
    }

    private void ChkFlag_CheckedChanged(object? sender, EventArgs e)
    {
        if (_suppressFlagEvents || _selectedFlag == null) return;
        var flag = _selectedFlag;

        byte oldByte = _data[flag.Offset];
        BinFile.WriteFlag(_data, flag.Offset, flag.Mask, _chkFlag.Checked);
        byte newByte = _data[flag.Offset];

        var command = new EditCommand { Description = $"Flag: {flag.Name}" };
        command.Changes.Add(new CellChange { Address = flag.Offset, OldBytes = new[] { oldByte }, NewBytes = new[] { newByte } });
        _history.Record(command);

        _hex.Invalidate();
        UpdateUndoRedoButtons();
    }

    private void RefreshGridFromData()
    {
        if (_selected == null) return;
        var table = _selected;
        var math = new MathEquation(table.MathEquation);

        _suppressGridEvents = true;
        _grid.Columns.Clear();
        _grid.Rows.Clear();

        for (int c = 0; c < table.Cols; c++)
            _grid.Columns.Add($"col{c}", AxisHeader(table.XAxis, c));
        _grid.RowHeadersVisible = true;

        var values = new double[table.Rows, table.Cols];
        double min = double.MaxValue, max = double.MinValue;

        for (int r = 0; r < table.Rows; r++)
        {
            var rowValues = new string[table.Cols];
            for (int c = 0; c < table.Cols; c++)
            {
                int addr = ElementAddress(table, r, c);
                long raw = BinFile.ReadElement(_data, addr, table.ElementSizeBits, table.Signed, table.BigEndian);
                double physical = math.ToPhysical(raw);
                values[r, c] = physical;
                if (physical < min) min = physical;
                if (physical > max) max = physical;
                rowValues[c] = physical.ToString("F" + table.DecimalPlaces);
            }
            int rowIdx = _grid.Rows.Add(rowValues);
            _grid.Rows[rowIdx].HeaderCell.Value = AxisHeader(table.YAxis, r);
        }

        for (int r = 0; r < table.Rows; r++)
        {
            for (int c = 0; c < table.Cols; c++)
            {
                double norm = max > min ? (values[r, c] - min) / (max - min) : 0.5;
                var cell = _grid.Rows[r].Cells[c];
                cell.Style.BackColor = Theme.HeatColor(norm);
                cell.Style.ForeColor = norm > 0.65 ? Color.White : Color.Black;
            }
        }

        _suppressGridEvents = false;
    }

    /// <summary>RPM breakpoints are always whole numbers on real Honda ECUs — round the display so
    /// tiny raw/equation rounding noise (e.g. 9199.88) doesn't show up as fake precision (TunerPro shows 9200).
    /// TPS breakpoints are genuinely fractional (%), so those keep decimal formatting.</summary>
    private string AxisHeader(AxisDef? axis, int index)
    {
        double val = AxisValue(axis, index);
        bool isRpm = axis?.Label.Contains("RPM", StringComparison.OrdinalIgnoreCase) == true;
        return isRpm ? Math.Round(val).ToString("0") : val.ToString("0.##");
    }

    /// <summary>One-line hint above the grid stating which axis is which, e.g. "แถว = รอบเครื่องยนต์ RPM".</summary>
    private static string DescribeAxes(TableDef table)
    {
        if (table.XAxis == null && table.YAxis == null) return "";
        bool yIsRpm = table.YAxis?.Label.Contains("RPM", StringComparison.OrdinalIgnoreCase) == true;
        bool xIsTps = table.XAxis?.Label.Contains("TPS", StringComparison.OrdinalIgnoreCase) == true;

        string? rowPart = table.YAxis == null ? null : yIsRpm ? "แถว = รอบเครื่องยนต์ (RPM)" : $"แถว = {table.YAxis.Label}";
        string? colPart = table.XAxis == null ? null : xIsTps ? "คอลัมน์ = ค่าคันเร่ง (TPS %)" : $"คอลัมน์ = {table.XAxis.Label}";

        return string.Join("        ", new[] { rowPart, colPart }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>Real-world axis value (RPM/TPS/etc.) at a row/column index — used for headers and for
    /// weighting the Interpolate X/Y/XY functions by actual breakpoint position, not raw index.</summary>
    private double AxisValue(AxisDef? axis, int index)
    {
        if (axis == null) return index;
        if (axis.StaticValues != null && index < axis.StaticValues.Length)
            return axis.StaticValues[index];
        if (axis.EmbeddedAddress is int addr)
        {
            int bytesPer = axis.ElementSizeBits / 8;
            int elemAddr = addr + index * bytesPer;
            if (elemAddr + bytesPer <= _data.Length)
            {
                long raw = BinFile.ReadElement(_data, elemAddr, axis.ElementSizeBits, axis.Signed, axis.BigEndian);
                return new MathEquation(axis.MathEquation).ToPhysical(raw);
            }
        }
        return index;
    }

    private static int ElementAddress(TableDef table, int row, int col) =>
        table.Offset + (row * table.Cols + col) * table.BytesPerElement;

    private void Grid_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (_suppressGridEvents || _selected == null) return;
        var table = _selected;
        var cell = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex];

        if (!double.TryParse(cell.Value?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double physical))
        {
            MessageBox.Show(this, "ค่าที่ป้อนไม่ใช่ตัวเลข", "ผิดพลาด", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshGridFromData();
            return;
        }

        var math = new MathEquation(table.MathEquation);
        double rawD = Math.Round(math.ToRaw(physical));
        bool clamped = false;
        if (rawD < table.RawMin) { rawD = table.RawMin; clamped = true; }
        if (rawD > table.RawMax) { rawD = table.RawMax; clamped = true; }

        int addr = ElementAddress(table, e.RowIndex, e.ColumnIndex);
        ApplyRawWrite(table, addr, (long)rawD, "แก้ไขช่อง");

        if (clamped)
            MessageBox.Show(this, $"ค่าเกินขอบเขตของชนิดข้อมูล ({table.RawMin}..{table.RawMax}) — ปรับให้เป็นค่าขอบสุดแล้ว", "ค่าเกินขอบเขต", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        RefreshGridFromData();
        _hex.Invalidate();
        UpdateUndoRedoButtons();
    }

    /// <summary>Applies op(currentPhysical, value) to every selected cell in the current table.</summary>
    private void ApplyBatchOp(string title, double value, Func<double, double, double> op)
    {
        var table = _selected!;
        var math = new MathEquation(table.MathEquation);
        var command = new EditCommand { Description = title };
        bool anyClamped = false;

        var cells = _grid.SelectedCells.Cast<DataGridViewCell>().ToList();
        foreach (var cell in cells)
        {
            int addr = ElementAddress(table, cell.RowIndex, cell.ColumnIndex);
            long oldRaw = BinFile.ReadElement(_data, addr, table.ElementSizeBits, table.Signed, table.BigEndian);
            double physical = math.ToPhysical(oldRaw);
            double newPhysical = op(physical, value);
            WriteTableCell(table, math, addr, newPhysical, command, ref anyClamped);
        }

        FinishEdit(command, anyClamped);
    }

    private void ApplyRawWrite(TableDef table, int addr, long rawValue, string description)
    {
        int byteCount = table.BytesPerElement;
        var oldBytes = _data.Skip(addr).Take(byteCount).ToArray();
        BinFile.WriteElement(_data, addr, table.ElementSizeBits, table.BigEndian, rawValue);
        var newBytes = _data.Skip(addr).Take(byteCount).ToArray();

        var command = new EditCommand { Description = description };
        command.Changes.Add(new CellChange { Address = addr, OldBytes = oldBytes, NewBytes = newBytes });
        _history.Record(command);
    }

    /// <summary>Writes one table cell's physical value, clamping to the raw range and recording the byte change.</summary>
    private void WriteTableCell(TableDef table, MathEquation math, int addr, double newPhysical, EditCommand command, ref bool anyClamped)
    {
        double newRawD = Math.Round(math.ToRaw(newPhysical));
        if (newRawD < table.RawMin) { newRawD = table.RawMin; anyClamped = true; }
        if (newRawD > table.RawMax) { newRawD = table.RawMax; anyClamped = true; }

        int byteCount = table.BytesPerElement;
        var oldBytes = _data.Skip(addr).Take(byteCount).ToArray();
        BinFile.WriteElement(_data, addr, table.ElementSizeBits, table.BigEndian, (long)newRawD);
        var newBytes = _data.Skip(addr).Take(byteCount).ToArray();
        command.Changes.Add(new CellChange { Address = addr, OldBytes = oldBytes, NewBytes = newBytes });
    }

    private void FinishEdit(EditCommand command, bool anyClamped)
    {
        _history.Record(command);
        if (anyClamped)
            MessageBox.Show(this, "บางช่องมีค่าเกินขอบเขต ถูกปรับให้เป็นค่าขอบสุดแล้ว", "ค่าเกินขอบเขต", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        RefreshGridFromData();
        _hex.Invalidate();
        UpdateUndoRedoButtons();
    }

    /// <summary>Dispatches the toolbar's "ฟังก์ชัน" dropdown to the matching table operation, TunerPro-style.</summary>
    private void ExecuteFunction()
    {
        if (_selected == null || _grid.SelectedCells.Count == 0)
        {
            MessageBox.Show(this, "กรุณาเลือกช่องในตารางก่อน", "แจ้งเตือน", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string fn = _cmbFunction.SelectedItem as string ?? FnOffset;

        if (fn == FnCopyFromCompare) { CopyFromCompare(); return; }
        if (fn == FnSmooth) { SmoothSelection(); return; }
        if (fn == FnInterpolateX) { InterpolateSelection(alongX: true, alongY: false); return; }
        if (fn == FnInterpolateY) { InterpolateSelection(alongX: false, alongY: true); return; }
        if (fn == FnInterpolateXY) { InterpolateSelection(alongX: true, alongY: true); return; }

        if (!double.TryParse(_txtFunctionValue.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value))
        {
            MessageBox.Show(this, "กรุณาป้อนค่าตัวเลขในช่อง \"ค่า\"", "แจ้งเตือน", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (fn == FnOffset)
            ApplyBatchOp(fn, value, (phys, v) => phys + v);
        else if (fn == FnMultiply)
            ApplyBatchOp(fn, value, (phys, v) => phys * v);
        else if (fn == FnDivide)
        {
            if (value == 0)
            {
                MessageBox.Show(this, "หารด้วยศูนย์ไม่ได้", "แจ้งเตือน", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ApplyBatchOp(fn, value, (phys, v) => phys / v);
        }
        else if (fn == FnScaleByPercent)
            ApplyBatchOp(fn, value, (phys, v) => phys * (1.0 + v / 100.0));
        else if (fn == FnFillWithValue)
            ApplyBatchOp(fn, value, (_, v) => v);
    }

    /// <summary>Averages each selected cell with its up/down/left/right neighbors (read from a
    /// pre-op snapshot so smoothing one cell doesn't cascade into the next).</summary>
    private void SmoothSelection()
    {
        var table = _selected!;
        var math = new MathEquation(table.MathEquation);
        var cells = _grid.SelectedCells.Cast<DataGridViewCell>().ToList();
        if (cells.Count == 0) return;

        var selectedSet = cells.Select(c => (r: c.RowIndex, c: c.ColumnIndex)).Distinct().ToList();

        var snapshot = new double[table.Rows, table.Cols];
        for (int r = 0; r < table.Rows; r++)
            for (int c = 0; c < table.Cols; c++)
            {
                long raw = BinFile.ReadElement(_data, ElementAddress(table, r, c), table.ElementSizeBits, table.Signed, table.BigEndian);
                snapshot[r, c] = math.ToPhysical(raw);
            }

        var command = new EditCommand { Description = FnSmooth };
        bool anyClamped = false;

        foreach (var (r, c) in selectedSet)
        {
            var neighbors = new List<double> { snapshot[r, c] };
            if (r > 0) neighbors.Add(snapshot[r - 1, c]);
            if (r < table.Rows - 1) neighbors.Add(snapshot[r + 1, c]);
            if (c > 0) neighbors.Add(snapshot[r, c - 1]);
            if (c < table.Cols - 1) neighbors.Add(snapshot[r, c + 1]);

            WriteTableCell(table, math, ElementAddress(table, r, c), neighbors.Average(), command, ref anyClamped);
        }

        FinishEdit(command, anyClamped);
    }

    /// <summary>Linearly interpolates selected cells between their row/column (or bounding-box corner)
    /// endpoints, weighted by real axis value (RPM/TPS) rather than raw index — like TunerPro's Interpolate X/Y/XY.</summary>
    private void InterpolateSelection(bool alongX, bool alongY)
    {
        var table = _selected!;
        var math = new MathEquation(table.MathEquation);
        var cells = _grid.SelectedCells.Cast<DataGridViewCell>().ToList();
        if (cells.Count == 0) return;

        double PhysicalAt(int r, int c)
        {
            long raw = BinFile.ReadElement(_data, ElementAddress(table, r, c), table.ElementSizeBits, table.Signed, table.BigEndian);
            return math.ToPhysical(raw);
        }

        var command = new EditCommand { Description = alongX && alongY ? FnInterpolateXY : alongX ? FnInterpolateX : FnInterpolateY };
        bool anyClamped = false;

        if (alongX && alongY)
        {
            int minR = cells.Min(c => c.RowIndex), maxR = cells.Max(c => c.RowIndex);
            int minC = cells.Min(c => c.ColumnIndex), maxC = cells.Max(c => c.ColumnIndex);
            if (minR == maxR || minC == maxC)
            {
                MessageBox.Show(this, "Interpolate XY ต้องเลือกช่วงอย่างน้อย 2x2 ช่อง (มุมบน-ล่าง ซ้าย-ขวา)", "แจ้งเตือน", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            double v00 = PhysicalAt(minR, minC), v01 = PhysicalAt(minR, maxC);
            double v10 = PhysicalAt(maxR, minC), v11 = PhysicalAt(maxR, maxC);
            double x0 = AxisValue(table.XAxis, minC), x1 = AxisValue(table.XAxis, maxC);
            double y0 = AxisValue(table.YAxis, minR), y1 = AxisValue(table.YAxis, maxR);

            foreach (var cell in cells)
            {
                int r = cell.RowIndex, c = cell.ColumnIndex;
                if ((r == minR || r == maxR) && (c == minC || c == maxC)) continue; // corners are anchors, leave unchanged
                double tx = x1 != x0 ? (AxisValue(table.XAxis, c) - x0) / (x1 - x0) : 0;
                double ty = y1 != y0 ? (AxisValue(table.YAxis, r) - y0) / (y1 - y0) : 0;
                double top = v00 + (v01 - v00) * tx;
                double bottom = v10 + (v11 - v10) * tx;
                WriteTableCell(table, math, ElementAddress(table, r, c), top + (bottom - top) * ty, command, ref anyClamped);
            }
        }
        else if (alongX)
        {
            foreach (var rowGroup in cells.GroupBy(c => c.RowIndex))
            {
                var cols = rowGroup.Select(c => c.ColumnIndex).Distinct().OrderBy(x => x).ToList();
                if (cols.Count < 3) continue;
                int cMin = cols[0], cMax = cols[^1];
                double vMin = PhysicalAt(rowGroup.Key, cMin), vMax = PhysicalAt(rowGroup.Key, cMax);
                double xMin = AxisValue(table.XAxis, cMin), xMax = AxisValue(table.XAxis, cMax);
                foreach (int c in cols.Skip(1).Take(cols.Count - 2))
                {
                    double t = xMax != xMin ? (AxisValue(table.XAxis, c) - xMin) / (xMax - xMin) : 0;
                    WriteTableCell(table, math, ElementAddress(table, rowGroup.Key, c), vMin + (vMax - vMin) * t, command, ref anyClamped);
                }
            }
        }
        else if (alongY)
        {
            foreach (var colGroup in cells.GroupBy(c => c.ColumnIndex))
            {
                var rows = colGroup.Select(c => c.RowIndex).Distinct().OrderBy(x => x).ToList();
                if (rows.Count < 3) continue;
                int rMin = rows[0], rMax = rows[^1];
                double vMin = PhysicalAt(rMin, colGroup.Key), vMax = PhysicalAt(rMax, colGroup.Key);
                double yMin = AxisValue(table.YAxis, rMin), yMax = AxisValue(table.YAxis, rMax);
                foreach (int r in rows.Skip(1).Take(rows.Count - 2))
                {
                    double t = yMax != yMin ? (AxisValue(table.YAxis, r) - yMin) / (yMax - yMin) : 0;
                    WriteTableCell(table, math, ElementAddress(table, r, colGroup.Key), vMin + (vMax - vMin) * t, command, ref anyClamped);
                }
            }
        }

        if (command.Changes.Count == 0)
        {
            MessageBox.Show(this, "ต้องเลือกอย่างน้อย 3 ช่องต่อเนื่องกันในแถว/คอลัมน์เดียวกัน (หัว-ท้ายจะเป็นค่าอ้างอิง ตรงกลางจะถูกเชื่อมค่า)", "แจ้งเตือน", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        FinishEdit(command, anyClamped);
    }

    /// <summary>Copies raw bytes from the loaded compare .bin into every selected cell (same address).</summary>
    private void CopyFromCompare()
    {
        if (_compareData == null)
        {
            MessageBox.Show(this, "กรุณาโหลดไฟล์เปรียบเทียบก่อน (ปุ่ม \"โหลดไฟล์เปรียบเทียบ...\")", "แจ้งเตือน", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var table = _selected!;
        var cells = _grid.SelectedCells.Cast<DataGridViewCell>().ToList();
        var command = new EditCommand { Description = $"{FnCopyFromCompare} ({_compareLabel})" };
        int byteCount = table.BytesPerElement;

        foreach (var cell in cells)
        {
            int addr = ElementAddress(table, cell.RowIndex, cell.ColumnIndex);
            if (addr + byteCount > _compareData.Length) continue;

            var oldBytes = _data.Skip(addr).Take(byteCount).ToArray();
            var newBytes = _compareData.Skip(addr).Take(byteCount).ToArray();
            if (oldBytes.SequenceEqual(newBytes)) continue;

            Array.Copy(_compareData, addr, _data, addr, byteCount);
            command.Changes.Add(new CellChange { Address = addr, OldBytes = oldBytes, NewBytes = newBytes });
        }

        if (command.Changes.Count == 0) return;
        FinishEdit(command, anyClamped: false);
    }

    private void LoadCompareFile()
    {
        using var dlg = new OpenFileDialog { Filter = "ไฟล์ ECU BIN (*.bin)|*.bin|ไฟล์ทั้งหมด (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _compareData = BinFile.Load(dlg.FileName);
            _compareLabel = Path.GetFileName(dlg.FileName);
            _lblCompare.Text = $"ไฟล์เปรียบเทียบ: {_compareLabel} ({_compareData.Length:N0} bytes)";
            _lblCompare.ForeColor = Theme.Silver;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"เปิดไฟล์เปรียบเทียบไม่สำเร็จ:\n{ex.Message}", "ผิดพลาด", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshSelectionFromData()
    {
        if (_selectedFlag != null) RefreshFlagFromData();
        else if (_selected != null) RefreshGridFromData();
    }

    private void UpdateUndoRedoButtons()
    {
        _btnUndo.Enabled = _history.CanUndo;
        _btnRedo.Enabled = _history.CanRedo;
    }

    private void SaveAs()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "ไฟล์ ECU BIN (*.bin)|*.bin|ไฟล์ทั้งหมด (*.*)|*.*",
            FileName = Path.GetFileNameWithoutExtension(_binPath) + "_tuned.bin",
            InitialDirectory = Path.GetDirectoryName(_binPath),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            BinFile.SaveAs(dlg.FileName, _data, _binPath);

            var readBack = File.ReadAllBytes(dlg.FileName);
            bool roundTripOk = readBack.SequenceEqual(_data);
            var diffs = BinFile.DiffOffsets(_original, readBack);

            string msg = roundTripOk
                ? $"บันทึกไฟล์สำเร็จ: {dlg.FileName}\nแก้ไขทั้งหมด {diffs.Count} byte(s) เทียบกับไฟล์ต้นฉบับ\n\n" +
                  "คำเตือน: FKBtuner ยังไม่ได้คำนวณ checksum ใหม่ให้ไฟล์นี้เอง\n" +
                  "ห้าม flash ไฟล์นี้ตรงๆ เด็ดขาด — ให้เปิดไฟล์นี้ใน ARTTUNER แล้วใช้ปุ่ม \"เขียนไฟล์ (Write ECU)\" " +
                  "ซึ่งมีระบบ auto-checksum ในตัว เพื่อคำนวณ checksum ให้ถูกต้องก่อน flash เข้า ECU จริงทุกครั้ง"
                : "คำเตือน: อ่านไฟล์ที่บันทึกกลับมาแล้วไม่ตรงกับข้อมูลในโปรแกรม — กรุณาตรวจสอบไฟล์ก่อนใช้งาน";

            MessageBox.Show(this, msg, "บันทึกไฟล์", MessageBoxButtons.OK,
                roundTripOk ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"บันทึกไฟล์ไม่สำเร็จ:\n{ex.Message}", "ผิดพลาด", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

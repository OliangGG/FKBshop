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
    private readonly Label _lblTableInfo;
    private readonly Button _btnUndo;
    private readonly Button _btnRedo;

    private TableDef? _selected;
    private bool _suppressGridEvents;

    public EditorForm(byte[] binData, string binPath, ParsedXdf xdf)
    {
        _data = (byte[])binData.Clone();
        _original = (byte[])binData.Clone();
        _binPath = binPath;
        _xdf = xdf;

        Text = $"BinTuner Editor — {Path.GetFileName(binPath)}";
        Theme.Apply(this);
        WindowState = FormWindowState.Maximized;

        var toolbar = BuildToolbar();

        _tree = new TreeView
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ForeColor = Theme.Silver,
            BorderStyle = BorderStyle.None,
            HideSelection = false,
        };
        _tree.AfterSelect += (_, e) => SelectTable(e.Node?.Tag as TableDef);

        var treePanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(4) };
        var treeHeader = new Label { Text = "Parameter Tree", Dock = DockStyle.Top, ForeColor = Theme.Accent, Font = new Font(Theme.UiFont, FontStyle.Bold), Height = 24, TextAlign = ContentAlignment.MiddleLeft };
        treePanel.Controls.Add(_tree);
        treePanel.Controls.Add(treeHeader);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Theme.Background,
            GridColor = Theme.GridLine,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.AutoSizeToAllHeaders,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
        };
        _grid.DefaultCellStyle.BackColor = Theme.Background;
        _grid.DefaultCellStyle.ForeColor = Color.Black;
        _grid.DefaultCellStyle.SelectionBackColor = Theme.Accent;
        _grid.DefaultCellStyle.SelectionForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Panel;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Silver;
        _grid.RowHeadersDefaultCellStyle.BackColor = Theme.Panel;
        _grid.RowHeadersDefaultCellStyle.ForeColor = Theme.Silver;
        _grid.EnableHeadersVisualStyles = false;
        _grid.CellEndEdit += Grid_CellEndEdit;

        _lblTableInfo = new Label { Dock = DockStyle.Top, Height = 26, ForeColor = Theme.TextMuted, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };

        var gridPanel = new Panel { Dock = DockStyle.Fill };
        gridPanel.Controls.Add(_grid);
        gridPanel.Controls.Add(_lblTableInfo);

        _hex = new HexViewerControl { Dock = DockStyle.Fill, Data = _data };

        var rightSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 420 };
        rightSplit.Panel1.Controls.Add(gridPanel);
        rightSplit.Panel2.Controls.Add(_hex);

        var mainSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 240 };
        mainSplit.Panel1.Controls.Add(treePanel);
        mainSplit.Panel2.Controls.Add(rightSplit);

        Controls.Add(mainSplit);
        Controls.Add(toolbar);

        _btnUndo = (Button)toolbar.Controls["btnUndo"]!;
        _btnRedo = (Button)toolbar.Controls["btnRedo"]!;
        UpdateUndoRedoButtons();

        RebuildTree();
    }

    private Panel BuildToolbar()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Theme.Panel };

        var btnSaveAs = Theme.StyledButton("Save As...");
        btnSaveAs.Name = "btnSaveAs";
        btnSaveAs.Location = new Point(8, 7);
        btnSaveAs.Click += (_, _) => SaveAs();

        var btnUndo = Theme.StyledButton("Undo");
        btnUndo.Name = "btnUndo";
        btnUndo.Location = new Point(140, 7);
        btnUndo.Click += (_, _) => { _history.Undo(_data); UpdateUndoRedoButtons(); RefreshGridFromData(); _hex.Invalidate(); };

        var btnRedo = Theme.StyledButton("Redo");
        btnRedo.Name = "btnRedo";
        btnRedo.Location = new Point(220, 7);
        btnRedo.Click += (_, _) => { _history.Redo(_data); UpdateUndoRedoButtons(); RefreshGridFromData(); _hex.Invalidate(); };

        var btnOffset = Theme.StyledButton("Offset +/-");
        btnOffset.Location = new Point(310, 7);
        btnOffset.Click += (_, _) => ApplyBatchOp("Offset", (phys, k) => phys + k);

        var btnScale = Theme.StyledButton("Scale x/");
        btnScale.Location = new Point(410, 7);
        btnScale.Click += (_, _) => ApplyBatchOp("Scale (คูณ)", (phys, k) => phys * k);

        var btnPercent = Theme.StyledButton("Percentage %");
        btnPercent.Location = new Point(510, 7);
        btnPercent.Click += (_, _) => ApplyBatchOp("Percentage (เช่น 5 = +5%, -10 = -10%)", (phys, k) => phys * (1 + k / 100.0));

        var btnManual = Theme.StyledButton("เพิ่มตารางเอง...");
        btnManual.Location = new Point(650, 7);
        btnManual.Click += (_, _) => AddManualTable();

        bar.Controls.AddRange(new Control[] { btnSaveAs, btnUndo, btnRedo, btnOffset, btnScale, btnPercent, btnManual });
        return bar;
    }

    private void RebuildTree()
    {
        _tree.Nodes.Clear();
        var scalarsRoot = new TreeNode("Scalars");
        var tablesRoot = new TreeNode("Tables");

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

        if (scalarsRoot.Nodes.Count > 0) _tree.Nodes.Add(scalarsRoot);
        if (tablesRoot.Nodes.Count > 0) _tree.Nodes.Add(tablesRoot);
        _tree.ExpandAll();
    }

    private void AddManualTable()
    {
        using var dlg = new ManualTableDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;

        if (dlg.Result.Offset < 0 || dlg.Result.Offset + dlg.Result.TotalByteLength > _data.Length)
        {
            MessageBox.Show(this, "Offset + ขนาดตาราง เกินขอบเขตไฟล์ .bin", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _xdf.Tables.Add(dlg.Result);
        RebuildTree();
    }

    private void SelectTable(TableDef? table)
    {
        _selected = table;
        if (table == null)
        {
            _lblTableInfo.Text = "";
            _grid.Rows.Clear();
            _grid.Columns.Clear();
            _hex.SetHighlight(-1, 0);
            return;
        }

        _lblTableInfo.Text = $"{table.Name}   offset=0x{table.Offset:X}   {table.Rows}x{table.Cols}   {table.ElementSizeBits}-bit {(table.Signed ? "signed" : "unsigned")}   unit={table.Unit}   eq: raw -> {table.MathEquation}";
        _hex.SetHighlight(table.Offset, table.TotalByteLength);
        _hex.ScrollToOffset(table.Offset);
        RefreshGridFromData();
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

    private string AxisHeader(AxisDef? axis, int index)
    {
        if (axis == null) return index.ToString();
        if (axis.StaticValues != null && index < axis.StaticValues.Length)
            return axis.StaticValues[index].ToString("0.##");
        if (axis.EmbeddedAddress is int addr)
        {
            int bytesPer = axis.ElementSizeBits / 8;
            int elemAddr = addr + index * bytesPer;
            if (elemAddr + bytesPer <= _data.Length)
            {
                long raw = BinFile.ReadElement(_data, elemAddr, axis.ElementSizeBits, axis.Signed, axis.BigEndian);
                double physical = new MathEquation(axis.MathEquation).ToPhysical(raw);
                return physical.ToString("0.##");
            }
        }
        return index.ToString();
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
            MessageBox.Show(this, "ค่าที่ป้อนไม่ใช่ตัวเลข", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshGridFromData();
            return;
        }

        var math = new MathEquation(table.MathEquation);
        double rawD = Math.Round(math.ToRaw(physical));
        bool clamped = false;
        if (rawD < table.RawMin) { rawD = table.RawMin; clamped = true; }
        if (rawD > table.RawMax) { rawD = table.RawMax; clamped = true; }

        int addr = ElementAddress(table, e.RowIndex, e.ColumnIndex);
        ApplyRawWrite(table, addr, (long)rawD, "Edit cell");

        if (clamped)
            MessageBox.Show(this, $"ค่าเกินขอบเขตของชนิดข้อมูล ({table.RawMin}..{table.RawMax}) — ปรับให้เป็นค่าขอบสุดแล้ว", "Clamped", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        RefreshGridFromData();
        _hex.Invalidate();
        UpdateUndoRedoButtons();
    }

    private void ApplyBatchOp(string title, Func<double, double, double> op)
    {
        if (_selected == null || _grid.SelectedCells.Count == 0)
        {
            MessageBox.Show(this, "กรุณาเลือกช่องในตารางก่อน", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new NumericPromptDialog(title, "ป้อนค่า:");
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var table = _selected;
        var math = new MathEquation(table.MathEquation);
        var command = new EditCommand { Description = title };
        bool anyClamped = false;

        var cells = _grid.SelectedCells.Cast<DataGridViewCell>().ToList();
        foreach (var cell in cells)
        {
            int addr = ElementAddress(table, cell.RowIndex, cell.ColumnIndex);
            long oldRaw = BinFile.ReadElement(_data, addr, table.ElementSizeBits, table.Signed, table.BigEndian);
            double physical = math.ToPhysical(oldRaw);
            double newPhysical = op(physical, dlg.Value);
            double newRawD = Math.Round(math.ToRaw(newPhysical));
            if (newRawD < table.RawMin) { newRawD = table.RawMin; anyClamped = true; }
            if (newRawD > table.RawMax) { newRawD = table.RawMax; anyClamped = true; }

            int byteCount = table.BytesPerElement;
            var oldBytes = _data.Skip(addr).Take(byteCount).ToArray();
            BinFile.WriteElement(_data, addr, table.ElementSizeBits, table.BigEndian, (long)newRawD);
            var newBytes = _data.Skip(addr).Take(byteCount).ToArray();
            command.Changes.Add(new CellChange { Address = addr, OldBytes = oldBytes, NewBytes = newBytes });
        }

        _history.Record(command);
        if (anyClamped)
            MessageBox.Show(this, "บางช่องมีค่าเกินขอบเขต ถูกปรับให้เป็นค่าขอบสุดแล้ว", "Clamped", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        RefreshGridFromData();
        _hex.Invalidate();
        UpdateUndoRedoButtons();
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

    private void UpdateUndoRedoButtons()
    {
        _btnUndo.Enabled = _history.CanUndo;
        _btnRedo.Enabled = _history.CanRedo;
    }

    private void SaveAs()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "ECU BIN files (*.bin)|*.bin|All files (*.*)|*.*",
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
                  "คำเตือน: ไฟล์นี้ยังไม่มีการคำนวณ checksum ใหม่\n\"ห้ามนำไป flash เข้า ECU\" จนกว่าจะทำ Phase 3 (checksum) เสร็จ"
                : "คำเตือน: อ่านไฟล์ที่บันทึกกลับมาแล้วไม่ตรงกับข้อมูลในโปรแกรม — กรุณาตรวจสอบไฟล์ก่อนใช้งาน";

            MessageBox.Show(this, msg, "Save As", MessageBoxButtons.OK,
                roundTripOk ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"บันทึกไฟล์ไม่สำเร็จ:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

namespace BinTuner.Models;

public enum ParamKind
{
    Scalar,
    Table
}

public class AxisDef
{
    public string Label = "";
    public int Count;
    public double[]? StaticValues;
    public int? EmbeddedAddress;
    public int ElementSizeBits = 8;
    public bool Signed;
    public bool BigEndian;
    public string MathEquation = "X";
    public string Unit = "";
}

public class TableDef
{
    public string Name = "";
    public string Category = "Uncategorized";
    public int Offset;
    public int Rows = 1;
    public int Cols = 1;
    public int ElementSizeBits = 8;
    public bool Signed;
    public bool BigEndian;
    public string MathEquation = "X";
    public string Unit = "";
    public int DecimalPlaces = 2;
    public ParamKind Kind = ParamKind.Table;
    public AxisDef? XAxis;
    public AxisDef? YAxis;

    public int BytesPerElement => ElementSizeBits / 8;
    public int TotalElements => Rows * Cols;
    public int TotalByteLength => TotalElements * BytesPerElement;

    public double RawMin => Signed ? -(1 << (ElementSizeBits - 1)) : 0;
    public double RawMax => Signed ? (1 << (ElementSizeBits - 1)) - 1 : (1 << ElementSizeBits) - 1;
}

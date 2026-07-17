using System.Globalization;
using System.Xml.Linq;
using BinTuner.Models;

namespace BinTuner.Xdf;

/// <summary>
/// Parses a subset of the TunerPro .xdf format: header/categories, XDFTABLE (2D/1D
/// tables) and XDFCONSTANT (scalars), embedded-data address/size/sign, and linear
/// MATH equations. XDF is a large, loosely-documented format with many optional/rare
/// elements (axis linking, static LABEL axes, float data, etc.); this covers what
/// real-world 8/16-bit fuel and ignition tables use in practice, not the full spec.
/// </summary>
public static class XdfParser
{
    public static ParsedXdf Parse(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root ?? throw new FormatException("XDF file has no root element");
        if (root.Name.LocalName != "XDFFORMAT")
            throw new FormatException("Not a recognized XDF file (missing <XDFFORMAT> root)");

        var result = new ParsedXdf();
        var categories = new Dictionary<int, string>();

        var header = root.Element("XDFHEADER");
        if (header != null)
        {
            result.EcuId = header.Element("deftitle")?.Value.Trim() ?? "";
            var baseOffsetEl = header.Element("baseoffset");
            if (baseOffsetEl != null)
                result.BaseOffset = ParseIntFlexible(baseOffsetEl.Attribute("offset")?.Value ?? "0");

            foreach (var cat in header.Elements("CATEGORY"))
            {
                int idx = ParseIntFlexible(cat.Attribute("index")?.Value ?? "0");
                string name = cat.Attribute("name")?.Value ?? $"Category {idx}";
                categories[idx] = name;
                result.Categories.Add(name);
            }
        }

        foreach (var tableEl in root.Elements("XDFTABLE"))
        {
            var table = ParseTable(tableEl, categories);
            if (table != null)
                result.Tables.Add(table);
        }

        foreach (var constEl in root.Elements("XDFCONSTANT"))
        {
            var scalar = ParseConstant(constEl, categories);
            if (scalar != null)
                result.Tables.Add(scalar);
        }

        return result;
    }

    private static TableDef? ParseTable(XElement tableEl, Dictionary<int, string> categories)
    {
        var axes = tableEl.Elements("XDFAXIS").ToList();
        var zEl = axes.FirstOrDefault(a => a.Attribute("id")?.Value.Equals("z", StringComparison.OrdinalIgnoreCase) == true);
        if (zEl == null)
            return null; // malformed table, no data axis

        var xEl = axes.FirstOrDefault(a => a.Attribute("id")?.Value.Equals("x", StringComparison.OrdinalIgnoreCase) == true);
        var yEl = axes.FirstOrDefault(a => a.Attribute("id")?.Value.Equals("y", StringComparison.OrdinalIgnoreCase) == true);

        var (address, sizeBits, signed, bigEndian) = ParseEmbeddedData(zEl);
        if (address == null)
            return null; // no in-file location, nothing to display/edit

        int zCount = ParseIntFlexible(zEl.Element("indexcount")?.Value ?? "0");
        int cols = xEl != null ? ParseIntFlexible(xEl.Element("indexcount")?.Value ?? "0") : zCount;
        int rows = yEl != null ? ParseIntFlexible(yEl.Element("indexcount")?.Value ?? "0") : 1;
        if (cols <= 0) cols = Math.Max(zCount, 1);
        if (rows <= 0) rows = 1;
        if (rows * cols != zCount && zCount > 0 && rows > 0 && zCount % rows == 0)
            cols = zCount / rows;

        var table = new TableDef
        {
            Name = tableEl.Element("title")?.Value.Trim() ?? "(unnamed table)",
            Category = ResolveCategory(tableEl, categories),
            Offset = address.Value,
            Rows = rows,
            Cols = cols,
            ElementSizeBits = sizeBits,
            Signed = signed,
            BigEndian = bigEndian,
            MathEquation = ParseMathEquation(zEl),
            Unit = zEl.Element("units")?.Value.Trim() ?? "",
            DecimalPlaces = ParseIntFlexible(zEl.Element("decimalpl")?.Value ?? "2"),
            Kind = ParamKind.Table,
            XAxis = xEl != null ? ParseAxis(xEl, "TPS/Column") : null,
            YAxis = yEl != null ? ParseAxis(yEl, "RPM/Row") : null,
        };
        return table;
    }

    private static TableDef? ParseConstant(XElement constEl, Dictionary<int, string> categories)
    {
        var (address, sizeBits, signed, bigEndian) = ParseEmbeddedData(constEl);
        if (address == null)
            return null;

        return new TableDef
        {
            Name = constEl.Element("title")?.Value.Trim() ?? "(unnamed scalar)",
            Category = ResolveCategory(constEl, categories),
            Offset = address.Value,
            Rows = 1,
            Cols = 1,
            ElementSizeBits = sizeBits,
            Signed = signed,
            BigEndian = bigEndian,
            MathEquation = ParseMathEquation(constEl),
            Unit = constEl.Element("units")?.Value.Trim() ?? "",
            DecimalPlaces = ParseIntFlexible(constEl.Element("decimalpl")?.Value ?? "2"),
            Kind = ParamKind.Scalar,
        };
    }

    private static AxisDef ParseAxis(XElement axisEl, string defaultLabel)
    {
        var (address, sizeBits, signed, bigEndian) = ParseEmbeddedData(axisEl);
        int count = ParseIntFlexible(axisEl.Element("indexcount")?.Value ?? "0");

        var labels = axisEl.Elements("LABEL").ToList();
        double[]? staticValues = null;
        if (labels.Count > 0)
        {
            staticValues = new double[labels.Count];
            foreach (var lbl in labels)
            {
                int idx = ParseIntFlexible(lbl.Attribute("index")?.Value ?? "0");
                double val = double.TryParse(lbl.Attribute("value")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
                if (idx >= 0 && idx < staticValues.Length)
                    staticValues[idx] = val;
            }
        }

        return new AxisDef
        {
            Label = defaultLabel,
            Count = count,
            StaticValues = staticValues,
            EmbeddedAddress = address,
            ElementSizeBits = sizeBits,
            Signed = signed,
            BigEndian = bigEndian,
            MathEquation = ParseMathEquation(axisEl),
            Unit = axisEl.Element("units")?.Value.Trim() ?? "",
        };
    }

    private static string ResolveCategory(XElement parent, Dictionary<int, string> categories)
    {
        var mem = parent.Element("CATEGORYMEM");
        if (mem == null) return "Uncategorized";
        int idx = ParseIntFlexible(mem.Attribute("category")?.Value ?? "-1");
        return categories.TryGetValue(idx, out var name) ? name : "Uncategorized";
    }

    private static string ParseMathEquation(XElement parent)
    {
        var math = parent.Element("MATH");
        return math?.Attribute("equation")?.Value.Trim() ?? "X";
    }

    /// <returns>(address, elementSizeBits, signed, bigEndian) or (null, 8, false, false) if no EMBEDDEDDATA present.</returns>
    private static (int? address, int sizeBits, bool signed, bool bigEndian) ParseEmbeddedData(XElement parent)
    {
        var el = parent.Element("EMBEDDEDDATA");
        if (el == null)
            return (null, 8, false, false);

        int address = ParseIntFlexible(el.Attribute("mmedaddress")?.Value ?? "0");
        int sizeBits = ParseIntFlexible(el.Attribute("mmedelementsizebits")?.Value ?? "8");
        int typeFlags = ParseIntFlexible(el.Attribute("mmedTypeFlags")?.Value ?? "0");
        bool signed = (typeFlags & 0x01) != 0;
        bool bigEndian = (typeFlags & 0x02) != 0;
        return (address, sizeBits <= 0 ? 8 : sizeBits, signed, bigEndian);
    }

    private static int ParseIntFlexible(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Convert.ToInt32(s[2..], 16);
        if (s.StartsWith("-0x", StringComparison.OrdinalIgnoreCase))
            return -Convert.ToInt32(s[3..], 16);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}

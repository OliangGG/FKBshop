using System.Text.Json;
using BinTuner.Models;

namespace BinTuner.Presets;

/// <summary>
/// Loads hand-verified table definitions from a small JSON file (see
/// Presets/HondaK3MHT71_0106A70D01.json) — the lightweight version of the
/// Phase-4 "table definition system" from the spec. Only tables that were
/// actually confirmed against TunerPro's Table Properties dialog and the raw
/// .bin bytes belong in these files.
/// </summary>
public static class PresetLoader
{
    public static ParsedXdf Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var result = new ParsedXdf
        {
            EcuId = root.TryGetProperty("ecuId", out var ecuIdEl) ? ecuIdEl.GetString() ?? "" : "",
            PartNumber = root.TryGetProperty("partNumber", out var partEl) ? partEl.GetString() ?? "" : "",
        };

        if (!root.TryGetProperty("tables", out var tablesEl))
            return result;

        foreach (var t in tablesEl.EnumerateArray())
        {
            var table = new TableDef
            {
                Name = GetString(t, "name", "(unnamed)"),
                Category = GetString(t, "category", "Preset"),
                Offset = ParseOffset(GetString(t, "offset", "0x0")),
                Rows = GetInt(t, "rows", 1),
                Cols = GetInt(t, "cols", 1),
                ElementSizeBits = GetInt(t, "elementSizeBits", 8),
                Signed = GetBool(t, "signed", false),
                BigEndian = GetBool(t, "bigEndian", false),
                MathEquation = GetString(t, "mathEquation", "X"),
                Unit = GetString(t, "unit", ""),
                DecimalPlaces = GetInt(t, "decimalPlaces", 2),
                Kind = ParamKind.Table,
            };
            result.Tables.Add(table);
        }

        return result;
    }

    private static string GetString(JsonElement el, string prop, string fallback) =>
        el.TryGetProperty(prop, out var v) ? v.GetString() ?? fallback : fallback;

    private static int GetInt(JsonElement el, string prop, int fallback) =>
        el.TryGetProperty(prop, out var v) ? v.GetInt32() : fallback;

    private static bool GetBool(JsonElement el, string prop, bool fallback) =>
        el.TryGetProperty(prop, out var v) ? v.GetBoolean() : fallback;

    private static int ParseOffset(string s)
    {
        s = s.Trim();
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt32(s[2..], 16)
            : int.Parse(s);
    }
}

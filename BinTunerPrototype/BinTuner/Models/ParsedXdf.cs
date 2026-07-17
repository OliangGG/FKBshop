namespace BinTuner.Models;

public class ParsedXdf
{
    public string EcuId = "";
    public string PartNumber = "";
    public int BaseOffset;
    public List<string> Categories = new();
    public List<TableDef> Tables = new();
}

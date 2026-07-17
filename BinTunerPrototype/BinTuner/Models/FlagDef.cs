namespace BinTuner.Models;

/// <summary>A single-byte bitfield flag (TunerPro's XDFFLAG) — on when all masked bits are set.</summary>
public class FlagDef
{
    public string Name = "";
    public string Category = "Flag";
    public int Offset;
    public int Mask = 0x01;
}

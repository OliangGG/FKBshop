namespace BinTuner.BinIO;

/// <summary>
/// Loads/saves raw .bin dumps. Never touches the file the user originally opened:
/// loading creates a ".original" backup once, and Save As always targets a new path.
/// </summary>
public static class BinFile
{
    public static byte[] Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);

        string backupPath = path + ".original";
        if (!File.Exists(backupPath))
            File.Copy(path, backupPath);

        return data;
    }

    /// <summary>
    /// Writes <paramref name="data"/> to <paramref name="newPath"/>. Refuses to target
    /// the original source file. If a file already sits at newPath (re-saving over a
    /// previous Save As), that prior version is preserved with a timestamp suffix first.
    /// </summary>
    public static void SaveAs(string newPath, byte[] data, string originalLoadedPath)
    {
        if (string.Equals(Path.GetFullPath(newPath), Path.GetFullPath(originalLoadedPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Save As cannot target the original file that was opened.");

        if (File.Exists(newPath))
        {
            string backupPath = $"{newPath}.bak_{DateTime.Now:yyyyMMdd_HHmmss}";
            File.Copy(newPath, backupPath, overwrite: false);
        }

        File.WriteAllBytes(newPath, data);
    }

    /// <summary>Byte offsets where two equal-length buffers differ.</summary>
    public static List<int> DiffOffsets(byte[] a, byte[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        var diffs = new List<int>();
        for (int i = 0; i < len; i++)
            if (a[i] != b[i])
                diffs.Add(i);
        return diffs;
    }

    public static long ReadElement(byte[] data, int address, int sizeBits, bool signed, bool bigEndian)
    {
        int byteCount = sizeBits / 8;
        if (address < 0 || address + byteCount > data.Length)
            throw new ArgumentOutOfRangeException(nameof(address), "Element falls outside the file.");

        ulong raw = 0;
        for (int i = 0; i < byteCount; i++)
        {
            byte b = data[address + i];
            int shift = bigEndian ? (byteCount - 1 - i) * 8 : i * 8;
            raw |= (ulong)b << shift;
        }

        if (!signed)
            return (long)raw;

        long signBit = 1L << (sizeBits - 1);
        long mask = (1L << sizeBits) - 1;
        long value = (long)raw & mask;
        return (value & signBit) != 0 ? value - (mask + 1) : value;
    }

    public static void WriteElement(byte[] data, int address, int sizeBits, bool bigEndian, long value)
    {
        int byteCount = sizeBits / 8;
        if (address < 0 || address + byteCount > data.Length)
            throw new ArgumentOutOfRangeException(nameof(address), "Element falls outside the file.");

        ulong raw = unchecked((ulong)value) & ((sizeBits >= 64) ? ulong.MaxValue : (1UL << sizeBits) - 1);
        for (int i = 0; i < byteCount; i++)
        {
            int shift = bigEndian ? (byteCount - 1 - i) * 8 : i * 8;
            data[address + i] = (byte)((raw >> shift) & 0xFF);
        }
    }
}

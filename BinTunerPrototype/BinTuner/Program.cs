using BinTuner.BinIO;
using BinTuner.Models;
using BinTuner.Presets;
using BinTuner.UI;
using BinTuner.Xdf;

namespace BinTuner;

internal static class Program
{
    /// <summary>
    /// Command-line launch contract — for opening FKBtuner directly from another program (e.g.
    /// ARTTUNER) instead of showing the file-picker screen first:
    ///
    ///   FKBtuner.exe "C:\path\to\file.bin" ["C:\path\to\definition.xdf" | "C:\path\to\preset.json"]
    ///
    /// arg 0 (required) = path to the .bin ECU dump to open
    /// arg 1 (optional) = path to a .xdf definition or a FKBtuner .json preset — if omitted, the
    ///                    editor opens with no known tables (still usable via "เพิ่มตารางเอง...")
    ///
    /// Launched this way, the file-picker screen (MainForm) is skipped entirely and the table
    /// editor opens directly with the given file(s). Launching with no arguments (e.g. double-
    /// clicking the .exe) behaves exactly as before — MainForm is shown.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length >= 1)
        {
            RunDirectToEditor(args);
            return;
        }

        Application.Run(new MainForm());
    }

    private static void RunDirectToEditor(string[] args)
    {
        string binPath = args[0];
        byte[] binData;
        try
        {
            binData = BinFile.Load(binPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"เปิดไฟล์ BIN ไม่สำเร็จ:\n{binPath}\n\n{ex.Message}",
                "FKBtuner — ผิดพลาด", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var xdf = new ParsedXdf();
        if (args.Length >= 2)
        {
            string defPath = args[1];
            try
            {
                if (defPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var preset = PresetLoader.Load(defPath);
                    xdf.EcuId = preset.EcuId;
                    xdf.PartNumber = preset.PartNumber;
                    xdf.Tables.AddRange(preset.Tables);
                }
                else
                {
                    xdf = XdfParser.Parse(defPath);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"อ่านไฟล์กำหนดตาราง ({defPath}) ไม่สำเร็จ:\n{ex.Message}\n\nจะเปิดตัวแก้ไขต่อโดยไม่มีตารางที่รู้จัก",
                    "FKBtuner — คำเตือน", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        Application.Run(new EditorForm(binData, binPath, xdf));
    }
}

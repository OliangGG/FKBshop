using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BinTuner.Afr
{
    /// <summary>
    /// เครื่องมือหลักสำหรับสร้างตาราง AFR เทียบกับ RPM x TPS จากข้อมูลเรียลไทม์
    ///
    /// วิธีใช้งาน:
    ///  1. ทุกครั้งที่ได้ค่า RPM/TPS ใหม่จาก ECU -> เรียก AddEngineSample()
    ///  2. ทุกครั้งที่ได้ค่า AFR/O2 ใหม่จาก ECU -> เรียก AddAfrSample()
    ///  (ทั้งสองอย่างอาจมาไม่พร้อมกัน หรือคนละความถี่กันก็ได้ ตัว logger จัดการ time-align ให้เอง)
    ///
    /// เหตุผลที่แยก RPM/TPS กับ AFR ออกจากกัน:
    /// เซนเซอร์ O2 มี "transport lag" คือค่าที่วัดได้จะสะท้อนสภาพไอเสียที่เผาไหม้ไปแล้ว
    /// ก่อนหน้านั้นเล็กน้อย (ปกติ ~100-300ms) ถ้าจับคู่ AFR กับ RPM/TPS ณ เวลาเดียวกันตรงๆ
    /// ตอนเร่ง/ผ่อนคันเร่งกะทันหันข้อมูลจะเพี้ยนไปอยู่ผิด cell
    /// </summary>
    public class AfrLogger
    {
        // ---- ค่าตั้งต้นที่ปรับได้ ----

        /// <summary>ขนาดแต่ละช่อง RPM ต่อ 1 แถวของตาราง (รอบ/นาที)</summary>
        public double RpmBinSize { get; set; } = 500;

        /// <summary>
        /// จุดแบ่งช่วง TPS (หน่วยองศา) ตรงกับแกน X ที่ใช้จริงในตาราง tune ของ Honda
        /// (ก็อปมาจากตาราง TPS breakpoint ที่เห็นใน TunerPro ตรงๆ) ไม่ใช่แบ่งเท่าๆ กันแบบเดิม
        /// </summary>
        public static readonly double[] TpsBreakpoints =
        {
            0.0, 0.4, 0.6, 1.0, 1.5, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0,
            10.0, 12.0, 14.0, 16.0, 18.0, 20.0, 25.0, 30.0, 34.9, 40.0,
            45.0, 50.0, 54.9, 59.9, 65.0, 71.0, 72.5
        };

        /// <summary>หาตำแหน่ง bin ของค่า TPS (องศา) จาก breakpoint ด้านบน (ปัดลงหาช่วงที่ตรงที่สุด)</summary>
        public static int GetTpsBinIndex(double tpsDegrees)
        {
            int idx = 0;
            for (int i = 0; i < TpsBreakpoints.Length; i++)
            {
                if (TpsBreakpoints[i] <= tpsDegrees) idx = i;
                else break;
            }
            return idx;
        }

        /// <summary>ระยะเวลาหน่วงของค่า AFR เทียบกับ RPM/TPS จริง (มิลลิวินาที) — ปรับจูนตามการทดสอบจริง</summary>
        public int AfrDelayMs { get; set; } = 200;

        /// <summary>ถ้า RPM เปลี่ยนแปลงเกินนี้ภายในหน้าต่างเวลาตรวจสอบ ถือว่าเป็นช่วง transient ไม่เอาเข้าตาราง</summary>
        public double RpmStableThreshold { get; set; } = 300;

        /// <summary>ถ้า TPS เปลี่ยนแปลงเกินนี้ภายในหน้าต่างเวลาตรวจสอบ ถือว่าเป็นช่วง transient ไม่เอาเข้าตาราง</summary>
        public double TpsStableThreshold { get; set; } = 5;

        /// <summary>หน้าต่างเวลาที่ใช้ตรวจสอบความนิ่งของ RPM/TPS รอบๆ จุดที่จะบันทึก</summary>
        public TimeSpan StabilityWindow { get; set; } = TimeSpan.FromMilliseconds(150);

        /// <summary>ถ้าหาค่า RPM/TPS ที่ align เวลาไม่เจอภายในระยะนี้ ให้ทิ้ง sample AFR นั้นไปเลย (กันข้อมูลค้าง/เก่าเกินไป)</summary>
        public double MaxAlignmentGapMs { get; set; } = 300;

        // ---- ที่เก็บข้อมูล ----

        private readonly List<EngineSample> _buffer = new();
        private readonly Dictionary<(int rpmBin, int tpsBin), AfrCell> _table = new();

        public IReadOnlyDictionary<(int rpmBin, int tpsBin), AfrCell> Table => _table;

        /// <summary>จำนวน sample ทั้งหมดที่ถูกทิ้งเพราะช่วง transient (ไว้ debug/แสดงสถิติ)</summary>
        public long DiscardedTransientCount { get; private set; } = 0;

        /// <summary>จำนวน sample ทั้งหมดที่ถูกทิ้งเพราะ align เวลาไม่ได้</summary>
        public long DiscardedNoAlignCount { get; private set; } = 0;

        public long AcceptedCount { get; private set; } = 0;

        /// <summary>
        /// ป้อนค่า RPM/TPS ล่าสุดเข้า buffer สำหรับใช้ align เวลากับ AFR ทีหลัง
        /// เรียกทุกครั้งที่ได้ข้อมูลใหม่จาก ECU (ควรเรียกถี่กว่าหรือเท่ากับความถี่ AFR)
        /// </summary>
        public void AddEngineSample(DateTime timestamp, double rpm, double tps)
        {
            _buffer.Add(new EngineSample(timestamp, rpm, tps));

            // ตัด buffer ส่วนที่เก่าเกินความจำเป็นทิ้ง กันหน่วยความจำบวมตอนรันนานๆ
            var cutoff = timestamp - TimeSpan.FromMilliseconds(AfrDelayMs) - StabilityWindow - TimeSpan.FromSeconds(1);
            int removeCount = 0;
            foreach (var s in _buffer)
            {
                if (s.Timestamp < cutoff) removeCount++;
                else break;
            }
            if (removeCount > 0) _buffer.RemoveRange(0, removeCount);
        }

        /// <summary>
        /// ป้อนค่า AFR ล่าสุดเข้าระบบ ตัว logger จะหาค่า RPM/TPS ที่เกิดขึ้นก่อนหน้า
        /// ตามระยะเวลา AfrDelayMs แล้วเช็คว่าช่วงนั้นนิ่งพอไหมก่อนจะบันทึกลงตาราง
        /// </summary>
        public void AddAfrSample(DateTime timestamp, double afr)
        {
            if (afr <= 0) return; // ค่า AFR ที่อ่านไม่ได้ (sensor ยังไม่พร้อม) มักส่งมาเป็น 0 หรือค่าติดลบ

            var targetTime = timestamp - TimeSpan.FromMilliseconds(AfrDelayMs);
            var aligned = FindNearest(targetTime);

            if (aligned == null)
            {
                DiscardedNoAlignCount++;
                return;
            }

            // ข้ามการเช็ค "นิ่งพอไหม" ถ้าไม่มีการหน่วงเวลา (AfrDelayMs=0) เพราะกรณีนี้
            // AFR คำนวณมาจาก RPM/TPS ณ ขณะเดียวกันเป๊ะๆ อยู่แล้ว ไม่มีความเสี่ยงจับคู่ผิดจังหวะ
            // แบบตอนใช้ O2 sensor จริงที่มี physical lag - เช็คแล้วมีแต่จะถ่วงเปล่าๆ ตอนขับจริง
            if (AfrDelayMs > 0 && !IsStableAround(aligned.Value.Timestamp))
            {
                DiscardedTransientCount++;
                return;
            }

            int rpmBin = (int)(aligned.Value.Rpm / RpmBinSize);
            int tpsBin = GetTpsBinIndex(aligned.Value.Tps);
            var key = (rpmBin, tpsBin);

            if (!_table.TryGetValue(key, out var cell))
            {
                cell = new AfrCell();
                _table[key] = cell;
            }
            cell.AddSample(afr);
            AcceptedCount++;
        }

        private EngineSample? FindNearest(DateTime targetTime)
        {
            if (_buffer.Count == 0) return null;

            EngineSample? best = null;
            double bestDiffMs = double.MaxValue;

            foreach (var s in _buffer)
            {
                double diffMs = Math.Abs((s.Timestamp - targetTime).TotalMilliseconds);
                if (diffMs < bestDiffMs)
                {
                    bestDiffMs = diffMs;
                    best = s;
                }
            }

            if (bestDiffMs > MaxAlignmentGapMs) return null;
            return best;
        }

        private bool IsStableAround(DateTime centerTime)
        {
            double halfWindowMs = StabilityWindow.TotalMilliseconds;
            var windowSamples = _buffer
                .Where(s => Math.Abs((s.Timestamp - centerTime).TotalMilliseconds) <= halfWindowMs)
                .ToList();

            // ถ้าข้อมูลรอบจุดนี้มีน้อยเกินไปที่จะตัดสิน ให้ผ่านแบบระวังไว้ก่อน (ไม่ทิ้งทั้งหมด)
            if (windowSamples.Count < 2) return true;

            double minRpm = windowSamples.Min(s => s.Rpm);
            double maxRpm = windowSamples.Max(s => s.Rpm);
            double minTps = windowSamples.Min(s => s.Tps);
            double maxTps = windowSamples.Max(s => s.Tps);

            return (maxRpm - minRpm) <= RpmStableThreshold
                && (maxTps - minTps) <= TpsStableThreshold;
        }

        public void Clear()
        {
            _table.Clear();
            _buffer.Clear();
            DiscardedTransientCount = 0;
            DiscardedNoAlignCount = 0;
            AcceptedCount = 0;
        }

        /// <summary>
        /// เซฟตารางเป็น CSV รูปแบบเดียวกับตาราง ignition/fuel map ทั่วไป
        /// แถวแรก = ค่า TPS (องศา) ของแต่ละคอลัมน์ ตรงกับ breakpoint จริงของ Honda,
        /// คอลัมน์แรก = ค่า RPM ของแต่ละแถว
        /// </summary>
        public void ExportCsv(string path, int maxRpmBin, int maxTpsBin)
        {
            using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8);

            // หัวตาราง: ค่า TPS (องศา) ของแต่ละคอลัมน์ ตาม breakpoint จริง
            writer.Write("RPM \\ TPS(deg)");
            for (int t = 0; t <= maxTpsBin && t < TpsBreakpoints.Length; t++)
            {
                writer.Write(",");
                writer.Write(TpsBreakpoints[t].ToString("0.0"));
            }
            writer.WriteLine();

            for (int r = 0; r <= maxRpmBin; r++)
            {
                writer.Write((r * RpmBinSize).ToString("0"));
                for (int t = 0; t <= maxTpsBin && t < TpsBreakpoints.Length; t++)
                {
                    writer.Write(",");
                    if (_table.TryGetValue((r, t), out var cell) && cell.SampleCount > 0)
                    {
                        writer.Write(cell.AvgAfr.ToString("0.00"));
                    }
                    // cell ว่าง (ไม่มีข้อมูล) ปล่อยว่างไว้ ไม่ใส่ 0 เพราะ 0 จะทำให้เข้าใจผิดว่ามีข้อมูล
                }
                writer.WriteLine();
            }
        }
    }
}

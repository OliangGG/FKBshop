using System;

namespace BinTuner.Afr
{
    /// <summary>
    /// เก็บข้อมูลสถิติของ AFR ในหนึ่งช่อง (cell) ของตาราง RPM x TPS
    /// ใช้ Welford's online algorithm เพื่อคำนวณค่าเฉลี่ยและ std deviation
    /// แบบสะสมทีละ sample โดยไม่ต้องเก็บ raw data ทั้งหมดไว้ในหน่วยความจำ
    /// (สำคัญเพราะโปรแกรมนี้จะรันเก็บข้อมูลต่อเนื่องเป็นชั่วโมงได้โดยไม่กิน RAM มาก)
    /// </summary>
    public class AfrCell
    {
        public double AvgAfr { get; private set; } = 0.0;
        public int SampleCount { get; private set; } = 0;

        // ตัวแปรภายในสำหรับคำนวณ variance แบบ Welford's algorithm
        private double _m2 = 0.0;

        public double StdDev => SampleCount > 1 ? Math.Sqrt(_m2 / (SampleCount - 1)) : 0.0;

        /// <summary>
        /// ความน่าเชื่อถือของข้อมูลใน cell นี้ ใช้สำหรับกำหนดความทึบ/จางตอนวาด heatmap
        /// ยิ่ง sample เยอะยิ่งน่าเชื่อถือ แต่ให้ค่าอิ่มตัวที่ 30 sample (ไม่ต้องรอเป็นพันครั้ง)
        /// </summary>
        public double Confidence => Math.Min(1.0, SampleCount / 30.0);

        public void AddSample(double afr)
        {
            SampleCount++;
            double delta = afr - AvgAfr;
            AvgAfr += delta / SampleCount;
            double delta2 = afr - AvgAfr;
            _m2 += delta * delta2;
        }

        public void Reset()
        {
            AvgAfr = 0.0;
            SampleCount = 0;
            _m2 = 0.0;
        }
    }
}

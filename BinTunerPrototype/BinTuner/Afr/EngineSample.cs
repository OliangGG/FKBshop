using System;

namespace BinTuner.Afr
{
    /// <summary>
    /// สแนปช็อตของค่า RPM/TPS ณ เวลาหนึ่ง ใช้เก็บใน buffer ของ AfrLogger
    /// เพื่อทำ time-alignment กับค่า AFR ที่มาช้ากว่า (O2 sensor lag)
    /// </summary>
    public readonly struct EngineSample
    {
        public DateTime Timestamp { get; }
        public double Rpm { get; }
        public double Tps { get; }

        public EngineSample(DateTime timestamp, double rpm, double tps)
        {
            Timestamp = timestamp;
            Rpm = rpm;
            Tps = tps;
        }
    }
}

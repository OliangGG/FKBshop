using System;

namespace BinTuner.Afr
{
    /// <summary>
    /// จำลองข้อมูล RPM/TPS/AFR แบบสมจริงพอประมาณ (idle, เร่ง, คงที่, ผ่อน)
    /// ใช้ทดสอบ UI และ logic ของ AfrLogger ได้โดยไม่ต้องต่อ ECU จริง
    /// </summary>
    public class DataSimulator
    {
        private readonly Random _rand = new();
        private const double MaxTpsDegrees = 73.0; // ให้ตรงกับสเกลจริงของ HondaEcuReader
        private double _rpm = 1400;
        private double _tps = 0;
        private double _targetTps = 0;
        private double _phaseTimer = 0;
        private readonly double _dtSeconds;

        public DataSimulator(double sampleIntervalMs)
        {
            _dtSeconds = sampleIntervalMs / 1000.0;
        }

        /// <summary>เรียกทุก tick เพื่อสร้างค่าถัดไป</summary>
        public (double rpm, double tps, double afr) NextSample()
        {
            _phaseTimer -= _dtSeconds;
            if (_phaseTimer <= 0)
            {
                // สุ่มเป้าหมาย TPS ใหม่ทุก 1.5-4 วินาที จำลองการบิดคันเร่งขึ้นๆ ลงๆ (หน่วยองศา 0-73)
                _targetTps = _rand.NextDouble() * MaxTpsDegrees;
                _phaseTimer = 1.5 + _rand.NextDouble() * 2.5;
            }

            // ให้ TPS ไล่เข้าหาเป้าหมายแบบมีความเฉื่อย (เหมือนมือจริงบิดคันเร่ง)
            double tpsRate = MaxTpsDegrees * 0.6; // องศา/วิ สูงสุด (ปรับสัดส่วนตาม MaxTpsDegrees)
            double tpsDiff = _targetTps - _tps;
            double tpsStep = Math.Sign(tpsDiff) * Math.Min(Math.Abs(tpsDiff), tpsRate * _dtSeconds);
            _tps += tpsStep;
            _tps = Math.Clamp(_tps + (_rand.NextDouble() - 0.5) * 0.5, 0, MaxTpsDegrees); // noise เล็กน้อย

            // RPM ไล่ตาม TPS แบบมี lag/momentum เหมือนเครื่องยนต์จริง (ไม่สนองทันที)
            double targetRpm = 1300 + (_tps / MaxTpsDegrees) * 8500; // TPS สุด -> ~9800rpm โดยประมาณ
            double rpmRate = 4000.0; // rpm/sec สูงสุดตอนเร่ง
            double rpmDiff = targetRpm - _rpm;
            double rpmStep = Math.Sign(rpmDiff) * Math.Min(Math.Abs(rpmDiff), rpmRate * _dtSeconds);
            _rpm += rpmStep;
            _rpm = Math.Clamp(_rpm + (_rand.NextDouble() - 0.5) * 20, 1300, 10500);

            // AFR: ประมาณค่าจาก TPS โดยคร่าวๆ (โหลดสูง = รวยขึ้น/ตัวเลขต่ำลง) บวก noise
            // แล้วจำลอง lag ของ O2 sensor คร่าวๆ ด้วยการหน่วงเบาๆ ผ่าน low-pass ง่ายๆ
            double targetAfr = 14.7 - (_tps / MaxTpsDegrees) * 2.2; // full throttle ~12.5, idle ~14.7
            _afrSmoothed += (targetAfr - _afrSmoothed) * Math.Min(1.0, _dtSeconds / 0.25);
            double afr = _afrSmoothed + (_rand.NextDouble() - 0.5) * 0.3;

            return (_rpm, _tps, afr);
        }

        private double _afrSmoothed = 14.7;
    }
}

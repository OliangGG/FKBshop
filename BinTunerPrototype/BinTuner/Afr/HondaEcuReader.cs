using System;
using System.Linq;
using System.Threading;
using FTD2XX_NET;

namespace BinTuner.Afr
{
    /// <summary>
    /// เชื่อมต่อกับ ECU จริงของ Honda Giorno125 (ตระกูล K3MH) ผ่านชิป FTDI โดยตรง
    ///
    /// ยืนยันแล้วจากการทดสอบจริงหลายรอบ:
    ///  - RPM: table 17, offset 5-6 (2 byte, ค่าตรงๆ ไม่ต้องแปลง)
    ///  - TPS: table 17, offset 7-8 (2 byte, ค่าดิบ — คำนวณเป็น % คร่าวๆ จากช่วงที่สังเกตได้)
    ///
    /// เรื่อง AFR: สแกนหาตำแหน่ง byte ของ O2 sensor จริงในทุก table ที่มีแล้วไม่เจอ
    /// (ทั้งจากการสแกนของเราเองและจากการดักฟัง NM Tool ตัวจริง) เป็นไปได้สูงว่า
    /// ECU รุ่นนี้ไม่ส่งค่า O2 ดิบผ่านโปรโตคอลนี้ หรือ NM Tool คำนวณ AFR เองจาก
    /// fuel map แทนที่จะอ่านจากเซนเซอร์ตรงๆ — โปรแกรมนี้เลยแสดง "AFR ประมาณการ"
    /// จากค่า TPS แทน (รวยขึ้นตอนโหลดสูง ตามหลักการทั่วไปของการจูน) ไม่ใช่ค่าจริงจากเซนเซอร์
    /// ถ้าในอนาคตเจอตำแหน่ง byte ของ O2 จริง ให้มาแก้ตรงเมธอด ReadLiveData() จุดเดียว
    /// </summary>
    public class HondaEcuReader : IDisposable
    {
        private readonly FTDI _ftdi = new FTDI();
        private const int KeepAliveMs = 20000;
        private DateTime _lastSuccessTime = DateTime.MinValue;

        // มุม TPS สูงสุดจริงของ Honda (ยืนยันจากผู้ใช้ว่าตอนจูนใช้ 73 องศาเป็นจุดสุด)
        private const double MaxTpsDegrees = 73.0;

        // ค่า calibration TPS - ปรับได้ผ่านปุ่ม Calibrate ใน UI (ไม่ใช่ const แข็งอีกต่อไป)
        // ค่าเริ่มต้นนี้เป็นแค่ placeholder เดาไว้ก่อน ควรกด Calibrate จริงก่อนใช้งาน
        private double _tpsRawAtIdle = 6144;
        private double _tpsRawAtWideOpen = 9840;

        public double TpsCalibIdle => _tpsRawAtIdle;
        public double TpsCalibWideOpen => _tpsRawAtWideOpen;

        /// <summary>เรียกตอนคันเร่งอยู่ที่ 0% (ไม่บิดเลย) เพื่อจำค่า raw ปัจจุบันเป็นจุดอ้างอิง 0%</summary>
        public void CalibrateIdle() => _tpsRawAtIdle = LastRawTps;

        /// <summary>เรียกตอนบิดคันเร่งสุด (WOT) เพื่อจำค่า raw ปัจจุบันเป็นจุดอ้างอิง 100%</summary>
        public void CalibrateWideOpen() => _tpsRawAtWideOpen = LastRawTps;

        // เก็บค่า TPS ดิบล่าสุด 3 ค่า ใช้ทำ median filter กันสัญญาณกระตุกแบบเดี่ยวๆ
        // (TPS แบบ potentiometer มีธรรมชาติของสัญญาณรบกวนที่จุดสัมผัสแปรงถ่านอยู่แล้ว)
        private readonly System.Collections.Generic.Queue<double> _tpsRawHistory = new();

        public double LastRawTps { get; private set; } = 0; // ไว้ debug ดูค่าดิบก่อน smoothing
        public double? LastO2Voltage { get; private set; } = null; // ค่า O2(V) ทดลอง เอาไว้เทียบกับ TunerPro
        public double LastInjMsEstimate { get; private set; } = 0; // ค่า INJ(ms) ทดลอง จากสูตรเส้นตรง 2 จุด
        public bool IsConnected { get; private set; } = false;

        public void Open(uint deviceIndex = 0)
        {
            var status = _ftdi.OpenByIndex(deviceIndex);
            if (status != FTDI.FT_STATUS.FT_OK)
                throw new Exception($"เปิดอุปกรณ์ FTDI ไม่สำเร็จ: {status}");

            ConfigureUartMode();
        }

        private void ConfigureUartMode()
        {
            _ftdi.SetBitMode(0x00, FTDI.FT_BIT_MODES.FT_BIT_MODE_RESET);
            _ftdi.SetBaudRate(10400);
            _ftdi.SetDataCharacteristics(FTDI.FT_DATA_BITS.FT_BITS_8, FTDI.FT_STOP_BITS.FT_STOP_BITS_1, FTDI.FT_PARITY.FT_PARITY_NONE);
            _ftdi.SetFlowControl(FTDI.FT_FLOW_CONTROL.FT_FLOW_NONE, 0, 0);
            _ftdi.SetTimeouts(500, 500);
        }

        private void SendWakePulse()
        {
            _ftdi.SetBitMode(0x01, FTDI.FT_BIT_MODES.FT_BIT_MODE_ASYNC_BITBANG);
            uint written = 0;
            _ftdi.Write(new byte[] { 0x00 }, 1, ref written);
            Thread.Sleep(70);
            _ftdi.Write(new byte[] { 0x01 }, 1, ref written);
            Thread.Sleep(130);
            ConfigureUartMode();
        }

        /// <summary>ทำ init sequence กับ ECU คืนค่า true ถ้าสำเร็จ</summary>
        public bool Init()
        {
            _ftdi.Purge(FTDI.FT_PURGE.FT_PURGE_RX | FTDI.FT_PURGE.FT_PURGE_TX);
            SendWakePulse();

            byte[] wakeup = { 0xFE, 0x04, 0x72, 0x8C };
            WriteBytes(wakeup);
            Thread.Sleep(200);
            var rawWakeup = ReadAvailable(250);
            var wakeupResp = rawWakeup == null ? null : StripEcho(rawWakeup, wakeup);

            byte[] init = { 0x72, 0x05, 0x00, 0xF0, 0x99 };
            WriteBytes(init);
            var rawInit = ReadAvailable(350);
            var initResp = rawInit == null ? null : StripEcho(rawInit, init);

            bool ok = initResp != null && initResp.Length > 0 && initResp[0] == 0x02;
            IsConnected = ok;
            if (ok) _lastSuccessTime = DateTime.Now;
            return ok;
        }

        /// <summary>
        /// อ่านค่า RPM/TPS จริงจาก ECU (table 17) แล้วคำนวณ AFR ประมาณการจาก TPS
        /// คืนค่า null ถ้าอ่านไม่สำเร็จ (จะได้ลองใหม่ในรอบถัดไปโดยไม่ทำโปรแกรมล่ม)
        /// </summary>
        public (double rpm, double tps, double afrEstimated)? ReadLiveData()
        {
            // ต้อง re-init ถ้าห่างจากครั้งล่าสุดที่สำเร็จนานเกินไป (กัน ECU หลับ)
            if ((DateTime.Now - _lastSuccessTime).TotalMilliseconds > KeepAliveMs)
            {
                if (!Init()) return null;
            }

            // ใช้ format เดียวกับที่ TunerPro ใช้จริง (ยืนยันจากการดักจับว่าไวถึง 24Hz ไม่มี error)
            // สั้นกว่าของเดิม 1 byte (query แบบ full-read 0x71 แทน range-read 0x72)
            byte[] requestBody = { 0x72, 0x05, 0x71, 0x17 };
            byte checksum = ComputeChecksum(requestBody);
            byte[] request = requestBody.Append(checksum).ToArray();

            _ftdi.Purge(FTDI.FT_PURGE.FT_PURGE_RX);
            WriteBytes(request);
            var raw = ReadAvailable(90); // ลดจาก 120ms อีกนิด (สายแก้แล้ว ตอบสนองไวขึ้น)
            if (raw == null) { IsConnected = false; return null; }

            var resp = StripEcho(raw, request);
            // ต้องมีอย่างน้อยถึง offset 8 (RPM 2 byte + TPS 2 byte หลัง header 5 byte: 02 25 72 17 00)
            if (resp.Length < 8) { IsConnected = false; return null; }

            // ตรวจ checksum ก่อนเชื่อข้อมูล - ถ้า byte เลื่อนตำแหน่ง/ข้อมูลเพี้ยนจะจับได้ตรงนี้
            // (กันปัญหา TPS อ่านไปโดนช่วง FF FF ที่ไม่ใช่ข้อมูลจริง ทำให้ค่ากระโดดมั่วๆ)
            if (!VerifyChecksum(resp)) { return null; }

            // เช็คเพิ่มว่า header ตรงกับที่คาดไว้ (byte แรกควรเป็น 0x02, byte ตำแหน่ง 3 ควรเป็น table 0x17)
            if (resp[0] != 0x02 || resp.Length < 4 || resp[3] != 0x17) { return null; }

            IsConnected = true;
            _lastSuccessTime = DateTime.Now;

            double rpm = (resp[4] << 8) | resp[5];
            double tpsRawInstant = (resp[6] << 8) | resp[7];
            LastRawTps = tpsRawInstant;

            // INJ(ms) ทดลอง จาก table 17 index 17 (raw byte เดียว) - สูตรเส้นตรงจาก 2 จุดข้อมูลจริง
            // ที่เทียบกับ TunerPro มา (idle raw~159->2.28ms, บิดหนัก raw~248->12.5ms)
            // ยังไม่แม่นยำ 100% (มีแค่ 2 จุด) รอเก็บข้อมูลเพิ่มเพื่อปรับสูตรให้แม่นขึ้นทีหลัง
            if (resp.Length > 17)
            {
                double rawInj = resp[17];
                LastInjMsEstimate = Math.Max(0, rawInj * 0.1148 - 15.97);
            }

            // median filter: เก็บ 3 ค่าล่าสุด เอาค่ากลางมาใช้ กันสัญญาณกระตุกเดี่ยวๆ จาก TPS
            _tpsRawHistory.Enqueue(tpsRawInstant);
            if (_tpsRawHistory.Count > 3) _tpsRawHistory.Dequeue();
            double tpsRaw = Median(_tpsRawHistory);

            double tpsDegrees = (tpsRaw - _tpsRawAtIdle) / (_tpsRawAtWideOpen - _tpsRawAtIdle) * MaxTpsDegrees;
            tpsDegrees = Math.Clamp(tpsDegrees, 0, MaxTpsDegrees);

            // AFR ประมาณการแบบละเอียดขึ้น (ไม่ใช่ค่าจริงจากเซนเซอร์ O2 — ดูหมายเหตุด้านบนของคลาสนี้)
            // ใช้ทั้ง TPS และ RPM ร่วมกัน แทนที่จะดู TPS อย่างเดียว:
            //  - TPS สูง -> รวยขึ้นแบบไม่เชิงเส้น (โค้งกำลัง 1.3 ใกล้เคียงพฤติกรรมจริงของ fuel map ทั่วไป)
            //  - รอบเครื่องสูง + TPS สูงพร้อมกัน -> รวยเพิ่มอีกเล็กน้อย (จำลอง high-RPM enrichment
            //    ที่ ECU มักเพิ่มให้เพื่อระบายความร้อน/ป้องกันน็อคตอนโหลดหนักรอบจัด)
            double tpsRatio = tpsDegrees / MaxTpsDegrees;
            double rpmRatio = Math.Clamp(rpm / 9000.0, 0, 1.3); // เกิน 9000 ยังให้คิดต่อได้ (ไม่ clamp แข็งที่ 1.0)

            double tpsEnrichment = Math.Pow(tpsRatio, 1.3) * 2.6;
            double highRpmEnrichment = rpmRatio * tpsRatio * 0.6;

            double afrEstimated = 14.7 - tpsEnrichment - highRpmEnrichment;
            afrEstimated = Math.Clamp(afrEstimated, 11.5, 15.2);

            return (rpm, tpsDegrees, afrEstimated);
        }

        /// <summary>
        /// เรียกเพิ่มหลัง ReadLiveData() ทุกครั้งที่ต้องการ (แยกออกมาต่างหาก ไม่บังคับ)
        /// เพื่อ poll table 20 (O2 ทดลอง) สลับกับ table 17 แบบเดียวกับที่ TunerPro ทำ
        /// อัปเดต LastO2Voltage ให้ค่าล่าสุดเสมอ (ไม่ throw ถ้าอ่านไม่สำเร็จ แค่คงค่าเดิมไว้)
        /// </summary>
        public void PollO2VoltageIfDue()
        {
            var v = ReadO2VoltageExperimental();
            if (v.HasValue) LastO2Voltage = v;
        }

        private static double Median(System.Collections.Generic.Queue<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int n = sorted.Count;
            if (n == 0) return 0;
            return sorted[n / 2]; // ค่ากลาง (ถ้าจำนวนคู่ ปัดไปตัวหลัง ง่ายและพอเพียงสำหรับงานนี้)
        }

        /// <summary>
        /// อ่าน ECM ID จาก table 00 (ตรงกับที่ NM Tool โชว์ "ECM ID: 0106A70D01")
        /// คืนค่า string เช่น "0106A70D01" หรือ null ถ้าอ่านไม่สำเร็จ
        /// </summary>
        public string? ReadEcmId()
        {
            byte[] requestBody = { 0x72, 0x07, 0x72, 0x00, 0x00, 0x1F };
            byte checksum = ComputeChecksum(requestBody);
            byte[] request = requestBody.Append(checksum).ToArray();

            _ftdi.Purge(FTDI.FT_PURGE.FT_PURGE_RX);
            WriteBytes(request);
            var raw = ReadAvailable(150);
            if (raw == null) return null;

            var resp = StripEcho(raw, request);
            if (resp.Length < 10 || !VerifyChecksum(resp)) return null;
            if (resp[0] != 0x02 || resp[3] != 0x00) return null;

            // ECM ID อยู่ที่ offset 5-9 (5 byte) ตรงกับที่ NM Tool แสดง เช่น 01 06 A7 0D 01 -> "0106A70D01"
            return string.Concat(resp[5..10].Select(b => b.ToString("X2")));
        }

        /// <summary>
        /// อ่านค่า O2 sensor voltage แบบทดลอง จาก table 20 (สเกลที่ใช้ตอนนี้เป็นการประมาณ
        /// จากการเทียบ byte pattern กับค่าที่เห็นจริงใน TunerPro dashboard - ยังไม่ยืนยัน 100%
        /// เอาไว้เทียบกับ TunerPro ข้างๆ กันเพื่อยืนยัน/ปรับสเกลให้แม่นขึ้นทีหลัง)
        /// คืนค่า null ถ้าอ่านไม่สำเร็จ
        /// </summary>
        public double? ReadO2VoltageExperimental()
        {
            byte[] requestBody = { 0x72, 0x05, 0x71, 0x20 };
            byte checksum = ComputeChecksum(requestBody);
            byte[] request = requestBody.Append(checksum).ToArray();

            _ftdi.Purge(FTDI.FT_PURGE.FT_PURGE_RX);
            WriteBytes(request);
            var raw = ReadAvailable(90);
            if (raw == null) return null;

            var resp = StripEcho(raw, request);
            if (resp.Length < 6 || !VerifyChecksum(resp)) return null;
            if (resp[0] != 0x02 || resp[3] != 0x20) return null;

            // byte แรกของข้อมูล (index 4) เป็นตัวเต็ง O2(V) - สเกล *0.02 เป็นค่าประมาณเริ่มต้น
            // (เทียบจากช่วงค่าที่เห็นจริงใน TunerPro ~0.3-1.05V ตอนที่เก็บ capture)
            double rawByte = resp[4];
            return rawByte * 0.02;
        }

        private void WriteBytes(byte[] data)
        {
            uint written = 0;
            _ftdi.Write(data, data.Length, ref written);
        }

        private byte[]? ReadAvailable(int windowMs)
        {
            var buffer = new System.Collections.Generic.List<byte>();
            var deadline = DateTime.Now.AddMilliseconds(windowMs);
            bool gotAny = false;

            while (DateTime.Now < deadline)
            {
                uint toRead = 0;
                _ftdi.GetRxBytesAvailable(ref toRead);
                if (toRead > 0)
                {
                    byte[] chunk = new byte[toRead];
                    uint actuallyRead = 0;
                    _ftdi.Read(chunk, toRead, ref actuallyRead);
                    buffer.AddRange(chunk.Take((int)actuallyRead));
                    gotAny = true;
                    // ได้ข้อมูลครบ frame แล้ว (>= 20 byte) จบเลยไม่ต้องรอต่อ
                    if (buffer.Count >= 20) break;
                    deadline = DateTime.Now.AddMilliseconds(10);
                }
                else
                {
                    Thread.Sleep(1); // poll ถี่ขึ้น (1ms แทน 5ms) ให้ตอบสนองไวขึ้น
                }
            }
            return gotAny ? buffer.ToArray() : null;
        }

        private static byte[] StripEcho(byte[] captured, byte[] sent)
        {
            int idx = FindSubsequence(captured, sent);
            if (idx < 0) return captured;
            int remainderStart = idx + sent.Length;
            if (remainderStart >= captured.Length) return Array.Empty<byte>();
            return captured[remainderStart..];
        }

        private static int FindSubsequence(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private static byte ComputeChecksum(byte[] bytes)
        {
            int sum = bytes.Sum(b => (int)b);
            return (byte)((0x100 - (sum & 0xFF)) & 0xFF);
        }

        /// <summary>ตรวจว่า checksum ของ response (byte สุดท้าย) ตรงกับผลรวม byte ก่อนหน้าไหม</summary>
        private static bool VerifyChecksum(byte[] response)
        {
            if (response.Length < 2) return false;
            byte expected = ComputeChecksum(response[..^1]);
            return expected == response[^1];
        }

        public void Dispose()
        {
            _ftdi.Close();
        }
    }
}

// Заряд Logitech-пристроїв через протокол HID++ 2.0 (ресивери Unifying/LIGHTSPEED/Bolt і прямі пристрої).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BatteryTray
{
    static class Logitech
    {
        const byte SwId = 0x0B;
        const byte Long = 0x11;

        public static Action<string> Log = delegate { };

        class Session
        {
            public HidConnection Conn;
            public byte Dev;

            // Надсилає запит і чекає на відповідь. null — помилка або тайм-аут.
            public byte[] Call(byte feature, byte func, int timeoutMs, params byte[] args)
            {
                var req = new byte[20];
                req[0] = Long; req[1] = Dev; req[2] = feature; req[3] = (byte)((func << 4) | SwId);
                Array.Copy(args, 0, req, 4, Math.Min(args.Length, 16));
                if (!Conn.Write(req)) return null;
                var deadline = Environment.TickCount + timeoutMs;
                while (true)
                {
                    int left = deadline - Environment.TickCount;
                    if (left <= 0) return null;
                    var r = Conn.Read(left);
                    if (r == null) return null;
                    if (r.Length < 7 || r[1] != Dev) continue;
                    if (r[2] == feature && r[3] == req[3]) return r.Skip(4).ToArray();
                    if (r[2] == 0xFF && r[3] == feature && r[4] == req[3]) return null; // HID++ 2.0 error
                    if (r[2] == 0x8F && r[3] == feature) return null;                   // HID++ 1.0 error
                }
            }

            public byte FeatureIndex(ushort id)
            {
                var r = Call(0x00, 0, 400, (byte)(id >> 8), (byte)id);
                return r == null ? (byte)0 : r[0];
            }
        }

        static readonly int[,] VoltageCurve = {
            { 4186, 100 }, { 4067, 90 }, { 3989, 80 }, { 3922, 70 }, { 3859, 60 }, { 3811, 50 },
            { 3778, 40 }, { 3751, 30 }, { 3717, 20 }, { 3671, 10 }, { 3646, 5 }, { 3579, 2 }, { 3500, 0 } };

        public static int VoltageToPercent(int mv)
        {
            if (mv >= VoltageCurve[0, 0]) return 100;
            for (int i = 1; i < VoltageCurve.GetLength(0); i++)
                if (mv >= VoltageCurve[i, 0])
                {
                    double t = (double)(mv - VoltageCurve[i, 0]) / (VoltageCurve[i - 1, 0] - VoltageCurve[i, 0]);
                    return (int)Math.Round(VoltageCurve[i, 1] + t * (VoltageCurve[i - 1, 1] - VoltageCurve[i, 1]));
                }
            return 0;
        }

        static int ReadBattery(Session s, out bool charging)
        {
            charging = false;
            byte idx;
            if ((idx = s.FeatureIndex(0x1004)) != 0) // UNIFIED_BATTERY
            {
                var r = s.Call(idx, 1, 500);
                if (r != null)
                {
                    charging = r[2] == 1 || r[2] == 2;
                    if (r[0] > 0 && r[0] <= 100) return r[0];
                    // Деякі пристрої дають лише рівень: 8=full,4=good,2=low,1=critical
                    if ((r[1] & 8) != 0) return 100; if ((r[1] & 4) != 0) return 60;
                    if ((r[1] & 2) != 0) return 20; if ((r[1] & 1) != 0) return 5;
                }
            }
            if ((idx = s.FeatureIndex(0x1000)) != 0) // BATTERY_STATUS
            {
                var r = s.Call(idx, 0, 500);
                if (r != null && r[0] <= 100) { charging = r[2] >= 1 && r[2] <= 3; return r[0]; }
            }
            if ((idx = s.FeatureIndex(0x1001)) != 0) // BATTERY_VOLTAGE
            {
                var r = s.Call(idx, 0, 500);
                if (r != null) { charging = (r[2] & 0x80) != 0; return VoltageToPercent((r[0] << 8) | r[1]); }
            }
            if ((idx = s.FeatureIndex(0x1F20)) != 0) // ADC_MEASUREMENT
            {
                var r = s.Call(idx, 0, 500);
                if (r != null) { charging = (r[2] & 0x02) != 0; return VoltageToPercent((r[0] << 8) | r[1]); }
            }
            return -1;
        }

        static string ReadName(Session s, out int type)
        {
            type = -1;
            byte idx = s.FeatureIndex(0x0005);
            if (idx == 0) return null;
            var t = s.Call(idx, 2, 400);
            if (t != null) type = t[0];
            var c = s.Call(idx, 0, 400);
            if (c == null) return null;
            var sb = new StringBuilder();
            while (sb.Length < c[0])
            {
                var p = s.Call(idx, 1, 400, (byte)sb.Length);
                if (p == null) break;
                int before = sb.Length;
                foreach (byte b in p) { if (b == 0 || sb.Length >= c[0]) break; sb.Append((char)b); }
                if (sb.Length == before) break;
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        public static List<BatteryDevice> Scan()
        {
            var result = new List<BatteryDevice>();
            var ifaces = Hid.Enumerate(v => v == 0x046D)
                .Where(i => i.UsagePage == 0xFF00 && i.Usage == 0x0002 && i.OutputLen == 20).ToList();
            foreach (var iface in ifaces)
            {
                HidConnection conn = Hid.Open(iface);
                if (conn == null) { Log("cannot open " + iface.Path); continue; }
                using (conn)
                {
                    // 0xFF — сам пристрій (дротовий/прямий), 1..6 — слоти ресивера.
                    foreach (byte dev in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 })
                    {
                        var s = new Session { Conn = conn, Dev = dev };
                        var ping = s.Call(0x00, 1, 300, 0, 0, 0x5A); // getProtocolVersion
                        if (ping == null || ping[0] < 2) { Log(string.Format("{0:X4} dev {1:X2}: no HID++2.0", iface.Pid, dev)); continue; }
                        bool charging;
                        int pct = ReadBattery(s, out charging);
                        int type;
                        string name = ReadName(s, out type) ?? iface.Product ?? "Logitech";
                        Log(string.Format("{0:X4} dev {1:X2}: '{2}' type={3} battery={4} charging={5}", iface.Pid, dev, name, type, pct, charging));
                        if (pct < 0) continue;
                        result.Add(new BatteryDevice
                        {
                            Container = StableGuid("logi|" + iface.Path + "|" + dev),
                            Name = "Logitech " + name.Replace("Logitech ", ""),
                            Percent = pct,
                            Charging = charging,
                            Kind = type == 0 || type == 2 ? DeviceKind.Keyboard
                                 : (type >= 3 && type <= 5) ? DeviceKind.Mouse
                                 : type == 8 ? DeviceKind.Headphones : DeviceKind.Other
                        });
                        if (dev == 0xFF) break; // прямий пристрій — слоти не потрібні
                    }
                }
            }
            return result;
        }

        // Навушники PRO X 2 LIGHTSPEED: власний протокол (репорт 0x51, сторінка 0xFFA0), не HID++.
        static readonly int[] HeadsetPids = { 0x0AF7 };

        public static List<BatteryDevice> ScanHeadsets()
        {
            var result = new List<BatteryDevice>();
            var ifaces = Hid.Enumerate(v => v == 0x046D)
                .Where(i => HeadsetPids.Contains(i.Pid) && i.UsagePage == 0xFFA0 && i.OutputLen == 64).ToList();
            foreach (var iface in ifaces)
            {
                HidConnection conn = Hid.Open(iface);
                if (conn == null) { Log("cannot open " + iface.Path); continue; }
                using (conn)
                {
                    var req = new byte[64];
                    new byte[] { 0x51, 0x08, 0x00, 0x03, 0x1A, 0x00, 0x03, 0x00, 0x04, 0x0A }.CopyTo(req, 0);
                    if (!conn.Write(req)) { Log("headset write failed"); continue; }
                    int deadline = Environment.TickCount + 1500;
                    int pct = -1; bool charging = false, off = false;
                    while (pct < 0 && !off)
                    {
                        int left = deadline - Environment.TickCount;
                        if (left <= 0) break;
                        var r = conn.Read(left);
                        if (r == null) break;
                        Log("headset frame: " + BitConverter.ToString(r, 0, Math.Min(16, r.Length)));
                        if (r.Length <= 12 || r[0] != 0x51) continue;
                        if (r[1] == 0x0B && r[8] == 0x04 && r[10] <= 100) { pct = r[10]; charging = r[12] == 0x02; }
                        else if (r[1] == 0x05 && r[6] == 0x00) off = true;
                    }
                    Log(string.Format("headset {0:X4}: battery={1} charging={2} off={3}", iface.Pid, pct, charging, off));
                    if (pct < 0) continue;
                    result.Add(new BatteryDevice
                    {
                        Container = StableGuid("logi-headset|" + iface.Path),
                        Name = "Logitech " + (iface.Product ?? "headset"),
                        Percent = pct, Charging = charging, Kind = DeviceKind.Headphones
                    });
                }
            }
            return result;
        }

        public static Guid StableGuid(string s)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
                return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(s)));
        }
    }
}

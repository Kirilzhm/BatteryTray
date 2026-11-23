// Заряд пристроїв Attack Shark (і сумісних на тих самих чипах). Три сімейства:
//
// 1. Клавіатури на ресиверах YiChip, VID 0x3151 (твоя 2.4G, K86, X65HE, K75, K85, X85 Pro…).
//    Шлемо ЛИШЕ команду статусу ресивера 0xF7 (читання). Відповідь: [1] заряд клави %,
//    [2] заряд мишки %, [3] 0 = клава онлайн, [4] 0 = мишка онлайн, [6] 1 клава / 2 мишка / 3 обидві.
//    Джерело: github.com/dniminenn/sharkfin (receiver.rs).
//
// 2. Мишки на ресиверах Beken, VID 0x1D57 (X11, X6, R1, X8 SE, X8 Plus, X3…).
//    Нічого не надсилаємо: мишка сама періодично шле вхідний репорт
//    03 <id> 40 <стан> <заряд>, де заряд — 0..10 (×10 %) або 0..100 %.
//    Слухаємо у фоні й запам'ятовуємо останнє значення.
//    Джерела: blak0p/attack-shark-linux, jlucaso1/attack-shark-r1, iago-fragnan/attack-shark-x11-linux,
//    viix0dev/attackshark-bridge, cyberps96/attack-shark-ctl.
//
// 3. Клавіатура X87 на ресивері 0x3554:0xFA09 (сторінка 0xFF02).
//    20-байтні репорти 0x13: 0x07 — стан зв'язку, 0x4A — заряд (обидві лише читання).
//    Джерело: github.com/thedtvn/attack_shark_x87_web (transport.ts).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace BatteryTray
{
    static class AttackShark
    {
        public static Action<string> Log = delegate { };

        public static List<BatteryDevice> Scan()
        {
            var result = new List<BatteryDevice>();
            try { result.AddRange(ScanYiChip()); } catch (Exception e) { Log("yichip: " + e.Message); }
            try { result.AddRange(ScanBekenMice()); } catch (Exception e) { Log("beken: " + e.Message); }
            try { result.AddRange(ScanX87()); } catch (Exception e) { Log("x87: " + e.Message); }
            try { result.AddRange(ScanUltra()); } catch (Exception e) { Log("ultra: " + e.Message); }
            try { result.AddRange(ScanR5Ultra()); } catch (Exception e) { Log("r5: " + e.Message); }
            try { result.AddRange(ScanX98()); } catch (Exception e) { Log("x98: " + e.Message); }
            return result;
        }

        // Надсилає вихідний репорт і чекає вхідний, що проходить перевірку match.
        static byte[] Exchange(HidConnection c, byte[] req, Func<byte[], bool> match, int timeoutMs)
        {
            if (!c.Write(req)) return null;
            int deadline = Environment.TickCount + timeoutMs;
            while (true)
            {
                int left = deadline - Environment.TickCount;
                if (left <= 0) return null;
                var r = c.Read(left);
                if (r == null) return null;
                if (match(r)) return r;
            }
        }

        // ──────────── 4. R11 Ultra / X11 Ultra (VID 0x3554, інтерфейс FF02, репорт 0x08) ────────────
        // Команда 0x04 "рівень заряду" (лише читання). Пакет: [08][cmd][addr BE x2][len][payload…][checksum],
        // checksum = 0x55 − сума попередніх байтів. Відповідь: [1]=04, [2]=0 (ок), [6]=%, [7]=1 заряджається;
        // якщо [10]==1 — точніший відсоток у [11].
        // Джерела: damien1141/attack-shark-r11_ultra-linux, imAmachine/attackshark-linux-driver.
        static List<BatteryDevice> ScanUltra()
        {
            var result = new List<BatteryDevice>();
            foreach (var iface in Hid.Enumerate(v => v == 0x3554)
                         .Where(i => i.UsagePage == 0xFF02 && i.OutputLen == 17 && i.InputLen >= 12))
            {
                var conn = Hid.Open(iface);
                if (conn == null) { Log("ultra: cannot open " + iface.Path); continue; }
                using (conn)
                {
                    var req = new byte[17];
                    req[0] = 0x08; req[1] = 0x04;
                    int sum = 0;
                    for (int i = 0; i < 16; i++) sum += req[i];
                    req[16] = (byte)((0x55 - sum) & 0xFF);
                    byte[] r = null;
                    for (int attempt = 0; attempt < 3 && r == null; attempt++)
                        r = Exchange(conn, req, x => x.Length >= 12 && x[0] == 0x08 && x[1] == 0x04, 300);
                    if (r == null) { Log(string.Format("ultra {0:X4}: no reply (mouse asleep?)", iface.Pid)); continue; }
                    Log(string.Format("ultra {0:X4} reply: {1}", iface.Pid, BitConverter.ToString(r, 0, Math.Min(16, r.Length))));
                    if (r[2] != 0) continue;
                    int pct = r[10] == 1 ? r[11] : r[6];
                    if (pct <= 0 || pct > 100) continue;
                    result.Add(new BatteryDevice
                    {
                        Container = Logitech.StableGuid("ultra|" + iface.Vid + ":" + iface.Pid),
                        Name = "Attack Shark " + (string.IsNullOrEmpty(iface.Product) ? "Ultra mouse" : iface.Product),
                        Percent = pct,
                        Charging = r[7] == 1,
                        Kind = DeviceKind.Mouse
                    });
                }
            }
            return result;
        }

        // ──────────── 5. R5 Ultra (VID 0x373E, PID 0x0046 дріт / 0x0047 ресивер) ────────────
        // Feature-репорт 64 байти без ID. Запит 00 00 02 02 00 83, відповідь починається з A1 00 02 02 00 83:
        // [7] = заряд %, [6] = 1 заряджається (з урахуванням байта ID — [8] і [7]).
        // Джерело: maxboeer/attackshark-battery-bridge.
        static List<BatteryDevice> ScanR5Ultra()
        {
            var result = new List<BatteryDevice>();
            foreach (var iface in Hid.Enumerate(v => v == 0x373E)
                         .Where(i => (i.Pid == 0x0046 || i.Pid == 0x0047) && i.UsagePage == 0xFFFF && i.FeatureLen == 65
                                     && (i.Product ?? "").IndexOf("Attack Shark", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var conn = Hid.Open(iface);
                if (conn == null) { Log("r5: cannot open " + iface.Path); continue; }
                using (conn)
                {
                    var req = new byte[65];
                    new byte[] { 0x00, 0x00, 0x00, 0x02, 0x02, 0x00, 0x83 }.CopyTo(req, 0);
                    if (!Hid.HidD_SetFeature(conn.Handle, req, req.Length)) { Log("r5: SetFeature failed"); continue; }
                    byte[] r = null;
                    for (int attempt = 0; attempt < 3 && r == null; attempt++)
                    {
                        Thread.Sleep(20 + attempt * 30);
                        var b = new byte[65];
                        if (!Hid.HidD_GetFeature(conn.Handle, b, b.Length)) continue;
                        Log("r5 reply: " + BitConverter.ToString(b, 0, 12));
                        if (b[1] == 0xA1 && b[2] == 0x00 && b[3] == 0x02 && b[4] == 0x02 && b[6] == 0x83) r = b;
                        // A3 … — пристрій ще готує відповідь, читаємо ще раз.
                    }
                    if (r == null || r[8] > 100 || r[8] == 0) continue;
                    result.Add(new BatteryDevice
                    {
                        Container = Logitech.StableGuid("r5|" + iface.Pid),
                        Name = "Attack Shark R5 Ultra",
                        Percent = r[8],
                        Charging = iface.Pid == 0x0046 && r[7] == 1,
                        Kind = DeviceKind.Mouse
                    });
                }
            }
            return result;
        }

        // ──────────── 6. X98 (VID 0x1A2C, PID 0x8FFF ресивер / 0xA034 кабель) ────────────
        // Вихідний репорт 64 байти: AA 55 CC 33 0E. Відповідь AA 55 CC 33 0E <заряд> <стан>, стан 0 = офлайн.
        // Джерело: ceeprus/Wireless-Device-Battery-Monitor (semico.cpp), перевірено автором.
        static List<BatteryDevice> ScanX98()
        {
            var result = new List<BatteryDevice>();
            foreach (var iface in Hid.Enumerate(v => v == 0x1A2C)
                         .Where(i => (i.Pid == 0x8FFF || i.Pid == 0xA034) && i.UsagePage == 0xFF02 && i.Usage == 0x0002
                                     && i.OutputLen == 65 && i.InputLen == 65))
            {
                var conn = Hid.Open(iface);
                if (conn == null) { Log("x98: cannot open " + iface.Path); continue; }
                using (conn)
                {
                    var req = new byte[65];
                    new byte[] { 0x00, 0xAA, 0x55, 0xCC, 0x33, 0x0E }.CopyTo(req, 0);
                    var r = Exchange(conn, req, x => x.Length >= 8 && x[1] == 0xAA && x[2] == 0x55 && x[3] == 0xCC && x[4] == 0x33 && x[5] == 0x0E, 600);
                    if (r == null) { Log("x98: no reply"); continue; }
                    Log("x98 reply: " + BitConverter.ToString(r, 0, 10));
                    if (r[7] == 0 || r[6] == 0 || r[6] > 100) continue; // офлайн
                    result.Add(new BatteryDevice
                    {
                        Container = Logitech.StableGuid("x98|" + iface.Pid),
                        Name = "Attack Shark X98",
                        Percent = r[6],
                        Kind = DeviceKind.Keyboard
                    });
                }
            }
            return result;
        }

        // ───────────────────────── 1. YiChip (VID 0x3151) ─────────────────────────

        static byte[] YiChipFrame(byte op)
        {
            var f = new byte[65]; // [0] = report ID 0, далі 64 байти
            f[1] = op;
            int sum = 0;
            for (int i = 1; i <= 7; i++) sum += f[i];
            f[8] = (byte)(0xFF - (sum & 0xFF));
            return f;
        }

        static List<BatteryDevice> ScanYiChip()
        {
            var result = new List<BatteryDevice>();
            // Вендорська колекція 0xFFFF; більшість плат — usage 2, деякі (Akko ACR75 v2) — usage 1.
            foreach (var iface in Hid.Enumerate(v => v == 0x3151)
                         .Where(i => i.UsagePage == 0xFFFF && (i.Usage == 0x0002 || i.Usage == 0x0001) && i.FeatureLen == 65))
            {
                var conn = Hid.Open(iface);
                if (conn == null) { Log("yichip: cannot open " + iface.Path); continue; }
                using (conn)
                {
                    var req = YiChipFrame(0xF7);
                    if (!Hid.HidD_SetFeature(conn.Handle, req, req.Length)) { Log(string.Format("yichip {0:X4}: SetFeature failed", iface.Pid)); continue; }
                    byte[] r = null;
                    for (int attempt = 0; attempt < 5 && r == null; attempt++)
                    {
                        Thread.Sleep(100);
                        var b = new byte[65];
                        if (!Hid.HidD_GetFeature(conn.Handle, b, b.Length)) continue;
                        Log(string.Format("yichip {0:X4} reply: {1}", iface.Pid, BitConverter.ToString(b, 0, 16)));
                        var data = b.Skip(1).ToArray();
                        // Справжня відповідь ресивера: тип у [6] — 1, 2 або 3 (інакше це не ресивер / застаріла відповідь).
                        if (data[0] != 0xF7 && data[6] >= 1 && data[6] <= 3) r = data;
                    }
                    if (r == null) { Log(string.Format("yichip {0:X4}: no receiver status (cable keyboard?)", iface.Pid)); continue; }

                    bool hasKb = r[6] == 1 || r[6] == 3, hasMouse = r[6] == 2 || r[6] == 3;
                    Log(string.Format("yichip {0:X4}: kb={1}% online={2}  mouse={3}% online={4}  type={5}",
                        iface.Pid, r[1], r[3] == 0, r[2], r[4] == 0, r[6]));
                    string product = iface.Product ?? "2.4G";
                    if (hasKb && r[3] == 0 && r[1] >= 1 && r[1] <= 100)
                        result.Add(new BatteryDevice
                        {
                            Container = Logitech.StableGuid("yc|" + iface.Path),
                            Name = "Attack Shark " + product,
                            Percent = r[1],
                            Kind = DeviceKind.Keyboard
                        });
                    if (hasMouse && r[4] == 0 && r[2] >= 1 && r[2] <= 100)
                        result.Add(new BatteryDevice
                        {
                            Container = Logitech.StableGuid("yc-mouse|" + iface.Path),
                            Name = "Attack Shark mouse (" + product + ")",
                            Percent = r[2],
                            Kind = DeviceKind.Mouse
                        });
                }
            }
            return result;
        }

        // ─────────────────────── 2. Мишки Beken (VID 0x1D57) ───────────────────────

        class MouseState { public int Percent = -1; public bool Charging; public string Product; public DateTime At; }

        static readonly object _lock = new object();
        static readonly Dictionary<string, MouseState> _mice = new Dictionary<string, MouseState>();
        static readonly HashSet<string> _listening = new HashSet<string>();

        static List<BatteryDevice> ScanBekenMice()
        {
            // Слухаємо всі вендорські/системні колекції ресивера (не клавіатуру й не мишу —
            // їх Windows не дає читати). Репорт заряду має ID 3 і 5+ байт.
            var ifaces = Hid.Enumerate(v => v == 0x1D57)
                .Where(i => i.InputLen >= 5 && !(i.UsagePage == 0x0001 && (i.Usage == 0x0002 || i.Usage == 0x0006)))
                .ToList();
            foreach (var iface in ifaces)
            {
                lock (_lock)
                {
                    if (_listening.Contains(iface.Path)) continue;
                    _listening.Add(iface.Path);
                }
                Log(string.Format("beken {0:X4}: listening page={1:X4} usage={2:X4} '{3}'", iface.Pid, iface.UsagePage, iface.Usage, iface.Product));
                var t = new Thread(() => Listen(iface)) { IsBackground = true, Name = "BekenListen" };
                t.Start();
            }

            var result = new List<BatteryDevice>();
            lock (_lock)
            {
                foreach (var kv in _mice)
                {
                    var m = kv.Value;
                    // Мишка шле заряд періодично; значення старше 15 хв вважаємо неактуальним.
                    if (m.Percent < 0 || DateTime.Now - m.At > TimeSpan.FromMinutes(15)) continue;
                    result.Add(new BatteryDevice
                    {
                        Container = Logitech.StableGuid("beken|" + kv.Key),
                        Name = "Attack Shark " + (string.IsNullOrEmpty(m.Product) || m.Product.Contains("Beken") ? "mouse" : m.Product),
                        Percent = m.Percent,
                        Charging = m.Charging,
                        Kind = DeviceKind.Mouse
                    });
                }
            }
            if (ifaces.Count > 0 && result.Count == 0) Log("beken: receiver found, waiting for the mouse to report battery");
            return result;
        }

        static void Listen(HidInterface iface)
        {
            try
            {
                var conn = Hid.OpenRead(iface);
                if (conn == null) { Log(string.Format("beken {0:X4}: cannot open {1:X4}/{2:X4}", iface.Pid, iface.UsagePage, iface.Usage)); return; }
                using (conn)
                {
                    int silentErrors = 0;
                    while (true)
                    {
                        var r = conn.Read(60000);
                        if (r == null)
                        {
                            // Тайм-аут — нормально (мишка мовчить). Перевіряємо, чи пристрій ще існує.
                            if (!File.Exists(iface.Path) && ++silentErrors > 2) break;
                            continue;
                        }
                        silentErrors = 0;
                        int pct; bool charging;
                        if (TryParseMouseBattery(r, out pct, out charging))
                        {
                            Log(string.Format("beken {0:X4}: {1} -> {2}% charging={3}", iface.Pid, BitConverter.ToString(r, 0, 5), pct, charging));
                            lock (_lock)
                                _mice[DeviceKey(iface)] = new MouseState { Percent = pct, Charging = charging, Product = iface.Product, At = DateTime.Now };
                        }
                    }
                }
            }
            catch (Exception e) { Log("beken listen: " + e.Message); }
            finally { lock (_lock) _listening.Remove(iface.Path); }
        }

        // Один ресивер = одна мишка; її заряд може прийти в різні колекції — групуємо за VID:PID.
        static string DeviceKey(HidInterface i)
        {
            return string.Format("{0:X4}:{1:X4}", i.Vid, i.Pid);
        }

        // 03 <id> 40|41 <стан> <заряд>
        internal static bool TryParseMouseBattery(byte[] r, out int pct, out bool charging)
        {
            pct = -1; charging = false;
            if (r.Length < 5 || r[0] != 0x03 || (r[2] != 0x40 && r[2] != 0x41)) return false;
            int raw = r[4];
            if (raw <= 10) pct = raw * 10;          // X6/X11/R1: рівень 0..10
            else if (raw <= 100) pct = raw;          // X8 SE/X8 Plus/0x55-варіант X11: відсотки
            else return false;
            byte state = r[3];
            // 0x02 — заряджається (X11, X8 Plus, R1-дротовий), 0x03 — від кабелю/база, 0x80 — док-станція R1.
            charging = state == 0x02 || state == 0x03 || state == 0x80;
            return true;
        }

        // ─────────────────────── 3. X87 (0x3554:0xFA09) ───────────────────────

        static byte[] X87Packet(byte cmd)
        {
            var p = new byte[20];
            p[0] = 0x13; p[1] = cmd; p[2] = 1; // total = 1, index = 0, sub/len = 0
            int s = 0;
            for (int i = 0; i < 19; i++) s += p[i];
            p[19] = (byte)(s & 0xFF);
            return p;
        }

        static byte[] X87Query(HidConnection c, byte cmd, int timeoutMs)
        {
            if (!c.Write(X87Packet(cmd))) return null;
            int deadline = Environment.TickCount + timeoutMs;
            while (true)
            {
                int left = deadline - Environment.TickCount;
                if (left <= 0) return null;
                var r = c.Read(left);
                if (r == null) return null;
                if (r.Length >= 20 && r[0] == 0x13 && r[1] == cmd) return r;
            }
        }

        static List<BatteryDevice> ScanX87()
        {
            var result = new List<BatteryDevice>();
            foreach (var iface in Hid.Enumerate(v => v == 0x3554)
                         .Where(i => i.Pid == 0xFA09 && i.UsagePage == 0xFF02 && i.Usage == 0x0002 && i.OutputLen == 20))
            {
                var conn = Hid.Open(iface);
                if (conn == null) { Log("x87: cannot open " + iface.Path); continue; }
                using (conn)
                {
                    var link = X87Query(conn, 0x07, 600);
                    if (link == null) { Log("x87: no link reply"); continue; }
                    if (link[5] == 0) { Log("x87: keyboard asleep/off"); continue; }
                    var bat = X87Query(conn, 0x4A, 2500);
                    if (bat == null) { Log("x87: no battery reply"); continue; }
                    int pct = Math.Min(100, (int)bat[5]);
                    bool charging = (bat[6] & 0x10) != 0;
                    Log(string.Format("x87: battery={0} charging={1}", pct, charging));
                    if (pct <= 0) continue;
                    result.Add(new BatteryDevice
                    {
                        Container = Logitech.StableGuid("x87|" + iface.Path),
                        Name = "Attack Shark X87",
                        Percent = pct,
                        Charging = charging,
                        Kind = DeviceKind.Keyboard
                    });
                }
            }
            return result;
        }
    }
}

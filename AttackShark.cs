// Заряд 2.4G-клавіатур на ресиверах VID 0x3151 (Attack Shark та ін., протокол YiChip/"sharkfin").
// Шле ЛИШЕ команду статусу ресивера 0xF7 (читання): byte1 = заряд клави %, byte3 = 0 коли клава онлайн.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BatteryTray
{
    static class AttackShark
    {
        public static Action<string> Log = delegate { };

        static byte[] Frame(byte op)
        {
            var f = new byte[65]; // [0] = report ID 0, далі 64 байти
            f[1] = op;
            int sum = 0;
            for (int i = 1; i <= 7; i++) sum += f[i];
            f[8] = (byte)(0xFF - (sum & 0xFF));
            return f;
        }

        public static List<BatteryDevice> Scan()
        {
            var result = new List<BatteryDevice>();
            foreach (var iface in Hid.Enumerate(v => v == 0x3151).Where(i => i.UsagePage == 0xFFFF && i.Usage == 0x0002 && i.FeatureLen == 65))
            {
                var conn = Hid.Open(iface);
                if (conn == null) { Log("cannot open " + iface.Path); continue; }
                using (conn)
                {
                    var req = Frame(0xF7);
                    if (!Hid.HidD_SetFeature(conn.Handle, req, req.Length)) { Log("SetFeature failed"); continue; }
                    byte[] r = null;
                    for (int attempt = 0; attempt < 5 && r == null; attempt++)
                    {
                        Thread.Sleep(100);
                        var b = new byte[65];
                        if (!Hid.HidD_GetFeature(conn.Handle, b, b.Length)) continue;
                        Log("reply: " + BitConverter.ToString(b, 0, 16));
                        if (b[1] != 0xF7) r = b.Skip(1).ToArray(); // відповідь ресивера не повторює опкод
                    }
                    if (r == null) continue;
                    int pct = r[1];
                    bool online = r[3] == 0;
                    Log(string.Format("{0:X4}: battery={1} online={2} type={3}", iface.Pid, pct, online, r[6]));
                    if (!online || pct == 0 || pct > 100) continue;
                    result.Add(new BatteryDevice
                    {
                        Container = Logitech.StableGuid("yc|" + iface.Path),
                        Name = "Attack Shark " + (iface.Product ?? "keyboard"),
                        Percent = pct,
                        Kind = DeviceKind.Keyboard
                    });
                }
            }
            return result;
        }
    }
}

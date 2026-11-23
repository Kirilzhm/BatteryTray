// BatteryTray: показує заряд Bluetooth-мишки, клавіатури та навушників у треї.
// Компілюється вбудованим csc.exe з .NET Framework 4 (див. build.ps1).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryTray
{
    enum DeviceKind { Mouse, Keyboard, Headphones, Other }

    class BatteryDevice
    {
        public Guid Container;
        public string Name;
        public int Percent;
        public DeviceKind Kind;
        public bool Charging;
    }

    static class DeviceScanner
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct DEVPROPKEY { public Guid fmtid; public uint pid; public DEVPROPKEY(string g, uint p) { fmtid = new Guid(g); pid = p; } }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key,
            out uint propType, byte[] buffer, int bufferSize, out int requiredSize, int flags);

        const int DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;

        static DEVPROPKEY KeyBattery = new DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
        static DEVPROPKEY KeyConnected = new DEVPROPKEY("83DA6326-97A6-4088-9453-A1923F573B29", 15);
        static DEVPROPKEY KeyFriendlyName = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
        static DEVPROPKEY KeyDeviceDesc = new DEVPROPKEY("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
        static DEVPROPKEY KeyContainer = new DEVPROPKEY("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);

        static readonly Guid ClassMouse = new Guid("4d36e96f-e325-11ce-bfc1-08002be10318");
        static readonly Guid ClassKeyboard = new Guid("4d36e96b-e325-11ce-bfc1-08002be10318");
        static readonly Guid ClassMedia = new Guid("4d36e96c-e325-11ce-bfc1-08002be10318");
        static readonly Guid ClassAudioEndpoint = new Guid("c166523c-fe0c-4a94-a586-f1a80cfbbf3e");

        static byte[] GetProp(IntPtr set, ref SP_DEVINFO_DATA d, DEVPROPKEY key)
        {
            uint type; int req;
            byte[] buf = new byte[512];
            if (!SetupDiGetDevicePropertyW(set, ref d, ref key, out type, buf, buf.Length, out req, 0)) return null;
            if (req < buf.Length) Array.Resize(ref buf, req);
            return buf;
        }

        static string GetString(IntPtr set, ref SP_DEVINFO_DATA d, DEVPROPKEY key)
        {
            byte[] b = GetProp(set, ref d, key);
            if (b == null) return null;
            return Encoding.Unicode.GetString(b).TrimEnd('\0');
        }

        public static List<BatteryDevice> Scan()
        {
            var result = new Dictionary<Guid, BatteryDevice>();
            var classesByContainer = new Dictionary<Guid, HashSet<Guid>>();

            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == new IntPtr(-1)) return new List<BatteryDevice>();
            try
            {
                var d = new SP_DEVINFO_DATA();
                d.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
                {
                    byte[] c = GetProp(set, ref d, KeyContainer);
                    Guid container = (c != null && c.Length >= 16) ? new Guid(c.Take(16).ToArray()) : Guid.Empty;
                    if (container != Guid.Empty)
                    {
                        HashSet<Guid> cls;
                        if (!classesByContainer.TryGetValue(container, out cls)) classesByContainer[container] = cls = new HashSet<Guid>();
                        cls.Add(d.ClassGuid);
                    }

                    byte[] bat = GetProp(set, ref d, KeyBattery);
                    if (bat == null || bat.Length < 1) continue;
                    int pct = bat[0];
                    if (pct > 100) continue;

                    byte[] conn = GetProp(set, ref d, KeyConnected);
                    if (conn != null && conn.Length >= 1 && conn[0] == 0) continue; // спарений, але не підключений

                    string name = GetString(set, ref d, KeyFriendlyName) ?? GetString(set, ref d, KeyDeviceDesc) ?? "Пристрій";
                    Guid key = container != Guid.Empty ? container : Guid.NewGuid();
                    BatteryDevice existing;
                    if (result.TryGetValue(key, out existing))
                    {
                        if (name.Length < existing.Name.Length) existing.Name = name; // коротше ім'я зазвичай "чистіше"
                        continue;
                    }
                    result[key] = new BatteryDevice { Container = key, Name = name, Percent = pct };
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }

            foreach (var dev in result.Values)
            {
                HashSet<Guid> cls;
                classesByContainer.TryGetValue(dev.Container, out cls);
                dev.Kind = Classify(dev.Name, cls ?? new HashSet<Guid>());
            }
            return result.Values.OrderBy(x => (int)x.Kind).ThenBy(x => x.Name).ToList();
        }

        static DeviceKind Classify(string name, HashSet<Guid> cls)
        {
            string n = name.ToLowerInvariant();
            string[] audio = { "headphone", "headset", "buds", "airpods", "earbud", "навуш", "wh-", "wf-", "jbl", "audio", "hands-free", "ear", "pods", "beats", "soundcore" };
            string[] mouse = { "mouse", "миш", "mx master", "mx anywhere", "trackball" };
            string[] kb = { "keyboard", "клав", "keys", "kb" };
            if (mouse.Any(n.Contains)) return DeviceKind.Mouse;
            if (kb.Any(n.Contains)) return DeviceKind.Keyboard;
            if (audio.Any(n.Contains)) return DeviceKind.Headphones;
            if (cls.Contains(ClassAudioEndpoint) || cls.Contains(ClassMedia)) return DeviceKind.Headphones;
            if (cls.Contains(ClassMouse) && !cls.Contains(ClassKeyboard)) return DeviceKind.Mouse;
            if (cls.Contains(ClassKeyboard)) return DeviceKind.Keyboard;
            if (cls.Contains(ClassMouse)) return DeviceKind.Mouse;
            return DeviceKind.Other;
        }
    }

}

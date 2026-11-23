// Низькорівневий доступ до HID-пристроїв (переліку інтерфейсів, читання/запис репортів).
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BatteryTray
{
    class HidInterface
    {
        public string Path;
        public int Vid, Pid;
        public int UsagePage, Usage;
        public int InputLen, OutputLen, FeatureLen;
        public string Product;
    }

    static class Hid
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 27)] public ushort[] Rest;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

        static Guid HidGuid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
        const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, SHARE_RW = 3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

        public static List<HidInterface> Enumerate(Func<int, bool> vidFilter)
        {
            var list = new List<HidInterface>();
            IntPtr set = SetupDiGetClassDevsW(ref HidGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return list;
            try
            {
                var d = new SP_DEVICE_INTERFACE_DATA();
                d.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref HidGuid, i, ref d); i++)
                {
                    int req;
                    SetupDiGetDeviceInterfaceDetailW(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                    if (req <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(req);
                    string path;
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetailW(set, ref d, buf, req, out req, IntPtr.Zero)) continue;
                        path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                    }
                    finally { Marshal.FreeHGlobal(buf); }

                    using (var h = CreateFileW(path, 0, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                    {
                        if (h.IsInvalid) continue;
                        var a = new HIDD_ATTRIBUTES(); a.Size = Marshal.SizeOf(a);
                        if (!HidD_GetAttributes(h, ref a) || !vidFilter(a.VendorID)) continue;
                        var info = new HidInterface { Path = path, Vid = a.VendorID, Pid = a.ProductID };
                        IntPtr pp;
                        if (HidD_GetPreparsedData(h, out pp))
                        {
                            HIDP_CAPS caps;
                            HidP_GetCaps(pp, out caps);
                            HidD_FreePreparsedData(pp);
                            info.UsagePage = caps.UsagePage; info.Usage = caps.Usage;
                            info.InputLen = caps.InputReportByteLength; info.OutputLen = caps.OutputReportByteLength;
                            info.FeatureLen = caps.FeatureReportByteLength;
                        }
                        var pb = new byte[256];
                        if (HidD_GetProductString(h, pb, pb.Length))
                            info.Product = System.Text.Encoding.Unicode.GetString(pb).Split('\0')[0];
                        list.Add(info);
                    }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        public static HidConnection Open(HidInterface i)
        {
            var h = CreateFileW(i.Path, GENERIC_READ | GENERIC_WRITE, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); return null; }
            return new HidConnection(h, i);
        }

        // Для пасивного прослуховування: якщо запис заборонено, відкриваємо лише на читання.
        public static HidConnection OpenRead(HidInterface i)
        {
            var c = Open(i);
            if (c != null) return c;
            var h = CreateFileW(i.Path, GENERIC_READ, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); return null; }
            return new HidConnection(h, i, FileAccess.Read);
        }
    }

    class HidConnection : IDisposable
    {
        [DllImport("kernel32.dll")] static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);

        public readonly SafeFileHandle Handle;
        public readonly HidInterface Info;
        readonly FileStream _fs;
        IAsyncResult _pending;
        byte[] _pendingBuf;

        public HidConnection(SafeFileHandle h, HidInterface info) : this(h, info, FileAccess.ReadWrite) { }

        public HidConnection(SafeFileHandle h, HidInterface info, FileAccess access)
        {
            Handle = h; Info = info;
            _fs = new FileStream(h, access, 1, true);
        }

        public bool Write(byte[] report)
        {
            var buf = new byte[Info.OutputLen];
            Array.Copy(report, buf, Math.Min(report.Length, buf.Length));
            try { _fs.Write(buf, 0, buf.Length); return true; }
            catch { return false; }
        }

        // Повертає вхідний репорт або null, якщо за timeoutMs нічого не прийшло.
        public byte[] Read(int timeoutMs)
        {
            try
            {
                if (_pending == null)
                {
                    _pendingBuf = new byte[Info.InputLen];
                    _pending = _fs.BeginRead(_pendingBuf, 0, _pendingBuf.Length, null, null);
                }
                if (!_pending.AsyncWaitHandle.WaitOne(timeoutMs)) return null;
                int n = _fs.EndRead(_pending);
                _pending = null;
                if (n <= 0) return null;
                var r = new byte[n];
                Array.Copy(_pendingBuf, r, n);
                return r;
            }
            catch { _pending = null; return null; }
        }

        public void Dispose()
        {
            if (_pending != null) { CancelIoEx(Handle, IntPtr.Zero); try { _fs.EndRead(_pending); } catch { } }
            try { _fs.Dispose(); } catch { }
            Handle.Dispose();
        }
    }
}

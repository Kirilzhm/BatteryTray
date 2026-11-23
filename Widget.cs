// Віджет, вбудований прямо в панель задач Windows (зліва від області трею): [значок] 85%  [значок] 61% ...
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryTray
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int W, H; public SIZE(int w, int h) { W = w; H = h; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
        [StructLayout(LayoutKind.Sequential)] public struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwnd; public uint dwHoverTime; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("dwmapi.dll")] public static extern int DwmFlush();
        [DllImport("user32.dll")] public static extern IntPtr LoadCursorW(IntPtr inst, IntPtr id);
        [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr c);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hdcDst, IntPtr pptDst, ref SIZE size,
            IntPtr hdcSrc, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string s);
    }

    class TaskbarWidget : NativeWindow, IDisposable
    {
        const int WS_POPUP = unchecked((int)0x80000000);
        const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        const int WM_MOUSEMOVE = 0x200, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_MOUSELEAVE = 0x2A3, WM_MOUSEACTIVATE = 0x21;
        const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

        // За замовчуванням віджет — дочірнє вікно панелі задач: живе в її шарі, тож його видно
        // і при відкритому "Пуск" (Win). Старий режим окремого вікна: BATTERYTRAY_MODE=popup.
        public static readonly bool ChildMode = Environment.GetEnvironmentVariable("BATTERYTRAY_MODE") != "popup";
        public static string DumpPath = Environment.GetEnvironmentVariable("BATTERYTRAY_DUMP");
        public List<BatteryDevice> Devices = new List<BatteryDevice>();
        // Після першого сканування: якщо пристроїв немає — показуємо "—", щоб було видно, що програма працює.
        public bool ShowPlaceholder;
        bool HasContent { get { return Devices.Count > 0 || ShowPlaceholder; } }
        public ContextMenuStrip Menu;
        public Action LeftClick = delegate { };

        IntPtr _taskbar = IntPtr.Zero;
        Native.RECT _lastTray, _lastBar;
        int _w = 1, _h = 1;
        bool _tracking;
        readonly TipForm _tip = new TipForm();
        readonly System.Windows.Forms.Timer _watch = new System.Windows.Forms.Timer();
        static FontFamily _iconFamily;

        public TaskbarWidget()
        {
            _watch.Interval = 15; // видимість/z-порядок; плавний рух — у FollowLoop
            _watch.Tick += delegate { EnsureAttached(); };
            _watch.Start();
            EnsureAttached();
            if (!ChildMode) {
            var t = new Thread(FollowLoop) { IsBackground = true, Name = "TaskbarFollow", Priority = ThreadPriority.AboveNormal };
            t.Start();
            }
        }

        // Окремий потік, синхронізований з кадрами екрана (DwmFlush). Він показує/ховає віджет і веде
        // вертикальний рух: реагує вже на першому кадрі руху панелі, а при ховані прогнозує позицію
        // між її кроками (Windows рухає вікно панелі ~30 разів/с, плавна лише її картинка).
        volatile bool _alive = true;
        volatile IntPtr _hwnd, _bar;
        volatile int _followX = int.MinValue, _screenTop, _screenBottom;
        volatile bool _enabled, _visibleNow;

        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_HIDEWINDOW = 0x80, SWP_ASYNC = 0x4000;

        void FollowLoop()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double curY = 0, v = 0, lastRectT = 0;
            int lastRect = int.MinValue, lastSet = int.MinValue;
            bool shown = false;
            while (_alive)
            {
                IntPtr h = _hwnd, bar = _bar;
                Native.RECT r;
                if (h == IntPtr.Zero || bar == IntPtr.Zero || _followX == int.MinValue || !Native.GetWindowRect(bar, out r))
                {
                    Thread.Sleep(30);
                    continue;
                }
                int visible = Math.Min(r.Bottom, _screenBottom) - Math.Max(r.Top, _screenTop);
                bool want = _enabled && visible > 2 && Native.IsWindowVisible(bar);

                if (want && !shown)
                {
                    curY = r.Top; lastRect = r.Top; lastRectT = sw.Elapsed.TotalMilliseconds; v = 0; lastSet = r.Top;
                    Native.SetWindowPos(h, new IntPtr(-1) /*TOPMOST*/, _followX, r.Top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_ASYNC);
                    shown = true; _visibleNow = true;
                    if (LogPath != null) Debug("show y=" + r.Top);
                }
                else if (!want && shown)
                {
                    Native.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW | SWP_ASYNC);
                    shown = false; _visibleNow = false;
                    if (LogPath != null) Debug("hide");
                }
                else if (shown)
                {
                    double now = sw.Elapsed.TotalMilliseconds;
                    if (r.Top != lastRect)
                    {
                        v = (r.Top > lastRect && now - lastRectT < 150) ? (r.Top - lastRect) / (now - lastRectT) : 0;
                        lastRect = r.Top; lastRectT = now;
                    }
                    double target = lastRect;
                    if (v > 0 && now - lastRectT < 80) target = lastRect + v * Math.Min(now - lastRectT, 45);
                    if (target < curY) curY = target;                         // вгору — одразу, як сама панель
                    else curY = v > 0 ? Math.Max(curY, target) : target;     // вниз — плавно з прогнозом

                    int y = (int)Math.Round(curY);
                    if (y != lastSet)
                    {
                        Native.SetWindowPos(h, IntPtr.Zero, _followX, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNC);
                        lastSet = y;
                    }
                }

                if (shown || visible > 2) { if (Native.DwmFlush() != 0) Thread.Sleep(8); }
                else Thread.Sleep(4); // панель схована: часта, але дешева перевірка
            }
        }

        // Звичайне вікно "поверх усіх" (без прив'язки до панелі, інакше Windows блокує його
        // на час анімації Explorer). Перестворюється після перезапуску Explorer.
        void EnsureAttached()
        {
            if (_taskbar == IntPtr.Zero || !Native.IsWindow(_taskbar) || Handle == IntPtr.Zero)
            {
                if (Handle != IntPtr.Zero) DestroyHandle();
                _taskbar = Native.FindWindowW("Shell_TrayWnd", null);
                if (_taskbar == IntPtr.Zero) return;
                var cp = ChildMode
                    ? new CreateParams // дочірнє вікно панелі: живе в її шарі (видно і з відкритим "Пуск")
                    {
                        Caption = "BatteryTrayWidget",
                        Style = 0x40000000 /*CHILD*/ | 0x04000000 /*CLIPSIBLINGS*/,
                        ExStyle = WS_EX_LAYERED | WS_EX_NOACTIVATE,
                        Parent = _taskbar, X = 0, Y = 0, Width = 1, Height = 1
                    }
                    : new CreateParams
                    {
                        Caption = "BatteryTrayWidget",
                        Style = WS_POPUP,
                        ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | 0x8 /*TOPMOST*/,
                        X = 0, Y = 0, Width = 1, Height = 1
                    };
                CreateHandle(cp);
                _lastTray = new Native.RECT();
                Render();
                return;
            }
            Native.RECT tray, bar;
            if (!TrayRect(out tray) || !Native.GetWindowRect(_taskbar, out bar)) return;
            if (tray.Left - tray.Right != _lastTray.Left - _lastTray.Right || tray.Left - bar.Left != _lastTray.Left - _lastBar.Left
                || bar.Bottom - bar.Top != _lastBar.Bottom - _lastBar.Top)
                Render();
            else
                Place(tray, bar);
        }

        int _posX = int.MinValue;

        // Передає потоку FollowLoop координату x і чи є що показувати; тримає віджет над панеллю.
        void Place(Native.RECT tray, Native.RECT bar)
        {
            _lastTray = tray; _lastBar = bar;
            if (ChildMode)
            {
                // Координати відносно панелі; рухається й ховається разом із нею сам.
                int cx = tray.Left - bar.Left - _w - (int)(2 * (_h / 48f));
                bool want = HasContent;
                bool top = Native.GetWindow(_taskbar, 5 /*GW_CHILD*/) == Handle;
                if (!want) { if (_posX != int.MinValue) { Native.ShowWindow(Handle, 0); _posX = int.MinValue; _tip.Hide(); } return; }
                if (cx != _posX || !top)
                {
                    Native.SetWindowPos(Handle, IntPtr.Zero /*HWND_TOP*/, cx, 0, _w, _h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                    _posX = cx;
                }
                return;
            }
            var screen = Screen.FromHandle(_taskbar).Bounds;
            int x = tray.Left - _w - (int)(2 * (_h / 48f));
            _screenTop = screen.Top; _screenBottom = screen.Bottom;
            _hwnd = Handle; _bar = _taskbar; _followX = x;
            _enabled = HasContent;
            if (!_visibleNow) { _tip.Hide(); _posX = int.MinValue; return; }
            if (x != _posX || IsAbove(_taskbar, Handle))
            {
                Native.RECT me; Native.GetWindowRect(Handle, out me);
                Native.SetWindowPos(Handle, new IntPtr(-1), x, me.Top, _w, _h, SWP_NOACTIVATE | SWP_ASYNC);
            }
            _posX = x;
        }

        // true, якщо вікно a лежить вище за b у z-порядку.
        static bool IsAbove(IntPtr a, IntPtr b)
        {
            for (IntPtr h = Native.GetWindow(b, 3 /*GW_HWNDPREV*/); h != IntPtr.Zero; h = Native.GetWindow(h, 3))
                if (h == a) return true;
            return false;
        }

        public static string LogPath = Environment.GetEnvironmentVariable("BATTERYTRAY_LOG");
        static void Debug(string msg)
        {
            if (LogPath == null) return;
            try { System.IO.File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff ") + msg + Environment.NewLine); } catch { }
        }

        bool TrayRect(out Native.RECT r)
        {
            r = new Native.RECT();
            IntPtr tray = Native.FindWindowExW(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            return tray != IntPtr.Zero && Native.GetWindowRect(tray, out r);
        }

        static FontFamily IconFamily()
        {
            if (_iconFamily != null) return _iconFamily;
            using (var fonts = new InstalledFontCollection())
                foreach (string f in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
                    if (fonts.Families.Any(ff => ff.Name == f)) return _iconFamily = new FontFamily(f);
            return _iconFamily = FontFamily.GenericSansSerif;
        }

        // Ширина значка для заданого масштабу.
        static float GlyphWidth(DeviceKind k, float s)
        {
            switch (k)
            {
                case DeviceKind.Mouse: return 11 * s;
                case DeviceKind.Keyboard: return 19 * s;
                case DeviceKind.Headphones: return 16 * s;
                default: return 19 * s;
            }
        }

        static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            float d = r * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Мінімалістичні контурні значки, намальовані вектором.
        static void DrawGlyph(Graphics g, DeviceKind k, float x, float cy, float s, Color c)
        {
            float lw = 1.6f * s;
            using (var pen = new Pen(c, lw) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var br = new SolidBrush(c))
            {
                switch (k)
                {
                    case DeviceKind.Mouse:
                    {
                        // Миша, вид зверху: капсула + розділ між кнопками.
                        float w = 10 * s, h = 15 * s, top = cy - h / 2, left = x + lw / 2;
                        using (var p = RoundRect(left, top, w, h, w / 2)) g.DrawPath(pen, p);
                        g.DrawLine(pen, left + w / 2, top + 0.5f * lw, left + w / 2, top + h * 0.36f);
                        break;
                    }
                    case DeviceKind.Keyboard:
                    {
                        // Клавіатура: рамка, ряд "клавіш" і пробіл.
                        float w = 18 * s, h = 11 * s, top = cy - h / 2, left = x + lw / 2;
                        using (var p = RoundRect(left, top, w, h, 2.2f * s)) g.DrawPath(pen, p);
                        float dot = 1.6f * s, rowY = top + 3.4f * s;
                        for (int i = 0; i < 4; i++)
                            g.FillEllipse(br, left + 3.3f * s + i * 3.8f * s - dot / 2, rowY - dot / 2, dot, dot);
                        g.DrawLine(pen, left + 5.5f * s, top + h - 3.3f * s, left + w - 5.5f * s, top + h - 3.3f * s);
                        break;
                    }
                    case DeviceKind.Headphones:
                    {
                        // Навушники: дуга + дві чашки.
                        float w = 15 * s, h = 14 * s, top = cy - h / 2, left = x + lw / 2;
                        g.DrawArc(pen, left, top, w, w, 180, 180);
                        g.DrawLine(pen, left, top + w / 2, left, top + h - 4.5f * s);
                        g.DrawLine(pen, left + w, top + w / 2, left + w, top + h - 4.5f * s);
                        float cw = 3.6f * s, ch = 6 * s;
                        using (var p = RoundRect(left - lw / 2, top + h - ch, cw, ch, 1.3f * s)) g.FillPath(br, p);
                        using (var p = RoundRect(left + w + lw / 2 - cw, top + h - ch, cw, ch, 1.3f * s)) g.FillPath(br, p);
                        break;
                    }
                    default:
                    {
                        // Батарейка: корпус + "плюсик" справа.
                        float w = 16 * s, h = 9 * s, top = cy - h / 2, left = x + lw / 2;
                        using (var p = RoundRect(left, top, w, h, 2 * s)) g.DrawPath(pen, p);
                        using (var p = RoundRect(left + w + 0.6f * s, cy - 2.2f * s, 1.8f * s, 4.4f * s, 0.8f * s)) g.FillPath(br, p);
                        break;
                    }
                }
            }
        }

        static bool LightTaskbar()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                return v is int && (int)v == 1;
            }
        }

        public void Render()
        {
            if (Handle == IntPtr.Zero || _taskbar == IntPtr.Zero) return;
            Native.RECT tray, bar;
            if (!TrayRect(out tray) || !Native.GetWindowRect(_taskbar, out bar)) return;
            _lastTray = tray; _lastBar = bar;

            if (!HasContent) { Place(tray, bar); return; }
            bool placeholder = Devices.Count == 0;
            var shown = placeholder ? new List<BatteryDevice> { new BatteryDevice { Kind = DeviceKind.Other, Percent = -1 } } : Devices;

            _h = bar.Bottom - bar.Top;
            float scale = _h / 48f;
            Color fg = LightTaskbar() ? Color.FromArgb(25, 25, 25) : Color.White;

            using (var textFont = new Font("Segoe UI Semibold", 13.5f * scale, GraphicsUnit.Pixel))
            using (var measureBmp = new Bitmap(1, 1))
            using (var mg = Graphics.FromImage(measureBmp))
            {
                var fmt = StringFormat.GenericTypographic;
                float pad = 8 * scale, gapIcon = 6 * scale, gapItem = 16 * scale;
                var items = shown.Select(d => new
                {
                    Dev = d,
                    Text = d.Percent < 0 ? "—" : d.Percent + "%",
                    GW = GlyphWidth(d.Kind, scale) + 1.6f * scale,
                    TW = mg.MeasureString(d.Percent < 0 ? "—" : d.Percent + "%", textFont, 1000, fmt).Width
                }).ToList();
                if (placeholder) fg = Color.FromArgb(150, fg); // приглушено
                float total = pad * 2 + items.Sum(i => i.GW + gapIcon + i.TW) + gapItem * (items.Count - 1);
                _w = (int)Math.Ceiling(total);

                using (var bmp = new Bitmap(_w, _h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.FromArgb(1, 0, 0, 0)); // майже прозорий фон, щоб ловити мишку
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        float x = pad;
                        foreach (var it in items)
                        {
                            Color tc = it.Dev.Charging ? Color.FromArgb(40, 200, 90)
                                     : it.Dev.Percent <= 15 ? Color.FromArgb(240, 60, 60)
                                     : it.Dev.Percent <= 30 ? Color.FromArgb(255, 150, 20) : fg;
                            var ts = mg.MeasureString(it.Text, textFont, 1000, fmt);
                            DrawGlyph(g, it.Dev.Kind, x, _h / 2f, scale, fg);
                            x += it.GW + gapIcon;
                            using (var b2 = new SolidBrush(tc)) g.DrawString(it.Text, textFont, b2, x, (_h - ts.Height) / 2f - 0.5f * scale, fmt);
                            x += it.TW + gapItem;
                        }
                    }
                    Present(bmp);
                    _posX = int.MinValue;
                    Place(tray, bar);
                    if (DumpPath != null) try { bmp.Save(DumpPath); } catch { }
                }
            }
        }

        void Present(Bitmap bmp)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr mem = Native.CreateCompatibleDC(screen);
            IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Native.SelectObject(mem, hbmp);
            try
            {
                var size = new Native.SIZE(bmp.Width, bmp.Height);
                var src = new Native.POINT(0, 0);
                var blend = new Native.BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
                bool ok = Native.UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, 2);
                Debug("ULW " + size.W + "x" + size.H + " ok=" + ok + " err=" + Marshal.GetLastWin32Error());
            }
            finally
            {
                Native.SelectObject(mem, old);
                Native.DeleteObject(hbmp);
                Native.DeleteDC(mem);
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        string TooltipText()
        {
            if (Devices.Count == 0)
                return "BatteryTray: підтримуваних пристроїв не знайдено.\nПравий клік → «Звіт про пристрої»";
            return string.Join("\n", Devices.Select(d => d.Name + ": " + d.Percent + "%" + (d.Charging ? " ⚡ заряджається" : "")));
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case 0x20: // WM_SETCURSOR — звичайна стрілка замість "завантаження"
                    Native.SetCursor(Native.LoadCursorW(IntPtr.Zero, new IntPtr(32512) /*IDC_ARROW*/));
                    m.Result = (IntPtr)1;
                    return;
                case WM_MOUSEACTIVATE:
                    m.Result = (IntPtr)3; // MA_NOACTIVATE
                    return;
                case WM_MOUSEMOVE:
                    if (!_tracking)
                    {
                        var t = new Native.TRACKMOUSEEVENT { cbSize = Marshal.SizeOf(typeof(Native.TRACKMOUSEEVENT)), dwFlags = 2, hwnd = Handle };
                        Native.TrackMouseEvent(ref t);
                        _tracking = true;
                        Native.RECT r; Native.GetWindowRect(Handle, out r);
                        _tip.ShowAt(TooltipText(), new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
                    }
                    break;
                case WM_MOUSELEAVE:
                    _tracking = false;
                    _tip.Hide();
                    break;
                case WM_LBUTTONUP:
                    LeftClick();
                    break;
                case WM_RBUTTONUP:
                    _tip.Hide();
                    if (Menu != null) Menu.Show(Cursor.Position);
                    break;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            _alive = false;
            _watch.Stop();
            _tip.Close();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    // Невелика підказка над віджетом у стилі Windows 11.
    class TipForm : Form
    {
        readonly Label _label = new Label { AutoSize = true, Font = new Font("Segoe UI", 9f), Padding = new Padding(8, 6, 8, 6) };

        public TipForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Color.FromArgb(44, 44, 44);
            _label.ForeColor = Color.White;
            Controls.Add(_label);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; } // TOOLWINDOW | NOACTIVATE
        }

        public void ShowAt(string text, Rectangle anchor)
        {
            _label.Text = text;
            PerformLayout();
            var sz = GetPreferredSize(Size.Empty);
            var screen = Screen.FromRectangle(anchor).Bounds;
            int x = Math.Max(screen.Left, Math.Min(anchor.Left + (anchor.Width - sz.Width) / 2, screen.Right - sz.Width));
            Location = new Point(x, Math.Min(anchor.Top, screen.Bottom - 48) - sz.Height - 8);
            if (!Visible) Show();
        }
    }

    class App : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "BatteryTray";
        const int LowThreshold = 15;

        readonly TaskbarWidget _widget;
        readonly Control _invoker = new Control();
        readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        readonly Dictionary<Guid, BatteryDevice> _known = new Dictionary<Guid, BatteryDevice>();
        readonly Dictionary<Guid, DateTime> _lastSeen = new Dictionary<Guid, DateTime>();
        readonly HashSet<Guid> _warned = new HashSet<Guid>();
        readonly uint _taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
        bool _scanning;

        public App()
        {
            _invoker.CreateControl();
            IntPtr force = _invoker.Handle;
            if (!IsAutostart()) SetAutostart(true);

            _widget = new TaskbarWidget { Menu = BuildMenu() };
            _widget.LeftClick = Refresh;

            _timer.Interval = 60 * 1000;
            _timer.Tick += delegate { Refresh(); };
            _timer.Start();
            SystemEvents.UserPreferenceChanged += delegate { _invoker.BeginInvoke((Action)_widget.Render); };
            SystemEvents.DisplaySettingsChanged += delegate { _invoker.BeginInvoke((Action)_widget.Render); };
            SystemEvents.PowerModeChanged += delegate { Refresh(); };
            Refresh();
        }

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Оновити", null, delegate { Refresh(); });
            var auto = new ToolStripMenuItem("Запускати з Windows") { Checked = IsAutostart() };
            auto.Click += delegate { SetAutostart(!IsAutostart()); auto.Checked = IsAutostart(); };
            menu.Items.Add(auto);
            menu.Items.Add("Звіт про пристрої", null, delegate { SaveReport(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Вийти", null, delegate { ExitThread(); });
            return menu;
        }

        public static List<BatteryDevice> ScanAll()
        {
            var all = new List<BatteryDevice>();
            try { all.AddRange(DeviceScanner.Scan()); } catch { }
            try { all.AddRange(Logitech.Scan()); } catch { }
            try { all.AddRange(Logitech.ScanHeadsets()); } catch { }
            try { all.AddRange(AttackShark.Scan()); } catch { }
            return all;
        }

        void Refresh()
        {
            if (_scanning) return;
            _scanning = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var devices = ScanAll();
                try { _invoker.BeginInvoke((Action)delegate { _scanning = false; Apply(devices); }); }
                catch { _scanning = false; }
            });
        }

        void Apply(List<BatteryDevice> devices)
        {
            foreach (var d in devices) { _known[d.Container] = d; _lastSeen[d.Container] = DateTime.Now; }
            // Пристрій міг просто заснути — прибираємо його лише після 10 хв тиші.
            foreach (var k in _known.Keys.Where(k => DateTime.Now - _lastSeen[k] > TimeSpan.FromMinutes(10)).ToList())
                _known.Remove(k);

            _widget.ShowPlaceholder = true; // перше сканування завершене
            _widget.Devices = _known.Values
                .OrderBy(d => d.Kind == DeviceKind.Mouse ? 0 : d.Kind == DeviceKind.Keyboard ? 1 : d.Kind == DeviceKind.Headphones ? 2 : 3)
                .ThenBy(d => d.Name).ToList();
            _widget.Render();

            foreach (var d in devices)
            {
                if (d.Percent <= LowThreshold && !d.Charging && !_warned.Contains(d.Container))
                {
                    _warned.Add(d.Container);
                    Warn(d.Name + ": " + d.Percent + "%");
                }
                else if (d.Percent > LowThreshold + 5 || d.Charging) _warned.Remove(d.Container);
            }
        }

        // Зберігає на робочий стіл текстовий звіт про всі HID-пристрої та результат сканування,
        // щоб людина з непідтримуваним пристроєм могла надіслати його розробнику.
        void SaveReport()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("BatteryTray — звіт про пристрої");
                sb.AppendLine("Дата: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                sb.AppendLine("Windows: " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " x64" : " x86"));
                sb.AppendLine();
                sb.AppendLine("=== Знайдені пристрої із зарядом ===");
                Action<string> log = line => { lock (sb) sb.AppendLine("  [log] " + line); };
                var oldL = Logitech.Log; var oldA = AttackShark.Log;
                Logitech.Log = log; AttackShark.Log = log;
                List<BatteryDevice> found;
                try { found = ScanAll(); } finally { Logitech.Log = oldL; AttackShark.Log = oldA; }
                if (found.Count == 0) sb.AppendLine("  (жодного)");
                foreach (var d in found)
                    sb.AppendLine("  " + d.Kind + " | " + d.Percent + "% | " + d.Name + (d.Charging ? " (заряджається)" : ""));
                sb.AppendLine();
                sb.AppendLine("=== Усі HID-інтерфейси (VID:PID, usage page/usage, назва) ===");
                try
                {
                    foreach (var h in Hid.Enumerate(v => true).OrderBy(h => h.Vid).ThenBy(h => h.Pid).ThenBy(h => h.UsagePage))
                        sb.AppendLine(string.Format("  {0:X4}:{1:X4}  page={2:X4} usage={3:X4} in={4} out={5} feat={6}  {7}",
                            h.Vid, h.Pid, h.UsagePage, h.Usage, h.InputLen, h.OutputLen, h.FeatureLen, h.Product));
                }
                catch (Exception e) { sb.AppendLine("  помилка: " + e.Message); }

                string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "BatteryTray-report.txt");
                try
                {
                    System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
                    System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\"");
                }
                catch (Exception e)
                {
                    string msg = e.Message;
                    _invoker.BeginInvoke((Action)delegate { MessageBox.Show("Не вдалося зберегти звіт: " + msg, "BatteryTray"); });
                }
            });
        }

        void Warn(string text)
        {
            var ni = new NotifyIcon { Icon = SystemIcons.Warning, Visible = true, Text = "BatteryTray" };
            ni.ShowBalloonTip(5000, "Низький заряд", text, ToolTipIcon.Warning);
            var t = new System.Windows.Forms.Timer { Interval = 10000 };
            t.Tick += delegate { t.Stop(); t.Dispose(); ni.Visible = false; ni.Dispose(); };
            t.Start();
        }

        static bool IsAutostart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(AppName) != null;
        }

        static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(AppName, false);
            }
        }

        protected override void ExitThreadCore()
        {
            _timer.Stop();
            _widget.Dispose();
            base.ExitThreadCore();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--list")
            {
                Logitech.Log = AttackShark.Log = Console.WriteLine;
                foreach (var d in App.ScanAll())
                    Console.WriteLine("=> " + d.Kind + " | " + d.Percent + "% | " + d.Name + (d.Charging ? " (charging)" : ""));
                return;
            }
            if (args.Length > 0 && args[0] == "--hid")
            {
                foreach (var h in Hid.Enumerate(v => true))
                    Console.WriteLine(string.Format("{0:X4}:{1:X4} page={2:X4} usage={3:X4} in={4} out={5} feat={6} '{7}'",
                        h.Vid, h.Pid, h.UsagePage, h.Usage, h.InputLen, h.OutputLen, h.FeatureLen, h.Product));
                return;
            }

            bool created;
            using (new Mutex(true, "BatteryTray_SingleInstance", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App());
            }
        }
    }
}

// CI Widget for Windows - the taskbar window, hover card, menu and the program entry point.
// C# 5 only: compiled by the in-box .NET Framework compiler (csc.exe v4.x).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CiWidget
{
    internal sealed class WidgetForm : LayeredForm
    {
        readonly Settings settings = Settings.Load();
        readonly LayeredForm card = new LayeredForm();
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly List<CardLink> links = new List<CardLink>();
        Thread worker;
        volatile bool stopping, reloadConfig;
        volatile Snapshot last = new Snapshot();
        volatile Config cfg = Config.Load();
        bool dark = true, hover, pinned, hiddenForShell = true, mouseDown, dragging, inStrip;
        float scale = 1f;
        string drawnKey;
        int ticks, hoverTicks, leaveTicks, dragStartX, dragOrigX, trayLeft;
        Native.RECT tb, mon, work;
        Point pos;
        Point cardOrigin;
        ContextMenuStrip menu;
        AllViewForm allView;
        readonly uint myPid = (uint)Process.GetCurrentProcess().Id;
        Native.WinEventProc onForeground;   // kept in a field so the GC never frees the callback
        IntPtr fgHook;

        public WidgetForm()
        {
            dark = ReadDark();
            card.Text = "CI Widget details";
            card.MouseUp += CardMouseUp;
            card.MouseMove += CardMouseMove;
            MouseDown += PillMouseDown; MouseMove += PillMouseMove; MouseUp += PillMouseUp;
            tick.Interval = 250; tick.Tick += OnTick;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            card.CreateControl();
            IntPtr force = card.Handle;
            worker = new Thread(CollectLoop); worker.IsBackground = true; worker.Name = "CI collector"; worker.Start();
            onForeground = delegate { try { if (!stopping) BeginInvoke(new Action(KeepAboveTaskbar)); } catch { } };
            try { fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, onForeground, 0, 0, Native.WINEVENT_OUTOFCONTEXT); } catch { }
            tick.Start();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!IsHandleCreated) CreateHandle();
            base.SetVisibleCore(false);
        }

        // The collector runs off the UI thread; the card is drawn from its last snapshot, never on the hover path.
        void CollectLoop()
        {
            DateTime cfgStamp = ConfigStamp();
            Collector col = new Collector(cfg, delegate(AccountConfig a) { return (IFetcher)new GitHubFetcher(a); });
            while (!stopping)
            {
                DateTime stamp = ConfigStamp();
                if (reloadConfig || stamp != cfgStamp)
                {
                    reloadConfig = false;
                    Config next = Config.Load(cfg);
                    // A failed read keeps the last good config and is tried again next round (a locked file frees up).
                    cfgStamp = next.Error.Length > 0 ? DateTime.MaxValue : stamp;
                    if (next != cfg)
                    {
                        cfg = next;
                        col = new Collector(cfg, delegate(AccountConfig a) { return (IFetcher)new GitHubFetcher(a); });
                    }
                }
                try { last = col.Round(DateTime.UtcNow); }
                catch (Exception ex) { Program.Log(ex); }
                try { if (IsHandleCreated && !stopping) BeginInvoke(new Action(Redraw)); } catch { }
                wake.WaitOne(col.NextDelaySec(DateTime.UtcNow) * 1000);
            }
        }

        static DateTime ConfigStamp()
        {
            try { return File.Exists(Config.FilePath) ? File.GetLastWriteTimeUtc(Config.FilePath) : DateTime.MinValue; } catch { return DateTime.MinValue; }
        }

        static bool ReadDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return !(v is int && (int)v == 1);
                }
            }
            catch { return true; }
        }

        List<PillCell> CurrentCells() { return Painter.Cells(last, cfg, DateTime.UtcNow); }

        void Redraw()
        {
            if (stopping) return;
            List<PillCell> cells = CurrentCells();
            string spoken = Painter.AccessibleText(cells);
            if (spoken != AccessibleName) { AccessibleName = spoken; Text = "CI Widget: " + spoken; }
            if (!hiddenForShell)
            {
                string key = Painter.PillKey(cells, Palette.For(dark), scale, hover || dragging);
                Size sz = Painter.PillSize(cells, scale);
                if (key != drawnKey || Placed.X != pos.X || Placed.Y != pos.Y || Placed.Width != sz.Width)
                {
                    using (Bitmap b = Painter.Pill(cells, Palette.For(dark), scale, hover || dragging)) Present(b, pos.X, pos.Y);
                    drawnKey = key;
                }
                if (!Native.IsWindowVisible(Handle)) ShowQuiet();
            }
            if (card.Shown2) ShowCard();
            if (allView != null && !allView.IsDisposed && allView.Visible) allView.SetData(last);
        }

        // ------------------------------------------------ placement
        void OnTick(object sender, EventArgs e)
        {
            ticks++;
            if (ticks % 4 == 0) Housekeep(); else KeepAboveTaskbar();
            if (ticks % 40 == 0 && card.Shown2) ShowCard();   // elapsed times move on between refreshes
            HoverLogic();
        }

        void Housekeep()
        {
            if (ticks % 8 == 0) { bool d = ReadDark(); if (d != dark) { dark = d; Redraw(); } }
            IntPtr tray = Native.FindWindowW("Shell_TrayWnd", null);
            IntPtr strip = SolarityWidgets.Strip.Ensure();
            Native.RECT sr = new Native.RECT();
            inStrip = strip != IntPtr.Zero && Native.GetWindowRect(strip, out sr) && sr.Width > 0 && sr.Height > 0;
            if (inStrip) tb = sr;
            bool hide = !inStrip && (tray == IntPtr.Zero || !Native.GetWindowRect(tray, out tb));
            if (!hide)
            {
                Native.MONITORINFO mi = Native.Monitor(inStrip ? strip : tray); mon = mi.rcMonitor; work = mi.rcWork;
                float s;
                if (inStrip) s = tb.Height / (float)SolarityWidgets.Strip.HeightDip;
                else { uint dpi = 0; try { dpi = Native.GetDpiForWindow(tray); } catch { } s = dpi > 0 ? dpi / 96f : 1f; }
                if (Math.Abs(s - scale) > 0.01f) scale = s;
                hide = (!inStrip && TaskbarAutoHidden()) || ForegroundIsFullscreen(tray);
                if (!hide && !mouseDown) pos = Target(tray, Painter.PillSize(CurrentCells(), scale));
            }
            if (hide != hiddenForShell)
            {
                hiddenForShell = hide;
                if (hide) { HideQuiet(); card.HideQuiet(); pinned = false; drawnKey = null; }
            }
            if (!hide)
            {
                Size sz = Painter.PillSize(CurrentCells(), scale);
                if (!Native.IsWindowVisible(Handle) || Placed.X != pos.X || Placed.Y != pos.Y || Placed.Width != sz.Width || Placed.Height != sz.Height) Redraw();
                else Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }

        void KeepAboveTaskbar()
        {
            if (stopping || hiddenForShell || !Shown2) return;
            Native.RECT me;
            if (!Native.GetWindowRect(Handle, out me)) return;
            if (ZOrder.ShellCovers(ZOrder.Above(Handle), me))
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        bool TaskbarAutoHidden()
        {
            Native.APPBARDATA ab = new Native.APPBARDATA(); ab.cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA));
            long state = Native.SHAppBarMessage(4, ref ab).ToInt64();
            if ((state & 1) == 0) return false;
            return tb.Top < mon.Top - 1 || tb.Bottom > mon.Bottom + 1 || tb.Left < mon.Left - 1 || tb.Right > mon.Right + 1;
        }

        bool ForegroundIsFullscreen(IntPtr tray)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == tray || fg == Handle || fg == card.Handle) return false;
            if (allView != null && !allView.IsDisposed && fg == allView.Handle) return false;
            if (ZOrder.IsShellClass(Native.ClassOf(fg))) return false;
            Native.RECT r;
            if (!Native.GetWindowRect(fg, out r)) return false;
            Native.MONITORINFO fm = Native.Monitor(fg);
            if (fm.rcMonitor.Left != mon.Left || fm.rcMonitor.Top != mon.Top) return false;
            if (!ZOrder.IsFullscreen(r, mon, Native.IsZoomed(fg))) return false;
            uint pid; Native.GetWindowThreadProcessId(fg, out pid);
            return !ZOrder.IsShellProcess(ProcessName(pid));
        }

        static string ProcessName(uint pid)
        {
            try { using (Process p = Process.GetProcessById((int)pid)) return p.ProcessName; } catch { return ""; }
        }

        Point Target(IntPtr tray, Size sz)
        {
            int w = sz.Width, h = sz.Height, gap = (int)Math.Round(6 * scale);
            if (tb.Height > tb.Width) return new Point(work.Right - w - gap * 2, work.Bottom - h - gap * 2);
            if (inStrip) trayLeft = tb.Right;
            else
            {
                IntPtr notify = Native.FindWindowExW(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                Native.RECT nr;
                trayLeft = notify != IntPtr.Zero && Native.GetWindowRect(notify, out nr) && nr.Left > tb.Left ? nr.Left : tb.Right - (int)(300 * scale);
            }
            return ZOrder.PillPoint(tb, trayLeft, sz, scale, settings.OffsetFromRight, Obstacles(tray));
        }

        List<Native.RECT> Obstacles(IntPtr tray)
        {
            List<Native.RECT> list = new List<Native.RECT>();
            uint explorerPid; Native.GetWindowThreadProcessId(tray, out explorerPid);
            Native.EnumWindowsProc cb = delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                if (pid == myPid || pid == explorerPid) return true;
                if ((Native.GetWindowLongW(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) == 0) return true;
                int cloaked; if (Native.DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                Native.RECT r;
                if (!Native.GetWindowRect(h, out r) || r.Width <= 0 || r.Height <= 0) return true;
                if (r.Width > 700 * scale || r.Height > tb.Height + 2) return true;
                if (r.Top < tb.Top - 2 || r.Bottom > tb.Bottom + 2 || r.Right <= tb.Left || r.Left >= tb.Right) return true;
                list.Add(r);
                return true;
            };
            Native.EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return list;
        }

        // ------------------------------------------------ hover card, pinning, dragging, links
        void HoverLogic()
        {
            if (hiddenForShell || mouseDown) return;
            Point c = Cursor.Position;
            bool inPill = Shown2 && Placed.Contains(c);
            bool inCard = card.Shown2 && card.Placed.Contains(c);
            if (inPill != hover) { hover = inPill; Redraw(); }
            if (inPill || inCard)
            {
                leaveTicks = 0;
                if (inPill && ++hoverTicks >= 1 && !card.Shown2) ShowCard();
            }
            else
            {
                hoverTicks = 0;
                if (card.Shown2 && !pinned && ++leaveTicks >= 2) card.HideQuiet();
            }
        }

        void ShowCard()
        {
            if (hiddenForShell) return;
            Snapshot s = last;
            DateTime now = DateTime.UtcNow;
            string spoken = Painter.CardAccessibleText(s, now);
            if (spoken != card.AccessibleName) { card.AccessibleName = spoken; card.Text = "CI Widget details: " + spoken; }
            int maxH = Math.Max((int)(300 * scale), (int)((work.Bottom - work.Top) * 0.85));
            using (Bitmap b = Painter.Card(s, cfg, Palette.For(dark), scale, pinned, now, maxH, links))
            {
                int m = (int)Math.Round(Painter.CardMargin * scale), gap = (int)Math.Round(6 * scale);
                int x = Placed.X + Placed.Width / 2 - b.Width / 2;
                x = Math.Max(work.Left - m + gap, Math.Min(work.Right - b.Width + m - gap, x));
                int y = tb.Height > tb.Width ? Placed.Y - b.Height + m - gap : ZOrder.CardY(tb, mon, b.Height, m, gap);
                card.Present(b, x, y);
                cardOrigin = new Point(x, y);
            }
            if (!card.Shown2) card.ShowQuiet();
        }

        CardLink LinkAt(Point screen)
        {
            PointF p = new PointF(screen.X - cardOrigin.X, screen.Y - cardOrigin.Y);
            foreach (CardLink l in links) if (l.Rect.Contains(p)) return l;
            return null;
        }

        void CardMouseMove(object sender, MouseEventArgs e)
        {
            Cursor want = LinkAt(Cursor.Position) != null ? Cursors.Hand : Cursors.Default;
            if (card.Cursor != want) card.Cursor = want;
        }

        void PillMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            mouseDown = true; dragging = false;
            dragStartX = Cursor.Position.X; dragOrigX = Placed.X;
        }

        void PillMouseMove(object sender, MouseEventArgs e)
        {
            if (!mouseDown) return;
            int dx = Cursor.Position.X - dragStartX;
            if (!dragging && ZOrder.DragStarts(dx, scale, tb.Height > tb.Width)) { dragging = true; card.HideQuiet(); pinned = false; }
            if (!dragging || tb.Height > tb.Width) return;
            int gap = (int)Math.Round(6 * scale);
            int x = Math.Max(tb.Left + gap, Math.Min(trayLeft - gap - Placed.Width, dragOrigX + dx));
            pos = new Point(x, Placed.Y);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, Placed.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Placed = new Rectangle(x, Placed.Y, Placed.Width, Placed.Height);
        }

        void PillMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { ShowMenu(); return; }
            if (e.Button != MouseButtons.Left || !mouseDown) return;
            mouseDown = false;
            if (dragging)
            {
                dragging = false;
                settings.OffsetFromRight = (int)Math.Round((tb.Right - Placed.X) / scale);
                settings.Save();
                Redraw();
                return;
            }
            pinned = !pinned;
            if (pinned) ShowCard(); else card.HideQuiet();
        }

        void CardMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { ShowMenu(); return; }
            if (e.Button != MouseButtons.Left) return;
            CardLink l = LinkAt(Cursor.Position);
            if (l != null)
            {
                if (l.Target == "action:showall") ShowAll();
                else if (l.Target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) try { Process.Start(l.Target); } catch { }
                return;
            }
            pinned = !pinned; ShowCard();
        }

        void ShowAll()
        {
            if (allView == null || allView.IsDisposed) allView = new AllViewForm(settings);
            allView.SetData(last);
            allView.Show();
            if (allView.WindowState == FormWindowState.Minimized) allView.WindowState = FormWindowState.Normal;
            allView.Activate();
            card.HideQuiet(); pinned = false;
        }

        // ------------------------------------------------ menu
        ToolStripMenuItem Item(string text, bool check, EventHandler click)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text, null, click);
            it.Checked = check;
            if (dark) it.ForeColor = Color.White;
            return it;
        }

        void ShowMenu()
        {
            if (menu != null) menu.Dispose();
            menu = new ContextMenuStrip();
            menu.ShowImageMargin = true;
            if (dark) { menu.Renderer = new DarkMenuRenderer(); menu.BackColor = Palette.Hex("#1E1E2E"); menu.ForeColor = Color.White; }
            ToolStripMenuItem head = Item("CI Widget", false, null); head.Enabled = false;
            menu.Items.Add(head);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Refresh now", false, delegate { wake.Set(); }));
            menu.Items.Add(Item("Show all jobs", false, delegate { ShowAll(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Edit config", false, delegate { EditConfig(); }));
            menu.Items.Add(Item("Open config folder", false, delegate { try { Directory.CreateDirectory(Config.Dir); Process.Start("explorer.exe", "\"" + Config.Dir + "\""); } catch { } }));
            bool auto = Startup.Enabled;
            menu.Items.Add(Item("Start with Windows", auto, delegate { Startup.Toggle(!auto, null, Tell); }));
            menu.Items.Add(Item("Reset position", false, delegate { settings.OffsetFromRight = -1; settings.Save(); Housekeep(); Redraw(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Exit", false, delegate { Quit(); }));
            if (dark) foreach (ToolStripItem it in menu.Items) if (it is ToolStripSeparator) it.ForeColor = Palette.Hex("#3A3A55");
            menu.Show(Cursor.Position);
        }

        static void Tell(string text)
        {
            try { MessageBox.Show(text, "CI Widget", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
        }

        // Opens config.json in the user's editor, creating it from the example first.
        void EditConfig()
        {
            try
            {
                Directory.CreateDirectory(Config.Dir);
                if (!File.Exists(Config.FilePath)) File.WriteAllText(Config.FilePath, Config.Example, new UTF8Encoding(false));
                try { Process.Start(Config.FilePath); }
                catch { Process.Start("notepad.exe", "\"" + Config.FilePath + "\""); }
            }
            catch (Exception ex) { Program.Log(ex); }
            reloadConfig = true;
        }

        void Quit()
        {
            stopping = true; wake.Set(); tick.Stop();
            if (fgHook != IntPtr.Zero) { Native.UnhookWinEvent(fgHook); fgHook = IntPtr.Zero; }
            try { if (worker != null) worker.Join(2000); } catch { }
            card.HideQuiet(); HideQuiet();
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            stopping = true; wake.Set();
            base.OnFormClosing(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            try
            {
                if (args.Length >= 2 && args[0] == "--render") return TestModes.Render(args[1]);
                if (args.Length >= 2 && args[0] == "--probe") return TestModes.Probe(args[1]);
                if (args.Length >= 2 && args[0] == "--selftest") return TestModes.SelfTest(args[1]);
                if (args.Length >= 1 && args[0] == "--install") return SelfInstall.Install(InstallArgs.Parse(args, 1));
                if (args.Length >= 1 && args[0] == "--uninstall") return SelfInstall.Uninstall(InstallArgs.Parse(args, 1));
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "ciwidget-error.txt"), ex.ToString()); } catch { }
                return 2;
            }

            bool created;
            using (Mutex single = new Mutex(true, @"Local\CIWidget.Single", out created))
            {
                if (!created) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log(e.Exception); };
                try { Application.Run(new WidgetForm()); }
                finally { try { SolarityWidgets.Strip.Close(); } catch { } }
            }
            return 0;
        }

        public const long LogCap = 256 * 1024;

        public static void Log(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Config.Dir);
                AppendCapped(Path.Combine(Config.Dir, "error.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ", CultureInfo.InvariantCulture) + ex + "\r\n", LogCap);
            }
            catch { }
        }

        public static void AppendCapped(string path, string text, long cap)
        {
            FileInfo fi = new FileInfo(path);
            if (fi.Exists && fi.Length + Encoding.UTF8.GetByteCount(text) > cap)
            {
                string old = File.ReadAllText(path);
                int keep = (int)Math.Min(old.Length, cap / 2);
                string tail = old.Substring(old.Length - keep);
                int nl = tail.IndexOf('\n');
                File.WriteAllText(path, nl >= 0 ? tail.Substring(nl + 1) : "");
            }
            File.AppendAllText(path, text);
        }
    }
}

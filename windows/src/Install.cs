// CI Widget for Windows - install and uninstall without PowerShell or admin rights.
// "Install CI Widget (Windows).cmd" compiles the widget into %LOCALAPPDATA%\Programs\CI Widget and runs it with
// --install: the exe self-tests, writes an example config, adds a shortcut to the user's Startup folder and starts.
// C# 5 only: compiled by the in-box .NET Framework compiler (csc.exe v4.x).
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;

namespace CiWidget
{
    internal sealed class InstallArgs
    {
        public bool NoLaunch;
        public string StartupDir;   // test seam: a scratch folder instead of the user's Startup folder

        public static InstallArgs Parse(string[] args, int from)
        {
            InstallArgs a = new InstallArgs();
            for (int i = from; i < args.Length; i++)
            {
                string s = args[i].ToLowerInvariant();
                if (s == "--no-launch") a.NoLaunch = true;
                else if (s == "--startup" && i + 1 < args.Length) a.StartupDir = args[++i];
            }
            return a;
        }
    }

    internal static class Startup
    {
        public const string LinkName = "CI Widget.lnk";

        public static string LinkPath(string dir)
        {
            return Path.Combine(dir ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup), LinkName);
        }

        public static bool Enabled { get { return File.Exists(LinkPath(null)); } }

        public static bool Set(bool on, string dir)
        {
            try
            {
                if (on) ShellLink.Create(LinkPath(dir), SelfInstall.ExePath, Path.GetDirectoryName(SelfInstall.ExePath));
                else if (File.Exists(LinkPath(dir))) File.Delete(LinkPath(dir));
                return true;
            }
            catch { return false; }
        }
    }

    internal static class SelfInstall
    {
        // Exit codes, read by the .cmd scripts.
        public const int Ok = 0, SelfTestFailed = 3, StartupFailed = 4;

        // The gate passes only a report whose last line is "N passed, 0 failed".
        public static bool GatePasses(string lastLine)
        {
            return lastLine != null && Regex.IsMatch(lastLine, @"^\d+ passed, 0 failed$");
        }

        // The real file name: Application.ExecutablePath reads %41 in a folder name as "A".
        internal static string ExePath { get { return Process.GetCurrentProcess().MainModule.FileName; } }

        internal static bool RunGate(Func<string, int> selfTest, string report)
        {
            try { File.Delete(report); } catch { }   // an old passing report must never pass a new build
            try { selfTest(report); }
            catch { return false; }
            string last = null;
            try { string[] lines = File.ReadAllLines(report); if (lines.Length > 0) last = lines[lines.Length - 1]; } catch { }
            return GatePasses(last);
        }

        public static int Install(InstallArgs a)
        {
            string exe = ExePath;
            string report = Path.Combine(Path.GetDirectoryName(exe), "selftest.txt");
            if (!RunGate(TestModes.SelfTest, report)) return SelfTestFailed;
            Config.EnsureExample();
            bool linked = Startup.Set(true, a.StartupDir);
            if (!a.NoLaunch) try { Process.Start(exe); } catch { }
            return linked ? Ok : StartupFailed;
        }

        public static int Uninstall(InstallArgs a)
        {
            return Startup.Set(false, a.StartupDir) ? Ok : StartupFailed;
        }
    }

    // Writes .lnk files through the shell's own ShellLink object (no scripting host).
    internal static class ShellLink
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        public static void Create(string lnk, string target, string workDir)
        {
            IShellLinkW link = (IShellLinkW)new CShellLink();
            try
            {
                link.SetPath(target);
                link.SetWorkingDirectory(workDir);
                link.SetDescription("CI Widget: running and queued GitHub Actions jobs on the taskbar");
                Directory.CreateDirectory(Path.GetDirectoryName(lnk));
                ((IPersistFile)link).Save(lnk, true);
            }
            finally { Marshal.ReleaseComObject(link); }
        }
    }
}

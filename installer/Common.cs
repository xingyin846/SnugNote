// ============================================================================
//  任务便签 · 安装/卸载 公共部分（AppInfo / Program / Util / Payload / NativeMethods）
//
//  本文件被两个产物共用：
//    [安装包]  Common.cs + Install.cs  → dist\任务便签-安装包.exe
//    [卸载器]  Common.cs + Uninstall.cs → 安装目录\卸载.exe
//
//  设计要点：
//    1. 待安装文件全部以 base64 内嵌在 payload.g.cs（构建脚本生成），
//       最终产物是单个 exe，不依赖任何第三方打包工具。
//    2. 默认装到 %LOCALAPPDATA%\任务便签（用户级，全程零 UAC），允许换路径。
//    3. 只写「程序文件」，绝不创建/覆盖/删除 data 目录 —— 便签数据归用户，
//       卸载时原样保留（重装即恢复）。
//    4. 本源码刻意写成纯 ASCII：所有中文都用 \uXXXX 转义。因为 PowerShell 会把
//       源文件路径按系统 ANSI 传给原生 csc.exe，源码含非 ASCII 字面量易出乱码。
//    5. 只需 .NET Framework 4.x（Windows 10/11 自带），C# 5 语法，csc.exe 直编。
// ============================================================================

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

static class AppInfo
{
    // 产品名（2026-09-11 由「任务便签」改为「贴贴便签」，界面/启动器本来就用这个名字）
    public const string Name = "\u8D34\u8D34\u4FBF\u7B7E";              // 贴贴便签
    public const string Version = "1.0.0";
    public const string Publisher = "\u8D34\u8D34\u4FBF\u7B7E";         // 贴贴便签

    // 旧名（改名前叫「任务便签」）。只用于升级时识别并清理旧版残留，
    // 绝不用于新安装的路径/文件名。
    public const string LegacyName = "\u4EFB\u52A1\u4FBF\u7B7E";        // 任务便签

    public const string SubKeyName =
        "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\TietieNotes";
    public const string LegacySubKeyName =
        "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\TietieNotes";   // 键名未随产品名变，保留兼容

    // 同一份代码编译出两个 exe，靠自身文件名区分模式
    public const string UninstallFile = "\u5378\u8F7D.exe";             // 卸载.exe
    public const string UninstallStem = "\u5378\u8F7D";                 // 卸载
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
        {
            MessageBox.Show("\u7A0B\u5E8F\u51FA\u9519\uFF1A\n" + e.Exception.Message, AppInfo.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

#if INSTALLER_BUILD
        // 无界面模式（供排障与自动化验证；正常安装请直接双击本程序）：
        //   --extract <目录>            只释放程序文件：不建快捷方式、不写注册表
        //   --install <目录>            完整安装到指定目录（含快捷方式与卸载登记）
        //   --install <目录> --no-desktop   同上，但不建桌面快捷方式
        // 其余情况：打开安装向导。
        if (args != null && args.Length >= 2)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            if (args[0] == "--extract")
            {
                string target = args[1];
                Directory.CreateDirectory(target);
                for (int i = 0; i < Embedded.Files.Length; i++)
                {
                    string p = Payload.Materialize(i, target);
                    Console.WriteLine("\u91CA\u653E " + Embedded.Files[i].Rel + " -> " + p);
                }
                Console.WriteLine("\u5B8C\u6210\uFF1A" + Embedded.Files.Length + " \u4E2A\u6587\u4EF6");
                return;
            }

            if (args[0] == "--install")
            {
                bool noDesktop = false;
                for (int i = 2; i < args.Length; i++)
                    if (args[i] == "--no-desktop") noDesktop = true;

                string err;
                if (!InstallCore.Headless(args[1], noDesktop, out err))
                {
                    Console.WriteLine("\u5B89\u88C5\u5931\u8D25\uFF1A" + err);
                    Environment.ExitCode = 1;
                    return;
                }
                Console.WriteLine("\u5B89\u88C5\u5B8C\u6210\uFF1A" + args[1]);
                return;
            }
        }

        string defDir = null;
        bool autoRun = false;
        if (args != null)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--default-dir" && i + 1 < args.Length) defDir = args[i + 1];
                if (args[i] == "--auto") autoRun = true;
            }
        }
        Application.Run(new WelcomeForm(defDir, autoRun));
#else
        // 无界面卸载：--uninstall [目录]（QuietUninstallString 即用此形式）
        if (args != null && args.Length >= 1 && args[0] == "--uninstall")
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            string d = (args.Length >= 2) ? args[1] : Util.ReadInstallLocation();
            if (string.IsNullOrEmpty(d)) d = Path.GetDirectoryName(Application.ExecutablePath);
            string err;
            int n;
            if (!UninstallCore.Run(d, out n, out err))
            {
                Console.WriteLine("\u5378\u8F7D\u5931\u8D25\uFF1A" + err);
                Environment.ExitCode = 1;
                return;
            }
            Console.WriteLine("\u5DF2\u5378\u8F7D\uFF0C\u6E05\u7406 " + n + " \u4E2A\u6587\u4EF6");
            Console.WriteLine("\u4FBF\u7B7E\u6570\u636E\u4FDD\u7559\u5728\uFF1A" + Path.Combine(d, "data"));
            return;
        }
        Application.Run(new UninstallForm());
#endif
    }
}

// ---------------------------------------------------------------------------
//  公共小工具
// ---------------------------------------------------------------------------
static class Util
{
    public static string DefaultDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppInfo.Name);
    }

    public static string ReadInstallLocation()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(AppInfo.SubKeyName))
            {
                if (k != null)
                {
                    object v = k.GetValue("InstallLocation");
                    if (v != null && v.ToString().Length > 0) return v.ToString();
                }
            }
        }
        catch { }
        return null;
    }

    // 桌面/开始菜单快捷方式统一用 WScript.Shell 的 CreateShortcut（晚绑定，免引用 COM 程序集）
    public static void CreateShortcut(string linkPath, string targetPath, string workDir)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) throw new Exception("WScript.Shell \u4E0D\u53EF\u7528");   // 不可用

        object shell = Activator.CreateInstance(t);
        try
        {
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                null, shell, new object[] { linkPath });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { targetPath });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
            lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { targetPath + ",0" });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { AppInfo.Name });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
        }
        finally
        {
            if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
    }

    public static string StartMenuDir()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.Name);
    }

    public static string DesktopLink()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            AppInfo.Name + ".lnk");
    }

    public static string StartMenuLink()
    {
        return Path.Combine(StartMenuDir(), AppInfo.Name + ".lnk");
    }

    public static long DirBytes(string dir)
    {
        long total = 0;
        try
        {
            string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                try { total += new FileInfo(files[i]).Length; }
                catch { }
            }
        }
        catch { }
        return total;
    }

    public static string Human(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1048576) return (bytes / 1024.0).ToString("0.0") + " KB";
        return (bytes / 1048576.0).ToString("0.00") + " MB";
    }

    public static void AppIcon(Form f)
    {
        try
        {
            Icon ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (ic != null) f.Icon = ic;
        }
        catch { }
    }

    // a 是否等于 root 或位于 root 之下
    public static bool SameOrUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
        string a = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        string b = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLocked(string file)
    {
        try
        {
            using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            return false;
        }
        catch { return true; }
    }
}

static class NativeMethods
{
    public const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);
}

// ---------------------------------------------------------------------------
//  安装核心：安装向导与 --install 无界面模式共用同一套逻辑
// ---------------------------------------------------------------------------
static class InstallCore
{
    // 校验目标目录。返回 null 表示通过。
    public static string Prepare(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return "\u76EE\u5F55\u4E3A\u7A7A";   // 目录为空

        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (Util.SameOrUnder(dir, win) || Util.SameOrUnder(dir, sys))
            return "\u4E0D\u80FD\u5B89\u88C5\u5230\u7CFB\u7EDF\u76EE\u5F55";   // 不能安装到系统目录

        string selfDir = Path.GetDirectoryName(Application.ExecutablePath);
        if (Util.SameOrUnder(selfDir, dir))
            return "\u5B89\u88C5\u5305\u6B63\u5728\u8BE5\u76EE\u5F55\uFF08\u6216\u5176\u4E0A\u5C42\uFF09\u8FD0\u884C";   // 安装包正在该目录

        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        string exe = Path.Combine(dir, AppInfo.Name + ".exe");
        if (File.Exists(exe) && Util.IsLocked(exe))
            return AppInfo.Name + " \u6B63\u5728\u8FD0\u884C\uFF0C\u8BF7\u5148\u5173\u95ED\u5B83\u7684\u9ED1\u8272\u7A97\u53E3";   // 正在运行

        // 改名前装的旧版如果还在跑，装完会变成"两个图标、两个进程"，先让用户关掉
        if (Legacy.Running(Legacy.LegacyDir()))
            return "\u68C0\u6D4B\u5230\u65E7\u7248\uFF08" + AppInfo.LegacyName + "\uFF09\u8FD8\u5728\u8FD0\u884C\uFF0C"
                 + "\u8BF7\u5148\u5173\u95ED\u5B83\u7684\u9ED1\u8272\u7A97\u53E3\u518D\u5B89\u88C5";
        return null;
    }

    public static void MaterializeAll(string dir)
    {
        for (int i = 0; i < Embedded.Files.Length; i++) Payload.Materialize(i, dir);
    }

    // 建快捷方式；返回实际建成的（桌面 / 开始菜单）。problems 收集失败原因。
    public static string CreateShortcuts(string dir, bool desktop, out string problems)
    {
        string target = Path.Combine(dir, AppInfo.Name + ".exe");
        string made = "";
        problems = "";
        if (desktop)
        {
            try { Util.CreateShortcut(Util.DesktopLink(), target, dir); made += "\u684C\u9762 "; }
            catch (Exception ex) { problems += "desktop:" + ex.Message + " "; }
        }
        try
        {
            string sm = Util.StartMenuDir();
            if (!Directory.Exists(sm)) Directory.CreateDirectory(sm);
            Util.CreateShortcut(Util.StartMenuLink(), target, dir);
            made += "\u5F00\u59CB\u83DC\u5355";
        }
        catch (Exception ex) { problems += "startmenu:" + ex.Message + " "; }
        return made.Trim();
    }

    public static string CreateShortcuts(string dir, bool desktop)
    {
        string p;
        return CreateShortcuts(dir, desktop, out p);
    }

    public static void WriteRegistry(string dir)
    {
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(AppInfo.SubKeyName))
        {
            if (k == null) throw new Exception("\u65E0\u6CD5\u5199\u5165\u6CE8\u518C\u8868");
            string exe = Path.Combine(dir, AppInfo.Name + ".exe");
            string unins = Path.Combine(dir, AppInfo.UninstallFile);
            k.SetValue("DisplayName", AppInfo.Name);
            k.SetValue("DisplayVersion", AppInfo.Version);
            k.SetValue("Publisher", AppInfo.Publisher);
            k.SetValue("InstallLocation", dir);
            k.SetValue("DisplayIcon", exe);
            k.SetValue("UninstallString", "\"" + unins + "\"");
            k.SetValue("QuietUninstallString", "\"" + unins + "\" --uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }

    // 无界面完整安装
    public static bool Headless(string dir, bool noDesktop, out string err)
    {
        err = Prepare(dir);
        if (err != null) return false;
        try
        {
            MaterializeAll(dir);

            string problems;
            string made = CreateShortcuts(dir, !noDesktop, out problems);
            if (made.Length == 0)
            {
                err = "\u5FEB\u6377\u65B9\u5F0F\u5168\u90E8\u5931\u8D25 " + problems;   // 快捷方式全部失败
                return false;
            }

            WriteRegistry(dir);
            Legacy.Cleanup();
            if (problems.Length > 0) Console.WriteLine("\u8B66\u544A\uFF1A" + problems);
            return true;
        }
        catch (Exception ex)
        {
            err = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }
}

// ---------------------------------------------------------------------------
//  旧版残留清理：产品名从「任务便签」改为「贴贴便签」后，旧版留下的
//  exe / 快捷方式 / 开始菜单项 / 注册表项都不再被新版本使用。
//  这里只清"程序"层面的残留，user 数据目录（旧 data\notes.json）一律保留。
// ---------------------------------------------------------------------------
static class Legacy
{
    public static string ExePath
    {
        get { return Path.Combine(AppInfo.LegacyName + ".exe"); }
    }

    public static string LegacyDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppInfo.LegacyName);
    }

    public static bool Detected()
    {
        try
        {
            if (Directory.Exists(LegacyDir())) return true;
            if (File.Exists(Path.Combine(LegacyDir(), AppInfo.LegacyName + ".exe"))) return true;
        }
        catch { }
        try
        {
            string link = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                AppInfo.LegacyName + ".lnk");
            if (File.Exists(link)) return true;
        }
        catch { }
        return false;
    }

    // 旧版是否还在运行（运行中就不该删它的文件，也不该让用户以为换名成功了）
    public static bool Running(string legacyInstallDir)
    {
        try
        {
            string exe = Path.Combine(legacyInstallDir, AppInfo.LegacyName + ".exe");
            if (File.Exists(exe) && Util.IsLocked(exe)) return true;
        }
        catch { }
        return false;
    }

    public static void Cleanup()
    {
        // 1) 旧快捷方式（桌面 + 开始菜单）
        TryDeleteFile(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            AppInfo.LegacyName + ".lnk"));

        string oldMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            AppInfo.LegacyName);
        TryDeleteFile(Path.Combine(oldMenu, AppInfo.LegacyName + ".lnk"));
        TryDeleteEmptyDir(oldMenu);

        // 2) 旧安装目录里的"程序"文件（只删我们自己装过的那几个名字，data\ 不动）
        string dir = LegacyDir();
        TryDeleteFile(Path.Combine(dir, AppInfo.LegacyName + ".exe"));
        TryDeleteFile(Path.Combine(dir, AppInfo.UninstallFile));
        TryDeleteFile(Path.Combine(dir, "demo", "index.html"));
        TryDeleteFile(Path.Combine(dir, "demo", "styles.css"));
        TryDeleteFile(Path.Combine(dir, "demo", "app.js"));
        TryDeleteFile(Path.Combine(dir, "demo", "store.js"));
        TryDeleteFile(Path.Combine(dir, "demo", "demo-standalone.html"));
        TryDeleteEmptyDir(Path.Combine(dir, "demo"));
        TryDeleteEmptyDir(dir);   // 只有空目录才会被删（有 data\ 就自然保留）
    }

    static void TryDeleteFile(string f)
    {
        try { if (File.Exists(f)) File.Delete(f); }
        catch { }
    }

    static void TryDeleteEmptyDir(string d)
    {
        try
        {
            if (Directory.Exists(d) && Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d);
        }
        catch { }
    }
}

// ---------------------------------------------------------------------------
//  内嵌负载（Embedded.Files 定义在构建脚本生成的 payload.g.cs 中）
// ---------------------------------------------------------------------------
static class Payload
{
    public static int Total() { return Embedded.Files.Length; }

    public static long TotalBytes()
    {
        long n = 0;
        for (int i = 0; i < Embedded.Files.Length; i++) n += Embedded.Files[i].Data.Length;
        return n;
    }

    // 解出第 i 个文件并写盘，返回写入的绝对路径
    public static string Materialize(int i, string root)
    {
        Asset a = Embedded.Files[i];
        byte[] raw = Convert.FromBase64String(a.Data);
        string dest = Path.Combine(root, a.Rel.Replace('/', Path.DirectorySeparatorChar));
        string dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(dest, raw);
        return dest;
    }
}

// ============================================================================
//  任务便签 · 卸载器（UninstallForm + UninstallCore）
//  编译产物：安装目录\卸载.exe —— 同时登记在「设置 → 应用 → 已安装的应用」。
//  用户决策（2026-09-11）：卸载「保留数据，只删程序」——data\notes.json 一律不动。
//
//  两种入口共用 UninstallCore：图形界面（双击）与 --uninstall（静默）。
// ============================================================================

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

// ---------------------------------------------------------------------------
//  卸载核心
// ---------------------------------------------------------------------------
static class UninstallCore
{
    // 返回 false 表示中途出错（err 为原因）；cleaned 为删除的程序文件数。
    public static bool Run(string dir, out int cleaned, out string err)
    {
        cleaned = 0;
        err = null;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            err = "\u76EE\u5F55\u4E0D\u5B58\u5728\uFF1A" + dir;   // 目录不存在
            return false;
        }

        string dataPrefix = "data" + Path.DirectorySeparatorChar;
        string blocked = "";
        string selfName = Path.GetFileName(Application.ExecutablePath);

        // 1) 删程序文件。明确跳过 data\ —— 用户数据归用户
        //    清单来自构建期生成的 InstalledFiles.Rel（不含 data）
        for (int i = 0; i < InstalledFiles.Rel.Length; i++)
        {
            string rel = InstalledFiles.Rel[i].Replace('/', Path.DirectorySeparatorChar);
            if (rel.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                string f = Path.Combine(dir, rel);
                if (File.Exists(f)) { File.Delete(f); cleaned++; }
            }
            catch (Exception ex)
            {
                // 卸载器删不掉"正在运行的自己"，这是必然的：OnFormClosed 里已经
                // 用 MoveFileEx 安排重启后删除，因此不计为失败。
                if (string.Equals(rel, selfName, StringComparison.OrdinalIgnoreCase)) continue;

                // 其余情况最常见原因：程序还在运行（文件被占用）。如实报告，不假装成功。
                blocked += rel + "(" + ex.GetType().Name + ") ";
            }
        }
        if (blocked.Length > 0)
        {
            err = "\u4EE5\u4E0B\u7A0B\u5E8F\u6587\u4EF6\u65E0\u6CD5\u5220\u9664\uFF0C\u8BF7\u5148\u5173\u95ED " + AppInfo.Name + "\uFF1A" + blocked;
            return false;
        }

        // 1b) 改名前（任务便签）留下的同名程序文件，一并清掉；数据仍不动
        string legacyExe = Path.Combine(dir, AppInfo.LegacyName + ".exe");
        try { if (File.Exists(legacyExe)) { File.Delete(legacyExe); cleaned++; } }
        catch { }

        // 2) 快捷方式（新旧名都清）
        TryDeleteFile(Util.DesktopLink());
        TryDeleteFile(Util.StartMenuLink());
        TryDeleteFile(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            AppInfo.LegacyName + ".lnk"));
        try
        {
            string sm = Util.StartMenuDir();
            if (Directory.Exists(sm) && Directory.GetFileSystemEntries(sm).Length == 0) Directory.Delete(sm);
        }
        catch { }
        string oldMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            AppInfo.LegacyName);
        TryDeleteFile(Path.Combine(oldMenu, AppInfo.LegacyName + ".lnk"));
        TryDeleteEmptyDir(oldMenu);

        // 3) 注册表（「应用和功能」里的条目）
        try { Registry.CurrentUser.DeleteSubKeyTree(AppInfo.SubKeyName, false); }
        catch { }

        // 4) 清理空目录：data 非空 → 整目录自然保留（正是我们要的结果）
        //    v25: demo\icons\ 是子目录，不先删掉它的话 demo\ 永远不空，会残留一个空壳目录
        TryDeleteEmptyDir(Path.Combine(dir, "demo", "icons"));
        TryDeleteEmptyDir(Path.Combine(dir, "demo"));
        TryDeleteEmptyDir(dir);
        return true;
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
//  图形界面
// ---------------------------------------------------------------------------
class UninstallForm : Form
{
    readonly string dir;

    public UninstallForm()
    {
        dir = Util.ReadInstallLocation();
        if (dir == null || dir.Length == 0 || !Directory.Exists(dir))
        {
            string self = Path.GetDirectoryName(Application.ExecutablePath);
            dir = (self != null && self.Length > 0) ? self : Util.DefaultDir();
        }

        Text = "\u5378\u8F7D " + AppInfo.Name;                                  // 卸载 任务便签
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(492, 280);
        Util.AppIcon(this);

        Label t = new Label();
        t.Text = "\u5378\u8F7D " + AppInfo.Name;
        t.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold);
        t.SetBounds(24, 20, 440, 28);
        Controls.Add(t);

        Label p = new Label();
        p.Text = "\u5B89\u88C5\u4F4D\u7F6E\uFF1A" + dir;                        // 安装位置：
        p.SetBounds(26, 54, 440, 20);
        Controls.Add(p);

        Label body = new Label();
        body.Text = "\u5C06\u5220\u9664\uFF1A\u7A0B\u5E8F\u6587\u4EF6\u3001demo \u754C\u9762\u6587\u4EF6\u3001"
                  + "\u684C\u9762\u4E0E\u5F00\u59CB\u83DC\u5355\u5FEB\u6377\u65B9\u5F0F\u3002\n\n"
                  + "\u4FDD\u7559\uFF1Adata\\notes.json\uFF08\u60A8\u7684\u4FBF\u7B7E\u6570\u636E\uFF09\u3002"
                  + "\u91CD\u88C5\u540E\u4FBF\u7B7E\u4F1A\u539F\u6837\u56DE\u6765\u3002";
        body.SetBounds(26, 86, 440, 80);
        Controls.Add(body);

        Label warn = new Label();
        warn.Text = "\u786E\u5B9A\u8981\u5378\u8F7D\u5417\uFF1F";                 // 确定要卸载吗？
        warn.ForeColor = Color.FromArgb(150, 40, 40);
        warn.SetBounds(26, 178, 440, 20);
        Controls.Add(warn);

        Button no = new Button();
        no.Text = "\u53D6\u6D88";                                              // 取消
        no.SetBounds(300, 218, 80, 28);
        no.Click += delegate(object s, EventArgs e) { Close(); };
        Controls.Add(no);
        CancelButton = no;

        Button yes = new Button();
        yes.Text = "\u5378\u8F7D";                                             // 卸载
        yes.SetBounds(388, 218, 80, 28);
        yes.Click += delegate(object s, EventArgs e) { OnUninstall(); };
        Controls.Add(yes);
        AcceptButton = yes;
    }

    void OnUninstall()
    {
        int cleaned = 0;
        string err = null;
        try { UninstallCore.Run(dir, out cleaned, out err); }
        catch (Exception ex) { err = ex.Message; }

        string msg = (err == null)
            ? AppInfo.Name + " \u5DF2\u5378\u8F7D\uFF0C\u5171\u6E05\u7406 " + cleaned + " \u4E2A\u6587\u4EF6\u3002\n\n"
            : "\u5378\u8F7D\u8FC7\u7A0B\u4E2D\u51FA\u73B0\u95EE\u9898\uFF1A" + err + "\n\n";

        string dataDir = Path.Combine(dir, "data");
        msg += Directory.Exists(dataDir)
            ? "\u60A8\u7684\u4FBF\u7B7E\u6570\u636E\u4FDD\u7559\u5728\uFF1A\n" + dataDir + "\n"
              + "\u4E0D\u518D\u9700\u8981\u7684\u8BDD\uFF0C\u53EF\u4EE5\u624B\u52A8\u5220\u6389\u8FD9\u4E2A\u76EE\u5F55\u3002"
            : "\u6CA1\u6709\u53D1\u73B0 data \u76EE\u5F55\uFF08\u60A8\u8FD8\u6CA1\u5EFA\u8FC7\u4FBF\u7B7E\uFF09\uFF0C"
              + "\u5B89\u88C5\u76EE\u5F55\u5DF2\u6E05\u7A7A\u3002";

        MessageBox.Show(msg, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        // 自己删自己：进程活着时无法删除自身文件，交给系统在下次重启时清理
        try
        {
            NativeMethods.MoveFileEx(Application.ExecutablePath, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
        }
        catch { }
    }
}

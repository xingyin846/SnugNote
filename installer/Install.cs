// ============================================================================
//  任务便签 · 安装向导（WelcomeForm / ProgressForm / FinishForm）
//  仅编译进安装包；卸载器不含本文件。
// ============================================================================

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

// ---------------------------------------------------------------------------
//  第 1 步：选择安装位置
// ---------------------------------------------------------------------------
class WelcomeForm : Form
{
    TextBox txtDir;
    Label lblStatus;
    readonly bool autoRun;

    public WelcomeForm() : this(null, false) { }

    public WelcomeForm(string defaultDir, bool autoInstall)
    {
        autoRun = autoInstall;
        Text = AppInfo.Name + " \u5B89\u88C5\u5411\u5BFC";              // 安装向导
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(524, 286);
        Util.AppIcon(this);

        Label title = new Label();
        title.Text = AppInfo.Name + "  \u5B89\u88C5\u7A0B\u5E8F";       // 安装程序
        title.Font = new Font(Font.FontFamily, 14F, FontStyle.Bold);
        title.ForeColor = Color.FromArgb(38, 38, 42);
        title.SetBounds(24, 20, 476, 30);
        Controls.Add(title);

        Label sub = new Label();
        sub.Text = "\u4E00\u4E2A\u672C\u5730\u7684\u8F7B\u91CF\u4FBF\u7B7E + \u4EFB\u52A1\u7BA1\u7406\u5DE5\u5177\uFF1A"
                 + "\u4E0D\u8054\u7F51\u3001\u4E0D\u4F9D\u8D56 Python / Node\u3001\u4E0D\u9700\u7BA1\u7406\u5458\u6743\u9650\u3002";
        sub.ForeColor = Color.FromArgb(110, 110, 118);
        sub.SetBounds(26, 56, 480, 20);
        Controls.Add(sub);

        GroupBox box = new GroupBox();
        box.Text = "\u5B89\u88C5\u4F4D\u7F6E";                          // 安装位置
        box.SetBounds(24, 86, 476, 92);
        Controls.Add(box);

        txtDir = new TextBox();
        txtDir.SetBounds(16, 30, 352, 24);
        string prev = Util.ReadInstallLocation();
        if (!string.IsNullOrEmpty(defaultDir)) txtDir.Text = defaultDir;
        else if (!string.IsNullOrEmpty(prev)) txtDir.Text = prev;
        else txtDir.Text = Util.DefaultDir();
        box.Controls.Add(txtDir);

        Button browse = new Button();
        browse.Text = "\u6D4F\u89C8...";                                 // 浏览...
        browse.SetBounds(376, 29, 80, 26);
        browse.Click += delegate(object s, EventArgs e) { OnBrowse(); };
        box.Controls.Add(browse);

        Label note = new Label();
        note.Text = "\u4FBF\u7B7E\u6570\u636E\u5B58\u5728\u5B89\u88C5\u76EE\u5F55\u4E0B\u7684 "
                  + "data\\notes.json\uFF0C\u8BF7\u9009\u4E00\u4E2A\u60A8\u6709\u5199\u5165\u6743\u9650\u7684\u76EE\u5F55\u3002";
        note.ForeColor = Color.FromArgb(124, 124, 132);
        note.SetBounds(30, 184, 476, 18);
        Controls.Add(note);

        lblStatus = new Label();
        lblStatus.ForeColor = Color.FromArgb(94, 94, 102);
        lblStatus.SetBounds(26, 206, 476, 20);
        lblStatus.Text = "\u5C06\u5B89\u88C5 " + Payload.Total() + " \u4E2A\u6587\u4EF6\uFF0C"
                       + "\u5171 " + Util.Human(Payload.TotalBytes()) + "\u3002";   // 将安装 N 个文件，共 X
        Controls.Add(lblStatus);

        Panel bar = new Panel();
        bar.SetBounds(0, 230, 524, 56);
        bar.BackColor = Color.FromArgb(246, 246, 248);
        Controls.Add(bar);
        bar.SendToBack();

        Button cancel = new Button();
        cancel.Text = "\u53D6\u6D88";                                    // 取消
        cancel.SetBounds(348, 14, 76, 28);
        cancel.Click += delegate(object s, EventArgs e) { Close(); };
        bar.Controls.Add(cancel);
        CancelButton = cancel;

        Button ok = new Button();
        ok.Text = "\u5B89\u88C5";                                        // 安装
        ok.SetBounds(432, 14, 76, 28);
        ok.Click += delegate(object s, EventArgs e) { OnInstall(); };
        bar.Controls.Add(ok);
        AcceptButton = ok;
    }

    void OnBrowse()
    {
        FolderBrowserDialog fb = new FolderBrowserDialog();
        fb.Description = "\u9009\u62E9\u5B89\u88C5\u5230\u54EA\u4E2A\u76EE\u5F55\u4E0B";   // 选择安装到哪个目录下
        fb.ShowNewFolderButton = true;
        string cur = txtDir.Text.Trim();
        try { if (Directory.Exists(cur)) fb.SelectedPath = cur; }
        catch { }
        if (fb.ShowDialog(this) != DialogResult.OK || fb.SelectedPath.Length == 0) return;

        string p = fb.SelectedPath.TrimEnd('\\');
        // 选中的目录本身就叫「任务便签」时，不再叠加一层
        if (string.Equals(Path.GetFileName(p), AppInfo.Name, StringComparison.OrdinalIgnoreCase))
            txtDir.Text = p;
        else
            txtDir.Text = Path.Combine(p, AppInfo.Name);
    }

    void OnInstall()
    {
        string raw = txtDir.Text.Trim();
        if (raw.Length == 0)
        {
            Warn("\u8BF7\u5148\u9009\u62E9\u5B89\u88C5\u76EE\u5F55\u3002");   // 请先选择安装目录。
            return;
        }

        string dir;
        try { dir = Path.GetFullPath(raw); }
        catch (Exception ex)
        {
            Warn("\u8DEF\u5F84\u4E0D\u5408\u6CD5\uFF1A" + ex.Message);        // 路径不合法：
            return;
        }

        // 校验交给 InstallCore（与 --install 无界面模式共用同一套判定）
        string why = InstallCore.Prepare(dir);
        if (why != null)
        {
            Warn(why);
            return;
        }

        ProgressForm pf = new ProgressForm(dir);
        pf.ShowDialog(this);

        if (pf.Error != null)
        {
            MessageBox.Show(this, "\u5B89\u88C5\u5931\u8D25\uFF1A\n" + pf.Error.Message, AppInfo.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Error);                    // 安装失败
            return;
        }

        FinishForm ff = new FinishForm(dir);
        ff.ShowDialog(this);
        Close();
    }

    // --auto：预填目录后直接开始安装（自动化验证用；正常双击不带此参数）
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (autoRun) BeginInvoke(new MethodInvoker(OnInstall));
    }

    void Warn(string msg)
    {
        MessageBox.Show(this, msg, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}

// ---------------------------------------------------------------------------
//  第 2 步：释放文件 + 建快捷方式 + 登记卸载信息
// ---------------------------------------------------------------------------
class ProgressForm : Form
{
    readonly string dir;
    readonly Label lbl;
    readonly ProgressBar bar;
    public Exception Error;

    public ProgressForm(string targetDir)
    {
        dir = targetDir;
        Text = "\u6B63\u5728\u5B89\u88C5";                                  // 正在安装
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(424, 112);
        Util.AppIcon(this);

        lbl = new Label();
        lbl.SetBounds(18, 18, 388, 20);
        lbl.Text = "\u6B63\u5728\u51C6\u5907...";                            // 正在准备...
        Controls.Add(lbl);

        bar = new ProgressBar();
        bar.SetBounds(18, 46, 388, 18);
        bar.Minimum = 0;
        bar.Maximum = Math.Max(1, Payload.Total());
        Controls.Add(bar);

        Label hint = new Label();
        hint.Text = "\u5168\u7A0B\u672C\u5730\u5B8C\u6210\uFF0C\u4E0D\u8054\u7F51\u3002";
        hint.ForeColor = Color.FromArgb(130, 130, 138);
        hint.SetBounds(18, 76, 388, 18);
        Controls.Add(hint);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Application.DoEvents();
        try
        {
            for (int i = 0; i < Payload.Total(); i++)
            {
                bar.Value = i;
                lbl.Text = "\u6B63\u5728\u5B89\u88C5\uFF1A" + Embedded.Files[i].Rel;   // 正在安装：
                Application.DoEvents();
                Payload.Materialize(i, dir);
            }

            bar.Value = bar.Maximum;
            lbl.Text = "\u6B63\u5728\u521B\u5EFA\u5FEB\u6377\u65B9\u5F0F...";   // 正在创建快捷方式...
            Application.DoEvents();

            string created = InstallCore.CreateShortcuts(dir, true);
            InstallCore.WriteRegistry(dir);

            if (created.Length == 0)
                throw new Exception("\u5FEB\u6377\u65B9\u5F0F\u521B\u5EFA\u5931\u8D25\uFF08\u684C\u9762\u4E0E\u5F00\u59CB\u83DC\u5355\u90FD\u6CA1\u6210\u529F\uFF09");
            SuccessInfo = created;
        }
        catch (Exception ex)
        {
            Error = ex;
        }
        Close();
    }

    public string SuccessInfo = "";
}

// ---------------------------------------------------------------------------
//  第 3 步：完成
// ---------------------------------------------------------------------------
class FinishForm : Form
{
    readonly CheckBox chkRun;
    readonly string dir;

    public FinishForm(string installDir)
    {
        dir = installDir;
        Text = "\u5B89\u88C5\u5B8C\u6210";                                    // 安装完成
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(492, 268);
        Util.AppIcon(this);

        Label t = new Label();
        t.Text = AppInfo.Name + " \u5DF2\u5B89\u88C5\u5B8C\u6210";             // 已安装完成
        t.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold);
        t.SetBounds(24, 20, 440, 28);
        Controls.Add(t);

        Label p = new Label();
        p.Text = "\u5B89\u88C5\u4F4D\u7F6E\uFF1A" + dir;                      // 安装位置：
        p.SetBounds(26, 54, 440, 20);
        Controls.Add(p);

        Label d = new Label();
        d.Text = "\u6570\u636E\u6587\u4EF6\uFF1A" + Path.Combine(dir, "data") + "\\notes.json\n"
               + "\u4FBF\u7B7E\u6570\u636E\u5C31\u5728\u8FD9\u4E00\u4E2A\u6587\u4EF6\u91CC\uFF0C"
               + "\u60F3\u5907\u4EFD\u6216\u6362\u7535\u8111\uFF0C\u76F4\u63A5\u628A\u5B83\u62F7\u8D70\u5373\u53EF\u3002";
        d.ForeColor = Color.FromArgb(104, 104, 112);
        d.SetBounds(26, 76, 440, 38);
        Controls.Add(d);

        Label h = new Label();
        h.Text = "\u25CF \u684C\u9762\u5FEB\u6377\u65B9\u5F0F\u5DF2\u521B\u5EFA\n"
               + "\u25CF \u5F00\u59CB\u83DC\u5355\u5DF2\u6DFB\u52A0\n"
               + "\u25CF \u53EF\u5728\u300C\u8BBE\u7F6E \u2192 \u5E94\u7528 \u2192 \u5DF2\u5B89\u88C5\u7684\u5E94\u7528\u300D\u91CC\u5378\u8F7D"
               + "\uFF08\u5378\u8F7D\u4E0D\u5220\u60A8\u7684\u4FBF\u7B7E\uFF09";
        h.SetBounds(26, 120, 440, 60);
        Controls.Add(h);

        chkRun = new CheckBox();
        chkRun.Text = "\u7ACB\u5373\u8FD0\u884C " + AppInfo.Name;               // 立即运行
        chkRun.Checked = true;
        chkRun.SetBounds(28, 190, 300, 24);
        Controls.Add(chkRun);

        Button ok = new Button();
        ok.Text = "\u5B8C\u6210";                                              // 完成
        ok.SetBounds(384, 186, 80, 28);
        ok.Click += delegate(object s, EventArgs e) { OnFinish(); };
        Controls.Add(ok);
        AcceptButton = ok;
    }

    void OnFinish()
    {
        if (chkRun.Checked)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(dir, AppInfo.Name + ".exe"))
                {
                    WorkingDirectory = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "\u542F\u52A8\u5931\u8D25\uFF1A" + ex.Message, AppInfo.Name,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);                // 启动失败
            }
        }
        Close();
    }
}

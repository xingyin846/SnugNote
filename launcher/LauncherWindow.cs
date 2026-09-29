// 贴贴便签 v23 · 主窗口（网页风格化）
//
// 用户要求：「能否把贴贴便签.exe 的控制界面改成和网页相似的风格」——先出了三档预览
// （.tools\v23-style-demo\，含 PNG 与可双击的真实窗口），用户选定 **方案 B · 全网页化**
// （无边框圆角窗 + 自绘标题栏）并同时要求把「启动器半」改成 DPI 感知。本文件就是那一档的落地。
//
// 与预览 demo 的关系：demo 里那份绘制代码就是本批的"规格示意"——配色、圆角、间距、字号
// 与 demo 的 PaintB 逐项对应（demo\shots\preview-B-web.png 即验收时的对照图）。
//
// 自绘 / 真控件的分界（**这条是判据决定的，不是审美决定的**）：
//   * 自绘：窗口底色、标题栏、品牌块（图钉沿用 Geometry.DrawPin 同形体）、地址胶囊底、
//     日志卡片、状态胶囊、两个按钮、最小化/关闭按钮；
//   * 真控件：地址框与日志区仍是真 TextBox（选中/复制/滚动一个不丢），「开机自动启动」
//     仍是真 CheckBox —— v20 的检查脚本按 UIA 名字找到它、送 BM_CLICK 驱动
//     CheckedChanged 真接线，换成自绘控件那条判据就驱动不到被测行为了（宁可留一个原生方块）。
//
// DPI（v23 新增）：Program.Main 在**启动器模式**下声明 DPI 感知（贴纸半是另一个进程，
// 保持不感知，卡片坐标不受影响）。于是这里所有尺寸都按 dpi/96 手工缩放：绘制用像素单位字体
// （乘 S），真控件的字体用磅值（GDI+ 自己按 DPI 放大）——两者最终像素高度一致。
//
// 这个文件属于 launcher/（可以用中文，构建时 /codepage:65001）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// ---------------- 配色（与 demo/styles.css 的 :root 变量一一对应） ----------------
static class SkinPal
{
    public static Color H(string s) { return ColorTranslator.FromHtml(s); }

    public static readonly Color Bg = H("#f6f4ef");        // --bg
    public static readonly Color Surface = Color.White;    // --surface
    public static readonly Color Text = H("#24212b");      // --text
    public static readonly Color Soft = H("#6d6875");      // --text-soft
    public static readonly Color Border = H("#e8e3da");    // --border
    public static readonly Color Chip = H("#efece6");      // --chip-bg
    public static readonly Color Accent = H("#ff7a9e");    // 主色（粉）
    public static readonly Color Accent2 = H("#ffb05c");   // 渐变另一头（橙）
    public static readonly Color LogoA = H("#ffd36e");
    public static readonly Color LogoB = H("#ff8fab");
    public static readonly Color Green = H("#4fae6b");     // 状态点（调色板外新增，仅状态用）
}

// ---------------- 绘图原语 ----------------
static class SkinDraw
{
    public static GraphicsPath Round(RectangleF r, float rad)
    {
        GraphicsPath p = new GraphicsPath();
        if (rad <= 0.5f) { p.AddRectangle(r); return p; }
        float d = rad * 2f;
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        p.AddArc(r.X, r.Y, d, d, 180f, 90f);
        p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
        p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
        p.CloseFigure();
        return p;
    }

    public static void FillG(Graphics g, RectangleF r, float rad, Color c)
    {
        using (GraphicsPath p = Round(r, rad))
        using (SolidBrush b = new SolidBrush(c))
            g.FillPath(b, p);
    }

    public static void FillGB(Graphics g, RectangleF r, float rad, Brush b)
    {
        using (GraphicsPath p = Round(r, rad)) g.FillPath(b, p);
    }

    public static void StrokeG(Graphics g, RectangleF r, float rad, Color c, float w)
    {
        using (GraphicsPath p = Round(r, rad))
        using (Pen pen = new Pen(c, w))
            g.DrawPath(pen, p);
    }

    /// <summary>很浅的投影：几层半透明圆角矩形往下叠（窗口本体另有 CS_DROPSHADOW）。</summary>
    public static void Shadow(Graphics g, RectangleF r, float rad)
    {
        for (int i = 4; i >= 1; i--)
        {
            float off = i * 1.0f;
            int a = 6 + (4 - i) * 4;
            RectangleF rr = new RectangleF(r.X - i * 0.6f, r.Y + off, r.Width + i * 1.2f, r.Height + i * 1.2f);
            FillG(g, rr, rad + i * 0.5f, Color.FromArgb(a, 30, 25, 45));
        }
    }

    /// <summary>网页里的 135deg 渐变（#ffb05c → #ff7a9e）。</summary>
    public static Brush Grad(RectangleF r, Color a, Color b)
    {
        RectangleF rr = r;
        if (rr.Width < 1f) rr.Width = 1f;
        if (rr.Height < 1f) rr.Height = 1f;
        return new LinearGradientBrush(rr, a, b, 45f);
    }

    public static void Txt(Graphics g, string s, Font f, Color c, float x, float y)
    {
        using (SolidBrush b = new SolidBrush(c)) g.DrawString(s, f, b, x, y);
    }

    public static SizeF Measure(Graphics g, string s, Font f) { return g.MeasureString(s, f); }

    public static void TxtIn(Graphics g, string s, Font f, Color c, RectangleF box,
                             StringAlignment h, StringAlignment v, bool ellipsis)
    {
        using (StringFormat sf = new StringFormat())
        {
            sf.FormatFlags = StringFormatFlags.NoWrap;
            sf.Alignment = h;
            sf.LineAlignment = v;
            if (ellipsis) sf.Trimming = StringTrimming.EllipsisCharacter;
            using (SolidBrush b = new SolidBrush(c)) g.DrawString(s, f, b, box, sf);
        }
    }

    /// <summary>图钉（与 desktop-sticker/src/Geometry.cs 的 DrawPin 同一形体，按 box 缩放）。</summary>
    public static void Pin(Graphics g, RectangleF box, Color c)
    {
        float s = Math.Min(box.Width, box.Height) / 25f;
        GraphicsState st = g.Save();
        g.TranslateTransform(box.X, box.Y);
        g.ScaleTransform(s, s);
        using (SolidBrush b = new SolidBrush(c))
        {
            g.FillEllipse(b, 5f, 3.5f, 15f, 9f);
            g.FillRectangle(b, 10.5f, 12f, 4f, 4f);
            g.FillPolygon(b, new PointF[] {
                new PointF(10f, 16f), new PointF(15f, 16f), new PointF(12.5f, 25f) });
        }
        using (Pen p = new Pen(c, 2.4f))
        {
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            g.DrawLine(p, 12.5f, 23f, 12.5f, 27f);
        }
        g.Restore(st);
    }

    /// <summary>把文字画进一个矩形，并在其中垂直居中（状态胶囊/按钮内部用）。</summary>
    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }
}

// ---------------- 字体缓存（像素单位：调用方已经把 DPI 系数乘进去） ----------------
static class SkinFonts
{
    static readonly Dictionary<string, Font> map = new Dictionary<string, Font>();
    public static Font Get(string fam, float px, FontStyle st)
    {
        string k = fam + "|" + px.ToString("0.##") + "|" + ((int)st).ToString();
        Font f;
        if (!map.TryGetValue(k, out f))
        {
            f = new Font(fam, px, st, GraphicsUnit.Pixel);
            map[k] = f;
        }
        return f;
    }
}

// ---------------- 窗口面板：底色 + 标题栏 + 卡片 + 状态（按钮/输入框是真控件，叠在上面） ----------------
class SkinPanel : Control
{
    public const string Ui = "Microsoft YaHei UI";

    public float S = 1f;
    public string DataPath = "";

    string statusText = "";
    bool statusRunning;

    public SkinPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = SkinPal.Bg;
    }

    public float Px(float v) { return v * S; }
    public int H(float v) { return (int)Math.Round(v * S); }
    public RectangleF R(float x, float y, float w, float h)
    {
        return new RectangleF(x * S, y * S, w * S, h * S);
    }

    /// <summary>状态胶囊的文案（贴纸在不在跑 / 贴了几张）。只在真的变了时才重画。</summary>
    public void SetStatus(string text, bool running)
    {
        if (text == statusText && running == statusRunning) return;
        statusText = text;
        statusRunning = running;
        try { Invalidate(); } catch { }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { /* 全部由 OnPaint 画，避免闪 */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        SkinDraw.Prepare(g);
        using (SolidBrush b = new SolidBrush(SkinPal.Bg)) g.FillRectangle(b, ClientRectangle);

        // ---- 自绘标题栏（白底 + 一条下边线；最小化/关闭是它上面的真控件）----
        using (SolidBrush b = new SolidBrush(SkinPal.Surface)) g.FillRectangle(b, R(0, 0, 520, 46));
        using (Pen p = new Pen(SkinPal.Border, Px(1f)))
            g.DrawLine(p, 0, R(0, 0, 1, 46).Bottom - Px(0.5f), ClientRectangle.Right, R(0, 0, 1, 46).Bottom - Px(0.5f));

        Brand(g, R(16, 11, 24, 24));
        SkinDraw.TxtIn(g, "贴贴便签", SkinFonts.Get(Ui, Px(14f), FontStyle.Bold), SkinPal.Text,
            R(48, 11, 200, 24), StringAlignment.Near, StringAlignment.Center, false);

        // ---- 界面地址：标签 + 胶囊底（正文是真 TextBox，右边留出复制图标的位置）----
        SkinDraw.TxtIn(g, "界面地址（可复制到浏览器打开）", SkinFonts.Get(Ui, Px(12f), FontStyle.Regular), SkinPal.Soft,
            R(18, 62, 400, 18), StringAlignment.Near, StringAlignment.Center, false);
        UrlPill(g, R(18, 84, 320, 34));

        // ---- 数据文件 ----
        SkinDraw.TxtIn(g, "数据文件：" + DataPath, SkinFonts.Get(Ui, Px(11.5f), FontStyle.Regular), SkinPal.Soft,
            R(18, 126, 484, 18), StringAlignment.Near, StringAlignment.Center, true);

        // ---- 运行日志：标签 +（同一行右对齐的）状态胶囊 + 日志卡片 ----
        SkinDraw.TxtIn(g, "运行日志", SkinFonts.Get(Ui, Px(12f), FontStyle.Bold), SkinPal.Soft,
            R(18, 150, 200, 18), StringAlignment.Near, StringAlignment.Center, false);
        if (statusText.Length > 0) StatusPill(g, Px(502f), Px(147f));

        LogCard(g, R(18, 170, 484, 118));
    }

    void Brand(Graphics g, RectangleF box)
    {
        float rad = box.Width * 0.32f;
        using (Brush b = SkinDraw.Grad(box, SkinPal.LogoA, SkinPal.LogoB)) SkinDraw.FillGB(g, box, rad, b);
        float inset = box.Width * 0.22f;
        SkinDraw.Pin(g, new RectangleF(box.X + inset, box.Y + inset, box.Width - inset * 2, box.Height - inset * 2), Color.White);
    }

    void UrlPill(Graphics g, RectangleF r)
    {
        SkinDraw.Shadow(g, r, r.Height / 2f);
        SkinDraw.FillG(g, r, r.Height / 2f, SkinPal.Surface);
        SkinDraw.StrokeG(g, r, r.Height / 2f, SkinPal.Border, Px(1f));
        // 复制图标（两个叠着的圆角方框）——真 TextBox 只占左边 266px，这里不会被盖住
        float s = Px(9f);
        RectangleF a = new RectangleF(r.Right - Px(30f), r.Y + (r.Height - s) / 2f, s, s);
        RectangleF b = new RectangleF(a.X + Px(3.2f), a.Y + Px(3.2f), s, s);
        using (Pen p = new Pen(SkinPal.Soft, Px(1.2f)))
        {
            using (GraphicsPath pa = SkinDraw.Round(a, Px(2.4f))) g.DrawPath(p, pa);
            using (GraphicsPath pb = SkinDraw.Round(b, Px(2.4f))) g.DrawPath(p, pb);
        }
    }

    void LogCard(Graphics g, RectangleF card)
    {
        SkinDraw.Shadow(g, card, Px(12f));
        SkinDraw.FillG(g, card, Px(12f), SkinPal.Surface);
        SkinDraw.StrokeG(g, card, Px(12f), SkinPal.Border, Px(1f));
    }

    void StatusPill(Graphics g, float rightX, float y)
    {
        Font f = SkinFonts.Get(Ui, Px(11.5f), FontStyle.Regular);
        SizeF sz = SkinDraw.Measure(g, statusText, f);
        float dot = Px(7f), padX = Px(11f), h = Px(24f), gap = Px(6f);
        float w = padX * 2 + dot + gap + sz.Width;
        RectangleF r = new RectangleF(rightX - w, y, w, h);
        SkinDraw.FillG(g, r, h / 2f, SkinPal.Chip);
        using (SolidBrush b = new SolidBrush(statusRunning ? SkinPal.Green : SkinPal.Soft))
            g.FillEllipse(b, r.X + padX, r.Y + (h - dot) / 2f, dot, dot);
        SkinDraw.TxtIn(g, statusText, f, statusRunning ? SkinPal.Text : SkinPal.Soft,
            new RectangleF(r.X + padX + dot + gap, r.Y, sz.Width + Px(2f), h),
            StringAlignment.Near, StringAlignment.Center, false);
    }

    // ---- 标题栏空白处按下 = 拖窗口（无边框窗没有系统标题栏可用）----
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && e.Y < H(46))
        {
            ReleaseCapture();
            Form f = FindForm();
            if (f != null) SendMessage(f.Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // WM_NCLBUTTONDOWN / HTCAPTION
        }
        base.OnMouseDown(e);
    }

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}

// ---------------- 自绘按钮（主按钮 / 次要按钮 / 最小化 / 关闭） ----------------
class SkinButton : Control, IButtonControl
{
    public enum BtnStyle { Primary, Soft, IconMin, IconClose }

    public BtnStyle Style = BtnStyle.Soft;
    public float S = 1f;

    /// <summary>我们的"被按下"事件。**刻意不用 WinForms 的 Click**：自绘控件（直接继承 Control）
    /// 不走 ButtonBase 那套 StandardClick 合成，合成消息（自动化验证里送的 WM_LBUTTONDOWN/UP）
    /// 也不一定会合出 Click；OnMouseDown/OnMouseUp 是确定会被调到的两个点，动作挂在这里最稳。</summary>
    public event EventHandler Pressed;

    bool hover;
    bool pressedOnMe;

    public SkinButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = SkinPal.Bg;
    }

    // ---- IButtonControl：让 form.AcceptButton 仍能把回车接到「打开便签界面」----
    public DialogResult DialogResult { get { return DialogResult.None; } set { } }
    public void NotifyDefault(bool value) { }
    public void PerformClick() { if (Enabled) Raise(); }

    void Raise() { EventHandler h = Pressed; if (h != null) h(this, EventArgs.Empty); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { pressedOnMe = true; Invalidate(); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        bool hit = pressedOnMe && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
        pressedOnMe = false;
        Invalidate();
        if (hit && Enabled) Raise();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { e.Handled = true; Raise(); }
        base.OnKeyDown(e);
    }

    float Px(float v) { return v * S; }
    int H(float v) { return (int)Math.Round(v * S); }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaintBackground(PaintEventArgs e) { /* OnPaint 里先铺底色 */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        SkinDraw.Prepare(g);
        using (SolidBrush b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);

        // 控件比按钮本体大一圈（PadLogical），留出投影/悬停上浮的空间
        float pad = Px(4f);
        RectangleF r = new RectangleF(pad, pad, Width - pad * 2, Height - pad * 2);
        if (Style == BtnStyle.IconMin || Style == BtnStyle.IconClose)
            PaintIconButton(g, r);
        else
            PaintTextButton(g, r);
    }

    void PaintTextButton(Graphics g, RectangleF r)
    {
        Font f = SkinFonts.Get(SkinPanel.Ui, Px(13.5f), FontStyle.Bold);
        if (Style == BtnStyle.Primary)
        {
            if (hover) r = new RectangleF(r.X, r.Y - Px(1f), r.Width, r.Height);
            SkinDraw.Shadow(g, r, Px(12f));
            using (Brush b = SkinDraw.Grad(r, SkinPal.Accent2, SkinPal.Accent)) SkinDraw.FillGB(g, r, Px(12f), b);
            SkinDraw.TxtIn(g, Text, f, Color.White, r, StringAlignment.Center, StringAlignment.Center, false);
            return;
        }
        if (hover) SkinDraw.FillG(g, r, Px(12f), SkinPal.Chip);
        else
        {
            SkinDraw.FillG(g, r, Px(12f), SkinPal.Surface);
            SkinDraw.StrokeG(g, r, Px(12f), SkinPal.Border, Px(1f));
        }
        SkinDraw.TxtIn(g, Text, f, hover ? SkinPal.Text : SkinPal.Soft, r,
            StringAlignment.Center, StringAlignment.Center, false);
    }

    void PaintIconButton(Graphics g, RectangleF r)
    {
        bool close = (Style == BtnStyle.IconClose);
        SkinDraw.FillG(g, r, Px(10f), hover ? (close ? SkinPal.Accent : SkinPal.Chip) : SkinPal.Surface);
        if (!hover) SkinDraw.StrokeG(g, r, Px(10f), SkinPal.Border, Px(1f));
        Color c = (hover && close) ? Color.White : SkinPal.Soft;
        float m = Px(close ? 10f : 9f);
        using (Pen p = new Pen(c, Px(1.7f)))
        {
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            if (close)
            {
                g.DrawLine(p, r.X + m, r.Y + m, r.Right - m, r.Bottom - m);
                g.DrawLine(p, r.Right - m, r.Y + m, r.X + m, r.Bottom - m);
            }
            else
            {
                g.DrawLine(p, r.X + m, r.Y + r.Height / 2f, r.Right - m, r.Y + r.Height / 2f);
            }
        }
    }
}

/// <summary>无边框圆角窗：CS_DROPSHADOW 给一点系统投影（Region 已裁成圆角）。</summary>
class SkinForm : Form
{
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000;
            return cp;
        }
    }
}

class LauncherWindow : IDisposable
{
    readonly SkinForm form = new SkinForm();
    readonly SkinPanel panel = new SkinPanel();
    readonly TextBox logBox = new TextBox();
    readonly TextBox urlBox = new TextBox();
    readonly CheckBox autoStart = new CheckBox();   // v20：开机自启动（真控件，见文件头说明）
    readonly SkinButton openBtn = new SkinButton();
    readonly SkinButton exitBtn = new SkinButton();
    readonly SkinButton minBtn = new SkinButton();
    readonly SkinButton closeBtn = new SkinButton();
    readonly Timer timer = new Timer();

    readonly float S;                    // v23：dpi/96，所有尺寸按它缩放
    readonly string dataPath;
    readonly Func<int> placedCount;      // 已贴出的便签张数（-1 = 读不到）
    readonly Func<bool> stickerRunning;  // 贴纸模式是否在跑（它自己的单实例互斥体说了算）
    readonly Action onExit;              // 关窗/点退出时要执行的收尾动作

    int logLines;
    bool exited;
    bool syncingAutoStart;   // v20：程序自己改勾选态时，别把它当成"用户点了复选框"

    /// <summary>窗口停靠点：Program.Main 用 Application.Run(win.Form) 起消息循环。</summary>
    public Form Form { get { return form; } }

    int H(float v) { return (int)Math.Round(v * S); }

    public LauncherWindow(string url, string dataPath, Func<int> placedCount, Func<bool> stickerRunning, Action onExit)
    {
        this.dataPath = dataPath;
        this.placedCount = placedCount;
        this.stickerRunning = stickerRunning;
        this.onExit = onExit;
        S = DpiScale();

        // ---------- 窗体本体：无边框 + 圆角 + 任务栏里是一个普通应用 ----------
        form.Text = "贴贴便签";
        form.FormBorderStyle = FormBorderStyle.None;
        form.AutoScaleMode = AutoScaleMode.None;
        form.ShowInTaskbar = true;
        form.MinimizeBox = true;   // 无边框窗也要能最小化：缺了 WS_MINIMIZEBOX，ShowWindow(SW_MINIMIZE) 会被拒
        form.StartPosition = FormStartPosition.CenterScreen;
        form.BackColor = SkinPal.Bg;
        form.ClientSize = new Size(H(520), H(336));
        try { form.Region = new Region(SkinDraw.Round(new RectangleF(0, 0, form.ClientSize.Width, form.ClientSize.Height), H(18))); }
        catch { }
        try { form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        panel.S = S;
        panel.DataPath = this.dataPath;
        panel.Dock = DockStyle.Fill;
        panel.BackColor = SkinPal.Bg;
        form.Controls.Add(panel);

        // ---------- 界面地址（真 TextBox，可选中/复制；胶囊底由面板自绘）----------
        urlBox.Text = url;
        urlBox.ReadOnly = true;
        urlBox.BorderStyle = BorderStyle.None;
        urlBox.BackColor = SkinPal.Surface;
        urlBox.ForeColor = SkinPal.Text;
        urlBox.Font = new Font("Consolas", 9.4f);      // ≈12.5px @96dpi；DPI 感知下由 GDI+ 放大
        int pillTop = H(84), pillH = H(34);
        int ubH = urlBox.PreferredHeight;
        urlBox.SetBounds(H(32), pillTop + (pillH - ubH) / 2, H(266), ubH);
        urlBox.TabStop = true;
        panel.Controls.Add(urlBox);

        // ---------- 打开便签界面（主按钮）----------
        openBtn.Style = SkinButton.BtnStyle.Primary;
        openBtn.Text = "打开便签界面";
        openBtn.S = S;
        openBtn.BackColor = SkinPal.Bg;
        openBtn.SetBounds(H(346 - 4), H(84 - 4), H(156 + 8), H(34 + 8));
        openBtn.Pressed += delegate { OpenBrowser(); };
        panel.Controls.Add(openBtn);
        form.AcceptButton = openBtn;

        // ---------- v20：开机自启动（真 CheckBox，勾选态从注册表读回来）----------
        // 顺序要紧：先设 Checked、再挂 CheckedChanged —— 否则这一句赋值会被当成"用户点了复选框"
        // 而立刻去写注册表（用户什么都没做，启动项却变了）。
        autoStart.Text = "开机自动启动（自动恢复桌面便签）";
        autoStart.AutoSize = false;
        autoStart.FlatStyle = FlatStyle.Flat;
        autoStart.BackColor = SkinPal.Bg;
        autoStart.ForeColor = SkinPal.Text;
        autoStart.Font = new Font("Microsoft YaHei UI", 9.4f);
        autoStart.TextAlign = ContentAlignment.MiddleLeft;
        autoStart.SetBounds(H(18), H(294), H(330), H(24));
        autoStart.Checked = (AutoStart.State() == "on");
        autoStart.CheckedChanged += delegate { OnAutoStartToggled(); };
        panel.Controls.Add(autoStart);

        // ---------- 退出 / 最小化 / 关闭 ----------
        exitBtn.Style = SkinButton.BtnStyle.Soft;
        exitBtn.Text = "退出";
        exitBtn.S = S;
        exitBtn.BackColor = SkinPal.Bg;
        exitBtn.SetBounds(H(394 - 4), H(291 - 4), H(108 + 8), H(30 + 8));
        exitBtn.Pressed += delegate { RequestExit(); };
        panel.Controls.Add(exitBtn);

        minBtn.Style = SkinButton.BtnStyle.IconMin;
        minBtn.Text = "最小化";
        minBtn.S = S;
        minBtn.BackColor = SkinPal.Surface;
        minBtn.TabStop = false;
        minBtn.SetBounds(H(520 - 74 - 4), H(9 - 4), H(28 + 8), H(28 + 8));
        minBtn.Pressed += delegate { Minimize(); };
        panel.Controls.Add(minBtn);

        closeBtn.Style = SkinButton.BtnStyle.IconClose;
        closeBtn.Text = "关闭";
        closeBtn.S = S;
        closeBtn.BackColor = SkinPal.Surface;
        closeBtn.TabStop = false;
        closeBtn.SetBounds(H(520 - 40 - 4), H(9 - 4), H(28 + 8), H(28 + 8));
        closeBtn.Pressed += delegate { form.Close(); };
        panel.Controls.Add(closeBtn);

        // ---------- 运行日志（真 TextBox：追加/滚动/选中都照旧）----------
        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.WordWrap = false;
        logBox.BorderStyle = BorderStyle.None;
        logBox.BackColor = SkinPal.Surface;
        logBox.ForeColor = SkinPal.Text;
        logBox.Font = new Font("Consolas", 8.6f);      // ≈11.5px @96dpi
        logBox.SetBounds(H(30), H(180), H(460), H(100));
        panel.Controls.Add(logBox);

        form.FormClosing += delegate(object s, FormClosingEventArgs e) { RequestExit(); };

        timer.Interval = 1500;
        timer.Tick += delegate { RefreshStatus(); };
        timer.Start();
        RefreshStatus();
    }

    /// <summary>最小化（自绘标题栏上的那个按钮调它）。无边框 + 圆角窗没有系统标题栏，
    /// 所以这里显式走 WindowState；MinimizeBox 必须为 true，否则 SW_MINIMIZE 会被系统拒掉。</summary>
    void Minimize()
    {
        try { form.WindowState = FormWindowState.Minimized; }
        catch (Exception ex) { AppendLog("最小化失败：" + ex.Message); }
    }

    static float DpiScale()
    {
        try
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                float s = g.DpiX / 96f;
                if (s < 0.5f || s > 4f) s = 1f;
                return s;
            }
        }
        catch { return 1f; }
    }

    /// <summary>把一行运行信息写进窗口（任意线程可调用）。这就是控制台的替代品。</summary>
    public void AppendLog(string line)
    {
        try
        {
            if (form.IsDisposed) return;
            if (form.InvokeRequired) { form.BeginInvoke(new Action<string>(AppendLog), new object[] { line }); return; }
            if (++logLines > 400)                 // 只留最近 400 行，别让长跑会话把内存撑起来
            {
                logBox.Clear();
                logBox.AppendText("（只显示最近 400 行）" + Environment.NewLine);
                logLines = 1;
            }
            logBox.AppendText(line + Environment.NewLine);
        }
        catch { }
    }

    void RefreshStatus()
    {
        try
        {
            bool running = false;
            try { running = stickerRunning(); } catch { }
            int n = -1;
            try { n = placedCount(); } catch { }
            string text;
            if (!running) text = "桌面贴纸：未运行";
            else if (n < 0) text = "桌面贴纸：运行中（贴出张数读不到）";
            else text = "桌面贴纸：运行中 · 已贴出 " + n + " 张";
            panel.SetStatus(text, running);
        }
        catch { }
    }

    void OpenBrowser()
    {
        // v26：优先开「已安装的应用」（Chrome/Edge 装出来的 PWA）；没装过才退回开网址。
        // 用户 2026-09-29 报的落差正在这里：把应用装好之后，点这个按钮出来的却是浏览器标签页。
        try { AppendLog(AppLaunch.Open(Url())); }
        catch (Exception ex) { AppendLog("未能打开界面：" + ex.Message + "（请手动访问 " + Url() + "）"); }
    }

    string Url() { return urlBox.Text; }

    void RequestExit()
    {
        if (exited) return;
        exited = true;
        try { if (onExit != null) onExit(); } catch { }
    }

    /// <summary>v20：复选框被点 ⇒ 写/删注册表里那条启动项，然后把勾选态重新对齐到"注册表里的真实情况"。
    /// 为什么要重新对齐：写失败（权限、注册表被策略锁住、路径取不到）时勾选框必须自己弹回去 ——
    /// 界面替失败撒谎比功能没做更糟：用户会以为已经设好了。
    /// 这一段就是本程序唯一的启动项写者（AutoStart.Enable / AutoStart.Disable）。</summary>
    void OnAutoStartToggled()
    {
        if (syncingAutoStart) return;
        bool want = autoStart.Checked;
        string err;
        bool ok = want ? AutoStart.Enable(out err) : AutoStart.Disable(out err);
        if (ok) AppendLog("开机自启动：" + AutoStart.Describe());
        else AppendLog("开机自启动：设置失败（" + err + "），已保持原样。");
        SyncAutoStartBox();
    }

    /// <summary>把勾选态同步成注册表里的真实值（此间不触发 OnAutoStartToggled）。</summary>
    void SyncAutoStartBox()
    {
        try
        {
            syncingAutoStart = true;
            autoStart.Checked = (AutoStart.State() == "on");
        }
        catch { }
        finally { syncingAutoStart = false; }
    }

    public void Dispose()
    {
        try { timer.Stop(); timer.Dispose(); } catch { }
        try { if (!form.IsDisposed) form.Dispose(); } catch { }
    }
}

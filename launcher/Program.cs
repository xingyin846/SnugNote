// 贴贴便签 · 启动器 + 贴纸程序（v14 起是同一个 exe）
// 作用：双击运行后，在本机启动一个极简 HTTP 服务（仅绑定 127.0.0.1）：
//       ① 静态文件服务 → 便签界面
//       ② 数据接口 /api/notes（GET 读 / PUT 写）→ 数据落盘到 data\notes.json
//       ③ v13 桌面桥接口 /api/desktop（GET 读已贴出清单 / POST 追加一条请求）
// 启动后自动打开默认浏览器，并打开一个正规窗口（LauncherWindow）—— 关掉窗口即退出全部。
// 特点：不依赖 Python / Node，不联网，不需要管理员权限。
// 用法：贴贴便签.exe [端口] [--no-open] [--headless]
//         --no-open   不自动开浏览器（自动化验证用）
//         --headless  不建窗口，只跑服务（自动化验证用：避免测试时弹出窗口抢焦点）
//
// v14（用户要求「把 Sticker.exe 整合进 贴贴便签.exe」）：本 exe 同时装着两套代码——
//   * 不带贴纸开关运行 = 启动器（本文件的 TietieLauncher，winexe + LauncherWindow）；
//   * 带 --sticker（或任何贴纸专用开关，如 --selftest/--note/--render/--toggle-check）= 贴纸程序
//     （desktop-sticker/src 的 TieTieSticker.Program），由 Main 在这里派发过去。
//   于是「点『贴到桌面』自动启动贴纸」= 启动自己，那个「找不到 Sticker.exe」的失败类彻底消失。

using Microsoft.Win32;                     // v20：开机自启动 = HKCU\...\Run 里的一条值（无需管理员）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class TietieLauncher
{
    static string baseDir;
    static int port;
    static NoteStore notes;
    static TcpListener listener;                 // v14: 退出时要从窗口那边停掉它，所以提到字段

    /// <summary>v13.1: exe 所在目录（v14 起自动启动贴纸 = 启动本 exe 自己）。</summary>
    internal static string ExeDir;

    /// <summary>v13: the file-relay bridge to the desktop sticker app. The launcher is the ONLY
    /// writer of data\bridge\request.json and the ONLY reader of data\bridge\placed.json.</summary>
    static DesktopBridge bridge;

    /// <summary>v14: 运行信息的去处。控制台没了（winexe），窗口就是新的"控制台"；
    /// 自动化（--headless）时这个钩子为空，信息照旧走 stdout（重定向时仍可抓）。</summary>
    static Action<string> logSink;
    static bool shuttingDown;

    /// <summary>v14: 贴纸模式的开关。任何一个出现在命令行里 = 本进程扮演贴纸程序。</summary>
    static readonly string[] StickerFlags = new string[]
    {
        "--sticker", "--selftest", "--note", "--notes", "--verbose-hit",
        "--render", "--render-samples", "--floating", "--toggle-check"
    };

    static readonly Dictionary<string, string> Mime =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { ".html", "text/html; charset=utf-8" },
        { ".htm",  "text/html; charset=utf-8" },
        { ".css",  "text/css; charset=utf-8" },
        { ".js",   "application/javascript; charset=utf-8" },
        { ".mjs",  "application/javascript; charset=utf-8" },
        { ".json", "application/json; charset=utf-8" },
        { ".png",  "image/png" },
        { ".jpg",  "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".gif",  "image/gif" },
        { ".svg",  "image/svg+xml" },
        { ".ico",  "image/x-icon" },
        { ".txt",  "text/plain; charset=utf-8" },
        { ".woff", "font/woff" },
        { ".woff2","font/woff2" },
    };

    [STAThread]
    static void Main(string[] args)
    {
        // ---- v14：同一个 exe，两种身份 ----
        // 用户要求「把 Sticker.exe 整合进 贴贴便签.exe」。这里先判身份：命令行里出现贴纸专用
        // 开关（或显式的 --sticker）⇒ 本进程扮演贴纸程序，整条命令行原样交给 TieTieSticker.Program。
        if (IsStickerMode(args))
        {
            TieTieSticker.Program.Main(StickerArgs(args));
            // 贴纸模式的正常路径都以 Environment.Exit 收尾；万一它返回了，也绝不能接着当启动器跑。
            return;
        }

        // ---- v23：启动器半声明 DPI 感知（**只在这一支**，贴纸半绝不碰）----
        // 用户 2026-09-29 选定「方案 B · 全网页化」= 自绘界面；在 125% 缩放下不声明感知的话，
        // 整个窗口会被系统位图拉伸，自绘的圆角/描边/文字全糊掉，等于白做。
        // 位置要紧：必须放在贴纸派发**之后**——贴纸半是**另一个进程**（StickerStarter 用 --sticker
        // 拉起本 exe），一旦它也变成感知，桌面上所有便签的坐标（sticker-state.json 存的是
        // unaware 像素，含用户手工摆好的位置）会整体偏移。
        try { SetProcessDPIAware(); } catch { }

        // ---- v20：开机自启动的命令行入口 ----
        // 与窗口里那个复选框共用 AutoStart 一套实现（同一段代码、同一个写者）；放在这里是为了
        // 让自动化验证**驱动真实程序**去读写启动项，而不是在 PowerShell 里重抄一遍注册表逻辑
        // （重抄的那份永远测不到产品的接线）。验证脚本用 TIETIE_AUTOSTART_ROOT 把它指到测试子键，
        // 所以真实启动项全程不被碰；这里做完就退出，不建窗口、不起服务。
        if (AutoStart.IsCli(args)) Environment.Exit(AutoStart.Cli(args));

        try { Console.Title = "贴贴便签"; } catch { }
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        // ---- v26：命令行测试口 —— 只回答"这次会开什么"，不起服务、不建窗口、不碰任何状态 ----
        // 为什么要有它：本批把「打开便签界面」从「开网址」改成「优先开已安装的应用」。这条**选择
        // 逻辑**必须在自动化里驱动到真实程序（而不是在 PowerShell 里照抄一遍），所以给它一个只读
        // 出口；TIETIE_APP_SEARCH_ROOTS 把搜索根指到沙盒，真实快捷方式全程不被建也不被改。
        if (HasFlag(args, "--print-open-target"))
        {
            // 显式写 UTF-8 字节，不用 Console.WriteLine：本程序是 /target:winexe，stdout 被重定向时
            // Console.OutputEncoding 不一定真的生效，而这条输出里带**中文路径**（快捷方式名）——
            // 按默认代码页写出去、读端按 UTF-8 解，就会变成乱码，判据会假红。
            // 之前所有走 stdout 的自测输出都是 ASCII，所以这个坑一直没露过面。
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(
                    AppLaunch.Resolve("http://127.0.0.1:" + PortArg(args, 8787) + "/") + "\r\n");
                using (Stream so = Console.OpenStandardOutput())
                {
                    so.Write(bytes, 0, bytes.Length);
                    so.Flush();
                }
            }
            catch { }
            return;
        }

        bool headless = HasFlag(args, "--headless");

        // 定位便签界面目录：优先 exe 同级的 demo 目录，其次 exe 自身所在目录
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        ExeDir = exeDir;
        string demoDir = Path.Combine(exeDir, "demo");
        baseDir = Directory.Exists(demoDir) ? demoDir : exeDir;

        // 数据文件：exe 同级的 data\notes.json（整个便签数据 = 一个可备份、可带走的文件）
        notes = new NoteStore(Path.Combine(exeDir, "data", "notes.json"));

        // v13 桌面桥：桥文件放 data\bridge\ 子目录 → data\ 顶层仍只有 notes.json 与 .bak
        bridge = new DesktopBridge(Path.Combine(exeDir, "data", "bridge"));

        if (!File.Exists(Path.Combine(baseDir, "index.html")))
        {
            string msg = "未找到便签界面（index.html），当前查找目录：" + baseDir;
            Say(msg);
            if (!headless)
            {
                MessageBox.Show(msg + "\n\n请把本程序放在包含 demo 文件夹的目录中再运行。",
                    "贴贴便签", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Environment.ExitCode = 1;
            return;
        }

        port = FindFreePort(PortArg(args, 8787));
        StartServer();

        string url = "http://127.0.0.1:" + port + "/";
        bool noOpen = HasNoOpen(args);
        Say("=========================================");
        Say("  贴贴便签  已启动");
        Say("  界面地址：" + url);
        Say("  数据文件：" + notes.FilePath);
        Say("-----------------------------------------");
        Say("  数据由本程序管理，关闭浏览器不丢、清浏览器数据也不丢");
        Say(headless ? "  --headless：只跑服务，不建窗口" : "  关闭窗口即可退出（贴纸也会一起收起来）");
        Say("=========================================");

        try
        {
            if (noOpen)
            {
                // Test affordance (v13): automated verification must not spray browser tabs over the
                // user's desktop. Default behaviour is unchanged - the browser opens unless --no-open.
                Say("（--no-open：已跳过自动打开浏览器，请手动访问上面的地址）");
            }
            else
            {
                // v26：优先开「已安装的应用」（Chrome/Edge 装出来的 PWA），没装过才退回开网址。
                Say(AppLaunch.Open(url));
            }
        }
        catch (Exception ex)
        {
            Say("未能自动打开浏览器：" + ex.Message);
            Say("请手动在浏览器中访问：" + url);
        }

        if (headless)
        {
            // 自动化路径：不建窗口、不碰 WinForms，保持运行直到被外部结束（与 v13 行为一致）。
            while (true) Thread.Sleep(1000);
        }

        // ---- v14：正规窗口替代控制台（任务栏里就是一个普通应用） ----
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        LauncherWindow win = new LauncherWindow(url, notes.FilePath,
            delegate { return bridge.PlacedCount(); },
            delegate { return StickerStarter.IsRunning(); },
            delegate { Shutdown(); });
        logSink = win.AppendLog;
        win.AppendLog("窗口已就绪：任务栏里的「贴贴便签」就是本应用；关闭窗口 = 退出全部。");
        // ---- v20：把开机自启动的当前状态写进日志区 ----
        // 复选框的勾选态本身就来自注册表（LauncherWindow 构造时读的），这里补一句"这意味着什么"：
        // 注册表里有一条但不是本程序当前设置（旧路径/参数不同）时，用户只看勾选框会看不出原因。
        win.AppendLog("开机自启动：" + AutoStart.Describe());

        // ---- v15：启动就把桌面贴纸恢复出来 ----
        // 用户 2026-09-21 报告「退出后重启，桌面上贴的便签不能保留」。根因不是状态丢了：v14 的关窗口
        // 会把贴纸一起收掉（这是用户上次选的口径），而重启后此前**没有任何人**再把贴纸叫起来 —— 全程序
        // 只有"网页点『贴到桌面』"那一条 Ensure 调用（POST /api/desktop 的 place 分支）⇒ 桌面一直是空
        // 的，而 sticker-state.json 里的记录其实都还在。
        // 这里在窗口就绪后立刻按需启动贴纸模式：它自己读 state，把上次贴的那几张原样贴回来（位置/大小/
        // 是否置顶都还原）；记录是空集时按 v11 既有规矩重贴全部未归档便签（用户 2026-09-20 定的口径：
        // 「重启时若放置集合为空，按首启规则重贴全部」，防"全关掉后桌面空空的再也找不回来"）。
        // 三道既有护栏照旧生效：本 exe 的单实例互斥体（已在跑就不重复启动）、8 秒节流、TIETIE_NO_AUTOSTART=1。
        // --headless 走不到这里（上面已 return），自动化验证不受影响。
        string restore = StickerStarter.EnsureStartup(exeDir);
        win.AppendLog("桌面贴纸：" + RestoreText(restore));

        try
        {
            Application.Run(win.Form);
        }
        catch (Exception ex)
        {
            Say("窗口异常：" + ex.GetType().Name + ": " + ex.Message);
        }
        win.Dispose();
    }

    /// <summary>v14: 运行信息唯一的出口 —— 有窗口时进窗口的日志区，任何时候都照写 stdout
    /// （winexe 在 stdout 被重定向时仍可写；没有控制台时 .NET 会把它丢进 null 流，不报错）。</summary>
    internal static void Say(string line)
    {
        try { Console.WriteLine(line); } catch { }
        Action<string> sink = logSink;
        if (sink != null)
        {
            try { sink(line); } catch { }
        }
    }

    /// <summary>v23: 启动器半的 DPI 感知（只被 Main 的启动器分支调用一次）。
    /// 为什么用系统级 SetProcessDPIAware 而不是 per-monitor v2：本机只有一块 125% 的屏，
    /// 且这条 API 从 Vista 起就在，不需要 app.manifest、不改构建方式。</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    /// <summary>v14: 关窗口/点「退出」的收尾 —— 先请贴纸干净退出，再停本地服务。
    /// 为什么先发请求而不是直接杀进程：请求走的是既有的 data\bridge\request.json（本启动器是它唯一的
    /// 写者），贴纸读到后会走它自己的退出路径（Persist 状态、释放互斥体、收掉托盘图标）；杀进程则会
    /// 跳过这些收尾。贴纸侧的"请求太旧就不认"时限判据保证这条请求不会误杀下次启动的贴纸。</summary>
    static void Shutdown()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        try
        {
            if (StickerStarter.IsRunning())
            {
                long seq;
                string err;
                if (bridge.AppendExit(out seq, out err)) Say("已请桌面贴纸退出（exit 请求 seq=" + seq + "）");
                else Say("请桌面贴纸退出失败：" + err);
            }
        }
        catch (Exception ex)
        {
            Say("请桌面贴纸退出时异常：" + ex.GetType().Name + ": " + ex.Message);
        }
        try { if (listener != null) listener.Stop(); } catch { }
        Say("贴贴便签已退出（本地服务已停止）。");
        try { Application.ExitThread(); } catch { }
    }

    /// <summary>v15: 把 StickerStarter.Ensure 的回执翻成窗口日志里的一句话（用户看日志区就知道
    /// 桌面贴纸到底有没有被恢复起来）。</summary>
    static string RestoreText(string state)
    {
        if (state == "running") return "已在运行（未重复启动）。";
        if (state == "started") return "已启动，正在按 sticker-state.json 恢复上次贴出的便签。";
        if (state == "starting") return "刚刚启动过（8 秒节流内），请稍候。";
        if (state == "disabled") return "已跳过（自动化开关 TIETIE_NO_AUTOSTART / TIETIE_NO_STARTUP_STICKER）。";
        if (state == "notfound") return "取不到本程序自身路径，未能启动。";
        return "启动失败，详见下方日志。";
    }

    /// <summary>v14: 命令行里有没有贴纸专用开关（含 --flag=value 形式）。</summary>
    internal static bool IsStickerMode(string[] args)
    {
        if (args == null) return false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (string.IsNullOrEmpty(a)) continue;
            for (int j = 0; j < StickerFlags.Length; j++)
            {
                if (string.Equals(a, StickerFlags[j], StringComparison.OrdinalIgnoreCase)) return true;
                if (a.StartsWith(StickerFlags[j] + "=", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    /// <summary>v14: 派发给贴纸模式前把 --sticker 自己摘掉（贴纸的参数解析不认识它）。</summary>
    internal static string[] StickerArgs(string[] args)
    {
        if (args == null) return new string[0];
        List<string> list = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--sticker", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(args[i]);
        }
        return list.ToArray();
    }

    static bool HasFlag(string[] args, string flag)
    {
        if (args == null) return false;
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Usage: 贴贴便签.exe [port] [--no-open] - the port may appear anywhere on the line.</summary>
    static int PortArg(string[] args, int fallback)
    {
        if (args != null)
        {
            for (int i = 0; i < args.Length; i++)
            {
                int p;
                if (int.TryParse(args[i], out p) && p > 0 && p < 65536) return p;
            }
        }
        return fallback;
    }

    static bool HasNoOpen(string[] args)
    {
        if (args == null) return false;
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--no-open", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    static int FindFreePort(int start)
    {
        for (int p = start; p < start + 100; p++)
        {
            try
            {
                TcpListener probe = new TcpListener(IPAddress.Loopback, p);
                probe.Start();
                probe.Stop();
                return p;
            }
            catch { }
        }
        return start;
    }

    static void StartServer()
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Thread t = new Thread(delegate()
        {
            while (true)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate(object state) { Handle((TcpClient)state); }, client);
                }
                catch { }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    static void Handle(TcpClient client)
    {
        try
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                // 只从原始网络流逐行/逐字节读取：
                // 切勿把 StreamReader 与原始流混用 —— 它会预读，导致请求体被吞、请求卡死（踩过的坑）。
                string requestLine = StreamReadLine(stream);
                if (string.IsNullOrEmpty(requestLine)) return;

                // 收集请求头（Content-Length / Transfer-Encoding 决定请求体读法）
                int contentLength = 0;
                bool chunked = false;
                while (true)
                {
                    string h = StreamReadLine(stream);
                    if (string.IsNullOrEmpty(h)) break;
                    int c = h.IndexOf(':');
                    if (c <= 0) continue;
                    string name = h.Substring(0, c).Trim();
                    string val = h.Substring(c + 1).Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        int len;
                        if (int.TryParse(val, out len) && len > 0) contentLength = len;
                    }
                    else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    {
                        // 浏览器/Node 的 fetch 对带 body 的请求可能用分块传输，必须支持
                        if (val.ToLowerInvariant().Contains("chunked")) chunked = true;
                    }
                }

                string[] parts = requestLine.Split(' ');
                if (parts.Length < 2) return;

                string method = parts[0].ToUpperInvariant();
                string urlPath = parts[1];
                int q = urlPath.IndexOf('?');
                if (q >= 0) urlPath = urlPath.Substring(0, q);
                urlPath = Uri.UnescapeDataString(urlPath);
                if (urlPath.Length == 0) urlPath = "/";

                // ---------- 数据接口 ----------
                if (urlPath.Equals("/api/notes", StringComparison.OrdinalIgnoreCase))
                {
                    if (method == "GET")
                    {
                        WriteResponse(stream, 200, "application/json; charset=utf-8", notes.ReadBytes());
                        return;
                    }
                    if (method == "PUT" || method == "PATCH" || method == "POST")
                    {
                        byte[] body = chunked ? ReadChunkedBody(stream) : ReadBody(stream, contentLength);
                        string err;
                        if (notes.Write(body, out err))
                            WriteResponse(stream, 200, "application/json; charset=utf-8",
                                Encoding.UTF8.GetBytes("{\"ok\":true,\"bytes\":" + (body == null ? 0 : body.Length) + "}"));
                        else
                            WriteResponse(stream, 400, "application/json; charset=utf-8",
                                Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"" + Escape(err) + "\"}"));
                        return;
                    }
                    // Unsupported method on the data endpoint: drain the announced body first (same
                    // reason as the /api/desktop 405 below) so the client can actually read the 405.
                    if (chunked) ReadChunkedBody(stream); else ReadBody(stream, contentLength);
                    WriteResponse(stream, 405, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("405 Method Not Allowed"));
                    return;
                }

                // ---------- v13 桌面桥接口 ----------
                // GET  = 只读返回「已贴出清单」（解析后的字段，绝不返回原始文件内容，绝不 500）
                // POST = 校验后向 request.json 追加一条放置/收起请求（唯一写者 = 本启动器）
                if (urlPath.Equals("/api/desktop", StringComparison.OrdinalIgnoreCase))
                {
                    if (method == "GET")
                    {
                        // v19 智能检测（用户要求）：网页轮询时若发现"有未被处理的贴出请求 + 贴纸半没在跑"，
                        // 就顺手把贴纸拉起来——用户不该被要求去双击某个文件（那条旧提示已删除）。
                        // 沿用既有的三道护栏：互斥体判"在没在跑"（不靠进程名猜）、8 秒节流（轮询 1.5s 也不会猛拉）、
                        // TIETIE_NO_AUTOSTART=1 一刀切（自动化验证与"我自己在管"的场景不受影响）。
                        int pendingPlaces;
                        int pendingAll = bridge.PendingRequestCount(out pendingPlaces);
                        if (pendingPlaces > 0 && !StickerStarter.IsRunning())
                        {
                            string healed = StickerStarter.Ensure(ExeDir);
                            if (healed == "started" || healed == "failed" || healed == "notfound")
                                Say("智能检测：有 " + pendingPlaces + " 条未处理的贴出请求且贴纸未运行 -> "
                                    + healed + "（" + RestoreText(healed) + "）");
                        }
                        WriteResponse(stream, 200, "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes(bridge.ReadViewJson()));
                        return;
                    }
                    if (method == "POST")
                    {
                        byte[] dbody = chunked ? ReadChunkedBody(stream) : ReadBody(stream, contentLength);
                        string derr;
                        string daction;
                        long seq;
                        if (bridge.AppendRequest(dbody, notes.ReadBytes(), out seq, out daction, out derr))
                        {
                            // v13.1 (用户要求): 点「贴到桌面」时，若贴纸程序没在运行就自动把它启动起来。
                            // 只有 place 需要启动它（对没在运行的贴纸发 remove 本来就没有意义）；
                            // 是否"在运行"以它自己的单实例互斥体为准，不靠进程名猜。
                            string sticker = (daction == "place") ? StickerStarter.Ensure(ExeDir) : "skipped";
                            WriteResponse(stream, 200, "application/json; charset=utf-8",
                                Encoding.UTF8.GetBytes("{\"ok\":true,\"seq\":" + seq + ",\"sticker\":\"" + Escape(sticker) + "\"}"));
                        }
                        else
                            WriteResponse(stream, 400, "application/json; charset=utf-8",
                                Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"" + Escape(derr) + "\"}"));
                        return;
                    }
                    // Same drain-before-405 rule: a PUT/PATCH with a body must not be answered by a
                    // socket close while the client is still writing.
                    if (chunked) ReadChunkedBody(stream); else ReadBody(stream, contentLength);
                    WriteResponse(stream, 405, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("405 Method Not Allowed"));
                    return;
                }

                // ---------- 静态文件 ----------
                if (method != "GET" && method != "HEAD")
                {
                    // Drain whatever body the client announced BEFORE answering: closing the socket while
                    // the client is still writing makes it report a transport error instead of reading 405.
                    if (chunked) ReadChunkedBody(stream); else ReadBody(stream, contentLength);
                    WriteResponse(stream, 405, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("405 Method Not Allowed"));
                    return;
                }

                if (urlPath == "/") urlPath = "/index.html";
                string rel = urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                string full = Path.GetFullPath(Path.Combine(baseDir, rel));

                // 防目录穿越
                string rootFull = Path.GetFullPath(baseDir);
                if (!rootFull.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    rootFull += Path.DirectorySeparatorChar;
                if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    WriteResponse(stream, 403, "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("403 Forbidden"));
                    return;
                }

                if (!File.Exists(full))
                {
                    WriteResponse(stream, 404, "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("404 Not Found"));
                    return;
                }

                byte[] data = File.ReadAllBytes(full);
                string ext = Path.GetExtension(full);
                string ct = Mime.ContainsKey(ext) ? Mime[ext] : "application/octet-stream";
                WriteResponse(stream, 200, ct, data);
            }
        }
        catch { }
    }

    // 从原始网络流读一行（以 \n 结束，去掉尾部 \r / \n）。
    // 不用 StreamReader：它对 socket 流会预读，把后续请求体吞掉，导致请求卡死。
    static string StreamReadLine(NetworkStream stream)
    {
        MemoryStream ms = new MemoryStream();
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0) break;
            if (b == (int)'\n') break;
            if (b == (int)'\r') continue;
            ms.WriteByte((byte)b);
            if (ms.Length > 65536) break;   // 防御：超长行直接截断
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // 分块传输（Transfer-Encoding: chunked）请求体读取：
    // 浏览器 / Node fetch 对带 body 的请求常用它，服务端必须支持，否则会互等超时。
    static byte[] ReadChunkedBody(NetworkStream stream)
    {
        MemoryStream ms = new MemoryStream();
        while (true)
        {
            string line = StreamReadLine(stream);
            if (string.IsNullOrEmpty(line)) break;
            int semi = line.IndexOf(';');
            if (semi >= 0) line = line.Substring(0, semi);
            int size;
            try { size = Convert.ToInt32(line.Trim(), 16); }
            catch { break; }
            if (size <= 0) break;                       // 0 块 = 结束
            byte[] buf = new byte[size];
            int read = 0;
            while (read < size)
            {
                int n = stream.Read(buf, read, size - read);
                if (n <= 0) break;
                read += n;
            }
            ms.Write(buf, 0, read);
            StreamReadLine(stream);                     // 块尾 CRLF
        }
        return ms.ToArray();
    }

    static byte[] ReadBody(NetworkStream stream, int contentLength)
    {
        if (contentLength <= 0) return new byte[0];
        if (contentLength > 64 * 1024 * 1024) return new byte[0];
        byte[] buf = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n = stream.Read(buf, read, contentLength - read);
            if (n <= 0) break;
            read += n;
        }
        if (read == contentLength) return buf;
        byte[] trimmed = new byte[read];
        Array.Copy(buf, trimmed, read);
        return trimmed;
    }

    // 供 DesktopBridge 复用（同一程序集内可见）
    internal static string Escape(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
    }

    static void WriteResponse(NetworkStream stream, int status, string contentType, byte[] body)
    {
        string statusText = status == 200 ? "OK"
            : (status == 404 ? "Not Found"
            : (status == 403 ? "Forbidden"
            : (status == 400 ? "Bad Request"
            : (status == 405 ? "Method Not Allowed"
            : (status == 500 ? "Internal Server Error" : "Error")))));
        StringBuilder header = new StringBuilder();
        header.Append("HTTP/1.1 ").Append(status).Append(' ').Append(statusText).Append("\r\n");
        header.Append("Content-Type: ").Append(contentType).Append("\r\n");
        header.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        header.Append("Cache-Control: no-cache\r\n");
        header.Append("Connection: close\r\n\r\n");
        byte[] hb = Encoding.ASCII.GetBytes(header.ToString());
        stream.Write(hb, 0, hb.Length);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }
}

// ---------------------------------------------------------------------------
//  v26 · 「打开便签界面」到底开什么：优先「已安装的应用」，其次「网址」
// ---------------------------------------------------------------------------
// 用户 2026-09-29 报的落差：「打开便签页面打开的还是网址,而不是下载的应用(电脑端)」。
// 根因：这里原本就是 Process.Start(url, UseShellExecute) —— 交给**系统默认浏览器**开一个标签页；
// 而他已经把贴贴便签用 Chrome 装成了 PWA（桌面上多出一个"贴贴便签 (1)"），那是**另一条入口**。
// 于是"应用确实装好了"与"点按钮出来的是浏览器网址"同时为真，用户有充分理由认为坏了。
//
// 现在的规则（顺序固定）：
//   ①在搜索根里找一个**已安装应用**的快捷方式，必须同时满足两条：
//        a) 名字以"贴贴便签"开头（系统遇到重名会变成"贴贴便签 (1)"，所以用前缀匹配）；
//        b) 它的命令行里带 --app-id=（Chrome/Edge 装出来的 PWA 快捷方式都长这样）。
//      第 b 条是**必须的**：搜索根里另有一个同名快捷方式指向本 exe（启动器自己），
//      只看名字会把它选中 —— 结果变成"点一下又开一个启动器"，比原来的毛病更糟。
//   ②找不到才退回原来的行为：让系统默认浏览器打开网址。
// 读 .lnk 走 WScript.Shell（COM 晚绑定，不需要额外的程序集引用）。
static class AppLaunch
{
    public const string AppName = "贴贴便签";

    /// <summary>搜索根：开始菜单（当前用户 + 全体）与桌面。TIETIE_APP_SEARCH_ROOTS 可覆盖（分号分隔），
    /// 供自动化把搜索指向沙盒 —— 真实快捷方式因此全程只读。</summary>
    static string[] SearchRoots()
    {
        string env = Environment.GetEnvironmentVariable("TIETIE_APP_SEARCH_ROOTS");
        if (env != null && env.Length > 0)
        {
            string[] parts = env.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) return parts;
        }
        List<string> roots = new List<string>();
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                               @"Microsoft\Windows\Start Menu\Programs"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                               @"Microsoft\Windows\Start Menu\Programs"));
        roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        return roots.ToArray();
    }

    /// <summary>已安装应用的快捷方式路径；没有就返回 null。</summary>
    public static string FindInstalledApp()
    {
        try
        {
            string[] roots = SearchRoots();
            for (int r = 0; r < roots.Length; r++)
            {
                string root = roots[r];
                if (root == null || root.Length == 0) continue;
                if (!Directory.Exists(root)) continue;
                string[] lnks;
                try { lnks = Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories); }
                catch { continue; }
                Array.Sort(lnks, StringComparer.OrdinalIgnoreCase);   // 结果稳定，判据才好写
                for (int i = 0; i < lnks.Length; i++)
                {
                    string stem = Path.GetFileNameWithoutExtension(lnks[i]);
                    if (stem == null) continue;
                    if (stem.IndexOf(AppName, StringComparison.OrdinalIgnoreCase) != 0) continue;
                    string a = ShortcutArguments(lnks[i]);
                    if (a == null) continue;
                    if (a.IndexOf("--app-id=", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    return lnks[i];
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>读 .lnk 的命令行参数；读不到返回 null（不抛）。</summary>
    static string ShortcutArguments(string lnkPath)
    {
        object shell = null;
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return null;
            shell = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (sc == null) return null;
            object a = sc.GetType().InvokeMember("Arguments",
                System.Reflection.BindingFlags.GetProperty, null, sc, null);
            return a as string;
        }
        catch { return null; }
        finally
        {
            try { if (shell != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
            catch { }
        }
    }

    /// <summary>这次会开什么：返回 "app|&lt;快捷方式路径&gt;" 或 "url|&lt;网址&gt;"。--print-open-target 用它（只读）。</summary>
    public static string Resolve(string url)
    {
        string app = FindInstalledApp();
        if (app != null) return "app|" + app;
        return "url|" + url;
    }

    /// <summary>真正打开，返回一句写给日志区看的话。**不抛异常**：应用启动失败也要退回开网址 ——
    /// 不能因为"装了应用"反而让用户点不动。</summary>
    public static string Open(string url)
    {
        string app = FindInstalledApp();
        if (app != null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(app) { UseShellExecute = true });
                return "已启动已安装的应用：" + Path.GetFileNameWithoutExtension(app);
            }
            catch (Exception ex)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    return "应用未能启动（" + ex.Message + "），已改为在浏览器中打开：" + url;
                }
                catch (Exception ex2)
                {
                    return "未能打开界面：" + ex2.Message + "（请手动访问 " + url + "）";
                }
            }
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return "已在浏览器中打开：" + url;
        }
        catch (Exception ex)
        {
            return "未能打开浏览器：" + ex.Message + "（请手动访问 " + url + "）";
        }
    }
}

// 数据文件读写：data\notes.json（整库 JSON 数组）
// 写入用「临时文件 + 原子替换」，避免写一半崩溃导致数据文件损坏；
// 单把锁串行化所有访问，避免并发请求互相覆盖。
class NoteStore
{
    readonly object gate = new object();
    readonly string path;
    readonly string tmp;
    readonly string bak;

    public NoteStore(string filePath)
    {
        path = filePath;
        tmp = filePath + ".tmp";
        bak = filePath + ".bak";
        try
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            lock (gate)
            {
                if (!File.Exists(path)) File.WriteAllText(path, "[]", new UTF8Encoding(false));
            }
        }
        catch { }
    }

    public string FilePath { get { return path; } }

    public byte[] ReadBytes()
    {
        lock (gate)
        {
            try
            {
                if (!File.Exists(path)) return Encoding.UTF8.GetBytes("[]");
                byte[] raw = File.ReadAllBytes(path);
                foreach (byte b in raw)
                {
                    if (b == 0x20 || b == 0x09 || b == 0x0D || b == 0x0A) continue;  // 跳过前导空白
                    if (b != (byte)'[') return Fallback();                            // 首个有效字符必须是 '['
                    return raw;                                                       // 合法 JSON 数组
                }
                return Encoding.UTF8.GetBytes("[]");                                  // 全是空白 → 空库
            }
            catch
            {
                return Fallback();
            }
        }
    }

    byte[] Fallback()
    {
        try
        {
            if (File.Exists(bak))
            {
                byte[] b = File.ReadAllBytes(bak);
                foreach (byte x in b) if (x == (byte)'[') return b;
            }
        }
        catch { }
        return Encoding.UTF8.GetBytes("[]");
    }

    // 校验 + 原子落盘
    public bool Write(byte[] body, out string err)
    {
        err = null;
        string text = Encoding.UTF8.GetString(body == null ? new byte[0] : body).Trim();
        if (text.Length == 0) text = "[]";
        if (text[0] != '[')
        {
            err = "请求体必须是 JSON 数组";
            return false;
        }
        lock (gate)
        {
            try
            {
                if (File.Exists(path)) File.Copy(path, bak, true);
                File.WriteAllText(tmp, text, new UTF8Encoding(false));   // 先写临时文件
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);                                     // 再原子替换
                return true;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
        }
    }

    // 粗略统计条数（仅供接口回执展示）：只数数组元素层的对象，跳过字符串内容
    public int Count(byte[] body)
    {
        string text = Encoding.UTF8.GetString(body == null ? new byte[0] : body);
        int depth = 0, objs = 0;
        bool inStr = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inStr)
            {
                if (c == '\\') { i++; continue; }   // 转义：连同下一个字符一起跳过
                if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; continue; }
            if (c == '[') { depth++; continue; }
            if (c == ']') { depth--; continue; }
            if (c == '{') { if (depth == 1) objs++; continue; }   // 仅数组元素层的对象计为一条便签
        }
        return objs;
    }
}

// =====================================================================
// v13 桌面桥（DesktopBridge）—— 「网页按钮 → 桌面贴纸」的文件中转站
//
// 为什么必须绕这一圈（不是实现偷懒，是链路决定的）：
//   ① 浏览器没有权限创建原生桌面窗口 ⇒ 网页只能"请求外面的程序"去开；
//   ② 贴纸程序有一条自 S1 起的硬约束「应用自身不监听端口」，且自测断言
//      no-listener-symbol-in-assembly 禁用成员名含 Socket / TcpListener / HttpListener /
//      NamedPipe 的符号 ⇒ 端口与管道都不可用；
//   ③ 唯一同时"知道数据目录"又"已经在监听端口"的现成组件，就是本启动器。
//
// 单写者矩阵（与数据层同一条纪律 ⇒ 不需要文件锁，也不会互相覆盖）：
//   data\bridge\request.json —— 唯一写者 = 本启动器（只增不删，带单调 seq）
//   data\bridge\placed.json  —— 唯一写者 = 贴纸程序（本启动器只读）
// =====================================================================
class DesktopBridge
{
    readonly object gate = new object();
    readonly string dir;
    readonly string requestPath;
    readonly string placedPath;

    public DesktopBridge(string bridgeDir)
    {
        dir = bridgeDir;
        requestPath = Path.Combine(bridgeDir, "request.json");
        placedPath = Path.Combine(bridgeDir, "placed.json");
    }

    public string RequestPath { get { return requestPath; } }
    public string PlacedPath { get { return placedPath; } }

    // ---------------- POST：追加一条请求（唯一写者 = 本启动器） ----------------

    /// <summary>
    /// 校验（action 合法 + noteId 非空且存在于 notes.json）后追加一条请求并原子落盘。
    /// 校验失败一律返回 false + err（上层回 400），且**不写任何文件**。
    /// </summary>
    public bool AppendRequest(byte[] body, byte[] notesBytes, out long seq, out string usedAction, out string err)
    {
        seq = 0;
        usedAction = null;
        err = null;
        string text = Encoding.UTF8.GetString(body == null ? new byte[0] : body).Trim();
        if (text.Length == 0) { err = "请求体必须是 JSON 对象"; return false; }

        BNode req;
        try { req = BNode.Parse(text); }
        catch (Exception ex) { err = "请求体不是合法 JSON：" + ex.Message; return false; }
        if (req == null || !req.IsObject) { err = "请求体必须是 JSON 对象"; return false; }

        string action = req.StrOf("action");
        string noteId = req.StrOf("noteId");
        if (action != "place" && action != "remove" && action != "exit")
        {
            err = "action 必须是 place、remove 或 exit";
            return false;
        }
        if (action == "exit")
        {
            // v14: exit 是控制请求，不针对某条便签 ⇒ 不需要 noteId（它由本启动器的退出路径发出；
            // 网页从来没有这个按钮，保留在接口上是给自动化验证与排障用的对称入口）。
            return AppendEntry(action, "", out seq, out err);
        }
        if (string.IsNullOrEmpty(noteId)) { err = "noteId 不能为空"; return false; }
        if (!NoteExists(notesBytes, noteId)) { err = "noteId 在 notes.json 里不存在"; return false; }
        if (!AppendEntry(action, noteId, out seq, out err)) return false;
        usedAction = action;
        return true;
    }

    /// <summary>v14: 请贴纸模式干净退出（唯一写者 = 本启动器）。只在它真的在运行时调用；
    /// 贴纸侧另有"请求太旧就不认"的时限判据，防止这条请求误杀下一次启动的贴纸。</summary>
    public bool AppendExit(out long seq, out string err)
    {
        return AppendEntry("exit", "", out seq, out err);
    }

    /// <summary>v14: 已贴出的便签张数（给主窗口的状态行用；读不到返回 -1，绝不抛）。</summary>
    public int PlacedCount()
    {
        try
        {
            if (!File.Exists(placedPath)) return 0;
            BNode root = BNode.Parse(File.ReadAllText(placedPath, Encoding.UTF8));
            if (root == null || !root.IsObject) return -1;
            BNode arr = root.Get("placed");
            if (arr == null || !arr.IsArray) return -1;
            return arr.Items.Count;
        }
        catch { return -1; }
    }

    /// <summary>追加一条请求并原子落盘（seq 单调递增，请求只增不删）。</summary>
    bool AppendEntry(string action, string noteId, out long seq, out string err)
    {
        seq = 0;
        err = null;
        lock (gate)
        {
            try
            {
                List<BridgeRequestEntry> list = LoadRequests();
                long max = 0;
                for (int i = 0; i < list.Count; i++) if (list[i].Seq > max) max = list[i].Seq;

                seq = max + 1;
                BridgeRequestEntry ne = new BridgeRequestEntry();
                ne.Seq = seq;
                ne.Action = action;
                ne.NoteId = noteId;
                ne.TsMs = NowMs();
                list.Add(ne);

                StringBuilder sb = new StringBuilder();
                sb.Append("{\"seq\":").Append(seq).Append(",\"requests\":[");
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"seq\":").Append(list[i].Seq)
                      .Append(",\"action\":\"").Append(list[i].Action)
                      .Append("\",\"noteId\":\"").Append(TietieLauncher.Escape(list[i].NoteId))
                      .Append("\",\"at\":null,\"tsMs\":").Append(list[i].TsMs).Append('}');
                }
                sb.Append("]}");

                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                WriteAtomic(requestPath, sb.ToString());
                return true;
            }
            catch (Exception ex)
            {
                err = "写入请求失败：" + ex.Message;
                return false;
            }
        }
    }

    /// <summary>读现有请求（容错：文件不存在 / 坏 JSON / 单条坏记录 ⇒ 跳过，绝不抛）。</summary>
    List<BridgeRequestEntry> LoadRequests()
    {
        List<BridgeRequestEntry> list = new List<BridgeRequestEntry>();
        try
        {
            if (!File.Exists(requestPath)) return list;
            BNode root = BNode.Parse(File.ReadAllText(requestPath, Encoding.UTF8));
            if (root == null || !root.IsObject) return list;
            BNode arr = root.Get("requests");
            if (arr == null || !arr.IsArray) return list;
            for (int i = 0; i < arr.Items.Count; i++)
            {
                BNode it = arr.Items[i];
                if (it == null || !it.IsObject) continue;
                double sq = it.NumOf("seq", -1);
                string a = it.StrOf("action");
                string nid = it.StrOf("noteId");
                if (sq < 0) continue;
                if (a != "place" && a != "remove" && a != "exit") continue;
                // exit 没有 noteId（见 AppendExit）；place/remove 必须有，否则这条记录无意义。
                if (a != "exit" && string.IsNullOrEmpty(nid)) continue;
                BridgeRequestEntry e = new BridgeRequestEntry();
                e.Seq = (long)sq;
                e.Action = a;
                e.NoteId = nid;
                e.TsMs = (long)it.NumOf("tsMs", 0);
                list.Add(e);
            }
        }
        catch { }
        return list;
    }

    /// <summary>request.json 里已知的最大 seq（0 = 没有请求）。供 GET 回报"最后一次请求序号"。</summary>
    long LastRequestSeq()
    {
        long max = 0;
        List<BridgeRequestEntry> list = LoadRequests();
        for (int i = 0; i < list.Count; i++) if (list[i].Seq > max) max = list[i].Seq;
        return max;
    }

    // ---------------- GET：只读视图（**绝不 500**，只返回解析后的字段） ----------------

    /// <summary>v19 智能检测：request.json 里还有多少条**未被贴纸端确认**的请求（seq > placed.json 的 ackSeq），
    /// 以及其中有多少条是 place。网页用它显示"还有 N 条待处理"；启动器用它决定要不要自动把贴纸拉起来
    /// （用户不该被要求去双击某个文件——那条提示已随本批删除）。任何异常都退化为 0，绝不抛给调用方。</summary>
    public int PendingRequestCount(out int pendingPlaces)
    {
        pendingPlaces = 0;
        try
        {
            long ack = 0;
            if (File.Exists(placedPath))
            {
                BNode root = BNode.Parse(File.ReadAllText(placedPath, Encoding.UTF8));
                if (root != null && root.IsObject) ack = (long)root.NumOf("ackSeq", 0);
            }
            List<BridgeRequestEntry> list = LoadRequests();
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Seq <= ack) continue;
                n++;
                if (string.Equals(list[i].Action, "place", StringComparison.Ordinal)) pendingPlaces++;
            }
            return n;
        }
        catch (Exception ex)
        {
            TietieLauncher.Say("桥：统计待处理请求失败（按 0 处理）：" + ex.GetType().Name + ": " + ex.Message);
            pendingPlaces = 0;
            return 0;
        }
    }

    public string ReadViewJson()
    {
        List<BridgeViewItem> placed = new List<BridgeViewItem>();
        long savedAt = 0, ack = 0, lastReq = 0;
        bool ok = false;
        string reason = "";
        int pendingPlacesIgnored;
        int pendingCount = PendingRequestCount(out pendingPlacesIgnored);   // v19

        try { lastReq = LastRequestSeq(); }
        catch { }

        try
        {
            if (!File.Exists(placedPath))
            {
                reason = "placed.json 不存在（贴纸程序还没运行过）";
            }
            else
            {
                BNode root = BNode.Parse(File.ReadAllText(placedPath, Encoding.UTF8));
                if (root == null || !root.IsObject) { reason = "placed.json 不是 JSON 对象"; }
                else
                {
                    BNode arr = root.Get("placed");
                    if (arr == null || !arr.IsArray) { reason = "placed.json 缺少 placed 数组"; }
                    else
                    {
                        savedAt = (long)root.NumOf("savedAtMs", 0);
                        ack = (long)root.NumOf("ackSeq", 0);
                        for (int i = 0; i < arr.Items.Count; i++)
                        {
                            BNode it = arr.Items[i];
                            if (it == null || !it.IsObject) continue;
                            string nid = it.StrOf("noteId");
                            if (string.IsNullOrEmpty(nid)) continue;
                            BridgeViewItem v = new BridgeViewItem();
                            v.NoteId = nid;
                            v.Title = it.StrOf("title");
                            if (v.Title == null) v.Title = "";
                            v.X = (int)it.NumOf("x", 0);
                            v.Y = (int)it.NumOf("y", 0);
                            v.W = (int)it.NumOf("w", 0);
                            v.H = (int)it.NumOf("h", 0);
                            v.TopMost = it.BoolOf("topMost", false);
                            placed.Add(v);
                        }
                        ok = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ok = false;
            placed.Clear();
            reason = "placed.json 解析失败：" + ex.Message;
        }

        StringBuilder sb = new StringBuilder();
        sb.Append("{\"ok\":").Append(ok ? "true" : "false")
          .Append(",\"reason\":\"").Append(TietieLauncher.Escape(reason))
          .Append("\",\"placedSavedAtMs\":").Append(savedAt)
          .Append(",\"lastRequestSeq\":").Append(lastReq)
          .Append(",\"ackSeq\":").Append(ack)
          // v19：把"贴纸到底在不在跑"和"还有几条请求没被处理"直接告诉网页，让它能说人话
          // （旧版让网页自己算 seq 差、并在提示里叫用户去双击一个已经不存在的手册文件）。
          .Append(",\"stickerRunning\":").Append(StickerStarter.IsRunning() ? "true" : "false")
          .Append(",\"pendingRequests\":").Append(pendingCount)
          .Append(",\"placed\":[");
        for (int i = 0; i < placed.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"noteId\":\"").Append(TietieLauncher.Escape(placed[i].NoteId))
              .Append("\",\"title\":\"").Append(TietieLauncher.Escape(placed[i].Title))
              .Append("\",\"x\":").Append(placed[i].X)
              .Append(",\"y\":").Append(placed[i].Y)
              .Append(",\"w\":").Append(placed[i].W)
              .Append(",\"h\":").Append(placed[i].H)
              .Append(",\"topMost\":").Append(placed[i].TopMost ? "true" : "false")
              .Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    // ---------------- 内部工具 ----------------

    /// <summary>noteId 是否真在 notes.json 里（按顶层数组元素的 "id" 精确比对，不做子串匹配）。</summary>
    static bool NoteExists(byte[] notesBytes, string noteId)
    {
        if (notesBytes == null || notesBytes.Length == 0) return false;
        try
        {
            BNode root = BNode.Parse(Encoding.UTF8.GetString(notesBytes));
            if (root == null || !root.IsArray) return false;
            for (int i = 0; i < root.Items.Count; i++)
            {
                BNode it = root.Items[i];
                if (it == null || !it.IsObject) continue;
                string id = it.StrOf("id");
                if (id != null && string.Equals(id, noteId, StringComparison.Ordinal)) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 原子替换：temp + File.Replace。**绝不 Delete + Move**（那样并发读者会看到"文件不存在"）。
    /// File.Replace 不可用时退化为"就地覆盖"，同样不删原文件。
    /// </summary>
    static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        if (File.Exists(path))
        {
            try { File.Replace(tmp, path, null); return; }
            catch { }
            File.Copy(tmp, path, true);
            try { File.Delete(tmp); } catch { }
            return;
        }
        File.Move(tmp, path);
    }

    static long NowMs()
    {
        return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
    }

    internal sealed class BridgeRequestEntry
    {
        public long Seq;
        public string Action = "";
        public string NoteId = "";
        public long TsMs;
    }

    internal sealed class BridgeViewItem
    {
        public string NoteId = "";
        public string Title = "";
        public int X, Y, W, H;
        public bool TopMost;
    }
}

// =====================================================================
// 贴纸模式按需启动（v13.1 用户要求：「点『贴到桌面』自动运行 Sticker.exe（如果未运行）」）
//
// 为什么由启动器做：浏览器同样不能启动本机程序，"点一下就把贴纸跑起来"只能由已经在跑的那个
// 本机进程代劳 —— 也就是本启动器。
// v14（用户要求「把 Sticker.exe 整合进 贴贴便签.exe」）：贴纸模式就在本 exe 里，所以这里
// **启动的是自己**（<本 exe> --sticker），不再是去磁盘上找 Sticker.exe。
// 「是否已经在运行」怎么判：贴纸模式自己持有单实例互斥体 Local\TieTieSticker.S1（规格 v11 §8.6），
// 所以 Mutex.OpenExisting 就是权威判据，**不靠进程名去猜**（进程名判断会误杀/误判）。
// 关掉自动启动：置环境变量 TIETIE_NO_AUTOSTART=1（自动化验证用：桥端到端要测"请求在贴纸关闭时
// 仍然持久化、下次启动补做"，自动启动会把那个场景消灭掉）。
// =====================================================================
class StickerStarter
{
    public const string MutexName = "Local\\TieTieSticker.S1";
    const string OptOutEnv = "TIETIE_NO_AUTOSTART";
    /// <summary>v15: 只关"启动就恢复桌面贴纸"，不影响"网页点『贴到桌面』按需启动"。自动化验证要
    /// 分别测这两条路径：v15-autostart-check 测启动路径（用 TIETIE_NO_AUTOSTART 一刀切），
    /// v14-merge-check 的 M5-M8 测请求路径（只关启动路径，请求路径照旧自动启动）。</summary>
    const string OptOutStartupEnv = "TIETIE_NO_STARTUP_STICKER";

    static readonly object gate = new object();
    static DateTime lastLaunchAttemptUtc = DateTime.MinValue;

    /// <summary>返回 "running"（已在跑）/ "started" / "starting"（刚启动过，节流中）/
    /// "notfound" / "failed" / "disabled"（被 TIETIE_NO_AUTOSTART 关掉）。</summary>
    public static string Ensure(string exeDir)
    {
        lock (gate)
        {
            try
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OptOutEnv))) return "disabled";
                if (IsRunning()) return "running";
                // 节流：连点两下不能让第二次去和第一次启动中的实例抢互斥体（那会让贴纸程序
                // 弹一个"已在运行"的提示框，用户会以为点坏了）。
                if ((DateTime.UtcNow - lastLaunchAttemptUtc).TotalSeconds < 8) return "starting";

                // v14：贴纸模式就在本 exe 里 ⇒ 启动自己（--sticker）。这个路径永远存在，
                // v13.1 那个「找不到 Sticker.exe」的 notfound 分支在合并版里已不可能出现；
                // 只剩"取不到自身路径"这一种防御性失败。
                string path = SelfPath();
                if (path == null)
                {
                    TietieLauncher.Say("取不到本程序自身路径，无法自动启动贴纸模式。");
                    return "notfound";
                }
                lastLaunchAttemptUtc = DateTime.UtcNow;
                Process.Start(new ProcessStartInfo(path, "--sticker")
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(path)
                });
                TietieLauncher.Say("已自动启动贴纸模式：" + path + " --sticker");
                return "started";
            }
            catch (Exception ex)
            {
                TietieLauncher.Say("自动启动贴纸模式失败：" + ex.GetType().Name + ": " + ex.Message);
                return "failed";
            }
        }
    }

    /// <summary>v15: 启动时恢复桌面贴纸 —— 只比 Ensure 多一条"启动路径专用"的自动化开关。
    /// TIETIE_NO_AUTOSTART=1 仍然一刀切（两条路径都不启动）。</summary>
    public static string EnsureStartup(string exeDir)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OptOutStartupEnv))) return "disabled";
        return Ensure(exeDir);
    }

    /// <summary>本 exe 的完整路径 —— 贴纸模式与启动器是同一个文件。</summary>
    internal static string SelfPath()
    {
        try
        {
            string p = Application.ExecutablePath;
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
        }
        catch { }
        try
        {
            Process me = Process.GetCurrentProcess();
            if (me.MainModule != null && !string.IsNullOrEmpty(me.MainModule.FileName)) return me.MainModule.FileName;
        }
        catch { }
        return null;
    }

    /// <summary>贴纸模式是否在跑：以它自己的单实例互斥体为权威判据，**不靠进程名去猜**
    /// （进程名判断会误杀/误判）。</summary>
    internal static bool IsRunning()
    {
        try
        {
            using (Mutex m = Mutex.OpenExisting(MutexName)) { return true; }
        }
        catch { return false; }
    }
}

// =====================================================================
// v20（用户要求「添加开机自启动功能(可在贴贴便签.exe窗口设置)」）：开机自启动登记。
//
// 机制选择 —— HKCU 下 CurrentVersion 子键里的 Run 键，放一条字符串值：
//   * 不用「启动」文件夹里的快捷方式：那要造 .lnk，得走 COM(WScript.Shell) 或 IShellLink
//     P/Invoke；本工程不用任何第三方/额外依赖，注册表这一条是最小的可读写形态。
//   * 不用任务计划程序：更重、部分选项要管理员权限；用户要的只是"开机后它自己起来"。
//   * HKCU 不需要管理员权限（与安装包、程序本身的既有约束一致），也不影响别的用户。
// 值的数据 = "<本 exe 全路径>" --no-open
//   * 带引号：路径将来可能有空格；
//   * --no-open：登录时自动启动不该每次都弹一个浏览器标签（窗口里有「打开便签界面」按钮）；
//   * **绝不放 --headless**：启动器在 --headless 下会在「启动恢复桌面贴纸」那一行之前就 return
//     （见 Main 里的顺序）⇒ 那样开机后桌面上根本不会贴出便签，这个功能等于白做。
// 一句话语义：勾上 = 以后登录 Windows 时，等价于用户亲手双击了一次 贴贴便签.exe --no-open。
//
// 自动化验证的重定向（**验证脚本绝不能碰用户真实的启动项**）：
//   置环境变量 TIETIE_AUTOSTART_ROOT=<HKCU 下的子键路径> ⇒ 只读写那个子键。
//   脚本只在自己进程里设置它（子进程继承），跑完删掉测试子键；真实 Run 键全程保持原样
//   （这一点由 .tools\v20-autostart-check.ps1 的 E0/E1 明确断言）。
// =====================================================================
class AutoStart
{
    /// <summary>真实的启动项位置（HKCU 下，无需管理员）。</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>启动项的名字 = 任务管理器「启动」页里显示的名字，所以用产品名（与其他用户可见处一致）。</summary>
    public const string ValueName = "贴贴便签";

    /// <summary>自动化验证的重定向开关（见上面注释）。</summary>
    const string OverrideEnv = "TIETIE_AUTOSTART_ROOT";

    public const string SwitchOn = "--autostart-on";
    public const string SwitchOff = "--autostart-off";
    public const string SwitchStatus = "--autostart-status";

    /// <summary>开机启动时附加在 exe 路径后面的参数（Single writer: here）。</summary>
    public const string BootFlags = " --no-open";

    /// <summary>本次实际读写的 HKCU 子键：正常 = Run；自动化验证 = TIETIE_AUTOSTART_ROOT 指定的测试子键。</summary>
    public static string KeyPath()
    {
        try
        {
            string o = Environment.GetEnvironmentVariable(OverrideEnv);
            if (!string.IsNullOrEmpty(o)) return o;
        }
        catch { }
        return RunKeyPath;
    }

    public static bool Redirected()
    {
        return !string.Equals(KeyPath(), RunKeyPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>本程序期望写进启动项的那条完整命令行；取不到自身路径时返回 null。</summary>
    public static string CommandLine()
    {
        string path = StickerStarter.SelfPath();
        if (string.IsNullOrEmpty(path)) return null;
        return "\"" + path + "\"" + BootFlags;
    }

    /// <summary>读当前设置（顺带把"注册表里那条到底是什么"经 current 带出来）：
    ///   "on"    = 启动项正是本程序的当前设置；
    ///   "off"   = 没有这一条；
    ///   "stale" = 有一条，但不是本程序的当前设置（旧的安装路径，或参数不同）；
    ///   "error" = 读不到（取不到自身路径 / 注册表异常）。</summary>
    public static string State(out string current)
    {
        current = null;
        string want = CommandLine();
        if (want == null) return "error";
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath(), false))
            {
                if (k == null) return "off";
                object v = k.GetValue(ValueName);
                if (v == null) return "off";
                current = Convert.ToString(v);
                if (string.Equals(current.Trim(), want, StringComparison.OrdinalIgnoreCase)) return "on";
                return "stale";
            }
        }
        catch { return "error"; }
    }

    public static string State() { string ignored; return State(out ignored); }

    /// <summary>开启：把启动项写成 CommandLine()。失败时 err 是给用户看的一句话。</summary>
    public static bool Enable(out string err)
    {
        err = null;
        string want = CommandLine();
        if (want == null) { err = "取不到本程序自身路径"; return false; }
        // 不许把 .tools 下的临时/实验副本登记进**真实的**开机启动项：那里全是变异体与一次性沙盒，
        // 一旦进了真实的 Run 键，以后每次登录都会去拉一个早就删掉的 exe。
        // 只在写真实键时拦（Redirected() = 自动化验证写的是测试子键，那里的路径指向哪里都无所谓）。
        if (!Redirected() && want.IndexOf(@"\.tools\", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            err = "拒绝把临时目录里的程序登记为开机启动（" + want + "）";
            return false;
        }
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath()))
            {
                if (k == null) { err = "打不开注册表子键 HKCU\\" + KeyPath(); return false; }
                k.SetValue(ValueName, want, RegistryValueKind.String);
            }
            return true;
        }
        catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; return false; }
    }

    /// <summary>关闭：删掉这条启动项（不管它当前指向哪里——这条值的名字就属于本程序）。
    /// 子键本来就不存在 / 本来就没有这条值 = 成功（幂等）。</summary>
    public static bool Disable(out string err)
    {
        err = null;
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath(), true))
            {
                if (k == null) return true;
                if (k.GetValue(ValueName) == null) return true;
                k.DeleteValue(ValueName, false);
            }
            return true;
        }
        catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; return false; }
    }

    /// <summary>窗口日志区/状态里的那句话（用户只看勾选框看不出"为什么没勾上"）。</summary>
    public static string Describe()
    {
        string cur;
        string st = State(out cur);
        if (st == "on") return "已开启（登录 Windows 后自动启动，不自动弹浏览器）";
        if (st == "off") return "未开启";
        if (st == "stale") return "未开启（注册表里另有一条："
            + cur + "；勾选一次即更新为本程序的设置，取消勾选即删除）";
        return "暂时读不到启动项设置";
    }

    // ---------- 命令行入口（自动化验证用；与窗口复选框共用上面这套实现） ----------

    static bool HasFlag(string[] args, string flag)
    {
        if (args == null) return false;
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool IsCli(string[] args)
    {
        return HasFlag(args, SwitchOn) || HasFlag(args, SwitchOff) || HasFlag(args, SwitchStatus);
    }

    /// <summary>输出是给脚本读的稳定 ASCII 令牌（winexe 没有控制台，但 stdout 被重定向时照旧可写）：
    ///   AUTOSTART on|off|stale|error [当前那条命令行]
    /// 退出码：0 = 成功（status 只要读得到就是 0），1 = 失败。</summary>
    public static int Cli(string[] args)
    {
        bool on = HasFlag(args, SwitchOn);
        bool off = HasFlag(args, SwitchOff);
        try
        {
            string err;
            if (on && !Enable(out err))
            {
                Console.WriteLine("AUTOSTART error " + err);
                return 1;
            }
            if (off && !Disable(out err))
            {
                Console.WriteLine("AUTOSTART error " + err);
                return 1;
            }
            string cur;
            string st = State(out cur);
            Console.WriteLine("AUTOSTART " + st + (string.IsNullOrEmpty(cur) ? "" : " " + cur));
            if (st == "error") return 1;
            if (on && st != "on") return 1;
            if (off && st != "off") return 1;
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("AUTOSTART error " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }
}

// =====================================================================
// 极简 JSON 解析器（只读树）。启动器原本只靠"首个非空白字符是不是 '['"校验数据文件，
// 但 v13 需要按字段校验 action/noteId、并只把 placed.json 的**解析结果**回给网页
// （不把原始文件字节吐出去）⇒ 需要一个真正的解析器。无第三方依赖、无网络。
// =====================================================================
class BNode
{
    public const int KNull = 0;
    public const int KBool = 1;
    public const int KNum = 2;
    public const int KStr = 3;
    public const int KArr = 4;
    public const int KObj = 5;

    public int Kind = KNull;
    public bool Bool;
    public double Num;
    public string Str;
    public List<BNode> Items;
    public List<KeyValuePair<string, BNode>> Members;

    public bool IsObject { get { return Kind == KObj; } }
    public bool IsArray { get { return Kind == KArr; } }

    public BNode Get(string key)
    {
        if (Kind != KObj || Members == null) return null;
        for (int i = 0; i < Members.Count; i++)
            if (string.Equals(Members[i].Key, key, StringComparison.Ordinal)) return Members[i].Value;
        return null;
    }

    /// <summary>字符串成员；不是字符串（缺失/类型不符）时返回 null。</summary>
    public string StrOf(string key)
    {
        BNode v = Get(key);
        return (v != null && v.Kind == KStr) ? v.Str : null;
    }

    public double NumOf(string key, double fallback)
    {
        BNode v = Get(key);
        return (v != null && v.Kind == KNum) ? v.Num : fallback;
    }

    public bool BoolOf(string key, bool fallback)
    {
        BNode v = Get(key);
        return (v != null && v.Kind == KBool) ? v.Bool : fallback;
    }

    const int MaxDepth = 48;

    public static BNode Parse(string text)
    {
        if (text == null) throw new Exception("empty input");
        int i = 0;
        BNode n = ParseValue(text, ref i, 0);
        SkipWs(text, ref i);
        if (i != text.Length) throw new Exception("trailing characters at offset " + i);
        return n;
    }

    static BNode ParseValue(string s, ref int i, int depth)
    {
        if (depth > MaxDepth) throw new Exception("nesting too deep");
        SkipWs(s, ref i);
        if (i >= s.Length) throw new Exception("unexpected end of input");
        char c = s[i];
        if (c == '{') return ParseObject(s, ref i, depth);
        if (c == '[') return ParseArray(s, ref i, depth);
        if (c == '"') { BNode n = new BNode(); n.Kind = KStr; n.Str = ParseString(s, ref i); return n; }
        if (c == 't') { Expect(s, ref i, "true"); BNode n = new BNode(); n.Kind = KBool; n.Bool = true; return n; }
        if (c == 'f') { Expect(s, ref i, "false"); BNode n = new BNode(); n.Kind = KBool; n.Bool = false; return n; }
        if (c == 'n') { Expect(s, ref i, "null"); return new BNode(); }
        return ParseNumber(s, ref i);
    }

    static BNode ParseObject(string s, ref int i, int depth)
    {
        BNode n = new BNode();
        n.Kind = KObj;
        n.Members = new List<KeyValuePair<string, BNode>>();
        i++;                                   // '{'
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return n; }
        while (true)
        {
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '"') throw new Exception("expected a member name at offset " + i);
            string key = ParseString(s, ref i);
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != ':') throw new Exception("expected ':' at offset " + i);
            i++;
            BNode v = ParseValue(s, ref i, depth + 1);
            n.Members.Add(new KeyValuePair<string, BNode>(key, v));
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == '}') { i++; return n; }
            throw new Exception("expected ',' or '}' at offset " + i);
        }
    }

    static BNode ParseArray(string s, ref int i, int depth)
    {
        BNode n = new BNode();
        n.Kind = KArr;
        n.Items = new List<BNode>();
        i++;                                   // '['
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return n; }
        while (true)
        {
            n.Items.Add(ParseValue(s, ref i, depth + 1));
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == ']') { i++; return n; }
            throw new Exception("expected ',' or ']' at offset " + i);
        }
    }

    static string ParseString(string s, ref int i)
    {
        if (i >= s.Length || s[i] != '"') throw new Exception("expected '\"' at offset " + i);
        i++;
        StringBuilder sb = new StringBuilder();
        while (true)
        {
            if (i >= s.Length) throw new Exception("unterminated string");
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= s.Length) throw new Exception("unterminated escape");
            char e = s[i++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 > s.Length) throw new Exception("bad \\u escape");
                    sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                    i += 4;
                    break;
                default: throw new Exception("bad escape '\\" + e + "'");
            }
        }
    }

    static BNode ParseNumber(string s, ref int i)
    {
        int start = i;
        while (i < s.Length)
        {
            char c = s[i];
            if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') { i++; continue; }
            break;
        }
        if (i == start) throw new Exception("unexpected character '" + s[start] + "' at offset " + start);
        double d;
        if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            throw new Exception("bad number '" + s.Substring(start, i - start) + "'");
        BNode n = new BNode();
        n.Kind = KNum;
        n.Num = d;
        return n;
    }

    static void Expect(string s, ref int i, string word)
    {
        if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
            throw new Exception("expected '" + word + "' at offset " + i);
        i += word.Length;
    }

    static void SkipWs(string s, ref int i)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
            break;
        }
    }
}

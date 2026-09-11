// 贴贴便签 · 一键启动器
// 作用：双击运行后，在本机启动一个极简 HTTP 服务（仅绑定 127.0.0.1）：
//       ① 静态文件服务 → 便签界面
//       ② 数据接口 /api/notes（GET 读 / PUT 写）→ 数据落盘到 data\notes.json
//       启动后自动打开默认浏览器。关闭本窗口即停止服务。
// 特点：不依赖 Python / Node，不联网，不需要管理员权限。
// 用法：贴贴便签.exe [端口]     （不传则从 8787 起找空闲端口）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class TietieLauncher
{
    static string baseDir;
    static int port;
    static NoteStore notes;

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

    static void Main(string[] args)
    {
        Console.Title = "贴贴便签";
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch { }

        // 定位便签界面目录：优先 exe 同级的 demo 目录，其次 exe 自身所在目录
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string demoDir = Path.Combine(exeDir, "demo");
        baseDir = Directory.Exists(demoDir) ? demoDir : exeDir;

        // 数据文件：exe 同级的 data\notes.json（整个便签数据 = 一个可备份、可带走的文件）
        notes = new NoteStore(Path.Combine(exeDir, "data", "notes.json"));

        if (!File.Exists(Path.Combine(baseDir, "index.html")))
        {
            Console.WriteLine("未找到便签界面（index.html）。");
            Console.WriteLine("请把本程序放在包含 demo 文件夹的目录中再运行。");
            Console.WriteLine("当前查找目录：" + baseDir);
            Console.WriteLine();
            Console.WriteLine("按任意键退出...");
            Console.ReadKey(true);
            return;
        }

        port = FindFreePort(PortArg(args, 8787));
        StartServer();

        string url = "http://127.0.0.1:" + port + "/";
        Console.WriteLine("=========================================");
        Console.WriteLine("  贴贴便签  已启动");
        Console.WriteLine("  界面地址：" + url);
        Console.WriteLine("  数据文件：" + notes.FilePath);
        Console.WriteLine("-----------------------------------------");
        Console.WriteLine("  数据由本程序管理，关闭浏览器不丢、清浏览器数据也不丢");
        Console.WriteLine("  关闭本窗口即可停止服务");
        Console.WriteLine("=========================================");
        Console.WriteLine();

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine("未能自动打开浏览器：" + ex.Message);
            Console.WriteLine("请手动在浏览器中访问：" + url);
        }

        // 保持运行
        while (true) Thread.Sleep(1000);
    }

    static int PortArg(string[] args, int fallback)
    {
        if (args != null && args.Length > 0)
        {
            int p;
            if (int.TryParse(args[0], out p) && p > 0 && p < 65536) return p;
        }
        return fallback;
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
        TcpListener listener = new TcpListener(IPAddress.Loopback, port);
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
                    WriteResponse(stream, 405, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("405 Method Not Allowed"));
                    return;
                }

                // ---------- 静态文件 ----------
                if (method != "GET" && method != "HEAD")
                {
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

    static string Escape(string s)
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
            : (status == 405 ? "Method Not Allowed" : "Error"))));
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

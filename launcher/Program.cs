// 贴贴便签 · 一键启动器
// 作用：双击运行后，在本机启动一个极简静态 HTTP 服务（仅绑定 127.0.0.1），
//       并自动打开默认浏览器指向便签界面。关闭本窗口即停止服务。
// 特点：不依赖 Python / Node，不联网，不需要管理员权限。

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

        // 定位便签界面目录：优先 exe 同级的 demo 目录，其次 exe 自身所在目录
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string demoDir = Path.Combine(exeDir, "demo");
        baseDir = Directory.Exists(demoDir) ? demoDir : exeDir;

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

        port = FindFreePort(8787);
        StartServer();

        string url = "http://127.0.0.1:" + port + "/";
        Console.WriteLine("=========================================");
        Console.WriteLine("  贴贴便签  已启动");
        Console.WriteLine("  界面地址：" + url);
        Console.WriteLine("-----------------------------------------");
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
                StreamReader reader = new StreamReader(stream, Encoding.ASCII);
                string requestLine = reader.ReadLine();
                if (string.IsNullOrEmpty(requestLine)) return;

                // 丢弃请求头
                while (true)
                {
                    string h = reader.ReadLine();
                    if (string.IsNullOrEmpty(h)) break;
                }

                string[] parts = requestLine.Split(' ');
                if (parts.Length < 2) return;

                string urlPath = parts[1];
                int q = urlPath.IndexOf('?');
                if (q >= 0) urlPath = urlPath.Substring(0, q);
                urlPath = Uri.UnescapeDataString(urlPath);
                if (urlPath.Length == 0 || urlPath == "/") urlPath = "/index.html";

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

    static void WriteResponse(NetworkStream stream, int status, string contentType, byte[] body)
    {
        string statusText = status == 200 ? "OK" : (status == 404 ? "Not Found" : "Forbidden");
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

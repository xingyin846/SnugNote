// S1 sticker: file logger (append-only, never throws).
// Pure ASCII source.
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace TieTieSticker
{
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static string Path
        {
            get { return _path; }
        }

        public static void Init(string exeDir)
        {
            lock (Gate)
            {
                try
                {
                    _path = System.IO.Path.Combine(exeDir, "sticker-debug.log");
                    File.AppendAllText(_path, "", Encoding.UTF8);
                }
                catch
                {
                    try
                    {
                        string dir = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "TieTieSticker");
                        Directory.CreateDirectory(dir);
                        _path = System.IO.Path.Combine(dir, "sticker-debug.log");
                        File.AppendAllText(_path, "", Encoding.UTF8);
                    }
                    catch
                    {
                        _path = null;
                    }
                }
            }
        }

        public static void Write(string fmt, params object[] args)
        {
            string msg;
            try
            {
                msg = (args == null || args.Length == 0) ? fmt : string.Format(CultureInfo.InvariantCulture, fmt, args);
            }
            catch
            {
                msg = fmt;
            }
            Line(msg);
        }

        public static void Line(string msg)
        {
            lock (Gate)
            {
                if (_path == null) return;
                try
                {
                    string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    File.AppendAllText(_path, stamp + " [" + AppDomain.CurrentDomain.Id + "] " + msg + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // logging must never break the app
                }
            }
        }

        public static void Exception(string where, Exception ex)
        {
            try
            {
                Line("EXCEPTION in " + where + ": " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
            }
            catch
            {
            }
        }
    }
}

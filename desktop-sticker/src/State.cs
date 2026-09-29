// S1 sticker: sticker-state.json persistence (spec section 8).
// Atomic replace, UTF-8 no BOM, lock-guarded, immediate (no throttling).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace TieTieSticker
{
    internal sealed class StickerRecord
    {
        public string NoteId = "";
        public int X;
        public int Y;
        public int W = Typo.DEFAULT_W;
        public int H = Typo.DEFAULT_H;
        public bool TopMost;
        /// <summary>v21: this card was closed by the user, but its geometry is REMEMBERED. A closed
        /// record owns no window; it exists so that an emptied desk can be restored with the very
        /// arrangement the user had, instead of the place-every-note-in-a-cascade fallback (which the
        /// v20 auto-start turned into an every-sign-in dump - user report 2026-09-22).</summary>
        public bool Closed;
        public string Monitor = "";
        public bool HasMonitorBounds;
        public int MonitorX, MonitorY, MonitorW, MonitorH;

        public StickerRecord Clone()
        {
            StickerRecord r = new StickerRecord();
            r.NoteId = NoteId; r.X = X; r.Y = Y; r.W = W; r.H = H; r.TopMost = TopMost; r.Closed = Closed;
            r.Monitor = Monitor; r.HasMonitorBounds = HasMonitorBounds;
            r.MonitorX = MonitorX; r.MonitorY = MonitorY; r.MonitorW = MonitorW; r.MonitorH = MonitorH;
            return r;
        }
    }

    internal sealed class StickerState
    {
        public const string AppTag = "tietie-sticker";
        public const int SchemaVersion = 1;

        public List<StickerRecord> Records = new List<StickerRecord>();

        /// <summary>True when the state file did not exist -&gt; "first run" (spec 10.1 step 2 / P1).</summary>
        public bool FirstRun = true;

        /// <summary>Set when the file existed but had to be discarded (schema mismatch / bad JSON).</summary>
        public bool Corrupt;

        // ---------------- load ----------------

        // Spec v11.2 3.12 (captain ruling): the %APPDATA%\TieTieSticker file is WRITE-ONLY - it is
        // never read. Load therefore takes ONLY the primary path; there is no parameter through which
        // a fallback could be read (assertion fallback-is-never-read #24 is mutation-proven by putting
        // the fallback read back, which must turn it red).
        public static StickerState Load(string primaryPath)
        {
            StickerState st = new StickerState();
            string path = PickReadable(primaryPath);
            if (path == null)
            {
                Log.Line("state: no file at " + primaryPath + " -> FIRST RUN (spec 8.1 v11.2: the %APPDATA% fallback is write-only and is never read)");
                st.FirstRun = true;
                return st;
            }

            string text = null;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Line("state read failed (" + path + "): " + ex.GetType().Name + ": " + ex.Message + " -> treat as first run");
                st.FirstRun = true;
                return st;
            }

            JsonValue root = null;
            try
            {
                root = Json.Parse(text);
            }
            catch (Exception ex)
            {
                Log.Line("state JSON invalid (" + path + "): " + ex.Message);
            }

            if (root == null || !root.IsObject)
            {
                st.FirstRun = false;
                st.Corrupt = true;
                Backup(path, "invalid-json");
                Log.Line("state: not a JSON object -> backed up and rebuilt as empty schema:1");
                return st;
            }

            string app = JsonValue.StrOr(root, "app", "");
            JsonValue sv = root.Get("schema");
            int schema = (sv != null && sv.Kind == JsonKind.Number) ? (int)sv.Number : -1;
            if (!string.Equals(app, AppTag, StringComparison.Ordinal) || schema != SchemaVersion)
            {
                st.FirstRun = false;
                st.Corrupt = true;
                Backup(path, "schema-mismatch");
                Log.Line("state: app='" + app + "' schema=" + schema + " -> backed up and rebuilt as schema:1 (file NOT deleted)");
                return st;
            }

            st.FirstRun = false;
            JsonValue arr = root.Get("stickers");
            if (arr == null || !arr.IsArray)
            {
                Log.Line("state: stickers missing / not an array -> empty list");
                return st;
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < arr.Items.Count; i++)
            {
                JsonValue s = arr.Items[i];
                if (s == null || !s.IsObject) continue;
                JsonValue idv = s.Get("noteId");
                if (idv == null || idv.Kind != JsonKind.String || string.IsNullOrEmpty(idv.Str)) continue;
                if (seen.Contains(idv.Str))
                {
                    Log.Line("state: duplicate noteId in file, keeping the first occurrence: " + idv.Str);
                    continue;
                }
                seen.Add(idv.Str);

                StickerRecord r = new StickerRecord();
                r.NoteId = idv.Str;
                r.X = IntOr(s, "x", int.MinValue);
                r.Y = IntOr(s, "y", int.MinValue);
                r.W = IntOr(s, "w", Typo.DEFAULT_W);
                r.H = IntOr(s, "h", Typo.DEFAULT_H);
                r.TopMost = JsonValue.BoolOr(s, "topMost", false);
                r.Closed = JsonValue.BoolOr(s, "closed", false);
                r.Monitor = JsonValue.StrOr(s, "monitor", "");
                JsonValue mb = s.Get("monitorBounds");
                if (mb != null && mb.IsObject)
                {
                    r.HasMonitorBounds = true;
                    r.MonitorX = IntOr(mb, "x", 0);
                    r.MonitorY = IntOr(mb, "y", 0);
                    r.MonitorW = IntOr(mb, "w", 0);
                    r.MonitorH = IntOr(mb, "h", 0);
                }
                st.Records.Add(r);
            }

            Log.Line("state loaded from " + path + ": " + st.Records.Count + " record(s)");
            return st;
        }

        private static string PickReadable(string primary)
        {
            // Spec v11.2 3.12: primary only. The former second line
            //     if (!string.IsNullOrEmpty(fallback) && File.Exists(fallback)) return fallback;
            // was the deviation recorded in the spec (a missing primary silently loaded whatever the
            // write-only fallback happened to contain). REMOVED - do not reintroduce.
            if (!string.IsNullOrEmpty(primary) && File.Exists(primary)) return primary;
            return null;
        }

        private static int IntOr(JsonValue parent, string key, int fallback)
        {
            if (parent == null) return fallback;
            JsonValue v = parent.Get(key);
            if (v == null || v.Kind != JsonKind.Number) return fallback;
            double d = v.Number;
            if (d > int.MaxValue || d < int.MinValue) return fallback;
            return (int)Math.Round(d);
        }

        private static void Backup(string path, string reason)
        {
            try
            {
                string bak = path + ".bak";
                File.Copy(path, bak, true);
                Log.Line("state: backup written " + bak + " (reason=" + reason + ")");
            }
            catch (Exception ex)
            {
                Log.Line("state: backup failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------------- save ----------------

        /// <summary>
        /// Serializes and atomically replaces the state file at every given path.
        /// Paths are tried in priority order (exeDir primary; the %APPDATA% entry passed
        /// last is a FALLBACK and is only written when the earlier target could not be written, so a
        /// sandboxed environment does not produce a failed-write line on every persist).
        /// Returns the paths that actually received the body (for the audit log).
        /// </summary>
        /// <summary>
        /// What the last Save() attempt actually did, per path. Reported verbatim in the audit log so
        /// the record can never claim a path that was not really written (the earlier single-list format
        /// logged the FAILED %APPDATA% entry as if it had been written).
        /// </summary>
        public sealed class SaveReport
        {
            public List<string> Written = new List<string>();
            public List<string> Offered = new List<string>();
            public List<KeyValuePair<string, string>> Failed = new List<KeyValuePair<string, string>>();

            public string Describe()
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("written=[").Append(Written.Count == 0 ? "none" : string.Join(" | ", Written.ToArray())).Append(']');
                if (Offered.Count > 0) sb.Append(" offered=[").Append(string.Join(" | ", Offered.ToArray())).Append(']');
                if (Failed.Count > 0)
                {
                    List<string> f = new List<string>();
                    for (int i = 0; i < Failed.Count; i++) f.Add(Failed[i].Key + " (" + Failed[i].Value + ")");
                    sb.Append(" failed=[").Append(string.Join(" | ", f.ToArray())).Append(']');
                }
                return sb.ToString();
            }
        }

        public SaveReport LastReport = new SaveReport();

        /// <summary>
        /// Serializes and atomically replaces the state file at every given path.
        /// Paths are tried in priority order (exeDir primary; the %APPDATA% entry passed last is
        /// a documented FALLBACK and is only offered when no higher-priority target could be written, so a
        /// sandboxed environment does not attempt - or report - it on every persist).
        /// Returns the per-path outcome so the caller's log states exactly what happened.
        /// </summary>
        public SaveReport Save(string[] paths)
        {
            _writeStartedUtc = DateTime.UtcNow;
            SaveReport report = new SaveReport();
            List<string> attempted = new List<string>();
            string body = Serialize(DateTime.UtcNow);
            bool wroteSomething = false;
            for (int i = 0; i < paths.Length; i++)
            {
                if (string.IsNullOrEmpty(paths[i])) continue;
                bool dup = false;
                for (int j = 0; j < i; j++)
                {
                    if (string.Equals(paths[j], paths[i], StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                }
                if (dup) continue;
                if (wroteSomething && i >= 2)
                {
                    report.Offered.Add(paths[i]);   // fallback available but not needed
                    continue;
                }
                attempted.Add(paths[i]);
                string err = AtomicWrite(paths[i], body);
                if (err == null) report.Written.Add(paths[i]);
                else report.Failed.Add(new KeyValuePair<string, string>(paths[i], err));
                wroteSomething = report.Written.Count > 0;
            }

            // keep the "was this file refreshed by THIS write" cross-check for the audit trail
            List<string> verified = new List<string>();
            for (int i = 0; i < attempted.Count; i++)
            {
                try
                {
                    if (File.Exists(attempted[i])
                        && File.GetLastWriteTimeUtc(attempted[i]).Ticks >= _writeStartedUtc.AddSeconds(-1).Ticks)
                    {
                        verified.Add(attempted[i]);
                    }
                }
                catch { }
            }
            report.Written = verified;
            LastReport = report;
            return report;
        }

        private DateTime _writeStartedUtc;

        /// <summary>Atomic write. Returns null on success, or the error summary on failure.</summary>
        private static string AtomicWrite(string path, string body)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, body, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tmp, path, null, true);
                        return null;
                    }
                    catch
                    {
                        File.Delete(path);
                    }
                }
                File.Move(tmp, path);
                return null;
            }
            catch (Exception ex)
            {
                string detail = ex.GetType().Name + ": " + ex.Message;
                Log.Line("state WRITE FAILED (" + path + "): " + detail);
                return detail;
            }
        }

        public string Serialize(DateTime savedAtUtc)
        {
            JsonValue root = JsonValue.NewObject();
            root.Put("schema", JsonValue.FromInt(SchemaVersion));
            root.Put("app", JsonValue.FromString(AppTag));
            long ms = (long)(savedAtUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            root.Put("savedAt", JsonValue.FromInt(ms));

            JsonValue arr = JsonValue.NewArray();
            for (int i = 0; i < Records.Count; i++)
            {
                StickerRecord r = Records[i];
                JsonValue o = JsonValue.NewObject();
                o.Put("noteId", JsonValue.FromString(r.NoteId));
                o.Put("x", JsonValue.FromInt(r.X));
                o.Put("y", JsonValue.FromInt(r.Y));
                o.Put("w", JsonValue.FromInt(r.W));
                o.Put("h", JsonValue.FromInt(r.H));
                o.Put("topMost", JsonValue.FromBool(r.TopMost));
                if (r.Closed) o.Put("closed", JsonValue.FromBool(true));
                if (!string.IsNullOrEmpty(r.Monitor)) o.Put("monitor", JsonValue.FromString(r.Monitor));
                if (r.HasMonitorBounds)
                {
                    JsonValue mb = JsonValue.NewObject();
                    mb.Put("x", JsonValue.FromInt(r.MonitorX));
                    mb.Put("y", JsonValue.FromInt(r.MonitorY));
                    mb.Put("w", JsonValue.FromInt(r.MonitorW));
                    mb.Put("h", JsonValue.FromInt(r.MonitorH));
                    o.Put("monitorBounds", mb);
                }
                arr.Items.Add(o);
            }
            root.Put("stickers", arr);
            return Json.Write(root);
        }
    }

    /// <summary>Spec 8.5: out-of-bounds clamping back into the primary screen working area.</summary>
    internal static class ScreenClamp
    {
        public static bool IsVisibleEnough(Rectangle r)
        {
            Screen[] screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                Rectangle wa = screens[i].WorkingArea;
                Rectangle inter = Rectangle.Intersect(r, wa);
                if (inter.Width >= Typo.MIN_VISIBLE_W && inter.Height >= Typo.MIN_VISIBLE_H) return true;
            }
            return false;
        }

        /// <summary>Returns true when the rectangle had to be moved/resized.</summary>
        public static bool ClampToPrimary(ref Rectangle r)
        {
            if (IsVisibleEnough(r)) return false;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int w = ClampInt(r.Width, Typo.MIN_W, Math.Max(Typo.MIN_W, wa.Width));
            int h = ClampInt(r.Height, Typo.MIN_H, Math.Max(Typo.MIN_H, wa.Height));
            int x = ClampInt(r.X, wa.X, wa.X + wa.Width - w);
            int y = ClampInt(r.Y, wa.Y, wa.Y + wa.Height - h);
            bool changed = (w != r.Width || h != r.Height || x != r.X || y != r.Y);
            r = new Rectangle(x, y, w, h);
            return changed;
        }

        public static int ClampInt(int v, int lo, int hi)
        {
            if (hi < lo) hi = lo;
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        public static string ScreenOf(Rectangle r)
        {
            Screen s = Screen.FromRectangle(r) ?? Screen.PrimaryScreen;
            return s.DeviceName;
        }
    }

    internal static class PathPick
    {
        /// <summary>
        /// Spec 8.1: EXACTLY TWO candidates - exeDir first, %APPDATA%\TieTieSticker as the write-only
        /// fallback. Spec v11 8.1 (captain ruling): the former third candidate
        /// (workspace-level data\sticker-state.json) is DELETED - it was outside spec 8.1, it dropped a
        /// new file into the read-only data\ directory, and that path sits in the .gitignore blind spot.
        /// Guarded by the assertion state-path-candidates-exactly-two.
        /// </summary>
        public static string[] CandidatePaths(string exeDir, string appDataDir)
        {
            List<string> list = new List<string>();
            if (!string.IsNullOrEmpty(exeDir)) list.Add(Path.Combine(exeDir, "sticker-state.json"));
            if (!string.IsNullOrEmpty(appDataDir)) list.Add(Path.Combine(appDataDir, "sticker-state.json"));
            return list.ToArray();
        }
    }
}

// v13 sticker: the FILE-RELAY bridge to the web UI (through the launcher).
//
// Why a file relay and nothing nicer:
//   1. a browser cannot create a native desktop window, so the web page can only ASK another
//      process to do it;
//   2. this program has a hard constraint since S1: it must not listen on any port, and the
//      self-test bans member names containing Socket / TcpListener / HttpListener / NamedPipe
//      (assertion no-listener-symbol-in-assembly) - so ports AND named pipes are both out;
//   3. the only component that already knows the data directory AND already listens on a port is
//      the launcher (TieTie.exe, 127.0.0.1 + /api/notes).
//
// Single-writer matrix (same discipline as the data layer, so no file lock is needed and the two
// sides can never overwrite each other):
//   data\bridge\request.json - written ONLY by the launcher (append-only, monotonic seq);
//                              this process only READS it.
//   data\bridge\placed.json  - written ONLY by this process; the launcher only reads it.
//
// Pure ASCII source. NO sockets, NO pipes, NO listeners - plain files only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace TieTieSticker
{
    /// <summary>One queued request as written by the launcher (seq / action / noteId / tsMs).</summary>
    internal sealed class BridgeRequest
    {
        public long Seq;
        public string Action = "";
        public string NoteId = "";
        /// <summary>When the launcher wrote it (epoch ms, 0 = the launcher did not say).
        /// v14 uses it as the freshness fence for the exit request: a control request that has been
        /// sitting in the file since a previous session must never shut down a live app.</summary>
        public long TsMs;
    }

    /// <summary>One row of the published view (what the web button reads back).</summary>
    internal sealed class BridgePlacement
    {
        public string NoteId = "";
        public string Title = "";
        public int X, Y, W, H;
        public bool TopMost;
    }

    /// <summary>What happened to one request - the manager's answer, used for logging and tests.</summary>
    internal enum BridgeApplyOutcome
    {
        Placed,
        AlreadyPlaced,
        Removed,
        NotPlaced,
        NoteMissing
    }

    /// <summary>
    /// The pure half of the bridge: path rules, request parsing, view serialisation.
    /// No windows, no timers - so the self-test can drive it headlessly and mutate it cheaply.
    /// </summary>
    internal static class BridgeProtocol
    {
        public const string ActionPlace = "place";
        public const string ActionRemove = "remove";
        /// <summary>v14: the launcher's main window is closing -> please shut this app down cleanly.
        /// Control request: no noteId, and honoured only while it is still fresh (ExitFreshnessMs).</summary>
        public const string ActionExit = "exit";
        /// <summary>How old an exit request may be and still be obeyed. The launcher only writes one
        /// when it believes this app is running, so in practice it is consumed within one poll;
        /// the fence exists because the request FILE outlives both processes.</summary>
        public const long ExitFreshnessMs = 120000;
        public const string DirName = "bridge";
        public const string RequestFileName = "request.json";
        public const string PlacedFileName = "placed.json";

        /// <summary>Is this exit request recent enough to act on? A missing timestamp (0) is treated
        /// as NOT fresh: an unattributable control request must never kill a running app.</summary>
        public static bool ExitIsFresh(BridgeRequest r, long nowMs)
        {
            if (r == null) return false;
            if (r.TsMs <= 0) return false;
            long age = nowMs - r.TsMs;
            return age >= -5000 && age <= ExitFreshnessMs;   // tolerate a little clock skew
        }

        /// <summary>
        /// The bridge lives NEXT TO the note database (data\bridge), never under the exe: the sticker
        /// exe sits in .tools\sticker_build, while the shared data directory is the one the launcher owns.
        /// </summary>
        public static string DirForNotesPath(string notesPath)
        {
            if (string.IsNullOrEmpty(notesPath)) return null;
            string dir;
            try { dir = Path.GetDirectoryName(notesPath); }
            catch { return null; }
            if (string.IsNullOrEmpty(dir)) return null;
            return Path.Combine(dir, DirName);
        }

        public static string RequestPath(string bridgeDir) { return Path.Combine(bridgeDir, RequestFileName); }
        public static string PlacedPath(string bridgeDir) { return Path.Combine(bridgeDir, PlacedFileName); }

        /// <summary>
        /// Parse the request file. Tolerant by design: a missing field, an unknown action or a
        /// non-positive seq drops THAT entry (the launcher writes the file, so a partly broken file
        /// must not freeze the queue); a file that is not JSON at all yields an empty list + error.
        /// </summary>
        public static List<BridgeRequest> ParseRequests(string text, out string error)
        {
            error = null;
            List<BridgeRequest> list = new List<BridgeRequest>();
            if (string.IsNullOrEmpty(text)) return list;

            JsonValue root = null;
            try { root = Json.Parse(text); }
            catch (Exception ex) { error = "request.json is not valid JSON: " + ex.Message; return list; }
            if (root == null || !root.IsObject) { error = "request.json root is not an object"; return list; }

            JsonValue arr = root.Get("requests");
            if (arr == null || !arr.IsArray) { error = "request.json has no requests array"; return list; }

            int skipped = 0;
            for (int i = 0; i < arr.Items.Count; i++)
            {
                JsonValue it = arr.Items[i];
                if (it == null || !it.IsObject) { skipped++; continue; }

                JsonValue sq = it.Get("seq");
                JsonValue ac = it.Get("action");
                JsonValue nid = it.Get("noteId");
                JsonValue ts = it.Get("tsMs");

                if (sq == null || sq.Kind != JsonKind.Number) { skipped++; continue; }
                long seq = (long)Math.Round(sq.Number);
                if (seq <= 0) { skipped++; continue; }
                if (ac == null || ac.Kind != JsonKind.String) { skipped++; continue; }
                string action = ac.Str;
                if (action != ActionPlace && action != ActionRemove && action != ActionExit) { skipped++; continue; }
                long atMs = (ts != null && ts.Kind == JsonKind.Number) ? (long)Math.Round(ts.Number) : 0;

                if (action == ActionExit)
                {
                    // v14: a CONTROL request - it targets the app, not a note, so it has no noteId.
                    BridgeRequest re = new BridgeRequest();
                    re.Seq = seq;
                    re.Action = action;
                    re.NoteId = "";
                    re.TsMs = atMs;
                    list.Add(re);
                    continue;
                }
                if (nid == null || nid.Kind != JsonKind.String || nid.Str == null || nid.Str.Length == 0) { skipped++; continue; }

                BridgeRequest r = new BridgeRequest();
                r.Seq = seq;
                r.Action = action;
                r.NoteId = nid.Str;
                r.TsMs = atMs;
                list.Add(r);
            }
            if (skipped > 0) error = "request.json: " + skipped + " unusable entr(ies) skipped";
            return list;
        }

        /// <summary>Last seq this process already acknowledged (0 when absent/unreadable).</summary>
        public static long ReadAckSeq(string placedPath)
        {
            try
            {
                if (!File.Exists(placedPath)) return 0;
                JsonValue root = Json.Parse(File.ReadAllText(placedPath, Encoding.UTF8));
                if (root == null || !root.IsObject) return 0;
                JsonValue ack = root.Get("ackSeq");
                if (ack == null || ack.Kind != JsonKind.Number) return 0;
                long n = (long)Math.Round(ack.Number);
                return n > 0 ? n : 0;
            }
            catch { return 0; }
        }

        /// <summary>Serialise the published view (canonical JSON, via the project's own writer).</summary>
        public static string BuildViewJson(List<BridgePlacement> rows, long ackSeq, long savedAtMs)
        {
            JsonValue root = JsonValue.NewObject();
            root.Put("savedAtMs", JsonValue.FromInt(savedAtMs));
            root.Put("ackSeq", JsonValue.FromInt(ackSeq));
            JsonValue arr = JsonValue.NewArray();
            for (int i = 0; i < rows.Count; i++)
            {
                JsonValue o = JsonValue.NewObject();
                o.Put("noteId", JsonValue.FromString(rows[i].NoteId));
                o.Put("title", JsonValue.FromString(rows[i].Title));
                o.Put("x", JsonValue.FromInt(rows[i].X));
                o.Put("y", JsonValue.FromInt(rows[i].Y));
                o.Put("w", JsonValue.FromInt(rows[i].W));
                o.Put("h", JsonValue.FromInt(rows[i].H));
                o.Put("topMost", JsonValue.FromBool(rows[i].TopMost));
                arr.Items.Add(o);
            }
            root.Put("placed", arr);
            return Json.Write(root);
        }
    }

    /// <summary>
    /// The runtime half: consume pending requests (once at startup, then every PollIntervalMs) and
    /// publish the placed view after every state write.
    /// Deliberately a Timer poll and NOT a FileSystemWatcher: one less race class to reason about
    /// (the spec picked the poll), and the request file persists, so a button pressed while this
    /// program was closed is applied at the next start instead of being lost.
    /// </summary>
    internal sealed class DesktopBridge
    {
        public const int PollIntervalMs = 1000;

        private readonly StickerManager _mgr;
        private readonly string _dir;
        private readonly string _requestPath;
        private readonly string _placedPath;
        /// <summary>v14: what "the launcher asked us to quit" actually does. It is a field (not a
        /// direct _mgr.ExitAll call) so the self-test can observe THIS call site: breaking the wiring
        /// here must turn the exit assertion red instead of only breaking a pure helper.</summary>
        private readonly Action<string> _exitAction;

        private long _ackSeq;
        private Timer _timer;
        private bool _stopped;
        private int _pollCount;
        private int _appliedCount;

        public DesktopBridge(StickerManager mgr, string bridgeDir)
            : this(mgr, bridgeDir, null)
        {
        }

        internal DesktopBridge(StickerManager mgr, string bridgeDir, Action<string> exitAction)
        {
            _mgr = mgr;
            _dir = bridgeDir;
            _requestPath = BridgeProtocol.RequestPath(bridgeDir);
            _placedPath = BridgeProtocol.PlacedPath(bridgeDir);
            _exitAction = exitAction != null
                ? exitAction
                : delegate(string reason) { _mgr.ExitAll(reason); };

            // ---- v14 FIX: read the acknowledged seq HERE, not in Start() ----
            // Program.cs wires the publisher and THEN calls mgr.Start(), whose "startup" Persist
            // publishes the view - and Publish() writes THIS field. While the read lived in Start(),
            // the first publish therefore wrote ackSeq=0 OVER the real ack; Start() then read that 0
            // back and the whole request history was replayed on every launch (observed 2026-09-21:
            // a stale "remove" closed a card the user had on screen, and stale "place" requests
            // re-cascaded cards away from their stored geometry). Reading it in the constructor makes
            // that window impossible instead of merely unlikely.
            _ackSeq = BridgeProtocol.ReadAckSeq(_placedPath);
            _mgr.SetBridgeAck(_ackSeq);
        }

        public string Dir { get { return _dir; } }
        public string RequestPath { get { return _requestPath; } }
        public string PlacedPath { get { return _placedPath; } }
        public long AckSeq { get { return _ackSeq; } }
        public int PollCount { get { return _pollCount; } }
        public int AppliedCount { get { return _appliedCount; } }

        /// <summary>
        /// v17 (user 2026-09-21): how many UNACKNOWLEDGED "place" requests are waiting in request.json?
        /// The launcher appends the request BEFORE it starts this process (v13.1 order, verified in
        /// launcher/Program.cs), so at start-up this count is exactly "the user just clicked 「贴到桌面」
        /// for these named notes". The manager uses it to suppress the "empty placed set -> place ALL
        /// notes" fallback: otherwise clicking the button while the desktop holds no card dumps every
        /// note onto the desktop instead of the one that was clicked.
        /// </summary>
        public int PendingPlaceCount()
        {
            try
            {
                if (!File.Exists(_requestPath)) return 0;
                string text = File.ReadAllText(_requestPath, Encoding.UTF8);
                if (string.IsNullOrEmpty(text)) return 0;
                string perr;
                List<BridgeRequest> reqs = BridgeProtocol.ParseRequests(text, out perr);
                int n = 0;
                for (int i = 0; i < reqs.Count; i++)
                {
                    if (reqs[i].Seq <= _ackSeq) continue;
                    if (string.Equals(reqs[i].Action, BridgeProtocol.ActionPlace, StringComparison.Ordinal)) n++;
                }
                return n;
            }
            catch (Exception ex)
            {
                Log.Exception("bridge PendingPlaceCount", ex);
                return 0;
            }
        }

        /// <summary>Startup: report the ack picked up at construction, consume once (a request that was
        /// pressed while this program was closed must still be applied), then poll.</summary>
        public void Start()
        {
            // NOTE: the ack was already read (and handed to the manager) in the constructor; see the
            // comment there for why it must NOT be read here. Nothing may reset _ackSeq after this.
            Log.Line("bridge: dir=" + _dir + " ackSeq(from construction)=" + _ackSeq
                + " pollMs=" + PollIntervalMs + " requestFile=" + _requestPath);
            ConsumeOnce();

            _timer = new Timer();
            _timer.Interval = PollIntervalMs;
            _timer.Tick += delegate(object s, EventArgs e) { ConsumeOnce(); };
            _timer.Start();
            Log.Line("bridge: polling started");
        }

        public void Stop()
        {
            _stopped = true;
            if (_timer != null)
            {
                try { _timer.Stop(); _timer.Dispose(); } catch { }
                _timer = null;
            }
        }

        /// <summary>
        /// Read the request file and apply every request with seq &gt; ackSeq, in file order.
        /// request.json is READ-ONLY here: this process never writes it (single-writer matrix), and
        /// the ack is advanced BEFORE each apply so a request that cannot be applied (unknown note)
        /// still moves the queue forward instead of blocking every later request forever.
        /// Returns the number of requests applied.
        /// </summary>
        public int ConsumeOnce()
        {
            _pollCount++;
            string text = null;
            try
            {
                if (File.Exists(_requestPath)) text = File.ReadAllText(_requestPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Line("bridge: request.json read failed (will retry): " + ex.GetType().Name + ": " + ex.Message);
                return 0;
            }
            if (string.IsNullOrEmpty(text)) return 0;

            string perr;
            List<BridgeRequest> reqs = BridgeProtocol.ParseRequests(text, out perr);
            if (perr != null) Log.Line("bridge: " + perr);

            int applied = 0;
            for (int i = 0; i < reqs.Count; i++)
            {
                BridgeRequest r = reqs[i];
                if (r.Seq <= _ackSeq) continue;

                _ackSeq = r.Seq;
                _mgr.SetBridgeAck(_ackSeq);          // any Persist inside the apply publishes the new ack

                if (string.Equals(r.Action, BridgeProtocol.ActionExit, StringComparison.Ordinal))
                {
                    // v14: the launcher's window is closing -> quit this app cleanly (persist state,
                    // release the mutex, drop the tray icon). Only a FRESH request counts: the file
                    // outlives both processes, so a stale exit must never kill a live app.
                    applied++;
                    bool fresh = BridgeProtocol.ExitIsFresh(r, NowMs());
                    Log.Line("bridge: seq=" + r.Seq + " action=exit tsMs=" + r.TsMs + " fresh=" + fresh
                        + (fresh ? " -> shutting down (launcher closed)" : " -> IGNORED (stale / no timestamp)"));
                    Publish("bridge-exit");          // ack must be visible even though no record changed
                    if (fresh)
                    {
                        _exitAction("launcher-exit");
                        return applied;
                    }
                    continue;
                }

                BridgeApplyOutcome o = _mgr.ApplyBridgeRequest(r.Action == BridgeProtocol.ActionPlace, r.NoteId);
                applied++;
                Log.Line("bridge: seq=" + r.Seq + " action=" + r.Action + " noteId=" + Short(r.NoteId)
                    + " -> " + o);
            }

            if (applied > 0)
            {
                _appliedCount += applied;
                // ALWAYS publish once more: a no-op request (already placed) changes no record and
                // therefore triggers no Persist, yet its ack MUST be visible to the web page.
                Publish("bridge-batch");
            }
            return applied;
        }

        /// <summary>Rewrite the placed view. Called after every state write (so the web button can
        /// never drift from the real placed set: closing a card with X updates it too).</summary>
        public void Publish(string reason)
        {
            if (_stopped) return;
            try
            {
                List<BridgePlacement> rows = _mgr.BridgePlacements();
                string json = BridgeProtocol.BuildViewJson(rows, _ackSeq, NowMs());
                WriteAtomic(_placedPath, json);
                Log.Line("bridge: view published (" + reason + "): " + rows.Count + " placed, ackSeq=" + _ackSeq
                    + " -> " + _placedPath);
            }
            catch (Exception ex)
            {
                Log.Exception("bridge publish(" + reason + ")", ex);
            }
        }

        /// <summary>temp + File.Replace, never Delete + Move (a concurrent reader must never observe
        /// "the file does not exist"). Falls back to an in-place overwrite if Replace is unavailable.</summary>
        private static void WriteAtomic(string path, string text)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
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

        private static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private static string Short(string id)
        {
            if (string.IsNullOrEmpty(id)) return "(empty)";
            return id.Length <= 8 ? id : id.Substring(0, 8);
        }
    }
}

// S1 sticker: entry point - argument parsing, single-instance mutex, startup wiring.
// Pure ASCII source. NO WebView2, NO sockets, NO listeners.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace TieTieSticker
{
    internal sealed class Options
    {
        public List<string> NoteIds = new List<string>();
        public string NotesPath;
        public bool VerboseHitTest;
        public bool SelfTest;
        public string RenderPath;
        public bool RenderSamples;
        /// <summary>Render with floating=true (spec v11.2 3.11): used only by the render CLI so the
        /// floating card face - and therefore the pink-dot assertion #21 - is not vacuous. It NEVER
        /// changes runtime semantics: inside the app, floating is driven by the pin state alone.</summary>
        public bool RenderFloating;

        /// <summary>S2 (spec v12 4.x): headless checklist write - "click the box at (id, index)" without
        /// a window. Exercises the REAL write path so the interaction script can drive it and read the
        /// outcome + exit code. Requires the single-instance mutex to be free, like every other mode.</summary>
        public string ToggleCheckNoteId;
        public int ToggleCheckIndex = -1;

        public static Options Parse(string[] args)
        {
            Options o = new Options();
            List<string> positional = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, "--note", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) o.NoteIds.Add(args[++i]);
                }
                else if (a.StartsWith("--note=", StringComparison.OrdinalIgnoreCase))
                {
                    o.NoteIds.Add(a.Substring("--note=".Length));
                }
                else if (string.Equals(a, "--notes", StringComparison.OrdinalIgnoreCase))
                {
                    if (NextIsValue(args, i)) o.NotesPath = args[++i];
                }
                else if (a.StartsWith("--notes=", StringComparison.OrdinalIgnoreCase))
                {
                    o.NotesPath = a.Substring("--notes=".Length);
                }
                else if (string.Equals(a, "--verbose-hit", StringComparison.OrdinalIgnoreCase))
                {
                    o.VerboseHitTest = true;
                }
                else if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase))
                {
                    o.SelfTest = true;
                }
                else if (string.Equals(a, "--render", StringComparison.OrdinalIgnoreCase))
                {
                    if (NextIsValue(args, i)) o.RenderPath = args[++i];
                }
                else if (string.Equals(a, "--render-samples", StringComparison.OrdinalIgnoreCase))
                {
                    // deliberately does NOT consume the next token: v11.2 documents
                    // `--render-samples --floating <dir>`, where the path comes AFTER a flag.
                    // A bare path is picked up by the positional fallback below.
                    o.RenderSamples = true;
                }
                else if (string.Equals(a, "--floating", StringComparison.OrdinalIgnoreCase))
                {
                    o.RenderFloating = true;
                }
                else if (string.Equals(a, "--toggle-check", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 2 < args.Length)
                    {
                        o.ToggleCheckNoteId = args[++i];
                        int idx;
                        if (int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
                            o.ToggleCheckIndex = idx;
                    }
                }
                else if (a.StartsWith("--render=", StringComparison.OrdinalIgnoreCase))
                {
                    o.RenderPath = a.Substring("--render=".Length);
                }
                else if (!a.StartsWith("--", StringComparison.Ordinal))
                {
                    positional.Add(a);
                }
            }

            // The render output directory may be written before OR after `--floating`. Neither token may
            // swallow the other (the old parser ate "--floating" as the path, so the documented command
            // silently rendered WITHOUT floating into a directory literally named "--floating").
            if (string.IsNullOrEmpty(o.RenderPath) && positional.Count > 0) o.RenderPath = positional[0];
            return o;
        }

        /// <summary>
        /// True when args[i+1] is a VALUE and not another flag. Without this, `--render-samples --floating out`
        /// swallowed "--floating" as the output path (so the v11.2 documented command silently rendered
        /// WITHOUT floating - the pink-dot assertion #21 would then have been reading the wrong image).
        /// </summary>
        private static bool NextIsValue(string[] args, int i)
        {
            return i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
        }
    }

    internal static class Program
    {
        private const string MutexName = "Local\\TieTieSticker.S1";
        private static Mutex _instanceMutex;

        [STAThread]
        internal static void Main(string[] args)
        {
            // v14: this entry point is reached in TWO ways - directly (standalone Sticker.exe built by
            // desktop-sticker/build.ps1) and by dispatch from the merged exe (launcher/Program.cs calls
            // TieTieSticker.Program.Main after seeing --sticker). It must therefore be internal, and it
            // must never return into launcher code: every normal path ends in Environment.Exit.
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (string.IsNullOrEmpty(exeDir)) exeDir = Environment.CurrentDirectory;

            Log.Init(exeDir);
            Log.Line("=== TieTieSticker S1 starting ===");
            Log.Line("exePath=" + Application.ExecutablePath);
            Log.Line("exeDir=" + exeDir);
            Log.Line("args=[" + string.Join(" ", args) + "]");
            Log.Line("processId=" + Process.GetCurrentProcess().Id + " dpiAwareness=unaware(no manifest)");
            Log.Line("logFile=" + Log.Path);

            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log.Line("UNHANDLED: " + (e.ExceptionObject == null ? "(null)" : e.ExceptionObject.ToString()));
            };
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                Log.Exception("Application.ThreadException", e.Exception);
            };

            Options opt = null;
            try
            {
                opt = Options.Parse(args);
            }
            catch (Exception ex)
            {
                Log.Exception("Options.Parse", ex);
                opt = new Options();
            }

            if (opt.SelfTest) { Environment.Exit(SelfTest.Run(exeDir, opt) ? 0 : 1); }

            // ---- single instance mutex (P6, spec 8.6) ----
            bool createdNew;
            try
            {
                _instanceMutex = new Mutex(true, MutexName, out createdNew);
            }
            catch (Exception ex)
            {
                Log.Exception("Mutex", ex);
                createdNew = true;
            }
            if (!createdNew)
            {
                // spec v11 3.10 (captain ruling): the second instance must NOT exit silently. The exe is
                // /target:winexe (no console) and has no tray, so a log line is invisible to the user -
                // without this box, launching the exe twice looks like "nothing happened at all".
                Log.Line("another instance already owns " + MutexName
                    + " -> second instance places NO card; showing a visible notice, then exits");
                try
                {
                    MessageBox.Show(UiStrings.AlreadyRunningBody, UiStrings.AlreadyRunningTitle,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    Log.Exception("second-instance notice", ex);
                }
                Environment.Exit(0);
            }

            string appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TieTieSticker");
            string[] statePaths = PathPick.CandidatePaths(exeDir, appDataDir);
            Log.Line("state candidate paths: " + string.Join(" | ", statePaths));

            // ---- notes.json (READ ONLY) ----
            string notesPath = NotesStore.ResolvePath(exeDir, opt.NotesPath);
            Log.Line("FINAL notes.json path = " + (notesPath == null ? "(not found)" : notesPath));
            List<Note> notes = NotesStore.Load(notesPath);
            if (notesPath != null)
            {
                Log.Line("notes.json byte length at read time = " + SafeLength(notesPath));
            }

            // ---- offscreen render mode (self-verification aid; no windows, no state writes) ----
            if (opt.RenderSamples)
            {
                Environment.Exit(RenderMode.RunSamples(opt) ? 0 : 1);
            }
            if (!string.IsNullOrEmpty(opt.RenderPath))
            {
                Environment.Exit(RenderMode.Run(notes, opt) ? 0 : 1);
            }

            // ---- headless checklist write (S2 self-verification aid; same write path as the click) ----
            if (!string.IsNullOrEmpty(opt.ToggleCheckNoteId))
            {
                Environment.Exit(RunToggleCheck(notes, notesPath, exeDir, opt));
            }

            if (notes.Count == 0)
            {
                Log.Line("WARNING: 0 usable notes -> 0 cards will be shown");
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            StickerManager mgr = new StickerManager(notes, statePaths, exeDir, opt.VerboseHitTest, notesPath, exeDir, false);

            // ---- v13: desktop bridge (web button -> native card) + system tray ----
            // The bridge needs the directory that holds notes.json (data\bridge), because the launcher
            // owns that data directory and this exe lives in .tools\sticker_build.
            DesktopBridge bridge = null;
            TrayIcon tray = null;
            string bridgeDir = BridgeProtocol.DirForNotesPath(notesPath);
            if (bridgeDir != null)
            {
                bridge = new DesktopBridge(mgr, bridgeDir);
                try
                {
                    tray = new TrayIcon(mgr, delegate { mgr.ExitAll("tray-exit"); });
                }
                catch (Exception ex)
                {
                    Log.Exception("tray init", ex);
                    tray = null;
                }
                mgr.AttachBridgePublisher(delegate(string reason)
                {
                    bridge.Publish(reason);
                    if (tray != null) tray.Refresh();
                });
                Log.Line("bridge ENABLED: dir=" + bridgeDir + " tray=" + (tray != null));
            }
            else
            {
                Log.Line("bridge DISABLED: notes.json not found -> no data directory to relay through (and no tray)");
            }

            Application.ApplicationExit += delegate
            {
                Log.Line("ApplicationExit event -> final state write");
                mgr.Persist("application-exit");
                if (bridge != null) bridge.Stop();
                if (tray != null) tray.Dispose();
                Log.Line("=== TieTieSticker S1 stopped ===");
            };

            try
            {
                // v17: BEFORE Start() decides between "restore / place everything" and "place exactly
                // what was asked", tell it whether the user explicitly clicked 「贴到桌面」 for named
                // notes (the launcher appends the request before it starts this process).
                if (bridge != null) mgr.SetPendingBridgePlaces(bridge.PendingPlaceCount());
                mgr.Start(opt.NoteIds);
                // Consume pending requests BEFORE the empty-set exit test: a button pressed while this
                // program was closed must still be able to place the very first card.
                if (bridge != null) bridge.Start();
                if (mgr.CardCount == 0)
                {
                    Log.Line("no card to show -> exiting cleanly (no listener, no lingering tray icon)");
                    if (bridge != null) bridge.Stop();
                    if (tray != null) tray.Dispose();
                    Environment.Exit(0);
                }
                Application.Run();
            }
            catch (Exception ex)
            {
                Log.Exception("Application.Run", ex);
                Environment.Exit(2);
            }
        }

        private static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return -1; }
        }

        /// <summary>
        /// S2 headless write. Exit codes are the contract the verification script reads:
        ///   0 = Written, 10 = Unchanged, 11 = Conflict, 12 = NoteGone, 13 = ItemGone, 14 = IoError.
        /// The stdout line "TOGGLE outcome=..." is the human-readable half of the same contract.
        /// </summary>
        private static int RunToggleCheck(List<Note> notes, string notesPath, string exeDir, Options opt)
        {
            Note target = null;
            for (int i = 0; i < notes.Count; i++)
            {
                if (string.Equals(notes[i].Id, opt.ToggleCheckNoteId, StringComparison.Ordinal)) { target = notes[i]; break; }
            }
            if (target == null)
            {
                Console.WriteLine("TOGGLE outcome=NoteGone detail=id not present in the loaded notes.json");
                Log.Line("--toggle-check: note id not present in the loaded notes.json");
                return 12;
            }
            if (opt.ToggleCheckIndex < 0 || target.Checklist == null || opt.ToggleCheckIndex >= target.Checklist.Count)
            {
                Console.WriteLine("TOGGLE outcome=ItemGone detail=index " + opt.ToggleCheckIndex + " out of range count="
                    + (target.Checklist == null ? 0 : target.Checklist.Count));
                Log.Line("--toggle-check: index out of range");
                return 13;
            }

            CheckItem item = target.Checklist[opt.ToggleCheckIndex];
            ToggleResult r = NotesWriter.ToggleChecklist(notesPath, exeDir, target.Id, opt.ToggleCheckIndex,
                item.Text, item.Done, target.RawJson);
            Console.WriteLine("TOGGLE outcome=" + r.Outcome + " desired=" + r.DesiredDone + " was=" + r.WasDone
                + " sha256=" + (r.NewFileSha256 ?? "(none)") + " detail=" + r.Detail);
            Log.Line("--toggle-check finished: " + r.Describe());

            switch (r.Outcome)
            {
                case ToggleOutcome.Written: return 0;
                case ToggleOutcome.Unchanged: return 10;
                case ToggleOutcome.Conflict: return 11;
                case ToggleOutcome.NoteGone: return 12;
                case ToggleOutcome.ItemGone: return 13;
                default: return 14;
            }
        }
    }

    /// <summary>
    /// Offscreen PNG writer for self-verification. Uses the SAME CardRenderer as the live window.
    /// Cards are drawn at exactly 1 logical pixel per unit (the live geometry) and the resulting
    /// bitmap is then upscaled by an integer factor with nearest-neighbour, because GDI text
    /// (TextRenderer) does NOT honour a GDI+ world transform - drawing at 2x directly would keep
    /// the glyphs at 1x and invent a mismatch that the real window does not have.
    /// </summary>
    internal static class RenderMode
    {
        /// <summary>Synthetic cards that exercise the paths the local notes.json does not contain
        /// (due badge in all three states, pinned flag, done title, done checklist row, overflow clipping).</summary>
        public static bool RunSamples(Options opt)
        {
            try
            {
                string baseDir = string.IsNullOrEmpty(opt.RenderPath) ? Environment.CurrentDirectory : Path.GetFullPath(opt.RenderPath);
                if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);

                List<KeyValuePair<string, Note>> cases = new List<KeyValuePair<string, Note>>();

                Note dueToday = new Note();
                dueToday.Id = "sample-due-today"; dueToday.Title = "Sample due today"; dueToday.Color = "pink";
                dueToday.Content = "line one\nline two wraps here to show the body block";
                dueToday.DueAt = Typo.TodayUtc();
                dueToday.Tags.Add("work"); dueToday.Tags.Add("longer-tag");
                dueToday.Checklist.Add(new CheckItem()); dueToday.Checklist[0].Text = "done item"; dueToday.Checklist[0].Done = true;
                dueToday.Checklist.Add(new CheckItem()); dueToday.Checklist[1].Text = "open item";
                cases.Add(new KeyValuePair<string, Note>("sample-1-due-today", dueToday));

                Note overdue = new Note();
                overdue.Id = "sample-overdue"; overdue.Title = "Sample overdue + pinned + done";
                overdue.Color = "blue"; overdue.Done = true; overdue.Pinned = true;
                overdue.Content = "this note is marked done, so only the title gets a strikethrough";
                overdue.DueAt = DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-dd");
                cases.Add(new KeyValuePair<string, Note>("sample-2-overdue-pinned-done", overdue));

                Note plain = new Note();
                plain.Id = "sample-plain"; plain.Title = "Plain due date";
                plain.Color = "green"; plain.DueAt = DateTime.UtcNow.AddDays(9).ToString("yyyy-MM-dd");
                plain.Content = "future due date shows the raw YYYY-MM-DD text on a white pill";
                cases.Add(new KeyValuePair<string, Note>("sample-3-plain-due", plain));

                Note overflow = new Note();
                overflow.Id = "sample-overflow"; overflow.Title = "Overflow: content is clipped from the bottom";
                overflow.Color = "orange";
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append("body line ").Append(i + 1).Append('\n');
                overflow.Content = sb.ToString();
                for (int i = 0; i < 6; i++)
                {
                    CheckItem ci = new CheckItem();
                    ci.Text = "checklist item " + (i + 1);
                    overflow.Checklist.Add(ci);
                }
                cases.Add(new KeyValuePair<string, Note>("sample-4-overflow", overflow));

                Note many = new Note();
                many.Id = "sample-tags"; many.Title = "Tag chips wrap";
                many.Color = "purple";
                many.Tags.Add("alpha"); many.Tags.Add("beta"); many.Tags.Add("gamma");
                many.Tags.Add("delta"); many.Tags.Add("epsilon"); many.Tags.Add("zeta");
                many.Tags.Add("eta"); many.Tags.Add("theta");
                cases.Add(new KeyValuePair<string, Note>("sample-5-tag-wrap", many));

                int zoom = 3;
                for (int i = 0; i < cases.Count; i++)
                {
                    Note note = cases[i].Value;
                    int w = Typo.DEFAULT_W;
                    int h = Typo.DEFAULT_H;
                    using (Bitmap card = new Bitmap(w, h))
                    {
                        using (Graphics g = Graphics.FromImage(card))
                        {
                            g.Clear(Color.FromArgb(246, 244, 239));
                            CardRenderer.Render(g, note, w, h, opt.RenderFloating);
                        }
                        using (Bitmap big = new Bitmap(w * zoom, h * zoom))
                        {
                            using (Graphics g2 = Graphics.FromImage(big))
                            {
                                g2.InterpolationMode = InterpolationMode.NearestNeighbor;
                                g2.PixelOffsetMode = PixelOffsetMode.Half;
                                g2.DrawImage(card, new Rectangle(0, 0, w * zoom, h * zoom));
                            }
                            string outPath = Path.Combine(baseDir, cases[i].Key + ".png");
                            big.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                            Log.Line("render sample " + cases[i].Key + " size=" + w + "x" + h + " naturalH=" + Typo.MeasureNaturalHeight(note, w) + " -> " + outPath);
                            Console.WriteLine("sample OK -> " + outPath);
                        }
                    }
                }

                // same card at the minimum size, to prove the pin stays reachable
                Note mini = cases[0].Value;
                int mw = Typo.MIN_W;
                int mh = Typo.MIN_H;
                using (Bitmap card = new Bitmap(mw, mh))
                {
                    using (Graphics g = Graphics.FromImage(card))
                    {
                        g.Clear(Color.FromArgb(246, 244, 239));
                        CardRenderer.Render(g, mini, mw, mh, opt.RenderFloating);
                    }
                    using (Bitmap big = new Bitmap(mw * zoom, mh * zoom))
                    {
                        using (Graphics g2 = Graphics.FromImage(big))
                        {
                            g2.InterpolationMode = InterpolationMode.NearestNeighbor;
                            g2.PixelOffsetMode = PixelOffsetMode.Half;
                            g2.DrawImage(card, new Rectangle(0, 0, mw * zoom, mh * zoom));
                        }
                        string outPath = Path.Combine(baseDir, "sample-6-minsize.png");
                        big.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                        Console.WriteLine("sample OK -> " + outPath);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("RenderMode.RunSamples", ex);
                Console.WriteLine("render samples FAILED: " + ex.Message);
                return false;
            }
        }

        public static bool Run(List<Note> notes, Options opt)
        {            try
            {
                List<Note> chosen = new List<Note>();
                if (opt.NoteIds.Count == 0)
                {
                    for (int i = 0; i < notes.Count; i++)
                    {
                        if (!notes[i].Archived) chosen.Add(notes[i]);
                    }
                }
                else
                {
                    for (int i = 0; i < opt.NoteIds.Count; i++)
                    {
                        for (int j = 0; j < notes.Count; j++)
                        {
                            if (string.Equals(notes[j].Id, opt.NoteIds[i], StringComparison.Ordinal)) { chosen.Add(notes[j]); break; }
                        }
                    }
                }
                if (chosen.Count == 0)
                {
                    Log.Line("render: no matching note -> nothing written");
                    return false;
                }

                int cardW = Typo.DEFAULT_W;
                int cardH = Typo.DEFAULT_H;
                int zoom = 3;
                string basePath = Path.GetFullPath(opt.RenderPath);
                string dir = Path.GetDirectoryName(basePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string stem = Path.GetFileNameWithoutExtension(basePath);

                for (int i = 0; i < chosen.Count; i++)
                {
                    using (Bitmap card = new Bitmap(cardW, cardH))
                    {
                        using (Graphics g = Graphics.FromImage(card))
                        {
                            g.Clear(Color.FromArgb(246, 244, 239)); // --bg, so the rounded corners show
                            CardRenderer.Render(g, chosen[i], cardW, cardH, opt.RenderFloating);
                        }
                        using (Bitmap big = new Bitmap(cardW * zoom, cardH * zoom))
                        {
                            using (Graphics g2 = Graphics.FromImage(big))
                            {
                                g2.InterpolationMode = InterpolationMode.NearestNeighbor;
                                g2.PixelOffsetMode = PixelOffsetMode.Half;
                                g2.DrawImage(card, new Rectangle(0, 0, cardW * zoom, cardH * zoom));
                            }
                            string outPath = Path.Combine(dir ?? ".", stem + "-" + (i + 1) + ".png");
                            big.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                            Log.Line("render: card " + (i + 1) + "/" + chosen.Count + " noteId=" + chosen[i].Id
                                + " -> " + outPath + " (" + (cardW * zoom) + "x" + (cardH * zoom) + ", naturalHeight@260=" + Typo.MeasureNaturalHeight(chosen[i], cardW) + ")");
                            Console.WriteLine("render OK -> " + outPath);
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("RenderMode.Run", ex);
                Console.WriteLine("render FAILED: " + ex.Message);
                return false;
            }
        }
    }
}

// S1 sticker: the single decision point for "which cards exist / which one floats / when to persist".
// Pure ASCII source.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TieTieSticker
{
    internal sealed class StickerManager
    {
        private readonly List<Note> _notes;
        private readonly Dictionary<string, Note> _noteById;
        private readonly string[] _statePaths;
        private readonly string _exeDir;
        private readonly bool _verboseHitTest;
        private readonly string _notesPath;
        private readonly string _notesBackupDir;

        /// <summary>v13 self-test affordance: run the REAL placement bookkeeping without creating a
        /// window (the self-test is forbidden from showing a card). Only SelfTest passes true.</summary>
        private readonly bool _headless;

        /// <summary>v13: set by DesktopBridge - called after every successful state write so the
        /// published bridge view can never drift from the real placed set.</summary>
        private Action<string> _bridgePublisher;

        /// <summary>v13: last request seq this process acknowledged (echoed into the published view).</summary>
        private long _bridgeAckSeq;

        /// <summary>v17: unacknowledged bridge "place" requests seen at start-up (0 = nobody clicked).</summary>
        private int _pendingBridgePlaces;

        /// <summary>S2: SHA-256 of notes.json as this process last read it (audit trail only - the
        /// write path re-reads the file and compares per-note JSON, never this digest).</summary>
        private string _notesSha256;

        private readonly List<string> _order = new List<string>();
        private readonly Dictionary<string, StickerForm> _forms = new Dictionary<string, StickerForm>(StringComparer.Ordinal);
        private readonly List<StickerRecord> _records = new List<StickerRecord>();
        private readonly StickerState _state = new StickerState();

        private string _topMostNoteId;
        private int _cascadeIndex;
        private bool _exiting;
        private bool _suppressPersist;

        public bool IsExiting { get { return _exiting; } }

        /// <summary>
        /// Number of placed cards. In the product this is the live window count; in --selftest
        /// (headless, _headless == true) there are no windows, so the placed set is the authority.
        /// Both collections are appended together in CreateCard and removed together in
        /// OnCardClosing, so the two definitions agree by construction in the real path.
        /// </summary>
        public int CardCount { get { return _headless ? _order.Count : _forms.Count; } }
        public IList<string> PlacedNoteIds { get { return _order; } }
        public IList<StickerRecord> Records { get { return _records; } }

        public StickerManager(List<Note> notes, string[] statePaths, string exeDir, bool verboseHitTest,
            string notesPath, string notesBackupDir, bool headless)
        {
            _notes = notes;
            _statePaths = statePaths;
            _exeDir = exeDir;
            _verboseHitTest = verboseHitTest;
            _notesPath = notesPath;
            _notesBackupDir = string.IsNullOrEmpty(notesBackupDir) ? exeDir : notesBackupDir;
            _headless = headless;
            _notesSha256 = NotesStore.LastLoadedSha256;
            _noteById = new Dictionary<string, Note>(StringComparer.Ordinal);
            for (int i = 0; i < notes.Count; i++) _noteById[notes[i].Id] = notes[i];
        }

        /// <summary>v13: DesktopBridge registers its Publish here; Persist() calls it after every write.</summary>
        public void AttachBridgePublisher(Action<string> publisher)
        {
            _bridgePublisher = publisher;
        }

        /// <summary>v13: the bridge tells us which request seq it has consumed (goes into the view).</summary>
        public void SetBridgeAck(long seq)
        {
            _bridgeAckSeq = seq;
        }

        public long BridgeAckSeq { get { return _bridgeAckSeq; } }

        /// <summary>v17: how many unacknowledged bridge "place" requests existed when this process
        /// started (see DesktopBridge.PendingPlaceCount). Must be set BEFORE Start() so the
        /// place-everything fallback can yield to the notes the user actually clicked.</summary>
        public void SetPendingBridgePlaces(int count)
        {
            _pendingBridgePlaces = count < 0 ? 0 : count;
        }

        public void LogHitTest(string msg)
        {
            if (_verboseHitTest) Log.Line("hit: " + msg);
        }

        /// <summary>Spec 8.1 (v11): primary = exeDir, fallback = %APPDATA%\TieTieSticker (write-only).</summary>
        public string StatePathForLog
        {
            get { return string.Join(" | ", _statePaths); }
        }

        /// <summary>
        /// Spec v11 4.2 row 4: with an EMPTY placed set, an explicit --note <id> suppresses the
        /// place-everything fallback (otherwise --note idempotence checks get polluted by the other
        /// cards). Pure predicate so --selftest can assert both directions of the rule.
        /// </summary>
        public static bool SuppressPlaceAllForCliNote(bool firstRunPlacement, int cliNoteCount)
        {
            return firstRunPlacement && cliNoteCount > 0;
        }

        /// <summary>
        /// v17 (user 2026-09-21, report: "当桌面没有贴纸时,点击贴到桌面,会将所有贴纸都贴到桌面"):
        /// with an EMPTY placed set the sticker falls back to placing EVERY non-archived note (v11 P1).
        /// That fallback must yield to an EXPLICIT target - the --note ids of v11 4.2 row 4, and now also
        /// unacknowledged bridge "place" requests (i.e. the user just clicked 「贴到桌面」 for a named
        /// note). Pure predicate so --selftest can assert every direction of the rule.
        /// </summary>
        public static bool SuppressPlaceAllForExplicitPlacements(bool firstRunPlacement, int cliNoteCount, int pendingBridgePlaces)
        {
            return firstRunPlacement && (cliNoteCount > 0 || pendingBridgePlaces > 0);
        }

        /// <summary>
        /// v21 (user report 2026-09-22: with auto-start switched on, every sign-in dumped ALL notes onto
        /// the desk in the default cascade instead of restoring the previous arrangement). Root cause:
        /// v11 turned an EMPTY placed set into "place every non-archived note", and that fallback used to
        /// run only when the user opened the app by hand - the v20 auto-start made it run on every sign-in.
        /// v21 keeps the arrangement instead: closing a card REMEMBERS its geometry (record.Closed = true)
        /// rather than deleting the record, so
        ///   * empty desk + remembered cards -> re-open exactly those cards at their saved positions;
        ///   * no records at all (true first run / discarded state) -> the old place-all fallback.
        /// Explicit targets still win (v11 4.2 row 4 + v17): if the user just clicked the web button for
        /// one note, or passed --note, the remembered cards stay closed. Pure predicate so --selftest can
        /// assert every direction of the rule.
        /// </summary>
        public static bool ShouldReopenClosedSet(int openCount, int closedCount, int cliNoteCount, int pendingBridgePlaces)
        {
            return openCount == 0 && closedCount > 0 && cliNoteCount == 0 && pendingBridgePlaces == 0;
        }

        public void Start(List<string> cliNoteIds)
        {
            Log.Line("=== sticker manager start: notes=" + _notes.Count + " watchState=" + StatePathForLog + " ===");

            // --- load state (PRIMARY ONLY: spec v11.2 3.12 - %APPDATA% is written but never read) ---
            StickerState loaded = StickerState.Load(_statePaths[0]);
            _state.FirstRun = loaded.FirstRun;
            _state.Corrupt = loaded.Corrupt;
            _records.Clear();
            _records.AddRange(loaded.Records);

            // --- spec 10.1 step 1: drop records whose note no longer exists ---
            int before = _records.Count;
            for (int i = _records.Count - 1; i >= 0; i--)
            {
                if (!_noteById.ContainsKey(_records[i].NoteId))
                {
                    Log.Line("state: noteId no longer present in notes.json, dropping from state: " + _records[i].NoteId);
                    _records.RemoveAt(i);
                }
            }
            if (before != _records.Count) MarkDirty();

            // --- v21: an EMPTY DESK with REMEMBERED cards is not a first run ---
            // Closing a card keeps its record (Closed=true) instead of deleting it, so the desk can be
            // empty while the arrangement is still known. Re-open it as it was; the place-every-note
            // fallback (P1) is reserved for "nothing was ever remembered".
            int cliCount = cliNoteIds == null ? 0 : cliNoteIds.Count;
            int closedCount = CountClosedRecords();
            if (ShouldReopenClosedSet(_records.Count - closedCount, closedCount, cliCount, _pendingBridgePlaces))
            {
                int reopened = ReopenClosedRecords();
                Log.Line("v21 reopen: empty desk but " + reopened + " card(s) remembered -> restoring the saved arrangement (NOT the place-all fallback)");
            }
            else if (_records.Count > 0 && closedCount == _records.Count)
            {
                Log.Line("v21 reopen suppressed: explicit target (--note ids=" + cliCount + ", pending bridge place requests="
                    + _pendingBridgePlaces + ") with an empty desk -> the " + closedCount + " remembered card(s) stay closed");
            }

            // --- spec 7.3.2: recovery convergence (at most ONE topMost) ---
            ConvergeTopMost();

            // --- P1: first run places every non-archived note (v21: only when NOTHING was ever remembered) ---
            int placedNew = 0;
            bool firstRunPlacement = _state.FirstRun || _records.Count == 0;
            bool cliExplicit = cliCount > 0;
            // v17: an explicit bridge click counts as an explicit target too (see the predicate).
            bool suppressPlaceAll = SuppressPlaceAllForExplicitPlacements(
                firstRunPlacement, cliExplicit ? cliNoteIds.Count : 0, _pendingBridgePlaces);
            if (firstRunPlacement)
            {
                if (suppressPlaceAll)
                {
                    // spec v11 4.2 row 4 + v17: an explicit target wins over the place-all fallback,
                    // otherwise the idempotence check of --note gets polluted by the other cards, and a
                    // single click on 「贴到桌面」 would dump every note onto the desktop.
                    Log.Line("first-run place-all suppressed: explicit target (--note ids="
                        + (cliExplicit ? cliNoteIds.Count : 0) + ", pending bridge place requests=" + _pendingBridgePlaces
                        + ") with an empty placed set -> placing ONLY the explicitly named notes");
                }
                else
                {
                    string reason = _state.FirstRun ? "state file absent" : (_state.Corrupt ? "state discarded (schema/JSON)" : "state had no usable records");
                    Log.Line("first-run placement (" + reason + "): placing ALL !archived notes (P1)");
                    for (int i = 0; i < _notes.Count; i++)
                    {
                        Note n = _notes[i];
                        if (n.Archived) { Log.Line("first-run: skip archived note " + n.Id); continue; }
                        if (AddRecord(n.Id, null)) placedNew++;
                    }
                }
            }

            // --- spec 10.1 step 3: --note <id> idempotent append ---
            if (cliNoteIds != null)
            {
                for (int i = 0; i < cliNoteIds.Count; i++)
                {
                    string id = cliNoteIds[i];
                    if (!_noteById.ContainsKey(id))
                    {
                        Log.Line("--note " + id + ": not found in notes.json -> ignored");
                        continue;
                    }
                    StickerRecord known = FindRecord(id);
                    if (known != null && !known.Closed)
                    {
                        Log.Line("--note " + id + ": already in the placed set -> idempotent no-op (no duplicate card)");
                        continue;
                    }
                    if (known != null)
                    {
                        // v21: the card was closed but remembered -> bring it back where it was
                        known.Closed = false;
                        Log.Line("--note " + id + ": was closed but remembered -> re-opened at its stored position " + known.X + "," + known.Y);
                        continue;
                    }
                    if (AddRecord(id, null)) placedNew++;
                }
            }

            // --- create one window per record, honouring the state's array order ---
            List<StickerRecord> snapshot = new List<StickerRecord>(_records);
            int remembered = 0;
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (snapshot[i].Closed) { remembered++; continue; }   // v21: remembered, deliberately not on screen
                CreateCard(snapshot[i]);
            }
            if (remembered > 0) Log.Line("v21: " + remembered + " closed-but-remembered record(s) kept out of the placed set");

            Log.Line("placed " + _forms.Count + " card(s) (" + placedNew + " newly added this session); topMost=" + (_topMostNoteId == null ? "(none)" : _topMostNoteId));

            if (_forms.Count == 0)
            {
                Log.Line("NO CARD PLACED. hint: data/notes.json has no !archived note, or every note is already closed in state.");
            }

            Persist("startup");
        }

        // ------------------------------------------------------------------

        private bool AddRecord(string noteId, Rectangle? bounds)
        {
            if (FindRecord(noteId) != null) return false;
            StickerRecord r = new StickerRecord();
            r.NoteId = noteId;
            if (bounds.HasValue)
            {
                r.X = bounds.Value.X; r.Y = bounds.Value.Y;
                r.W = bounds.Value.Width; r.H = bounds.Value.Height;
            }
            else
            {
                r.X = int.MinValue; r.Y = int.MinValue;   // resolved at card creation (cascade)
                r.W = Typo.DEFAULT_W;
                r.H = Typo.DEFAULT_H;
            }
            _records.Add(r);
            MarkDirty();
            return true;
        }

        private StickerRecord FindRecord(string noteId)
        {
            for (int i = 0; i < _records.Count; i++)
            {
                if (string.Equals(_records[i].NoteId, noteId, StringComparison.Ordinal)) return _records[i];
            }
            return null;
        }

        /// <summary>v21: closing a card keeps its record (geometry + monitor bounds) and only marks it
        /// closed, so an emptied desk can later be restored with the very arrangement the user had.</summary>
        private StickerRecord CloseRecord(string noteId)
        {
            StickerRecord r = FindRecord(noteId);
            if (r == null) return null;
            r.Closed = true;
            r.TopMost = false;
            return r;
        }

        private int CountClosedRecords()
        {
            int n = 0;
            for (int i = 0; i < _records.Count; i++) if (_records[i].Closed) n++;
            return n;
        }

        /// <summary>v21: turns every remembered (closed) record back into a placed one. Archived notes
        /// are left closed (the same rule the first-run place-all uses). Returns how many were re-opened.</summary>
        private int ReopenClosedRecords()
        {
            int n = 0;
            for (int i = 0; i < _records.Count; i++)
            {
                if (!_records[i].Closed) continue;
                Note n2;
                if (_noteById.TryGetValue(_records[i].NoteId, out n2) && n2.Archived)
                {
                    Log.Line("v21 reopen: note is archived, left closed: " + _records[i].NoteId);
                    continue;
                }
                _records[i].Closed = false;
                n++;
            }
            return n;
        }

        private void ConvergeTopMost()
        {
            string winner = null;
            int flagged = 0;
            for (int i = 0; i < _records.Count; i++)
            {
                if (_records[i].Closed) { _records[i].TopMost = false; continue; }   // v21
                if (!_records[i].TopMost) continue;
                flagged++;
                if (winner == null) winner = _records[i].NoteId;
            }
            _topMostNoteId = winner;
            for (int i = 0; i < _records.Count; i++)
            {
                bool should = !_records[i].Closed && string.Equals(_records[i].NoteId, winner, StringComparison.Ordinal);
                if (_records[i].TopMost != should) _records[i].TopMost = should;
            }
            if (flagged > 1)
            {
                Log.Line("recovery convergence: " + flagged + " records had topMost=true -> keeping only the first in array order: " + winner);
                MarkDirty();
            }
            else if (flagged == 1)
            {
                Log.Line("recovery: single topMost record = " + winner);
            }
        }

        private void MarkDirty()
        {
            // records list changed structurally; state is always serialised from _records
        }

        // ------------------------------------------------------------------
        // card creation / removal
        // ------------------------------------------------------------------

        private void CreateCard(StickerRecord record)
        {
            Note note;
            if (!_noteById.TryGetValue(record.NoteId, out note))
            {
                Log.Line("CreateCard: note " + record.NoteId + " missing in notes.json -> skipped");
                return;
            }

            Rectangle bounds;
            if (record.X == int.MinValue || record.Y == int.MinValue)
            {
                bounds = CascadeBounds();
                Log.Line("card " + record.NoteId + " had no stored position -> cascade " + StickerForm.BoundsToString(bounds));
            }
            else
            {
                Rectangle want = new Rectangle(record.X, record.Y,
                    ScreenClamp.ClampInt(record.W, Typo.MIN_W, int.MaxValue),
                    ScreenClamp.ClampInt(record.H, Typo.MIN_H, int.MaxValue));
                bounds = want;
                bool moved = ScreenClamp.ClampToPrimary(ref bounds);
                if (moved) Log.Line("restore clamp noteId=" + record.NoteId + " " + StickerForm.BoundsToString(want) + " -> " + StickerForm.BoundsToString(bounds));
            }

            bool topMost = string.Equals(_topMostNoteId, record.NoteId, StringComparison.Ordinal);
            if (_headless)
            {
                // --selftest only: record the placement (the placed set = _order) without a window.
                // CardCount uses _order in headless mode, so every downstream decision (the empty-set
                // exit rule, the published view, the tray list) is still driven by real product logic.
                _order.Add(record.NoteId);
            }
            else
            {
                StickerForm form = new StickerForm(this, note, bounds, topMost);
                _forms[record.NoteId] = form;
                _order.Add(record.NoteId);

                try
                {
                    form.Show();
                }
                catch (Exception ex)
                {
                    Log.Exception("form.Show " + record.NoteId, ex);
                }
            }

            // write resolved geometry back into the record
            record.X = bounds.X; record.Y = bounds.Y; record.W = bounds.Width; record.H = bounds.Height;
            record.TopMost = topMost;
            record.Monitor = ScreenClamp.ScreenOf(bounds);
            Rectangle wa = Screen.FromRectangle(bounds).WorkingArea;
            record.HasMonitorBounds = true;
            record.MonitorX = wa.X; record.MonitorY = wa.Y; record.MonitorW = wa.Width; record.MonitorH = wa.Height;
        }

        private Rectangle CascadeBounds()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int idx = _cascadeIndex++;
            int slot = idx % 8;
            int w = Typo.DEFAULT_W;
            int h = Typo.DEFAULT_H;
            int x = wa.X + Typo.CASCADE_OFFSET + slot * Typo.CASCADE_STEP;
            int y = wa.Y + Typo.CASCADE_OFFSET + slot * Typo.CASCADE_STEP;
            if (x + w > wa.Right || y + h > wa.Bottom)
            {
                x = wa.X + Typo.CASCADE_OFFSET;
                y = wa.Y + Typo.CASCADE_OFFSET;
            }
            return new Rectangle(x, y, w, h);
        }

        /// <summary>Called by StickerForm.OnFormClosing. Alt+F4 closes ONE card and removes it from state.</summary>
        public void OnCardClosing(StickerForm form, CloseReason reason)
        {
            if (_exiting)
            {
                Log.Line("card " + form.NoteId + " closing during app exit -> state untouched (cards come back on next start)");
                return;
            }
            Log.Line("card " + form.NoteId + " closed by user (reason=" + reason + ") -> geometry REMEMBERED (v21), immediate write");
            _forms.Remove(form.NoteId);
            _order.Remove(form.NoteId);
            CloseRecord(form.NoteId);
            if (string.Equals(_topMostNoteId, form.NoteId, StringComparison.Ordinal)) _topMostNoteId = null;
            MarkDirty();
            Persist("card-close");

            // spec v11 3.9: when the close makes the placed set empty, exit normally in the SAME close
            // flow - otherwise the process lingers with zero cards while still holding the single
            // instance mutex, and every later start silently places nothing (availability defect).
            // ORDER IS MANDATORY: marking the record closed (v21) + Persist("card-close") MUST already have happened;
            // ExitAll() sets _exiting=true, and that would divert this very close into the
            // "_exiting == true" branch above (state untouched) => "the card the user just closed
            // comes back on the next start". Assertion close-order-must-remove-record-first guards it.
            if (CardCount == 0)
            {
                Log.Line("last card closed -> exiting app so the single-instance mutex is released (spec v11 3.9)");
                ExitAll("last-card-closed");
            }
        }

        // ------------------------------------------------------------------
        // S2: checklist write-back - the ONE place in the product that writes notes.json
        // ------------------------------------------------------------------

        /// <summary>
        /// Called by StickerForm when a pending check is released inside the same item band.
        /// Spec v12 3.x: on Conflict / NoteGone / ItemGone NOTHING is written - the notes are re-read,
        /// every card is repainted from the fresh file, and the clicked card shows a transient notice
        /// asking for a second click (never a silent overwrite). On IoError the original bytes are
        /// already back on disk (NotesWriter rolls back and verifies before returning).
        /// </summary>
        public void ToggleChecklistItem(string noteId, int itemIndex)
        {
            Note note;
            if (!_noteById.TryGetValue(noteId, out note))
            {
                Log.Line("checklist toggle: note " + NotesWriter.Short(noteId) + " is not loaded -> ignored");
                return;
            }
            if (note.Checklist == null || itemIndex < 0 || itemIndex >= note.Checklist.Count)
            {
                Log.Line("checklist toggle: index " + itemIndex + " out of range for "
                    + NotesWriter.Short(noteId) + " -> ignored");
                return;
            }

            CheckItem item = note.Checklist[itemIndex];
            Log.Line("checklist toggle REQUEST noteId=" + NotesWriter.Short(noteId) + " idx=" + itemIndex
                + " text='" + item.Text + "' displayedDone=" + item.Done
                + " baselineFileSha256=" + (_notesSha256 ?? "(none)") + " path=" + (_notesPath ?? "(none)"));

            ToggleResult r = NotesWriter.ToggleChecklist(
                _notesPath, _notesBackupDir, noteId, itemIndex, item.Text, item.Done, note.RawJson);

            StickerForm form;
            _forms.TryGetValue(noteId, out form);

            switch (r.Outcome)
            {
                case ToggleOutcome.Written:
                    // the file is the truth now - take the value that was actually written
                    item.Done = r.DesiredDone;
                    note.RawJson = r.NewNoteJson;
                    _notesSha256 = r.NewFileSha256;
                    if (form != null) form.Invalidate();
                    Log.Line("checklist toggle WRITTEN noteId=" + NotesWriter.Short(noteId) + " idx=" + itemIndex + " " + r.Describe());
                    return;

                case ToggleOutcome.Unchanged:
                    // the file already holds exactly what the user asked for (usually the web version won the race)
                    item.Done = r.WasDone;
                    if (form != null) form.Invalidate();
                    Log.Line("checklist toggle NO-OP noteId=" + NotesWriter.Short(noteId) + " idx=" + itemIndex + " " + r.Describe());
                    return;

                case ToggleOutcome.NoteGone:
                    ReloadNotesFromDisk("toggle-note-gone");
                    if (form != null) form.ShowNotice(UiStrings.NoticeNoteGone, 3200);
                    return;

                case ToggleOutcome.Conflict:
                case ToggleOutcome.ItemGone:
                    ReloadNotesFromDisk("toggle-" + r.Outcome);
                    if (form != null) form.ShowNotice(UiStrings.NoticeConflict, 3200);
                    return;

                default:
                    if (form != null) form.ShowNotice(UiStrings.NoticeWriteFailed, 3600);
                    return;
            }
        }

        /// <summary>
        /// S2 3.4: re-read notes.json and swap the fresh Note objects into the live cards. Used after a
        /// refused write (so the user sees what is really on disk) and after the file changed under us.
        /// Cards keep their window geometry; only the content is replaced. A card whose note vanished is
        /// deliberately left on screen (never silently closed) - its next click reports NoteGone.
        /// </summary>
        public void ReloadNotesFromDisk(string reason)
        {
            try
            {
                if (string.IsNullOrEmpty(_notesPath)) return;
                List<Note> fresh = NotesStore.Load(_notesPath);
                _notesSha256 = NotesStore.LastLoadedSha256;
                _notes.Clear();
                _notes.AddRange(fresh);
                _noteById.Clear();
                for (int i = 0; i < fresh.Count; i++) _noteById[fresh[i].Id] = fresh[i];

                int updated = 0, orphaned = 0;
                List<string> ids = new List<string>(_forms.Keys);
                for (int i = 0; i < ids.Count; i++)
                {
                    Note n;
                    if (_noteById.TryGetValue(ids[i], out n)) { _forms[ids[i]].SetNote(n); updated++; }
                    else orphaned++;
                }
                Log.Line("notes RELOADED (" + reason + "): " + fresh.Count + " note(s) on disk, "
                    + updated + " card(s) refreshed, " + orphaned + " card(s) whose note is gone (left on screen), sha256="
                    + (_notesSha256 ?? "(none)"));
            }
            catch (Exception ex)
            {
                Log.Exception("ReloadNotesFromDisk(" + reason + ")", ex);
            }
        }

        // ------------------------------------------------------------------
        // v13: bridge (web button) - place / remove a note on the desktop
        // ------------------------------------------------------------------

        /// <summary>
        /// The single entry point the bridge uses for one request (spec v13 3.1). Keeps the same
        /// idempotence rule as --note and the same close order as the card's X button.
        /// </summary>
        public BridgeApplyOutcome ApplyBridgeRequest(bool place, string noteId)
        {
            if (string.IsNullOrEmpty(noteId) || !_noteById.ContainsKey(noteId))
            {
                Log.Line("bridge apply: note " + Short(noteId) + " is not in notes.json -> skipped (ack still advances)");
                return BridgeApplyOutcome.NoteMissing;
            }

            if (place)
            {
                StickerRecord memo = FindRecord(noteId);
                if (memo != null && !memo.Closed)
                {
                    Log.Line("bridge apply: " + Short(noteId) + " is already placed -> idempotent no-op");
                    RaiseCard(noteId);
                    return BridgeApplyOutcome.AlreadyPlaced;
                }
                if (memo != null)
                {
                    // v21: closed earlier, now clicked again -> put it back where it was (not a new slot)
                    memo.Closed = false;
                    CreateCard(memo);
                    Log.Line("bridge apply: RE-OPENED " + Short(noteId) + " at its remembered position " + memo.X + "," + memo.Y);
                    Persist("bridge-reopen");
                    return BridgeApplyOutcome.Placed;
                }
                if (!AddRecord(noteId, null))
                {
                    Log.Line("bridge apply: AddRecord refused for " + Short(noteId) + " -> no-op");
                    return BridgeApplyOutcome.AlreadyPlaced;
                }
                StickerRecord rec = FindRecord(noteId);
                if (rec != null) CreateCard(rec);
                Log.Line("bridge apply: PLACED " + Short(noteId) + " (cascade default position, spec v13 3.1)");
                Persist("bridge-place");
                return BridgeApplyOutcome.Placed;
            }

            // remove: with a live window, close it and let OnCardClosing own the record removal,
            // the persist and the "last card closes the app" rule (v11 semantics stay in ONE place).
            StickerForm form;
            if (_forms.TryGetValue(noteId, out form) && form != null)
            {
                Log.Line("bridge apply: closing card " + Short(noteId) + " through the normal X path");
                form.Close();
                return BridgeApplyOutcome.Removed;
            }

            // no window (headless self-test, or a record whose card never materialised): mirror the
            // same order the X path uses - drop the record, persist, and only then consider the exit.
            if (FindRecord(noteId) == null)
            {
                Log.Line("bridge apply: " + Short(noteId) + " is not placed -> remove is a no-op");
                return BridgeApplyOutcome.NotPlaced;
            }
            _order.Remove(noteId);
            CloseRecord(noteId);
            if (string.Equals(_topMostNoteId, noteId, StringComparison.Ordinal)) _topMostNoteId = null;
            MarkDirty();
            Persist("bridge-remove");
            Log.Line("bridge apply: REMOVED " + Short(noteId) + " (no live window)");
            if (!_headless && CardCount == 0)
            {
                Log.Line("bridge remove emptied the placed set -> exiting normally (spec v11 3.9)");
                ExitAll("bridge-remove-last");
            }
            return BridgeApplyOutcome.Removed;
        }

        /// <summary>Spec v13 3.4: bring a placed card to the front and flash it once. Deliberately does
        /// NOT touch the topMost arbitration - only the pin decides whether a card may cover other apps.</summary>
        public void RaiseCard(string noteId)
        {
            StickerForm form;
            if (!_forms.TryGetValue(noteId, out form) || form == null) return;
            try
            {
                form.Activate();
                form.BringToFront();
                form.Invalidate();
                Log.Line("bridge raise: card " + Short(noteId) + " brought to front (topMost untouched)");
            }
            catch (Exception ex)
            {
                Log.Exception("RaiseCard " + noteId, ex);
            }
        }

        /// <summary>Placed ids in state (records) order - the published view and the tray both use it.</summary>
        public List<string> PlacedNoteIdsOrdered()
        {
            List<string> ids = new List<string>();
            for (int i = 0; i < _records.Count; i++)
            {
                if (_records[i].Closed) continue;   // v21: remembered cards are not "placed"
                ids.Add(_records[i].NoteId);
            }
            return ids;
        }

        /// <summary>Display title of a note; an empty title becomes the "untitled" caption.</summary>
        public string TitleOf(string noteId)
        {
            Note n;
            if (_noteById.TryGetValue(noteId, out n) && !string.IsNullOrEmpty(n.Title)) return n.Title;
            return UiStrings.NoTitle;
        }

        /// <summary>Snapshot for the published view (live geometry when the window exists).</summary>
        public List<BridgePlacement> BridgePlacements()
        {
            List<BridgePlacement> rows = new List<BridgePlacement>();
            for (int i = 0; i < _records.Count; i++)
            {
                StickerRecord r = _records[i];
                if (r.Closed) continue;   // v21: the published view lists what is on the desk right now
                BridgePlacement p = new BridgePlacement();
                p.NoteId = r.NoteId;
                p.Title = TitleOf(r.NoteId);
                StickerForm f;
                if (_forms.TryGetValue(r.NoteId, out f) && f != null)
                {
                    Rectangle b = f.Bounds;
                    p.X = b.X; p.Y = b.Y; p.W = b.Width; p.H = b.Height;
                }
                else
                {
                    p.X = r.X; p.Y = r.Y; p.W = r.W; p.H = r.H;
                }
                p.TopMost = r.TopMost;
                rows.Add(p);
            }
            return rows;
        }

        private static string Short(string id)
        {
            if (string.IsNullOrEmpty(id)) return "(empty)";
            return id.Length <= 8 ? id : id.Substring(0, 8);
        }

        // ------------------------------------------------------------------
        // top-most arbitration (spec 7.3.2)
        // ------------------------------------------------------------------

        public void ToggleTopMost(string noteId)
        {
            bool currentlyOn = string.Equals(_topMostNoteId, noteId, StringComparison.Ordinal);
            SetTopMost(noteId, !currentlyOn);
        }

        public void SetTopMost(string noteId, bool on)
        {
            if (on)
            {
                if (_topMostNoteId != null && !string.Equals(_topMostNoteId, noteId, StringComparison.Ordinal))
                {
                    StickerForm old;
                    if (_forms.TryGetValue(_topMostNoteId, out old)) old.SetPinVisual(false);
                    Log.Line("pin sink: " + _topMostNoteId + " (only one card may float)");
                }
                _topMostNoteId = noteId;
            }
            else
            {
                if (string.Equals(_topMostNoteId, noteId, StringComparison.Ordinal)) _topMostNoteId = null;
            }

            // Clear EVERY stale flag - not just the arbiter's - so the invariant
            // "at most one record has topMost=true" cannot drift after any history.
            for (int i = 0; i < _records.Count; i++)
            {
                bool should = string.Equals(_records[i].NoteId, _topMostNoteId, StringComparison.Ordinal);
                if (_records[i].TopMost != should) _records[i].TopMost = should;
            }
            StickerForm target;
            if (_forms.TryGetValue(noteId, out target)) target.SetPinVisual(on);
            Log.Line("pin " + (on ? "UP" : "DOWN") + " noteId=" + noteId + " (topMostNow=" + (_topMostNoteId == null ? "(none)" : _topMostNoteId) + ") -> immediate write");
            Persist("pin-toggle");
        }

        // ------------------------------------------------------------------
        // geometry bookkeeping + persistence
        // ------------------------------------------------------------------

        public void SyncRecordGeometry(string noteId, Rectangle bounds)
        {
            StickerRecord r = FindRecord(noteId);
            if (r == null) return;
            r.X = bounds.X; r.Y = bounds.Y; r.W = bounds.Width; r.H = bounds.Height;
            r.Monitor = ScreenClamp.ScreenOf(bounds);
            Rectangle wa = Screen.FromRectangle(bounds).WorkingArea;
            r.HasMonitorBounds = true;
            r.MonitorX = wa.X; r.MonitorY = wa.Y; r.MonitorW = wa.Width; r.MonitorH = wa.Height;
        }

        /// <summary>Spec 8.3 write points only: drag/resize release, pin toggle, placement, card close, app exit.</summary>
        public void Persist(string reason)
        {
            if (_suppressPersist) return;
            try
            {
                // refresh geometry of every live card before serialising
                for (int i = 0; i < _records.Count; i++)
                {
                    StickerForm f;
                    if (_forms.TryGetValue(_records[i].NoteId, out f)) SyncRecordGeometry(_records[i].NoteId, f.Bounds);
                }
                _state.Records = _records;
                StickerState.SaveReport report = _state.Save(_statePaths);
                Log.Line("state written (" + reason + "): " + _records.Count + " record(s) -> " + report.Describe()
                    + " topMostFlags=" + TopMostFlags());

                // v13 3.2: the published bridge view follows the SAME write points as the state file,
                // so closing a card with X (or the web button) updates the web button within one poll.
                if (_bridgePublisher != null)
                {
                    try { _bridgePublisher(reason); }
                    catch (Exception ex) { Log.Exception("bridge publisher(" + reason + ")", ex); }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("Persist(" + reason + ")", ex);
            }
        }

        private string TopMostFlags()
        {
            List<string> parts = new List<string>();
            for (int i = 0; i < _records.Count; i++)
            {
                parts.Add((_records[i].TopMost ? "1" : "0") + ":" + _records[i].NoteId.Substring(0, Math.Min(8, _records[i].NoteId.Length)));
            }
            return "[" + string.Join(",", parts.ToArray()) + "] arbiter=" + (_topMostNoteId == null ? "(none)" : _topMostNoteId.Substring(0, Math.Min(8, _topMostNoteId.Length)));
        }

        public void ExitAll(string reason)
        {
            if (_exiting) return;
            _exiting = true;
            Log.Line("EXIT requested via " + reason + ": writing state, then closing " + _forms.Count + " card(s)");
            Persist("exit:" + reason);
            _suppressPersist = true;
            try
            {
                Application.Exit();
            }
            catch (Exception ex)
            {
                Log.Exception("ExitAll/Application.Exit", ex);
                Environment.Exit(0);
            }
        }
    }
}

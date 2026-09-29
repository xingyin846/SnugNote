// S2 sticker: the ONE and ONLY writer for data/notes.json.
//
// S1 shipped with a hard "notes.json is read-only" constraint. S2 (user ruling 2026-09-20,
// "S2 第一位 = 清单勾选回写") narrows that constraint to:
//     reads are still read-only; the ONLY write is an explicit click on a checklist box.
//
// Every write therefore has to survive a second writer (the web app served by 贴贴便签.exe
// rewrites the whole array on PATCH /api/notes). The rules implemented here:
//   1. re-read the file at write time and operate on the FRESH tree (never on a stale copy),
//      so a change made by the web app to ANOTHER note can never be lost;
//   2. refuse (Conflict) when THIS note is not byte-for-byte the one the card was rendered from
//      - the caller re-reads, repaints and asks the user to click again (never a silent overwrite);
//   3. refuse when the checklist item the user clicked is gone or its text changed (ItemGone);
//   4. write atomically (temp + File.Replace, falling back to in-place copy - the data file is
//      NEVER deleted, so there is no window in which a concurrent reader sees "no file");
//   5. verify the written file by re-parsing it and diffing it against the pre-write tree:
//      the ONLY differences allowed are the one flipped `done` and that note's `updatedAt`.
//      Anything else (a dropped field, a bad round-trip) rolls the original bytes back.
// Nothing in this file runs unless the user clicks a checkbox; simply showing a card writes nothing.
// Pure ASCII source (user-visible strings live in UiStrings.cs as hex escapes).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TieTieSticker
{
    internal enum ToggleOutcome
    {
        Written,     // the item was flipped; the file was replaced atomically and verified
        Unchanged,   // the file already held the value the user asked for -> nothing was written
        Conflict,    // this note changed since the card was rendered -> refuse (caller refreshes)
        NoteGone,    // the note is no longer in the file -> refuse (caller refreshes)
        ItemGone,    // the clicked checklist item is gone / the list was restructured -> refuse
        IoError      // read/parse/write/verify failure; the original bytes are restored
    }

    internal sealed class ToggleResult
    {
        public ToggleOutcome Outcome = ToggleOutcome.IoError;
        public string Detail = "";
        public bool DesiredDone;
        public bool WasDone;
        public string NewFileSha256;
        public string NewNoteJson;

        public string Describe()
        {
            return "outcome=" + Outcome + " desired=" + DesiredDone + " was=" + WasDone
                + " newFileSha256=" + (NewFileSha256 ?? "(none)") + " detail=" + Detail;
        }
    }

    internal static class NotesWriter
    {
        /// <summary>Temp file suffix used for the atomic replace (never left behind on success).</summary>
        public const string TmpSuffix = ".sticker-tmp";

        /// <summary>Rolling safety copy of the file as it was before this process' FIRST write.
        /// It lives next to the exe (NOT inside the user's data\ directory, which must keep exactly
        /// notes.json + notes.json.bak).</summary>
        public const string BackupFileName = "notes-backup-before-sticker-write.json";

        private static bool _backupWrittenThisProcess;

        // ------------------------------------------------------------------
        // the one public entry point
        // ------------------------------------------------------------------

        /// <summary>
        /// Flip <paramref name="itemIndex"/> of the checklist of note <paramref name="noteId"/>.
        /// <paramref name="renderedNoteJson"/> is the canonical JSON of the note as the card last
        /// rendered it; a mismatch means somebody else edited that note and the write is refused.
        /// </summary>
        public static ToggleResult ToggleChecklist(
            string path, string backupDir, string noteId, int itemIndex,
            string displayedItemText, bool displayedDone, string renderedNoteJson)
        {
            ToggleResult r = new ToggleResult();
            r.DesiredDone = !displayedDone;
            r.WasDone = displayedDone;

            if (string.IsNullOrEmpty(path))
            {
                r.Outcome = ToggleOutcome.IoError;
                r.Detail = "no notes.json path resolved";
                Log.Line("notes write REFUSED: " + r.Detail);
                return r;
            }

            // ---- 1. read the CURRENT file (shared, retried: the web app may hold it for a moment) ----
            string readErr;
            byte[] original = ReadAllBytesShared(path, out readErr);
            if (original == null)
            {
                r.Outcome = ToggleOutcome.IoError;
                r.Detail = "read failed: " + readErr;
                Log.Line("notes write REFUSED: " + r.Detail);
                return r;
            }

            JsonValue root;
            string parseErr;
            if (!TryParseArray(original, out root, out parseErr))
            {
                r.Outcome = ToggleOutcome.IoError;
                r.Detail = parseErr;
                Log.Line("notes write REFUSED: " + r.Detail);
                return r;
            }

            // ---- 2. locate the note in the FRESH tree ----
            int noteIndex = FindNoteIndex(root, noteId);
            if (noteIndex < 0)
            {
                r.Outcome = ToggleOutcome.NoteGone;
                r.Detail = "note " + Short(noteId) + " is not in " + path + " any more";
                Log.Line("notes write REFUSED (" + r.Outcome + "): " + r.Detail);
                return r;
            }
            JsonValue noteObj = root.Items[noteIndex];
            string freshNoteJson = Json.Write(noteObj);

            // ---- 3. conflict check against what the card rendered ----
            if (string.IsNullOrEmpty(renderedNoteJson))
            {
                r.Outcome = ToggleOutcome.Conflict;
                r.Detail = "no rendered baseline for note " + Short(noteId) + " (refusing to guess)";
                Log.Line("notes write REFUSED (" + r.Outcome + "): " + r.Detail);
                return r;
            }
            if (!string.Equals(renderedNoteJson, freshNoteJson, StringComparison.Ordinal))
            {
                r.Outcome = ToggleOutcome.Conflict;
                r.Detail = "note " + Short(noteId) + " changed after the card was rendered"
                    + " (rendered=" + Sha256Hex(Encoding.UTF8.GetBytes(renderedNoteJson)).Substring(0, 12)
                    + " fresh=" + Sha256Hex(Encoding.UTF8.GetBytes(freshNoteJson)).Substring(0, 12) + ")";
                Log.Line("notes write REFUSED (" + r.Outcome + "): " + r.Detail);
                return r;
            }

            // ---- 4. locate the checklist item ----
            JsonValue list = noteObj.Get("checklist");
            if (list == null || !list.IsArray || itemIndex < 0 || itemIndex >= list.Items.Count)
            {
                r.Outcome = ToggleOutcome.ItemGone;
                r.Detail = "checklist index " + itemIndex + " is out of range (count="
                    + (list == null || !list.IsArray ? "(no checklist array)" : list.Items.Count.ToString(CultureInfo.InvariantCulture)) + ")";
                Log.Line("notes write REFUSED (" + r.Outcome + "): " + r.Detail);
                return r;
            }
            JsonValue item = list.Items[itemIndex];
            if (item == null || !item.IsObject)
            {
                r.Outcome = ToggleOutcome.ItemGone;
                r.Detail = "checklist[" + itemIndex + "] is not an object";
                Log.Line("notes write REFUSED (" + r.Outcome + "): " + r.Detail);
                return r;
            }
            string freshText = JsonValue.StrOr(item, "text", "");
            if (!string.Equals(freshText, displayedItemText, StringComparison.Ordinal))
            {
                r.Outcome = ToggleOutcome.Conflict;
                r.Detail = "checklist[" + itemIndex + "] text changed (card='" + displayedItemText
                    + "' file='" + freshText + "')";
                Log.Line("notes write REFUSED (" + r.Outcome + "): " + r.Detail);
                return r;
            }

            bool currentDone = Boolish(item.Get("done"));
            r.WasDone = currentDone;
            if (currentDone == r.DesiredDone)
            {
                r.Outcome = ToggleOutcome.Unchanged;
                r.Detail = "file already holds done=" + currentDone + " for checklist[" + itemIndex + "]";
                Log.Line("notes write SKIPPED (" + r.Outcome + "): " + r.Detail);
                return r;
            }

            // ---- 5. mutate the fresh tree: only this item + this note's updatedAt ----
            SetMember(item, "done", JsonValue.FromBool(r.DesiredDone));
            SetMember(noteObj, "updatedAt", JsonValue.FromInt(NowMs()));

            string newText = Json.Write(root);
            byte[] newBytes = new UTF8Encoding(false).GetBytes(newText);

            // ---- 6. rolling safety copy (once per process, next to the exe) ----
            WriteSafetyCopyOnce(backupDir, original);

            // ---- 7. atomic replace ----
            string writeErr;
            if (!AtomicReplace(path, newBytes, out writeErr))
            {
                r.Outcome = ToggleOutcome.IoError;
                r.Detail = "write failed: " + writeErr;
                Log.Line("notes write FAILED: " + r.Detail);
                return r;
            }

            // ---- 8. verify by re-parsing the file that is now on disk ----
            string verifyErr;
            if (!VerifyOnlyExpectedPathsChanged(root, noteIndex, itemIndex, path, out verifyErr))
            {
                string restoreErr;
                bool restored = AtomicReplace(path, original, out restoreErr);
                r.Outcome = ToggleOutcome.IoError;
                r.Detail = "post-write verification failed (" + verifyErr + "); original bytes "
                    + (restored ? "RESTORED" : "COULD NOT BE RESTORED: " + restoreErr);
                Log.Line("notes write ROLLED BACK: " + r.Detail);
                return r;
            }

            r.Outcome = ToggleOutcome.Written;
            r.NewFileSha256 = Sha256Hex(newBytes);
            r.NewNoteJson = Json.Write(noteObj);
            r.Detail = "checklist[" + itemIndex + "] done " + currentDone + "->" + r.DesiredDone
                + "; file " + original.Length + "->" + newBytes.Length + " bytes";
            Log.Line("notes write OK: " + r.Detail + " sha256=" + r.NewFileSha256);
            return r;
        }

        // ------------------------------------------------------------------
        // verification: exactly the two intended paths may differ
        // ------------------------------------------------------------------

        /// <summary>
        /// Re-read what is on disk, parse it, diff it against the tree we meant to write, and accept
        /// ONLY the two intended paths. This is what turns "the serializer probably round-trips" into
        /// an asserted property: a dropped field, a reordered array or a lost member shows up as an
        /// unexpected path and rolls the file back.
        /// </summary>
        private static bool VerifyOnlyExpectedPathsChanged(
            JsonValue expected, int noteIndex, int itemIndex, string path, out string err)
        {
            err = null;
            string readErr;
            byte[] onDisk = ReadAllBytesShared(path, out readErr);
            if (onDisk == null) { err = "re-read failed: " + readErr; return false; }

            JsonValue actual;
            string parseErr;
            if (!TryParseArray(onDisk, out actual, out parseErr)) { err = "re-parse failed: " + parseErr; return false; }

            List<string> diff = new List<string>();
            Diff(expected, actual, "", diff);

            string expectDone = "/" + noteIndex + "/checklist/" + itemIndex + "/done";
            string expectStamp = "/" + noteIndex + "/updatedAt";
            List<string> unexpected = new List<string>();
            for (int i = 0; i < diff.Count; i++)
            {
                if (!string.Equals(diff[i], expectDone, StringComparison.Ordinal)
                    && !string.Equals(diff[i], expectStamp, StringComparison.Ordinal))
                {
                    unexpected.Add(diff[i]);
                }
            }
            if (unexpected.Count > 0)
            {
                err = "unexpected difference(s): [" + string.Join(", ", unexpected.ToArray())
                    + "] (allowed only " + expectDone + " and " + expectStamp + ")";
                return false;
            }
            return true;
        }

        /// <summary>Structural diff: reports the JSON paths at which <paramref name="b"/> differs from
        /// <paramref name="a"/>. Object members are compared by key (a pure re-ordering is not a
        /// difference), arrays by index, and a length change is reported as "&lt;path&gt;[len]".
        /// Public so --selftest can prove the verifier has discriminating power (negative control).</summary>
        public static void Diff(JsonValue a, JsonValue b, string path, List<string> outPaths)
        {
            if (a == null || b == null)
            {
                if (!ReferenceEquals(a, b)) outPaths.Add(path + "(null)");
                return;
            }
            if (a.Kind != b.Kind) { outPaths.Add(path + "(kind)"); return; }
            switch (a.Kind)
            {
                case JsonKind.Null:
                    return;
                case JsonKind.Bool:
                    if (a.Bool != b.Bool) outPaths.Add(path);
                    return;
                case JsonKind.Number:
                    if (a.Number != b.Number) outPaths.Add(path);
                    return;
                case JsonKind.String:
                    if (!string.Equals(a.Str, b.Str, StringComparison.Ordinal)) outPaths.Add(path);
                    return;
                case JsonKind.Array:
                    if (a.Items == null || b.Items == null) { outPaths.Add(path); return; }
                    if (a.Items.Count != b.Items.Count) { outPaths.Add(path + "[len]"); return; }
                    for (int i = 0; i < a.Items.Count; i++) Diff(a.Items[i], b.Items[i], path + "/" + i, outPaths);
                    return;
                case JsonKind.Object:
                    if (a.Members == null || b.Members == null) { outPaths.Add(path); return; }
                    Dictionary<string, JsonValue> bm = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                    for (int i = 0; i < b.Members.Count; i++) bm[b.Members[i].Key] = b.Members[i].Value;
                    HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < a.Members.Count; i++)
                    {
                        string k = a.Members[i].Key;
                        seen.Add(k);
                        JsonValue bv;
                        if (!bm.TryGetValue(k, out bv)) { outPaths.Add(path + "/" + k); continue; }
                        Diff(a.Members[i].Value, bv, path + "/" + k, outPaths);
                    }
                    for (int i = 0; i < b.Members.Count; i++)
                    {
                        if (!seen.Contains(b.Members[i].Key)) outPaths.Add(path + "/" + b.Members[i].Key);
                    }
                    return;
            }
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        public static int FindNoteIndex(JsonValue root, string noteId)
        {
            if (root == null || !root.IsArray || root.Items == null) return -1;
            for (int i = 0; i < root.Items.Count; i++)
            {
                JsonValue o = root.Items[i];
                if (o == null || !o.IsObject) continue;
                if (string.Equals(JsonValue.StrOr(o, "id", ""), noteId, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        public static bool TryParseArray(byte[] bytes, out JsonValue root, out string err)
        {
            root = null;
            err = null;
            string text = DecodeUtf8(bytes);
            if (text == null) { err = "content is not valid UTF-8"; return false; }
            text = text.Trim();
            if (text.Length == 0) { err = "file is empty"; return false; }
            if (text[0] != '[') { err = "root is not a JSON array"; return false; }
            try
            {
                root = Json.Parse(text);
            }
            catch (Exception ex)
            {
                err = "JSON parse failed: " + ex.Message;
                return false;
            }
            if (root == null || !root.IsArray) { err = "parsed root is not an array"; return false; }
            return true;
        }

        /// <summary>UTF-8 decode that strips a BOM and refuses replacement characters (a mojibake file
        /// must never be re-serialised on top of itself).</summary>
        public static string DecodeUtf8(byte[] bytes)
        {
            if (bytes == null) return null;
            int start = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) start = 3;
            try
            {
                UTF8Encoding strict = new UTF8Encoding(false, true);
                return strict.GetString(bytes, start, bytes.Length - start);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Read every byte, tolerating a concurrent writer: share read/write/delete and retry.</summary>
        public static byte[] ReadAllBytesShared(string path, out string err)
        {
            err = null;
            Exception last = null;
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                try
                {
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        long len = fs.Length;
                        byte[] buf = new byte[len];
                        int got = 0;
                        while (got < buf.Length)
                        {
                            int n = fs.Read(buf, got, buf.Length - got);
                            if (n <= 0) break;
                            got += n;
                        }
                        if (got != buf.Length)
                        {
                            byte[] shorter = new byte[got];
                            Array.Copy(buf, shorter, got);
                            return shorter;
                        }
                        return buf;
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                    System.Threading.Thread.Sleep(40);
                }
            }
            err = last == null ? "unknown" : (last.GetType().Name + ": " + last.Message);
            return null;
        }

        /// <summary>
        /// Atomic replacement that never deletes the destination: File.Replace (ReplaceFile) keeps the
        /// old file readable to anyone who already has it open and swaps the directory entry in one
        /// step, so a concurrent reader can never observe "the data file is missing". If Replace is not
        /// available it falls back to an in-place overwrite (still never a missing file).
        /// </summary>
        public static bool AtomicReplace(string path, byte[] content, out string err)
        {
            err = null;
            string tmp = path + TmpSuffix;
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                using (FileStream fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(content, 0, content.Length);
                    fs.Flush(true);
                }
            }
            catch (Exception ex)
            {
                err = "temp write failed: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            Exception replaceFail = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                    return true;
                }
                catch (Exception ex)
                {
                    replaceFail = ex;
                    System.Threading.Thread.Sleep(50);
                }
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Copy(tmp, path, true);   // overwrite in place: no window with no file
                    File.Delete(tmp);
                    return true;
                }
            }
            catch (Exception ex)
            {
                err = "File.Replace failed (" + Describe(replaceFail) + "); in-place copy failed ("
                    + ex.GetType().Name + ": " + ex.Message + ")";
                return false;
            }
            err = "File.Replace failed: " + Describe(replaceFail);
            return false;
        }

        private static string Describe(Exception ex)
        {
            return ex == null ? "(none)" : (ex.GetType().Name + ": " + ex.Message);
        }

        private static void WriteSafetyCopyOnce(string backupDir, byte[] original)
        {
            if (_backupWrittenThisProcess) return;
            if (string.IsNullOrEmpty(backupDir)) return;
            try
            {
                if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);
                string target = Path.Combine(backupDir, BackupFileName);
                File.WriteAllBytes(target, original);
                _backupWrittenThisProcess = true;
                Log.Line("safety copy of the pre-write notes.json written once per process -> " + target
                    + " (" + original.Length + " bytes)");
            }
            catch (Exception ex)
            {
                Log.Line("safety copy FAILED (not fatal, the write itself is unaffected): "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Test hook: lets --selftest emulate "a fresh process" for the once-per-process copy.</summary>
        public static void ResetSafetyCopyFlagForTest()
        {
            _backupWrittenThisProcess = false;
        }

        public static void SetMember(JsonValue obj, string key, JsonValue value)
        {
            if (obj == null || obj.Members == null) return;
            for (int i = 0; i < obj.Members.Count; i++)
            {
                if (string.Equals(obj.Members[i].Key, key, StringComparison.Ordinal))
                {
                    obj.Members[i] = new KeyValuePair<string, JsonValue>(key, value);
                    return;
                }
            }
            obj.Members.Add(new KeyValuePair<string, JsonValue>(key, value));
        }

        public static bool Boolish(JsonValue v)
        {
            if (v == null) return false;
            switch (v.Kind)
            {
                case JsonKind.Null: return false;
                case JsonKind.Bool: return v.Bool;
                case JsonKind.Number: return v.Number != 0 && !double.IsNaN(v.Number);
                case JsonKind.String: return v.Str != null && v.Str.Length > 0;
                default: return true;
            }
        }

        public static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        public static string Sha256Hex(byte[] bytes)
        {
            if (bytes == null) return null;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder(digest.Length * 2);
                for (int i = 0; i < digest.Length; i++) sb.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString().ToUpperInvariant();
            }
        }

        public static string Short(string id)
        {
            if (string.IsNullOrEmpty(id)) return "(none)";
            return id.Substring(0, Math.Min(8, id.Length));
        }
    }
}

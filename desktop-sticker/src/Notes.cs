// S1 sticker: domain model + faithful port of demo/app.js normalize() semantics.
// Pure ASCII source.
//
// S2 (2026-09-20) narrowed the S1 "notes.json is read-only" constraint to: THIS FILE never writes.
// Loading stays read-only by construction (no File.Write*, no backup, no touch); the single writer in
// the product is NotesWriter.cs, and it only runs when the user clicks a checklist box.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TieTieSticker
{
    internal sealed class CheckItem
    {
        public string Text = "";
        public bool Done;
    }

    internal sealed class Note
    {
        public string Id = "";
        public string Title = "";
        public string Content = "";
        public string Color = "yellow";
        public bool Done;
        public bool Pinned;
        public bool Archived;
        public string DueAt = "";
        public List<string> Tags = new List<string>();
        public List<CheckItem> Checklist = new List<CheckItem>();

        /// <summary>Index inside the notes.json array (used for stable tie-breaks only).</summary>
        public int SourceIndex;

        /// <summary>Milliseconds since the epoch as stored in notes.json (0 when absent). Diagnostics only.</summary>
        public long UpdatedAt;

        /// <summary>
        /// Canonical JSON of THIS note exactly as it was read (S2). The write path compares the
        /// freshly read note against this string: a difference means somebody else edited the note
        /// after the card was rendered, and the click is refused instead of silently overwriting.
        /// </summary>
        public string RawJson = "";
    }

    /// <summary>
    /// Read-only loader for data/notes.json.
    /// Writes: NEVER. No File.Exists-then-modify, no backup, no touch.
    /// (The only writer in the product is NotesWriter.cs - assertion s2-loader-still-write-free.)
    /// </summary>
    internal static class NotesStore
    {
        public const string EnvVarName = "TIETIE_NOTES";

        /// <summary>SHA-256 of the file content captured by the most recent Load() (audit trail).</summary>
        public static string LastLoadedSha256;
        public static string LastLoadedPath;

        /// <summary>Independent SHA-256 of a file, computed without going through the parser.</summary>
        public static string Sha256OfFile(string path)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            using (System.IO.FileStream fs = new System.IO.FileStream(path, System.IO.FileMode.Open,
                       System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
            {
                byte[] digest = sha.ComputeHash(fs);
                System.Text.StringBuilder sb = new System.Text.StringBuilder(digest.Length * 2);
                for (int i = 0; i < digest.Length; i++) sb.Append(digest[i].ToString("x2"));
                return sb.ToString().ToUpperInvariant();
            }
        }

        /// <summary>Resolve per spec 9.1: --notes > TIETIE_NOTES > exeDir\data > ..\data > ..\..\data</summary>
        public static string ResolvePath(string exeDir, string cliNotes)
        {
            List<string> tried = new List<string>();
            if (!string.IsNullOrEmpty(cliNotes))
            {
                string p = cliNotes;
                tried.Add(p);
                if (File.Exists(p)) { Log.Line("notes.json resolved via --notes: " + p); return p; }
            }

            string env = Environment.GetEnvironmentVariable(EnvVarName);
            if (!string.IsNullOrEmpty(env))
            {
                tried.Add(env);
                if (File.Exists(env)) { Log.Line("notes.json resolved via $" + EnvVarName + ": " + env); return env; }
            }

            string cur = exeDir;
            for (int up = 0; up < 3; up++)
            {
                string cand = Path.Combine(cur, "data", "notes.json");
                tried.Add(cand);
                if (File.Exists(cand)) { Log.Line("notes.json resolved by walk-up (" + up + "): " + cand); return cand; }
                string parent = Path.GetDirectoryName(cur.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(parent)) break;
                cur = parent;
            }

            Log.Line("notes.json NOT FOUND. candidates tried: " + string.Join(" | ", tried.ToArray()));
            return null;
        }

        /// <summary>
        /// Read + parse + normalize. Never throws. Returns empty list on any failure.
        /// READ-ONLY BY CONSTRUCTION: this method contains no write call of any kind
        /// (no File.Write*, no backup, no touch, no FileMode.Open-with-lock).
        /// </summary>
        public static List<Note> Load(string path)
        {
            List<Note> result = new List<Note>();
            if (string.IsNullOrEmpty(path))
            {
                Log.Line("notes load: no path -> empty library (0 cards)");
                return result;
            }

            // Record the content hash for the audit trail: the self-test compares this against an
            // independently computed digest so "read-only" is proven by hash, not by byte length.
            try { LastLoadedSha256 = Sha256OfFile(path); LastLoadedPath = path; }
            catch (Exception ex) { LastLoadedSha256 = null; Log.Line("notes: hash capture failed: " + ex.Message); }
            Log.Line("notes.json read-only load: sha256=" + (LastLoadedSha256 ?? "(unavailable)") + " path=" + path);

            string text = null;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    text = File.ReadAllText(path, Encoding.UTF8);
                    break;
                }
                catch (Exception ex)
                {
                    Log.Line("notes read attempt " + attempt + " failed: " + ex.GetType().Name + ": " + ex.Message);
                    if (attempt == 1) System.Threading.Thread.Sleep(100);
                }
            }
            if (text == null)
            {
                Log.Line("notes load FAILED after retry -> empty library (0 cards). No file was written.");
                return result;
            }

            JsonValue root;
            try
            {
                root = Json.Parse(text);
            }
            catch (Exception ex)
            {
                Log.Line("notes JSON parse failed: " + ex.Message + " -> empty library (0 cards)");
                return result;
            }

            if (root == null || !root.IsArray)
            {
                Log.Line("notes root is not a JSON array -> empty library (0 cards)");
                return result;
            }

            int skippedNoId = 0;
            for (int i = 0; i < root.Items.Count; i++)
            {
                JsonValue raw = root.Items[i];
                if (raw == null || !raw.IsObject) continue;
                Note n = Normalize(raw, i);
                if (n == null) { skippedNoId++; continue; }
                result.Add(n);
            }
            Log.Line("notes loaded: " + result.Count + " normalized, " + skippedNoId + " skipped (missing/empty id)");
            return result;
        }

        /// <summary>Faithful port of demo/app.js normalize(note). Returns null when id is not usable.</summary>
        private static Note Normalize(JsonValue raw, int index)
        {
            JsonValue idv = raw.Get("id");
            // id must exist, be a string and be non-empty -> otherwise skip the note entirely.
            // Comparison key stays the ORIGINAL string: no trim, no normalization.
            if (idv == null || idv.Kind != JsonKind.String) return null;
            if (idv.Str == null || idv.Str.Length == 0) return null;

            Note n = new Note();
            n.Id = idv.Str;
            n.SourceIndex = index;

            JsonValue t = raw.Get("title");
            n.Title = (t != null && t.Kind == JsonKind.String) ? t.Str : "";

            JsonValue c = raw.Get("content");
            n.Content = (c != null && c.Kind == JsonKind.String) ? c.Str : "";

            // normalize(): color defaults to "yellow" when missing / not one of the 6 keys.
            JsonValue col = raw.Get("color");
            string color = (col != null && col.Kind == JsonKind.String) ? col.Str : null;
            n.Color = Palette.IsKnownColor(color) ? color : "yellow";

            n.Done = Boolish(raw.Get("done"));
            n.Pinned = Boolish(raw.Get("pinned"));
            n.Archived = Boolish(raw.Get("archived"));

            JsonValue due = raw.Get("dueAt");
            n.DueAt = (due != null && due.Kind == JsonKind.String) ? due.Str : "";

            JsonValue tags = raw.Get("tags");
            if (tags != null && tags.IsArray)
            {
                for (int i = 0; i < tags.Items.Count; i++) n.Tags.Add(ToText(tags.Items[i]));
            }

            JsonValue cl = raw.Get("checklist");
            if (cl != null && cl.IsArray)
            {
                for (int i = 0; i < cl.Items.Count; i++)
                {
                    JsonValue item = cl.Items[i];
                    CheckItem ci = new CheckItem();
                    if (item != null && item.IsObject)
                    {
                        ci.Text = ToText(item.Get("text"));
                        ci.Done = Boolish(item.Get("done"));
                    }
                    else
                    {
                        ci.Text = "";
                        ci.Done = false;
                    }
                    n.Checklist.Add(ci);
                }
            }

            JsonValue up = raw.Get("updatedAt");
            if (up != null && up.Kind == JsonKind.Number && !double.IsNaN(up.Number))
                n.UpdatedAt = (long)Math.Round(up.Number);

            // S2: canonical form of the note exactly as read - the write path's conflict baseline.
            try { n.RawJson = Json.Write(raw); }
            catch { n.RawJson = ""; }

            return n;
        }

        /// <summary>JS truthiness of `!!value`: falsey = false/0/""/null/undefined/NaN.</summary>
        private static bool Boolish(JsonValue v)
        {
            if (v == null) return false;
            switch (v.Kind)
            {
                case JsonKind.Null: return false;
                case JsonKind.Bool: return v.Bool;
                case JsonKind.Number: return v.Number != 0 && !double.IsNaN(v.Number);
                case JsonKind.String: return v.Str != null && v.Str.Length > 0;
                default: return true; // arrays and objects are always truthy in JS
            }
        }

        /// <summary>JS String(x) for the two field types we care about.</summary>
        private static string ToText(JsonValue v)
        {
            if (v == null) return "";
            switch (v.Kind)
            {
                case JsonKind.Null: return "";
                case JsonKind.String: return v.Str ?? "";
                case JsonKind.Bool: return v.Bool ? "true" : "false";
                case JsonKind.Number:
                    if (v.Number == Math.Floor(v.Number) && !double.IsInfinity(v.Number))
                        return ((long)v.Number).ToString(CultureInfo.InvariantCulture);
                    return v.Number.ToString("R", CultureInfo.InvariantCulture);
                default: return "";
            }
        }
    }
}

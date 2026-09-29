// S1 sticker: user-visible strings. Written as ASCII escapes so this source file stays pure ASCII
// regardless of the code page csc.exe assumes for .cs input.
using System;
using System.Globalization;

namespace TieTieSticker
{
    internal static class UiStrings
    {
        /// <summary>Spec 7.4 / P5: the ONE and ONLY context-menu entry.</summary>
        public static readonly string ExitSticker = Esc("9000 51FA 8D34 7EB8");

        /// <summary>Spec 9.5: due badge caption for the "today" state.</summary>
        public static readonly string Today = Esc("4ECA 5929");

        /// <summary>Spec 9.5: due badge caption for the "overdue" state.</summary>
        public static readonly string Overdue = Esc("5DF2 903E 671F");

        /// <summary>Spec v11 3.10: title of the one and only modal notice (second instance).</summary>
        public static readonly string AlreadyRunningTitle = Esc("8D34 8D34 4FBF 7B7E");

        /// <summary>Spec v11 3.10: body of that notice - "the sticker app is already running".</summary>
        public static readonly string AlreadyRunningBody = Esc("8D34 8D34 4FBF 7B7E 8D34 7EB8 5DF2 5728 8FD0 884C 3002");

        // ---- S2 3.x: the transient card notice (drawn on the card, no modal, no console) ----
        /// <summary>Shown when the clicked note changed elsewhere: "content was updated, please click again".</summary>
        public static readonly string NoticeConflict = Esc("5185 5BB9 5DF2 66F4 65B0 FF0C 8BF7 518D 70B9 4E00 6B21");

        /// <summary>Shown when the note is gone from notes.json: "this note no longer exists".</summary>
        public static readonly string NoticeNoteGone = Esc("4FBF 7B7E 5DF2 4E0D 5B58 5728");

        /// <summary>Shown when the write itself failed and the data was rolled back: "write failed, data unchanged".</summary>
        public static readonly string NoticeWriteFailed = Esc("5199 5165 5931 8D25 FF0C 6570 636E 672A 6539 52A8");

        // ---- v13: the system tray (spec 3.4) ----
        /// <summary>Tray tooltip / app name.</summary>
        public static readonly string TrayTooltip = Esc("8D34 8D34 4FBF 7B7E");

        /// <summary>Tray header base text: "placed notes" - the count is appended as (N).</summary>
        public static readonly string TrayHeader = Esc("5DF2 8D34 51FA 7684 4FBF 7B7E");

        /// <summary>Full-width opening parenthesis (U+FF08) - the header uses full-width brackets.</summary>
        public static readonly string ParenOpen = Esc("FF08");

        /// <summary>Full-width closing parenthesis (U+FF09).</summary>
        public static readonly string ParenClose = Esc("FF09");

        /// <summary>A placed note whose title is empty: "untitled".</summary>
        public static readonly string NoTitle = Esc("65E0 6807 9898");

        private static string Esc(string hexWords)
        {
            string[] parts = hexWords.Split(' ');
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                sb.Append((char)int.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}

// S1 sticker: typography engine - fonts, metrics, greedy line wrapping, natural-height measurement.
// Pure ASCII source. All metrics come from spec section 4 (converted from demo/styles.css).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TieTieSticker
{
    internal static class Typo
    {
        // Font families (spec 4.1): Microsoft YaHei UI -> Microsoft YaHei -> Segoe UI
        public static readonly string Family = PickFamily();

        private static string PickFamily()
        {
            string[] chain = new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" };
            List<string> installed = new List<string>();
            try
            {
                FontFamily[] fams = FontFamily.Families;
                for (int i = 0; i < fams.Length; i++) installed.Add(fams[i].Name);
            }
            catch
            {
            }
            for (int i = 0; i < chain.Length; i++)
            {
                for (int j = 0; j < installed.Count; j++)
                {
                    if (string.Equals(installed[j], chain[i], StringComparison.OrdinalIgnoreCase)) return chain[i];
                }
            }
            return "Segoe UI";
        }

        public static readonly Font Title = new Font(Family, 12f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Body = new Font(Family, 10f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Tag = new Font(Family, 8f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Check = new Font(Family, 10f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Due = new Font(Family, 9f, FontStyle.Bold, GraphicsUnit.Point);

        // ------- spec 4.2 spacing constants (px) -------
        public const int PAD_LR = 16;
        public const int PAD_TOP = 16;
        public const int PAD_BOTTOM = 14;
        public const int BAND_H = 5;
        public const int RADIUS = 18;
        public const int ROW_TOP_H = 21;
        public const int ROW_TOP_GAP = 5;
        public const int TITLE_LH = 22;
        public const int TITLE_MB = 6;
        public const int BODY_LH = 22;
        public const int BODY_MB = 10;
        public const int TAG_H = 20;
        public const int TAG_GAP_X = 6;
        public const int TAG_GAP_Y = 6;
        public const int TAGS_MB = 9;
        public const int CHECK_BOX = 13;
        public const int CHECK_BOX_MT = 3;
        public const int CHECK_GAP_X = 8;
        public const int CHECK_ROW_H = 18;
        public const int CHECK_GAP = 6;
        public const int CHECK_MB = 9;
        public const int FOOT_BORDER = 1;
        public const int FOOT_PT = 9;
        public const int FOOT_H = 30;
        /// <summary>Spec 4.4 definition 1: FOOT_BORDER + FOOT_PT + FOOT_H - the bottom row always reserves space.</summary>
        public const int FOOT_BLOCK = FOOT_BORDER + FOOT_PT + FOOT_H;   // 40
        public const int DUE_H = 21;
        public const int FLAG_SIZE = 12;
        public const int PIN_BTN = 30;

        // ------- spec 7.2.1 size constants -------
        public const int DEFAULT_W = 260;
        public const int DEFAULT_H = 320;
        public const int MIN_W = 180;
        // MIN_H = 190（原 140）。
        //   来源：用户 2026-09-20 交互选择 —— captain 用提问工具问「卡片拉到最小时，标题末行会被
        //     底部图钉行压住 —— 你想怎么处理？」，用户在「①保持 180×140 ②抬高最小高度（例如
        //     180×190）③先不动」中选定 **②**。规格 v10 §7.2.1 已按此定案（规格是唯一真源）。
        //   几何依据（规格 v10 §7.2.1 说明栏，分析师独立推导）：
        //     FooterTop = H − PAD_BOTTOM(14) − FOOT_BLOCK(40) = H − 54
        //     标题块末尾 = TitleY(42) + TITLE_LH(22)×n + TITLE_MB(6)
        //     要求标题末行不被底部图钉行压住 ⇒ H ≥ 124 + 22(n−1)
        //     ⇒ 1 行 124 / 2 行 146 / 3 行 168 / 4 行恰好 190
        //   ⇒ 原 140 只覆盖 1 行标题；190 覆盖至 4 行标题（= 用户选定的保守产品下限，非几何必需）。
        //   ⚠️ 124 = PAD_TOP(16) + (ROW_TOP_H+ROW_TOP_GAP)(26) + (TITLE_LH+TITLE_MB)(28) + FOOT_BLOCK(40)
        //      + PAD_BOTTOM(14) —— 那是"1 行标题的最低高度"，**不是 190 的算式**；190 的算式是上面的
        //      H ≥ 124 + 22(n−1) 取 n=4。
        //   更长的标题（>4 行）在最小卡上仍按 §7.2.3 底部裁剪（图钉可点优先，既定行为）。
        public const int MIN_H = 190;
        public const int RESIZE_BORDER = 6;
        public const int RESIZE_CORNER = 16;
        public const int MIN_VISIBLE_W = 80;
        public const int MIN_VISIBLE_H = 40;
        public const int CASCADE_STEP = 28;
        public const int CASCADE_OFFSET = 40;

        // ---------------- spec v11 3.2: the close (X) box in the .note-top row ----------------
        // v11 spec: .dsh-meow/sticker/06-S1增量规格v11-关闭按钮与空集重贴.md (locator "^### 3\\.2")
        // All integers: no float geometry, so the assertions below are exact.
        public const int CLOSE_BOX = 18;                 // visual box (square)
        public const int CLOSE_HIT = 22;                 // hit box (> visual box, min clickable size)
        public const int CLOSE_GAP = 6;                  // gap between the pinned flag and the box
        public const int CLOSE_TOP = PAD_TOP + 2;        // = 18, vertically centred in ROW_TOP_H
        public const int CLOSE_RIGHT_INSET = 0;          // box right edge == content right edge
        public const int CLOSE_HIT_HALF = (CLOSE_HIT - CLOSE_BOX) / 2;   // = 2
        public const int CLOSE_HIT_TOP = CLOSE_TOP - CLOSE_HIT_HALF;     // = 16 == PAD_TOP
        public const double CLOSE_ALPHA = 0.60;          // same band as PIN_OFF_ALPHA

        // ---------------- spec v11 6: the 4-level hit priority (single source of truth) ----------------
        // 1 resize band > 2 close hit box > 3 pin button > 4 drag everywhere else.
        public enum HitLevel { Resize, Close, Pin, Client }

        public static int ContentRight(int clientWidth) { return clientWidth - PAD_LR; }

        /// <summary>Visual box left edge (spec v11 3.2).</summary>
        public static int CloseLeft(int clientWidth)
        {
            return ContentRight(clientWidth) - CLOSE_RIGHT_INSET - CLOSE_BOX;
        }

        /// <summary>Hit box rectangle, client coordinates (spec v11 3.4).</summary>
        public static Rectangle CloseHitRect(int clientWidth)
        {
            return new Rectangle(CloseLeft(clientWidth) - CLOSE_HIT_HALF, CLOSE_HIT_TOP, CLOSE_HIT, CLOSE_HIT);
        }

        /// <summary>Pinned flag right edge after the v11 shift (user ruling: shift = CLOSE_BOX + CLOSE_GAP).</summary>
        public static int FlagRight(int clientWidth) { return CloseLeft(clientWidth) - CLOSE_GAP; }

        public static int FlagLeft(int clientWidth) { return FlagRight(clientWidth) - FLAG_SIZE; }

        /// <summary>
        /// Pure 4-level hit classification (spec v11 6). Used by the live form AND by --selftest, so the
        /// assertion "a press inside the close box does not arm a drag" tests the real production rule.
        /// </summary>
        public static HitLevel ClassifyHit(int clientWidth, int clientHeight, int footerContentTop, Point p)
        {
            int b = RESIZE_BORDER;
            if (p.X < b || p.X >= clientWidth - b || p.Y < b || p.Y >= clientHeight - b) return HitLevel.Resize;
            if (CloseHitRect(clientWidth).Contains(p)) return HitLevel.Close;
            if (new Rectangle(PAD_LR, footerContentTop, PIN_BTN, PIN_BTN).Contains(p)) return HitLevel.Pin;
            return HitLevel.Client;
        }

        /// <summary>
        /// S2: index of the checklist item whose clickable band contains <paramref name="p"/>, or -1.
        /// Uses the SAME rects the renderer paints from (Layout.ChecklistHitRects), so the clickable
        /// target can never drift away from the painted box - --selftest asserts both directions.
        /// Only reached when ClassifyHit() already answered Client, i.e. the resize band, the close box
        /// and the pin button keep their v11 priority.
        /// </summary>
        public static int ChecklistItemAt(Layout L, Point p)
        {
            if (L == null || L.ChecklistHitRects == null) return -1;
            for (int i = 0; i < L.ChecklistHitRects.Count; i++)
            {
                if (L.ChecklistHitRects[i].Contains(p)) return i;
            }
            return -1;
        }

        public const string TagChipMode = "EDGE_TINT";   // spec 3.5 / P3
        public const bool AutoFitOnFirstShow = false;    // spec 7.2.1 alternative, default off
        public const bool UseLocalDate = false;          // spec 9.5 / P4 - faithful UTC slice

        public const double BODY_ALPHA = 0.88;
        public const double BAND_ALPHA = 0.85;
        public const double TAG_ALPHA = 0.40;
        public const double FOOT_BORDER_ALPHA = 0.70;
        public const double PIN_OFF_ALPHA = 0.60;
        public const double FLAG_ALPHA = 0.85;
        public const double DONE_ALPHA = 0.55;

        public static readonly Color DueTodayFg = ColorTranslator.FromHtml("#c8811a");
        public static readonly Color DueTodayBg = Color.FromArgb(36, 200, 129, 26);
        public static readonly Color DueOverdueFg = ColorTranslator.FromHtml("#d64545");
        public static readonly Color DueOverdueBg = Color.FromArgb(31, 214, 69, 69);
        public static readonly Color CheckboxBorder = ColorTranslator.FromHtml("#B7B2AB");
        /// <summary>Spec v11 3.3: --text @ CLOSE_ALPHA(0.60) = Color.FromArgb(153, 0x24, 0x21, 0x2B).</summary>
        public static readonly Color CloseFg = Color.FromArgb(153, 0x24, 0x21, 0x2B);

        // ---------------- wrapping ----------------

        /// <summary>
        /// Greedy per-character wrap (spec 5.3): honours '\n', then accumulates characters while the
        /// measured width fits into <paramref name="maxWidth"/>. Never uses GDI WordBreak.
        /// </summary>
        public static List<string> WrapLine(Font font, string text, int maxWidth)
        {
            List<string> lines = new List<string>();
            if (text == null) text = "";
            string[] paragraphs = text.Split('\n');
            for (int pi = 0; pi < paragraphs.Length; pi++)
            {
                string para = paragraphs[pi];
                para = para.TrimEnd('\r');
                if (para.Length == 0)
                {
                    lines.Add("");
                    continue;
                }
                System.Text.StringBuilder cur = new System.Text.StringBuilder();
                int curW = 0;
                for (int i = 0; i < para.Length; i++)
                {
                    string chStr = para[i].ToString();
                    int cw = Measure(font, chStr);
                    if (cur.Length > 0 && curW + cw > maxWidth)
                    {
                        lines.Add(cur.ToString());
                        cur.Length = 0;
                        curW = 0;
                    }
                    cur.Append(para[i]);
                    curW += cw;
                }
                lines.Add(cur.ToString());
            }
            if (lines.Count == 0) lines.Add("");
            return lines;
        }

        public static int Measure(Font font, string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
        }

        /// <summary>Line count of a wrapped block; 0 when the text is empty (block not rendered).</summary>
        public static int CountLines(Font font, string text, int maxWidth)
        {
            if (text == null || text.Length == 0) return 0;
            return WrapLine(font, text, maxWidth).Count;
        }

        // ---------------- layout engine (spec 4.3 / 4.4) ----------------

        public sealed class Layout
        {
            public int ContentTop;
            public int RowTopY;
            public int TitleY;
            public int BodyY;
            public int TagsY;
            public int ChecklistY;
            public int ContentEnd;      // y BEFORE the bottom row is added (spec 4.4 definition 1, v9 wording)
            public int FooterTop;       // y of the 1px dashed border
            public int FooterContentTop;// pin button top
            public int FooterBottom;    // pin button bottom
            /// <summary>ContentEnd + FOOT_BLOCK(40) + PAD_BOTTOM(14) - spec 4.4 definition 1. Includes the
            /// bottom row, so the pin row can never be clipped away.</summary>
            public int NaturalHeight;
            public int MaxTextWidth;

            public List<string> TitleLines;
            public List<string> BodyLines;
            public List<int> ChecklistLineCounts;

            // ---- S2: checklist hit geometry (one source for painting AND hit-testing) ----
            /// <summary>Top y of every checklist item (same progression DrawChecklist paints with).</summary>
            public List<int> ChecklistItemY;
            /// <summary>Painted checkbox square of every item, in client coordinates.</summary>
            public List<Rectangle> ChecklistBoxRects;
            /// <summary>Clickable band of every item (box + its text rows), in client coordinates.
            /// The web version toggles on a click on the box OR the label text (demo/app.js renders a
            /// label), so the sticker offers the same target instead of a 13x13 pixel square.</summary>
            public List<Rectangle> ChecklistHitRects;
        }

        public static Layout Measure(Note note, int clientWidth, int clientHeight, int padTop)
        {
            Layout L = new Layout();
            int textW = clientWidth - 2 * PAD_LR;
            if (textW < 20) textW = 20;
            L.MaxTextWidth = textW;

            int y = padTop;
            L.ContentTop = y;
            L.RowTopY = y;
            y += ROW_TOP_H + ROW_TOP_GAP;

            L.TitleY = y;
            L.TitleLines = WrapLine(Title, note.Title, textW);
            y += TITLE_LH * L.TitleLines.Count;
            y += TITLE_MB;

            L.BodyLines = null;
            if (note.Content != null && note.Content.Length > 0)
            {
                L.BodyY = y;
                L.BodyLines = WrapLine(Body, note.Content, textW);
                y += BODY_LH * L.BodyLines.Count;
                y += BODY_MB;
            }

            bool hasTags = note.Tags != null && note.Tags.Count > 0;
            if (hasTags)
            {
                L.TagsY = y;
                int rows = TagRowCount(note.Tags, textW);
                y += TAG_H * rows + TAG_GAP_Y * (rows - 1);
                y += TAGS_MB;
            }

            bool hasCheck = note.Checklist != null && note.Checklist.Count > 0;
            if (hasCheck)
            {
                L.ChecklistY = y;
                L.ChecklistLineCounts = new List<int>();
                L.ChecklistItemY = new List<int>();
                L.ChecklistBoxRects = new List<Rectangle>();
                L.ChecklistHitRects = new List<Rectangle>();
                int total = 0;
                int availW = textW - (CHECK_BOX + CHECK_GAP_X);
                if (availW < 20) availW = 20;
                int rowY = y;
                for (int i = 0; i < note.Checklist.Count; i++)
                {
                    int n = CountLines(Check, note.Checklist[i].Text, availW);
                    if (n < 1) n = 1;
                    L.ChecklistLineCounts.Add(n);
                    L.ChecklistItemY.Add(rowY);
                    L.ChecklistBoxRects.Add(new Rectangle(PAD_LR, rowY + CHECK_BOX_MT, CHECK_BOX, CHECK_BOX));
                    L.ChecklistHitRects.Add(new Rectangle(PAD_LR, rowY, textW, CHECK_ROW_H * n));
                    total += n;
                    // same per-item advance as CardRenderer.DrawChecklist (gap after EVERY item)
                    rowY += CHECK_ROW_H * n + CHECK_GAP;
                }
                y += CHECK_ROW_H * total + CHECK_GAP * (note.Checklist.Count - 1);
                y += CHECK_MB;
            }

            L.ContentEnd = y;
            // Spec 4.4 definition 1: NaturalHeight = ContentEnd + FOOT_BLOCK(40) + PAD_BOTTOM(14).
            // The bottom row always reserves its 40px, so the pin can never be clipped away.
            L.NaturalHeight = y + FOOT_BLOCK + PAD_BOTTOM;

            if (clientHeight < 1) clientHeight = 1;
            int footerBottom = clientHeight - PAD_BOTTOM;
            int footerTop = footerBottom - FOOT_BLOCK;
            int lowest = padTop + FOOT_BLOCK;
            if (footerTop < lowest) footerTop = lowest;
            L.FooterTop = footerTop;
            L.FooterContentTop = footerTop + FOOT_BORDER + FOOT_PT;
            L.FooterBottom = L.FooterContentTop + FOOT_H;
            return L;
        }

        /// <summary>
        /// Wraps tag chips into rows, honouring TAG_GAP_X between chips and the container width.
        /// The SAME row split is used by the layout engine and the renderer, so a multi-row tag block
        /// can never make the measured height disagree with the painted block positions.
        /// </summary>
        public static List<List<int>> TagRows(List<string> tags, int textW)
        {
            List<List<int>> rows = new List<List<int>>();
            if (tags == null || tags.Count == 0) return rows;
            List<int> current = new List<int>();
            int x = 0;
            for (int i = 0; i < tags.Count; i++)
            {
                int w = TagChipWidth(tags[i]);
                if (x > 0 && x + TAG_GAP_X + w > textW)
                {
                    rows.Add(current);
                    current = new List<int>();
                    x = w;
                }
                else
                {
                    x = (x == 0) ? w : x + TAG_GAP_X + w;
                }
                current.Add(i);
            }
            if (current.Count > 0) rows.Add(current);
            return rows;
        }

        public static int TagRowCount(List<string> tags, int textW)
        {
            return Math.Max(1, TagRows(tags, textW).Count);
        }

        public static int TagChipWidth(string tag)
        {
            return 8 + Measure(Tag, "#" + tag) + 8;
        }

        /// <summary>
        /// Smallest card height at which a title of <paramref name="titleLines"/> lines stays fully
        /// above the bottom row (so the pinned row never covers text), derived from the spec 4.2
        /// constants plus the 4.3 flow:
        ///   MIN_VISIBLE_TITLE_H(n) = PAD_TOP + (ROW_TOP_H + ROW_TOP_GAP) + (n * TITLE_LH + TITLE_MB)
        ///                            + FOOT_BLOCK + PAD_BOTTOM
        ///                          = 16 + 26 + (22n + 6) + 40 + 14 = 96 + 22n + 6
        /// Examples: n=1 -> 124, n=2 -> 146, n=3 -> 168, n=4 -> 190.
        /// NOTE: the title block is counted ONCE (TITLE_LH + TITLE_MB = 28). Adding another 28 for
        /// "the title block" a second time is the easy double-count and inflates the minimum.
        /// </summary>
        public static int MinHeightForTitleLines(int titleLines)
        {
            if (titleLines < 1) titleLines = 1;
            return PAD_TOP + (ROW_TOP_H + ROW_TOP_GAP) + (titleLines * TITLE_LH + TITLE_MB) + FOOT_BLOCK + PAD_BOTTOM;
        }

        /// <summary>Product floor agreed with the user (180x190 example); MIN_H must never go below it.</summary>
        public const int MIN_H_USER_FLOOR = 190;

        /// <summary>Spec 4.4: natural height of the content flow at a given card width.</summary>
        public static int MeasureNaturalHeight(Note note, int clientWidth)
        {
            return Measure(note, clientWidth, int.MaxValue / 4, PAD_TOP).NaturalHeight;
        }

        // ---------------- due badge (spec 9.5 / P4) ----------------

        public enum DueKind { None, Normal, Today, Overdue }

        public sealed class DueInfo
        {
            public DueKind Kind = DueKind.None;
            /// <summary>Raw dueAt prefix (YYYY-MM-DD); used for the Normal badge text.</summary>
            public string Text = "";

            /// <summary>Badge caption: "today" / "overdue" / the raw date; "" when there is no due date.</summary>
            public string Caption
            {
                get
                {
                    if (Kind == DueKind.None) return "";
                    if (Kind == DueKind.Today) return UiStrings.Today;
                    if (Kind == DueKind.Overdue) return UiStrings.Overdue;
                    return Text;
                }
            }
        }

        public static string TodayUtc()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Faithful port of dateLabel()/today() from demo/app.js@85CA714C.</summary>
        public static DueInfo DateLabel(string dueAt)
        {
            DueInfo info = new DueInfo();
            if (string.IsNullOrEmpty(dueAt)) { info.Kind = DueKind.None; return info; }
            if (dueAt.Length < 10) { info.Kind = DueKind.Normal; info.Text = dueAt; return info; }

            string due = dueAt.Substring(0, 10);
            string today;
#pragma warning disable 0162
            // The disabled branch is the deliberate spec-13.1 P4 alternative switch (USE_LOCAL_DATE).
            if (UseLocalDate)
            {
                today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                today = TodayUtc();
            }
#pragma warning restore 0162
            info.Text = due;

            if (string.Equals(due, today, StringComparison.Ordinal))
            {
                info.Kind = DueKind.Today;
            }
            else if (string.CompareOrdinal(due, today) < 0)
            {
                info.Kind = DueKind.Overdue;
            }
            else
            {
                info.Kind = DueKind.Normal;
            }
            return info;
        }
    }
}

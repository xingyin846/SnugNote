// S1 sticker: the full card renderer (spec sections 3.x - 4.x).
// Shared by the live card window (StickerForm.OnPaint) and the offscreen PNG writer used for
// self-verification. Drawing happens in logical (unscaled) pixel space; callers may apply a scale
// transform for crisper previews.
// Pure ASCII source.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TieTieSticker
{
    internal static class CardRenderer
    {
        public static void Render(Graphics g, Note note, int w, int h, bool floating)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle full = new Rectangle(0, 0, Math.Max(1, w), Math.Max(1, h));
            Typo.Layout L = Typo.Measure(note, w, h, Typo.PAD_TOP);

            Color bg = Palette.NoteBg(note.Color);
            Color edge = Palette.NoteEdge(note.Color);

            using (GraphicsPath path = Geometry.RoundedRect(full, Typo.RADIUS))
            {
                using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, path);
                using (Pen p = new Pen(edge, 1f)) g.DrawPath(p, path);

                GraphicsState st = g.Save();
                g.SetClip(path, CombineMode.Intersect);
                g.SetClip(new Rectangle(0, 0, w, Math.Max(0, L.FooterTop)), CombineMode.Intersect);

                DrawBand(g, edge, w);
                DrawTopRow(g, note, L, w);
                DrawTitle(g, note, L);
                DrawBody(g, note, L);
                DrawTags(g, note, L);
                DrawChecklist(g, note, L);
                g.Restore(st);
            }

            DrawFooter(g, note, L, bg, edge, w, h, floating);
        }

        private static void DrawBand(Graphics g, Color edge, int w)
        {
            using (SolidBrush b = new SolidBrush(Palette.Alpha(edge, Typo.BAND_ALPHA)))
            {
                g.FillRectangle(b, 0, 0, w, Typo.BAND_H);
            }
        }

        private static void DrawTopRow(Graphics g, Note note, Typo.Layout L, int w)
        {
            Typo.DueInfo due = Typo.DateLabel(note.DueAt);
            int x = Typo.PAD_LR;
            int rowH = Typo.ROW_TOP_H;

            if (due.Kind != Typo.DueKind.None)
            {
                string caption = due.Caption;
                int textW = Typo.Measure(Typo.Due, caption);
                int clockW = 12;   // hand-drawn clock (8) + 4px gap - replaces the emoji prefix
                int badgeW = 8 + clockW + textW + 8;
                Rectangle badge = new Rectangle(x, L.RowTopY, badgeW, rowH);
                Color fg = Palette.TextSoft;
                Color badgeBg = Palette.Surface;
                if (due.Kind == Typo.DueKind.Today) { fg = Typo.DueTodayFg; badgeBg = Typo.DueTodayBg; }
                if (due.Kind == Typo.DueKind.Overdue) { fg = Typo.DueOverdueFg; badgeBg = Typo.DueOverdueBg; }

                using (SolidBrush b = new SolidBrush(badgeBg))
                using (GraphicsPath p = Geometry.RoundedRect(badge, rowH / 2))
                {
                    g.FillPath(b, p);
                }
                Geometry.DrawClock(g, new Point(badge.X + 8 + 4, badge.Y + rowH / 2), fg);
                TextRenderer.DrawText(g, caption, Typo.Due,
                    new Rectangle(badge.X + 8 + clockW, badge.Y, textW + 4, rowH), fg,
                    TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }

            // spec v16 (user ruling 2026-09-21): the pin glyph between the due badge and the close box is
            // GONE - "直接删去❌左面的置顶图钉标志". It had carried two different meanings over its life
            // (v11: "this note is pinned in the web app", read-only; v15: "this card floats on top"),
            // and the user reads neither of them off that corner. The floating state is expressed by the
            // footer pin BUTTON alone (accent while floating), which is also the control that toggles it.
            // Assertions v16-top-flag-removed-* guard the empty box; the pink-dot box (234,39,10,10) of
            // v11.2 stays empty as well (see .tools/s2-pinkdot-check.ps1).

            // spec v11 3.1/3.3: the close (X) box - always visible (pinned or not), self-drawn,
            // painted AFTER the row content and inside the rounded clip.
            Geometry.DrawClose(g, new Rectangle(Typo.CloseLeft(w), Typo.CLOSE_TOP, Typo.CLOSE_BOX, Typo.CLOSE_BOX), Typo.CloseFg);
        }

        private static void DrawTitle(Graphics g, Note note, Typo.Layout L)
        {
            Color c = Palette.Text;
            bool done = note.Done;
            if (done) c = Palette.Alpha(Palette.Text, Typo.DONE_ALPHA);
            int y = L.TitleY;
            for (int i = 0; i < L.TitleLines.Count; i++)
            {
                string line = L.TitleLines[i];
                if (line.Length > 0)
                {
                    TextRenderer.DrawText(g, line, Typo.Title,
                        new Rectangle(Typo.PAD_LR, y, L.MaxTextWidth + 2, Typo.TITLE_LH), c,
                        TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine);
                    if (done)
                    {
                        int tw = Typo.Measure(Typo.Title, line);
                        int ly = y + Typo.TITLE_LH / 2 + 2;
                        using (Pen p = new Pen(c, 1f))
                        {
                            g.DrawLine(p, Typo.PAD_LR, ly, Typo.PAD_LR + tw, ly);
                        }
                    }
                }
                y += Typo.TITLE_LH;
            }
        }

        private static void DrawBody(Graphics g, Note note, Typo.Layout L)
        {
            if (L.BodyLines == null) return;
            Color c = Palette.Alpha(Palette.Text, Typo.BODY_ALPHA);
            int y = L.BodyY;
            for (int i = 0; i < L.BodyLines.Count; i++)
            {
                string line = L.BodyLines[i];
                if (line.Length > 0)
                {
                    TextRenderer.DrawText(g, line, Typo.Body,
                        new Rectangle(Typo.PAD_LR, y, L.MaxTextWidth + 2, Typo.BODY_LH), c,
                        TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine);
                }
                y += Typo.BODY_LH;
            }
        }

        private static void DrawTags(Graphics g, Note note, Typo.Layout L)
        {
            if (note.Tags == null || note.Tags.Count == 0) return;
            // TAG_CHIP_MODE = EDGE_TINT (spec 3.5 / P3): --note-edge @ 40%, single colour, no border, no dot.
            Color chipBg = Palette.Alpha(Palette.NoteEdge(note.Color), Typo.TAG_ALPHA);
            Color chipFg = Palette.Text;

            int x = Typo.PAD_LR;
            // Row split shared with the layout engine (spec 4.3: TAG_GAP_Y between rows), so a
            // multi-row tag block can never push the measured height out of sync with the painting.
            List<List<int>> tagRows = Typo.TagRows(note.Tags, L.MaxTextWidth);
            for (int r = 0; r < tagRows.Count; r++)
            {
                x = Typo.PAD_LR;
                int y = L.TagsY + r * (Typo.TAG_H + Typo.TAG_GAP_Y);
                for (int k = 0; k < tagRows[r].Count; k++)
                {
                    int i = tagRows[r][k];
                    int cw = Typo.TagChipWidth(note.Tags[i]);
                    Rectangle chip = new Rectangle(x, y, cw, Typo.TAG_H);
                    using (SolidBrush b = new SolidBrush(chipBg))
                    using (GraphicsPath p = Geometry.RoundedRect(chip, Typo.TAG_H / 2))
                    {
                        g.FillPath(b, p);
                    }
                    TextRenderer.DrawText(g, "#" + note.Tags[i], Typo.Tag,
                        new Rectangle(chip.X + 8, chip.Y + 1, Math.Max(4, cw - 16), Typo.TAG_H), chipFg,
                        TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine);
                    x += cw + Typo.TAG_GAP_X;
                }
            }
        }

        private static void DrawChecklist(Graphics g, Note note, Typo.Layout L)
        {
            if (L.ChecklistLineCounts == null) return;
            int y = L.ChecklistY;
            int availW = L.MaxTextWidth - (Typo.CHECK_BOX + Typo.CHECK_GAP_X);
            if (availW < 20) availW = 20;
            int textX = Typo.PAD_LR + Typo.CHECK_BOX + Typo.CHECK_GAP_X;

            for (int i = 0; i < note.Checklist.Count; i++)
            {
                CheckItem item = note.Checklist[i];
                int lines = L.ChecklistLineCounts[i];

                // S2: the box rect and the item's top y come from the layout engine, which is the same
                // object Typo.ChecklistItemAt() hit-tests against - painting and clicking cannot drift.
                bool laid = L.ChecklistItemY != null && L.ChecklistBoxRects != null
                    && i < L.ChecklistItemY.Count && i < L.ChecklistBoxRects.Count;
                int itemY = laid ? L.ChecklistItemY[i] : y;
                Rectangle box = laid ? L.ChecklistBoxRects[i]
                    : new Rectangle(Typo.PAD_LR, itemY + Typo.CHECK_BOX_MT, Typo.CHECK_BOX, Typo.CHECK_BOX);
                DrawCheckBox(g, box, item.Done);

                List<string> wrapped = Typo.WrapLine(Typo.Check, item.Text, availW);
                Color c = item.Done ? Palette.Alpha(Palette.Text, 0.5) : Palette.Text;
                int ty = itemY;
                for (int k = 0; k < wrapped.Count; k++)
                {
                    string line = wrapped[k];
                    if (line.Length > 0)
                    {
                        TextRenderer.DrawText(g, line, Typo.Check,
                            new Rectangle(textX, ty + 2, availW + 2, Typo.CHECK_ROW_H), c,
                            TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine);
                        if (item.Done)
                        {
                            int tw = Typo.Measure(Typo.Check, line);
                            int ly = ty + 2 + Typo.CHECK_ROW_H / 2;
                            using (Pen p = new Pen(c, 1f))
                            {
                                g.DrawLine(p, textX, ly, textX + tw, ly);
                            }
                        }
                    }
                    ty += Typo.CHECK_ROW_H;
                }
                y = itemY + Typo.CHECK_ROW_H * lines + Typo.CHECK_GAP;
            }
        }

        private static void DrawCheckBox(Graphics g, Rectangle box, bool done)
        {
            if (done)
            {
                using (SolidBrush b = new SolidBrush(Palette.Accent))
                using (GraphicsPath p = Geometry.RoundedRect(box, 2))
                {
                    g.FillPath(b, p);
                }
                using (Pen pen = new Pen(Color.White, 2f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    float x = box.X;
                    float y = box.Y;
                    g.DrawLines(pen, new PointF[]
                    {
                        new PointF(x + 3f, y + 6.5f),
                        new PointF(x + 5.5f, y + 9f),
                        new PointF(x + 10f, y + 3.5f)
                    });
                }
            }
            else
            {
                using (SolidBrush b = new SolidBrush(Color.White))
                using (GraphicsPath p = Geometry.RoundedRect(box, 2))
                {
                    g.FillPath(b, p);
                    using (Pen pen = new Pen(Typo.CheckboxBorder, 1f)) g.DrawPath(pen, p);
                }
            }
        }

        private static void DrawFooter(Graphics g, Note note, Typo.Layout L, Color bg, Color edge, int w, int h, bool floating)
        {
            using (SolidBrush b = new SolidBrush(bg))
            {
                g.FillRectangle(b, 0, L.FooterTop, w, Math.Max(0, h - L.FooterTop));
            }
            using (Pen pen = new Pen(Palette.Alpha(edge, Typo.FOOT_BORDER_ALPHA), 1f))
            {
                pen.DashStyle = DashStyle.Dash;
                g.DrawLine(pen, Typo.PAD_LR, L.FooterTop, w - Typo.PAD_LR, L.FooterTop);
            }

            Rectangle pinBtn = new Rectangle(Typo.PAD_LR, L.FooterContentTop, Typo.PIN_BTN, Typo.PIN_BTN);
            Color pinColor = floating ? Palette.Accent : Palette.Alpha(Palette.Text, Typo.PIN_OFF_ALPHA);
            Geometry.DrawPin(g, pinBtn, pinColor);

            // Spec v11.2 3.11: the floating "is floating" pink dot in the top-right corner (former spec
            // 7.3.1) is REMOVED, and since spec v16 the pin GLYPH next to the close box is removed too.
            // Floating is expressed in exactly ONE place: this footer pin button (accent while floating -
            // it is also the toggle). Assertion floating-still-expressed-by-pin (#23) is its positive
            // control; v16-top-flag-removed-* guard that the top-right corner stays ink-free.
        }
    }
}

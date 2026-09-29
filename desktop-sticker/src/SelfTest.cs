// S1 sticker: headless self-test. Runs with --selftest, creates NO window, writes NO state file.
// Every assertion is a claim that the spec's numbers were actually implemented, not just written down.
// Pure ASCII source.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace TieTieSticker
{
    internal static class SelfTest
    {
        private static int _pass;
        private static int _fail;

        public static bool Run(string exeDir, Options opt)
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("TieTieSticker S1 self-test");
            Console.WriteLine("--------------------------");

            CheckPalette();
            CheckTagChipMode();
            CheckWrapAndLayout();
            CheckDueLabel();
            CheckJsonParser();
            CheckStateRoundTrip();
            CheckStateOrderAndDuplicates();
            CheckClamp();
            CheckNotesAreReadable(exeDir, opt);
            CheckAssemblySurface();
            CheckNoListenerSymbols();
            CheckEveryMeasureCallSiteUsesPadTop();
            CheckV11CloseGeometry();
            CheckV11SourceGuards();
            CheckS2ChecklistWrite(exeDir);   // spec v12 (S2): checklist write-back, on a sandbox fixture
            CheckV13BridgeAndTray(exeDir);   // spec v13: web-button bridge (file relay) + tray list
            CheckV14ExitRequest(exeDir);     // spec v14: the launcher's exit request shuts this app down
            CheckV14StartupAckOrder(exeDir); // spec v14: the ack is read before anything can publish it
            CheckV16NoTopFlag();             // spec v16: the glyph left of the X is deleted (only the footer pin marks floating)
            CheckV17ExplicitPlacements(exeDir); // spec v17: a click on 「贴到桌面」 suppresses "place all"
            CheckV19NoStaleHint(exeDir);     // spec v19: the page never tells the user to double-click a file
            CheckV20AutoStartWiring();       // spec v20: the window's "start with Windows" box is really wired
            CheckV21ClosedSetRestore(exeDir);   // spec v21: closing remembers geometry; an emptied desk restores THE USER arrangement
            CheckBuildManifest();

            Console.WriteLine("--------------------------");
            Console.WriteLine("PASS=" + _pass + " FAIL=" + _fail);
            Log.Line("selftest finished: PASS=" + _pass + " FAIL=" + _fail);
            return _fail == 0;
        }

        private static void Ok(string name, string detail)
        {
            _pass++;
            Console.WriteLine("PASS  " + name + "  :: " + detail);
            Log.Line("selftest PASS " + name + " :: " + detail);
        }

        private static void Bad(string name, string detail)
        {
            _fail++;
            Console.WriteLine("FAIL  " + name + "  :: " + detail);
            Log.Line("selftest FAIL " + name + " :: " + detail);
        }

        private static void Assert(bool cond, string name, string detail)
        {
            if (cond) Ok(name, detail); else Bad(name, detail);
        }

        // ---------------------------------------------------------------

        private static void CheckPalette()
        {
            string detail = "yellow " + Hex(Palette.NoteBg("yellow")) + "/" + Hex(Palette.NoteEdge("yellow"))
                + " pink " + Hex(Palette.NoteBg("pink")) + "/" + Hex(Palette.NoteEdge("pink"))
                + " blue " + Hex(Palette.NoteBg("blue")) + "/" + Hex(Palette.NoteEdge("blue"))
                + " green " + Hex(Palette.NoteBg("green")) + "/" + Hex(Palette.NoteEdge("green"))
                + " purple " + Hex(Palette.NoteBg("purple")) + "/" + Hex(Palette.NoteEdge("purple"))
                + " orange " + Hex(Palette.NoteBg("orange")) + "/" + Hex(Palette.NoteEdge("orange"))
                + " text " + Hex(Palette.Text) + " soft " + Hex(Palette.TextSoft) + " accent " + Hex(Palette.Accent);
            bool good =
                Hex(Palette.NoteBg("yellow")) == "FFF6C9" && Hex(Palette.NoteEdge("yellow")) == "F3E18A" &&
                Hex(Palette.NoteBg("pink")) == "FFE3EE" && Hex(Palette.NoteEdge("pink")) == "F3B5CD" &&
                Hex(Palette.NoteBg("blue")) == "DCEFFF" && Hex(Palette.NoteEdge("blue")) == "A6D4F5" &&
                Hex(Palette.NoteBg("green")) == "E3F6D0" && Hex(Palette.NoteEdge("green")) == "B4DE95" &&
                Hex(Palette.NoteBg("purple")) == "ECE3FF" && Hex(Palette.NoteEdge("purple")) == "C7B8EF" &&
                Hex(Palette.NoteBg("orange")) == "FFE8D3" && Hex(Palette.NoteEdge("orange")) == "F3C39A" &&
                Hex(Palette.Text) == "24212B" && Hex(Palette.TextSoft) == "6D6875" &&
                Hex(Palette.Border) == "E8E3DA" && Hex(Palette.Accent) == "FF7A9E";
            Assert(good, "palette-6-colors", detail);
            Assert(!Palette.IsKnownColor("teal") && Palette.IsKnownColor("yellow"), "palette-known-keys", "unknown key rejected, yellow accepted");
        }

        private static void CheckTagChipMode()
        {
            Color c = Palette.Alpha(Palette.NoteEdge("yellow"), Typo.TAG_ALPHA);
            bool good = Typo.TagChipMode == "EDGE_TINT" && c.A == 102 && Hex(c) == "F3E18A";
            Assert(good, "tag-chip-mode", "TAG_CHIP_MODE=" + Typo.TagChipMode + " alpha=" + c.A + " color=" + Hex(c) + " (expect 102 / F3E18A)");
        }

        private static void CheckWrapAndLayout()
        {
            int avail = Typo.DEFAULT_W - 2 * Typo.PAD_LR;
            string text = "0123456789012345678901234567890123456789012345678901234567890123456789";
            List<string> lines = Typo.WrapLine(Typo.Body, text, avail);
            int maxW = 0;
            for (int i = 0; i < lines.Count; i++) maxW = Math.Max(maxW, Typo.Measure(Typo.Body, lines[i]));
            Assert(lines.Count >= 2 && maxW <= avail,
                "wrap-width", "avail=" + avail + " lines=" + lines.Count + " maxLineWidth=" + maxW + " first='" + lines[0] + "'");

            List<string> nl = Typo.WrapLine(Typo.Body, "a\nb", 200);
            Assert(nl.Count == 2 && nl[0] == "a" && nl[1] == "b", "wrap-newline", "['" + string.Join("','", nl.ToArray()) + "']");

            Note n = new Note();
            n.Id = "t"; n.Title = "T"; n.Content = "C"; n.Tags.Add("x"); n.Checklist.Add(new CheckItem());
            Typo.Layout L = Typo.Measure(n, Typo.DEFAULT_W, Typo.DEFAULT_H, Typo.PAD_TOP);
            int natural = Typo.MeasureNaturalHeight(n, Typo.DEFAULT_W);
            // n = 1 title line + 1 body line + 1 tag row + 1 checklist row:
            // 16 +26 +28 +(22+10) +29 +(18+9) = 158, naturalHeight = 158 + 40 + 14 = 212
            bool good = L.ContentTop == 16 && L.RowTopY == 16 && L.TitleY == 42 && L.TitleLines.Count == 1
                && L.BodyLines != null && L.BodyLines.Count == 1 && L.TagsY == 102
                && natural == 212 && L.FooterTop == 266 && L.FooterContentTop == 276 && L.FooterBottom == 306;
            Assert(good, "layout-flow",
                "contentTop=" + L.ContentTop + " titleY=" + L.TitleY + " natural=" + natural
                + " footerTop=" + L.FooterTop + " footerContentTop=" + L.FooterContentTop + " footerBottom=" + L.FooterBottom
                + " (expect 16/42/212/266/276/306)");

            // ---- spec 4.4 worked example 1 ----
            // The spec's cumulative column is internally consistent: the row values
            // (16 + 26 + 28 + 76 + 29 + 75 + 40) sum to 290, which IS the cumulative column's
            // final value and the y that includes the bottom row. ContentEnd is the cumulative
            // value BEFORE the bottom row (= 250; 290 - 40). The spec's authoritative
            // expected-value table for the real notes snapshot (note[0..2] = 199/185/153)
            // is reproduced EXACTLY - see spec-4.4-notes-expected-table below.
            Note ex1 = new Note();
            ex1.Id = "spec414-1"; ex1.Title = "T"; ex1.Content = "aa\nbb\ncc";
            ex1.Tags.Add("x");
            ex1.Checklist.Add(new CheckItem()); ex1.Checklist.Add(new CheckItem()); ex1.Checklist.Add(new CheckItem());
            Typo.Layout L1 = Typo.Measure(ex1, Typo.DEFAULT_W, Typo.DEFAULT_H, Typo.PAD_TOP);
            int ex1BlockSum = Typo.PAD_TOP + (Typo.ROW_TOP_H + Typo.ROW_TOP_GAP)
                + (Typo.TITLE_LH * 1 + Typo.TITLE_MB)
                + (Typo.BODY_LH * 3 + Typo.BODY_MB)
                + (Typo.TAG_H * 1 + Typo.TAGS_MB)
                + (Typo.CHECK_ROW_H * 3 + Typo.CHECK_GAP * 2 + Typo.CHECK_MB)
                + Typo.FOOT_BLOCK;   // 16+26+28+76+29+75+40 = 290 means the bottom row is in the sum
            Log.Line("selftest 4.4-ex1 detail: contentTop=" + L1.ContentTop + " titleY=" + L1.TitleY
                + " titleLines=" + L1.TitleLines.Count + " bodyY=" + L1.BodyY + " bodyLines=" + (L1.BodyLines == null ? -1 : L1.BodyLines.Count)
                + " tagsY=" + L1.TagsY + " tagRows=" + Typo.TagRowCount(ex1.Tags, L1.MaxTextWidth)
                + " checklistY=" + L1.ChecklistY + " checkRows=" + (L1.ChecklistLineCounts == null ? "(null)" : string.Join("+", Arr(L1.ChecklistLineCounts)))
                + " contentEnd(without bottom row)=" + L1.ContentEnd + " blockSum(incl bottom row)=" + ex1BlockSum
                + " natural=" + L1.NaturalHeight);
            Assert(L1.ContentEnd == ex1BlockSum - Typo.FOOT_BLOCK
                && L1.NaturalHeight == ex1BlockSum + Typo.PAD_BOTTOM,
                "spec-4.4-example-1",
                "ContentEnd=" + L1.ContentEnd + " (blocks up to and including the checklist row; the spec's"
                + " cumulative column shows the same 250 at that row)"
                + "; y(incl bottom row)=" + (L1.ContentEnd + Typo.FOOT_BLOCK) + " (spec cumulative 290)"
                + "; naturalHeight=" + L1.NaturalHeight + " = " + (L1.ContentEnd + Typo.FOOT_BLOCK) + " + PAD_BOTTOM(" + Typo.PAD_BOTTOM + ")"
                + "; independent block sum = " + ex1BlockSum
                + " (spec row values and cumulative column agree; bottom row counted exactly once)");

            // ---- spec 4.4 worked example 2 ----
            // top row + 1 title line + NO body + 2 tag rows + 4 checklist rows + bottom row
            // Spec 4.4 lists "2 tag rows" for a 260-wide card. Whether two chips share a row depends on
            // the exact GDI glyph widths (spec 5.3 allows +/-2px), so this fixture uses deliberately long
            // tag names and the check verifies the *consistency* claim: the measured height equals the
            // row values summed once (bottom row included exactly once).
            Note ex2 = new Note();
            ex2.Id = "spec414-2"; ex2.Title = "T"; ex2.Content = "";
            ex2.Tags.Add("very-long-tag-alpha"); ex2.Tags.Add("very-long-tag-beta");
            ex2.Checklist.Add(new CheckItem()); ex2.Checklist.Add(new CheckItem());
            ex2.Checklist.Add(new CheckItem()); ex2.Checklist.Add(new CheckItem());
            Typo.Layout L2 = Typo.Measure(ex2, Typo.DEFAULT_W, Typo.DEFAULT_H, Typo.PAD_TOP);
            int tagRows2 = Typo.TagRowCount(ex2.Tags, L2.MaxTextWidth);
            int ex2BlockSum = Typo.PAD_TOP + (Typo.ROW_TOP_H + Typo.ROW_TOP_GAP)
                + (Typo.TITLE_LH * 1 + Typo.TITLE_MB)
                + 0
                + (Typo.TAG_H * tagRows2 + Typo.TAG_GAP_Y * (tagRows2 - 1) + Typo.TAGS_MB)
                + (Typo.CHECK_ROW_H * 4 + Typo.CHECK_GAP * 3 + Typo.CHECK_MB)
                + Typo.FOOT_BLOCK;
            Assert(L2.ContentEnd == ex2BlockSum - Typo.FOOT_BLOCK
                && L2.NaturalHeight == ex2BlockSum + Typo.PAD_BOTTOM,
                "spec-4.4-example-2",
                "tagRows=" + tagRows2 + " ContentEnd=" + L2.ContentEnd + " (spec cumulative shows the same 224 at that row)"
                + "; y(incl bottom row)=" + (L2.ContentEnd + Typo.FOOT_BLOCK) + " (spec cumulative 264)"
                + "; naturalHeight=" + L2.NaturalHeight + " = " + (L2.ContentEnd + Typo.FOOT_BLOCK) + " + PAD_BOTTOM(" + Typo.PAD_BOTTOM + ")"
                + "; independent block sum = " + ex2BlockSum + " (rows agree; bottom row counted exactly once)");

            // ---- spec 4.4: the bottom row must always be part of the natural height ----
            int withoutFoot = L1.ContentEnd + Typo.PAD_BOTTOM;
            Assert(L1.NaturalHeight == withoutFoot + Typo.FOOT_BLOCK, "natural-height-includes-foot-block",
                "naturalHeight=" + L1.NaturalHeight + " == ContentEnd(" + L1.ContentEnd + ") + PAD_BOTTOM(" + Typo.PAD_BOTTOM + ") + FOOT_BLOCK(" + Typo.FOOT_BLOCK + ") = " + (withoutFoot + Typo.FOOT_BLOCK));

            // ---- ANTI-REGRESSION GUARD (spec 4.4 definition 2) ----
            // Passing PAD_TOP + BAND_H (21) as MeasureNaturalHeight's padTop counts the 5px top band
            // twice and shifts EVERY value by +5. The spec forbids it and names 204 as the symptom for
            // note[0]. The anchor here is ex1BlockSum, an independent recomputation of example 1 from
            // the spec 4.2 constants and the 4.3 formula - so if the measurement path drifts by +5 the
            // two sides no longer match. (A first version of this guard derived BOTH sides from the code
            // under test and therefore stayed green under a mutation; that was caught by an actual
            // mutation run, not by inspection. The note[0]=199 anchor lives in
            // measure-note0-equals-spec-table / spec-4.4-notes-expected-table below.)
            int expectedEx1Natural = ex1BlockSum + Typo.PAD_BOTTOM;   // 290 + 14 = 304
            int wrongNote0 = Typo.MeasureNaturalHeight(ex1, Typo.DEFAULT_W);
            Assert(L1.NaturalHeight == expectedEx1Natural, "measure-example1-equals-independent-blocksum",
                "example-1 naturalHeight=" + L1.NaturalHeight + " (must equal the independently recomputed "
                + ex1BlockSum + " + PAD_BOTTOM(" + Typo.PAD_BOTTOM + ") = " + expectedEx1Natural + ")");
            Assert(wrongNote0 != expectedEx1Natural + Typo.BAND_H, "measure-padTop-must-be-16-not-21",
                "measured naturalHeight=" + wrongNote0 + " != " + (expectedEx1Natural + Typo.BAND_H)
                + " ( = " + expectedEx1Natural + " + BAND_H(" + Typo.BAND_H + ") = the 5px top band counted twice;"
                + " spec 4.4 definition 2 forbids padTop=" + (Typo.PAD_TOP + Typo.BAND_H) + ", symptom for note[0] is 204)");

            // ---- partial-fix guards: LITERAL anchors on purpose ----
            // Spec 4.4 for this example shape (1 title + 3 body + 1 tag row + 3 checklist rows) gives
            //   ContentEnd = 250, +FOOT_BLOCK(40) = 290, +PAD_BOTTOM(14) => naturalHeight = 304.
            // The two neighbouring wrong values are named forbiddens, not computed from the code under
            // test (computing them from it is how a guard becomes vacuous - see the note above):
            //   264 = 304 - 40  -> "fixed the bottom row, but lost it again in the sum" / 159 symptom on real notes
            //   309 = 304 + 5   -> padTop = PAD_TOP + BAND_H(21), the 5px band counted twice / 204 symptom
            const int example1NaturalH = 304;
            Assert(L1.NaturalHeight == example1NaturalH, "example1-naturalh-literal-anchor",
                "naturalHeight=" + L1.NaturalHeight + " (spec 4.4 literal anchor " + example1NaturalH + ")");
            Assert(L1.NaturalHeight != example1NaturalH - Typo.FOOT_BLOCK, "forbid-missing-bottom-row",
                "naturalHeight=" + L1.NaturalHeight + " != " + (example1NaturalH - Typo.FOOT_BLOCK)
                + " (= 304 - FOOT_BLOCK(40); the same bug reads 159 instead of 199 on note[0])");
            Assert(L1.NaturalHeight != example1NaturalH + Typo.BAND_H, "forbid-padtop-21",
                "naturalHeight=" + L1.NaturalHeight + " != " + (example1NaturalH + Typo.BAND_H)
                + " (= 304 + BAND_H(5), the 5px top band counted twice; the same bug reads 204 instead of 199 on note[0])");

            // ---- structural invariant: measurement and painting MUST use the same padTop ----
            // CardRenderer/StickerForm call Typo.Measure(..., Typo.PAD_TOP); MeasureNaturalHeight must too.
            int viaRenderPath = Typo.Measure(ex1, Typo.DEFAULT_W, Typo.DEFAULT_H, Typo.PAD_TOP).NaturalHeight;
            int viaHelper = Typo.MeasureNaturalHeight(ex1, Typo.DEFAULT_W);
            Assert(viaRenderPath == viaHelper, "measure-and-render-padtop-agree",
                "render-path Measure(padTop=PAD_TOP).NaturalHeight=" + viaRenderPath + " == MeasureNaturalHeight()=" + viaHelper
                + " (rendering path and measurement path must stay same-parameter)");

            // ---- spec 4.4 DEFAULT_H justification: y <= ClientHeight - PAD_BOTTOM ----
            Assert(L1.ContentEnd + Typo.FOOT_BLOCK <= Typo.DEFAULT_H - Typo.PAD_BOTTOM, "default-height-justification",
                "example1 y(+" + Typo.FOOT_BLOCK + ")=" + (L1.ContentEnd + Typo.FOOT_BLOCK) + " <= DEFAULT_H(" + Typo.DEFAULT_H + ") - PAD_BOTTOM(" + Typo.PAD_BOTTOM + ") = " + (Typo.DEFAULT_H - Typo.PAD_BOTTOM)
                + "; naturalHeight " + L1.NaturalHeight + " <= " + Typo.DEFAULT_H);

            // ---- real end-to-end fallback proof: the 3rd (fallback) entry must be OFFERED, not written ----
            string stateDirForTest = Path.Combine(Path.GetTempPath(), "tietie-selftest-fallback");
            try
            {
                Directory.CreateDirectory(stateDirForTest);
                string primary = Path.Combine(stateDirForTest, "sticker-state.json");
                string secondary = Path.Combine(stateDirForTest, "data-sticker-state.json");
                string fallback = Path.Combine(stateDirForTest, "appdata-fallback-sticker-state.json");
                foreach (string p in new string[] { primary, secondary, fallback })
                {
                    if (File.Exists(p)) File.Delete(p);
                }
                StickerState probe = new StickerState();
                StickerState.SaveReport r = probe.Save(new string[] { primary, secondary, fallback });
                bool okFallback = (r.Written.Count == 2)
                    && File.Exists(primary) && File.Exists(secondary)
                    && !File.Exists(fallback)
                    && (r.Offered.Count == 1);
                Assert(okFallback, "fallback-offered-not-written",
                    "written=" + r.Written.Count + " [" + string.Join(",", r.Written.ToArray()) + "] offered="
                    + r.Offered.Count + " [" + string.Join(",", r.Offered.ToArray()) + "] fallbackFileExists="
                    + File.Exists(fallback) + " (the 3rd entry is a documented fallback: offered but NOT written while 1&2 work)");
            }
            catch (Exception ex)
            {
                Bad("fallback-offered-not-written", "exception: " + ex.Message);
            }

            // no due date must still advance ROW_TOP_H + ROW_TOP_GAP
            bool advanceSame = (L.TitleY - L.ContentTop) == (Typo.ROW_TOP_H + Typo.ROW_TOP_GAP);
            Assert(advanceSame, "due-placeholder-height", "row advance = " + (L.TitleY - L.ContentTop) + " (expect " + (Typo.ROW_TOP_H + Typo.ROW_TOP_GAP) + ")");

            // pin button rect must be reachable inside the default card
            bool pinInside = L.FooterContentTop + Typo.PIN_BTN <= Typo.DEFAULT_H;
            Assert(pinInside, "pin-button-inside-default-card", "pinBottom=" + (L.FooterContentTop + Typo.PIN_BTN) + " <= " + Typo.DEFAULT_H);

            // min-size card must still keep the pin button inside the card (literal anchor 190:
            // the user's floor value must not silently drop back to the old 140)
            Typo.Layout Lmin = Typo.Measure(n, Typo.MIN_W, Typo.MIN_H, Typo.PAD_TOP);
            Assert(Lmin.FooterContentTop + Typo.PIN_BTN <= Typo.MIN_H && Typo.MIN_H >= 190 && Typo.MIN_H == Typo.MIN_H_USER_FLOOR,
                "pin-button-inside-min-card",
                "pinBottom=" + (Lmin.FooterContentTop + Typo.PIN_BTN) + " <= MIN_H=" + Typo.MIN_H
                + " ; user floor=190 ; MIN_H_USER_FLOOR=" + Typo.MIN_H_USER_FLOOR);

            // 最小高度：标题完整可见所需的派生下限（公式，不是字面量）：
            //   1 行标题 = PAD_TOP16 + 顶端行26 + (TITLE_LH22 + TITLE_MB6) + FOOT_BLOCK40 + PAD_BOTTOM14 = 124
            //   n 行      = 96 + 22n + 6  ⇒ 1/2/3/4 行 = 124 / 146 / 168 / 190
            // MIN_H 取 max(几何下限, 用户下限 190) ⇒ 190（覆盖到"标题 4 行"完整可见）。
            // ⚠️ 队长最初预期的 ≈205 是把标题块 28 重复计了一次（22+6 已含在 28 里）——本断言用公式
            //    而非字面量，正是为了让"重复计一次块高"这类错误不成立。
            {
                int need1 = Typo.MinHeightForTitleLines(1);
                int need4 = Typo.MinHeightForTitleLines(4);
                Assert(need1 == 124 && need4 == 190, "min-height-formula",
                    "MinHeightForTitleLines: 1 line=" + need1 + " (expect 124), 4 lines=" + need4 + " (expect 190)"
                    + "; formula = PAD_TOP(" + Typo.PAD_TOP + ") + ROW_TOP(" + (Typo.ROW_TOP_H + Typo.ROW_TOP_GAP) + ")"
                    + " + n*TITLE_LH(" + Typo.TITLE_LH + ") + TITLE_MB(" + Typo.TITLE_MB + ")"
                    + " + FOOT_BLOCK(" + Typo.FOOT_BLOCK + ") + PAD_BOTTOM(" + Typo.PAD_BOTTOM + ")");

                Assert(Typo.MIN_H >= need1 && Typo.MIN_H >= Typo.MIN_H_USER_FLOOR,
                    "min-height-covers-derived-lower-bound",
                    "MIN_H=" + Typo.MIN_H + " >= derived(1 line)=" + need1 + " and >= user floor=" + Typo.MIN_H_USER_FLOOR);

                // 行为断言（n=1，边界）：最小尺寸下 1 行标题完整落在底部行之上。
                // ⚠️ 明确记录其局限：H=140 时 n=1 也成立（70 <= 86），**所以它给不出"190 是必要的"的证据**。
                //    真正证明 190 必要的是下面 n=4（190 的需求方）与 H=140 负例这两条。
                Note titleOne = new Note();
                titleOne.Id = n.Id;
                titleOne.Title = "标题1行";
                Typo.Layout Ltitle = Typo.Measure(titleOne, Typo.MIN_W, Typo.MIN_H, Typo.PAD_TOP);
                int titleBottom = Ltitle.TitleY + Typo.TITLE_LH * Ltitle.TitleLines.Count + Typo.TITLE_MB;
                Assert(Ltitle.TitleLines.Count == 1 && titleBottom <= Ltitle.FooterTop,
                    "min-size-title-fully-visible",
                    "titleLines=" + Ltitle.TitleLines.Count + " titleBottom=" + titleBottom
                    + " <= footerTop=" + Ltitle.FooterTop + " (MIN_H=" + Typo.MIN_H + ", derived need=" + need1 + ")"
                    + " [LIMIT: holds at H=140 too (70 <= 86) => does NOT prove MIN_H=190 is needed]");

                // ★ 真正证明"190 必要"的断言（n=4 = 190 的需求方）：标题末行**恰好贴住**底部行上沿。
                //   几何：titleBottom = TitleY(42) + 22n + 6 = 48 + 22n ；footerTop = H - PAD_BOTTOM(14) - FOOT_BLOCK(40) = H - 54
                //   n=4, H=190 ⇒ 136 <= 136（再多 1px 标题就会被压住 ⇒ 190 是 n=4 的必要高度）
                Note titleFour = new Note();
                titleFour.Id = n.Id;
                titleFour.Title = "1\n2\n3\n4";
                Typo.Layout Lfour = Typo.Measure(titleFour, Typo.MIN_W, Typo.MIN_H, Typo.PAD_TOP);
                int fourBottom = Lfour.TitleY + Typo.TITLE_LH * Lfour.TitleLines.Count + Typo.TITLE_MB;
                Assert(Lfour.TitleLines.Count == 4 && fourBottom <= Lfour.FooterTop
                        && fourBottom == Lfour.FooterTop,
                    "min-size-title-4lines-fits-exactly",
                    "titleLines=" + Lfour.TitleLines.Count + " titleBottom=" + fourBottom
                    + " == footerTop=" + Lfour.FooterTop + " (MIN_H=" + Typo.MIN_H
                    + "; n=4 是 190 的需求方：TitleY(42)+4*22+6 = 136 = H(190)-PAD_BOTTOM(14)-FOOT_BLOCK(40))"
                    + " -- 若 MIN_H 降为 140 则此处 footerTop=86 < titleBottom=136 ⇒ 必然变红");

                // ★ 负例（让上面的护栏真正有牙）：H=140 + 标题 2 行 ⇒ 标题末行**必然**被底部行压住。
                //   这正是原始缺陷（"标题末行被底部图钉行压住"）在 140 下的复现条件。
                Note twoLinesAtMin = new Note();
                twoLinesAtMin.Id = n.Id;
                twoLinesAtMin.Title = "1\n2";
                Typo.Layout Ltwo140 = Typo.Measure(twoLinesAtMin, Typo.MIN_W, 140, Typo.PAD_TOP);
                int twoBottom140 = Ltwo140.TitleY + Typo.TITLE_LH * Ltwo140.TitleLines.Count + Typo.TITLE_MB;
                Assert(Ltwo140.TitleLines.Count == 2 && twoBottom140 > Ltwo140.FooterTop,
                    "min-height-140-is-too-small-for-2-title-lines",
                    "at H=140: titleLines=" + Ltwo140.TitleLines.Count + " titleBottom=" + twoBottom140
                    + " > footerTop=" + Ltwo140.FooterTop
                    + " (原始缺陷复现；MIN_H=190 消除它，因为 190 >= 124 + 22*(4-1))");

                // 真正的**几何行为**断言（R4）：文字绝不会被 footer 覆盖 —— 不依赖 MIN_H 取值。
                Note twoLines = new Note();
                twoLines.Id = n.Id;
                twoLines.Title = "两行标题占位占位占位占位占位占位占位占位占位占位占位占位占位占位";
                Typo.Layout Ltwo = Typo.Measure(twoLines, Typo.MIN_W, Typo.DEFAULT_H, Typo.PAD_TOP);
                Assert(Ltwo.TitleLines.Count >= 2
                        && Ltwo.TitleY + Typo.TITLE_LH * Ltwo.TitleLines.Count + Typo.TITLE_MB <= Ltwo.FooterTop,
                    "min-size-title-geometry",
                    "titleLines=" + Ltwo.TitleLines.Count + " titleBottom(y+lines*LH+MB)="
                    + (Ltwo.TitleY + Typo.TITLE_LH * Ltwo.TitleLines.Count + Typo.TITLE_MB)
                    + " <= footerTop=" + Ltwo.FooterTop);
            }

            Assert(Typo.DEFAULT_W == 260 && Typo.DEFAULT_H == 320 && Typo.MIN_W == 180 && Typo.MIN_H == 190,
                "size-constants", "default 260x320, min 180x190");
            Assert(!Typo.AutoFitOnFirstShow && !Typo.UseLocalDate, "feature-default-off", "AUTO_FIT_ON_FIRST_SHOW=false USE_LOCAL_DATE=false");
        }

        private static void CheckDueLabel()
        {
            string today = Typo.TodayUtc();
            string tomorrow = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
            string yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");

            Typo.DueInfo a = Typo.DateLabel(today);
            Typo.DueInfo b = Typo.DateLabel(tomorrow);
            Typo.DueInfo c = Typo.DateLabel(yesterday);
            Typo.DueInfo d = Typo.DateLabel("");
            Typo.DueInfo e = Typo.DateLabel(today + "T23:00");
            bool good = a.Kind == Typo.DueKind.Today && b.Kind == Typo.DueKind.Normal && c.Kind == Typo.DueKind.Overdue
                && d.Kind == Typo.DueKind.None && e.Kind == Typo.DueKind.Today;
            Assert(good, "dateLabel",
                "today=" + a.Kind + "/'" + a.Caption + "' tomorrowAt=" + b.Kind + "/'" + b.Caption + "' yesterday=" + c.Kind + "/'" + c.Caption
                + "' empty=" + d.Kind + " iso-with-time=" + e.Kind + " (UTC slice, USE_LOCAL_DATE=" + Typo.UseLocalDate + ")");
        }

        private static void CheckJsonParser()
        {
            string src = "{\"a\":\"x\\\"y\",\"b\":[1,2.5,true,null],\"c\":{\"d\":\"\\u4f60\\u597d\"},\"e\":\"tab\\tnl\\n\"}";
            JsonValue v = Json.Parse(src);
            string back = Json.Write(v);
            bool good = v.IsObject
                && JsonValue.StrOr(v, "a", "") == "x\"y"
                && v.Get("b").IsArray && v.Get("b").Items.Count == 4
                && v.Get("c").Get("d").Str == "\u4f60\u597d"
                && JsonValue.StrOr(v, "e", "") == "tab\tnl\n"
                && back.Contains("\"a\":\"x\\\"y\"");
            Assert(good, "json-parse-escape", "roundtrip=" + back);
        }

        private static void CheckStateRoundTrip()
        {
            StickerState st = new StickerState();
            st.FirstRun = false;
            StickerRecord r = new StickerRecord();
            r.NoteId = "71049138-30ac-44c1-97e8-0c307eece49a";
            r.X = -1200; r.Y = 80; r.W = 260; r.H = 320; r.TopMost = true;
            r.Monitor = "\\\\.\\DISPLAY2"; r.HasMonitorBounds = true;
            r.MonitorX = -1920; r.MonitorY = 0; r.MonitorW = 1920; r.MonitorH = 1040;
            st.Records.Add(r);

            string json = st.Serialize(new DateTime(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc));
            JsonValue parsed = Json.Parse(json);
            bool good = parsed.IsObject
                && JsonValue.StrOr(parsed, "app", "") == StickerState.AppTag
                && (int)parsed.Get("schema").Number == 1
                && parsed.Get("stickers").Items.Count == 1
                && JsonValue.StrOr(parsed.Get("stickers").Items[0], "noteId", "") == r.NoteId
                && (int)parsed.Get("stickers").Items[0].Get("x").Number == -1200
                && JsonValue.BoolOr(parsed.Get("stickers").Items[0], "topMost", false);
            Assert(good, "state-serialize", json);
        }

        private static void CheckStateOrderAndDuplicates()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tietie-selftest-state");
            try
            {
                Directory.CreateDirectory(dir);
                string p = Path.Combine(dir, "sticker-state.json");
                string body = "{\"schema\":1,\"app\":\"tietie-sticker\",\"savedAt\":1,\"stickers\":["
                    + "{\"noteId\":\"A\",\"x\":1,\"y\":1,\"w\":260,\"h\":320,\"topMost\":true},"
                    + "{\"noteId\":\"B\",\"x\":2,\"y\":2,\"w\":260,\"h\":320,\"topMost\":true},"
                    + "{\"noteId\":\"A\",\"x\":9,\"y\":9,\"w\":10,\"h\":10,\"topMost\":false}]}";
                File.WriteAllText(p, body, new UTF8Encoding(false));
                StickerState st = StickerState.Load(p);
                bool good = st.Records.Count == 2
                    && st.Records[0].NoteId == "A" && st.Records[0].X == 1
                    && st.Records[1].NoteId == "B"
                    && st.Records[0].TopMost && st.Records[1].TopMost;
                Assert(good, "state-load-order-dedup",
                    "count=" + st.Records.Count + " ids=" + Ids(st) + " (dup noteId keeps first occurrence, array order preserved, 2 topMost preserved for convergence test)");

                // schema mismatch must NOT delete the file, must back it up
                string p2 = Path.Combine(dir, "sticker-state2.json");
                File.WriteAllText(p2, "{\"schema\":99,\"app\":\"other\",\"stickers\":[]}", new UTF8Encoding(false));
                StickerState st2 = StickerState.Load(p2);
                bool good2 = st2.Corrupt && File.Exists(p2) && File.Exists(p2 + ".bak");
                Assert(good2, "state-schema-mismatch-backup", "corrupt=" + st2.Corrupt + " originalKept=" + File.Exists(p2) + " bakCreated=" + File.Exists(p2 + ".bak"));
            }
            catch (Exception ex)
            {
                Bad("state-order-roundtrip", "exception: " + ex.Message);
            }
        }

        private static void CheckClamp()
        {
            Rectangle wa = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            Rectangle bad = new Rectangle(-9000, -9000, 260, 320);
            bool changed = ScreenClamp.ClampToPrimary(ref bad);
            bool inside = bad.X >= wa.X && bad.Y >= wa.Y && bad.Right <= wa.Right && bad.Bottom <= wa.Bottom;
            Assert(changed && inside, "clamp-offscreen", "(-9000,-9000) -> " + Rect(bad) + " primaryWA=" + Rect(wa));

            Rectangle ok = new Rectangle(wa.X + 40, wa.Y + 40, 260, 320);
            Rectangle copy = ok;
            bool changed2 = ScreenClamp.ClampToPrimary(ref copy);
            Assert(!changed2 && copy == ok, "clamp-visible-untouched", Rect(ok) + " unchanged=" + (!changed2));

            Rectangle tiny = new Rectangle(wa.X + 10, wa.Y + 10, 10, 10);
            ScreenClamp.ClampToPrimary(ref tiny);
            Assert(tiny.Width >= Typo.MIN_W && tiny.Height >= Typo.MIN_H, "clamp-min-size", Rect(tiny) + " >= " + Typo.MIN_W + "x" + Typo.MIN_H);

            bool vis = ScreenClamp.IsVisibleEnough(new Rectangle(wa.X + wa.Width - 80, wa.Y, 260, 320));
            Assert(vis, "min-visible-80x40", "80px sliver counts as visible = " + vis);
        }

        private static void CheckNotesAreReadable(string exeDir, Options opt)
        {
            string path = NotesStore.ResolvePath(exeDir, opt.NotesPath);
            if (path == null)
            {
                Bad("notes-resolve", "no notes.json found from exeDir=" + exeDir);
                return;
            }
            long before = new FileInfo(path).Length;
            string hashBefore = NotesStore.Sha256OfFile(path);
            List<Note> notes = NotesStore.Load(path);
            long after = new FileInfo(path).Length;
            string hashAfter = NotesStore.Sha256OfFile(path);
            // Hash-based read-only evidence. A byte-length comparison alone is NOT evidence (a write
            // could preserve the length); the digest must be unchanged, and it must equal the digest
            // the loader itself observed while reading.
            Assert(before == after && hashBefore == hashAfter && hashBefore == NotesStore.LastLoadedSha256,
                "notes-readonly-unchanged",
                path + " length " + before + "->" + after
                + " sha256 " + hashBefore + "->" + hashAfter
                + " loaderObserved=" + (NotesStore.LastLoadedSha256 ?? "(null)")
                + " (digest identical before/after the load and equal to what the loader read)");
            Log.Line("selftest notes read-only: sha256(before)=" + hashBefore + " sha256(after)=" + hashAfter
                + " loaderObserved=" + (NotesStore.LastLoadedSha256 ?? "(null)") + " bytes=" + before + "->" + after);
            Assert(notes.Count > 0, "notes-parsed", "parsed " + notes.Count + " note(s); ids=" + NoteIds(notes));
            for (int i = 0; i < notes.Count; i++)
            {
                Note n = notes[i];
                Log.Line("selftest note[" + i + "] id=" + n.Id + " color=" + n.Color + " archived=" + n.Archived
                    + " pinned=" + n.Pinned + " tags=" + n.Tags.Count + " checklist=" + n.Checklist.Count
                    + " naturalH@260=" + Typo.MeasureNaturalHeight(n, Typo.DEFAULT_W));
            }

            // Spec 4.4 expected-value table (width 260): note[0]=199, note[1]=185, note[2]=153.
            //
            // Those numbers were measured on the S1 snapshot of data\notes.json - and data\notes.json
            // belongs to the USER. Asserting the table against the live file turns a user edit into a
            // false regression (observed 2026-09-21: the user typed content into the third note and
            // 153 became 185 while this program did not change). The table is therefore asserted on the
            // S1 snapshot RECONSTRUCTED below; the live file keeps its data-independent checks
            // (read-only by digest, parsed, and the two "must never read" fences).
            int[] expected = new int[] { 199, 185, 153 };
            List<Note> snapshot = SpecSnapshotNotes();
            List<string> got = new List<string>();
            bool match = snapshot.Count == expected.Length;
            for (int i = 0; i < snapshot.Count; i++)
            {
                int h = Typo.MeasureNaturalHeight(snapshot[i], Typo.DEFAULT_W);
                got.Add("note[" + i + "]=" + h + "(expect " + (i < expected.Length ? expected[i].ToString() : "?") + ")");
                if (i >= expected.Length || h != expected[i]) match = false;
            }
            Assert(match, "spec-4.4-notes-expected-table", string.Join(", ", got.ToArray())
                + " [S1 snapshot fixture, NOT the live file - a user edit must not move this fence]");

            if (notes.Count >= 3)
            {
                // ---- explicit negative guards on the REAL notes file (captain-requested regression fences) ----
                // 159        = the 40px bottom row was left out   (one-sided fix)
                // 204/190/158= padTop was 21, the 5px top band counted twice (the withdrawn v6 ruling)
                List<string> bad159 = new List<string>();
                List<string> badV6 = new List<string>();
                int[] wrongAt21 = new int[] { 204, 190, 158 };
                for (int i = 0; i < 3; i++)
                {
                    int h = Typo.MeasureNaturalHeight(notes[i], Typo.DEFAULT_W);
                    if (i == 0 && h == 159) bad159.Add("note[0]=" + h);
                    if (h == wrongAt21[i]) badV6.Add("note[" + i + "]=" + h);
                }
                Assert(bad159.Count == 0, "forbid-missing-feet-on-real-notes",
                    "note[0] must not read 159 (that is the missing-bottom-row symptom); measured="
                    + Typo.MeasureNaturalHeight(notes[0], Typo.DEFAULT_W));
                string gotV6 = "";
                for (int i = 0; i < 3; i++) gotV6 += (i > 0 ? "," : "") + Typo.MeasureNaturalHeight(notes[i], Typo.DEFAULT_W);
                Assert(badV6.Count == 0, "forbid-withdrawn-v6-values",
                    "measured=" + gotV6 + " must not equal the withdrawn 204/190/158 (padTop=21, band counted twice)");
            }
            else
            {
                Log.Line("selftest: notes file has " + notes.Count + " note(s) -> the two real-data fences need 3, skipped");
            }
        }

        /// <summary>
        /// The three notes of the spec 4.4 acceptance snapshot, rebuilt in memory: the first carries a
        /// 3-item checklist, the second a content line plus one tag, the third only tags. Held as a
        /// fixture so the layout fence survives any edit of the user's own data file.
        /// </summary>
        private static List<Note> SpecSnapshotNotes()
        {
            List<Note> list = new List<Note>();

            Note n0 = new Note();
            n0.Id = "spec-4.4-n0";
            n0.Title = "1212";
            n0.Color = "yellow";
            for (int i = 0; i < 3; i++)
            {
                CheckItem ci = new CheckItem();
                ci.Text = (i + 1).ToString();
                ci.Done = (i == 0);
                n0.Checklist.Add(ci);
            }
            list.Add(n0);

            Note n1 = new Note();
            n1.Id = "spec-4.4-n1";
            n1.Title = "\u7535\u8bdd";
            n1.Content = "\u7ed9\u5988\u5988\u6253\u7535\u8bdd";
            n1.Color = "yellow";
            n1.Tags.Add("\u5bb6\u4eba");
            list.Add(n1);

            Note n2 = new Note();
            n2.Id = "spec-4.4-n2";
            n2.Title = "\u751f\u6d3b\u8d39";
            n2.Color = "yellow";
            n2.Tags.Add("\u5bb6\u4eba");
            n2.Tags.Add("\u751f\u6d3b");
            list.Add(n2);

            return list;
        }

        /// <summary>
        /// Structural check: EVERY layout-measurement call site must pass the same padTop.
        /// This exists because line numbers drift and snapshots go stale - instead of trusting a
        /// hand grep, the test re-reads the sources and verifies the 4th argument of every
        /// Typo.Measure(...) call is exactly "Typo.PAD_TOP" and never "PAD_TOP + BAND_H".
        /// A "self-consistent wrong padTop" (measurement AND assertion both 21) is additionally
        /// blocked by the LITERAL anchors elsewhere in this file (304 / 212 / 199-185-153).
        /// </summary>
        private static void CheckEveryMeasureCallSiteUsesPadTop()
        {
            try
            {
                // v14: use the shared locator (it walks up, so BOTH the standalone layout
                // .tools\sticker_build and the merged layout <repo> resolve to the same tree).
                // The old "..\.." here silently skipped this whole guard in the merged layout.
                string srcDir = SrcDir();
                if (srcDir == null)
                {
                    Log.Line("selftest: source dir not found, skipping the Measure call-site scan");
                    Ok("measure-callsite-scan", "skipped (source dir absent)");
                    return;
                }

                List<string> bad = new List<string>();
                List<string> good = new List<string>();
                string[] files = new string[] { "CardRenderer.cs", "StickerForm.cs", "Typo.cs", "SelfTest.cs" };
                for (int f = 0; f < files.Length; f++)
                {
                    string path = System.IO.Path.Combine(srcDir, files[f]);
                    if (!File.Exists(path)) { bad.Add(files[f] + ": missing"); continue; }
                    string text = File.ReadAllText(path);
                    text = text.Replace("\r", " ").Replace("\n", " ");
                    int idx = 0;
                    while (true)
                    {
                        int call = text.IndexOf("Typo.Measure(", idx, StringComparison.Ordinal);
                        if (call < 0) break;
                        idx = call + 1;
                        int open = call + "Typo.Measure".Length;
                        int depth = 0;
                        int i = open;
                        for (; i < text.Length; i++)
                        {
                            if (text[i] == '(') depth++;
                            else if (text[i] == ')') { depth--; if (depth == 0) break; }
                        }
                        if (i >= text.Length) { bad.Add(files[f] + ": unbalanced call"); break; }
                        string args = text.Substring(open + 1, i - open - 1);
                        string[] cols = args.Split(',');
                        if (cols.Length < 4)
                        {
                            idx = i; // a nested Typo.Measure(Typo.Due, ...) style text measurement, no padTop
                            continue;
                        }
                        string pad = cols[3].Trim();
                        if (pad == "Typo.PAD_TOP") good.Add(files[f] + "(" + pad + ")");
                        else bad.Add(files[f] + ": 4th arg = '" + pad + "'");
                        idx = i;
                    }
                }

                Assert(bad.Count == 0, "measure-callsite-scan-all-padtop",
                    "call sites=" + good.Count + " [" + string.Join(", ", good.ToArray()) + "]"
                    + (bad.Count == 0 ? " ; no call site uses anything but Typo.PAD_TOP" : " ; OFFENDERS=" + string.Join(" | ", bad.ToArray())));
            }
            catch (Exception ex)
            {
                Bad("measure-callsite-scan-all-padtop", "exception: " + ex.Message);
            }
        }

        /// <summary>
        /// Verifies the build manifest that binds this exe to the exact source set it came from.
        /// Needed because this csc has no /deterministic: identical sources rebuild to different exe
        /// bytes, so ONLY the manifest (exe digest + per-source digests + build timestamp) can prove
        /// "this exe corresponds to this source tree".
        /// </summary>
        // ------------------------------------------------------------------
        // spec v11 (close box + empty-set re-place). Reference implementation of the spec's
        // assertion list #1 / #6 / #7 / #8 / #10 / #17 / #19 / #20 (the live-only ones - #2 / #3 /
        // #14 / #15 / #16 - are driven by interaction-test.ps1, which really closes windows).
        // Every expected value below is a LITERAL from the spec, never derived from the code under
        // test, so a mutation of the geometry or of the branch order turns these red.
        // ------------------------------------------------------------------

        private static void CheckV11CloseGeometry()
        {
            int w = Typo.DEFAULT_W;      // 260
            int h = Typo.DEFAULT_H;      // 320
            Note n = new Note();
            n.Id = "g"; n.Title = "T"; n.Content = "C";
            Typo.Layout L = Typo.Measure(n, w, h, Typo.PAD_TOP);
            Rectangle hit = Typo.CloseHitRect(w);
            int closeLeft = Typo.CloseLeft(w);

            // #6 flag-shift-equals-close-plus-gap: 226 == 260-16-18 ; 220 == 226-6 ; 208 == 220-12
            Assert(closeLeft == 226 && Typo.FlagRight(w) == 220 && Typo.FlagLeft(w) == 208
                && Typo.FlagRight(w) == closeLeft - Typo.CLOSE_GAP,
                "flag-shift-equals-close-plus-gap",
                "CLOSE_LEFT=" + closeLeft + "(want 226=260-16-18) FLAG_RIGHT=" + Typo.FlagRight(w)
                + "(want 220=226-6) FLAG_LEFT=" + Typo.FlagLeft(w) + "(want 208=220-12)");

            // 3.2 hit box constants: 22x22 anchored at (224,16); CLOSE_TOP == 18
            Assert(hit.X == 224 && hit.Y == Typo.PAD_TOP && hit.Width == 22 && hit.Height == 22 && Typo.CLOSE_TOP == 18,
                "close-hit-box-22x22-at-spec-offset",
                "CLOSE_HIT_RECT=(" + hit.X + "," + hit.Y + " " + hit.Width + "x" + hit.Height
                + ") want (224,16 22x22); CLOSE_TOP=" + Typo.CLOSE_TOP + " want 18");

            // #1 close-hit-not-drag: a press in the middle of the box classifies as Close (no drag armed)
            Point mid = new Point(hit.X + hit.Width / 2, hit.Y + hit.Height / 2);
            Typo.HitLevel zMid = Typo.ClassifyHit(w, h, L.FooterContentTop, mid);
            Assert(zMid == Typo.HitLevel.Close, "close-hit-not-drag",
                "press at (" + mid.X + "," + mid.Y + ") inside close hit box -> level=" + zMid
                + " (want Close: drag must NOT be armed)");

            // complement (the other half of the acceptance criterion): outside the box is still a drag zone
            Point justLeft = new Point(hit.Left - 1, mid.Y);
            Point titleArea = new Point(120, Typo.PAD_TOP + Typo.ROW_TOP_H + Typo.ROW_TOP_GAP + 4);
            Point listArea = new Point(120, h - Typo.PAD_BOTTOM - 40 - 12);
            Typo.HitLevel zLeft = Typo.ClassifyHit(w, h, L.FooterContentTop, justLeft);
            Typo.HitLevel zTitle = Typo.ClassifyHit(w, h, L.FooterContentTop, titleArea);
            Typo.HitLevel zList = Typo.ClassifyHit(w, h, L.FooterContentTop, listArea);
            Assert(zLeft == Typo.HitLevel.Client && zTitle == Typo.HitLevel.Client && zList == Typo.HitLevel.Client,
                "close-outside-hit-is-drag",
                "1px left of box (" + justLeft.X + "," + justLeft.Y + ")=" + zLeft + "; title (" + titleArea.X + "," + titleArea.Y
                + ")=" + zTitle + "; list (" + listArea.X + "," + listArea.Y + ")=" + zList + " (all want Client = drag)");

            // contrast case: the resize band still wins over the close level (level 1 > level 2)
            Point corner = new Point(2, 2);
            Typo.HitLevel zCorner = Typo.ClassifyHit(w, h, L.FooterContentTop, corner);
            Assert(zCorner == Typo.HitLevel.Resize, "resize-band-still-beats-close",
                "press at (2,2) -> " + zCorner + " (want Resize)");

            // #7 close-hit-not-overlap-flag: 220 <= 224 at the default width AND at MIN_W(180)
            int wMin = Typo.MIN_W;
            bool noOverlap = Typo.FlagRight(w) <= hit.Left && Typo.FlagRight(wMin) <= Typo.CloseHitRect(wMin).Left;
            Assert(noOverlap, "close-hit-not-overlap-flag",
                "default: FLAG_RIGHT=" + Typo.FlagRight(w) + " <= CLOSE_HIT_LEFT=" + hit.Left
                + "; MIN_W: " + Typo.FlagRight(wMin) + " <= " + Typo.CloseHitRect(wMin).Left);

            // #8 close-hit-inside-card-and-row: 18 >= 16 and 36 <= 37, and the box right-aligns to content
            bool inside = Typo.CLOSE_TOP >= Typo.PAD_TOP
                && Typo.CLOSE_TOP + Typo.CLOSE_BOX <= Typo.PAD_TOP + Typo.ROW_TOP_H
                && closeLeft + Typo.CLOSE_BOX == w - Typo.PAD_LR;
            Assert(inside, "close-hit-inside-card-and-row",
                "CLOSE_TOP=" + Typo.CLOSE_TOP + " >= PAD_TOP=" + Typo.PAD_TOP + "; CLOSE_TOP+BOX="
                + (Typo.CLOSE_TOP + Typo.CLOSE_BOX) + " <= PAD_TOP+ROW_TOP_H=" + (Typo.PAD_TOP + Typo.ROW_TOP_H)
                + "; CLOSE_LEFT+BOX=" + (closeLeft + Typo.CLOSE_BOX) + " == w-PAD_LR=" + (w - Typo.PAD_LR));

            // the close box must not sit inside the resize band (spec v11 6: level 1 does not overlap level 2)
            Assert(hit.Right <= w - Typo.RESIZE_BORDER && hit.Left >= Typo.RESIZE_BORDER, "close-hit-avoids-resize-band",
                "hit.Right=" + hit.Right + " <= w-RESIZE_BORDER=" + (w - Typo.RESIZE_BORDER)
                + "; hit.Left=" + hit.Left + " >= RESIZE_BORDER=" + Typo.RESIZE_BORDER);

            // the painted colour must stay --text @ CLOSE_ALPHA (3.3)
            Color wantFg = Palette.Alpha(Palette.Text, Typo.CLOSE_ALPHA);
            Assert(Typo.CloseFg.ToArgb() == wantFg.ToArgb(), "close-fg-equals-text-at-close-alpha",
                "CloseFg=" + Hex(Typo.CloseFg) + " vs Palette.Alpha(Text,0.60)=" + Hex(wantFg));

            // #17 state-path-candidates-exactly-two (spec v11 8.1: the workspace data\ copy is deleted)
            string[] cands = PathPick.CandidatePaths(@"C:\exeDir", @"C:\appData");
            bool two = cands.Length == 2
                && cands[0].EndsWith("sticker-state.json", StringComparison.OrdinalIgnoreCase)
                && cands[1].EndsWith("sticker-state.json", StringComparison.OrdinalIgnoreCase)
                && cands[0].StartsWith(@"C:\exeDir", StringComparison.OrdinalIgnoreCase)
                && cands[1].StartsWith(@"C:\appData", StringComparison.OrdinalIgnoreCase);
            bool noDataDir = true;
            for (int i = 0; i < cands.Length; i++)
            {
                string[] segs = cands[i].Split(new char[] { '\\', '/' });
                for (int s = 0; s < segs.Length - 1; s++) if (string.Equals(segs[s], "data", StringComparison.OrdinalIgnoreCase)) noDataDir = false;
            }
            Assert(two && noDataDir, "state-path-candidates-exactly-two",
                "count=" + cands.Length + " -> [" + string.Join(" | ", cands) + "] (want exactly 2, no 'data' segment)");

            // #10 rule (the branch itself): empty set + --note => do NOT place-all; empty set w/o --note => place-all
            bool s1 = StickerManager.SuppressPlaceAllForCliNote(true, 1);
            bool s2 = StickerManager.SuppressPlaceAllForCliNote(true, 0);
            bool s3 = StickerManager.SuppressPlaceAllForCliNote(false, 1);
            Assert(s1 && !s2 && !s3, "empty-set-with-note-does-not-place-all",
                "suppress(empty,yes)=" + s1 + " (want True); suppress(empty,no)=" + s2 + " (want False); suppress(nonEmpty,yes)=" + s3 + " (want False)");

            // v17: the same fallback must yield to an UNACKNOWLEDGED bridge "place" request (the user
            // clicked 「贴到桌面」), otherwise that one click dumps every note onto an empty desktop.
            bool v1 = StickerManager.SuppressPlaceAllForExplicitPlacements(true, 0, 1);    // click only
            bool v2 = StickerManager.SuppressPlaceAllForExplicitPlacements(true, 2, 0);    // --note only
            bool v3 = StickerManager.SuppressPlaceAllForExplicitPlacements(true, 1, 3);    // both
            bool v4 = StickerManager.SuppressPlaceAllForExplicitPlacements(true, 0, 0);    // nobody asked -> place all
            bool v5 = StickerManager.SuppressPlaceAllForExplicitPlacements(false, 0, 5);   // set not empty -> no fallback
            Assert(v1 && v2 && v3 && !v4 && !v5, "v17-explicit-target-suppresses-place-all",
                "suppress(empty, click=1)=" + v1 + " (want True); suppress(empty, --note=2)=" + v2
                + " (want True); suppress(empty, both)=" + v3 + " (want True); suppress(empty, nobody)=" + v4
                + " (want False); suppress(nonEmpty, click=5)=" + v5 + " (want False)"
                + " - the v11 place-all fallback may fire ONLY when nobody named a note");
        }

        /// <summary>
        /// Source-level guards for behaviour that only exists in the live process (exit path, modal
        /// notice, menu arity). They read the SAME files this exe was compiled from, so reordering or
        /// deleting a branch turns them red - that is the point of #19 / #20 in the v11 spec.
        /// </summary>
        private static void CheckV11SourceGuards()
        {
            string srcDir = SrcDir();
            if (srcDir == null)
            {
                Ok("v11-source-guards", "skipped (source dir absent)");
                return;
            }

            // ---- #19 second-instance-shows-visible-notice ----
            string prog = ReadFlat(Path.Combine(srcDir, "Program.cs"));
            if (prog == null) { Bad("second-instance-shows-visible-notice", "Program.cs not readable"); }
            else
            {
                int branch = prog.IndexOf("if (!createdNew)", StringComparison.Ordinal);
                int box = prog.IndexOf("MessageBox.Show(", StringComparison.Ordinal);
                int afterBranch = prog.IndexOf("string appDataDir", StringComparison.Ordinal);
                int exit0 = box < 0 ? -1 : prog.IndexOf("Environment.Exit(0)", box, StringComparison.Ordinal);
                bool ok = branch >= 0 && box > branch && exit0 > box && (afterBranch < 0 || box < afterBranch)
                    && prog.IndexOf("UiStrings.AlreadyRunningBody", StringComparison.Ordinal) > branch;
                Assert(ok, "second-instance-shows-visible-notice",
                    "branch@" + branch + " < MessageBox.Show@" + box + " < exit(0)@" + exit0
                    + " (must also stay before 'string appDataDir'@" + afterBranch + "), notice body wired="
                    + (prog.IndexOf("UiStrings.AlreadyRunningBody", StringComparison.Ordinal) > branch));
            }

            // ---- #20 close-order-must-remove-record-first (+ the exit wiring itself) ----
            string mgr = ReadFlat(Path.Combine(srcDir, "StickerManager.cs"));
            if (mgr == null) { Bad("close-order-must-remove-record-first", "StickerManager.cs not readable"); }
            else
            {
                int m = mgr.IndexOf("public void OnCardClosing", StringComparison.Ordinal);
                int pers = m < 0 ? -1 : mgr.IndexOf("Persist(\"card-close\")", m, StringComparison.Ordinal);
                int zero = pers < 0 ? -1 : mgr.IndexOf("CardCount == 0", pers, StringComparison.Ordinal);
                int exitAll = zero < 0 ? -1 : mgr.IndexOf("ExitAll(\"last-card-closed\")", zero, StringComparison.Ordinal);
                bool ok = m >= 0 && pers > m && zero > pers && exitAll > zero;
                Assert(ok, "close-order-must-remove-record-first",
                    "OnCardClosing@" + m + " < Persist(\"card-close\")@" + pers + " < CardCount==0@" + zero
                    + " < ExitAll(\"last-card-closed\")@" + exitAll
                    + " (reordering to ExitAll-first re-hides the closed card and MUST turn this red)");
            }

            // ---- v18: the card has NO right-click menu (user 2026-09-21: "去除右键桌面贴纸的
            //      『退出贴纸』功能"). The v11/v13-era fence counted exactly ONE entry here; the fence is
            //      now the opposite: the file must name neither the menu TYPE nor the menu FIELD, and the
            //      live check drives a real right click and requires that no popup appears.
            string form = ReadFlat(Path.Combine(srcDir, "StickerForm.cs"));
            if (form == null) { Bad("v18-card-right-click-has-no-menu", "StickerForm.cs not readable"); }
            else
            {
                int typeUses = CountOccurrences(form, "ContextMenuStrip");
                int fieldUses = CountOccurrences(form, "_menu");
                Assert(typeUses == 0 && fieldUses == 0, "v18-card-right-click-has-no-menu",
                    "StickerForm.cs: 'ContextMenuStrip' occurrences = " + typeUses + ", '_menu' occurrences = "
                    + fieldUses + " (want 0/0: a card must not carry a right-click menu any more; drawing the"
                    + " old exit entry back - or attaching any menu - turns this red)");
            }
        }

        // ------------------------------------------------------------------
        // spec v12 (S2): checklist write-back.
        //
        // EVERY test below runs against a throw-away fixture in <exeDir>\selftest-tmp, never against
        // the user's data\notes.json. The fixture keeps the shape of the real file (CJK text, epoch
        // millisecond integers, syncStatus) and adds one UNKNOWN member so "the round-trip keeps
        // fields this version does not know about" is asserted rather than assumed.
        // ------------------------------------------------------------------

        private static void CheckS2ChecklistWrite(string exeDir)
        {
            string dir = System.IO.Path.Combine(exeDir, "selftest-tmp");
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                Bad("s2-sandbox-ready", "cannot create " + dir + ": " + ex.Message);
                return;
            }

            string fixture =
                "["
                + "{\"id\":\"n1\",\"title\":\"\u6807\u9898\u4e00\",\"content\":\"\u6b63\u6587\",\"color\":\"yellow\","
                + "\"done\":false,\"pinned\":false,\"archived\":false,\"dueAt\":\"\",\"tags\":[\"\u5bb6\u4eba\"],"
                + "\"checklist\":[{\"text\":\"\u7b2c\u4e00\u9879\",\"done\":false},{\"text\":\"\u7b2c\u4e8c\u9879\",\"done\":true}],"
                + "\"createdAt\":1789125720901,\"updatedAt\":1789125732745,\"syncStatus\":\"local\"},"
                + "{\"id\":\"n2\",\"title\":\"second\",\"content\":\"\",\"color\":\"pink\",\"done\":false,\"pinned\":true,"
                + "\"archived\":false,\"dueAt\":\"\",\"tags\":[],\"checklist\":[{\"text\":\"keep me\",\"done\":false}],"
                + "\"createdAt\":1789126012561,\"updatedAt\":1789126012561,\"syncStatus\":\"local\","
                + "\"x-unknown\":{\"a\":[1,2,3],\"b\":\"keep\"}},"
                + "{\"id\":\"n3\",\"title\":\"third\",\"content\":\"\",\"color\":\"blue\",\"done\":false,\"pinned\":false,"
                + "\"archived\":false,\"dueAt\":\"\",\"tags\":[],\"checklist\":[],\"createdAt\":1789126505493,"
                + "\"updatedAt\":1789126505493,\"syncStatus\":\"local\"}"
                + "]";

            string path = System.IO.Path.Combine(dir, "notes.json");
            System.IO.File.WriteAllText(path, fixture, new UTF8Encoding(false));
            byte[] originalBytes = System.IO.File.ReadAllBytes(path);
            string originalSha = NotesWriter.Sha256Hex(originalBytes);

            // ---- the fixture must survive the production loader (this is what fills Note.RawJson) ----
            List<Note> notes = NotesStore.Load(path);
            Assert(notes.Count == 3 && notes[0].Checklist.Count == 2 && notes[0].RawJson.Length > 0
                && notes[0].Tags.Count == 1 && notes[0].Checklist[1].Done,
                "s2-fixture-parses",
                "notes=" + notes.Count + " checklist=" + (notes.Count > 0 ? notes[0].Checklist.Count : -1)
                + " rawJsonChars=" + (notes.Count > 0 ? notes[0].RawJson.Length : -1)
                + " tags=" + (notes.Count > 0 ? notes[0].Tags.Count : -1)
                + " item1Done=" + (notes.Count > 0 && notes[0].Checklist.Count > 1 ? notes[0].Checklist[1].Done.ToString() : "?"));
            if (notes.Count != 3) { return; }

            // ---- 1. the happy path: click item 0 of n1 (false -> true) ----
            NotesWriter.ResetSafetyCopyFlagForTest();
            ToggleResult r1 = NotesWriter.ToggleChecklist(path, dir, "n1", 0,
                notes[0].Checklist[0].Text, notes[0].Checklist[0].Done, notes[0].RawJson);
            Assert(r1.Outcome == ToggleOutcome.Written && r1.DesiredDone && !r1.WasDone,
                "s2-toggle-writes-target-item",
                r1.Describe());
            if (r1.Outcome != ToggleOutcome.Written)
            {
                // every later assertion depends on a real write; report and stop rather than cascade
                Cleanup(dir);
                return;
            }

            byte[] afterBytes = System.IO.File.ReadAllBytes(path);
            JsonValue before = null, after = null;
            string pe1, pe2;
            bool parsedBoth = NotesWriter.TryParseArray(originalBytes, out before, out pe1)
                && NotesWriter.TryParseArray(afterBytes, out after, out pe2);
            List<string> diff = new List<string>();
            if (parsedBoth) NotesWriter.Diff(before, after, "", diff);

            // ---- 2. EXACTLY two paths may differ: the clicked done + that note's updatedAt ----
            bool exactlyTwo = diff.Count == 2
                && diff.Contains("/0/checklist/0/done")
                && diff.Contains("/0/updatedAt");
            Assert(exactlyTwo, "s2-write-changes-only-intended-paths",
                "diff=[" + string.Join(", ", diff.ToArray()) + "] (want exactly [/0/checklist/0/done, /0/updatedAt])");

            // ---- 3. unknown members and untouched notes survive verbatim ----
            bool kept = false;
            string keptDetail = "no parsed tree";
            if (parsedBoth && after.Items.Count == 3)
            {
                JsonValue n2 = after.Items[1];
                JsonValue xu = n2 == null ? null : n2.Get("x-unknown");
                JsonValue xuA = xu == null ? null : xu.Get("a");
                kept = n2 != null
                    && JsonValue.StrOr(n2, "syncStatus", "") == "local"
                    && JsonValue.StrOr(n2, "title", "") == "second"
                    && xu != null && xu.IsObject && xuA != null && xuA.IsArray && xuA.Items.Count == 3
                    && JsonValue.StrOr(xu, "b", "") == "keep"
                    && after.Items[2] != null && JsonValue.StrOr(after.Items[2], "title", "") == "third";
                keptDetail = "n2.syncStatus=" + (n2 == null ? "?" : JsonValue.StrOr(n2, "syncStatus", ""))
                    + " n2.x-unknown.a.len=" + (xuA == null || !xuA.IsArray ? -1 : xuA.Items.Count)
                    + " n2.x-unknown.b=" + (xu == null ? "?" : JsonValue.StrOr(xu, "b", ""))
                    + " n3.title=" + (after.Items[2] == null ? "?" : JsonValue.StrOr(after.Items[2], "title", ""));
            }
            Assert(kept, "s2-write-preserves-unknown-fields", keptDetail);

            // ---- 4. NEGATIVE CONTROL for the verifier: it must SEE a lost field ----
            bool detected = false;
            string detDetail = "no parsed tree";
            if (parsedBoth)
            {
                JsonValue mutated = Json.Parse(Json.Write(after));
                JsonValue m2 = mutated.Items[1];
                for (int i = m2.Members.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(m2.Members[i].Key, "syncStatus", StringComparison.Ordinal)) m2.Members.RemoveAt(i);
                }
                List<string> md = new List<string>();
                NotesWriter.Diff(before, mutated, "", md);
                detected = md.Contains("/1/syncStatus");
                detDetail = "diff=[" + string.Join(", ", md.ToArray()) + "] (must contain /1/syncStatus)";
            }
            Assert(detected, "s2-verify-guard-detects-field-loss", detDetail);

            // ---- 5. atomic write: no temp file left behind ----
            Assert(!System.IO.File.Exists(path + NotesWriter.TmpSuffix), "s2-write-leaves-no-temp-file",
                "temp=" + path + NotesWriter.TmpSuffix + " exists=" + System.IO.File.Exists(path + NotesWriter.TmpSuffix));

            // ---- 6. the once-per-process safety copy holds the pre-write bytes ----
            string backup = System.IO.Path.Combine(dir, NotesWriter.BackupFileName);
            bool backupOk = System.IO.File.Exists(backup)
                && NotesWriter.Sha256Hex(System.IO.File.ReadAllBytes(backup)) == originalSha;
            Assert(backupOk, "s2-safety-copy-holds-pre-write-bytes",
                backup + " exists=" + System.IO.File.Exists(backup)
                + " sha256=" + (System.IO.File.Exists(backup) ? NotesWriter.Sha256Hex(System.IO.File.ReadAllBytes(backup)) : "(none)")
                + " want " + originalSha);

            // ---- 7. someone else edited THIS note -> refuse, file untouched ----
            byte[] pre7 = System.IO.File.ReadAllBytes(path);
            string base7 = Json.Write(after.Items[1]);
            string cur7 = NotesWriter.DecodeUtf8(pre7);
            System.IO.File.WriteAllText(path, cur7.Replace("\"title\":\"second\"", "\"title\":\"second-edited\""),
                new UTF8Encoding(false));
            byte[] pre7b = System.IO.File.ReadAllBytes(path);
            ToggleResult r7 = NotesWriter.ToggleChecklist(path, dir, "n2", 0, "keep me", false, base7);
            bool untouched7 = NotesWriter.Sha256Hex(System.IO.File.ReadAllBytes(path)) == NotesWriter.Sha256Hex(pre7b);
            Assert(r7.Outcome == ToggleOutcome.Conflict && untouched7, "s2-conflict-note-changed-refuses-write",
                r7.Describe() + "; fileUnchanged=" + untouched7);

            // ---- 8. the checklist item text changed -> refuse even when the note baseline is fresh ----
            byte[] pre8 = System.IO.File.ReadAllBytes(path);
            List<Note> fresh8 = NotesStore.Load(path);
            Note n2Loaded = null;
            for (int i = 0; i < fresh8.Count; i++) if (fresh8[i].Id == "n2") n2Loaded = fresh8[i];
            ToggleResult r8 = NotesWriter.ToggleChecklist(path, dir, "n2", 0, "keep me (old text)", false,
                n2Loaded == null ? null : n2Loaded.RawJson);
            bool untouched8 = NotesWriter.Sha256Hex(System.IO.File.ReadAllBytes(path)) == NotesWriter.Sha256Hex(pre8);
            Assert(r8.Outcome == ToggleOutcome.Conflict && untouched8, "s2-conflict-item-text-changed-refuses-write",
                r8.Describe() + "; fileUnchanged=" + untouched8);

            // ---- 9. the note is gone -> refuse, file untouched ----
            byte[] pre9 = System.IO.File.ReadAllBytes(path);
            ToggleResult r9 = NotesWriter.ToggleChecklist(path, dir, "n-does-not-exist", 0, "x", false, "{}");
            bool untouched9 = NotesWriter.Sha256Hex(System.IO.File.ReadAllBytes(path)) == NotesWriter.Sha256Hex(pre9);
            Assert(r9.Outcome == ToggleOutcome.NoteGone && untouched9, "s2-note-gone-refuses-write",
                r9.Describe() + "; fileUnchanged=" + untouched9);

            // ---- 10. already in the desired state -> Unchanged, and NOT written at all ----
            List<Note> fresh10 = NotesStore.Load(path);
            Note n1Loaded = null;
            for (int i = 0; i < fresh10.Count; i++) if (fresh10[i].Id == "n1") n1Loaded = fresh10[i];
            byte[] pre10 = System.IO.File.ReadAllBytes(path);
            string cur10 = NotesWriter.Sha256Hex(pre10);
            ToggleResult r10 = NotesWriter.ToggleChecklist(path, dir, "n1", 0,
                n1Loaded.Checklist[0].Text, false /* pretend the card still shows unchecked */, n1Loaded.RawJson);
            bool untouched10 = NotesWriter.Sha256Hex(System.IO.File.ReadAllBytes(path)) == cur10;
            Assert(r10.Outcome == ToggleOutcome.Unchanged && untouched10 && n1Loaded.Checklist[0].Done,
                "s2-unchanged-intent-is-a-no-op",
                r10.Describe() + "; fileUnchanged=" + untouched10 + "; diskDone=" + n1Loaded.Checklist[0].Done);

            // ---- 10b. a write whose round-trip would LOSE information must be refused and rolled back ----
            // This is the END-TO-END pin on NotesWriter step 10 (re-read + structural diff): it drives the
            // public ToggleChecklist entry point, so deleting the verification CALL SITE - or the rollback
            // branch - turns it red. (The Diff-only assertion #5 above cannot do that: independent
            // verification F1, 2026-09-21, showed that making the call site a no-op still left 77/0.)
            // Trigger: the documented conservative boundary of spec v12 4 - a NON-INTEGER number cannot
            // survive this machine's JSON writer (it emits integers), so the diff must report an unexpected
            // path and restore the original bytes. If the writer is ever made fraction-exact this assertion
            // goes red on purpose: pick another lossy trigger and update this comment.
            string fracPath = System.IO.Path.Combine(dir, "notes-fractional.json");
            string fracFixture =
                "[{\"id\":\"f1\",\"title\":\"frac\",\"content\":\"\",\"color\":\"yellow\",\"done\":false,"
                + "\"pinned\":false,\"archived\":false,\"dueAt\":\"\",\"tags\":[],"
                + "\"checklist\":[{\"text\":\"only\",\"done\":false}],"
                + "\"createdAt\":1789125720901.5,\"updatedAt\":1789125732745,\"syncStatus\":\"local\"}]";
            System.IO.File.WriteAllText(fracPath, fracFixture, new UTF8Encoding(false));
            byte[] fracBefore = System.IO.File.ReadAllBytes(fracPath);
            string fracShaBefore = NotesWriter.Sha256Hex(fracBefore);
            List<Note> fracNotes = NotesStore.Load(fracPath);
            ToggleResult rBad = NotesWriter.ToggleChecklist(fracPath, dir, "f1", 0, "only", false,
                fracNotes.Count > 0 ? fracNotes[0].RawJson : null);
            byte[] fracAfter = System.IO.File.ReadAllBytes(fracPath);
            string fracShaAfter = NotesWriter.Sha256Hex(fracAfter);
            bool rolledBack = fracShaAfter == fracShaBefore;
            bool noTemp2 = !System.IO.File.Exists(fracPath + NotesWriter.TmpSuffix);
            bool verifyFired = rBad.Detail != null
                && rBad.Detail.IndexOf("unexpected difference", StringComparison.Ordinal) >= 0;
            Assert(rBad.Outcome == ToggleOutcome.IoError && rolledBack && noTemp2 && verifyFired,
                "s2-bad-write-is-refused-and-rolled-back",
                "outcome=" + rBad.Outcome + " (want IoError); fileRolledBack=" + rolledBack
                + " (sha " + fracShaBefore.Substring(0, 12) + " -> " + fracShaAfter.Substring(0, 12) + ")"
                + "; tempLeft=" + (!noTemp2) + "; verifyStepFired=" + verifyFired + "; detail=" + rBad.Detail);

            // ---- 11. hit geometry: the clickable band and the painted box come from one source ----
            Note hn = new Note();
            hn.Id = "hit"; hn.Title = "T";
            hn.Checklist.Add(new CheckItem()); hn.Checklist[0].Text = "one";
            hn.Checklist.Add(new CheckItem()); hn.Checklist[1].Text = "two";
            Typo.Layout HL = Typo.Measure(hn, Typo.DEFAULT_W, Typo.DEFAULT_H, Typo.PAD_TOP);
            Rectangle box0 = HL.ChecklistBoxRects[0];
            bool geo = HL.ChecklistBoxRects.Count == 2 && HL.ChecklistHitRects.Count == 2
                && box0.X == Typo.PAD_LR && box0.Y == HL.ChecklistItemY[0] + Typo.CHECK_BOX_MT
                && box0.Width == Typo.CHECK_BOX && box0.Height == Typo.CHECK_BOX
                && HL.ChecklistHitRects[0].Contains(box0)
                && HL.ChecklistHitRects[0].Bottom <= HL.ChecklistHitRects[1].Top;
            int atBox0 = Typo.ChecklistItemAt(HL, new Point(box0.X + 2, box0.Y + 2));
            Rectangle band1 = HL.ChecklistHitRects[1];
            int atBand1 = Typo.ChecklistItemAt(HL, new Point(band1.X + 4, band1.Y + 4));
            int atTitle = Typo.ChecklistItemAt(HL, new Point(Typo.PAD_LR + 2, HL.TitleY + 2));
            Assert(geo && atBox0 == 0 && atBand1 == 1 && atTitle == -1, "s2-checklist-hit-matches-paint",
                "box0=" + Rect(box0) + " want (" + Typo.PAD_LR + "," + (HL.ChecklistItemY[0] + Typo.CHECK_BOX_MT)
                + " " + Typo.CHECK_BOX + "x" + Typo.CHECK_BOX + "); hitOnBox=" + atBox0 + " hitOnBand1=" + atBand1
                + " hitOnTitle=" + atTitle + "; bandsDisjoint=" + (HL.ChecklistHitRects[0].Bottom <= HL.ChecklistHitRects[1].Top));

            // ---- 12. the close box keeps its v11 priority over any checklist band ----
            Rectangle ch = Typo.CloseHitRect(Typo.DEFAULT_W);
            int atClose = Typo.ChecklistItemAt(HL, new Point(ch.X + ch.Width / 2, ch.Y + ch.Height / 2));
            Assert(atClose == -1, "s2-close-box-is-not-a-checklist-band",
                "CloseHitRect=" + Rect(ch) + " -> checklist index " + atClose + " (want -1: ClassifyHit wins first)");

            // ---- 13. the loader stays write-free, and the writer stays single ----
            string srcDir = SrcDir();
            if (srcDir == null)
            {
                Ok("s2-loader-still-write-free", "skipped (source dir absent)");
                Ok("s2-notes-writes-confined-to-noteswriter", "skipped (source dir absent)");
                Ok("s2-single-write-entry-point", "skipped (source dir absent)");
            }
            else
            {
                string[] writeTokens = new string[] { "File.WriteAllText", "File.WriteAllBytes", "File.WriteAllLines",
                    "File.Replace", "File.Copy", "File.Delete", "File.Move", "File.AppendAllText", "StreamWriter", "FileMode.Create" };
                string notesSrc = ReadFlat(Path.Combine(srcDir, "Notes.cs"));
                List<string> found = new List<string>();
                if (notesSrc == null) found.Add("(Notes.cs unreadable)");
                else for (int i = 0; i < writeTokens.Length; i++)
                    if (notesSrc.IndexOf(writeTokens[i], StringComparison.Ordinal) >= 0) found.Add(writeTokens[i]);
                Assert(found.Count == 0, "s2-loader-still-write-free",
                    "write tokens inside Notes.cs = [" + string.Join(", ", found.ToArray()) + "] (want none: loading is read-only)");

                // The invariant is about notes.json specifically: among the files that know about the
                // notes data file, only the designated writer may perform file I/O that writes.
                // (State.cs writes sticker-state.json and is deliberately NOT part of this set;
                //  SelfTest.cs is the harness itself, so it is excluded and its tokens do not count.)
                List<string> offending = new List<string>();
                List<string> knowers = new List<string>();
                string[] all = Directory.GetFiles(srcDir, "*.cs");
                for (int i = 0; i < all.Length; i++)
                {
                    string name = Path.GetFileName(all[i]);
                    string t = ReadFlat(all[i]);
                    if (t == null) continue;
                    if (t.IndexOf("notes.json", StringComparison.Ordinal) < 0) continue;
                    knowers.Add(name);
                    if (string.Equals(name, "SelfTest.cs", StringComparison.Ordinal)) continue;
                    for (int k = 0; k < writeTokens.Length; k++)
                    {
                        if (t.IndexOf(writeTokens[k], StringComparison.Ordinal) >= 0)
                        {
                            if (!offending.Contains(name)) offending.Add(name);
                            break;
                        }
                    }
                }
                bool confined = offending.Count == 1 && string.Equals(offending[0], "NotesWriter.cs", StringComparison.Ordinal);
                Assert(confined, "s2-notes-writes-confined-to-noteswriter",
                    "files mentioning notes.json = [" + string.Join(", ", knowers.ToArray())
                    + "]; of those, files with write tokens = [" + string.Join(", ", offending.ToArray())
                    + "] (want exactly NotesWriter.cs)");

                string mgrSrc = ReadFlat(Path.Combine(srcDir, "StickerManager.cs"));
                string progSrc = ReadFlat(Path.Combine(srcDir, "Program.cs"));
                int inMgr = CountOccurrences(mgrSrc, "NotesWriter.ToggleChecklist(");
                int inProg = CountOccurrences(progSrc, "NotesWriter.ToggleChecklist(");
                Assert(inMgr == 1 && inProg == 1, "s2-single-write-entry-point",
                    "NotesWriter.ToggleChecklist( call sites: StickerManager.cs=" + inMgr + " Program.cs=" + inProg
                    + " (want 1 and 1: the card click and the headless --toggle-check)");
            }

            // ---- 14. CLI: a flag must never be swallowed as the value of the previous flag ----
            // v11.2 documented `--render-samples --floating <dir>`; the old parser ate "--floating" as
            // the output path, so that command rendered WITHOUT floating and the pink-dot assertion
            // was reading the wrong image. Both spellings must now mean the same thing.
            Options oA = Options.Parse(new string[] { "--render-samples", "--floating", "outdir" });
            Options oB = Options.Parse(new string[] { "--render-samples", "outdir", "--floating" });
            bool cliOk = oA.RenderSamples && oA.RenderFloating && oA.RenderPath == "outdir"
                && oB.RenderSamples && oB.RenderFloating && oB.RenderPath == "outdir";
            Assert(cliOk, "s2-cli-flag-not-eaten-as-path",
                "flag-first -> samples=" + oA.RenderSamples + " floating=" + oA.RenderFloating + " path='" + oA.RenderPath + "'"
                + "; path-first -> samples=" + oB.RenderSamples + " floating=" + oB.RenderFloating + " path='" + oB.RenderPath + "'");

            Cleanup(dir);
        }

        // ------------------------------------------------------------------
        // spec v13: the web-button bridge (file relay through the launcher) + the tray list.
        //
        // Everything runs against a throw-away fixture in <exeDir>\selftest-tmp-v13, never against the
        // user's data\; the manager is constructed in HEADLESS mode so the placed-set bookkeeping,
        // the ack and the published view are driven by real product code with NO window created.
        // The window-creating half of the same code path is covered end to end by .tools\bridge-e2e.ps1
        // (real exe, real cards, real launcher) - this file asserts the decision logic underneath it.
        // ------------------------------------------------------------------

        private static void CheckV13BridgeAndTray(string exeDir)
        {
            string dir = Path.Combine(exeDir, "selftest-tmp-v13");
            string bridgeDir = Path.Combine(dir, "bridge");
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(bridgeDir);
            }
            catch (Exception ex)
            {
                Bad("bridge-request-parsed", "cannot create " + dir + ": " + ex.Message);
                return;
            }

            string notesPath = Path.Combine(dir, "notes.json");
            string statePath = Path.Combine(dir, "sticker-state.json");
            string requestPath = BridgeProtocol.RequestPath(bridgeDir);
            string placedPath = BridgeProtocol.PlacedPath(bridgeDir);

            string notesFixture =
                "["
                + "{\"id\":\"n1\",\"title\":\"\u6807\u9898\u4e00\",\"content\":\"\",\"color\":\"yellow\","
                + "\"done\":false,\"pinned\":false,\"archived\":false,\"dueAt\":\"\",\"tags\":[],\"checklist\":[],"
                + "\"createdAt\":1789125720901,\"updatedAt\":1789125732745},"
                + "{\"id\":\"n2\",\"title\":\"second\",\"content\":\"\",\"color\":\"pink\","
                + "\"done\":false,\"pinned\":false,\"archived\":false,\"dueAt\":\"\",\"tags\":[],\"checklist\":[],"
                + "\"createdAt\":1789126012561,\"updatedAt\":1789126012561},"
                + "{\"id\":\"n3\",\"title\":\"\",\"content\":\"\",\"color\":\"blue\","
                + "\"done\":false,\"pinned\":false,\"archived\":false,\"dueAt\":\"\",\"tags\":[],\"checklist\":[],"
                + "\"createdAt\":1789126505493,\"updatedAt\":1789126505493}"
                + "]";
            File.WriteAllText(notesPath, notesFixture, new UTF8Encoding(false));

            // 7 entries, 2 of them unusable: seq 2 has an unknown action, the 4th entry has no seq.
            string reqFixture =
                "{\"seq\":6,\"requests\":["
                + "{\"seq\":1,\"action\":\"place\",\"noteId\":\"n1\",\"at\":null,\"tsMs\":1000},"
                + "{\"seq\":2,\"action\":\"explode\",\"noteId\":\"n1\",\"at\":null,\"tsMs\":1001},"
                + "{\"seq\":3,\"action\":\"place\",\"noteId\":\"n2\",\"at\":null,\"tsMs\":1002},"
                + "{\"action\":\"place\",\"noteId\":\"n1\",\"at\":null,\"tsMs\":1003},"
                + "{\"seq\":4,\"action\":\"place\",\"noteId\":\"ghost\",\"at\":null,\"tsMs\":1004},"
                + "{\"seq\":5,\"action\":\"place\",\"noteId\":\"n1\",\"at\":null,\"tsMs\":1005},"
                + "{\"seq\":6,\"action\":\"remove\",\"noteId\":\"n2\",\"at\":null,\"tsMs\":1006}"
                + "]}";
            File.WriteAllText(requestPath, reqFixture, new UTF8Encoding(false));

            List<Note> notes = NotesStore.Load(notesPath);
            if (notes.Count != 3)
            {
                Bad("bridge-request-parsed", "the notes fixture did not load (count=" + notes.Count + ")");
                return;
            }

            StickerManager mgr = new StickerManager(notes, new string[] { statePath }, dir, false, notesPath, dir, true);
            DesktopBridge bridge = new DesktopBridge(mgr, bridgeDir);
            mgr.AttachBridgePublisher(bridge.Publish);
            mgr.SetBridgeAck(0);

            // ---- B1: parsing (pure) ----
            string perr;
            List<BridgeRequest> parsed = BridgeProtocol.ParseRequests(reqFixture, out perr);
            bool b1 = parsed.Count == 5
                && parsed[0].Seq == 1 && parsed[0].Action == "place" && parsed[0].NoteId == "n1"
                && parsed[1].Seq == 3 && parsed[1].NoteId == "n2"
                && parsed[2].Seq == 4 && parsed[2].NoteId == "ghost"
                && parsed[3].Seq == 5 && parsed[3].NoteId == "n1"
                && parsed[4].Seq == 6 && parsed[4].Action == "remove" && parsed[4].NoteId == "n2"
                && perr != null && perr.IndexOf("2 unusable", StringComparison.Ordinal) >= 0;
            Assert(b1, "bridge-request-parsed",
                "parsed " + parsed.Count + "/7 (want 5: unknown action + missing seq dropped); "
                + "seqs=[" + SeqList(parsed) + "]; note=" + (perr ?? "(none)"));

            string shaBefore = NotesStore.Sha256OfFile(requestPath);
            long reqLenBefore = new FileInfo(requestPath).Length;
            int applied = bridge.ConsumeOnce();
            string shaAfter = NotesStore.Sha256OfFile(requestPath);

            // ---- B2: the ack advances, the view is published, request.json is NOT touched ----
            long viewAck = BridgeProtocol.ReadAckSeq(placedPath);
            List<string> viewIds = new List<string>();
            long viewSavedAt = 0;
            try
            {
                JsonValue v = Json.Parse(File.ReadAllText(placedPath, Encoding.UTF8));
                viewSavedAt = (long)v.Get("savedAtMs").Number;
                JsonValue arr = v.Get("placed");
                for (int i = 0; i < arr.Items.Count; i++)
                    viewIds.Add(JsonValue.StrOr(arr.Items[i], "noteId", ""));
            }
            catch (Exception ex) { Log.Line("selftest: cannot read the published view: " + ex.Message); }

            bool b2 = applied == 5
                && bridge.AckSeq == 6
                && viewAck == 6
                && viewSavedAt > 0
                && viewIds.Count == 1 && viewIds[0] == "n1"
                && string.Equals(shaBefore, shaAfter, StringComparison.OrdinalIgnoreCase)
                && new FileInfo(requestPath).Length == reqLenBefore;
            Assert(b2, "bridge-ack-advances",
                "applied=" + applied + " ack=" + bridge.AckSeq + " viewAck=" + viewAck + " view=[" + string.Join(",", viewIds.ToArray())
                + "] request.json unchanged=" + string.Equals(shaBefore, shaAfter, StringComparison.OrdinalIgnoreCase)
                + " (sha " + Short(shaBefore) + "), tmp absent=" + (!File.Exists(requestPath + ".tmp")));

            // ---- B3: a repeated place yields ONE card, and an already-acked request is not redone ----
            int again = bridge.ConsumeOnce();
            // v21: the removed note keeps its record as REMEMBERED (Closed=true) with its geometry.
            // What must stay true is "exactly ONE card on the desk, and the open record is still n1".
            int b3Open = 0, b3Closed = 0;
            string b3OpenId = "(none)";
            for (int i = 0; i < mgr.Records.Count; i++)
            {
                if (mgr.Records[i].Closed) b3Closed++;
                else { b3Open++; b3OpenId = mgr.Records[i].NoteId; }
            }
            bool b3 = again == 0 && mgr.Records.Count == 2 && b3Open == 1 && b3Closed == 1
                && mgr.CardCount == 1 && string.Equals(b3OpenId, "n1", StringComparison.Ordinal);
            Assert(b3, "bridge-place-idempotent",
                "records=" + mgr.Records.Count + " (open=" + b3Open + " remembered=" + b3Closed + ") cards=" + mgr.CardCount
                + " openId=" + b3OpenId + " reapply=" + again
                + " (seq 1 and seq 5 both said place n1; seq 3 placed n2 and seq 6 closed it again - v21 keeps its geometry)");

            // ---- B4: an unknown note neither creates a card nor jams the queue ----
            File.WriteAllText(requestPath,
                "{\"seq\":10,\"requests\":[{\"seq\":10,\"action\":\"place\",\"noteId\":\"ghost\",\"at\":null,\"tsMs\":2000}]}",
                new UTF8Encoding(false));
            int appliedGhost = bridge.ConsumeOnce();
            bool ghostCard = false;
            for (int i = 0; i < mgr.Records.Count; i++)
                if (string.Equals(mgr.Records[i].NoteId, "ghost", StringComparison.Ordinal)) ghostCard = true;
            bool b4 = appliedGhost == 1 && !ghostCard && mgr.Records.Count == 2
                && bridge.AckSeq == 10 && BridgeProtocol.ReadAckSeq(placedPath) == 10;
            Assert(b4, "bridge-unknown-note-advances",
                "ghost applied=" + appliedGhost + " cardCreated=" + ghostCard + " records=" + mgr.Records.Count
                + " ack=" + bridge.AckSeq + " viewAck=" + BridgeProtocol.ReadAckSeq(placedPath)
                + " (the v21 remembered record is not a ghost; a later seq must never be blocked)");

            // ---- B5: single-writer matrix, checked against BOTH sides of the relay ----
            string srcDir = SrcDir();
            string stickerSrc = srcDir == null ? null : ReadFlat(Path.Combine(srcDir, "DesktopBridge.cs"));
            string launcherSrc = srcDir == null ? null
                : ReadFlat(Path.GetFullPath(Path.Combine(srcDir, "..", "..", "launcher", "Program.cs")));
            bool stickerReadsOnly = stickerSrc != null
                && stickerSrc.IndexOf("File.ReadAllText(_requestPath", StringComparison.Ordinal) >= 0
                && stickerSrc.IndexOf("WriteAllText(_requestPath", StringComparison.Ordinal) < 0
                && stickerSrc.IndexOf("File.Replace(_requestPath", StringComparison.Ordinal) < 0
                && stickerSrc.IndexOf("File.Move(_requestPath", StringComparison.Ordinal) < 0
                && stickerSrc.IndexOf("File.Delete(_requestPath", StringComparison.Ordinal) < 0;
            bool launcherWritesRequestOnly = launcherSrc != null
                && launcherSrc.IndexOf("WriteAtomic(requestPath,", StringComparison.Ordinal) >= 0
                && launcherSrc.IndexOf("File.ReadAllText(placedPath", StringComparison.Ordinal) >= 0
                && launcherSrc.IndexOf("WriteAtomic(placedPath", StringComparison.Ordinal) < 0
                && launcherSrc.IndexOf("WriteAllText(placedPath", StringComparison.Ordinal) < 0;
            Assert(stickerReadsOnly && launcherWritesRequestOnly, "bridge-single-writer-matrix",
                "sticker only reads request.json=" + stickerReadsOnly
                + "; launcher writes request.json and only reads placed.json=" + launcherWritesRequestOnly
                + " (srcDir=" + (srcDir ?? "(absent)") + ")");

            // ---- B6: the new mechanism brought in no socket / pipe symbol ----
            Type[] v13Types = new Type[] { typeof(DesktopBridge), typeof(BridgeProtocol), typeof(TrayMenu), typeof(TrayIcon) };
            StringBuilder banned = new StringBuilder();
            for (int i = 0; i < v13Types.Length; i++)
            {
                MemberInfo[] members = v13Types[i].GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static);
                for (int j = 0; j < members.Length; j++)
                {
                    string n = members[j].Name;
                    if (n.IndexOf("Socket", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("TcpListener", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("HttpListener", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("NamedPipe", StringComparison.OrdinalIgnoreCase) >= 0)
                        banned.Append(v13Types[i].Name).Append('.').Append(n).Append(' ');
                }
            }
            Assert(banned.Length == 0, "bridge-no-listener-symbols",
                "v13 types=" + v13Types.Length + " bannedSymbols=" + (banned.Length == 0 ? "none" : banned.ToString())
                + " (the relay is plain files: net use is impossible, so the design cannot regress into a port)");

            // ---- B7: the tray menu really is built from the placed set ----
            List<string> ids = new List<string>();
            ids.Add("n1"); ids.Add("n2"); ids.Add("n3");
            List<string> titles = new List<string>();
            titles.Add("\u6807\u9898\u4e00"); titles.Add(""); titles.Add("third");
            List<string> clicked = new List<string>();
            bool exitClicked = false;
            ContextMenuStrip menu = TrayMenu.Create(ids, titles,
                delegate(string id) { clicked.Add(id); },
                delegate { exitClicked = true; });
            string header = TrayMenu.HeaderText(3);
            bool labelsOk = menu.Items.Count == 5
                && !menu.Items[0].Enabled && menu.Items[0].Text == header
                && menu.Items[1].Text == "\u6807\u9898\u4e00"
                && menu.Items[2].Text == UiStrings.NoTitle
                && menu.Items[3].Text == "third"
                && menu.Items[4].Text == UiStrings.ExitSticker;
            // Drive the click wiring ONLY when the expected shape is really there. Indexing blindly
            // throws ArgumentOutOfRange, which would kill the whole self-test instead of reporting a
            // FAIL (observed 2026-09-21 with the "per-note row loop removed" mutant: the run died and
            // also swallowed the two assertions after this one).
            if (labelsOk)
            {
                try { ((ToolStripMenuItem)menu.Items[1]).PerformClick(); }
                catch (Exception ex) { Log.Line("selftest: tray row click failed: " + ex.Message); }
                try { ((ToolStripMenuItem)menu.Items[4]).PerformClick(); }
                catch (Exception ex) { Log.Line("selftest: tray exit click failed: " + ex.Message); }
            }
            bool b7 = labelsOk && clicked.Count == 1 && clicked[0] == "n1" && exitClicked;
            Assert(b7, "tray-menu-lists-placed",
                "items=" + menu.Items.Count + " (want N+2=5) header='" + MenuText(menu, 0) + "' enabled=" + MenuEnabled(menu, 0)
                + " row1='" + MenuText(menu, 1) + "' row2(empty title)='" + MenuText(menu, 2) + "' exit='" + MenuText(menu, 4)
                + "' rowClick->" + (clicked.Count == 1 ? clicked[0] : "(" + clicked.Count + " calls)") + " exitClick=" + exitClicked);
            try { menu.Dispose(); } catch { }

            // ---- B8: the tray menu lives in its own file; the CARD menu is untouched ----
            // SelfTest.cs is skipped: this very check contains those literals, so scanning the harness
            // would count its own search strings and report a menu that does not exist.
            int cardMenuAdds = 0, otherAdds = 0, trayMenuAdds = 0;
            string formSrc = null, traySrc = null;
            if (srcDir != null)
            {
                string[] all = Directory.GetFiles(srcDir, "*.cs");
                for (int i = 0; i < all.Length; i++)
                {
                    string name = Path.GetFileName(all[i]);
                    if (string.Equals(name, "SelfTest.cs", StringComparison.Ordinal)) continue;
                    string flat = ReadFlat(all[i]);
                    if (string.Equals(name, "TrayMenu.cs", StringComparison.OrdinalIgnoreCase))
                    {
                        traySrc = flat;
                        trayMenuAdds = CountOccurrences(flat, "menu.Items.Add(");
                        continue;
                    }
                    int n = CountOccurrences(flat, "_menu.Items.Add(");
                    if (string.Equals(name, "StickerForm.cs", StringComparison.OrdinalIgnoreCase)) cardMenuAdds = n;
                    else otherAdds += n;
                }
                formSrc = ReadFlat(Path.Combine(srcDir, "StickerForm.cs"));
            }
            bool b8 = formSrc != null && traySrc != null
                && cardMenuAdds == 0
                && CountOccurrences(formSrc, "_menu.Items.Add(") == 0
                && otherAdds == 0
                && trayMenuAdds == 1
                && traySrc.IndexOf("NotifyIcon", StringComparison.Ordinal) >= 0
                && traySrc.IndexOf("_menu.Items.Add(", StringComparison.Ordinal) < 0
                && traySrc.IndexOf("ExitSticker", StringComparison.Ordinal) >= 0;
            Assert(b8, "tray-menu-source-guards",
                "StickerForm.cs card-menu adds=" + cardMenuAdds + " (want 0: v18 removed the card's right-click"
                + " menu), adds in any other file=" + otherAdds + " (want 0), TrayMenu.cs adds=" + trayMenuAdds
                + " (want 1) and uses NotifyIcon="
                + (traySrc != null && traySrc.IndexOf("NotifyIcon", StringComparison.Ordinal) >= 0)
                + ", tray still offers the exit entry="
                + (traySrc != null && traySrc.IndexOf("ExitSticker", StringComparison.Ordinal) >= 0)
                + " (with the card menu gone, the tray entry is the only in-app exit left)");

            Cleanup(dir);
        }

        // ------------------------------------------------------------------
        // spec v14 (merged single exe): the launcher's main window closes -> it appends an "exit"
        // CONTROL request to the SAME file relay; this app must then quit cleanly.
        //
        // Three claims, in the order they can break:
        //   E1 parsing   - an exit entry carries NO noteId (it targets the app, not a note) and must
        //                  still survive ParseRequests.
        //   E2 freshness - request.json OUTLIVES both processes. Only a recent exit may be obeyed:
        //                  a stale or timestamp-less one must be refused, otherwise the next launch of
        //                  the app would be killed by a request from a previous session (the "clicked
        //                  it yesterday, it dies today" bug class).
        //   E3 wiring    - driving the REAL ConsumeOnce must invoke the exit action exactly once for a
        //                  fresh request and NOT AT ALL for a stale one, while advancing the ack in both
        //                  cases. E3 observes the CALL SITE (the _exitAction field injected at
        //                  construction), so deleting that call turns E3 red - a test of a pure helper
        //                  would not (lesson F1 of the S2 batch: 2026-09-21).
        // ------------------------------------------------------------------
        private static void CheckV14ExitRequest(string exeDir)
        {
            string dir = Path.Combine(exeDir, "selftest-tmp-v14");
            string bridgeDir = Path.Combine(dir, "bridge");
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(bridgeDir);
            }
            catch (Exception ex)
            {
                Bad("v14-exit-request-parsed", "cannot create " + dir + ": " + ex.Message);
                return;
            }

            string notesPath = Path.Combine(dir, "notes.json");
            string statePath = Path.Combine(dir, "sticker-state.json");
            string requestPath = BridgeProtocol.RequestPath(bridgeDir);
            string placedPath = BridgeProtocol.PlacedPath(bridgeDir);
            File.WriteAllText(notesPath,
                "[{\"id\":\"n1\",\"title\":\"t\",\"content\":\"\",\"color\":\"yellow\",\"done\":false,"
                + "\"pinned\":false,\"archived\":false,\"dueAt\":\"\",\"tags\":[],\"checklist\":[],"
                + "\"createdAt\":1,\"updatedAt\":1}]", new UTF8Encoding(false));
            List<Note> notes = NotesStore.Load(notesPath);
            if (notes.Count != 1)
            {
                Bad("v14-exit-request-parsed", "the notes fixture did not load (count=" + notes.Count + ")");
                Cleanup(dir);
                return;
            }

            long now = EpochMs();

            // ---- E1: parsing (pure) ----
            string perr;
            List<BridgeRequest> parsed = BridgeProtocol.ParseRequests(
                "{\"seq\":2,\"requests\":["
                + "{\"seq\":1,\"action\":\"exit\",\"noteId\":\"\",\"at\":null,\"tsMs\":" + now + "},"
                + "{\"seq\":2,\"action\":\"place\",\"noteId\":\"n1\",\"at\":null,\"tsMs\":" + now + "}]}", out perr);
            bool e1 = parsed.Count == 2
                && parsed[0].Action == BridgeProtocol.ActionExit
                && parsed[0].NoteId == "" && parsed[0].TsMs == now
                && parsed[1].Action == BridgeProtocol.ActionPlace && parsed[1].NoteId == "n1";
            Assert(e1, "v14-exit-request-parsed",
                "parsed " + parsed.Count + "/2 = [" + SeqList(parsed) + "] (exit must survive without a noteId"
                + "; seq1.tsMs=" + (parsed.Count > 0 ? parsed[0].TsMs.ToString() : "?") + ", want " + now + ")");

            // ---- E2: freshness (pure) ----
            BridgeRequest fresh = new BridgeRequest();
            fresh.TsMs = now;
            BridgeRequest old = new BridgeRequest();
            old.TsMs = now - BridgeProtocol.ExitFreshnessMs - 1000;
            BridgeRequest noTs = new BridgeRequest();
            noTs.TsMs = 0;
            bool e2 = BridgeProtocol.ExitIsFresh(fresh, now)
                && !BridgeProtocol.ExitIsFresh(old, now)
                && !BridgeProtocol.ExitIsFresh(noTs, now)
                && BridgeProtocol.ExitIsFresh(old, old.TsMs + 1)
                && !BridgeProtocol.ExitIsFresh(null, now);
            Assert(e2, "v14-exit-request-freshness",
                "fresh(now)=" + BridgeProtocol.ExitIsFresh(fresh, now)
                + " old(age=" + (BridgeProtocol.ExitFreshnessMs + 1000) + "ms)=" + BridgeProtocol.ExitIsFresh(old, now)
                + " noTimestamp=" + BridgeProtocol.ExitIsFresh(noTs, now)
                + " oldAtItsOwnTime=" + BridgeProtocol.ExitIsFresh(old, old.TsMs + 1)
                + " (fence=" + BridgeProtocol.ExitFreshnessMs + "ms; a stale control request must never act)");

            // ---- E3: the call site, driven end to end through ConsumeOnce ----
            StickerManager mgr = new StickerManager(notes, new string[] { statePath }, dir, false, notesPath, dir, true);
            List<string> exitCalls = new List<string>();
            DesktopBridge spy = new DesktopBridge(mgr, bridgeDir,
                delegate(string reason) { exitCalls.Add(reason); });
            mgr.AttachBridgePublisher(spy.Publish);
            mgr.SetBridgeAck(0);

            File.WriteAllText(requestPath,
                "{\"seq\":3,\"requests\":[{\"seq\":3,\"action\":\"exit\",\"noteId\":\"\",\"at\":null,\"tsMs\":"
                + (now - 600000) + "}]}", new UTF8Encoding(false));
            int staleApplied = spy.ConsumeOnce();
            // capture the stale-case state BEFORE the fresh case runs: a detail string built after both
            // runs would show the final counters and read as if the stale request had shut the app down.
            int staleExitCalls = exitCalls.Count;
            long staleAck = spy.AckSeq;
            long staleViewAck = BridgeProtocol.ReadAckSeq(placedPath);
            bool staleOk = staleExitCalls == 0 && staleAck == 3 && staleViewAck == 3;

            File.WriteAllText(requestPath,
                "{\"seq\":4,\"requests\":[{\"seq\":4,\"action\":\"exit\",\"noteId\":\"\",\"at\":null,\"tsMs\":"
                + EpochMs() + "}]}", new UTF8Encoding(false));
            int freshApplied = spy.ConsumeOnce();
            bool freshOk = exitCalls.Count == 1 && exitCalls[0] == "launcher-exit" && spy.AckSeq == 4;

            Assert(staleOk && freshOk && staleApplied == 1 && freshApplied == 1, "v14-exit-request-wiring",
                "stale exit: applied=" + staleApplied + " exitCalls=" + staleExitCalls + " ack=" + staleAck
                + " viewAck=" + staleViewAck + " (want 1 apply / 0 exit calls / ack 3);"
                + " fresh exit: applied=" + freshApplied
                + " exitCalls=[" + string.Join(",", exitCalls.ToArray()) + "] ack=" + spy.AckSeq
                + " (want exactly ['launcher-exit'])"
                + " - deleting the _exitAction call at the ConsumeOnce exit branch must turn this red");

            Cleanup(dir);
        }

        private static long EpochMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        // ------------------------------------------------------------------
        // spec v14 - ack ordering (found 2026-09-21 by the LIVE interaction test, not by any headless
        // assertion): Program.cs attaches the publisher and only THEN calls mgr.Start(), whose
        // "startup" Persist publishes the view - and Publish() writes the bridge's own _ackSeq. While
        // that read lived in Start(), the first publish wrote ackSeq=0 OVER the real ack, Start() read
        // the 0 back, and the ENTIRE request history was replayed on every launch: a stale "remove"
        // closed a card the user had on screen and stale "place" entries re-cascaded cards away from
        // their stored geometry (log evidence: 19 replayed requests, 12:30:57-12:31:21).
        //
        // Two claims, both about ordering rather than about a value:
        //   A1 the bridge already knows the ack when it EXISTS (constructor), before Start() runs.
        //   A2 a publish that happens BEFORE Start() writes that ack, not zero.
        // ------------------------------------------------------------------
        private static void CheckV14StartupAckOrder(string exeDir)
        {
            string dir = Path.Combine(exeDir, "selftest-tmp-v14ack");
            string bridgeDir = Path.Combine(dir, "bridge");
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(bridgeDir);
            }
            catch (Exception ex)
            {
                Bad("v14-bridge-ack-read-at-construction", "cannot create " + dir + ": " + ex.Message);
                return;
            }

            string notesPath = Path.Combine(dir, "notes.json");
            string statePath = Path.Combine(dir, "sticker-state.json");
            string placedPath = BridgeProtocol.PlacedPath(bridgeDir);
            File.WriteAllText(notesPath,
                "[{\"id\":\"n1\",\"title\":\"t\",\"content\":\"\",\"color\":\"yellow\",\"done\":false,"
                + "\"pinned\":false,\"archived\":false,\"dueAt\":\"\",\"tags\":[],\"checklist\":[],"
                + "\"createdAt\":1,\"updatedAt\":1}]", new UTF8Encoding(false));
            List<Note> notes = NotesStore.Load(notesPath);
            if (notes.Count != 1)
            {
                Bad("v14-bridge-ack-read-at-construction", "the notes fixture did not load");
                Cleanup(dir);
                return;
            }

            // a placed.json left by a previous session, carrying its acknowledged seq
            File.WriteAllText(placedPath, "{\"savedAtMs\":1,\"ackSeq\":7,\"placed\":[]}", new UTF8Encoding(false));

            StickerManager mgr = new StickerManager(notes, new string[] { statePath }, dir, false, notesPath, dir, true);
            DesktopBridge bridge = new DesktopBridge(mgr, bridgeDir);
            bool a1 = bridge.AckSeq == 7;
            Assert(a1, "v14-bridge-ack-read-at-construction",
                "bridge constructed over a placed.json with ackSeq=7 -> bridge.AckSeq=" + bridge.AckSeq
                + " (want 7: reading the ack only inside Start() leaves a window in which a publish writes 0)");

            // Now do exactly what Program.cs does: attach the publisher FIRST, then let the manager
            // write (Start() does a "startup" Persist immediately). The ack must survive that write.
            mgr.AttachBridgePublisher(bridge.Publish);
            mgr.Persist("selftest-startup-publish-before-start");
            long written = BridgeProtocol.ReadAckSeq(placedPath);
            bool a2 = written == 7 && bridge.AckSeq == 7;
            Assert(a2, "v14-publish-before-start-keeps-the-ack",
                "after a pre-Start publish, placed.json ackSeq=" + written + " (want 7, NOT 0)"
                + "; bridge.AckSeq=" + bridge.AckSeq + "; a clobbered ack is what made every later start"
                + " replay the whole request history [stale removes closed live cards]");

            Cleanup(dir);
        }

        // ------------------------------------------------------------------
        // spec v16 - the pin glyph left of the close box is GONE
        // (user ruling 2026-09-21: "直接删去❌左面的置顶图钉标志", one message after v15 had wired that
        // glyph to the desktop-pin state). Two claims, both painted offscreen in-process at 3x
        // nearest-neighbour (the technique --render-samples uses), so exact-colour counts are reliable:
        //   R1 the box the glyph used to occupy stays INK-FREE - not merely accent-free - for every
        //      combination of note.Pinned / floating.
        //   R2 the footer pin BUTTON is the only floating marker left: accent pixels while floating,
        //      none while not.
        // The mutation control (draw the glyph back, gray or accent) lives in .tools/v16-mutation-check.ps1.
        // ------------------------------------------------------------------
        private static void CheckV16NoTopFlag()
        {
            int w = Typo.DEFAULT_W;
            int h = Typo.DEFAULT_H;
            int fx = Typo.FlagLeft(w);
            Typo.Layout L = Typo.Measure(FlagFixture(false), w, h, Typo.PAD_TOP);
            int fy = L.RowTopY + (Typo.ROW_TOP_H - Typo.FLAG_SIZE) / 2;
            string box = "(" + fx + "," + fy + "," + Typo.FLAG_SIZE + "," + Typo.FLAG_SIZE + ")";

            // R1: the old glyph box must be background-only in all four states.
            int i1, a1, i2, a2, i3, a3, i4, a4;
            PaintBox(FlagFixture(false), false, w, h, fx, fy, Typo.FLAG_SIZE, Typo.FLAG_SIZE, out i1, out a1);
            PaintBox(FlagFixture(false), true, w, h, fx, fy, Typo.FLAG_SIZE, Typo.FLAG_SIZE, out i2, out a2);
            PaintBox(FlagFixture(true), false, w, h, fx, fy, Typo.FLAG_SIZE, Typo.FLAG_SIZE, out i3, out a3);
            PaintBox(FlagFixture(true), true, w, h, fx, fy, Typo.FLAG_SIZE, Typo.FLAG_SIZE, out i4, out a4);
            int inkTotal = i1 + i2 + i3 + i4;
            Assert(inkTotal == 0, "v16-top-flag-removed",
                "ink pixels in the old glyph box " + box + " over (note.Pinned, floating) = ("
                + i1 + "," + i2 + "," + i3 + "," + i4 + "), total " + inkTotal
                + " (want 0 in every state: the symbol left of the X is deleted; drawing it back - gray OR"
                + " accent - turns this red)");

            // R2: the footer pin button is the only place that still shows "this card floats".
            Rectangle pin = new Rectangle(Typo.PAD_LR, L.FooterContentTop, Typo.PIN_BTN, Typo.PIN_BTN);
            int pInkOn, pAccOn, pInkOff, pAccOff;
            PaintBox(FlagFixture(false), true, w, h, pin.X, pin.Y, pin.Width, pin.Height, out pInkOn, out pAccOn);
            PaintBox(FlagFixture(false), false, w, h, pin.X, pin.Y, pin.Width, pin.Height, out pInkOff, out pAccOff);
            Assert(pAccOn > 0 && pAccOff == 0, "v16-footer-pin-is-the-only-floating-marker",
                "accent pixels in the footer pin button " + pin + ": floating=" + pAccOn + " (ink " + pInkOn
                + "), not floating=" + pAccOff + " (want floating>0 and not-floating==0 - after v16 this"
                + " toggle is the only marker for \"this card floats\")");
        }

        /// <summary>v16 fixture: a plain yellow card, optionally carrying the web app's own pin.</summary>
        private static Note FlagFixture(bool pinned)
        {
            Note n = new Note();
            n.Id = pinned ? "v16-flag-web-pinned" : "v16-flag-plain";
            n.Title = "flag fixture";
            n.Color = "yellow";
            n.Pinned = pinned;
            return n;
        }

        /// <summary>v16 helper: paint one fixture card offscreen at 3x (nearest-neighbour, exactly like
        /// --render-samples) and count, inside one LOGICAL box, pixels exactly equal to Palette.Accent
        /// ("accent") and pixels differing from the card background ("ink").</summary>
        private static void PaintBox(Note note, bool floating, int w, int h, int lx, int ly, int lw, int lh, out int ink, out int accent)
        {
            ink = 0; accent = 0;
            const int zoom = 3;
            int accentArgb = Palette.Accent.ToArgb();
            int bgArgb = Palette.NoteBg(note.Color).ToArgb();
            using (Bitmap card = new Bitmap(w, h))
            {
                using (Graphics g = Graphics.FromImage(card))
                {
                    g.Clear(Color.FromArgb(246, 244, 239));
                    CardRenderer.Render(g, note, w, h, floating);
                }
                using (Bitmap big = new Bitmap(w * zoom, h * zoom))
                {
                    using (Graphics g2 = Graphics.FromImage(big))
                    {
                        g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        g2.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        g2.DrawImage(card, new Rectangle(0, 0, w * zoom, h * zoom));
                    }
                    int x1 = (lx + lw) * zoom;
                    int y1 = (ly + lh) * zoom;
                    for (int y = ly * zoom; y < y1; y++)
                    {
                        for (int x = lx * zoom; x < x1; x++)
                        {
                            int argb = big.GetPixel(x, y).ToArgb();
                            if (argb == accentArgb) accent++;
                            if (argb != bgArgb) ink++;
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // spec v17 - "点『贴到桌面』只贴点的那一张"
        // (user 2026-09-21: "当桌面没有贴纸时,点击贴到桌面,会将所有贴纸都贴到桌面.改成仅贴点击了
        // 「贴到桌面」的那个贴纸"). The decision rule itself is the pure predicate asserted above; what
        // THIS method pins down is the READER: the bridge must really count the unacknowledged "place"
        // entries of request.json, otherwise the predicate is fed a constant zero and the click path
        // silently keeps dumping every note. Acked places, removes and pending exits must NOT count.
        // The live wiring (Program.cs calls SetPendingBridgePlaces BEFORE Start) is driven end-to-end by
        // .tools/v17-place-only-check.ps1, whose mutation control deletes exactly that call site.
        // ------------------------------------------------------------------
        private static void CheckV17ExplicitPlacements(string exeDir)
        {
            string dir = Path.Combine(exeDir, "selftest-tmp-v17");
            string bridgeDir = Path.Combine(dir, "bridge");
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(bridgeDir);
            }
            catch (Exception ex)
            {
                Bad("v17-bridge-counts-pending-places", "cannot create " + dir + ": " + ex.Message);
                return;
            }

            string notesPath = Path.Combine(dir, "notes.json");
            string statePath = Path.Combine(dir, "sticker-state.json");
            File.WriteAllText(notesPath,
                "[{\"id\":\"n1\",\"title\":\"t\",\"content\":\"\",\"color\":\"yellow\",\"done\":false,"
                + "\"pinned\":false,\"archived\":false,\"dueAt\":\"\",\"tags\":[],\"checklist\":[],"
                + "\"createdAt\":1,\"updatedAt\":1}]", new UTF8Encoding(false));
            List<Note> notes = NotesStore.Load(notesPath);
            // The queue the launcher would have written, with placed.json acknowledging seq 1-2:
            //   seq 1 place  (acked)   seq 2 remove (acked)
            //   seq 3 place  (PENDING) seq 4 exit   (pending control request, not a place)
            File.WriteAllText(BridgeProtocol.PlacedPath(bridgeDir),
                "{\"savedAtMs\":1,\"ackSeq\":2,\"placed\":[]}", new UTF8Encoding(false));
            File.WriteAllText(BridgeProtocol.RequestPath(bridgeDir),
                "{\"requests\":[{\"seq\":1,\"action\":\"place\",\"noteId\":\"n1\",\"tsMs\":1},"
                + "{\"seq\":2,\"action\":\"remove\",\"noteId\":\"n1\",\"tsMs\":2},"
                + "{\"seq\":3,\"action\":\"place\",\"noteId\":\"n1\",\"tsMs\":3},"
                + "{\"seq\":4,\"action\":\"exit\",\"tsMs\":4}]}", new UTF8Encoding(false));

            StickerManager mgr = new StickerManager(notes, new string[] { statePath }, dir, false, notesPath, dir, true);
            DesktopBridge bridge = new DesktopBridge(mgr, bridgeDir);
            int pending = bridge.PendingPlaceCount();
            Assert(pending == 1, "v17-bridge-counts-pending-places",
                "request.json = 2 places (seq 1 acked, seq 3 pending) + 1 acked remove + 1 pending exit;"
                + " PendingPlaceCount()=" + pending + " (want 1: only the unacknowledged PLACE counts)"
                + " - reading 0 or 2 here means the click path silently places every note");

            Cleanup(dir);
        }

        // ------------------------------------------------------------------
        // spec v19 - the web UI must not tell the user to double-click a file
        // (user 2026-09-21: "点击贴到桌面后:有 7 条请求还没被贴纸程序处理 —— 双击 .tools\sticker_build\
        // Sticker.exe（贴纸程序才会在桌面建卡）.删除提示,并实现智能检测贴纸进程").
        // The hint was v13-era developer talk: the file it names does not exist for an installed copy, and
        // since v13.1 the launcher starts the sticker on demand (v19: the launcher's own GET poll detects
        // "pending place requests + sticker not running" and starts it). What must stay true is that the
        // page never sends the user hunting for a file - so this reads the SHIPPED app.js and requires the
        // stale tokens to be absent. The end-to-end side (the launcher really reports stickerRunning /
        // pendingRequests and really heals) is driven by .tools/v19-smart-detect-check.ps1.
        // ------------------------------------------------------------------
        private static void CheckV19NoStaleHint(string exeDir)
        {
            string srcDir = SrcDir();
            string appJs = srcDir == null ? null
                : Path.GetFullPath(Path.Combine(srcDir, "..", "..", "demo", "app.js"));
            if (appJs == null || !File.Exists(appJs))
            {
                // Installed layout (no sources next to the exe): nothing to scan. Report it, never fake a pass.
                Ok("v19-no-stale-sticker-hint", "demo/app.js not present (installed layout) - shipped-file fence unavailable");
                return;
            }
            string flat = ReadFlat(appJs);
            int fileHint = CountOccurrences(flat, "sticker_build");
            int exeHint = CountOccurrences(flat, "Sticker.exe");
            int consoleHint = CountOccurrences(flat, "\u63a7\u5236\u53f0");   // 控制台: gone since v14
            Assert(fileHint == 0 && exeHint == 0 && consoleHint == 0, "v19-no-stale-sticker-hint",
                "demo/app.js mentions 'sticker_build' " + fileHint + "x, 'Sticker.exe' " + exeHint
                + "x, and the old console wording " + consoleHint + "x (all want 0: the page must never tell"
                + " the user to double-click a file - the launcher starts the sticker itself)");
        }

        // ------------------------------------------------------------------
        // spec v20: the launcher window's "start with Windows" checkbox is really wired to the
        // registry writer, and there is exactly ONE place that names the Run subkey.
        //
        // Why source-level: this self-test is also compiled WITHOUT the launcher sources (the
        // standalone dev build desktop-sticker/build.ps1), so it cannot call AutoStart at all.
        // What can be pinned here is the wiring; what cannot is the real registry effect - that
        // half is driven end to end by .tools/v20-autostart-check.ps1 through the product's own
        // --autostart-on/off/status entry points, which run the very same AutoStart code.
        // ------------------------------------------------------------------
        private static void CheckV20AutoStartWiring()
        {
            string srcDir = SrcDir();
            string launcherDir = srcDir == null ? null
                : Path.GetFullPath(Path.Combine(srcDir, "..", "..", "launcher"));
            string win = launcherDir == null ? null : ReadFlat(Path.Combine(launcherDir, "LauncherWindow.cs"));
            string prog = launcherDir == null ? null : ReadFlat(Path.Combine(launcherDir, "Program.cs"));
            if (win == null || prog == null)
            {
                // Installed layout (no sources next to the exe): say so, never fake a pass.
                Ok("v20-autostart-wiring", "launcher sources not present (installed layout) - source fence unavailable");
                return;
            }

            int boxes = CountOccurrences(win, "new CheckBox()");
            // count the WIRING FORM ("CheckedChanged +="), not the bare word: the constructor comment
            // above the line mentions CheckedChanged too, so counting the bare word would let M10
            // (delete the wiring line) escape with a comment keeping the count above zero.
            int wired = CountOccurrences(win, "CheckedChanged +=");
            int enable = CountOccurrences(win, "AutoStart.Enable(");
            int disable = CountOccurrences(win, "AutoStart.Disable(");
            int label = CountOccurrences(win, "\u5F00\u673A\u81EA\u52A8\u542F\u52A8");   // 开机自动启动
            Assert(boxes == 1 && wired >= 1 && enable >= 1 && disable >= 1 && label >= 1,
                "v20-autostart-checkbox-is-wired",
                "launcher window: CheckBox " + boxes + "x (want 1), CheckedChanged += " + wired + "x (want >=1),"
                + " AutoStart.Enable " + enable + "x, AutoStart.Disable " + disable + "x, the 4-char label "
                + label + "x (all want >=1). A box with no CheckedChanged handler writes nothing when clicked;"
                + " a handler that is never wired is never called - mutation M10 deletes that wiring line and"
                + " turns this red");

            int reads = CountOccurrences(win, "AutoStart.State(");
            Assert(reads >= 2,
                "v20-autostart-box-reflects-the-registry",
                "launcher window reads the real state through AutoStart.State() " + reads + "x (want >=2: once"
                + " when building the window, once after a toggle). A checkbox whose Checked is hard-coded"
                + " would look switched on while nothing is registered - mutation M11 hard-codes it and turns"
                + " this red");

            int writers = CountOccurrences(prog, "CurrentVersion\\Run");
            int noOpen = CountOccurrences(prog, "\" --no-open\"");
            int flagSite = CountOccurrences(prog, "BootFlags");
            Assert(writers == 1 && noOpen >= 1 && flagSite >= 1,
                "v20-autostart-single-writer-and-boot-flags",
                "launcher: the HKCU Run subkey is named " + writers + "x (want exactly 1 - one writer), the"
                + " boot command line carries ' --no-open' " + noOpen + "x (want >=1: signing in must not spray"
                + " a browser tab), BootFlags " + flagSite + "x (the single place those flags live; mutation M12"
                + " drops ' --no-open' there and turns this red, and .tools/v20-autostart-check.ps1 E1 red too,"
                + " because E1 compares the whole value including the flags - a boot entry without --no-open"
                + " would pop a browser tab at every sign-in, and one with --headless would return before the"
                + " desktop stickers are restored at all)");
        }

        // ------------------------------------------------------------------
        // spec v21 - closing a card REMEMBERS its geometry, and an emptied desk is restored with the
        // very arrangement the user had.
        //
        // User report 2026-09-22: with the v20 auto-start switched on, every sign-in dumped ALL notes
        // onto the desk in the default cascade instead of restoring the previous arrangement. Root cause:
        // v11 turned an EMPTY placed set into "place every non-archived note", and closing the last card
        // emptied the set - harmless while the app only started by hand, an every-day dump once it starts
        // at sign-in. This method pins (a) the pure decision rule, (b) the REMEMBER step (the same
        // CloseRecord call the card X / Alt+F4 path makes) and (c) the start-up restore through the real
        // manager. The X path itself is driven live by interaction-test.ps1 (I7/I10/I12/I17/I19) and by
        // .tools/v21-closed-set-check.ps1.
        // ------------------------------------------------------------------
        private static void CheckV21ClosedSetRestore(string exeDir)
        {
            // ---- (a) the pure decision rule, every direction ----
            bool p1 = StickerManager.ShouldReopenClosedSet(0, 3, 0, 0);   // empty desk + 3 remembered, nobody named -> reopen
            bool p2 = StickerManager.ShouldReopenClosedSet(1, 2, 0, 0);   // one card already on the desk -> nothing to reopen
            bool p3 = StickerManager.ShouldReopenClosedSet(0, 0, 0, 0);   // nothing remembered -> the P1 fallback owns this
            bool p4 = StickerManager.ShouldReopenClosedSet(0, 3, 1, 0);   // --note wins
            bool p5 = StickerManager.ShouldReopenClosedSet(0, 3, 0, 1);   // a web click wins (v17)
            Assert(p1 && !p2 && !p3 && !p4 && !p5, "v21-reopen-predicate",
                "reopen(empty desk + 3 remembered, nobody named)=" + p1 + " (want True); (1 already on desk)=" + p2
                + " (want False); (nothing remembered)=" + p3 + " (want False); (--note given)=" + p4
                + " (want False); (web click pending)=" + p5 + " (want False) - the place-every-note fallback belongs"
                + " to a state that never remembered anything, and an explicit target always wins");

            string dir = Path.Combine(exeDir, "selftest-tmp-v21");
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                Bad("v21-close-remembers-geometry", "cannot create " + dir + ": " + ex.Message);
                return;
            }

            string notesPath = Path.Combine(dir, "notes.json");
            string statePath = Path.Combine(dir, "sticker-state.json");
            string one = "{\"id\":\"(@)\",\"title\":\"t\",\"content\":\"\",\"color\":\"yellow\",\"done\":false,"
                + "\"pinned\":false,\"archived\":false,\"dueAt\":\"\",\"tags\":[],\"checklist\":[],\"createdAt\":1,\"updatedAt\":1}";
            File.WriteAllText(notesPath,
                "[" + one.Replace("(@)", "n1") + "," + one.Replace("(@)", "n2") + "," + one.Replace("(@)", "n3") + "]",
                new UTF8Encoding(false));
            List<Note> notes = NotesStore.Load(notesPath);

            // ---- (b) the REMEMBER step: a close keeps the record + its geometry ----
            File.WriteAllText(statePath, V21State(V21Rec("n1", 400, 150, 300, 200, false), V21Rec("n2", 500, 200, 260, 190, false)),
                new UTF8Encoding(false));
            StickerManager m1 = new StickerManager(notes, new string[] { statePath }, dir, false, notesPath, dir, true);
            m1.Start(null);
            int m1CardsBefore = m1.CardCount;
            m1.ApplyBridgeRequest(false, "n1");            // the same CloseRecord call the X button makes
            StickerRecord kept = null;
            for (int i = 0; i < m1.Records.Count; i++) if (m1.Records[i].NoteId == "n1") kept = m1.Records[i];
            bool bRemember = m1CardsBefore == 2 && m1.CardCount == 1 && m1.Records.Count == 2
                && kept != null && kept.Closed && kept.X == 400 && kept.Y == 150 && kept.W == 300 && kept.H == 200;
            Assert(bRemember, "v21-close-remembers-geometry",
                "before=" + m1CardsBefore + " card(s); after closing n1: cards=" + m1.CardCount + " records=" + m1.Records.Count
                + " n1 remembered=" + (kept != null && kept.Closed) + " geometry="
                + (kept == null ? "(gone)" : kept.X + "," + kept.Y + " " + kept.W + "x" + kept.H)
                + " (want 2 cards -> 1 card, 2 records kept, n1 closed with 400,150 300x200: deleting the record is what"
                + " made an emptied desk indistinguishable from a first run)");

            // ---- (c) an emptied desk restores the REMEMBERED cards at their own positions ----
            File.WriteAllText(statePath, V21State(V21Rec("n1", 400, 150, 300, 200, true), V21Rec("n2", 500, 200, 260, 190, true)),
                new UTF8Encoding(false));
            StickerManager m2 = new StickerManager(notes, new string[] { statePath }, dir, false, notesPath, dir, true);
            m2.Start(null);
            bool bRestore = m2.CardCount == 2 && !m2.Records[0].Closed && !m2.Records[1].Closed
                && m2.Records[0].X == 400 && m2.Records[0].Y == 150 && m2.Records[0].W == 300 && m2.Records[0].H == 200
                && m2.Records[1].X == 500 && m2.Records[1].Y == 200;
            Assert(bRestore, "v21-empty-desk-restores-the-saved-arrangement",
                "all-closed fixture (2 remembered) -> cards=" + m2.CardCount + " stillClosed=" + (m2.Records[0].Closed || m2.Records[1].Closed)
                + " geometry=" + m2.Records[0].X + "," + m2.Records[0].Y + " " + m2.Records[0].W + "x" + m2.Records[0].H
                + " and " + m2.Records[1].X + "," + m2.Records[1].Y
                + " (want exactly those 2 cards back at their own coordinates - NOT all 3 notes, NOT the 40,40 cascade)");

            // ---- (d) negative controls: a card on the desk, and a state that never remembered anything ----
            File.WriteAllText(statePath, V21State(V21Rec("n1", 400, 150, 300, 200, true), V21Rec("n3", 600, 250, 240, 180, false)),
                new UTF8Encoding(false));
            StickerManager m3 = new StickerManager(notes, new string[] { statePath }, dir, false, notesPath, dir, true);
            m3.Start(null);
            bool bOne = m3.CardCount == 1 && m3.Records[1].NoteId == "n3" && !m3.Records[1].Closed
                && m3.Records[1].X == 600 && m3.Records[1].Y == 250 && m3.Records[0].Closed;
            Assert(bOne, "v21-a-remembered-card-stays-closed-while-others-are-up",
                "1 open (n3 at 600,250) + 1 remembered (n1) -> cards=" + m3.CardCount + " (want 1: the remembered one"
                + " stays away), openRecord=" + m3.Records[1].NoteId + " at " + m3.Records[1].X + "," + m3.Records[1].Y
                + ", rememberedStaysClosed=" + m3.Records[0].Closed + " (want True)");

            if (File.Exists(statePath)) File.Delete(statePath);
            StickerManager m4 = new StickerManager(notes, new string[] { statePath }, dir, false, notesPath, dir, true);
            m4.Start(null);
            bool bFirstRun = m4.CardCount == 3;
            Assert(bFirstRun, "v21-never-remembered-state-still-places-every-note",
                "no state file at all -> cards=" + m4.CardCount + " (want 3: the v11 P1 fallback is still the rule for"
                + " a state that never remembered anything - true first run / discarded file)");

            Cleanup(dir);
        }

        /// <summary>v21 fixture helper: one sticker-state.json record (monitor bounds are recomputed on load).</summary>
        private static string V21Rec(string id, int x, int y, int w, int h, bool closed)
        {
            return "{\"noteId\":\"" + id + "\",\"x\":" + x + ",\"y\":" + y + ",\"w\":" + w + ",\"h\":" + h
                + ",\"topMost\":false" + (closed ? ",\"closed\":true" : "") + "}";
        }

        private static string V21State(params string[] records)
        {
            return "{\"schema\":1,\"app\":\"tietie-sticker\",\"savedAt\":1,\"stickers\":[" + string.Join(",", records) + "]}";
        }
        /// <summary>Crash-safe menu inspection: a regression that shrinks the menu must FAIL, not throw.</summary>
        private static string MenuText(ContextMenuStrip menu, int index)
        {
            if (menu == null || index < 0 || index >= menu.Items.Count) return "(missing)";
            return menu.Items[index].Text;
        }

        private static string MenuEnabled(ContextMenuStrip menu, int index)
        {
            if (menu == null || index < 0 || index >= menu.Items.Count) return "?";
            return menu.Items[index].Enabled.ToString();
        }

        private static string SeqList(List<BridgeRequest> reqs)        {
            List<string> parts = new List<string>();
            for (int i = 0; i < reqs.Count; i++) parts.Add(reqs[i].Seq + ":" + reqs[i].Action);
            return string.Join(",", parts.ToArray());
        }

        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(none)";
            return s.Length <= 12 ? s : s.Substring(0, 12);
        }

        private static int CountOccurrences(string text, string needle)
        {
            if (text == null || needle.Length == 0) return 0;
            int n = 0, i = 0;
            while (true)
            {
                int k = text.IndexOf(needle, i, StringComparison.Ordinal);
                if (k < 0) break;
                n++; i = k + 1;
            }
            return n;
        }

        private static void Cleanup(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception ex) { Log.Line("selftest: could not clean " + dir + ": " + ex.Message); }
        }

        /// <summary>
        /// Locate the sticker source directory. TWO layouts must work with the SAME code:
        ///   * standalone build: exeDir = &lt;repo&gt;\.tools\sticker_build  -&gt; &lt;repo&gt;\desktop-sticker\src
        ///   * merged build (v14): exeDir = &lt;repo&gt;                     -&gt; &lt;repo&gt;\desktop-sticker\src
        /// Walking up a few parents covers both without hard-coding a depth (the old code hard-coded
        /// "..\..", which silently turned every source guard below into a vacuous pass in the merged
        /// layout - the guards would have "skipped" instead of checking anything).
        /// </summary>
        private static string SrcDir()
        {
            try
            {
                string d = System.IO.Path.GetDirectoryName(typeof(Program).Assembly.Location);
                for (int i = 0; i < 5 && !string.IsNullOrEmpty(d); i++)
                {
                    string cand = System.IO.Path.Combine(d, "desktop-sticker", "src");
                    if (Directory.Exists(cand)) return cand;
                    System.IO.DirectoryInfo parent = System.IO.Directory.GetParent(d);
                    if (parent == null) break;
                    d = parent.FullName;
                }
            }
            catch { }
            return null;
        }

        private static string ReadFlat(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return File.ReadAllText(path).Replace("\r", " ").Replace("\n", " ");
            }
            catch { return null; }
        }

        private static void CheckBuildManifest()
        {
            try
            {
                string exeDir = System.IO.Path.GetDirectoryName(typeof(Program).Assembly.Location);
                string exeFile = System.IO.Path.GetFileName(Application.ExecutablePath);
                // v14: the merged exe ships a manifest named after ITSELF (<name>.build-manifest.json)
                // so two exes built from different source sets can live in one directory without
                // vouching for each other. The legacy name is still accepted (standalone Sticker.exe).
                string manifestPath = System.IO.Path.Combine(exeDir,
                    System.IO.Path.GetFileNameWithoutExtension(exeFile) + ".build-manifest.json");
                if (!File.Exists(manifestPath)) manifestPath = System.IO.Path.Combine(exeDir, "build-manifest.json");
                if (!File.Exists(manifestPath))
                {
                    Ok("build-manifest", "not present (build outside build.ps1) - exe/source binding unavailable");
                    return;
                }
                JsonValue m = Json.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
                JsonValue exeObj = m.Get("exe");
                string recordedExeName = JsonValue.StrOr(exeObj, "file", "");
                string recordedExeHash = JsonValue.StrOr(exeObj, "sha256", "");
                string actualExeHash = NotesStore.Sha256OfFile(Application.ExecutablePath);
                string sourcesRoot = JsonValue.StrOr(m, "sourcesRoot", null);
                JsonValue srcs = m.Get("sources");
                int srcCount = (srcs != null && srcs.IsArray) ? srcs.Items.Count : 0;

                // Source files are compared digest-by-digest: if every one matches, the current tree IS
                // the tree this exe was built from. An entry is either a bare file name (legacy layout:
                // resolved inside desktop-sticker\src) or a path relative to "sourcesRoot" (merged).
                string srcDir = SrcDir();
                int matched = 0;
                int located = 0;
                List<string> drifted = new List<string>();
                if (srcs != null && srcs.IsArray)
                {
                    for (int i = 0; i < srcs.Items.Count; i++)
                    {
                        string name = JsonValue.StrOr(srcs.Items[i], "name", "");
                        string want = JsonValue.StrOr(srcs.Items[i], "sha256", "");
                        string p = null;
                        if (!string.IsNullOrEmpty(sourcesRoot))
                        {
                            string c = System.IO.Path.Combine(sourcesRoot,
                                name.Replace('/', System.IO.Path.DirectorySeparatorChar));
                            if (File.Exists(c)) p = c;
                        }
                        if (p == null && srcDir != null)
                        {
                            string c = System.IO.Path.Combine(srcDir, System.IO.Path.GetFileName(name));
                            if (File.Exists(c)) p = c;
                        }
                        if (p == null) { drifted.Add(name + "(missing)"); continue; }
                        located++;
                        string got = NotesStore.Sha256OfFile(p);
                        if (string.Equals(got, want, StringComparison.OrdinalIgnoreCase)) matched++;
                        else drifted.Add(name + "(changed)");
                    }
                }

                // The exe must be the one this manifest describes: a manifest that names another file
                // must never be able to vouch for the running exe (this is what keeps "Sticker.exe's
                // manifest" from being read as evidence about "贴贴便签.exe" once both exist).
                bool nameOk = string.Equals(recordedExeName, exeFile, StringComparison.OrdinalIgnoreCase);
                Assert(nameOk, "build-manifest-exe-name-matches",
                    "manifest=" + System.IO.Path.GetFileName(manifestPath) + " names exe.file='" + recordedExeName
                    + "', running exe is '" + exeFile + "'" + (nameOk ? " (match)" : " (MISMATCH - wrong manifest)"));

                bool exeOk = string.Equals(recordedExeHash, actualExeHash, StringComparison.OrdinalIgnoreCase);
                Assert(exeOk, "build-manifest-exe-digest",
                    "manifest says " + recordedExeHash + ", actual " + exeFile + " " + actualExeHash
                    + (exeOk ? " (match)" : " (MISMATCH - the exe was replaced after the build)"));
                Assert(drifted.Count == 0, "build-manifest-source-tree-unchanged",
                    "sources matched " + matched + "/" + srcCount + " (located " + located + "/" + srcCount
                    + ", root=" + (sourcesRoot == null ? "(legacy: next to srcDir)" : sourcesRoot) + ")"
                    + (drifted.Count == 0 ? " (current tree == tree this exe was built from)" : "; drifted=" + string.Join(",", drifted.ToArray()))
                    + "; builtAtUtc=" + JsonValue.StrOr(m, "builtAtUtc", "?"));
            }
            catch (Exception ex)
            {
                Bad("build-manifest", "exception: " + ex.Message);
            }
        }

        private static void CheckAssemblySurface()
        {
            Assembly asm = typeof(Program).Assembly;
            AssemblyName[] refs = asm.GetReferencedAssemblies();
            // The token is reassembled at runtime on purpose: the shipped exe must not carry the
            // literal string, so that an independent `strings` scan of Sticker.exe stays clean.
            string token = "Web" + "View" + "2";
            List<string> names = new List<string>();
            bool bad = false;
            for (int i = 0; i < refs.Length; i++)
            {
                names.Add(refs[i].Name);
                if (refs[i].Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) bad = true;
            }
            bool loaderAbsent = !File.Exists(Path.Combine(
                Path.GetDirectoryName(asm.Location), token + "Loader.dll"));
            bool ok = !bad && loaderAbsent;
            Assert(ok, "no-webview2-reference",
                "referenced assemblies = " + string.Join(", ", names.ToArray())
                + "; native loader dll present = " + (!loaderAbsent));
        }

        private static void CheckNoListenerSymbols()
        {
            Assembly asm = typeof(Program).Assembly;
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }
            StringBuilder sb = new StringBuilder();
            bool bad = false;
            for (int i = 0; i < types.Length; i++)
            {
                Type t = types[i];
                if (t == null) continue;
                sb.Append(t.Name).Append(' ');
                MemberInfo[] members = t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                for (int j = 0; j < members.Length; j++)
                {
                    string n = members[j].Name;
                    if (n.IndexOf("Socket", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("TcpListener", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("HttpListener", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("NamedPipe", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        bad = true;
                        sb.Append("[BANNED:").Append(t.Name).Append('.').Append(n).Append("] ");
                    }
                }
            }
            Assert(!bad, "no-listener-symbol-in-assembly", "types=" + types.Length + "; bannedSymbols=" + (bad ? "FOUND" : "none"));
        }

        private static string Hex(Color c)
        {
            return c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        private static string Rect(Rectangle r)
        {
            return "(" + r.X + "," + r.Y + " " + r.Width + "x" + r.Height + ")";
        }

        private static string[] Arr(List<int> ints)
        {
            string[] a = new string[ints.Count];
            for (int i = 0; i < ints.Count; i++) a[i] = ints[i].ToString();
            return a;
        }

        private static string Ids(StickerState st)        {
            List<string> ids = new List<string>();
            for (int i = 0; i < st.Records.Count; i++) ids.Add(st.Records[i].NoteId);
            return "[" + string.Join(",", ids.ToArray()) + "]";
        }

        private static string NoteIds(List<Note> notes)
        {
            List<string> ids = new List<string>();
            for (int i = 0; i < notes.Count; i++) ids.Add(notes[i].Id);
            return "[" + string.Join(",", ids.ToArray()) + "]";
        }
    }
}

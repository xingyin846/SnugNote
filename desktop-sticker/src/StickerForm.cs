// S1 sticker: the frameless desktop card window.
// Pure WinForms / GDI+ painting. NO WebView2, NO child controls, NO network, NO listening socket.
// Pure ASCII source.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TieTieSticker
{
    internal enum HitZone
    {
        None, Client, Pin, Close, Checklist,
        Left, Right, Top, Bottom,
        TopLeft, TopRight, BottomLeft, BottomRight
    }

    internal sealed class StickerForm : Form
    {
        private readonly StickerManager _mgr;
        private Note _note;

        private Region _roundRegion;
        private bool _pinOn;

        // drag / resize state
        private bool _dragging;
        private Point _dragGrab;          // cursor - window origin at mouse down
        private bool _resizing;
        private HitZone _resizeZone;
        private Rectangle _resizeStartBounds;
        private Point _resizeStartCursor;
        private bool _layoutOverflowLogged;
        private bool _paintLogged;
        // spec v11 3.5: a left press inside the close hit box arms a pending close; the close only
        // fires when the matching MouseUp still lands inside the SAME rectangle (press-and-slide-away
        // cancels). While armed, neither drag nor resize is started.
        private bool _closeArmed;

        // spec v12 3.x (S2): a left press inside a checklist item band arms a pending check with the
        // same press/release rule. Because the band is wide, sliding past SystemInformation.DragSize
        // converts the pending check into a normal drag (so the card stays draggable from anywhere).
        private bool _checkArmed;
        private int _checkIndex = -1;
        private Rectangle _checkRect;
        private Point _pressScreen;

        // spec v12 3.x: the transient on-card notice (no modal box, no console - the exe is /winexe)
        private string _noticeText;
        private DateTime _noticeUntil = DateTime.MinValue;
        private Timer _noticeTimer;

        public string NoteId { get { return _note.Id; } }
        public Note Note { get { return _note; } }
        public bool PinOn { get { return _pinOn; } }

        public StickerForm(StickerManager mgr, Note note, Rectangle bounds, bool topMost)
        {
            _mgr = mgr;
            _note = note;
            _pinOn = topMost;

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            // spec v16 (user ruling 2026-09-21): "任务栏的贴纸窗应该预览贴纸窗口内容（有几个贴纸就有几个窗）".
            // Every card is a real taskbar window of its own (WinForms then sets WS_EX_APPWINDOW), so the
            // taskbar shows one button per sticker and hovering it previews that card's live content.
            // This deliberately supersedes the S1-era "无边框 / ShowInTaskbar=false" row: the user changed
            // the requirement. window title = the note title (set below) so the button/tooltip is readable.
            ShowInTaskbar = true;
            // Borderless card + taskbar button: without this the taskbar's window menu would offer
            // 最大化/最小化, and a maximised borderless card is a broken-looking full-screen sticker.
            MinimizeBox = false;
            MaximizeBox = false;
            TopMost = topMost;
            Text = string.IsNullOrEmpty(note.Title) ? ("sticker-" + note.Id) : note.Title;
            BackColor = Palette.NoteBg(note.Color);
            MinimumSize = new Size(Typo.MIN_W, Typo.MIN_H);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            Bounds = bounds;
            Log.Line("ctor  " + note.Id.Substring(0, Math.Min(8, note.Id.Length)) + " requested=" + BoundsToString(bounds) + " -> " + NativeMethods.GeometryDiag(this));

            // spec v18 (user ruling 2026-09-21): "去除右键桌面贴纸的『退出贴纸』功能". The card used to own a
            // one-entry right-click menu (that exit item, spec v11 7.4 / P5). It is gone and no menu is
            // attached any more, so a right click on a card does nothing at all.
            // Exit paths that remain: the tray menu's 退出贴纸 (TrayMenu.cs), the launcher window's X /
            // 退出 button (v14 exit control request), and closing the last card (v11 3.9).
            // Assertion v18-card-right-click-has-no-menu (SelfTest) requires this file to name neither the
            // menu TYPE nor the menu FIELD; the live check .tools/v18-no-card-menu-check.ps1 catches a menu
            // that actually pops up on screen.
        }

        /// <summary>CreateParams: CS_DROPSHADOW (U1). Falls back to no shadow automatically.</summary>
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                try { cp.ClassStyle |= 0x00020000; } catch { }
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RebuildRegion();
            // U4 assertion: Size == ClientSize must hold with FormBorderStyle.None.
            Log.Line("card created noteId=" + _note.Id + " handle=0x" + Handle.ToInt64().ToString("X")
                + " " + NativeMethods.GeometryDiag(this)
                + " TopMost=" + TopMost + " ShowInTaskbar=" + ShowInTaskbar
                + " FormBorderStyle=" + FormBorderStyle + " AutoScaleMode=" + AutoScaleMode);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Log.Line("card shown  noteId=" + _note.Id + " " + NativeMethods.GeometryDiag(this));
        }

        internal static string BoundsToString(Rectangle r)
        {
            return "(" + r.X + "," + r.Y + " " + r.Width + "x" + r.Height + ")";
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RebuildRegion();
            Invalidate();
        }

        private void RebuildRegion()
        {
            try
            {
                using (GraphicsPath p = Geometry.RoundedRect(new Rectangle(0, 0, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height)), Typo.RADIUS))
                {
                    Region r = new Region(p);
                    if (_roundRegion != null) _roundRegion.Dispose();
                    _roundRegion = r;
                    Region = r;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("RebuildRegion", ex);
            }
        }

        // ------------------------------------------------------------------
        // painting
        // ------------------------------------------------------------------

        private Typo.Layout CurrentLayout()
        {
            return Typo.Measure(_note, ClientSize.Width, ClientSize.Height, Typo.PAD_TOP);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // fully custom painting; nothing to do
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                if (!_paintLogged)
                {
                    _paintLogged = true;
                    Log.Line("first paint noteId=" + _note.Id + " " + NativeMethods.GeometryDiag(this));
                }
                CardRenderer.Render(e.Graphics, _note, ClientSize.Width, ClientSize.Height, _pinOn);
                if (_noticeText != null) DrawNotice(e.Graphics);
                Typo.Layout L = CurrentLayout();
                if (L.ContentEnd > L.FooterTop && !_layoutOverflowLogged)
                {
                    _layoutOverflowLogged = true;
                    Log.Line("card " + _note.Id + " content overflows: contentEnd=" + L.ContentEnd + " footerTop=" + L.FooterTop + " (clipped from bottom, no scrollbar)");
                }
            }
            catch (Exception ex)
            {
                Log.Exception("OnPaint", ex);
            }
        }

        /// <summary>
        /// S2 3.3: the transient notice. Drawn on top of the card (the exe has no console and no tray,
        /// so a log line would be invisible to the user) and it never touches the card layout or the
        /// offscreen renderer, so the sample PNGs stay byte-comparable.
        /// </summary>
        private void DrawNotice(Graphics g)
        {
            try
            {
                Typo.Layout L = CurrentLayout();
                int width = ClientSize.Width - 2 * Typo.PAD_LR;
                if (width < 40) return;
                int h = 26;
                int y = L.FooterTop - h - 6;
                if (y < Typo.PAD_TOP) y = Typo.PAD_TOP;
                Rectangle r = new Rectangle(Typo.PAD_LR, y, width, h);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(232, 0x24, 0x21, 0x2B)))
                using (GraphicsPath p = Geometry.RoundedRect(r, 6))
                {
                    g.FillPath(b, p);
                }
                TextRenderer.DrawText(g, _noticeText, Typo.Due, r, Color.White,
                    TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            catch (Exception ex)
            {
                Log.Exception("DrawNotice", ex);
            }
        }

        /// <summary>Show a short-lived notice on this card (S2 conflict / failure feedback).</summary>
        public void ShowNotice(string text, int milliseconds)
        {
            try
            {
                _noticeText = text;
                _noticeUntil = DateTime.UtcNow.AddMilliseconds(milliseconds);
                if (_noticeTimer == null)
                {
                    _noticeTimer = new Timer();
                    _noticeTimer.Interval = 200;
                    _noticeTimer.Tick += delegate
                    {
                        if (_noticeText != null && DateTime.UtcNow >= _noticeUntil)
                        {
                            _noticeText = null;
                            _noticeTimer.Stop();
                        }
                        Invalidate();
                    };
                }
                if (!_noticeTimer.Enabled) _noticeTimer.Start();
                Log.Line("notice noteId=" + _note.Id + " text=" + text + " ms=" + milliseconds);
                Invalidate();
            }
            catch (Exception ex)
            {
                Log.Exception("ShowNotice", ex);
            }
        }

        /// <summary>
        /// S2: swap in a freshly loaded Note (after the file changed underneath us). Also refreshes the
        /// window title and the painted card colour, which are captured at construction time.
        /// </summary>
        public void SetNote(Note note)
        {
            if (note == null) return;
            _note = note;
            try
            {
                Text = string.IsNullOrEmpty(note.Title) ? ("sticker-" + note.Id) : note.Title;
                BackColor = Palette.NoteBg(note.Color);
            }
            catch (Exception ex)
            {
                Log.Exception("SetNote", ex);
            }
            Invalidate();
        }

        private Rectangle PinButtonRect(int footerContentTop)
        {
            return new Rectangle(Typo.PAD_LR, footerContentTop, Typo.PIN_BTN, Typo.PIN_BTN);
        }

        // ------------------------------------------------------------------
        // hit testing (spec v11 6): resize band > close box > pin button > drag
        // The close/pin/drag levels come from Typo.ClassifyHit(), which is the SAME pure function
        // --selftest asserts on - so the assertion tests the production rule, not a copy of it.
        // ------------------------------------------------------------------

        private HitZone HitTest(Point p)
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            int b = Typo.RESIZE_BORDER;
            int c = Typo.RESIZE_CORNER;

            bool left = p.X < b;
            bool right = p.X >= w - b;
            bool top = p.Y < b;
            bool bottom = p.Y >= h - b;

            if (left && top) return HitZone.TopLeft;
            if (right && top) return HitZone.TopRight;
            if (left && bottom) return HitZone.BottomLeft;
            if (right && bottom) return HitZone.BottomRight;
            if (left && p.Y < c + Typo.ROW_TOP_H) return HitZone.Left;
            if (right && p.Y < c + Typo.ROW_TOP_H) return HitZone.Right;
            if (top && p.X < c + Typo.ROW_TOP_H) return HitZone.Top;
            if (bottom && p.X < c + Typo.ROW_TOP_H) return HitZone.Bottom;
            if (left) return HitZone.Left;
            if (right) return HitZone.Right;
            if (top) return HitZone.Top;
            if (bottom) return HitZone.Bottom;

            Typo.Layout L = CurrentLayout();
            switch (Typo.ClassifyHit(w, h, L.FooterContentTop, p))
            {
                case Typo.HitLevel.Close: return HitZone.Close;
                case Typo.HitLevel.Pin: return HitZone.Pin;
                default:
                    // S2: level 4 (checklist item band) sits between the pin button and the drag zone.
                    if (Typo.ChecklistItemAt(L, p) >= 0) return HitZone.Checklist;
                    return HitZone.Client;
            }
        }

        private static Cursor CursorFor(HitZone z)
        {
            switch (z)
            {
                case HitZone.Left:
                case HitZone.Right: return Cursors.SizeWE;
                case HitZone.Top:
                case HitZone.Bottom: return Cursors.SizeNS;
                case HitZone.TopLeft:
                case HitZone.BottomRight: return Cursors.SizeNWSE;
                case HitZone.TopRight:
                case HitZone.BottomLeft: return Cursors.SizeNESW;
                default: return Cursors.Default;
            }
        }

        /// <summary>Always HTCLIENT so every mouse event lands in our own handlers (spec 7.1).</summary>
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = (IntPtr)1; // HTCLIENT
                return;
            }
            if (m.Msg == 0x0201 /*WM_LBUTTONDOWN*/ || m.Msg == 0x0204 /*WM_RBUTTONDOWN*/)
            {
                // bring the card to the front so Alt+F4 targets the card the user just clicked
                NativeMethods.SetForegroundWindow(Handle);
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // v18: no right-click menu any more (the old exit item was removed on the user's request),
            // so a right button press is simply not a drag/resize gesture - it falls through to the
            // "not the left button" guard below.
            if (e.Button != MouseButtons.Left) return;

            HitZone z = HitTest(e.Location);
            _mgr.LogHitTest("mousedown noteId=" + _note.Id + " at " + e.X + "," + e.Y + " zone=" + z);
            if (z == HitZone.Close)
            {
                // spec v11 3.5: arm the pending close; DO NOT start a drag or a resize.
                _closeArmed = true;
                _dragging = false;
                _resizing = false;
                Capture = true;
                Cursor = Cursors.Default;
                return;
            }
            if (z == HitZone.Pin)
            {
                _mgr.ToggleTopMost(_note.Id);
                return;
            }
            if (z == HitZone.Checklist)
            {
                // spec v12 3.2: arm a pending check; drag/resize stay disarmed while it is pending.
                Typo.Layout L = CurrentLayout();
                int idx = Typo.ChecklistItemAt(L, e.Location);
                if (idx >= 0 && idx < _note.Checklist.Count)
                {
                    _checkArmed = true;
                    _checkIndex = idx;
                    _checkRect = L.ChecklistHitRects[idx];
                    _pressScreen = Cursor.Position;
                    _dragging = false;
                    _resizing = false;
                    _closeArmed = false;
                    Capture = true;
                    Cursor = Cursors.Default;
                    _mgr.LogHitTest("check-armed noteId=" + _note.Id + " idx=" + idx
                        + " band=" + _checkRect.X + "," + _checkRect.Y + " " + _checkRect.Width + "x" + _checkRect.Height);
                    return;
                }
                // band vanished between hit-test and arm (card resized): fall through to drag
            }
            if (z != HitZone.Client)
            {
                _resizing = true;
                _resizeZone = z;
                _resizeStartBounds = Bounds;
                _resizeStartCursor = Cursor.Position;
            }
            else
            {
                _dragging = true;
                _dragGrab = new Point(Cursor.Position.X - Location.X, Cursor.Position.Y - Location.Y);
            }
            Capture = true;
            Cursor = (z == HitZone.Client) ? Cursors.SizeAll : CursorFor(z);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_checkArmed)
            {
                // spec v12 3.2: a wide band must not steal dragging. Past the OS drag slop the pending
                // check becomes a normal drag (and can therefore never fire a checkbox on release).
                Size slopSize = SystemInformation.DragSize;
                Rectangle slop = new Rectangle(
                    _pressScreen.X - slopSize.Width / 2, _pressScreen.Y - slopSize.Height / 2,
                    Math.Max(2, slopSize.Width), Math.Max(2, slopSize.Height));
                if (slop.Contains(Cursor.Position)) return;
                _checkArmed = false;
                _dragging = true;
                _dragGrab = new Point(Cursor.Position.X - Location.X, Cursor.Position.Y - Location.Y);
                Cursor = Cursors.SizeAll;
                _mgr.LogHitTest("check-armed -> drag noteId=" + _note.Id + " (pointer left the drag slop; the check is CANCELLED)");
            }
            if (_dragging)
            {
                Point target = new Point(Cursor.Position.X - _dragGrab.X, Cursor.Position.Y - _dragGrab.Y);
                if (Location != target) Location = target;   // no repaint during drag (spec 7.1)
                return;
            }
            if (_resizing)
            {
                ApplyResize();
                return;
            }
            HitZone z = HitTest(e.Location);
            Cursor want = (z == HitZone.Client || z == HitZone.Pin || z == HitZone.Close) ? Cursors.Default : CursorFor(z);
            if (Cursor != want) Cursor = want;
        }

        private void ApplyResize()
        {
            Point cur = Cursor.Position;
            int dx = cur.X - _resizeStartCursor.X;
            int dy = cur.Y - _resizeStartCursor.Y;
            Rectangle b = _resizeStartBounds;

            int L = b.Left, T = b.Top, R = b.Right, B = b.Bottom;
            bool west = _resizeZone == HitZone.Left || _resizeZone == HitZone.TopLeft || _resizeZone == HitZone.BottomLeft;
            bool east = _resizeZone == HitZone.Right || _resizeZone == HitZone.TopRight || _resizeZone == HitZone.BottomRight;
            bool north = _resizeZone == HitZone.Top || _resizeZone == HitZone.TopLeft || _resizeZone == HitZone.TopRight;
            bool south = _resizeZone == HitZone.Bottom || _resizeZone == HitZone.BottomLeft || _resizeZone == HitZone.BottomRight;

            Rectangle wa = Screen.FromRectangle(_resizeStartBounds).WorkingArea;
            int maxW = Math.Max(Typo.MIN_W, wa.Width);
            int maxH = Math.Max(Typo.MIN_H, wa.Height);

            if (west) L = L + dx;
            if (east) R = R + dx;
            if (north) T = T + dy;
            if (south) B = B + dy;

            if (R - L < Typo.MIN_W) { if (west) L = R - Typo.MIN_W; else R = L + Typo.MIN_W; }
            if (B - T < Typo.MIN_H) { if (north) T = B - Typo.MIN_H; else B = T + Typo.MIN_H; }
            if (R - L > maxW) { if (west) L = R - maxW; else R = L + maxW; }
            if (B - T > maxH) { if (north) T = B - maxH; else B = T + maxH; }

            Rectangle next = Rectangle.FromLTRB(L, T, R, B);
            if (next != Bounds)
            {
                // bypass WinForms' silent MinimumSize/MaximumSize clipping (spec 7.2.2)
                SetBoundsCore(next.X, next.Y, next.Width, next.Height, BoundsSpecified.All);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            if (_closeArmed)
            {
                // spec v11 3.5: fire only when the release still lands inside the SAME hit rectangle.
                _closeArmed = false;
                Capture = false;
                Cursor = Cursors.Default;
                bool inside = Typo.CloseHitRect(ClientSize.Width).Contains(e.Location);
                _mgr.LogHitTest("close-release noteId=" + _note.Id + " at " + e.X + "," + e.Y
                    + " insideCloseHit=" + inside + (inside ? " -> closing this card only" : " -> cancelled"));
                if (inside) Close();     // goes through OnFormClosing -> StickerManager.OnCardClosing
                return;
            }

            if (_checkArmed)
            {
                // spec v12 3.2: same press/release rule as the close box.
                _checkArmed = false;
                Capture = false;
                Cursor = Cursors.Default;
                bool inside = _checkRect.Contains(e.Location);
                _mgr.LogHitTest("check-release noteId=" + _note.Id + " idx=" + _checkIndex + " at " + e.X + "," + e.Y
                    + " insideItemBand=" + inside + (inside ? " -> writing the checklist back" : " -> cancelled"));
                if (inside) _mgr.ToggleChecklistItem(_note.Id, _checkIndex);
                return;
            }

            bool wasDragging = _dragging;
            bool wasResizing = _resizing;
            _dragging = false;
            _resizing = false;
            Capture = false;
            Cursor = Cursors.Default;

            if (!wasDragging && !wasResizing) return;

            Rectangle fixedUp = Bounds;
            bool moved = ScreenClamp.ClampToPrimary(ref fixedUp);
            if (moved)
            {
                SetBoundsCore(fixedUp.X, fixedUp.Y, fixedUp.Width, fixedUp.Height, BoundsSpecified.All);
                Log.Line("post-move clamp noteId=" + _note.Id + " -> " + BoundsToString(fixedUp));
            }
            _mgr.LogHitTest((wasDragging ? "drag" : "resize") + " finished noteId=" + _note.Id
                + " bounds=" + BoundsToString(Bounds) + " clampChanged=" + moved + " -> write state NOW");
            _mgr.Persist("drag-or-resize-end");   // spec 8.3: immediate write
        }

        /// <summary>Alt+F4 (or any close) removes this single card and persists (spec 7.4).</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            Log.Line("card closing noteId=" + _note.Id + " reason=" + e.CloseReason + " appExiting=" + _mgr.IsExiting);
            _mgr.OnCardClosing(this, e.CloseReason);
        }

        public void SetPinVisual(bool on)
        {
            _pinOn = on;
            TopMost = on;      // manager is the single decision point (spec 7.3.2)
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_roundRegion != null) { _roundRegion.Dispose(); _roundRegion = null; }
                if (_noticeTimer != null) { _noticeTimer.Stop(); _noticeTimer.Dispose(); _noticeTimer = null; }
            }
            base.Dispose(disposing);
        }
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr GetThreadDpiAwarenessContext();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr ctx);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        public static string GeometryDiag(Form f)
        {
            string win = "";
            uint dpi = 0;
            int awareness = -1;
            try
            {
                if (f.IsHandleCreated)
                {
                    RECT r;
                    if (GetWindowRect(f.Handle, out r)) win = "(win " + r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + ")";
                    dpi = GetDpiForWindow(f.Handle);
                }
                awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
            }
            catch { }
            return "Size=" + f.Size.Width + "x" + f.Size.Height
                + " ClientSize=" + f.ClientSize.Width + "x" + f.ClientSize.Height
                + " Location=" + f.Location.X + "," + f.Location.Y
                + " dpi=" + dpi + " awareness=" + awareness
                + " SizeEqualsClientSize=" + (f.Size == f.ClientSize) + " " + win;
        }
    }
}

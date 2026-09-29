// v13 sticker: the system tray icon + the "already placed notes" list
// (the user asked for that list back in v11: "list every note that is placed out").
//
// IMPORTANT: this file owns the tray menu. The CARD context menu (StickerForm.cs) is untouched -
// assertion context-menu-item-count-unchanged counts the card menu's own Add calls in that file
// and must still find exactly ONE (the exit entry of spec 7.4).
//
// Pure ASCII source.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TieTieSticker
{
    /// <summary>One row of the tray menu, built without touching WinForms so it stays assertable.</summary>
    internal sealed class TrayMenuRow
    {
        public string Text = "";
        public string NoteId;       // null for the header row and the exit row
        public bool Enabled = true;
        public bool IsExit;
        public bool IsHeader;
    }

    internal static class TrayMenu
    {
        /// <summary>Spec v13 3.4: a non-clickable header - "placed notes (N)".</summary>
        public static string HeaderText(int count)
        {
            return UiStrings.TrayHeader + UiStrings.ParenOpen + count + UiStrings.ParenClose;
        }

        /// <summary>
        /// Spec v13 3.4 fixed structure: header row + one row per placed note + the exit row.
        /// A note without a title shows the "untitled" caption instead of an empty menu row.
        /// </summary>
        public static List<TrayMenuRow> BuildRows(IList<string> noteIds, IList<string> titles)
        {
            List<TrayMenuRow> rows = new List<TrayMenuRow>();

            TrayMenuRow head = new TrayMenuRow();
            head.Text = HeaderText(noteIds == null ? 0 : noteIds.Count);
            head.Enabled = false;
            head.IsHeader = true;
            rows.Add(head);

            if (noteIds != null)
            {
                for (int i = 0; i < noteIds.Count; i++)
                {
                    string title = (titles != null && i < titles.Count) ? titles[i] : null;
                    TrayMenuRow r = new TrayMenuRow();
                    r.Text = string.IsNullOrEmpty(title) ? UiStrings.NoTitle : title;
                    r.NoteId = noteIds[i];
                    rows.Add(r);
                }
            }

            TrayMenuRow exit = new TrayMenuRow();
            exit.Text = UiStrings.ExitSticker;
            exit.IsExit = true;
            rows.Add(exit);
            return rows;
        }

        /// <summary>
        /// Build the real menu from exactly those rows - the self-test drives THIS function (not only
        /// the row model) so "the list is actually wired into the menu" is asserted, not assumed.
        /// </summary>
        public static ContextMenuStrip Create(IList<string> noteIds, IList<string> titles,
            Action<string> onRow, Action onExit)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            List<TrayMenuRow> rows = BuildRows(noteIds, titles);
            for (int i = 0; i < rows.Count; i++)
            {
                TrayMenuRow row = rows[i];
                ToolStripMenuItem item = new ToolStripMenuItem(row.Text);
                item.Enabled = row.Enabled;
                if (row.IsExit)
                {
                    if (onExit != null) item.Click += delegate(object s, EventArgs e) { onExit(); };
                }
                else if (row.NoteId != null)
                {
                    string captured = row.NoteId;                 // one variable per iteration
                    if (onRow != null) item.Click += delegate(object s, EventArgs e) { onRow(captured); };
                }
                menu.Items.Add(item);
            }
            return menu;
        }
    }

    /// <summary>
    /// The live tray icon. The icon is drawn at runtime (Geometry.DrawPin) so no external .ico file
    /// is introduced, and the HICON is released again (DestroyIcon) so the handle cannot leak.
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        private readonly StickerManager _mgr;
        private readonly Action _onExit;
        private NotifyIcon _icon;
        private Icon _ownedIcon;
        private ContextMenuStrip _menu;
        private bool _disposed;

        public TrayIcon(StickerManager mgr, Action onExit)
        {
            _mgr = mgr;
            _onExit = onExit;

            _ownedIcon = BuildPinIcon();
            _icon = new NotifyIcon();
            _icon.Icon = _ownedIcon;
            _icon.Text = UiStrings.TrayTooltip;
            _icon.Visible = true;
            Refresh();
            Log.Line("tray: icon created and visible");
        }

        /// <summary>Rebuild the menu from the current placed set (called after every state write).</summary>
        public void Refresh()
        {
            if (_disposed) return;
            try
            {
                List<string> ids = _mgr.PlacedNoteIdsOrdered();
                List<string> titles = new List<string>();
                for (int i = 0; i < ids.Count; i++) titles.Add(_mgr.TitleOf(ids[i]));

                ContextMenuStrip fresh = TrayMenu.Create(ids, titles,
                    delegate(string noteId) { _mgr.RaiseCard(noteId); },
                    delegate { if (_onExit != null) _onExit(); });

                ContextMenuStrip old = _menu;
                _menu = fresh;
                _icon.ContextMenuStrip = _menu;
                if (old != null)
                {
                    try { old.Dispose(); } catch { }
                }
                Log.Line("tray: menu refreshed, " + ids.Count + " placed note row(s)");
            }
            catch (Exception ex)
            {
                Log.Exception("tray refresh", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_icon != null) { _icon.Visible = false; _icon.Dispose(); _icon = null; }
            }
            catch { }
            try { if (_menu != null) { _menu.Dispose(); _menu = null; } } catch { }
            try { if (_ownedIcon != null) { _ownedIcon.Dispose(); _ownedIcon = null; } } catch { }
            Log.Line("tray: disposed");
        }

        /// <summary>16x16 pin drawn from the shared glyph; the HICON is destroyed after cloning.</summary>
        private static Icon BuildPinIcon()
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                using (Bitmap bmp = new Bitmap(16, 16))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        Geometry.DrawPin(g, new Rectangle(0, 0, 16, 16), Palette.Accent);
                    }
                    h = bmp.GetHicon();
                    using (Icon raw = Icon.FromHandle(h))
                    {
                        return (Icon)raw.Clone();      // Clone owns its own copy => the handle may go
                    }
                }
            }
            finally
            {
                if (h != IntPtr.Zero)
                {
                    try { DestroyIcon(h); } catch { }
                }
            }
        }
    }
}

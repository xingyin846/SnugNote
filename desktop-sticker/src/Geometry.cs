// S1 sticker: vector geometry helpers (rounded rectangles, the pin glyph, the clock glyph).
// Emoji is deliberately NOT used - see spec 5.1 / U3.
// Pure ASCII source.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace TieTieSticker
{
    internal static class Geometry
    {
        /// <summary>Rounded rectangle path - mirrors CSS border-radius.</summary>
        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0 || r.Width <= d || r.Height <= d)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>
        /// Hand-drawn pushpin. The glyph is designed on a 25x25 grid (spec 5.1) and scaled into
        /// the given box, so the 12x12 top flag and the 30x30 footer button share one shape.
        /// </summary>
        /// <summary>
        /// Spec v11 3.3: the close (X) glyph - two round-capped diagonals, pen 2.0f, drawn inside a
        /// 10x10 glyph box centred in the 18x18 visual box (glyph box origin = box + 4, endpoints
        /// inset 1px). No emoji, no badge background, no hover state (S1 has neither).
        /// </summary>
        public static void DrawClose(Graphics g, Rectangle box, Color color)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int gx = box.X + (box.Width - 10) / 2;     // 10x10 glyph box
            int gy = box.Y + (box.Height - 10) / 2;
            using (Pen p = new Pen(color, 2.0f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawLine(p, gx + 1, gy + 1, gx + 9, gy + 9);
                g.DrawLine(p, gx + 9, gy + 1, gx + 1, gy + 9);
            }
            g.SmoothingMode = old;
        }

        public static void DrawPin(Graphics g, Rectangle box, Color color)
        {
            float s = Math.Min(box.Width, box.Height) / 25f;
            GraphicsState st = g.Save();
            g.TranslateTransform(box.X, box.Y);
            g.ScaleTransform(s, s);
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (SolidBrush b = new SolidBrush(color))
            {
                // cap (ellipse) - scaled from (7,5,10,6)
                g.FillEllipse(b, 5f, 3.5f, 15f, 9f);
                // cap neck
                g.FillRectangle(b, 10.5f, 12f, 4f, 4f);
                // needle triangle
                g.FillPolygon(b, new PointF[]
                {
                    new PointF(10f, 16f),
                    new PointF(15f, 16f),
                    new PointF(12.5f, 25f)
                });
            }
            using (Pen p = new Pen(color, 2.4f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawLine(p, 12.5f, 23f, 12.5f, 27f);
            }

            g.SmoothingMode = old;
            g.Restore(st);
        }

        /// <summary>Hand-drawn 8x8 clock replacing the emoji prefix of the due badge (spec 3.2).</summary>
        public static void DrawClock(Graphics g, Point center, Color color)
        {
            float cx = center.X;
            float cy = center.Y;
            using (Pen p = new Pen(color, 1.2f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawEllipse(p, cx - 3.5f, cy - 3.5f, 7f, 7f);
                g.DrawLine(p, cx, cy, cx, cy - 2.2f);
                g.DrawLine(p, cx, cy, cx + 1.7f, cy);
            }
        }
    }
}

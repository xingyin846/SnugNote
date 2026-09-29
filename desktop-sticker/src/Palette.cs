// S1 sticker: design tokens copied verbatim from demo/styles.css@588FE4A1 (light theme only).
// Pure ASCII source.
using System;
using System.Collections.Generic;
using System.Drawing;

namespace TieTieSticker
{
    internal static class Palette
    {
        // Global light tokens  (:root, :root[data-theme="light"])
        public static readonly Color Text = ColorTranslator.FromHtml("#24212B");
        public static readonly Color TextSoft = ColorTranslator.FromHtml("#6D6875");
        public static readonly Color Surface = ColorTranslator.FromHtml("#FFFFFF");
        public static readonly Color Border = ColorTranslator.FromHtml("#E8E3DA");
        public static readonly Color Bg = ColorTranslator.FromHtml("#F6F4EF");
        public static readonly Color ChipBg = ColorTranslator.FromHtml("#EFECE6");
        public static readonly Color Accent = ColorTranslator.FromHtml("#FF7A9E");

        // .note--* : --note-bg / --note-edge
        private static readonly Dictionary<string, Color[]> Map = BuildMap();

        private static Dictionary<string, Color[]> BuildMap()
        {
            Dictionary<string, Color[]> m = new Dictionary<string, Color[]>(StringComparer.Ordinal);
            m["yellow"] = new Color[] { ColorTranslator.FromHtml("#FFF6C9"), ColorTranslator.FromHtml("#F3E18A") };
            m["pink"] = new Color[] { ColorTranslator.FromHtml("#FFE3EE"), ColorTranslator.FromHtml("#F3B5CD") };
            m["blue"] = new Color[] { ColorTranslator.FromHtml("#DCEFFF"), ColorTranslator.FromHtml("#A6D4F5") };
            m["green"] = new Color[] { ColorTranslator.FromHtml("#E3F6D0"), ColorTranslator.FromHtml("#B4DE95") };
            m["purple"] = new Color[] { ColorTranslator.FromHtml("#ECE3FF"), ColorTranslator.FromHtml("#C7B8EF") };
            m["orange"] = new Color[] { ColorTranslator.FromHtml("#FFE8D3"), ColorTranslator.FromHtml("#F3C39A") };
            return m;
        }

        public static bool IsKnownColor(string key)
        {
            return key != null && Map.ContainsKey(key);
        }

        public static Color NoteBg(string color)
        {
            Color[] p;
            if (color != null && Map.TryGetValue(color, out p)) return p[0];
            return Surface;
        }

        public static Color NoteEdge(string color)
        {
            Color[] p;
            if (color != null && Map.TryGetValue(color, out p)) return p[1];
            return Border;
        }

        /// <summary>Alpha-composited variant: color-mix(in srgb, c pct%, transparent) -&gt; Color.FromArgb(round(pct*255), c).</summary>
        public static Color Alpha(Color c, double fraction)
        {
            int a = (int)Math.Round(fraction * 255.0);
            if (a < 0) a = 0;
            if (a > 255) a = 255;
            return Color.FromArgb(a, c.R, c.G, c.B);
        }
    }
}

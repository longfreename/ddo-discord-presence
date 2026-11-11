using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace DdoDiscordPresence.UI
{
    /// <summary>
    /// The plugin's icon and the Discord art assets, drawn in code in the family look of longfreename's other plugins
    /// (Hireling Commander's toolbar icon): a jewel-tone field lit from the upper left, a one-pixel inner bevel, and
    /// outlined metal emblems with a drop shadow, all laid out on a 36-unit grid.
    ///
    /// The emblem here is a lit steel speech bubble holding a gilt twenty-sided die with a crimson face, with Discord's
    /// green "online" presence dot cut into the lower right corner, on a deep violet field.
    ///
    /// The toolbar image is 36x36 for the same reason as Hireling Commander's: Dungeon Helper frames any image wider than
    /// 32 px and draws its centre 32x32 at (2,2), so the art lives in 2..34 and the margin only bleeds the field.
    /// </summary>
    internal static class PresenceIcon
    {
        private const float Grid = 36f;

        private static readonly Color GoldL = Color.FromArgb(255, 242, 196), GoldM = Color.FromArgb(226, 172, 72), GoldD = Color.FromArgb(118, 74, 22);
        private static readonly Color SteelL = Color.FromArgb(240, 242, 252), SteelM = Color.FromArgb(150, 160, 192), SteelD = Color.FromArgb(54, 58, 92);
        private static readonly Color CrimL = Color.FromArgb(255, 120, 110), CrimM = Color.FromArgb(200, 30, 44), CrimD = Color.FromArgb(92, 8, 18);
        private static readonly Color GreenL = Color.FromArgb(170, 250, 190), GreenM = Color.FromArgb(46, 184, 96), GreenD = Color.FromArgb(10, 84, 40);
        private static readonly Color BoneL = Color.FromArgb(255, 250, 236), BoneM = Color.FromArgb(214, 202, 176), BoneD = Color.FromArgb(96, 84, 64);

        private static readonly Color FieldGlow = Color.FromArgb(118, 96, 236), FieldDeep = Color.FromArgb(14, 8, 40);

        /// <summary>The 36x36 toolbar image.</summary>
        internal static Bitmap DrawToolbarIcon() => Render(36, g => PaintMain(g, bevel: true), supersample: 8);

        /// <summary>The main emblem at any size (the 1024x1024 Discord large image, previews).</summary>
        /// <remarks>Full bleed: the toolbar bevel is left out, Discord rounds the corners itself.</remarks>
        internal static Bitmap DrawMain(int size) => Render(size, g => PaintMain(g, bevel: false), supersample: size < 128 ? 8 : 1);

        /// <summary>A difficulty badge ("casual", "normal", "hard", "elite", "reaper") at any size; Discord shows it as a circle.</summary>
        internal static Bitmap DrawBadge(string key, int size) => Render(size, g => PaintBadge(g, key), supersample: size < 128 ? 8 : 1);

        /// <summary>Writes the Discord art assets: ddo-1024.png, the 512x512 badges and the 36x36 toolbar icon.</summary>
        internal static IReadOnlyList<string> ExportAll(string folder)
        {
            Directory.CreateDirectory(folder);
            var written = new List<string>();
            void Save(Bitmap b, string name)
            {
                using (b)
                {
                    var path = Path.Combine(folder, name);
                    b.Save(path, ImageFormat.Png);
                    written.Add(path);
                }
            }
            Save(DrawMain(1024), "ddo-1024.png");
            foreach (var key in Presence.PresenceFormatter.BadgeKeys) Save(DrawBadge(key, 512), key + "-512.png");
            Save(DrawToolbarIcon(), "toolbar-36.png");
            return written;
        }

        // ------------------------------------------------------------------ main emblem

        private static void PaintMain(Graphics g, bool bevel)
        {
            Field(g, FieldGlow, FieldDeep, bevel);

            // The speech bubble: a rounded panel with a tail toward the lower left.
            using (var bubble = RoundedRect(4.6f, 5.2f, 26.6f, 19.6f, 5.2f))
            {
                using var tail = new GraphicsPath();
                tail.AddLines(new[] { new PointF(15.4f, 22.8f), new PointF(6.4f, 31.2f), new PointF(8.6f, 22.8f) });
                tail.CloseFigure();
                using var outline = Outline(bubble, tail);
                Metal(g, outline, SteelL, SteelM, SteelD);
            }
            // A shadowed inner well so the die reads against the steel.
            using (var well = RoundedRect(7.2f, 7.6f, 21.4f, 14.8f, 3.6f))
            using (var b = new LinearGradientBrush(new RectangleF(7f, 7.4f, 22f, 15.4f), Color.FromArgb(70, 30, 26, 64), Color.FromArgb(150, 8, 6, 22), 70f))
                g.FillPath(b, well);

            // The die: a gilt hexagon with a crimson upper face and dark facet lines.
            var c = new PointF(17.9f, 15f);
            const float r = 6.6f;
            var hex = new PointF[6];
            for (var i = 0; i < 6; i++)
            {
                var a = (Math.PI / 180.0) * (-90 + 60 * i);
                hex[i] = new PointF(c.X + (float)(r * Math.Cos(a)), c.Y + (float)(r * Math.Sin(a)));
            }
            using (var die = new GraphicsPath())
            {
                die.AddPolygon(hex);
                Metal(g, die, GoldL, GoldM, GoldD, 0.7f);
            }
            var face = new[]
            {
                new PointF(c.X, c.Y - r * 0.52f),
                new PointF(c.X + r * 0.5f, c.Y + r * 0.34f),
                new PointF(c.X - r * 0.5f, c.Y + r * 0.34f),
            };
            using (var facets = new Pen(Color.FromArgb(200, 92, 52, 12), 0.45f) { LineJoin = LineJoin.Round })
            {
                g.DrawLine(facets, hex[0], face[0]);
                g.DrawLine(facets, hex[1], face[0]); g.DrawLine(facets, hex[1], face[1]);
                g.DrawLine(facets, hex[2], face[1]);
                g.DrawLine(facets, hex[3], face[1]); g.DrawLine(facets, hex[3], face[2]);
                g.DrawLine(facets, hex[4], face[2]);
                g.DrawLine(facets, hex[5], face[2]); g.DrawLine(facets, hex[5], face[0]);
            }
            using (var tri = new GraphicsPath())
            {
                tri.AddPolygon(face);
                using var b = new LinearGradientBrush(new RectangleF(c.X - 4f, c.Y - 4f, 8f, 7f), CrimL, CrimD, 70f)
                {
                    InterpolationColors = new ColorBlend { Colors = new[] { CrimL, CrimM, CrimD }, Positions = new[] { 0f, 0.5f, 1f } }
                };
                g.FillPath(b, tri);
                using var edge = new Pen(Color.FromArgb(220, 118, 74, 22), 0.45f) { LineJoin = LineJoin.Round };
                g.DrawPath(edge, tri);
            }

            // Discord's presence dot, cut into the field like the status dot on an avatar.
            using (var cut = new SolidBrush(FieldDeep)) g.FillEllipse(cut, 22.2f, 22.2f, 11.2f, 11.2f);
            using (var dot = new GraphicsPath())
            {
                dot.AddEllipse(23.7f, 23.7f, 8.2f, 8.2f);
                Metal(g, dot, GreenL, GreenM, GreenD, 0.6f);
            }
        }

        // ------------------------------------------------------------------ badges

        private static void PaintBadge(Graphics g, string key)
        {
            switch (key)
            {
                case "casual":
                    Field(g, Color.FromArgb(70, 178, 118), Color.FromArgb(4, 30, 16), bevel: false);
                    Chevrons(g, 1, SteelL, SteelM, SteelD);
                    break;
                case "normal":
                    Field(g, Color.FromArgb(56, 104, 200), Color.FromArgb(7, 13, 34), bevel: false);
                    Chevrons(g, 2, SteelL, SteelM, SteelD);
                    break;
                case "hard":
                    Field(g, Color.FromArgb(222, 138, 46), Color.FromArgb(42, 16, 4), bevel: false);
                    Chevrons(g, 3, SteelL, SteelM, SteelD);
                    break;
                case "elite":
                    Field(g, Color.FromArgb(150, 70, 220), Color.FromArgb(22, 6, 40), bevel: false);
                    using (var star = Star(18f, 18.4f, 10.6f, 4.4f, 5)) Metal(g, star, GoldL, GoldM, GoldD);
                    break;
                case "reaper":
                    Field(g, Color.FromArgb(170, 24, 30), Color.FromArgb(12, 2, 4), bevel: false);
                    Skull(g);
                    break;
                default:
                    Field(g, FieldGlow, FieldDeep, bevel: false);
                    break;
            }
        }

        private static void Chevrons(Graphics g, int count, Color l, Color m, Color d)
        {
            const float w = 18f, h = 6.4f, thick = 3.6f, gap = 1.6f;
            var total = count * thick + (count - 1) * gap + h;
            var top = 18f - total / 2f;
            for (var i = 0; i < count; i++)
            {
                var y = top + i * (thick + gap);
                using var p = new GraphicsPath();
                p.AddLines(new[]
                {
                    new PointF(18f - w / 2, y + h), new PointF(18f, y), new PointF(18f + w / 2, y + h),
                    new PointF(18f + w / 2, y + h + thick), new PointF(18f, y + thick), new PointF(18f - w / 2, y + h + thick),
                });
                p.CloseFigure();
                Metal(g, p, l, m, d, 0.7f);
            }
        }

        private static GraphicsPath Star(float cx, float cy, float outer, float inner, int points)
        {
            var pts = new PointF[points * 2];
            for (var i = 0; i < pts.Length; i++)
            {
                var rad = i % 2 == 0 ? outer : inner;
                var a = Math.PI / points * i - Math.PI / 2;
                pts[i] = new PointF(cx + (float)(rad * Math.Cos(a)), cy + (float)(rad * Math.Sin(a)));
            }
            var p = new GraphicsPath();
            p.AddPolygon(pts);
            return p;
        }

        private static void Skull(Graphics g)
        {
            using (var head = new GraphicsPath())
            {
                head.AddEllipse(9.4f, 6.6f, 17.2f, 16f);
                head.AddRectangle(new RectangleF(12.6f, 18f, 10.8f, 8.6f));
                using var outline = Outline(head, null);
                Metal(g, outline, BoneL, BoneM, BoneD);
            }
            using (var hole = new SolidBrush(Color.FromArgb(240, 26, 4, 6)))
            {
                g.FillEllipse(hole, 12.3f, 13.2f, 4.8f, 4.6f);
                g.FillEllipse(hole, 18.9f, 13.2f, 4.8f, 4.6f);
                using var nose = new GraphicsPath();
                nose.AddPolygon(new[] { new PointF(18f, 18.4f), new PointF(19.3f, 21f), new PointF(16.7f, 21f) });
                g.FillPath(hole, nose);
            }
            using (var teeth = new Pen(Color.FromArgb(230, 60, 40, 30), 0.6f))
            {
                g.DrawLine(teeth, 12.8f, 23.4f, 23.2f, 23.4f);
                for (var x = 14.6f; x < 22.5f; x += 1.8f) g.DrawLine(teeth, x, 22.2f, x, 26.4f);
            }
        }

        // ------------------------------------------------------------------ construction (Hireling Commander's)

        private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            var d = r * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>
        /// Overlapping figures as one shape. GDI+ has no path union, so the figures share one path with FillMode.Winding:
        /// <see cref="Metal"/> strokes every figure's outline first and then fills the union over it, which hides the
        /// outline segments that fall inside the other figure.
        /// </summary>
        private static GraphicsPath Outline(GraphicsPath a, GraphicsPath? b)
        {
            var p = new GraphicsPath(FillMode.Winding);
            p.AddPath(a, false);
            if (b != null) p.AddPath(b, false);
            return p;
        }

        private static Bitmap Render(int size, Action<Graphics> paint, int supersample)
        {
            var hiSize = size * Math.Max(1, supersample);
            var hi = new Bitmap(hiSize, hiSize, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(hi))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.ScaleTransform(hiSize / Grid, hiSize / Grid);
                paint(g);
            }
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (hi)
            using (var g = Graphics.FromImage(bmp))
            using (var edges = new ImageAttributes())
            {
                edges.SetWrapMode(WrapMode.TileFlipXY);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(hi, new Rectangle(0, 0, size, size), 0, 0, hi.Width, hi.Height, GraphicsUnit.Pixel, edges);
            }
            return bmp;
        }

        private static void Field(Graphics g, Color glow, Color deep, bool bevel)
        {
            // The glow is computed per pixel at toolbar resolution (288 px) and stretched: GDI+'s PathGradientBrush with a
            // bell shape leaves a dark speck at its centre point, which showed on the badges.
            using (var field = new Bitmap(288, 288, PixelFormat.Format32bppArgb))
            {
                const float cx = 13f, cy = 11f, radius = 27f;
                for (var py = 0; py < 288; py++)
                    for (var px = 0; px < 288; px++)
                    {
                        var x = (px + 0.5f) * Grid / 288f; var y = (py + 0.5f) * Grid / 288f;
                        var d = (float)Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / radius;
                        var t = Math.Clamp(1f - d, 0f, 1f);
                        t = t * t * (3f - 2f * t);
                        field.SetPixel(px, py, Mix(deep, glow, t));
                    }
                using var edges = new ImageAttributes();
                edges.SetWrapMode(WrapMode.TileFlipXY);
                var old = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(field, new[] { new PointF(0, 0), new PointF(Grid, 0), new PointF(0, Grid) }, new RectangleF(0, 0, 288, 288), GraphicsUnit.Pixel, edges);
                g.InterpolationMode = old;
            }
            if (!bevel) return;
            // A one-pixel bevel just inside the host's frame, lit from the upper left.
            using (var light = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
            {
                g.DrawLine(light, 2f, 2.5f, 34f, 2.5f);
                g.DrawLine(light, 2.5f, 2f, 2.5f, 34f);
            }
            using (var shade = new Pen(Color.FromArgb(110, 0, 0, 0), 1f))
            {
                g.DrawLine(shade, 2f, 33.5f, 34f, 33.5f);
                g.DrawLine(shade, 33.5f, 2f, 33.5f, 34f);
            }
        }

        private static Color Mix(Color a, Color b, float t) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        /// <summary>Fills a shape as lit metal: drop shadow, dark outline, then a light-to-dark sheen from the upper left.</summary>
        private static void Metal(Graphics g, GraphicsPath shape, Color light, Color mid, Color dark, float outline = 1f)
        {
            using (var shadow = (GraphicsPath)shape.Clone())
            using (var shift = new Matrix())
            {
                shift.Translate(0.8f, 1.1f);
                shadow.Transform(shift);
                using var b = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
                g.FillPath(b, shadow);
            }
            if (outline > 0)
                using (var edge = new Pen(Color.FromArgb(235, 8, 6, 10), outline * 2f) { LineJoin = LineJoin.Round })
                    g.DrawPath(edge, shape);
            var r = shape.GetBounds();
            r.Inflate(0.5f, 0.5f);
            using (var sheen = new LinearGradientBrush(r, light, dark, 55f))
            {
                sheen.InterpolationColors = new ColorBlend { Colors = new[] { light, mid, dark }, Positions = new[] { 0f, 0.5f, 1f } };
                g.FillPath(sheen, shape);
            }
        }
    }
}

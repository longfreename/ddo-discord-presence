using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using DdoDiscordPresence.Presence;

namespace DdoDiscordPresence.UI
{
    /// <summary>
    /// Draws the activity the way Discord's profile card shows it: the large image with the optional small badge, the
    /// application name, details, state and the elapsed timer. It renders exactly the <see cref="PresenceActivity"/>
    /// that is (or would be) sent, never a separate copy of the text.
    /// </summary>
    internal sealed class PreviewCard : Control
    {
        private PresenceActivity? _activity;
        private string _reason = "";
        private Bitmap? _large;
        private Bitmap? _small;
        private string? _smallKey;

        public PreviewCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 116;
            BackColor = Theme.Bg;
        }

        /// <summary>The application name Discord shows after "Playing" (it is the name of the user's Discord application).</summary>
        public string AppName { get; set; } = "DDO";
        /// <summary>The clock used for the elapsed timer (injectable for screenshots).</summary>
        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public void Show(PresenceActivity? activity, string reason)
        {
            _activity = activity;
            _reason = reason ?? "";
            if (activity?.SmallImageKey != _smallKey)
            {
                _small?.Dispose(); _small = null;
                _smallKey = activity?.SmallImageKey;
                if (_smallKey != null) _small = PresenceIcon.DrawBadge(_smallKey, 26);
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            var card = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Rounded(card, 8))
            {
                using var fill = new SolidBrush(Theme.Field);
                g.FillPath(fill, path);
                using var pen = new Pen(Theme.Line);
                g.DrawPath(pen, path);
            }
            using var capsFont = new Font(Font.FontFamily, Font.Size - 1.5f, FontStyle.Bold);
            using var boldFont = new Font(Font, FontStyle.Bold);
            var x = 12;
            TextRenderer.DrawText(g, _activity != null ? "PLAYING A GAME" : "NOT SHOWING ON DISCORD", capsFont, new Point(x, 9),
                _activity != null ? Theme.Dim : Theme.Accent, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            if (_activity == null)
            {
                var r = new Rectangle(x, 32, Width - 24, Height - 40);
                TextRenderer.DrawText(g, "Nothing is shared right now: " + _reason + ".", Font, r, Theme.Dim, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                return;
            }

            const int img = 72;
            var imgRect = new Rectangle(x, 30, img, img);
            _large ??= PresenceIcon.DrawMain(img);
            using (var clip = Rounded(imgRect, 8))
            {
                var state = g.Save();
                g.SetClip(clip);
                g.DrawImage(_large, imgRect);
                g.Restore(state);
            }
            if (_small != null)
            {
                var sr = new Rectangle(imgRect.Right - 20, imgRect.Bottom - 20, 26, 26);
                using (var ring = new SolidBrush(Theme.Field)) g.FillEllipse(ring, Rectangle.Inflate(sr, 3, 3));
                using var clip = new GraphicsPath();
                clip.AddEllipse(sr);
                var state = g.Save();
                g.SetClip(clip);
                g.DrawImage(_small, sr);
                g.Restore(state);
            }

            var tx = imgRect.Right + 14;
            var tw = Width - tx - 12;
            var y = 31;
            void Line(string? text, Font f, Color c)
            {
                if (string.IsNullOrEmpty(text)) return;
                TextRenderer.DrawText(g, text, f, new Rectangle(tx, y, tw, f.Height + 2), c, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                y += f.Height + 3;
            }
            Line(AppName, boldFont, Theme.Text);
            Line(_activity.Details, Font, Theme.Text);
            Line(_activity.State, Font, Theme.Text);
            if (_activity.StartUnixSeconds is long start)
            {
                var elapsed = UtcNow() - DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime;
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                Line(FormatElapsed(elapsed) + " elapsed", Font, Theme.Dim);
            }
        }

        internal static string FormatElapsed(TimeSpan t) => t.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", t.Minutes, t.Seconds);

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            var d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _large?.Dispose(); _small?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

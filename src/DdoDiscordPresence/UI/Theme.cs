using System.Drawing;
using System.Windows.Forms;

namespace DdoDiscordPresence.UI
{
    /// <summary>
    /// The same dark register as Hireling Commander's panel: charcoal surfaces, a maroon rule, ember for anything live and
    /// brass for anything you can act on. WinForms has no theming of its own, so <see cref="Apply"/> walks the finished
    /// control tree once.
    /// </summary>
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(26, 22, 24);
        public static readonly Color Panel = Color.FromArgb(35, 29, 32);
        public static readonly Color Field = Color.FromArgb(19, 16, 18);
        public static readonly Color Text = Color.FromArgb(226, 216, 208);
        public static readonly Color Dim = Color.FromArgb(150, 137, 130);
        public static readonly Color Accent = Color.FromArgb(214, 96, 70);    // ember
        public static readonly Color Brass = Color.FromArgb(245, 190, 130);
        public static readonly Color Line = Color.FromArgb(78, 32, 38);
        public static readonly Color Good = Color.FromArgb(126, 200, 132);
        public static readonly Color Bad = Color.FromArgb(224, 96, 92);
        public static readonly Color HoverBack = Color.FromArgb(58, 24, 28);
        public static readonly Color PressBack = Color.FromArgb(20, 10, 12);

        /// <summary>A section heading: brass small-caps over a hairline.</summary>
        public static Control Section(string text, Font baseFont, int width, int topGap = 10)
        {
            var p = new Panel { AutoSize = false, Width = width, Height = 26 + topGap, Margin = new Padding(0), BackColor = Bg };
            var l = new Label
            {
                Text = text.ToUpperInvariant(), AutoSize = true, ForeColor = Brass, BackColor = Color.Transparent,
                Font = new Font(baseFont.FontFamily, baseFont.Size - 0.5f, FontStyle.Bold), Location = new Point(0, topGap)
            };
            p.Controls.Add(l);
            p.Paint += (s, e) =>
            {
                var y = p.Height - 6;
                using var pen = new Pen(Line, 1);
                e.Graphics.DrawLine(pen, 0, y, p.Width, y);
            };
            return p;
        }

        public static Button Action(string text)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Panel, ForeColor = Brass,
                Padding = new Padding(8, 3, 8, 3), Margin = new Padding(3), UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderColor = Line;
            b.FlatAppearance.MouseOverBackColor = HoverBack;
            b.FlatAppearance.MouseDownBackColor = PressBack;
            return b;
        }

        /// <summary>Recolours a finished control tree.</summary>
        public static void Apply(Control c)
        {
            switch (c)
            {
                case TextBox t: t.BackColor = Field; t.ForeColor = Text; t.BorderStyle = BorderStyle.FixedSingle; break;
                case Button b:
                    b.FlatStyle = FlatStyle.Flat; b.BackColor = Panel; b.ForeColor = Brass; b.UseVisualStyleBackColor = false;
                    b.FlatAppearance.BorderColor = Line;
                    b.FlatAppearance.MouseOverBackColor = HoverBack;
                    b.FlatAppearance.MouseDownBackColor = PressBack;
                    break;
                // The system draws the tick; a flat one is invisible on these colours. An explicit background, not
                // Transparent: simulated transparency renders the label unevenly.
                case CheckBox ck: ck.ForeColor = Text; ck.BackColor = ck.Parent?.BackColor ?? Bg; ck.FlatStyle = FlatStyle.Standard; break;
                case Label l:
                    if (l.ForeColor == SystemColors.GrayText) l.ForeColor = Dim;
                    else if (l.ForeColor == SystemColors.ControlText) l.ForeColor = Text;
                    break;
                case Form f: f.BackColor = Bg; f.ForeColor = Text; break;
                case Panel p when p.BackColor == SystemColors.Control: p.BackColor = Bg; break;
            }
            foreach (Control child in c.Controls) Apply(child);
        }
    }
}

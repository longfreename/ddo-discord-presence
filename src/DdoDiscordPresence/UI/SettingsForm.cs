using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using DdoDiscordPresence.Discord;
using DdoDiscordPresence.Presence;

namespace DdoDiscordPresence.UI
{
    /// <summary>
    /// The plugin's panel inside Dungeon Helper: status, preview, what to share, the hide list and (collapsed) what the
    /// game reports. On a new install it first asks one question and shows nothing else. Opening it never writes settings:
    /// controls are filled under a loading flag, and only the player's own changes are saved.
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private const int ContentWidth = 520;

        private readonly Plugin _plugin;
        private bool _loading;
        private readonly Timer _tick = new Timer { Interval = 1000 };
        private readonly List<Control> _settingsControls = new List<Control>();
        private PresenceConfig? _shownConfig;

        private readonly FlowLayoutPanel _body = new FlowLayoutPanel();
        private Label _status = null!;
        private Panel _statusDot = null!;
        private Panel _firstRun = null!;
        private PreviewCard _preview = null!;
        private Button _test = null!;
        private Label _testResult = null!;
        private CheckBox _enabled = null!, _paused = null!, _quest = null!, _areas = null!, _difficulty = null!, _badge = null!,
            _levelClass = null!, _charName = null!, _party = null!, _elapsed = null!;
        private TextBox _hidden = null!;
        private Label _hiddenNote = null!;
        private Button _diagToggle = null!;
        private Label _diag = null!;

        public SettingsForm(Plugin plugin)
        {
            _plugin = plugin;
            // Dungeon Helper hosts the plugin UI as a borderless, docked form.
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.None;
            ControlBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "DDO Discord Presence";
            ClientSize = new Size(560, 720);
            MinimumSize = new Size(540, 480);
            try { Icon = Icon.FromHandle(PresenceIcon.DrawMain(32).GetHicon()); } catch { }

            BuildLayout();
            LoadFromConfig();
            Theme.Apply(this);
            RefreshView();

            _plugin.StateChanged += OnPluginStateChanged;
            _tick.Tick += (s, e) => _preview.Invalidate();
            VisibleChanged += (s, e) => { if (Visible) { _tick.Start(); RefreshView(); } else _tick.Stop(); };
        }

        // ------------------------------------------------------------------ layout

        private void BuildLayout()
        {
            SuspendLayout();
            _body.Dock = DockStyle.Fill;
            _body.FlowDirection = FlowDirection.TopDown;
            _body.WrapContents = false;
            _body.AutoScroll = true;
            _body.Padding = new Padding(14, 10, 14, 14);
            _body.BackColor = Theme.Bg;
            Controls.Add(_body);

            _body.Controls.Add(Header());
            _firstRun = FirstRunCard();
            _body.Controls.Add(_firstRun);

            Add(Theme.Section("Preview", Font, ContentWidth, 6));
            _preview = new PreviewCard { Width = ContentWidth, Font = Font, Margin = new Padding(0, 0, 0, 2), Name = "Preview" };
            Add(_preview);
            var testRow = Row();
            _test = Theme.Action("Send a test status (20 s)");
            _test.Name = "Test";
            _test.Click += async (s, e) => await RunTestAsync();
            testRow.Controls.Add(_test);
            Add(testRow);
            _testResult = Note("");
            Add(_testResult);

            Add(Theme.Section("What to share", Font, ContentWidth));
            _enabled = Check("Share my DDO activity on Discord", c => c.Enabled = _enabled.Checked);
            _enabled.Font = new Font(Font, FontStyle.Bold);
            _paused = Check("Pause sharing (clears your Discord status now)", c => c.Paused = _paused.Checked);
            _quest = Check("Quest names (otherwise \"In a quest\")", c => c.ShowQuestName = _quest.Checked);
            _areas = Check("Town, public area and wilderness names", c => c.ShowAreaNames = _areas.Checked);
            _difficulty = Check("Quest difficulty", c => c.ShowDifficulty = _difficulty.Checked);
            _badge = Check("Difficulty badge on the picture", c => c.ShowDifficultyBadge = _badge.Checked);
            _levelClass = Check("Character level and class", c => c.ShowLevelClass = _levelClass.Checked);
            _charName = Check("Show my character's name (all logged-in characters when multi-boxing)", c => c.ShowCharacterName = _charName.Checked);
            _charName.Name = "ShowCharacterName";
            _party = Check("Party size", c => c.ShowPartySize = _party.Checked);
            _elapsed = Check("Time in the current quest or area", c => c.ShowElapsed = _elapsed.Checked);
            foreach (var c in new Control[] { _enabled, _paused, _quest, _areas, _difficulty, _badge, _levelClass, _charName, _party, _elapsed }) Add(c);

            Add(Theme.Section("Hide while in", Font, ContentWidth));
            Add(Wrap("One entry per line. While the quest or area name contains any entry, your Discord status is cleared.", Theme.Dim));
            _hidden = new TextBox { Multiline = true, Width = ContentWidth, Height = 64, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Margin = new Padding(0, 4, 0, 2), Name = "HiddenPlaces" };
            Add(_hidden);
            var hideRow = Row();
            var saveHidden = Theme.Action("Save list");
            saveHidden.Click += (s, e) => SaveHidden();
            hideRow.Controls.Add(saveHidden);
            _hiddenNote = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 8, 0, 0), UseMnemonic = false };
            hideRow.Controls.Add(_hiddenNote);
            Add(hideRow);

            Add(Theme.Section("What the game reports", Font, ContentWidth));
            _diagToggle = Theme.Action("Show details");
            _diagToggle.Click += (s, e) => { _diag.Visible = !_diag.Visible; _diagToggle.Text = _diag.Visible ? "Hide details" : "Show details"; RefreshView(); };
            Add(_diagToggle);
            _diag = Wrap("", Theme.Dim);
            _diag.Name = "Diagnostics";
            _diag.Visible = false;
            Add(_diag);
            ResumeLayout(true);
        }

        private void Add(Control c) { _settingsControls.Add(c); _body.Controls.Add(c); }

        private Control Header()
        {
            var p = new Panel { Width = ContentWidth, Height = 64, Margin = new Padding(0, 0, 0, 4), BackColor = Theme.Bg };
            p.Controls.Add(new PictureBox { Image = PresenceIcon.DrawMain(48), Size = new Size(48, 48), Location = new Point(0, 6) });
            var title = new Label
            {
                Text = "DDO Discord Presence", AutoSize = true, Location = new Point(58, 6),
                Font = new Font(Font.FontFamily, Font.Size + 2f, FontStyle.Bold), ForeColor = Theme.Brass
            };
            p.Controls.Add(title);
            p.Controls.Add(new Label
            {
                Text = "v" + _plugin.Version.ToString(3) + "  by " + Plugin.AuthorName, AutoSize = true, ForeColor = Theme.Dim,
                Location = new Point(250, 11), UseMnemonic = false, Name = "Byline"
            });
            _statusDot = new Panel { Size = new Size(10, 10), Location = new Point(60, 40), BackColor = Theme.Dim };
            _statusDot.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.Clear(Theme.Bg);
                using var b = new SolidBrush(_statusDot.ForeColor);
                e.Graphics.FillEllipse(b, 0, 0, 9, 9);
            };
            p.Controls.Add(_statusDot);
            _status = new Label { AutoSize = false, AutoEllipsis = true, Location = new Point(74, 35), Size = new Size(ContentWidth - 74, 20), Font = new Font(Font, FontStyle.Bold), UseMnemonic = false, Name = "Status" };
            p.Controls.Add(_status);
            return p;
        }

        /// <summary>The one question a new install asks before anything is shared.</summary>
        private Panel FirstRunCard()
        {
            var card = new Panel { Width = ContentWidth, Margin = new Padding(0, 6, 0, 8), BackColor = Theme.Panel, Padding = new Padding(14), Name = "FirstRun" };
            card.Paint += (s, e) => { using var pen = new Pen(Theme.Line); e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1); };
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Panel, Location = new Point(14, 12), MaximumSize = new Size(ContentWidth - 28, 0) };
            flow.Controls.Add(new Label
            {
                Text = "Show what you're playing in DDO on your Discord profile?", AutoSize = true, MaximumSize = new Size(ContentWidth - 28, 0),
                Font = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold), ForeColor = Theme.Brass, UseMnemonic = false, Margin = new Padding(0, 0, 0, 8)
            });
            flow.Controls.Add(new Label
            {
                UseMnemonic = false, AutoSize = true, MaximumSize = new Size(ContentWidth - 28, 0), ForeColor = Theme.Text, BackColor = Theme.Panel,
                Text = "Your Discord status would show, for example:" + Environment.NewLine +
                       "  •  the quest you are in, or the town or area" + Environment.NewLine +
                       "  •  the quest's difficulty" + Environment.NewLine +
                       "  •  your character's level and class, and your party size" + Environment.NewLine +
                       "  •  how long you have been in the current quest or area" + Environment.NewLine +
                       "Your character's name is not shown unless you turn it on. You can switch each item off, hide places, " +
                       "or pause at any time. Discord desktop must be running.",
            });
            var buttons = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Panel, Margin = new Padding(0, 10, 0, 0) };
            var on = Theme.Action("Turn on");
            on.Name = "TurnOn";
            on.Font = new Font(Font, FontStyle.Bold);
            on.Click += (s, e) => { _plugin.AnswerFirstRun(true); LoadFromConfig(); RefreshView(); };
            var notNow = Theme.Action("Not now");
            notNow.Name = "NotNow";
            notNow.Click += (s, e) => { _plugin.AnswerFirstRun(false); LoadFromConfig(); RefreshView(); };
            buttons.Controls.Add(on);
            buttons.Controls.Add(notNow);
            flow.Controls.Add(buttons);
            card.Controls.Add(flow);
            card.Height = flow.PreferredSize.Height + 26;
            return card;
        }

        private static FlowLayoutPanel Row() => new FlowLayoutPanel
        {
            Width = ContentWidth, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
            Margin = new Padding(0, 2, 0, 2), Padding = new Padding(0), BackColor = Theme.Bg,
        };

        private Label Note(string text) => new Label
        {
            UseMnemonic = false, Text = text, AutoSize = true, MaximumSize = new Size(ContentWidth, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 4)
        };

        private Label Wrap(string text, Color color) => new Label
        {
            UseMnemonic = false, Text = text, AutoSize = true, MaximumSize = new Size(ContentWidth, 0), ForeColor = color, Margin = new Padding(0, 2, 0, 4)
        };

        private CheckBox Check(string text, Action<PresenceConfig> apply)
        {
            var c = new CheckBox { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 2, 0, 2) };
            c.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                _plugin.UpdateConfig(apply);
            };
            return c;
        }

        // ------------------------------------------------------------------ load / save

        private void LoadFromConfig()
        {
            _loading = true;
            try
            {
                var c = _plugin.Config;
                _shownConfig = c;
                _enabled.Checked = c.Enabled;
                _paused.Checked = c.Paused;
                _quest.Checked = c.ShowQuestName;
                _areas.Checked = c.ShowAreaNames;
                _difficulty.Checked = c.ShowDifficulty;
                _badge.Checked = c.ShowDifficultyBadge;
                _levelClass.Checked = c.ShowLevelClass;
                _charName.Checked = c.ShowCharacterName;
                _party.Checked = c.ShowPartySize;
                _elapsed.Checked = c.ShowElapsed;
                if (!_hidden.Focused) _hidden.Text = string.Join(Environment.NewLine, c.HiddenPlaces);
            }
            finally { _loading = false; }
        }

        private void SaveHidden()
        {
            var lines = (_hidden.Text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            _plugin.UpdateConfig(c => c.HiddenPlaces = new List<string>(lines));
            _hidden.Text = string.Join(Environment.NewLine, _plugin.Config.HiddenPlaces);
            _hiddenNote.Text = $"Saved ({_plugin.Config.HiddenPlaces.Count} entr{(_plugin.Config.HiddenPlaces.Count == 1 ? "y" : "ies")}).";
        }

        private async System.Threading.Tasks.Task RunTestAsync()
        {
            _test.Enabled = false;
            _testResult.ForeColor = Theme.Dim;
            _testResult.Text = "Sending a sample status to Discord...";
            try
            {
                var answer = await _plugin.TestAsync();
                if (IsDisposed) return;
                _testResult.ForeColor = answer.StartsWith("Discord accepted", StringComparison.Ordinal) ? Theme.Good : Theme.Bad;
                _testResult.Text = answer;
            }
            catch (Exception ex)
            {
                if (!IsDisposed) { _testResult.ForeColor = Theme.Bad; _testResult.Text = "Test failed: " + ex.Message; }
            }
            finally { if (!IsDisposed) _test.Enabled = true; }
        }

        // ------------------------------------------------------------------ live view

        private void OnPluginStateChanged()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(RefreshView));
            }
            catch { }
        }

        internal void RefreshView()
        {
            if (IsDisposed) return;
            var cfg = _plugin.Config;
            // Settings saved by another game client: show them (the shared file is the truth).
            if (_shownConfig == null || !ReferenceEquals(_shownConfig, cfg)) LoadFromConfig();

            var firstRun = cfg.NeedsFirstRunAnswer;
            if (_firstRun.Visible != firstRun) _firstRun.Visible = firstRun;
            foreach (var c in _settingsControls)
            {
                var show = !firstRun && (c != _diag || _diag.Visible);
                if (c == _diag) continue;      // its own toggle
                if (c.Visible != show) c.Visible = show;
            }

            var (kind, text) = _plugin.StatusLine();
            _status.Text = text;
            _statusDot.ForeColor = kind switch
            {
                PresenceStatusKind.Connected => Theme.Good,
                PresenceStatusKind.Delegated => Theme.Good,
                PresenceStatusKind.Error => Theme.Bad,
                PresenceStatusKind.Connecting => Theme.Brass,
                _ => Theme.Dim,
            };
            _status.ForeColor = kind == PresenceStatusKind.Error ? Theme.Bad : Theme.Text;
            _statusDot.Invalidate();
            var result = _plugin.Result;
            _preview.Show(result.Activity, result.Reason);
            if (_diag.Visible) _diag.Text = DiagnosticsText();
        }

        /// <summary>This client's raw readings, then every participating game client.</summary>
        internal string DiagnosticsText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(_plugin.Diagnostics.ToString());
            var view = _plugin.View;
            var now = DateTime.UtcNow;
            sb.AppendLine();
            sb.AppendLine(view.Participants.Count <= 1 ? "Game clients sharing: this one only" : $"Game clients sharing: {view.Participants.Count}");
            foreach (var p in view.Participants)
            {
                var c = p.Contribution;
                var who = !string.IsNullOrEmpty(c.Name) ? c.Name : "(name not shown)";
                var place = c.State == ContributionState.Visible ? c.Place : c.State == ContributionState.Hidden ? "(hidden place)" : "(" + c.Reason + ")";
                var age = Math.Max(0, (now - p.HeartbeatUtc).TotalSeconds).ToString("0", CultureInfo.InvariantCulture);
                var tags = new List<string>();
                if (p.InstanceId == view.LeaderId) tags.Add("leader");
                if (p.InstanceId == _plugin.InstanceId) tags.Add("this client");
                if (c.Foreground) tags.Add("foreground");
                sb.AppendLine($"  {who}: {place}{(tags.Count > 0 ? " [" + string.Join(", ", tags) + "]" : "")}, heartbeat {age} s ago");
            }
            if (_plugin.ConfigWarning != null) sb.AppendLine(_plugin.ConfigWarning);
            sb.Append("Settings are saved in " + ConfigStore.FileName + " beside the plugin.");
            return sb.ToString();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Dungeon Helper reuses this form; closing it would dispose the instance the host holds.
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _plugin.StateChanged -= OnPluginStateChanged;
                _tick.Stop();
                _tick.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

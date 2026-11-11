using System;
using DdoDiscordPresence.Coordination;
using DdoDiscordPresence.Discord;

namespace DdoDiscordPresence.Presence
{
    /// <summary>
    /// The panel's status line and the Test button's sample, kept apart from the plugin class and the panel so they
    /// can be used and tested without either.
    /// </summary>
    internal static class PresenceStatusText
    {
        /// <summary>The status line: settings first (off / paused), then the Discord connection or the client that holds it.</summary>
        public static (PresenceStatusKind Kind, string Text) StatusLine(PresenceConfig c, NodeView v, PresenceStatus? service, bool loggedIn)
        {
            if (c.NeedsFirstRunAnswer) return (PresenceStatusKind.Off, "Not sharing yet: choose below");
            if (!c.Enabled) return (PresenceStatusKind.Off, "Off: sharing is turned off");
            var s = service ?? new PresenceStatus(PresenceStatusKind.Error, "Error: not started");
            string text; var kind = s.Kind;
            if (!loggedIn && !v.IsLeader) { kind = PresenceStatusKind.Off; text = "Waiting for a character to log in"; }
            else if (!v.IsLeader && v.LeaderId != null)
            {
                kind = PresenceStatusKind.Delegated;
                text = "Shown by another game client" + (string.IsNullOrEmpty(v.LeaderStatus) ? "" : " (" + v.LeaderStatus + ")");
            }
            else if (!v.IsLeader && kind != PresenceStatusKind.Error) { kind = PresenceStatusKind.Connecting; text = "Starting..."; }
            else text = s.Text;
            if (c.Paused) return (kind, "Paused (nothing shown). " + text);
            return (kind, text);
        }

        /// <summary>The sample the Test button sends for 20 seconds.</summary>
        public static PresenceActivity SampleActivity(PresenceConfig c, DateTime nowUtc) => new PresenceActivity
        {
            Details = "The Sharn Syndicate",
            State = "Test" + PresenceFormatter.Separator + "Level 12 Paladin" + PresenceFormatter.Separator + "Elite",
            StartUnixSeconds = new DateTimeOffset(nowUtc).ToUnixTimeSeconds(),
            LargeImageKey = PresenceConfig.IsValidAssetKey(c.LargeImageKey) ? c.LargeImageKey : PresenceFormatter.DefaultLargeImageKey,
            LargeText = PresenceFormatter.GameTitle,
            SmallImageKey = c.ShowDifficultyBadge ? "elite" : null,
            SmallText = c.ShowDifficultyBadge ? "Elite" : null,
        };

        /// <summary>"07:42" or "1:07:42", as Discord shows the elapsed time.</summary>
        public static string FormatElapsed(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:00}:{1:00}", t.Minutes, t.Seconds);
        }
    }
}

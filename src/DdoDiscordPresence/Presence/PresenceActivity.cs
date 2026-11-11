using System.Text.Json.Nodes;

namespace DdoDiscordPresence.Presence
{
    /// <summary>
    /// The activity exactly as it is sent to Discord. A record, so two activities with the same content compare equal and
    /// the change detector can skip a resend.
    /// </summary>
    internal sealed record PresenceActivity
    {
        public string Details { get; init; } = "";
        public string? State { get; init; }
        /// <summary>Unix seconds; Discord shows "mm:ss elapsed" from it.</summary>
        public long? StartUnixSeconds { get; init; }
        public string LargeImageKey { get; init; } = PresenceFormatter.DefaultLargeImageKey;
        public string LargeText { get; init; } = PresenceFormatter.GameTitle;
        public string? SmallImageKey { get; init; }
        public string? SmallText { get; init; }

        public JsonObject ToJson()
        {
            var o = new JsonObject { ["details"] = Details };
            if (!string.IsNullOrEmpty(State)) o["state"] = State;
            if (StartUnixSeconds.HasValue) o["timestamps"] = new JsonObject { ["start"] = StartUnixSeconds.Value };
            var assets = new JsonObject { ["large_image"] = LargeImageKey, ["large_text"] = LargeText };
            if (!string.IsNullOrEmpty(SmallImageKey))
            {
                assets["small_image"] = SmallImageKey;
                if (!string.IsNullOrEmpty(SmallText)) assets["small_text"] = SmallText;
            }
            o["assets"] = assets;
            return o;
        }
    }

    /// <summary>The formatter's output: the activity to show, or null with the reason nothing is shown.</summary>
    internal sealed record PresenceResult(PresenceActivity? Activity, string Reason)
    {
        public static PresenceResult Nothing(string reason) => new PresenceResult(null, reason);
    }

    /// <summary>
    /// Decides whether a SET_ACTIVITY is needed. After a (re)connect Discord holds no activity for us, so "nothing" is
    /// already in effect; an unchanged activity is never resent.
    /// </summary>
    internal sealed class ActivityChangeDetector
    {
        private bool _known;
        private PresenceActivity? _last;

        /// <summary>Call on every new connection: Discord starts with no activity for this client.</summary>
        public void ResetForNewConnection() { _known = true; _last = null; }

        /// <summary>The connection state is unknown (no connection yet).</summary>
        public void Forget() { _known = false; _last = null; }

        public bool ShouldSend(PresenceActivity? desired) => !_known || !Equals(_last, desired);

        public void MarkSent(PresenceActivity? sent) { _known = true; _last = sent; }

        public PresenceActivity? LastSent => _last;
    }
}

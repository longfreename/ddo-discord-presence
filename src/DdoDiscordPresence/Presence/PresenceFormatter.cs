using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DdoDiscordPresence.Presence
{
    /// <summary>What one game client offers to the shared status.</summary>
    internal enum ContributionState
    {
        /// <summary>Not sharing (logged out, sharing off or paused, or nothing readable).</summary>
        None = 0,
        /// <summary>Sharing.</summary>
        Visible = 1,
        /// <summary>In a place on the hide list: if this is the primary character the whole status is cleared.</summary>
        Hidden = 2,
    }

    /// <summary>
    /// One game client's part of the status, already reduced by THAT client's privacy settings (a name is present only
    /// when its "show character name" is on). Instances exchange these through the coordination folder, so they hold
    /// display text, never raw ids beyond the place key.
    /// </summary>
    internal sealed record Contribution
    {
        public string InstanceId { get; init; } = "";
        public DateTime StartedUtc { get; init; }
        public ContributionState State { get; init; }
        public string Reason { get; init; } = "";
        public bool Foreground { get; init; }
        /// <summary>Character name, only when that client shows names.</summary>
        public string? Name { get; init; }
        /// <summary>Identity of the place (quest instance or area), to tell "same place" from "elsewhere".</summary>
        public string PlaceKey { get; init; } = "";
        /// <summary>The details line for this character: quest or area name, or the generic text its settings ask for.</summary>
        public string Place { get; init; } = "";
        public bool InQuest { get; init; }
        public DateTime PlaceSinceUtc { get; init; }
        public bool ShowElapsed { get; init; }
        public string? LevelClass { get; init; }
        public string? Level { get; init; }
        public string? Difficulty { get; init; }
        public string? Party { get; init; }
        public string? BadgeKey { get; init; }
        public string? BadgeText { get; init; }
    }

    /// <summary>
    /// Pure: game snapshot + settings + clock in, Discord activity (or nothing, with the reason) out. Every Discord field
    /// rule is enforced here: details/state are 2-128 characters, trimmed, free of control characters and markup, a
    /// name that cannot be read (empty, a hex DID, digits only) is never sent, and text is cut at a word boundary.
    ///
    /// Layout, single character:   details = "The Sharn Syndicate"
    ///                             state   = "Thalindra · Level 15 Paladin · Elite · Party of 4"
    /// Multi-boxing (2+ names):    details = "The Sharn Syndicate · +1 elsewhere"
    ///                             state   = "Borrik, Thalindra, Vessa · Elite · Party of 4"
    /// The name leads the state line because it answers "who"; the place stays alone on the first line, which Discord
    /// shows in bold-ish primary text, so a long quest name never competes with the character list. When the state line
    /// is too long the least important parts go first: party size, then class (the level stays), then difficulty, then
    /// the level; a name list that still does not fit ends with "+N more" and the full list moves to the large image's
    /// hover text.
    /// </summary>
    internal static class PresenceFormatter
    {
        public const string GameTitle = "Dungeons & Dragons Online";
        public const string DefaultLargeImageKey = "ddo";
        public const int MinLength = 2;
        public const int MaxLength = 128;
        public const string Separator = " · ";   // " · "
        public const string NameSeparator = ", ";

        /// <summary>The badge asset keys, in difficulty order.</summary>
        public static readonly IReadOnlyList<string> BadgeKeys = new[] { "casual", "normal", "hard", "elite", "reaper" };

        // ------------------------------------------------------------------ one character

        /// <summary>The single-instance status: this client's contribution merged on its own.</summary>
        public static PresenceResult Format(GameSnapshot game, PresenceConfig config, DateTime nowUtc)
        {
            var own = Contribute(game, config, nowUtc, "self", default, foreground: true);
            return Merge(new[] { own }, "self", config);
        }

        /// <summary>This client's contribution under its own settings.</summary>
        public static Contribution Contribute(GameSnapshot game, PresenceConfig config, DateTime nowUtc, string instanceId, DateTime startedUtc, bool foreground)
        {
            Contribution None(string reason) => new Contribution { InstanceId = instanceId, StartedUtc = startedUtc, State = ContributionState.None, Reason = reason, Foreground = foreground };
            if (config == null) return None("no settings");
            if (!config.HasValidApplicationId) return None("No Discord application");
            if (!config.Enabled) return None(config.NeedsFirstRunAnswer ? "Sharing is not turned on yet" : "Sharing is turned off");
            if (config.Paused) return None("Sharing is paused");
            if (game == null || !game.LoggedIn) return None("Not logged in to a character");

            var quest = CleanText(game.QuestName);
            var area = CleanText(game.AreaName);
            var hidden = MatchHidden(config.HiddenPlaces, quest, area);
            if (hidden != null)
                return new Contribution
                {
                    InstanceId = instanceId, StartedUtc = startedUtc, State = ContributionState.Hidden, Foreground = foreground,
                    Reason = $"Hidden here (matches \"{hidden}\")", PlaceKey = game.PlaceKey, InQuest = game.InQuest,
                };

            string? place;
            if (game.InQuest) place = !config.ShowQuestName ? "In a quest" : quest ?? area;   // a quest's own area is usually named after the dungeon
            else place = !config.ShowAreaNames ? (game.InTown == true ? "In town" : "In a public area") : area;
            place = FitWords(place);
            if (place == null)
                return None(game.InQuest ? "The quest's name is not readable yet" : "The area's name is not readable yet");

            var diff = game.InQuest && config.ShowDifficulty ? DifficultyText(game.Difficulty, game.ReaperSkulls) : null;
            string? badge = diff != null && config.ShowDifficultyBadge ? BadgeKey(game.Difficulty) : null;
            var cls = CleanText(game.ClassName);
            return new Contribution
            {
                InstanceId = instanceId,
                StartedUtc = startedUtc,
                State = ContributionState.Visible,
                Reason = game.InQuest ? "In a quest" : "In a public area",
                Foreground = foreground,
                Name = config.ShowCharacterName ? Fit(CleanText(game.CharacterName)) : null,
                PlaceKey = game.PlaceKey,
                Place = place,
                InQuest = game.InQuest,
                PlaceSinceUtc = game.PlaceSinceUtc != default && game.PlaceSinceUtc <= nowUtc.AddMinutes(1) ? game.PlaceSinceUtc : default,
                ShowElapsed = config.ShowElapsed,
                LevelClass = config.ShowLevelClass ? LevelClassText(game.Level, cls) : null,
                Level = config.ShowLevelClass ? LevelClassText(game.Level, null) : null,
                Difficulty = diff,
                Party = config.ShowPartySize && game.PartySize is int size && size >= 2 && size <= 12 ? $"Party of {size.ToString(CultureInfo.InvariantCulture)}" : null,
                BadgeKey = badge,
                BadgeText = badge != null ? Fit(diff) : null,
            };
        }

        // ------------------------------------------------------------------ merge

        /// <summary>
        /// The primary character: the client whose game window is in the foreground, else the leader, else the one whose
        /// client started first. Only sharing (visible or hidden-here) clients qualify.
        /// </summary>
        public static Contribution? PickPrimary(IReadOnlyList<Contribution> all, string? leaderId)
        {
            var c = all.Where(x => x.State != ContributionState.None).OrderBy(x => x.StartedUtc).ThenBy(x => x.InstanceId, StringComparer.Ordinal).ToList();
            return c.FirstOrDefault(x => x.Foreground) ?? c.FirstOrDefault(x => x.InstanceId == leaderId) ?? c.FirstOrDefault();
        }

        /// <summary>
        /// Merges every live client's contribution into one activity. Names are listed in the order the clients started
        /// (a stable order that does not change when you switch windows); the place, difficulty, party and timer are the
        /// primary character's. A hidden place for the primary clears everything; a hidden secondary is just left out.
        /// </summary>
        public static PresenceResult Merge(IReadOnlyList<Contribution> all, string? leaderId, PresenceConfig config)
        {
            var primary = PickPrimary(all, leaderId);
            if (primary == null)
            {
                var own = all.FirstOrDefault(x => x.InstanceId == leaderId) ?? all.FirstOrDefault();
                return PresenceResult.Nothing(own?.Reason ?? "No character is sharing");
            }
            if (primary.State == ContributionState.Hidden) return PresenceResult.Nothing(primary.Reason);

            var visible = all.Where(x => x.State == ContributionState.Visible)
                .OrderBy(x => x.StartedUtc).ThenBy(x => x.InstanceId, StringComparer.Ordinal).ToList();
            var names = visible.Where(x => !string.IsNullOrEmpty(x.Name)).Select(x => x.Name!).ToList();
            var elsewhere = visible.Count(x => x.InstanceId != primary.InstanceId && x.PlaceKey != primary.PlaceKey);

            var layout = Layout(primary.Place, elsewhere, names, primary.LevelClass, primary.Level, primary.Difficulty, primary.Party);
            if (layout.Details == null) return PresenceResult.Nothing("The place's name is not readable");

            long? start = null;
            if (primary.ShowElapsed && primary.PlaceSinceUtc != default)
                start = new DateTimeOffset(DateTime.SpecifyKind(primary.PlaceSinceUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
            var largeKey = PresenceConfig.IsValidAssetKey(config?.LargeImageKey) ? config!.LargeImageKey : DefaultLargeImageKey;
            var activity = new PresenceActivity
            {
                Details = layout.Details,
                State = layout.State,
                StartUnixSeconds = start,
                LargeImageKey = largeKey,
                LargeText = layout.LargeText ?? GameTitle,
                SmallImageKey = primary.BadgeKey != null && primary.BadgeText != null ? primary.BadgeKey : null,
                SmallText = primary.BadgeKey != null ? primary.BadgeText : null,
            };
            var reason = names.Count >= 2 ? $"{names.Count} characters" : primary.Reason;
            return new PresenceResult(activity, reason);
        }

        /// <summary>The text layout (pure, see the class summary). Details null = nothing readable to show.</summary>
        public static (string? Details, string? State, string? LargeText) Layout(
            string? place, int elsewhere, IReadOnlyList<string> names, string? levelClass, string? level, string? difficulty, string? party)
        {
            var details = FitWords(place);
            if (details == null) return (null, null, null);
            if (elsewhere > 0)
            {
                var hinted = details + Separator + "+" + elsewhere.ToString(CultureInfo.InvariantCulture) + " elsewhere";
                if (hinted.Length <= MaxLength) details = hinted;
            }

            var multi = names.Count >= 2;
            // Class and level describe one character: with several names they would be ambiguous, so they are left out.
            string? lc = multi ? null : levelClass, lvl = multi ? null : level;
            string? diff = difficulty, pty = party;
            string Join(string? who) => string.Join(Separator, new[] { who, lc, diff, pty }.Where(p => !string.IsNullOrEmpty(p)));
            var full = names.Count == 0 ? null : string.Join(NameSeparator, names);

            // Drop the least important parts first: party, class (keep the level), difficulty, level.
            var steps = new Action[] { () => pty = null, () => lc = lvl, () => diff = null, () => lc = null };
            var i = 0;
            while (Join(full).Length > MaxLength && i < steps.Length) steps[i++]();

            string? largeText = null;
            var who = full;
            if (full != null && Join(full).Length > MaxLength)
            {
                // Still too long: only the name list is left to shorten.
                var budget = MaxLength - (Join(null).Length > 0 ? Join(null).Length + Separator.Length : 0);
                who = CapNames(names, budget);
                largeText = Fit(FitWords("Playing as " + full));
            }
            var state = Join(who);
            state = state.Length == 0 ? null : FitWords(state);
            return (details, state, largeText);
        }

        /// <summary>"A, B, C +9 more" within <paramref name="budget"/> characters (at least one name, cut if it alone is too long).</summary>
        public static string CapNames(IReadOnlyList<string> names, int budget)
        {
            if (names.Count == 0) return "";
            budget = Math.Max(budget, 12);
            var all = string.Join(NameSeparator, names);
            if (all.Length <= budget) return all;
            for (var shown = names.Count - 1; shown >= 1; shown--)
            {
                var text = string.Join(NameSeparator, names.Take(shown)) + " +" + (names.Count - shown).ToString(CultureInfo.InvariantCulture) + " more";
                if (text.Length <= budget) return text;
            }
            var more = " +" + (names.Count - 1).ToString(CultureInfo.InvariantCulture) + " more";
            var first = names[0];
            var room = Math.Max(2, budget - more.Length - 1);
            return (first.Length > room ? first.Substring(0, room).TrimEnd() + "…" : first) + more;
        }

        // ------------------------------------------------------------------ parts

        public static string? DifficultyText(Difficulty d, int? skulls) => d switch
        {
            Presence.Difficulty.Solo => "Solo",
            Presence.Difficulty.Casual => "Casual",
            Presence.Difficulty.Normal => "Normal",
            Presence.Difficulty.Hard => "Hard",
            Presence.Difficulty.Elite => "Elite",
            Presence.Difficulty.Reaper => skulls is int s && s >= 1 && s <= 10 ? "Reaper " + s.ToString(CultureInfo.InvariantCulture) : "Reaper",
            _ => null,
        };

        public static string? BadgeKey(Difficulty d) => d switch
        {
            Presence.Difficulty.Casual => "casual",
            Presence.Difficulty.Normal => "normal",
            Presence.Difficulty.Hard => "hard",
            Presence.Difficulty.Elite => "elite",
            Presence.Difficulty.Reaper => "reaper",
            _ => null,
        };

        public static string? LevelClassText(int? level, string? className)
        {
            var lvl = level is int l && l >= 1 && l <= 40 ? "Level " + l.ToString(CultureInfo.InvariantCulture) : null;
            if (lvl != null && className != null) return lvl + " " + className;
            return lvl ?? className;
        }

        /// <summary>The first hide entry contained in the quest or area name (case-insensitive), or null.</summary>
        public static string? MatchHidden(IEnumerable<string>? hidden, params string?[] names)
        {
            if (hidden == null) return null;
            foreach (var raw in hidden)
            {
                var entry = raw?.Trim();
                if (string.IsNullOrEmpty(entry)) continue;
                foreach (var n in names)
                    if (n != null && n.IndexOf(entry, StringComparison.OrdinalIgnoreCase) >= 0) return entry;
            }
            return null;
        }

        private static readonly Regex Markup = new Regex(@"<[^<>]{0,80}>", RegexOptions.CultureInvariant);
        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.CultureInvariant);
        private static readonly Regex HexId = new Regex(@"^(0x)?[0-9a-fA-F]{6,16}$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Trims, removes control/format characters and client markup, collapses whitespace. Returns null for anything
        /// that is not a readable name: empty, a hex DID, digits only, unresolved placeholders, or (by default) fewer
        /// than two letters.
        /// </summary>
        public static string? CleanText(string? raw, bool requireLetters = true)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var sb = new StringBuilder(raw.Length);
            foreach (var ch in raw)
            {
                var cat = char.GetUnicodeCategory(ch);
                if (char.IsControl(ch) || cat == UnicodeCategory.Format || cat == UnicodeCategory.LineSeparator || cat == UnicodeCategory.ParagraphSeparator)
                    sb.Append(' ');
                else sb.Append(ch);
            }
            var s = Markup.Replace(sb.ToString(), " ");
            s = Spaces.Replace(s, " ").Trim();
            if (s.Length == 0) return null;
            if (!requireLetters) return s;
            if (HexId.IsMatch(s)) return null;
            if (s.Contains("${", StringComparison.Ordinal) || s.Contains("%s", StringComparison.Ordinal)) return null;
            if (s.Count(char.IsLetter) < 2) return null;
            return s;
        }

        /// <summary>Enforces Discord's 2-128 character rule: longer text is cut (never inside a surrogate pair) with an ellipsis; shorter is null.</summary>
        public static string? Fit(string? text)
        {
            if (text == null) return null;
            var s = text.Trim();
            if (s.Length > MaxLength)
            {
                var cut = MaxLength - 1;
                if (char.IsHighSurrogate(s[cut - 1])) cut--;
                s = s.Substring(0, cut).TrimEnd() + "…";
            }
            return s.Length < MinLength ? null : s;
        }

        /// <summary>Like <see cref="Fit"/>, but a cut falls on the last word boundary when one is in the second half.</summary>
        public static string? FitWords(string? text)
        {
            if (text == null) return null;
            var s = text.Trim();
            if (s.Length <= MaxLength) return s.Length < MinLength ? null : s;
            var limit = MaxLength - 1;
            var space = s.LastIndexOf(' ', limit);
            if (space >= MaxLength / 2) return s.Substring(0, space).TrimEnd(' ', ',', '·') + "…";
            return Fit(s);
        }
    }
}

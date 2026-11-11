using System;

namespace DdoDiscordPresence.Presence
{
    /// <summary>Quest difficulty as shown to players. Epic variants fold into the same names (DDO shows them that way).</summary>
    internal enum Difficulty
    {
        Unknown = 0,
        Solo,
        Casual,
        Normal,
        Hard,
        Elite,
        Reaper,
    }

    /// <summary>
    /// What the game says right now, already reduced to plain values. Built by <c>Game.GameReader</c> from the SDK; the
    /// formatter only ever sees this record, so every presence rule is testable without the game.
    /// </summary>
    internal sealed record GameSnapshot
    {
        public bool LoggedIn { get; init; }
        /// <summary>IDdoGameDataProvider.InTown(): true in a peaceful zone (town / public area), null when unknown.</summary>
        public bool? InTown { get; init; }
        /// <summary>GetInstanceQuestDid(): the quest of the current instance (0 = none).</summary>
        public uint InstanceQuestDid { get; init; }
        public string? QuestName { get; init; }
        /// <summary>GetMapInfo().AreaDid: the 0x70 area of the current map.</summary>
        public uint AreaDid { get; init; }
        public string? AreaName { get; init; }
        public string? CharacterName { get; init; }
        public int? Level { get; init; }
        public string? ClassName { get; init; }
        /// <summary>Party size including the player; null when unknown.</summary>
        public int? PartySize { get; init; }
        public Difficulty Difficulty { get; init; }
        public int? ReaperSkulls { get; init; }
        /// <summary>When the character entered the current quest (or public area).</summary>
        public DateTime PlaceSinceUtc { get; init; }

        /// <summary>
        /// Inside a quest instance: not in a peaceful zone and the instance names a quest. The same rule Hireling Commander
        /// uses (AutoSummoner.QuestKey). GetCurrentQuestDid() is NOT used: it follows the journal and can be stale.
        /// </summary>
        public bool InQuest => LoggedIn && InTown == false && InstanceQuestDid != 0;

        /// <summary>Identity of "where I am" for the elapsed timer: the quest instance, else the area.</summary>
        public string PlaceKey => InQuest ? "q:" + InstanceQuestDid.ToString("X8") : "a:" + AreaDid.ToString("X8");

        public static readonly GameSnapshot LoggedOut = new GameSnapshot();
    }

    /// <summary>
    /// Keeps the "since" time of the current place. A new place starts its timer at the map-area-change event when that
    /// event was recent (the SDK's values settle a moment after the event), else at the time it was first seen.
    /// Moving between areas inside one quest keeps the quest's timer.
    /// </summary>
    internal sealed class PlaceTracker
    {
        public static readonly TimeSpan AreaChangeWindow = TimeSpan.FromSeconds(30);
        private string? _key;
        private DateTime _since;

        public DateTime Update(string placeKey, DateTime nowUtc, DateTime? lastAreaChangeUtc)
        {
            if (placeKey != _key)
            {
                _key = placeKey;
                _since = lastAreaChangeUtc.HasValue && lastAreaChangeUtc.Value <= nowUtc && nowUtc - lastAreaChangeUtc.Value <= AreaChangeWindow
                    ? lastAreaChangeUtc.Value : nowUtc;
            }
            return _since;
        }

        public void Reset() { _key = null; _since = default; }
        public string? CurrentKey => _key;
    }
}

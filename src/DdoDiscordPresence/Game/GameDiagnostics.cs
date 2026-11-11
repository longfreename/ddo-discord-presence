using System;
using System.Globalization;
using DdoDiscordPresence.Presence;

namespace DdoDiscordPresence.Game
{
    // Nothing in this file touches the Dungeon Helper SDK.

    /// <summary>Raw readings behind a snapshot, shown in the settings window's diagnostics so the owner can check them.</summary>
    internal sealed class GameDiagnostics
    {
        public ulong CharacterId;
        public bool? InTown;
        public uint InstanceQuestDid;
        public uint JournalQuestDid;
        public uint AreaDid;
        public bool MapValid;
        public string? QuestNameRaw;
        public string? AreaNameRaw;
        public string DifficultySource = "none";
        public uint? DifficultyRaw;
        public int? ReaperRaw;
        public int? LevelRaw;
        public string? ClassesRaw;
        public int? PartyListCount;

        public override string ToString() => string.Join("  ", new[]
        {
            $"char {(CharacterId != 0 ? "yes" : "none")}",
            $"InTown={Fmt(InTown)}",
            $"instance quest 0x{InstanceQuestDid:X8} ({QuestNameRaw ?? "no name"})",
            $"journal quest 0x{JournalQuestDid:X8}",
            $"area 0x{AreaDid:X8} ({AreaNameRaw ?? "no name"}){(MapValid ? "" : " map not valid")}",
            $"difficulty {DifficultySource}={Fmt(DifficultyRaw)} reaper={Fmt(ReaperRaw)}",
            $"level={Fmt(LevelRaw)} classes={ClassesRaw ?? "-"} party list={Fmt(PartyListCount)}",
        });

        private static string Fmt<T>(T? v) where T : struct => v.HasValue ? Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? "-" : "-";
    }

    /// <summary>VoK.Sdk.Ddo.Enums.DifficultyType to the displayed difficulty.</summary>
    internal static class DifficultyMap
    {
        /// <summary>Epic variants fold in; anything that is not a quest difficulty is Unknown and is left out.</summary>
        public static Difficulty FromDifficultyType(uint raw) => raw switch
        {
            0 => Difficulty.Solo,
            1 => Difficulty.Normal,
            2 => Difficulty.Hard,
            3 => Difficulty.Elite,
            7 => Difficulty.Casual,
            8 => Difficulty.Casual,     // Epic_Casual
            9 => Difficulty.Hard,       // Epic_Hard
            10 => Difficulty.Elite,     // Epic_Elite
            11 => Difficulty.Reaper,
            12 => Difficulty.Reaper,    // Epic_Reaper
            _ => Difficulty.Unknown,    // 4 EliteFinished, 5 Epic (no tier), 200+ item rarities, unknown
        };

        /// <summary>
        /// Dungeon_CachedDifficulty on the character holds the ID of a difficulty PROPERTY, not a number (seen live
        /// 2026-11-11: type PropertyId, value Dungeon_EasyDifficulty). The game's internal names are older than the
        /// names players see: Easy / Medium / Hard are Normal / Hard / Elite. Epic and Fabled variants fold in.
        /// </summary>
        public static Difficulty FromDifficultyProperty(uint propertyId) => propertyId switch
        {
            268446866u => Difficulty.Solo,      // Dungeon_SoloDifficulty
            268454990u => Difficulty.Casual,    // Dungeon_CasualDifficulty
            268460948u => Difficulty.Casual,    // Dungeon_EpicCasualDifficulty
            268485007u => Difficulty.Casual,    // Dungeon_FabledCasualDifficulty
            268442566u => Difficulty.Normal,    // Dungeon_EasyDifficulty
            268453728u => Difficulty.Normal,    // Dungeon_EpicDifficulty
            268484997u => Difficulty.Normal,    // Dungeon_FabledDifficulty
            268442564u => Difficulty.Hard,      // Dungeon_MediumDifficulty
            268460947u => Difficulty.Hard,      // Dungeon_EpicHardDifficulty
            268484998u => Difficulty.Hard,      // Dungeon_FabledHardDifficulty
            268442567u => Difficulty.Elite,     // Dungeon_HardDifficulty
            268460946u => Difficulty.Elite,     // Dungeon_EpicEliteDifficulty
            268484999u => Difficulty.Elite,     // Dungeon_FabledEliteDifficulty
            268477892u => Difficulty.Reaper,    // Dungeon_ReaperDifficulty
            268477891u => Difficulty.Reaper,    // Dungeon_EpicReaperDifficulty
            _ => Difficulty.Unknown,
        };

        /// <summary>
        /// One verdict from what was read. A reaper level of 1..10 means Reaper with that many skulls whatever the
        /// difficulty property says: in a Reaper 1 quest the character read Dungeon_CachedReaperLevel = 1 while
        /// Dungeon_CachedDifficulty still named a non-reaper difficulty. Otherwise the named difficulty property,
        /// then a DifficultyType number.
        /// </summary>
        public static (Difficulty Difficulty, int? Skulls) Resolve(uint? difficultyProperty, uint? difficultyType, int? reaperLevel)
        {
            if (reaperLevel is int s && s >= 1 && s <= 10) return (Difficulty.Reaper, s);
            var d = difficultyProperty.HasValue ? FromDifficultyProperty(difficultyProperty.Value) : Difficulty.Unknown;
            if (d == Difficulty.Unknown && difficultyType.HasValue) d = FromDifficultyType(difficultyType.Value);
            return (d, null);
        }
    }
}

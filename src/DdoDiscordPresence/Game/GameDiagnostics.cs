using System;
using System.Collections.Generic;
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
        /// <summary>The difficulty property the quest entry panel reported for this instance, when one was seen.</summary>
        public uint? PortalDifficulty;
        /// <summary>What the monsters nearby say (see DifficultyMap.FromMonsterVariants), and how many were read.</summary>
        public Difficulty MonsterDifficulty;
        public bool MonsterDifficultyCertain;
        public int MonstersRead;
        public bool? EpicSide;
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
            $"difficulty {DifficultySource}={Fmt(DifficultyRaw)} reaper={Fmt(ReaperRaw)} entry panel={Fmt(PortalDifficulty)} monsters={MonsterDifficulty}{(MonsterDifficultyCertain ? "" : "?")}/{MonstersRead}",
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

        /// <summary>The ids of the per-difficulty rows a monster carries (Dungeon_*Difficulty), with what players call each.</summary>
        public static readonly IReadOnlyList<(uint Property, Difficulty Difficulty)> VariantRows = new[]
        {
            (268446866u, Difficulty.Solo), (268454990u, Difficulty.Casual), (268460948u, Difficulty.Casual),
            (268442566u, Difficulty.Normal), (268453728u, Difficulty.Normal),
            (268442564u, Difficulty.Hard), (268460947u, Difficulty.Hard),
            (268442567u, Difficulty.Elite), (268460946u, Difficulty.Elite),
        };

        /// <summary>
        /// The difficulty of the instance, from its monsters. Every monster carries one record id per difficulty
        /// (Dungeon_EasyDifficulty, Dungeon_MediumDifficulty, Dungeon_HardDifficulty ... and the Epic rows), and its OWN
        /// record id is one of them: seen live 2026-11-11 in a Heroic Elite run, two different monsters each matched
        /// their Dungeon_HardDifficulty row (the game's Easy / Medium / Hard are Normal / Hard / Elite). Works with no
        /// entry panel at all. A monster whose id matches rows of more than one difficulty (Casual and Normal often
        /// share a record) is an uncertain vote for the highest of them that is not Solo; certain votes win.
        /// Reaper runs use the Elite records, so the reaper level is checked first by the caller.
        /// </summary>
        public static (Difficulty Difficulty, bool Certain) FromMonsterVariants(IEnumerable<(uint RecordId, IReadOnlyDictionary<uint, uint> Rows)> monsters)
        {
            var certain = new Dictionary<Difficulty, int>();
            var unsure = new Dictionary<Difficulty, int>();
            foreach (var (id, rows) in monsters)
            {
                if (id == 0 || rows == null) continue;
                var hit = new HashSet<Difficulty>();
                foreach (var (prop, diff) in VariantRows)
                    if (rows.TryGetValue(prop, out var rowId) && rowId == id) hit.Add(diff);
                if (hit.Count == 0) continue;
                if (hit.Count == 1) { foreach (var h in hit) certain[h] = certain.TryGetValue(h, out var n) ? n + 1 : 1; continue; }
                hit.Remove(Difficulty.Solo);
                var pick = Difficulty.Unknown;
                foreach (var h in new[] { Difficulty.Normal, Difficulty.Hard, Difficulty.Elite, Difficulty.Casual })
                    if (hit.Contains(h)) { pick = h; break; }
                if (pick != Difficulty.Unknown) unsure[pick] = unsure.TryGetValue(pick, out var m) ? m + 1 : 1;
            }
            var best = Top(certain);
            if (best != Difficulty.Unknown) return (best, true);
            return (Top(unsure), false);
        }

        private static readonly HashSet<uint> EpicRows = new HashSet<uint> { 268460948u, 268453728u, 268460947u, 268460946u, 268477891u };
        private static readonly HashSet<uint> HeroicRows = new HashSet<uint> { 268454990u, 268442566u, 268442564u, 268442567u, 268477892u };

        /// <summary>A difficulty property (as the entry panel names it): epic side, heroic side, or neither known.</summary>
        public static bool? IsEpicProperty(uint propertyId) =>
            EpicRows.Contains(propertyId) ? true : HeroicRows.Contains(propertyId) ? false : (bool?)null;

        /// <summary>
        /// Which side of the quest the monsters are from: a monster whose record id matches only Epic rows votes epic,
        /// only heroic rows votes heroic (the Solo row is ignored: it is a shared placeholder). Null when no monster says.
        /// </summary>
        public static bool? EpicSideFromMonsters(IEnumerable<(uint RecordId, IReadOnlyDictionary<uint, uint> Rows)> monsters)
        {
            int epic = 0, heroic = 0;
            foreach (var (id, rows) in monsters)
            {
                if (id == 0 || rows == null) continue;
                bool e = false, h = false;
                foreach (var kv in rows)
                {
                    if (kv.Value != id) continue;
                    if (EpicRows.Contains(kv.Key)) e = true; else if (HeroicRows.Contains(kv.Key)) h = true;
                }
                if (e && !h) epic++; else if (h && !e) heroic++;
            }
            return epic == heroic ? (bool?)null : epic > heroic;
        }

        private static Difficulty Top(Dictionary<Difficulty, int> votes)
        {
            var best = Difficulty.Unknown; var count = 0;
            foreach (var kv in votes) if (kv.Value > count) { best = kv.Key; count = kv.Value; }
            return best;
        }

        /// <summary>
        /// Everything together: the reaper level first; then what the monsters say when they are certain; then the
        /// entry panel; then an uncertain monster reading; then a DifficultyType number.
        /// </summary>
        public static (Difficulty Difficulty, int? Skulls) Decide(uint? entryPanelProperty, uint? difficultyType, int? reaperLevel,
            Difficulty monsters, bool monstersCertain)
        {
            if (reaperLevel is int s && s >= 1 && s <= 10) return (Difficulty.Reaper, s);
            if (monsters != Difficulty.Unknown && monstersCertain) return (monsters, null);
            var panel = entryPanelProperty.HasValue ? FromDifficultyProperty(entryPanelProperty.Value) : Difficulty.Unknown;
            if (panel != Difficulty.Unknown) return (panel, null);
            if (monsters != Difficulty.Unknown) return (monsters, null);
            return (difficultyType.HasValue ? FromDifficultyType(difficultyType.Value) : Difficulty.Unknown, null);
        }

        /// <summary>
        /// One verdict from what was read. A reaper level of 1..10 means Reaper with that many skulls (the
        /// character's Dungeon_CachedReaperLevel read 1 in a Reaper 1 quest and 0 in an Elite one, 2026-11-11).
        /// Otherwise the difficulty property the quest entry panel named, then a DifficultyType number.
        /// The character's Dungeon_CachedDifficulty is NOT a source: it read Dungeon_EasyDifficulty in both a
        /// Reaper 1 and a Heroic Elite run of the same quest.
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

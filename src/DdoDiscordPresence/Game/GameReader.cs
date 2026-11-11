using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DdoDiscordPresence.Presence;
using VoK.Sdk;
using VoK.Sdk.Ddo;
using VoK.Sdk.Properties;

namespace DdoDiscordPresence.Game
{
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

    /// <summary>
    /// The only class that reads the game (besides Plugin's event wiring). Every SDK call is wrapped: a member that throws
    /// or returns nothing just leaves that field unknown, and unknown fields are left out of the presence.
    /// </summary>
    internal static class GameReader
    {
        // DdoProperty ids, from the VoK.Sdk 4.3.2 XML documentation (VoK.Sdk.Enums.DdoProperty).
        /// <summary>Quest_Name (0x1000030B), StringInfo; verified by Hireling Commander (GameHelpers.QuestName).</summary>
        public const uint Prop_Quest_Name = 0x1000030Bu;
        /// <summary>Area_Name (0x00000643), StringInfo on the 0x70 area definition (the client's area records carry it, e.g. "The Pit").</summary>
        public const uint Prop_Area_Name = 0x00000643u;
        /// <summary>Character_TotalLevel (268440965), Int32; verified live by Hireling Commander (= 15 on a level 15 character).</summary>
        public const uint Prop_Character_TotalLevel = 0x10001585u;
        /// <summary>Dungeon_DifficultyLevel (0x10001BC5), UInt32. [HYP] DifficultyType value of the current instance.</summary>
        public const uint Prop_Dungeon_DifficultyLevel = 0x10001BC5u;
        /// <summary>Dungeon_CachedDifficulty (0x10001CB9), UInt32. [HYP]</summary>
        public const uint Prop_Dungeon_CachedDifficulty = 0x10001CB9u;
        /// <summary>CachedDungeonDifficulty (0x10009A62), Enum. [HYP]</summary>
        public const uint Prop_CachedDungeonDifficulty = 0x10009A62u;
        /// <summary>Dungeon_ReaperLevel (0x1000A6AC), Int32. [HYP] reaper skulls of the instance.</summary>
        public const uint Prop_Dungeon_ReaperLevel = 0x1000A6ACu;
        /// <summary>Dungeon_CachedReaperLevel (0x1000A6AF), Int32. [HYP]</summary>
        public const uint Prop_Dungeon_CachedReaperLevel = 0x1000A6AFu;

        /// <summary>Reads everything the presence may use. Never throws.</summary>
        public static (GameSnapshot Snapshot, GameDiagnostics Diag) Read(IDdoGameDataProvider? p)
        {
            var d = new GameDiagnostics();
            if (p == null) return (GameSnapshot.LoggedOut, d);
            ulong charId = 0;
            try { charId = p.GetCurrentCharacterId() ?? 0; } catch { }
            d.CharacterId = charId;
            if (charId == 0) return (GameSnapshot.LoggedOut, d);

            bool? inTown = null;
            try { inTown = p.InTown(); } catch { }
            d.InTown = inTown;
            uint instanceQuest = 0, journalQuest = 0, areaDid = 0;
            try { instanceQuest = p.GetInstanceQuestDid() ?? 0; } catch { }
            try { journalQuest = p.GetCurrentQuestDid() ?? 0; } catch { }
            try
            {
                var map = p.GetMapInfo();
                if (map != null) { d.MapValid = map.IsValid; if (map.IsValid) areaDid = map.AreaDid; }
            }
            catch { }
            d.InstanceQuestDid = instanceQuest; d.JournalQuestDid = journalQuest; d.AreaDid = areaDid;

            var questName = instanceQuest != 0 ? StringInfo(p, instanceQuest, Prop_Quest_Name) : null;
            var areaName = areaDid != 0 ? StringInfo(p, areaDid, Prop_Area_Name) : null;
            d.QuestNameRaw = questName; d.AreaNameRaw = areaName;

            string? charName = null;
            try { charName = p.GetCurrentCharacterName(); } catch { }

            IEntity? me = null;
            try { me = p.GetCurrentCharacter(); } catch { }
            int? level = ReadInt(me, Prop_Character_TotalLevel);
            d.LevelRaw = level;

            string? className = null;
            try
            {
                var social = p.GetPlayerSocialEntity();
                if (social != null && social.Valid)
                {
                    var classes = (social.Classes ?? new List<IClassInfo>())
                        .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Name))
                        .OrderByDescending(c => c.Levels ?? 0).ToList();
                    d.ClassesRaw = string.Join("/", classes.Select(c => $"{c.Name} {c.Levels}"));
                    if (classes.Count > 0) className = string.Join("/", classes.Take(3).Select(c => c.Name.Trim()));
                    if (!level.HasValue && social.Level > 0 && social.Level <= 40) level = (int)social.Level;
                }
            }
            catch { }

            int? party = null;
            try
            {
                var members = p.GetPartyMembers();
                if (members != null)
                {
                    var ids = new HashSet<ulong>(members.Where(m => m != null && m.Id != 0).Select(m => m.Id));
                    d.PartyListCount = ids.Count;
                    ids.Add(charId);
                    party = ids.Count;
                }
            }
            catch { }

            var (difficulty, skulls) = ReadDifficulty(p, me, d);

            var snap = new GameSnapshot
            {
                LoggedIn = true,
                InTown = inTown,
                InstanceQuestDid = instanceQuest,
                QuestName = questName,
                AreaDid = areaDid,
                AreaName = areaName,
                CharacterName = charName,
                Level = level,
                ClassName = className,
                PartySize = party,
                Difficulty = difficulty,
                ReaperSkulls = skulls,
            };
            return (snap, d);
        }

        /// <summary>
        /// [HYP] The instance difficulty. VolumeProperties ("properties that describe the instance / world settings around
        /// the current character", new in SDK 4.3) is read first, then the character's own properties. A value is used
        /// only when it maps to a known VoK.Sdk.Ddo.Enums.DifficultyType; otherwise the difficulty is left out.
        /// </summary>
        private static (Difficulty, int?) ReadDifficulty(IDdoGameDataProvider p, IEntity? me, GameDiagnostics d)
        {
            IPropertyCollection? vol = null;
            try { vol = p.VolumeProperties; } catch { }
            var sources = new List<(string Name, IPropertyCollection? Coll)> { ("volume", vol) };
            try { sources.Add(("character", me?.PropertyCollection)); } catch { }
            foreach (var (name, coll) in sources)
            {
                if (coll == null) continue;
                uint? raw = null;
                foreach (var id in new[] { Prop_Dungeon_DifficultyLevel, Prop_CachedDungeonDifficulty, Prop_Dungeon_CachedDifficulty })
                {
                    raw = ReadUInt(coll, id);
                    if (raw.HasValue) break;
                }
                if (!raw.HasValue) continue;
                var diff = MapDifficulty(raw.Value);
                d.DifficultySource = name; d.DifficultyRaw = raw;
                int? skulls = null;
                foreach (var id in new[] { Prop_Dungeon_ReaperLevel, Prop_Dungeon_CachedReaperLevel })
                {
                    var r = ReadIntFrom(coll, id);
                    if (r.HasValue) { skulls = r; break; }
                }
                d.ReaperRaw = skulls;
                if (diff == Difficulty.Unknown) continue;
                return (diff, diff == Difficulty.Reaper && skulls is int s && s >= 1 && s <= 10 ? s : (int?)null);
            }
            return (Difficulty.Unknown, null);
        }

        /// <summary>VoK.Sdk.Ddo.Enums.DifficultyType value to the displayed difficulty (Epic variants fold in).</summary>
        public static Difficulty MapDifficulty(uint raw) => raw switch
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

        /// <summary>A 0x70 definition's StringInfo text through the client's property master (Hireling Commander's QuestName technique).</summary>
        internal static string? StringInfo(IGameDataProvider p, uint did, uint propertyId)
        {
            try
            {
                var master = p.PropertyMaster;
                var coll = master?.GetPropertyCollection(did);
                if (coll?.Properties == null) return null;
                if (!coll.Properties.TryGetValue(propertyId, out var prop) || !(prop is IStringInfoProperty si)) return null;
                var text = si.Text;
                if (string.IsNullOrEmpty(text)) { try { text = si.GetText(master, null, null); } catch { } }
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch { return null; }
        }

        private static int? ReadInt(IEntity? e, uint id)
        {
            if (e == null) return null;
            try
            {
                var v = ReadIntFrom(e.PropertyCollection, id);
                return v ?? ReadIntFrom(e.WeeniePropertyCollection, id);
            }
            catch { return null; }
        }

        private static int? ReadIntFrom(IPropertyCollection? c, uint id)
        {
            if (c == null) return null;
            try
            {
                var v = c.GetInt32PropertyValue(id);
                if (v.HasValue) return v;
                var u = c.GetUInt32PropertyValue(id);
                if (u.HasValue && u.Value <= int.MaxValue) return (int)u.Value;
            }
            catch { }
            return null;
        }

        private static uint? ReadUInt(IPropertyCollection c, uint id)
        {
            try
            {
                var u = c.GetUInt32PropertyValue(id);
                if (u.HasValue) return u;
                var e = c.GetEnumProperty(id);
                if (e != null) return e.UInt32Value;
                var i = c.GetInt32PropertyValue(id);
                if (i.HasValue && i.Value >= 0) return (uint)i.Value;
            }
            catch { }
            return null;
        }
    }
}

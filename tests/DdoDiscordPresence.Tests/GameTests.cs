using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DdoDiscordPresence.Game;
using DdoDiscordPresence.Presence;

namespace DdoDiscordPresence.Tests
{
    internal static class GameReaderTests
    {
        [Test]
        public static void LoggedOutReadsAsLoggedOut()
        {
            var g = new FakeGame { CharacterId = 0 };
            var (snap, _) = GameReader.Read(g.Provider());
            Assert.True(!snap.LoggedIn, "logged out");
            Assert.True(!GameReader.Read(null).Snapshot.LoggedIn, "no provider");
        }

        [Test]
        public static void QuestIsNamedFromTheInstanceNotTheJournal()
        {
            var g = new FakeGame { InTown = false, InstanceQuestDid = 0x70000001, JournalQuestDid = 0x70000002, AreaDid = 0x70000010, Level = 9 };
            g.QuestNames[0x70000001] = "The Sharn Syndicate";
            g.QuestNames[0x70000002] = "Stale Journal Quest";
            g.AreaNames[0x70000010] = "Sharn Syndicate Hideout";
            var (snap, diag) = GameReader.Read(g.Provider());
            Assert.True(snap.InQuest, "in quest");
            Assert.Equal("The Sharn Syndicate", snap.QuestName, "instance quest name");
            Assert.Equal("Sharn Syndicate Hideout", snap.AreaName, "area name");
            Assert.Equal(9, snap.Level, "level");
            Assert.Equal(0x70000002u, diag.JournalQuestDid, "journal quest only in diagnostics");
        }

        [Test]
        public static void TownWithAStaleInstanceIsNotAQuest()
        {
            var g = new FakeGame { InTown = true, InstanceQuestDid = 0x70000001, AreaDid = 0x70000020 };
            g.AreaNames[0x70000020] = "The Harbor";
            var (snap, _) = GameReader.Read(g.Provider());
            Assert.True(!snap.InQuest, "town");
            Assert.Equal("The Harbor", snap.AreaName, "area");
        }

        [Test]
        public static void DifficultyFromVolumePropertiesWhenKnown()
        {
            var g = new FakeGame { InTown = false, InstanceQuestDid = 1, VolumeDifficulty = 11, VolumeReaper = 4 };
            var (snap, diag) = GameReader.Read(g.Provider());
            Assert.Equal(Difficulty.Reaper, snap.Difficulty, "reaper");
            Assert.Equal(4, snap.ReaperSkulls, "skulls");
            Assert.Equal("reaper level", diag.DifficultySource, "source");
            g.VolumeDifficulty = 204;   // "Blank": not a quest difficulty
            Assert.Equal(Difficulty.Reaper, GameReader.Read(g.Provider()).Snapshot.Difficulty, "a reaper level decides on its own");
            g.VolumeReaper = 0;
            Assert.Equal(Difficulty.Unknown, GameReader.Read(g.Provider()).Snapshot.Difficulty, "unknown values are left out");
            g.VolumeDifficulty = null;
            Assert.Equal(Difficulty.Unknown, GameReader.Read(g.Provider()).Snapshot.Difficulty, "absent");
        }

        [Test]
        public static void DifficultyTypeMapping()
        {
            Assert.Equal(Difficulty.Casual, GameReader.MapDifficulty(7), "Casual");
            Assert.Equal(Difficulty.Normal, GameReader.MapDifficulty(1), "Normal");
            Assert.Equal(Difficulty.Hard, GameReader.MapDifficulty(9), "Epic_Hard");
            Assert.Equal(Difficulty.Elite, GameReader.MapDifficulty(3), "Elite");
            Assert.Equal(Difficulty.Reaper, GameReader.MapDifficulty(12), "Epic_Reaper");
            Assert.Equal(Difficulty.Unknown, GameReader.MapDifficulty(4), "EliteFinished");
            Assert.Equal(Difficulty.Unknown, GameReader.MapDifficulty(uint.MaxValue), "Invalid");
            // Seen live 2026-11-11 in a Reaper 1 quest: cached difficulty names Dungeon_EasyDifficulty, reaper level 1.
            Assert.Equal((Difficulty.Reaper, (int?)1), DifficultyMap.Resolve(268442566u, null, 1), "reaper level decides");
            Assert.Equal((Difficulty.Normal, (int?)null), DifficultyMap.Resolve(268442566u, null, 0), "Easy is Normal");
            Assert.Equal((Difficulty.Hard, (int?)null), DifficultyMap.Resolve(268442564u, null, null), "Medium is Hard");
            Assert.Equal((Difficulty.Elite, (int?)null), DifficultyMap.Resolve(268442567u, null, null), "Hard is Elite");
            Assert.Equal((Difficulty.Elite, (int?)null), DifficultyMap.Resolve(268460946u, null, 0), "Epic Elite");
            Assert.Equal((Difficulty.Casual, (int?)null), DifficultyMap.Resolve(268454990u, null, null), "Casual");
            Assert.Equal((Difficulty.Reaper, (int?)null), DifficultyMap.Resolve(268477892u, null, null), "Reaper, skulls unknown");
            Assert.Equal((Difficulty.Reaper, (int?)null), DifficultyMap.Resolve(268477892u, null, 11), "skulls out of range are left out");
            Assert.Equal((Difficulty.Hard, (int?)null), DifficultyMap.Resolve(null, 2, null), "falls back to the DifficultyType number");
            Assert.Equal((Difficulty.Unknown, (int?)null), DifficultyMap.Resolve(12345u, null, null), "unknown stays unknown");
            // Seen live 2026-11-11, Heroic Elite: a wolf whose record id is its Dungeon_HardDifficulty row.
            var wolf = new System.Collections.Generic.Dictionary<uint, uint> { [268453728u] = 1879242525, [268460946u] = 1879242527, [268460947u] = 1879242530, [268460948u] = 1879242525,
                [268454990u] = 1879242534, [268442564u] = 1879242539, [268442566u] = 1879242534, [268442567u] = 1879242529, [268446866u] = 1879050194 };
            (uint, System.Collections.Generic.IReadOnlyDictionary<uint, uint>) M(uint id) => (id, wolf);
            Assert.Equal((Difficulty.Elite, true), DifficultyMap.FromMonsterVariants(new[] { M(1879242529) }), "Hard row is Elite");
            Assert.Equal((Difficulty.Hard, true), DifficultyMap.FromMonsterVariants(new[] { M(1879242539) }), "Medium row is Hard");
            Assert.Equal((Difficulty.Elite, true), DifficultyMap.FromMonsterVariants(new[] { M(1879242527) }), "Epic Elite row");
            Assert.Equal((Difficulty.Normal, false), DifficultyMap.FromMonsterVariants(new[] { M(1879242534) }), "Casual and Normal share a record: Normal, uncertain");
            Assert.Equal((Difficulty.Unknown, false), DifficultyMap.FromMonsterVariants(new[] { M(42) }), "no row matches");
            Assert.Equal((Difficulty.Elite, true), DifficultyMap.FromMonsterVariants(new[] { M(1879242534), M(1879242529), M(1879242529) }), "certain votes win");
            Assert.Equal((Difficulty.Reaper, (int?)1), DifficultyMap.Decide(null, null, 1, Difficulty.Elite, true), "reaper level first");
            Assert.Equal((Difficulty.Elite, (int?)null), DifficultyMap.Decide(null, null, 0, Difficulty.Elite, true), "monsters with no entry panel");
            Assert.Equal((Difficulty.Elite, (int?)null), DifficultyMap.Decide(268442566u, null, 0, Difficulty.Elite, true), "certain monsters beat the panel");
            Assert.Equal((Difficulty.Casual, (int?)null), DifficultyMap.Decide(268454990u, null, 0, Difficulty.Normal, false), "the panel settles Casual against an uncertain Normal");
            Assert.Equal((Difficulty.Normal, (int?)null), DifficultyMap.Decide(null, null, 0, Difficulty.Normal, false), "uncertain monsters when nothing else");
            Assert.Equal((Difficulty.Unknown, (int?)null), DifficultyMap.Decide(null, null, null, Difficulty.Unknown, false), "nothing known: nothing shown");
            Assert.Equal((bool?)false, DifficultyMap.EpicSideFromMonsters(new[] { M(1879242529) }), "a heroic row: heroic side");
            Assert.Equal((bool?)true, DifficultyMap.EpicSideFromMonsters(new[] { M(1879242527) }), "an epic row: epic side");
            Assert.Equal((bool?)null, DifficultyMap.EpicSideFromMonsters(new[] { M(42) }), "no row: unknown");
            Assert.Equal((bool?)false, DifficultyMap.IsEpicProperty(0x10001BC7u), "the entry panel value seen for Heroic Elite");
            Assert.Equal((bool?)true, DifficultyMap.IsEpicProperty(268460946u), "Epic Elite");
            var gs = new GameSnapshot { EpicSide = false, QuestLevelHeroic = 10, QuestLevelEpic = 31 };
            Assert.Equal(("Heroic", (int?)10), PresenceFormatter.TierAndLevel(gs), "heroic side and its level");
            Assert.Equal(("Legendary", (int?)31), PresenceFormatter.TierAndLevel(gs with { EpicSide = true }), "31 is Legendary");
            Assert.Equal(("Epic", (int?)24), PresenceFormatter.TierAndLevel(gs with { EpicSide = true, QuestLevelEpic = 24 }), "24 is Epic");
            Assert.Equal(((string?)null, (int?)null), PresenceFormatter.TierAndLevel(gs with { EpicSide = null }), "side unknown: nothing added");
            Assert.Equal("Heroic Elite (L10)", PresenceFormatter.TierDifficultyText("Heroic", "Elite", 10, false), "short");
            Assert.Equal("Heroic Elite, quest level 10", PresenceFormatter.TierDifficultyText("Heroic", "Elite", 10, true), "long");
            Assert.Equal("Legendary Reaper 1 (L31)", PresenceFormatter.TierDifficultyText("Legendary", "Reaper 1", 31, false), "reaper");
            Assert.Equal("Elite", PresenceFormatter.TierDifficultyText(null, "Elite", null, false), "difficulty alone, as before");
            Assert.Equal("Heroic (L10)", PresenceFormatter.TierDifficultyText("Heroic", null, 10, false), "tier without a difficulty");
            Assert.Equal((string?)null, PresenceFormatter.TierDifficultyText(null, null, 10, false), "a level alone is not shown");
        }

        [Test]
        public static void PartyCountsMeOnce()
        {
            var g = new FakeGame { PartyOthers = 3 };
            Assert.Equal(4, GameReader.Read(g.Provider()).Snapshot.PartySize, "3 others + me (listed or not)");
            g.PartyOthers = 0;
            Assert.Equal(1, GameReader.Read(g.Provider()).Snapshot.PartySize, "solo");
        }

        [Test]
        public static void AThrowingClientNeverThrows()
        {
            var g = new FakeGame { ThrowEverywhere = true };
            var (snap, _) = GameReader.Read(g.Provider());
            Assert.True(!snap.LoggedIn, "unreadable = logged out, no exception");
        }
    }

    internal static class PluginTests
    {
        private static string TempPluginFolder()
        {
            var d = Path.Combine(Path.GetTempPath(), "ddp-tests", Guid.NewGuid().ToString("N"), "DdoDiscordPresence", "win-x64");
            Directory.CreateDirectory(d);
            return d;
        }

        [Test]
        public static void StorageFolderIsTheParentOfWinX64()
        {
            var d = TempPluginFolder();
            Assert.Equal(Path.GetDirectoryName(d), Plugin.ResolveStorageFolder(d), "parent of win-x64");
        }

        [Test]
        public static async Task InitializeOpenFormAndLogoutWithoutTouchingSettings()
        {
            var folder = TempPluginFolder();
            var cfgPath = Path.Combine(Path.GetDirectoryName(folder)!, ConfigStore.FileName);
            // An ID but sharing OFF: the test must never reach the real Discord.
            File.WriteAllText(cfgPath, "{ \"ApplicationId\": \"123456789012345678\", \"Enabled\": false, \"HiddenPlaces\": [\"Vale\"] }");
            var before = File.ReadAllBytes(cfgPath);
            var stamp = File.GetLastWriteTimeUtc(cfgPath);

            var g = new FakeGame { InTown = true, AreaDid = 0x70000020 };
            g.AreaNames[0x70000020] = "The Harbor";
            var plugin = new Plugin();
            plugin.Initialize(g.Provider(), folder);
            var ui = plugin.GetPluginUI()!;
            Assert.True(ui.ToolbarImage.Width == 36 && ui.ToolbarImage.Height == 36, "36x36 toolbar icon");
            var form = (System.Windows.Forms.Form)ui.UserInterfaceForm;
            Assert.True(form.FormBorderStyle == System.Windows.Forms.FormBorderStyle.None && !form.ControlBox, "borderless for the host");
            form.CreateControl();
            using (var bmp = new System.Drawing.Bitmap(form.Width, form.Height)) form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
            await Assert.Eventually(() => plugin.Snapshot.LoggedIn, "first poll ran");
            Assert.Equal(null, plugin.Result.Activity, "sharing off: nothing to show");
            Assert.True(plugin.StatusLine().Text.StartsWith("Off"), plugin.StatusLine().Text);
            Assert.True(before.SequenceEqual(File.ReadAllBytes(cfgPath)) && stamp == File.GetLastWriteTimeUtc(cfgPath), "opening the panel did not write the settings");

            // the owner turns sharing on with the form: now there is something to show (still no Discord: it is paused)
            plugin.UpdateConfig(c => { c.Enabled = true; c.Paused = true; });
            Assert.True(plugin.StatusLine().Text.StartsWith("Paused"), plugin.StatusLine().Text);
            plugin.UpdateConfig(c => { c.Enabled = false; c.Paused = false; });

            await g.RaiseAsync("OnLogout");
            Assert.True(!plugin.Snapshot.LoggedIn, "logged out");
            plugin.Terminate();
        }

        [Test]
        public static async Task QuestPresenceFromTheGameEndToEnd()
        {
            var folder = TempPluginFolder();
            var cfgPath = Path.Combine(Path.GetDirectoryName(folder)!, ConfigStore.FileName);
            // Paused so the service never sends; the formatted result is still computed for the preview only when not paused,
            // so this test reads the formatter through Recompute with sharing on but no Discord pipe of the test's own.
            File.WriteAllText(cfgPath, "{ \"ApplicationId\": \"123456789012345678\", \"Enabled\": false }");
            var g = new FakeGame { InTown = false, InstanceQuestDid = 0x70000001, AreaDid = 0x70000010, Level = 20, PartyOthers = 2, VolumeDifficulty = 3 };
            g.QuestNames[0x70000001] = "The Pit";
            var plugin = new Plugin();
            plugin.Initialize(g.Provider(), folder);
            await Assert.Eventually(() => plugin.Snapshot.InQuest, "quest seen");
            var result = PresenceFormatter.Format(plugin.Snapshot, new PresenceConfig { Enabled = true, FirstRunAnswered = true }, DateTime.UtcNow);
            Assert.Equal("The Pit", result.Activity!.Details, "details");
            Assert.Equal("Level 20 · Elite · Party of 3", result.Activity!.State, "state");
            plugin.Terminate();
        }
    }
}

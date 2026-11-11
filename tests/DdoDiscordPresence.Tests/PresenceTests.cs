using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DdoDiscordPresence.Presence;

namespace DdoDiscordPresence.Tests
{
    internal static class FormatterTests
    {
        private static readonly DateTime Now = new DateTime(2026, 3, 1, 20, 0, 0, DateTimeKind.Utc);

        private static PresenceConfig Cfg(Action<PresenceConfig>? change = null)
        {
            var c = new PresenceConfig { Enabled = true, FirstRunAnswered = true, ShowDifficultyBadge = false };
            change?.Invoke(c);
            return c;
        }

        private static GameSnapshot Quest() => new GameSnapshot
        {
            LoggedIn = true, InTown = false, InstanceQuestDid = 0x70012345, QuestName = "The Sharn Syndicate",
            AreaDid = 0x70054321, AreaName = "Sharn Syndicate Hideout", CharacterName = "Walkeng", Level = 12, ClassName = "Paladin",
            PartySize = 4, Difficulty = Difficulty.Elite, PlaceSinceUtc = Now.AddMinutes(-7),
        };

        private static GameSnapshot Town() => new GameSnapshot
        {
            LoggedIn = true, InTown = true, InstanceQuestDid = 0, AreaDid = 0x70011111, AreaName = "The Harbor",
            CharacterName = "Walkeng", Level = 12, ClassName = "Paladin", PartySize = 1, PlaceSinceUtc = Now.AddMinutes(-2),
        };

        [Test]
        public static void QuestShowsQuestNameAndCompactState()
        {
            var r = PresenceFormatter.Format(Quest(), Cfg(), Now);
            var a = r.Activity!;
            Assert.Equal("The Sharn Syndicate", a.Details, "details");
            Assert.Equal("Level 12 Paladin · Elite · Party of 4", a.State, "state");
            Assert.Equal(new DateTimeOffset(Now.AddMinutes(-7)).ToUnixTimeSeconds(), a.StartUnixSeconds, "start");
            Assert.Equal("ddo", a.LargeImageKey, "large image");
            Assert.Equal("Dungeons & Dragons Online", a.LargeText, "large text");
            Assert.Equal(null, a.SmallImageKey, "badge off by default");
        }

        [Test]
        public static void TownShowsTheAreaNameAndNoDifficulty()
        {
            var a = PresenceFormatter.Format(Town() with { Difficulty = Difficulty.Elite }, Cfg(), Now).Activity!;
            Assert.Equal("The Harbor", a.Details, "details");
            Assert.Equal("Level 12 Paladin", a.State, "solo: no party, no difficulty outside a quest");
        }

        [Test]
        public static void StaleInstanceQuestInTownIsNotAQuest()
        {
            // Live evidence: in town the client can still report the last instance's quest DID.
            var a = PresenceFormatter.Format(Town() with { InstanceQuestDid = 0x70099999, QuestName = "Old Quest" }, Cfg(), Now).Activity!;
            Assert.Equal("The Harbor", a.Details, "town name, not the stale quest");
        }

        [Test]
        public static void ReaperShowsSkulls()
        {
            var a = PresenceFormatter.Format(Quest() with { Difficulty = Difficulty.Reaper, ReaperSkulls = 3 }, Cfg(), Now).Activity!;
            Assert.True(a.State!.Contains(" · Reaper 3 · "), a.State);
            a = PresenceFormatter.Format(Quest() with { Difficulty = Difficulty.Reaper, ReaperSkulls = 99 }, Cfg(), Now).Activity!;
            Assert.True(a.State!.Contains(" · Reaper · "), "implausible skulls are dropped: " + a.State);
        }

        [Test]
        public static void EveryPrivacyToggle()
        {
            var q = Quest();
            Assert.Equal("In a quest", PresenceFormatter.Format(q, Cfg(c => c.ShowQuestName = false), Now).Activity!.Details, "quest name off");
            Assert.Equal("In town", PresenceFormatter.Format(Town(), Cfg(c => c.ShowAreaNames = false), Now).Activity!.Details, "area names off");
            Assert.Equal("In a public area", PresenceFormatter.Format(Town() with { InTown = null }, Cfg(c => c.ShowAreaNames = false), Now).Activity!.Details, "area off, not a town");
            Assert.Equal("Level 12 Paladin · Party of 4", PresenceFormatter.Format(q, Cfg(c => c.ShowDifficulty = false), Now).Activity!.State, "difficulty off");
            Assert.Equal("Elite · Party of 4", PresenceFormatter.Format(q, Cfg(c => c.ShowLevelClass = false), Now).Activity!.State, "level/class off");
            Assert.Equal("Level 12 Paladin · Elite", PresenceFormatter.Format(q, Cfg(c => c.ShowPartySize = false), Now).Activity!.State, "party off");
            Assert.Equal(null, PresenceFormatter.Format(q, Cfg(c => c.ShowElapsed = false), Now).Activity!.StartUnixSeconds, "elapsed off");
            Assert.True(!PresenceFormatter.Format(q, Cfg(), Now).Activity!.State!.Contains("Walkeng"), "the name is OFF by default");
            Assert.Equal("Walkeng · Level 12 Paladin · Elite · Party of 4", PresenceFormatter.Format(q, Cfg(c => c.ShowCharacterName = true), Now).Activity!.State, "name on");
            var none = PresenceFormatter.Format(q, Cfg(c => { c.ShowDifficulty = false; c.ShowLevelClass = false; c.ShowPartySize = false; }), Now).Activity!;
            Assert.Equal(null, none.State, "no state parts: the state field is left out, never empty");
            Assert.True(!none.ToJson().ContainsKey("state"), "no state key in the JSON");
        }

        [Test]
        public static void QuestNameOffStillHidesNothingFromTheHideList()
        {
            var r = PresenceFormatter.Format(Quest(), Cfg(c => { c.ShowQuestName = false; c.HiddenPlaces.Add("syndicate"); }), Now);
            Assert.Equal(null, r.Activity, "hidden even when the name is not shown");
            Assert.True(r.Reason.Contains("syndicate"), r.Reason);
        }

        [Test]
        public static void HideListMatchesSubstringsOfQuestOrArea()
        {
            Assert.Equal(null, PresenceFormatter.Format(Quest(), Cfg(c => c.HiddenPlaces.Add("SHARN")), Now).Activity, "quest name, any case");
            Assert.Equal(null, PresenceFormatter.Format(Quest(), Cfg(c => c.HiddenPlaces.Add("hideout")), Now).Activity, "area name");
            Assert.Equal(null, PresenceFormatter.Format(Town(), Cfg(c => c.HiddenPlaces.Add("harbor")), Now).Activity, "town");
            Assert.True(PresenceFormatter.Format(Town(), Cfg(c => c.HiddenPlaces.Add("Marketplace")), Now).Activity != null, "no match");
            Assert.True(PresenceFormatter.Format(Town(), Cfg(c => c.HiddenPlaces.AddRange(new[] { "", "  " })), Now).Activity != null, "blank entries match nothing");
        }

        [Test]
        public static void NothingIsSentWithoutIdWhenOffPausedOrLoggedOut()
        {
            Assert.Equal(null, PresenceFormatter.Format(Quest(), new PresenceConfig(), Now).Activity, "no id");
            Assert.True(PresenceFormatter.Format(Quest(), Cfg(c => c.ApplicationId = "12345"), Now).Activity != null, "an invalid override falls back to the built-in id");
            Assert.Equal(null, PresenceFormatter.Format(Quest(), PresenceConfig.NewInstall(), Now).Activity, "a new install shares nothing before the first-run answer");
            Assert.Equal(null, PresenceFormatter.Format(Quest(), Cfg(c => c.Enabled = false), Now).Activity, "off");
            Assert.Equal(null, PresenceFormatter.Format(Quest(), Cfg(c => c.Paused = true), Now).Activity, "paused");
            Assert.Equal(null, PresenceFormatter.Format(GameSnapshot.LoggedOut, Cfg(), Now).Activity, "logged out");
        }

        [Test]
        public static void UnreadableNamesSendNothingNotGarbage()
        {
            Assert.Equal(null, PresenceFormatter.Format(Town() with { AreaName = null }, Cfg(), Now).Activity, "no area name");
            Assert.Equal(null, PresenceFormatter.Format(Town() with { AreaName = "0x70011111" }, Cfg(), Now).Activity, "hex DID");
            Assert.Equal(null, PresenceFormatter.Format(Town() with { AreaName = "70011111" }, Cfg(), Now).Activity, "bare hex");
            Assert.Equal(null, PresenceFormatter.Format(Town() with { AreaName = "1879238693" }, Cfg(), Now).Activity, "digits");
            Assert.Equal(null, PresenceFormatter.Format(Town() with { AreaName = "  \t " }, Cfg(), Now).Activity, "blank");
            Assert.Equal(null, PresenceFormatter.Format(Town() with { AreaName = "${area}" }, Cfg(), Now).Activity, "placeholder");
            Assert.Equal(null, PresenceFormatter.Format(Town() with { AreaName = "X" }, Cfg(), Now).Activity, "one letter");
            // a quest without a readable name falls back to its area (usually the dungeon's name), else nothing
            Assert.Equal("Sharn Syndicate Hideout", PresenceFormatter.Format(Quest() with { QuestName = "0x7001ABCD" }, Cfg(), Now).Activity!.Details, "fallback to area");
            Assert.Equal(null, PresenceFormatter.Format(Quest() with { QuestName = null, AreaName = null }, Cfg(), Now).Activity, "nothing readable");
            // unreadable class or name parts are dropped, not sent
            var a = PresenceFormatter.Format(Quest() with { ClassName = "0x00ABCDEF", CharacterName = "" }, Cfg(c => c.ShowCharacterName = true), Now).Activity!;
            Assert.Equal("Level 12 · Elite · Party of 4", a.State, "garbage parts dropped");
        }

        [Test]
        public static void TextIsCleanedOfControlsAndMarkup()
        {
            var a = PresenceFormatter.Format(Town() with { AreaName = "  The\tHarbor\u0007 <rgb=#ff0000>  ​" }, Cfg(), Now).Activity!;
            Assert.Equal("The Harbor", a.Details, "cleaned");
            Assert.True(a.Details.All(ch => !char.IsControl(ch)), "no control characters");
        }

        [Test]
        public static void LengthRulesAreEnforced()
        {
            var longName = string.Concat(Enumerable.Repeat("Abcdefghij ", 20));
            var a = PresenceFormatter.Format(Town() with { AreaName = longName }, Cfg(), Now).Activity!;
            Assert.True(a.Details.Length <= 128 && a.Details.Length >= 2, "details length " + a.Details.Length);
            Assert.True(a.Details.EndsWith("…"), "ellipsis");
            var emoji = new string('a', 126) + "\U0001F600\U0001F600";
            var fit = PresenceFormatter.Fit(emoji)!;
            Assert.True(fit.Length <= 128, "fit length");
            Assert.True(!char.IsHighSurrogate(fit[fit.Length - 2]) || char.IsLowSurrogate(fit[fit.Length - 1]), "no split surrogate");
            foreach (var ch in fit) if (char.IsHighSurrogate(ch)) Assert.True(fit.IndexOf(ch) + 1 < fit.Length && char.IsLowSurrogate(fit[fit.IndexOf(ch) + 1]), "pair kept whole");
            Assert.Equal(null, PresenceFormatter.Fit("A"), "one character is never sent");
            Assert.Equal(null, PresenceFormatter.Fit(" "), "empty is never sent");
            Assert.Equal("Ab", PresenceFormatter.Fit(" Ab "), "two characters, trimmed");
            var longState = PresenceFormatter.Format(Quest() with { ClassName = string.Concat(Enumerable.Repeat("Paladin/", 30)) + "Rogue" }, Cfg(), Now).Activity!;
            Assert.True(longState.State!.Length <= 128, "state cut to 128");
        }

        [Test]
        public static void DifficultyBadgeUsesTheStableKeys()
        {
            var a = PresenceFormatter.Format(Quest(), Cfg(c => c.ShowDifficultyBadge = true), Now).Activity!;
            Assert.Equal("elite", a.SmallImageKey, "badge key");
            Assert.Equal("Elite", a.SmallText, "badge text");
            a = PresenceFormatter.Format(Quest() with { Difficulty = Difficulty.Reaper, ReaperSkulls = 5 }, Cfg(c => c.ShowDifficultyBadge = true), Now).Activity!;
            Assert.Equal("reaper", a.SmallImageKey, "reaper badge");
            Assert.Equal("Reaper 5", a.SmallText, "reaper text");
            a = PresenceFormatter.Format(Quest() with { Difficulty = Difficulty.Unknown }, Cfg(c => c.ShowDifficultyBadge = true), Now).Activity!;
            Assert.Equal(null, a.SmallImageKey, "unknown difficulty: no badge");
            a = PresenceFormatter.Format(Quest(), Cfg(c => { c.ShowDifficultyBadge = true; c.ShowDifficulty = false; }), Now).Activity!;
            Assert.Equal(null, a.SmallImageKey, "difficulty off: no badge either");
            Assert.True(PresenceFormatter.BadgeKeys.SequenceEqual(new[] { "casual", "normal", "hard", "elite", "reaper" }), "README keys");
        }

        [Test]
        public static void JsonShapeMatchesDiscord()
        {
            var a = PresenceFormatter.Format(Quest(), Cfg(c => c.ShowDifficultyBadge = true), Now).Activity!;
            var json = a.ToJson().ToJsonString();
            Assert.True(json.Contains("\"details\":\"The Sharn Syndicate\""), json);
            Assert.True(json.Contains("\"timestamps\":{\"start\":"), json);
            Assert.True(json.Contains("\"large_image\":\"ddo\"") && json.Contains("\"small_image\":\"elite\""), json);
        }

        [Test]
        public static void InvalidLargeImageKeyFallsBackToDdo()
        {
            var a = PresenceFormatter.Format(Quest(), Cfg(c => c.LargeImageKey = "Bad Key!"), Now).Activity!;
            Assert.Equal("ddo", a.LargeImageKey, "fallback");
        }

        [Test]
        public static void PlaceTrackerTimerResetsOnPlaceChangeOnly()
        {
            var t = new PlaceTracker();
            var t0 = Now;
            Assert.Equal(t0, t.Update("a:1", t0, null), "first place");
            Assert.Equal(t0, t.Update("a:1", t0.AddMinutes(5), null), "same place keeps its start");
            var change = t0.AddMinutes(6);
            Assert.Equal(change, t.Update("q:7", change.AddSeconds(3), change), "new place starts at the area-change event");
            Assert.Equal(change, t.Update("q:7", change.AddMinutes(10), change.AddMinutes(4)), "area change inside the quest keeps the quest's start");
            var late = t0.AddHours(1);
            Assert.Equal(late, t.Update("a:2", late, change), "an old event is not used");
            var q = Quest();
            Assert.Equal(q.PlaceKey, (q with { AreaDid = 5 }).PlaceKey, "quest key ignores the area");
            Assert.True(Town().PlaceKey != (Town() with { AreaDid = 9 }).PlaceKey, "outside quests the area is the place");
        }
    }

    internal static class ConfigTests
    {
        private static string TempFolder()
        {
            var d = Path.Combine(Path.GetTempPath(), "ddp-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        [Test]
        public static void FirstRunIsOffAndAsks()
        {
            var store = new ConfigStore(Path.Combine(TempFolder(), ConfigStore.FileName));
            var (c, warn) = store.Load();
            Assert.Equal(null, warn, "no warning");
            Assert.True(!c.Enabled && !c.SharingEnabled && c.NeedsFirstRunAnswer, "a new install is off and asks");
            Assert.Equal(PresenceConfig.BuiltInApplicationId, c.EffectiveApplicationId, "built-in application id");
            Assert.True(!c.ShowCharacterName && c.ShowQuestName && c.ShowAreaNames && c.ShowLevelClass && c.ShowPartySize && c.ShowDifficulty && c.ShowElapsed && !c.Paused, "default toggles");
            Assert.Equal("ddo", c.LargeImageKey, "default key");
            Assert.True(!File.Exists(store.Path), "loading does not write");
            c.FirstRunAnswered = true; c.Enabled = true;
            store.Save(c);
            var (back, _) = store.Load();
            Assert.True(back.SharingEnabled && !back.NeedsFirstRunAnswer, "the answer is kept");
        }

        [Test]
        public static void OwnersExistingFileStaysOnAndLosesTheBuiltInId()
        {
            var path = Path.Combine(TempFolder(), ConfigStore.FileName);
            // the 0.1.0 file as an existing install has it
            File.WriteAllText(path, "{ \"SchemaVersion\": 1, \"ApplicationId\": \"" + PresenceConfig.BuiltInApplicationId + "\", \"Enabled\": true, \"Paused\": false, " +
                "\"ShowQuestName\": true, \"ShowAreaNames\": true, \"ShowLevelClass\": true, \"ShowCharacterName\": false, \"ShowPartySize\": true, " +
                "\"ShowDifficulty\": true, \"ShowDifficultyBadge\": true, \"ShowElapsed\": true, \"HiddenPlaces\": [], \"LargeImageKey\": \"ddo\" }");
            var store = new ConfigStore(path);
            var (c, warn) = store.Load();
            Assert.Equal(null, warn, "no warning");
            Assert.True(c.SharingEnabled && !c.NeedsFirstRunAnswer && c.ShowDifficultyBadge, "still on, not asked again");
            Assert.Equal(PresenceConfig.BuiltInApplicationId, c.EffectiveApplicationId, "same id");
            store.Save(c);
            var text = File.ReadAllText(path);
            Assert.True(!text.Contains("ApplicationId") && !text.Contains("LargeImageKey"), "default override keys are not written back: " + text);
            Assert.True(text.Contains("\"FirstRunAnswered\": true") && text.Contains("\"Enabled\": true"), "answered and on");
        }

        [Test]
        public static void OverrideIsHonouredAndInvalidOverrideIgnored()
        {
            var path = Path.Combine(TempFolder(), ConfigStore.FileName);
            File.WriteAllText(path, "{ \"ApplicationId\": \"98765432109876543\", \"LargeImageKey\": \"other\", \"Enabled\": true, \"FirstRunAnswered\": true, \"SchemaVersion\": 2 }");
            var store = new ConfigStore(path);
            var (c, warn) = store.Load();
            Assert.Equal(null, warn, "valid override");
            Assert.Equal("98765432109876543", c.EffectiveApplicationId, "override used");
            Assert.Equal("other", c.LargeImageKey, "large key override");
            store.Save(c);
            var text = File.ReadAllText(path);
            Assert.True(text.Contains("98765432109876543") && text.Contains("\"other\""), "a non-default override survives a save");

            File.WriteAllText(path, "{ \"ApplicationId\": \"not-an-id\", \"Enabled\": true, \"FirstRunAnswered\": true, \"SchemaVersion\": 2 }");
            (c, warn) = new ConfigStore(path).Load();
            Assert.True(warn != null && warn.Contains("built-in"), "one note: " + warn);
            Assert.Equal(PresenceConfig.BuiltInApplicationId, c.EffectiveApplicationId, "falls back to the built-in id");
            Assert.True(c.SharingEnabled, "still shares");
        }

        [Test]
        public static void SaveLoadRoundTripIsAtomic()
        {
            var folder = TempFolder();
            var store = new ConfigStore(Path.Combine(folder, ConfigStore.FileName));
            var c = new PresenceConfig { Enabled = true, FirstRunAnswered = true, ShowCharacterName = true, HiddenPlaces = { "Sharn", "sharn", " Vale " } };
            store.Save(c);
            store.Save(c);            // overwrite an existing file
            var (back, warn) = store.Load();
            Assert.Equal(null, warn, "no warning");
            Assert.True(back.SharingEnabled && back.ShowCharacterName, "values kept");
            Assert.True(back.HiddenPlaces.SequenceEqual(new[] { "Sharn", "Vale" }), "list normalised: " + string.Join("|", back.HiddenPlaces));
            Assert.Equal(1, Directory.GetFiles(folder).Length, "no temp files left behind");
        }

        [Test]
        public static void ChangedNoticesAnotherClientsSave()
        {
            var path = Path.Combine(TempFolder(), ConfigStore.FileName);
            var a = new ConfigStore(path); var b = new ConfigStore(path);
            a.Save(new PresenceConfig { Enabled = true, FirstRunAnswered = true });
            b.Load();
            Assert.True(!a.Changed() && !b.Changed(), "both current");
            System.Threading.Thread.Sleep(20);
            b.Save(new PresenceConfig { Enabled = true, FirstRunAnswered = true, Paused = true, HiddenPlaces = { "Vale" } });
            Assert.True(a.Changed(), "a sees b's save");
            Assert.True(!b.Changed(), "b does not see its own save as a change");
            Assert.True(a.Load().Config.Paused, "a reloads b's toggle");
        }

        [Test]
        public static void CorruptFileNeverCrashesAndTurnsSharingOff()
        {
            var folder = TempFolder();
            var path = Path.Combine(folder, ConfigStore.FileName);
            File.WriteAllText(path, "{ \"Enabled\": tru");
            var (c, warn) = new ConfigStore(path).Load();
            Assert.True(warn != null && warn.Contains("sharing OFF"), "warning: " + warn);
            Assert.True(!c.Enabled && !c.SharingEnabled && c.NeedsFirstRunAnswer, "sharing is off and the question is asked again");
            Assert.True(File.Exists(path + ".corrupt"), "the broken file is kept");
            var (again, warn2) = new ConfigStore(path).Load();
            Assert.Equal(null, warn2, "the replacement is readable");
            Assert.True(!again.Enabled, "and still off");
        }

        [Test]
        public static void WrongTypesCountAsCorrupt()
        {
            var path = Path.Combine(TempFolder(), ConfigStore.FileName);
            File.WriteAllText(path, "{ \"Enabled\": \"yes\" }");
            var (c, warn) = new ConfigStore(path).Load();
            Assert.True(warn != null && !c.SharingEnabled, "a value of the wrong type does not enable sharing");
            File.WriteAllText(path, "[1,2,3]");
            (c, warn) = new ConfigStore(path).Load();
            Assert.True(warn != null && !c.SharingEnabled, "an array");
            File.WriteAllText(path, "null");
            (c, warn) = new ConfigStore(path).Load();
            Assert.True(warn != null && !c.SharingEnabled, "null");
        }

        [Test]
        public static void MissingAndNullFieldsGetSafeDefaults()
        {
            var path = Path.Combine(TempFolder(), ConfigStore.FileName);
            File.WriteAllText(path, "{ \"HiddenPlaces\": null, \"LargeImageKey\": null, \"ShowQuestName\": false }", new UTF8Encoding(true));
            var (c, warn) = new ConfigStore(path).Load();
            Assert.Equal(null, warn, "valid file");
            Assert.True(c.HiddenPlaces != null && c.HiddenPlaces.Count == 0, "null list repaired");
            Assert.Equal("ddo", c.LargeImageKey, "null key repaired");
            Assert.True(!c.ShowQuestName, "present field kept");
            Assert.True(!c.ShowCharacterName, "missing privacy field stays OFF");
            Assert.True(!c.SharingEnabled, "a file without Enabled does not share");
        }

        [Test]
        public static void ApplicationIdValidation()
        {
            Assert.True(PresenceConfig.IsValidApplicationId("12345678901234567"), "17 digits");
            Assert.True(PresenceConfig.IsValidApplicationId("12345678901234567890"), "20 digits");
            Assert.True(!PresenceConfig.IsValidApplicationId("1234567890123456"), "16 digits");
            Assert.True(!PresenceConfig.IsValidApplicationId("123456789012345678901"), "21 digits");
            Assert.True(!PresenceConfig.IsValidApplicationId("12345678901234567a"), "letters");
            Assert.True(!PresenceConfig.IsValidApplicationId(""), "empty");
            Assert.True(!PresenceConfig.IsValidApplicationId(null), "null");
        }
    }
}

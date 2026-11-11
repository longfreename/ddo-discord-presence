using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DdoDiscordPresence.Coordination;
using DdoDiscordPresence.Discord;
using DdoDiscordPresence.Presence;

namespace DdoDiscordPresence.Tests
{
    internal static class MultiFormatterTests
    {
        private const string D = " · ";
        private static readonly DateTime T0 = new DateTime(2026, 3, 1, 20, 0, 0, DateTimeKind.Utc);

        private static PresenceConfig Cfg(Action<PresenceConfig>? change = null)
        {
            var c = new PresenceConfig { Enabled = true, FirstRunAnswered = true, ShowCharacterName = true };
            change?.Invoke(c);
            return c;
        }

        internal static GameSnapshot InQuest(string name, uint quest = 0x70000001, string questName = "The Sharn Syndicate") => new GameSnapshot
        {
            LoggedIn = true, InTown = false, InstanceQuestDid = quest, QuestName = questName, AreaDid = 0x70000010, AreaName = "Hideout",
            CharacterName = name, Level = 15, ClassName = "Paladin", PartySize = 4, Difficulty = Difficulty.Elite, PlaceSinceUtc = T0.AddMinutes(-9),
        };

        internal static GameSnapshot InTown(string name) => new GameSnapshot
        {
            LoggedIn = true, InTown = true, AreaDid = 0x70000020, AreaName = "The Harbor", CharacterName = name, Level = 7, ClassName = "Rogue",
            PartySize = 1, PlaceSinceUtc = T0.AddMinutes(-2),
        };

        private static Contribution C(GameSnapshot g, int order, PresenceConfig? cfg = null, bool fg = false) =>
            PresenceFormatter.Contribute(g, cfg ?? Cfg(), T0, "i" + order.ToString("00"), T0.AddSeconds(order), fg);

        private static PresenceActivity Merge(params Contribution[] cs) => PresenceFormatter.Merge(cs, cs.First().InstanceId, Cfg()).Activity!;

        [Test]
        public static void OneCharacterInAQuest()
        {
            var a = Merge(C(InQuest("Thalindra"), 0));
            Assert.Equal("The Sharn Syndicate", a.Details, "details");
            Assert.Equal("Thalindra" + D + "Level 15 Paladin" + D + "Elite" + D + "Party of 4", a.State, "state");
            Assert.Equal(PresenceFormatter.GameTitle, a.LargeText, "hover");
        }

        [Test]
        public static void TwoCharactersInTheSameQuestSayItOnce()
        {
            var a = Merge(C(InQuest("Thalindra"), 0), C(InQuest("Borrik"), 1));
            Assert.Equal("The Sharn Syndicate", a.Details, "same place: no hint");
            Assert.Equal("Thalindra, Borrik" + D + "Elite" + D + "Party of 4", a.State, "names in start order; class left out");
        }

        [Test]
        public static void ThreeCharactersTwoInAQuestOneInTown()
        {
            var a = Merge(C(InQuest("Thalindra"), 0), C(InQuest("Borrik"), 1), C(InTown("Vessa"), 2));
            Assert.Equal("The Sharn Syndicate" + D + "+1 elsewhere", a.Details, "primary's place with the hint");
            Assert.Equal("Thalindra, Borrik, Vessa" + D + "Elite" + D + "Party of 4", a.State, "state");
            Assert.Equal(new DateTimeOffset(T0.AddMinutes(-9)).ToUnixTimeSeconds(), a.StartUnixSeconds, "the primary's timer");
        }

        [Test]
        public static void ForegroundClientIsThePrimaryButTheNameOrderStaysPut()
        {
            var a = Merge(C(InQuest("Thalindra"), 0), C(InQuest("Borrik"), 1), C(InTown("Vessa"), 2, fg: true));
            Assert.Equal("The Harbor" + D + "+2 elsewhere", a.Details, "the foreground client's place");
            Assert.True(a.State!.StartsWith("Thalindra, Borrik, Vessa"), "names do not reorder with the foreground window: " + a.State);
            Assert.True(!a.State.Contains("Elite"), "difficulty is the primary's (in town: none)");
        }

        [Test]
        public static void FiveAndTwelveNamesStayWithin128()
        {
            var five = Enumerable.Range(0, 5).Select(i => C(InQuest("Hero" + (char)('a' + i) + "name"), i)).ToArray();
            var a = Merge(five);
            Assert.Equal("Heroaname, Herobname, Herocname, Herodname, Heroename" + D + "Elite" + D + "Party of 4", a.State, "five");
            var twelve = Enumerable.Range(0, 12).Select(i => C(InQuest("Adventurer" + (char)('a' + i) + "xx"), i)).ToArray();
            a = Merge(twelve);
            Assert.True(a.State!.Length <= 128 && a.Details.Length <= 128, "limits: " + a.State.Length);
            Assert.True(a.State.Contains(" more"), "capped with +N more: " + a.State);
            Assert.True(!a.State.Contains("Party of"), "party dropped before names: " + a.State);
            Assert.True(a.LargeText.StartsWith("Playing as Adventureraxx, "), "the full list moves to the hover: " + a.LargeText);
            Assert.True(a.LargeText.Length <= 128, "hover within 128");
        }

        [Test]
        public static void TruncationDropsPartyThenClassThenDifficulty()
        {
            // one character, a long name n: everything fits up to n = 88; without the party up to 101; level only up to 109;
            // without the difficulty up to 117
            string State(int nameLength) => Merge(C(InQuest(new string('n', nameLength - 1) + "Q"), 0)).State!;
            Assert.True(State(80).EndsWith("Party of 4"), "everything fits: " + State(80));
            var s = State(100);
            Assert.True(!s.Contains("Party") && s.Contains("Paladin") && s.Contains("Elite"), "party dropped first: " + s);
            s = State(105);
            Assert.True(!s.Contains("Paladin") && s.Contains("Level 15") && s.Contains("Elite"), "then the class: " + s);
            s = State(112);
            Assert.True(!s.Contains("Elite") && s.Contains("Level 15"), "then the difficulty: " + s);
            foreach (var n in new[] { 60, 100, 110, 116, 120, 127, 128 }) Assert.True(State(n).Length <= 128, "<= 128 at " + n);
        }

        [Test]
        public static void LongQuestNameIsCutAtAWord()
        {
            var name = string.Join(" ", Enumerable.Repeat("Shadowfell", 14));   // 153 characters
            var a = Merge(C(InQuest("Thalindra", questName: name), 0));
            Assert.True(a.Details.Length <= 128 && a.Details.EndsWith("Shadowfell…"), "cut after a whole word: " + a.Details);
            var hinted = Merge(C(InQuest("Thalindra", questName: new string('x', 60) + " " + new string('y', 60)), 0), C(InTown("Vessa"), 1));
            Assert.True(!hinted.Details.Contains("elsewhere"), "the hint is left out when it would not fit");
        }

        [Test]
        public static void MixedPrivacy()
        {
            var nameOff = Cfg(c => c.ShowCharacterName = false);
            var paused = Cfg(c => c.Paused = true);
            var hiding = Cfg(c => c.HiddenPlaces.Add("Harbor"));
            var a = Merge(C(InQuest("Thalindra"), 0), C(InQuest("Borrik"), 1, nameOff), C(InQuest("Vessa"), 2, paused));
            Assert.True(a.State!.StartsWith("Thalindra" + D + "Level 15"), "name off contributes no name, paused contributes nothing: " + a.State);
            a = Merge(C(InQuest("Thalindra"), 0), C(InQuest("Borrik"), 1), C(InTown("Vessa"), 2, hiding));
            Assert.True(a.State!.StartsWith("Thalindra, Borrik" + D) && a.Details == "The Sharn Syndicate", "a hidden secondary is just left out: " + a.Details + " / " + a.State);
            var r = PresenceFormatter.Merge(new[] { C(InTown("Vessa"), 0, hiding, fg: true), C(InQuest("Thalindra"), 1) }, "i00", Cfg());
            Assert.Equal(null, r.Activity, "a hidden place for the primary clears the whole status");
            var off = PresenceFormatter.Merge(new[] { C(InQuest("Thalindra"), 0, paused) }, "i00", Cfg());
            Assert.Equal(null, off.Activity, "nobody sharing: nothing");
        }
    }

    /// <summary>A service whose connector counts attempts and (optionally) reaches a fake Discord.</summary>
    internal sealed class TestNode
    {
        public readonly CountingConnector Connector;
        public readonly PresenceService Service;
        public readonly PresenceNode Node;
        public GameSnapshot Game;
        public PresenceConfig Config = new PresenceConfig { Enabled = true, FirstRunAnswered = true, ShowCharacterName = true };

        public TestNode(IParticipantStore store, ManualClock clock, string id, GameSnapshot game, FakeDiscord? discord = null)
        {
            Connector = new CountingConnector(discord?.Connector());
            Service = new PresenceService(Connector, clock, new ThrottledLog((m, w) => { }, () => clock.UtcNow), 4242);
            Node = new PresenceNode(store, Service, clock, id, 4242);
            Game = game;
        }

        public NodeView Tick(bool fg = false) => Node.Tick(Game, Config, fg);
    }

    internal static class ElectionTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 3, 1, 20, 0, 0, DateTimeKind.Utc);

        private static void TickAll(ManualClock clock, params TestNode[] nodes)
        {
            foreach (var n in nodes) n.Tick();
            clock.Advance(Election.Heartbeat);
        }

        [Test]
        public static void SimultaneousStartGivesExactlyOneLeader()
        {
            var clock = new ManualClock(T0);
            var store = new MemoryParticipantStore();
            var a = new TestNode(store, clock, "bbbb", MultiFormatterTests.InQuest("Thalindra"));
            var b = new TestNode(store, clock, "aaaa", MultiFormatterTests.InQuest("Borrik"));   // same start time: id breaks the tie
            for (var i = 0; i < 4; i++) TickAll(clock, a, b);
            var views = new[] { a.Tick(), b.Tick() };
            Assert.Equal(1, views.Count(v => v.IsLeader), "exactly one leader");
            Assert.True(b.Node.View.IsLeader, "the tie goes to the lower instance id");
            Assert.True(views.All(v => v.LeaderId == "aaaa"), "both agree");
        }

        [Test]
        public static void NobodyLeadsDuringTheStartGrace()
        {
            var clock = new ManualClock(T0);
            var store = new MemoryParticipantStore();
            var a = new TestNode(store, clock, "a", MultiFormatterTests.InQuest("Thalindra"));
            Assert.True(!a.Tick().IsLeader, "not before the grace");
            clock.Advance(Election.Grace);
            Assert.True(a.Tick().IsLeader, "after the grace");
        }

        [Test]
        public static void LeaderThatDiesIsReplacedWithin10Seconds()
        {
            var clock = new ManualClock(T0);
            var store = new MemoryParticipantStore();
            var a = new TestNode(store, clock, "a", MultiFormatterTests.InQuest("Thalindra"));
            clock.Advance(TimeSpan.FromSeconds(1));
            var b = new TestNode(store, clock, "b", MultiFormatterTests.InQuest("Borrik"));
            for (var i = 0; i < 3; i++) TickAll(clock, a, b);
            Assert.True(a.Tick().IsLeader && !b.Tick().IsLeader, "the first started leads");
            // a stops ticking (crashed): b takes over once a's heartbeat is LeaderStale old
            var died = clock.UtcNow;
            var waited = TimeSpan.Zero;
            while (!b.Tick().IsLeader && waited < TimeSpan.FromSeconds(30)) { clock.Advance(Election.Heartbeat); waited += Election.Heartbeat; }
            Assert.True(b.Node.View.IsLeader, "b leads");
            Assert.True(clock.UtcNow - died <= TimeSpan.FromSeconds(10), $"within 10 s ({(clock.UtcNow - died).TotalSeconds} s)");
            Assert.True(b.Node.View.Result.Activity!.State!.StartsWith("Thalindra, Borrik"), "a's name stays until its record expires");
            clock.Advance(Election.Expire);
            Assert.True(b.Tick().Result.Activity!.State!.StartsWith("Borrik" + " · "), "a stale record expires and its name drops out");
        }

        [Test]
        public static void LeaderThatLeavesIsReplacedOnTheNextTick()
        {
            var clock = new ManualClock(T0);
            var store = new MemoryParticipantStore();
            var a = new TestNode(store, clock, "a", MultiFormatterTests.InQuest("Thalindra"));
            clock.Advance(TimeSpan.FromSeconds(1));
            var b = new TestNode(store, clock, "b", MultiFormatterTests.InTown("Vessa"));
            for (var i = 0; i < 3; i++) TickAll(clock, a, b);
            Assert.True(a.Tick().IsLeader, "a leads");
            a.Game = GameSnapshot.LoggedOut;          // logout
            a.Tick();
            var v = b.Tick();
            Assert.True(v.IsLeader, "b leads at once");
            Assert.Equal("Vessa" + " · " + "Level 7 Rogue", v.Result.Activity!.State, "the logged-out character's name is gone on the next update");
        }

        [Test]
        public static async Task OnlyTheLeaderEverCallsSetActivity()
        {
            await using var discord = new FakeDiscord().Start();
            var clock = new ManualClock(T0);
            var store = new MemoryParticipantStore();
            var a = new TestNode(store, clock, "a", MultiFormatterTests.InQuest("Thalindra"), discord);
            var b = new TestNode(store, clock, "b", MultiFormatterTests.InQuest("Borrik"), discord);
            var c = new TestNode(store, clock, "c", MultiFormatterTests.InTown("Vessa"), discord);
            foreach (var n in new[] { a, b, c }) n.Service.Start();
            for (var i = 0; i < 4; i++) TickAll(clock, a, b, c);
            await Assert.Eventually(() => discord.SetActivityCount >= 1, "the leader sent the merged status");
            for (var i = 0; i < 3; i++) TickAll(clock, a, b, c);
            await Task.Delay(200);
            Assert.Equal(0, b.Connector.Attempts + c.Connector.Attempts, "followers never connect");
            Assert.Equal(1, discord.Connections, "one Discord connection");
            var last = discord.ActivityList().Last()["activity"]!;
            Assert.Equal("Thalindra, Borrik, Vessa · Elite · Party of 4", last["state"]!.GetValue<string>(), "merged status");
            Assert.Equal("The Sharn Syndicate · +1 elsewhere", last["details"]!.GetValue<string>(), "merged details");
            foreach (var n in new[] { a, b, c }) await n.Service.StopAsync(TimeSpan.FromSeconds(1));
        }

        [Test]
        public static void FileStoreSharesRecordsBetweenSeparateHandles()
        {
            var folder = Path.Combine(Path.GetTempPath(), "ddp-tests", "store-" + Guid.NewGuid().ToString("N"));
            var clock = new ManualClock(T0);
            var a = new TestNode(new FileParticipantStore(folder, () => clock.UtcNow), clock, "a", MultiFormatterTests.InQuest("Thalindra"));
            clock.Advance(TimeSpan.FromSeconds(1));
            var b = new TestNode(new FileParticipantStore(folder, () => clock.UtcNow), clock, "b", MultiFormatterTests.InQuest("Borrik"));
            for (var i = 0; i < 3; i++) TickAll(clock, a, b);
            var va = a.Tick(); var vb = b.Tick();
            Assert.True(va.IsLeader && !vb.IsLeader && vb.LeaderId == "a", "one leader across handles");
            Assert.Equal(2, vb.Participants.Count, "both records visible");
            a.Game = GameSnapshot.LoggedOut; a.Tick();
            Assert.Equal(1, Directory.GetFiles(folder, "*.json").Length, "logout deletes the record");
            Assert.True(!File.ReadAllText(Directory.GetFiles(folder, "*.json")[0]).Contains("Thalindra"), "no trace of the logged-out character");
        }

        [Test]
        public static void NamesAreNotWrittenWhenTheNameIsOff()
        {
            var folder = Path.Combine(Path.GetTempPath(), "ddp-tests", "store-" + Guid.NewGuid().ToString("N"));
            var clock = new ManualClock(T0);
            var a = new TestNode(new FileParticipantStore(folder, () => clock.UtcNow), clock, "a", MultiFormatterTests.InQuest("Thalindra"));
            a.Config.ShowCharacterName = false;
            a.Tick();
            Assert.True(!File.ReadAllText(Directory.GetFiles(folder, "*.json")[0]).Contains("Thalindra"), "the record holds no name");
        }
    }

    /// <summary>Two real processes over the real file store: the test exe is started again as a participant.</summary>
    internal static class CrossProcessTests
    {
        private static readonly TimeSpan Stale = TimeSpan.FromMilliseconds(1500), Grace = TimeSpan.FromMilliseconds(400), Beat = TimeSpan.FromMilliseconds(250);

        /// <summary>Child role: --participant folder name seconds. Publishes as a sharing character until killed or done.</summary>
        public static int RunParticipant(string[] args)
        {
            var folder = args[1]; var name = args[2]; var seconds = double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
            var clock = SystemClock.Instance;
            var svc = new PresenceService(new CountingConnector(null), clock, new ThrottledLog((m, w) => { }), Environment.ProcessId);
            var node = new PresenceNode(new FileParticipantStore(folder), svc, clock, "child-" + Environment.ProcessId, Environment.ProcessId, Stale, Grace);
            var cfg = new PresenceConfig { Enabled = true, FirstRunAnswered = true, ShowCharacterName = true };
            var game = MultiFormatterTests.InTown(name) with { PlaceSinceUtc = DateTime.UtcNow };
            var sw = Stopwatch.StartNew();
            Console.WriteLine("ready");
            while (sw.Elapsed.TotalSeconds < seconds) { node.Tick(game, cfg, false); Thread.Sleep(Beat); }
            node.Tick(GameSnapshot.LoggedOut, cfg, false);
            return 0;
        }

        [Test]
        public static async Task TwoProcessesElectOneLeaderAndHandOver()
        {
            var folder = Path.Combine(Path.GetTempPath(), "ddp-tests", "xproc-" + Guid.NewGuid().ToString("N"));
            var exe = Process.GetCurrentProcess().MainModule!.FileName!;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (var a in new[] { "--participant", folder, "Vessa", "60" }) psi.ArgumentList.Add(a);
            using var child = Process.Start(psi)!;
            try
            {
                var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal("ready", line, "child started");
                await Task.Delay(800);       // the child started first, so it should lead
                var clock = SystemClock.Instance;
                var svc = new PresenceService(new CountingConnector(null), clock, new ThrottledLog((m, w) => { }), Environment.ProcessId);
                var node = new PresenceNode(new FileParticipantStore(folder), svc, clock, "parent", Environment.ProcessId, Stale, Grace);
                var cfg = new PresenceConfig { Enabled = true, FirstRunAnswered = true, ShowCharacterName = true };
                var game = MultiFormatterTests.InQuest("Thalindra") with { PlaceSinceUtc = DateTime.UtcNow };
                NodeView v = node.Tick(game, cfg, false);
                for (var i = 0; i < 8; i++) { await Task.Delay(Beat); v = node.Tick(game, cfg, false); }
                Assert.True(!v.IsLeader && v.LeaderId == "child-" + child.Id, "the other process leads: " + v.LeaderId);
                Assert.Equal(2, v.Participants.Count, "both processes participate");
                Assert.True(v.Result.Activity!.State!.StartsWith("Vessa, Thalindra"), "merged across processes, start order: " + v.Result.Activity.State);

                child.Kill();                // dies without cleaning up
                var killed = Stopwatch.StartNew();
                while (!v.IsLeader && killed.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(Beat); v = node.Tick(game, cfg, false); }
                Assert.True(v.IsLeader, "takeover after the leader died");
                Assert.True(killed.Elapsed <= Stale + TimeSpan.FromSeconds(1), $"within the stale bound ({killed.ElapsedMilliseconds} ms)");
            }
            finally { try { if (!child.HasExited) child.Kill(); } catch { } }
        }
    }

    internal static class PanelTests
    {
        private static string TempPluginFolder()
        {
            var d = Path.Combine(Path.GetTempPath(), "ddp-tests", Guid.NewGuid().ToString("N"), "DdoDiscordPresence", "win-x64");
            Directory.CreateDirectory(d);
            return d;
        }

        private static IEnumerable<Control> All(Control c)
        {
            foreach (Control child in c.Controls) { yield return child; foreach (var d in All(child)) yield return d; }
        }

        [Test]
        public static void NewInstallAsksFirstAndHasNoIdField()
        {
            var folder = TempPluginFolder();
            var g = new FakeGame { InTown = true, AreaDid = 0x70000020 };
            g.AreaNames[0x70000020] = "The Harbor";
            var plugin = new Plugin();
            plugin.Initialize(g.Provider(), folder);
            var form = (Form)plugin.GetPluginUI()!.UserInterfaceForm;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-20000, -20000);
            form.Show();
            Application.DoEvents();
            var controls = All(form).ToList();
            Assert.True(controls.OfType<TextBox>().All(t => t.Name == "HiddenPlaces"), "the only text box is the hide list");
            foreach (var bad in new[] { "Application ID", "developer portal", "art folder", "How to set" })
                Assert.True(!controls.Any(c => (c.Text ?? "").Contains(bad, StringComparison.OrdinalIgnoreCase)), "no '" + bad + "' in the panel");
            var card = controls.First(c => c.Name == "FirstRun");
            var nameBox = controls.First(c => c.Name == "ShowCharacterName");
            Assert.True(card.Visible && !nameBox.Visible, "first run: only the question");
            Assert.True(nameBox.Text.Contains("all logged-in characters when multi-boxing"), nameBox.Text);
            Assert.True(controls.First(c => c.Name == "Byline").Text.Contains("by longfreename"), "author shown");
            Assert.Equal(null, plugin.Result.Activity, "nothing shared before the answer");
            var cfgPath = Path.Combine(Path.GetDirectoryName(folder)!, ConfigStore.FileName);
            Assert.True(!File.Exists(cfgPath), "opening the panel wrote nothing");

            ((Button)controls.First(c => c.Name == "TurnOn")).PerformClick();
            Assert.True(!card.Visible && nameBox.Visible, "after Turn on: the settings");
            var text = File.ReadAllText(cfgPath);
            Assert.True(text.Contains("\"Enabled\": true") && text.Contains("\"FirstRunAnswered\": true"), "the choice is saved");
            Assert.True(!text.Contains("ApplicationId") && !text.Contains("LargeImageKey"), "no id in a normal user's file");
            form.Hide();
            plugin.Terminate();
        }

        [Test]
        public static void NotNowIsSavedAndSharesNothing()
        {
            var folder = TempPluginFolder();
            var plugin = new Plugin();
            plugin.Initialize(new FakeGame().Provider(), folder);
            plugin.AnswerFirstRun(false);
            var c = plugin.Config;
            Assert.True(!c.Enabled && !c.NeedsFirstRunAnswer, "off, not asked again");
            Assert.True(plugin.StatusLine().Text.StartsWith("Off"), plugin.StatusLine().Text);
            plugin.Terminate();
        }

        [Test]
        public static void AuthorIsLongfreename()
        {
            var p = new Plugin();
            Assert.Equal("longfreename", p.Author, "IPlugin.Author (the host's plugin list)");
            var asm = typeof(Plugin).Assembly;
            var company = (System.Reflection.AssemblyCompanyAttribute?)Attribute.GetCustomAttribute(asm, typeof(System.Reflection.AssemblyCompanyAttribute));
            var copyright = (System.Reflection.AssemblyCopyrightAttribute?)Attribute.GetCustomAttribute(asm, typeof(System.Reflection.AssemblyCopyrightAttribute));
            Assert.Equal("longfreename", company?.Company, "assembly company");
            Assert.Equal("Copyright (c) 2026 longfreename", copyright?.Copyright, "assembly copyright");
            Assert.Equal(new Version(0, 2, 0, 0), asm.GetName().Version, "version 0.2.0");
        }
    }
}

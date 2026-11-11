using System;
using System.Linq;
using DdoDiscordPresence.Presence;

namespace DdoDiscordPresence.Tests
{
    /// <summary>The documented examples, printed and pinned.</summary>
    internal static class ExampleTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 3, 1, 20, 0, 0, DateTimeKind.Utc);
        private static readonly PresenceConfig Cfg = new PresenceConfig { Enabled = true, FirstRunAnswered = true, ShowCharacterName = true };

        private static void Print(string title, PresenceActivity a) =>
            Console.WriteLine($"      [{title}] details=\"{a.Details}\" state=\"{a.State}\" hover=\"{a.LargeText}\" ({a.State?.Length} chars)");

        [Test]
        public static void DocumentedExamples()
        {
            var one = PresenceFormatter.Merge(new[] { PresenceFormatter.Contribute(MultiFormatterTests.InQuest("Thalindra"), Cfg, T0, "a", T0, true) }, "a", Cfg).Activity!;
            Print("one character in a quest", one);
            Assert.Equal("Thalindra · Level 15 Paladin · Elite · Party of 4", one.State, "one");

            var three = PresenceFormatter.Merge(new[]
            {
                PresenceFormatter.Contribute(MultiFormatterTests.InQuest("Thalindra"), Cfg, T0, "a", T0, false),
                PresenceFormatter.Contribute(MultiFormatterTests.InQuest("Borrik"), Cfg, T0, "b", T0.AddSeconds(1), false),
                PresenceFormatter.Contribute(MultiFormatterTests.InTown("Vessa"), Cfg, T0, "c", T0.AddSeconds(2), false),
            }, "a", Cfg).Activity!;
            Print("three: two in a quest, one in town", three);
            Assert.Equal("The Sharn Syndicate · +1 elsewhere", three.Details, "three details");

            var names = new[] { "Thalindra", "Borrik", "Vessa", "Kaelith", "Mirabel", "Oswin", "Tamsin", "Garrick", "Liora", "Fenwick", "Sablewood", "Quillon" };
            var twelve = PresenceFormatter.Merge(names.Select((n, i) =>
                PresenceFormatter.Contribute(MultiFormatterTests.InQuest(n), Cfg, T0, "i" + i.ToString("00"), T0.AddSeconds(i), false)).ToList(), "i00", Cfg).Activity!;
            Print("twelve in one quest", twelve);
            Assert.True(twelve.State!.Length <= 128, "twelve within 128");
        }
    }
}

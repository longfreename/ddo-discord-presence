using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DdoDiscordPresence.Discord;
using DdoDiscordPresence.Presence;

namespace DdoDiscordPresence.Tests
{
    internal static class LimiterTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        [Test]
        public static void BackoffIs5Then15Then60Capped()
        {
            var b = new Backoff();
            var seq = Enumerable.Range(0, 6).Select(_ => (int)b.Next().TotalSeconds).ToArray();
            Assert.True(seq.SequenceEqual(new[] { 5, 15, 60, 60, 60, 60 }), "sequence " + string.Join(",", seq));
            b.Reset();
            Assert.Equal(5, (int)b.Next().TotalSeconds, "after reset");
        }

        [Test]
        public static void OrdinaryUpdatesAreSpaced15Seconds()
        {
            var l = new ActivityRateLimiter();
            Assert.Equal(TimeSpan.Zero, l.WaitBeforeSend(T0, false), "first send");
            l.Record(T0);
            Assert.Equal(TimeSpan.FromSeconds(15), l.WaitBeforeSend(T0, false), "right after");
            Assert.Equal(TimeSpan.FromSeconds(5), l.WaitBeforeSend(T0.AddSeconds(10), false), "10 s later");
            Assert.Equal(TimeSpan.Zero, l.WaitBeforeSend(T0.AddSeconds(15), false), "15 s later");
        }

        [Test]
        public static void PrioritySendsSkipSpacingButNotTheBurstCap()
        {
            var l = new ActivityRateLimiter();
            l.Record(T0);
            Assert.Equal(TimeSpan.Zero, l.WaitBeforeSend(T0.AddSeconds(1), true), "a clear goes at once");
            l.Record(T0.AddSeconds(1));
            l.Record(T0.AddSeconds(2));
            // 3 sends in the 20 s window: even a clear waits for the oldest to age out (Discord allows 5 / 20 s)
            Assert.Equal(TimeSpan.FromSeconds(17), l.WaitBeforeSend(T0.AddSeconds(3), true), "burst cap");
            Assert.Equal(TimeSpan.Zero, l.WaitBeforeSend(T0.AddSeconds(20), true), "window passed");
        }

        [Test]
        public static void NeverMoreThanFiveIn20SecondsUnderAnyMix()
        {
            var l = new ActivityRateLimiter();
            var sends = new List<DateTime>();
            var rnd = new Random(7);
            var t = T0;
            for (var i = 0; i < 2000; i++)
            {
                t = t.AddMilliseconds(rnd.Next(0, 1500));
                var priority = rnd.Next(2) == 0;
                if (l.WaitBeforeSend(t, priority) == TimeSpan.Zero) { l.Record(t); sends.Add(t); }
            }
            for (var i = 0; i < sends.Count; i++)
            {
                var inWindow = sends.Count(s => s >= sends[i] && s < sends[i].AddSeconds(20));
                Assert.True(inWindow <= 3, $"at most 3 per 20 s (found {inWindow})");
            }
            Assert.True(sends.Count > 50, "still sends");
        }

        [Test]
        public static void ThrottledLogWritesEachMessageOncePerMinute()
        {
            var now = T0;
            var lines = new List<string>();
            var log = new ThrottledLog((m, w) => lines.Add((w ? "W " : "I ") + m), () => now);
            Assert.True(log.Warn("Discord not running"), "first");
            Assert.True(!log.Warn("Discord not running"), "repeat suppressed");
            Assert.True(log.Info("Connected as x"), "a different message passes");
            now = now.AddSeconds(59);
            Assert.True(!log.Warn("Discord not running"), "59 s later still suppressed");
            now = now.AddSeconds(2);
            Assert.True(log.Warn("Discord not running"), "after a minute again");
            Assert.Equal(3, lines.Count, "lines written");
        }

        [Test]
        public static void ChangeDetectorSkipsIdenticalActivities()
        {
            var d = new ActivityChangeDetector();
            var a = new PresenceActivity { Details = "The Harbor", State = "Level 5 Rogue", StartUnixSeconds = 100 };
            Assert.True(d.ShouldSend(null), "unknown state: even a clear is sent");
            d.ResetForNewConnection();
            Assert.True(!d.ShouldSend(null), "new connection: nothing is already shown");
            Assert.True(d.ShouldSend(a), "new content");
            d.MarkSent(a);
            Assert.True(!d.ShouldSend(a with { }), "same content (a copy) is not resent");
            Assert.True(d.ShouldSend(a with { StartUnixSeconds = 160 }), "a new start time is a change");
            Assert.True(d.ShouldSend(a with { State = "Level 6 Rogue" }), "a state change");
            Assert.True(d.ShouldSend(null), "clearing is a change");
            d.MarkSent(null);
            Assert.True(!d.ShouldSend(null), "already clear");
            d.Forget();
            Assert.True(d.ShouldSend(a), "after a disconnect");
        }
    }

    /// <summary>A connector that counts attempts and delegates (or fails as "not running").</summary>
    internal sealed class CountingConnector : IPipeConnector
    {
        private readonly IPipeConnector? _inner;
        public int Attempts;
        public CountingConnector(IPipeConnector? inner) { _inner = inner; }
        public IPipeConnector? Inner { get; set; }
        public Task<Stream> ConnectAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Attempts);
            var inner = Inner ?? _inner;
            if (inner == null) throw new DiscordNotRunningException("Discord is not running (test)");
            return inner.ConnectAsync(ct);
        }
    }

    internal static class ServiceTests
    {
        private const string Id = "123456789012345678";
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        private static (PresenceService Svc, ManualClock Clock, List<string> Log) Make(IPipeConnector connector)
        {
            var clock = new ManualClock(T0);
            var log = new List<string>();
            var tl = new ThrottledLog((m, w) => { lock (log) log.Add(m); }, () => clock.UtcNow);
            var svc = new PresenceService(connector, clock, tl, 777);
            return (svc, clock, log);
        }

        private static PresenceActivity A(string details) => new PresenceActivity { Details = details, State = "Level 5 Rogue" };

        [Test]
        public static async Task NotConfiguredDoesNothing()
        {
            var counting = new CountingConnector(null);
            var (svc, clock, _) = Make(counting);
            svc.Start();
            svc.SetDesired(A("The Harbor"));
            await Task.Delay(150);
            clock.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(100);
            Assert.Equal(0, counting.Attempts, "no connection without an Application ID");
            Assert.Equal(PresenceStatusKind.Off, svc.Status.Kind, "status");
            await svc.StopAsync(TimeSpan.FromSeconds(1));
        }

        [Test]
        public static async Task SendsOnceAndSkipsUnchangedContent()
        {
            await using var fake = new FakeDiscord().Start();
            var (svc, clock, _) = Make(fake.Connector());
            svc.Configure(Id);
            svc.SetDesired(A("The Harbor"));
            svc.Start();
            await Assert.Eventually(() => fake.SetActivityCount == 1, "first SET_ACTIVITY");
            Assert.Equal(PresenceStatusKind.Connected, svc.Status.Kind, "connected");
            Assert.Equal("Connected as tester", svc.Status.Text, "status names the READY user");
            Assert.Equal(777, fake.ActivityList()[0]["pid"]!.GetValue<int>(), "pid");
            svc.SetDesired(A("The Harbor"));          // same content
            clock.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(200);
            Assert.Equal(1, fake.SetActivityCount, "unchanged content is not resent");
            await svc.StopAsync(TimeSpan.FromSeconds(1));
        }

        [Test]
        public static async Task ChangesWithin15SecondsWaitForTheLimiter()
        {
            await using var fake = new FakeDiscord().Start();
            var (svc, clock, _) = Make(fake.Connector());
            svc.Configure(Id);
            svc.SetDesired(A("The Harbor"));
            svc.Start();
            await Assert.Eventually(() => fake.SetActivityCount == 1, "first");
            clock.Advance(TimeSpan.FromSeconds(3));
            svc.SetDesired(A("The Marketplace"));
            svc.SetDesired(A("House Deneith"));       // only the latest matters
            await Task.Delay(200);
            Assert.Equal(1, fake.SetActivityCount, "held by the 15 s spacing");
            clock.Advance(TimeSpan.FromSeconds(11));
            await Task.Delay(150);
            Assert.Equal(1, fake.SetActivityCount, "still held at 14 s");
            clock.Advance(TimeSpan.FromSeconds(1));
            await Assert.Eventually(() => fake.SetActivityCount == 2, "sent at 15 s");
            Assert.Equal("House Deneith", fake.ActivityList()[1]["activity"]!["details"]!.GetValue<string>(), "latest content wins");
            await svc.StopAsync(TimeSpan.FromSeconds(1));
        }

        [Test]
        public static async Task ClearIsImmediate()
        {
            await using var fake = new FakeDiscord().Start();
            var (svc, clock, _) = Make(fake.Connector());
            svc.Configure(Id);
            svc.SetDesired(A("The Harbor"));
            svc.Start();
            await Assert.Eventually(() => fake.SetActivityCount == 1, "first");
            svc.SetDesired(null);      // pause / hidden place / logout
            await Assert.Eventually(() => fake.SetActivityCount == 2, "clear sent without waiting 15 s");
            Assert.True(fake.ActivityList()[1]["activity"] == null, "activity null");
            await svc.StopAsync(TimeSpan.FromSeconds(1));
        }

        [Test]
        public static async Task NothingToShowOnANewConnectionSendsNothing()
        {
            await using var fake = new FakeDiscord().Start();
            var (svc, _, _) = Make(fake.Connector());
            svc.Configure(Id);
            svc.Start();
            await Assert.Eventually(() => svc.Status.Kind == PresenceStatusKind.Connected, "connected");
            await Task.Delay(200);
            Assert.Equal(0, fake.SetActivityCount, "no SET_ACTIVITY when there is nothing to show");
            await svc.StopAsync(TimeSpan.FromSeconds(1));
        }

        [Test]
        public static async Task DiscordNotRunningRetriesAt5Then15Then60()
        {
            var counting = new CountingConnector(null);
            var (svc, clock, log) = Make(counting);
            svc.Configure(Id);
            svc.SetDesired(A("The Harbor"));
            svc.Start();
            await Assert.Eventually(() => counting.Attempts == 1, "first attempt at once");
            await Assert.Eventually(() => svc.Status.Kind == PresenceStatusKind.DiscordNotRunning, "status says Discord not running");
            async Task Expect(int seconds, int attempts)
            {
                await Assert.Eventually(() => clock.PendingDelays > 0, "loop waiting");
                clock.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(100));
                await Task.Delay(120);
                Assert.Equal(attempts - 1, counting.Attempts, $"no attempt before {seconds} s");
                clock.Advance(TimeSpan.FromMilliseconds(100));
                await Assert.Eventually(() => counting.Attempts == attempts, $"attempt {attempts} after {seconds} s");
            }
            await Expect(5, 2);
            await Expect(15, 3);
            await Expect(60, 4);
            await Expect(60, 5);
            // 140 s of retries at 0, 5, 20, 80 and 140 s: the same message is written at most once a minute (0, 80, 140)
            lock (log)
            {
                Assert.True(log.Count(l => l.Contains("not running")) <= 3, "the log is throttled: " + string.Join(" | ", log));
                Assert.True(log.All(l => !l.Contains("retrying in")), "log lines leave out the changing delay");
                Assert.True(log.All(l => !l.Contains("Connecting")), "connect attempts are not logged");
            }
            await svc.StopAsync(TimeSpan.FromSeconds(1));
        }

        [Test]
        public static async Task DiscordRestartReconnectsAndResendsThePresence()
        {
            var fake = new FakeDiscord().Start();
            var prefix = fake.Prefix;
            var counting = new CountingConnector(fake.Connector());
            var (svc, clock, _) = Make(counting);
            svc.Configure(Id);
            svc.SetDesired(A("The Harbor"));
            svc.Start();
            await Assert.Eventually(() => fake.SetActivityCount == 1, "first");
            await fake.DisposeAsync();              // Discord quits
            await Assert.Eventually(() => svc.Status.Kind != PresenceStatusKind.Connected, "noticed the broken pipe");
            await using var restarted = new FakeDiscord(prefix).Start();     // Discord starts again
            await Assert.Eventually(() => clock.PendingDelays > 0, "loop waiting for back-off");
            clock.Advance(TimeSpan.FromSeconds(5));
            await Assert.Eventually(() => restarted.SetActivityCount == 1, "reconnected after 5 s and sent the presence again");
            Assert.Equal("The Harbor", restarted.ActivityList()[0]["activity"]!["details"]!.GetValue<string>(), "same presence");
            Assert.Equal(PresenceStatusKind.Connected, svc.Status.Kind, "connected again");
            await svc.StopAsync(TimeSpan.FromSeconds(1));
        }

        [Test]
        public static async Task CloseFromDiscordIsReportedThenRetried()
        {
            await using var fake = new FakeDiscord().Start();
            var (svc, clock, _) = Make(fake.Connector());
            svc.Configure(Id);
            svc.SetDesired(A("The Harbor"));
            svc.Start();
            await Assert.Eventually(() => fake.SetActivityCount == 1, "first");
            await fake.SendCloseAsync(4000, "Invalid Client ID");
            await Assert.Eventually(() => svc.Status.Kind == PresenceStatusKind.Error, "error status");
            Assert.True(svc.Status.Text.Contains("4000") && svc.Status.Text.Contains("Invalid Client ID"), "status carries Discord's code and message: " + svc.Status.Text);
            await svc.StopAsync(TimeSpan.FromSeconds(1));
        }

        [Test]
        public static async Task InvalidApplicationIdShowsDiscordsError()
        {
            await using var fake = new FakeDiscord { Mode = FakeDiscord.HandshakeMode.CloseInvalidClientId }.Start();
            var (svc, _, _) = Make(fake.Connector());
            svc.Configure("000000000000000000");
            svc.SetDesired(A("The Harbor"));
            svc.Start();
            await Assert.Eventually(() => svc.Status.Kind == PresenceStatusKind.Error, "error status");
            Assert.True(svc.Status.Text.StartsWith("Error: 4000 Invalid Client ID"), svc.Status.Text);
            await svc.StopAsync(TimeSpan.FromSeconds(1));
        }

        [Test]
        public static async Task UnconfiguringClearsThenDisconnects()
        {
            await using var fake = new FakeDiscord().Start();
            var (svc, _, _) = Make(fake.Connector());
            svc.Configure(Id);
            svc.SetDesired(A("The Harbor"));
            svc.Start();
            await Assert.Eventually(() => fake.SetActivityCount == 1, "first");
            svc.Configure(null);          // sharing turned off
            await Assert.Eventually(() => fake.SetActivityCount == 2, "cleared before disconnecting");
            Assert.True(fake.ActivityList()[1]["activity"] == null, "activity null");
            await Assert.Eventually(() => !svc.IsConnected && svc.Status.Kind == PresenceStatusKind.Off, "disconnected");
            await svc.StopAsync(TimeSpan.FromSeconds(1));
        }

        [Test]
        public static async Task TestSendsTheSampleAndReportsDiscordsAnswerThenRestores()
        {
            await using var fake = new FakeDiscord().Start();
            var (svc, clock, _) = Make(fake.Connector());
            svc.Configure(Id);
            svc.SetDesired(A("The Harbor"));
            svc.Start();
            await Assert.Eventually(() => fake.SetActivityCount == 1, "first");
            var answer = await svc.TestAsync(Id, A("The Sharn Syndicate"), TimeSpan.FromSeconds(5));
            Assert.True(answer.StartsWith("Discord accepted"), answer);
            Assert.Equal("The Sharn Syndicate", fake.ActivityList()[1]["activity"]!["details"]!.GetValue<string>(), "sample sent at once");
            clock.Advance(TimeSpan.FromSeconds(20));
            await Assert.Eventually(() => fake.SetActivityCount == 3, "normal presence restored after 20 s");
            Assert.Equal("The Harbor", fake.ActivityList()[2]["activity"]!["details"]!.GetValue<string>(), "restored");
            await svc.StopAsync(TimeSpan.FromSeconds(1));
        }

        [Test]
        public static async Task TestReportsAnErrorReply()
        {
            await using var fake = new FakeDiscord { ActivityError = (4000, "Invalid asset") }.Start();
            var (svc, _, _) = Make(fake.Connector());
            svc.Configure(Id);
            svc.Start();
            var answer = await svc.TestAsync(Id, A("The Sharn Syndicate"), TimeSpan.FromSeconds(5));
            Assert.True(answer.Contains("error") && answer.Contains("4000") && answer.Contains("Invalid asset"), answer);
            await svc.StopAsync(TimeSpan.FromSeconds(1));
        }

        [Test]
        public static async Task TestWithoutDiscordSaysSo()
        {
            var (svc, _, _) = Make(new CountingConnector(null));
            svc.Start();
            var answer = await svc.TestAsync(Id, A("The Sharn Syndicate"), TimeSpan.FromSeconds(5));
            Assert.True(answer.Contains("not running"), answer);
            await svc.StopAsync(TimeSpan.FromSeconds(1));
        }

        [Test]
        public static async Task StopClearsTheActivityWithinTheCap()
        {
            await using var fake = new FakeDiscord().Start();
            var (svc, _, _) = Make(fake.Connector());
            svc.Configure(Id);
            svc.SetDesired(A("The Harbor"));
            svc.Start();
            await Assert.Eventually(() => fake.SetActivityCount == 1, "first");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await svc.StopAsync(TimeSpan.FromSeconds(1));
            Assert.True(sw.ElapsedMilliseconds < 1300, "within the cap");
            await Assert.Eventually(() => fake.SetActivityCount == 2, "clear sent on stop");
            Assert.True(fake.ActivityList()[1]["activity"] == null, "activity null");
            Assert.Equal(PresenceStatusKind.Stopped, svc.Status.Kind, "stopped");
        }

        [Test]
        public static async Task StopIsCappedEvenWhenDiscordHangs()
        {
            await using var fake = new FakeDiscord().Start();
            var (svc, _, _) = Make(fake.Connector());
            svc.Configure(Id);
            svc.SetDesired(A("The Harbor"));
            svc.Start();
            await Assert.Eventually(() => fake.SetActivityCount == 1, "first");
            fake.AnswerCommands = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await svc.StopAsync(TimeSpan.FromSeconds(1));
            Assert.True(sw.ElapsedMilliseconds < 1400, $"capped at about 1 s ({sw.ElapsedMilliseconds} ms)");
        }
    }
}

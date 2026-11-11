using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace DdoDiscordPresence.Tests
{
    /// <summary>A small console test runner: every public static method of a *Tests class marked [Test] runs once.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class TestAttribute : Attribute { }

    internal sealed class AssertFailed : Exception
    {
        public AssertFailed(string message) : base(message) { }
    }

    internal static class Assert
    {
        public static void True(bool condition, string what)
        {
            if (!condition) throw new AssertFailed(what);
        }

        public static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertFailed($"{what}: expected <{expected}> but got <{actual}>");
        }

        public static TEx Throws<TEx>(Action action, string what) where TEx : Exception
        {
            try { action(); }
            catch (TEx ex) { return ex; }
            catch (Exception ex) { throw new AssertFailed($"{what}: expected {typeof(TEx).Name} but got {ex.GetType().Name}: {ex.Message}"); }
            throw new AssertFailed($"{what}: expected {typeof(TEx).Name}, nothing was thrown");
        }

        public static async Task<TEx> ThrowsAsync<TEx>(Func<Task> action, string what) where TEx : Exception
        {
            try { await action(); }
            catch (TEx ex) { return ex; }
            catch (Exception ex) { throw new AssertFailed($"{what}: expected {typeof(TEx).Name} but got {ex.GetType().Name}: {ex.Message}"); }
            throw new AssertFailed($"{what}: expected {typeof(TEx).Name}, nothing was thrown");
        }

        /// <summary>Polls (real time) until the condition holds.</summary>
        public static async Task Eventually(Func<bool> condition, string what, int timeoutMs = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return;
                await Task.Delay(10);
            }
            if (!condition()) throw new AssertFailed("timed out waiting for: " + what);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // A form created on this thread would otherwise install the WinForms synchronization context, and an awaited
            // test continuation would then wait for a message loop that the runner (blocked in Wait) never pumps.
            System.Windows.Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
            // Never reach the real Discord or the real coordination folder from a test.
            Environment.SetEnvironmentVariable("DDO_DISCORD_PRESENCE_PIPE_PREFIX", "ddp-test-nodiscord-" + Guid.NewGuid().ToString("N") + "-");
            Environment.SetEnvironmentVariable("DDO_DISCORD_PRESENCE_COORDINATION",
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ddp-tests", "coord-" + Guid.NewGuid().ToString("N")));
            if (args.Length > 0 && args[0] == "--participant") return CrossProcessTests.RunParticipant(args);
            var filter = args.Length > 0 ? args[0] : null;
            var tests = new List<(string Name, MethodInfo Method)>();
            foreach (var type in typeof(Program).Assembly.GetTypes())
            {
                if (!type.Name.EndsWith("Tests", StringComparison.Ordinal)) continue;
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.GetCustomAttribute<TestAttribute>() == null) continue;
                    var name = type.Name + "." + m.Name;
                    if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    tests.Add((name, m));
                }
            }
            tests.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            int passed = 0, failed = 0;
            var total = Stopwatch.StartNew();
            foreach (var (name, method) in tests)
            {
                var sw = Stopwatch.StartNew();
                SynchronizationContext.SetSynchronizationContext(null);
                try
                {
                    var result = method.Invoke(null, null);
                    if (result is Task t)
                    {
                        if (!t.Wait(TimeSpan.FromSeconds(30))) throw new AssertFailed("test timed out after 30 s");
                        t.GetAwaiter().GetResult();
                    }
                    passed++;
                    Console.WriteLine($"  pass  {name} ({sw.ElapsedMilliseconds} ms)");
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    if (inner is AggregateException ag && ag.InnerException != null) inner = ag.InnerException;
                    failed++;
                    Console.WriteLine($"  FAIL  {name}: {inner.GetType().Name}: {inner.Message}");
                    if (!(inner is AssertFailed)) Console.WriteLine(inner.StackTrace);
                }
            }
            Console.WriteLine();
            Console.WriteLine($"{passed} passed, {failed} failed, {tests.Count} total ({total.Elapsed.TotalSeconds:0.0} s)");
            return failed == 0 && tests.Count > 0 ? 0 : 1;
        }
    }

    /// <summary>A clock tests move by hand. Delays complete when the clock passes their due time.</summary>
    internal sealed class ManualClock : DdoDiscordPresence.Discord.IClock
    {
        private readonly object _lock = new object();
        private readonly List<(DateTime Due, TaskCompletionSource<bool> Tcs)> _waiters = new List<(DateTime, TaskCompletionSource<bool>)>();
        private DateTime _now;

        public ManualClock(DateTime start) { _now = start; }

        public DateTime UtcNow { get { lock (_lock) return _now; } }

        public int PendingDelays { get { lock (_lock) return _waiters.Count; } }

        public Task Delay(TimeSpan delay, CancellationToken ct)
        {
            if (delay <= TimeSpan.Zero) return Task.CompletedTask;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) _waiters.Add((_now + delay, tcs));
            if (ct.CanBeCanceled)
                ct.Register(() =>
                {
                    lock (_lock) _waiters.RemoveAll(w => w.Tcs == tcs);
                    tcs.TrySetCanceled();
                });
            return tcs.Task;
        }

        public void Advance(TimeSpan by)
        {
            var due = new List<TaskCompletionSource<bool>>();
            lock (_lock)
            {
                _now += by;
                foreach (var w in _waiters.ToArray())
                    if (w.Due <= _now) { due.Add(w.Tcs); _waiters.Remove(w); }
            }
            foreach (var t in due) t.TrySetResult(true);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DdoDiscordPresence.Discord
{
    /// <summary>Time source, injectable so reconnect back-off and rate limits can be tested without waiting.</summary>
    internal interface IClock
    {
        DateTime UtcNow { get; }
        Task Delay(TimeSpan delay, CancellationToken ct);
    }

    internal sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();
        public DateTime UtcNow => DateTime.UtcNow;
        public Task Delay(TimeSpan delay, CancellationToken ct) =>
            delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);
    }

    /// <summary>Reconnect back-off: 5 s, 15 s, then 60 s for every later attempt, until a connection succeeds.</summary>
    internal sealed class Backoff
    {
        public static readonly TimeSpan[] Steps = { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60) };
        private int _failures;

        public int Failures => _failures;
        public TimeSpan Next()
        {
            var d = Steps[Math.Min(_failures, Steps.Length - 1)];
            if (_failures < int.MaxValue) _failures++;
            return d;
        }
        public void Reset() => _failures = 0;
    }

    /// <summary>
    /// Spacing for SET_ACTIVITY. Discord allows 5 updates per 20 s per connection; this plugin sends an ordinary update at
    /// most once every 15 s. A clear (privacy: pause, hidden place, logout) and an explicit Test skip the 15 s spacing so
    /// they take effect at once, but every send still counts toward a cap of 3 per 20 s, well inside Discord's limit.
    /// </summary>
    internal sealed class ActivityRateLimiter
    {
        private readonly TimeSpan _minInterval;
        private readonly int _burstMax;
        private readonly TimeSpan _burstWindow;
        private readonly Queue<DateTime> _recent = new Queue<DateTime>();
        private DateTime? _last;

        public ActivityRateLimiter(TimeSpan? minInterval = null, int burstMax = 3, TimeSpan? burstWindow = null)
        {
            _minInterval = minInterval ?? TimeSpan.FromSeconds(15);
            _burstMax = Math.Max(1, burstMax);
            _burstWindow = burstWindow ?? TimeSpan.FromSeconds(20);
        }

        /// <summary>How long to wait before this send is allowed (zero = now).</summary>
        public TimeSpan WaitBeforeSend(DateTime nowUtc, bool priority)
        {
            while (_recent.Count > 0 && nowUtc - _recent.Peek() >= _burstWindow) _recent.Dequeue();
            var wait = TimeSpan.Zero;
            if (_recent.Count >= _burstMax) wait = _recent.Peek() + _burstWindow - nowUtc;
            if (!priority && _last.HasValue)
            {
                var spacing = _last.Value + _minInterval - nowUtc;
                if (spacing > wait) wait = spacing;
            }
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        public void Record(DateTime nowUtc)
        {
            _last = nowUtc;
            _recent.Enqueue(nowUtc);
        }

        /// <summary>A new connection starts with a fresh Discord-side budget.</summary>
        public void Reset() { _recent.Clear(); _last = null; }
    }

    /// <summary>A one-shot-at-a-time wake-up for the background loop. Capture <see cref="Task"/> before reading state.</summary>
    internal sealed class AsyncSignal
    {
        private readonly object _lock = new object();
        private TaskCompletionSource<bool> _tcs = NewTcs();

        private static TaskCompletionSource<bool> NewTcs() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task { get { lock (_lock) return _tcs.Task; } }

        public void Set()
        {
            TaskCompletionSource<bool> old;
            lock (_lock) { old = _tcs; _tcs = NewTcs(); }
            old.TrySetResult(true);
        }
    }

    /// <summary>
    /// Writes each distinct message at most once per window (default one minute), so a dead Discord or a repeating error
    /// cannot flood the Dungeon Helper log.
    /// </summary>
    internal sealed class ThrottledLog
    {
        private readonly Action<string, bool> _sink;
        private readonly Func<DateTime> _now;
        private readonly TimeSpan _window;
        private readonly Dictionary<string, DateTime> _last = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly object _lock = new object();

        public ThrottledLog(Action<string, bool> sink, Func<DateTime>? now = null, TimeSpan? window = null)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _now = now ?? (() => DateTime.UtcNow);
            _window = window ?? TimeSpan.FromMinutes(1);
        }

        public bool Info(string message) => Write(message, false);
        public bool Warn(string message) => Write(message, true);

        private bool Write(string message, bool warn)
        {
            if (string.IsNullOrEmpty(message)) return false;
            var now = _now();
            lock (_lock)
            {
                if (_last.TryGetValue(message, out var at) && now - at < _window) return false;
                if (_last.Count > 256)
                {
                    var stale = new List<string>();
                    foreach (var kv in _last) if (now - kv.Value >= _window) stale.Add(kv.Key);
                    foreach (var k in stale) _last.Remove(k);
                }
                _last[message] = now;
            }
            try { _sink(message, warn); } catch { }
            return true;
        }
    }
}

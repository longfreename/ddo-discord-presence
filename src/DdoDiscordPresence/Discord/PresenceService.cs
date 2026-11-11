using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DdoDiscordPresence.Presence;

namespace DdoDiscordPresence.Discord
{
    internal enum PresenceStatusKind
    {
        NotConfigured,
        Off,
        Connecting,
        DiscordNotRunning,
        Connected,
        Error,
        Stopped,
        /// <summary>Another game client's instance owns the Discord connection (multi-boxing).</summary>
        Delegated,
    }

    internal sealed record PresenceStatus(PresenceStatusKind Kind, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>
    /// Owns the Discord connection on one background task: connects (with 5/15/60 s back-off), sends SET_ACTIVITY only
    /// when the content changed and the rate limiter allows, clears the activity when there is nothing to show, and
    /// reconnects when Discord restarts. Callers only set inputs (<see cref="Configure"/>, <see cref="SetDesired"/>) and
    /// read <see cref="Status"/>; nothing here blocks the caller or throws into it.
    /// </summary>
    internal sealed class PresenceService : IDisposable
    {
        public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(20);

        private readonly IPipeConnector _connector;
        private readonly IClock _clock;
        private readonly ThrottledLog _log;
        private readonly int _pid;
        private readonly object _lock = new object();
        private readonly AsyncSignal _signal = new AsyncSignal();
        private readonly Backoff _backoff = new Backoff();
        private readonly ActivityRateLimiter _limiter;
        private readonly ActivityChangeDetector _detector = new ActivityChangeDetector();

        private string? _clientId;
        private bool _clearOnStop = true;
        private PresenceActivity? _desired;
        private TestRequest? _test;
        private DiscordRpcConnection? _conn;
        private string? _connClientId;
        private DateTime _nextAttemptUtc = DateTime.MinValue;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private PresenceStatus _status = new PresenceStatus(PresenceStatusKind.Off, "Off");
        private int _sendCount;

        private sealed class TestRequest
        {
            public string ClientId = "";
            public PresenceActivity Activity = new PresenceActivity();
            public DateTime UntilUtc;
            public bool Sent;
            public readonly TaskCompletionSource<string> Done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public PresenceService(IPipeConnector connector, IClock clock, ThrottledLog log, int pid, ActivityRateLimiter? limiter = null)
        {
            _connector = connector;
            _clock = clock;
            _log = log;
            _pid = pid;
            _limiter = limiter ?? new ActivityRateLimiter();
        }

        public event Action<PresenceStatus>? StatusChanged;

        public PresenceStatus Status { get { lock (_lock) return _status; } }
        /// <summary>The last reply Discord sent to SET_ACTIVITY (raw JSON), for diagnostics.</summary>
        public string? LastReply { get; private set; }
        /// <summary>The activity Discord last accepted from us (null = cleared / nothing).</summary>
        public PresenceActivity? Showing { get { lock (_lock) return _detector.LastSent; } }
        public int SendCount => Volatile.Read(ref _sendCount);
        public bool IsConnected { get { lock (_lock) return _conn != null && !_conn.Closed.IsCompleted; } }

        public void Start()
        {
            lock (_lock)
            {
                if (_loop != null) return;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _loop = Task.Run(() => RunAsync(token));
            }
        }

        /// <summary>The Application ID to use, or null for "do nothing" (clears and disconnects).</summary>
        public void Configure(string? clientId) => Configure(clientId, clearOnStop: true);

        /// <summary>
        /// Gives the Discord connection up WITHOUT clearing the activity: another instance (often in the same Dungeon
        /// Helper process, so with the same pid) is taking over and will set its own. Closing our pipe ends only the
        /// activity this connection set.
        /// </summary>
        public void StandDown() => Configure(null, clearOnStop: false);

        private void Configure(string? clientId, bool clearOnStop)
        {
            lock (_lock)
            {
                clientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId!.Trim();
                if (clientId == _clientId && _clearOnStop == clearOnStop) return;
                _clientId = clientId;
                _clearOnStop = clearOnStop;
                _nextAttemptUtc = DateTime.MinValue;
                _backoff.Reset();
            }
            _signal.Set();
        }

        /// <summary>What should be shown now; null = nothing (the activity is cleared).</summary>
        public void SetDesired(PresenceActivity? activity)
        {
            lock (_lock)
            {
                if (Equals(_desired, activity)) return;
                _desired = activity;
            }
            _signal.Set();
        }

        /// <summary>
        /// Sends <paramref name="sample"/> for 20 s with <paramref name="clientId"/> (connecting now, without waiting for
        /// the back-off) and returns exactly what Discord answered. Afterwards the normal presence is restored.
        /// </summary>
        public async Task<string> TestAsync(string clientId, PresenceActivity sample, TimeSpan? wait = null)
        {
            var req = new TestRequest { ClientId = clientId.Trim(), Activity = sample, UntilUtc = _clock.UtcNow + TestDuration };
            lock (_lock)
            {
                _test?.Done.TrySetResult("Replaced by a newer test.");
                _test = req;
                _nextAttemptUtc = DateTime.MinValue;
            }
            _signal.Set();
            var timeout = wait ?? TimeSpan.FromSeconds(15);
            var done = await Task.WhenAny(req.Done.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (done == req.Done.Task) return await req.Done.Task.ConfigureAwait(false);
            return "No answer from Discord within " + (int)timeout.TotalSeconds + " s. " + Status.Text;
        }

        private static readonly System.Text.RegularExpressions.Regex RetrySuffix =
            new System.Text.RegularExpressions.Regex(@" ?\(?(retrying|connection lost; retrying) in \d+ s\)?", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private void SetStatus(PresenceStatusKind kind, string text, bool warn = false)
        {
            var s = new PresenceStatus(kind, text);
            lock (_lock)
            {
                if (_status == s) return;
                _status = s;
            }
            // The log line leaves out the changing retry delay, so the once-a-minute throttle sees one message.
            if (kind != PresenceStatusKind.Connecting)
            {
                var line = "Discord: " + RetrySuffix.Replace(text, "").Trim();
                if (warn) _log.Warn(line); else _log.Info(line);
            }
            try { StatusChanged?.Invoke(s); } catch { }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await StepAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // Never escapes into the host. Drop the connection and retry after the back-off.
                    SetStatus(PresenceStatusKind.Error, "Error: " + ex.Message, warn: true);
                    await DropConnectionAsync().ConfigureAwait(false);
                    TimeSpan delay;
                    lock (_lock) { delay = _backoff.Next(); _nextAttemptUtc = _clock.UtcNow + delay; }
                    try { await WaitAsync(_signal.Task, null, delay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                }
            }
        }

        /// <summary>One decision of the loop. Each path ends in a send, a connect attempt, or a wait.</summary>
        internal async Task StepAsync(CancellationToken ct)
        {
            var wake = _signal.Task;          // captured BEFORE reading state, so no input change can be missed
            var now = _clock.UtcNow;
            string? clientId; PresenceActivity? desired; TestRequest? test; DiscordRpcConnection? conn; DateTime nextAttempt;
            lock (_lock)
            {
                if (_test != null && now >= _test.UntilUtc) _test = null;
                test = _test;
                clientId = test?.ClientId ?? _clientId;
                desired = test != null ? test.Activity : _desired;
                conn = _conn;
                nextAttempt = _nextAttemptUtc;
            }

            if (clientId == null)
            {
                bool clear;
                lock (_lock) clear = _clearOnStop;
                if (conn != null)
                {
                    if (clear) await ClearAndDropAsync(conn, ct).ConfigureAwait(false);
                    else await DropConnectionAsync().ConfigureAwait(false);
                }
                if (clear) SetStatus(PresenceStatusKind.Off, "Off");
                else SetStatus(PresenceStatusKind.Delegated, "Shown by another game client");
                await WaitAsync(wake, null, null, ct).ConfigureAwait(false);
                return;
            }

            if (conn != null && _connClientId != clientId)
            {
                // A different application (a Test with another ID, or the ID was changed): clear under the old one first.
                await ClearAndDropAsync(conn, ct).ConfigureAwait(false);
                return;
            }

            if (conn == null)
            {
                if (now < nextAttempt)
                {
                    await WaitAsync(wake, null, nextAttempt - now, ct).ConfigureAwait(false);
                    return;
                }
                await ConnectAsync(clientId, test, ct).ConfigureAwait(false);
                return;
            }

            if (conn.Closed.IsCompleted)
            {
                var info = await conn.Closed.ConfigureAwait(false);
                await DropConnectionAsync().ConfigureAwait(false);
                TimeSpan delay;
                lock (_lock) { delay = _backoff.Next(); _nextAttemptUtc = _clock.UtcNow + delay; }
                if (info.ByDiscord)
                    SetStatus(PresenceStatusKind.Error, "Error: Discord closed the connection: " + info.Describe() + $" (retrying in {(int)delay.TotalSeconds} s)", warn: true);
                else
                    SetStatus(PresenceStatusKind.DiscordNotRunning, $"Discord not running (connection lost; retrying in {(int)delay.TotalSeconds} s)");
                return;
            }

            bool shouldSend;
            lock (_lock) shouldSend = _detector.ShouldSend(desired) || test != null && !test.Sent;
            if (shouldSend)
            {
                var priority = desired == null || test != null && !test.Sent;
                TimeSpan wait;
                lock (_lock) wait = _limiter.WaitBeforeSend(now, priority);
                if (wait > TimeSpan.Zero)
                {
                    await WaitAsync(wake, conn.Closed, wait, ct).ConfigureAwait(false);
                    return;
                }
                await SendAsync(conn, desired, test, ct).ConfigureAwait(false);
                return;
            }

            TimeSpan? until = test != null ? test.UntilUtc - now : (TimeSpan?)null;
            await WaitAsync(wake, conn.Closed, until, ct).ConfigureAwait(false);
        }

        private async Task ConnectAsync(string clientId, TestRequest? test, CancellationToken ct)
        {
            SetStatus(PresenceStatusKind.Connecting, "Connecting to Discord...");
            try
            {
                var conn = await DiscordRpcConnection.OpenAsync(_connector, clientId, ConnectTimeout, ct).ConfigureAwait(false);
                lock (_lock)
                {
                    _conn = conn;
                    _connClientId = clientId;
                    _backoff.Reset();
                    _limiter.Reset();
                    _detector.ResetForNewConnection();
                }
                var who = conn.User.Display;
                SetStatus(PresenceStatusKind.Connected, string.IsNullOrEmpty(who) ? "Connected to Discord" : "Connected as " + who);
                _signal.Set();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                TimeSpan delay;
                lock (_lock) { delay = _backoff.Next(); _nextAttemptUtc = _clock.UtcNow + delay; }
                string text;
                PresenceStatusKind kind;
                switch (ex)
                {
                    case DiscordNotRunningException _:
                        kind = PresenceStatusKind.DiscordNotRunning;
                        text = $"Discord not running (retrying in {(int)delay.TotalSeconds} s)";
                        break;
                    case DiscordRpcException rpc:
                        kind = PresenceStatusKind.Error;
                        text = "Error: " + DiscordRpcException.Describe(rpc.Code, rpc.DiscordMessage) + (rpc.Code == 4000 ? " - check the Application ID" : "");
                        break;
                    default:
                        kind = PresenceStatusKind.Error;
                        text = "Error: " + ex.Message;
                        break;
                }
                SetStatus(kind, text, warn: kind == PresenceStatusKind.Error);
                test?.Done.TrySetResult(kind == PresenceStatusKind.DiscordNotRunning
                    ? "Discord is not running (no Discord pipe answered). Start Discord desktop and try again."
                    : "Discord refused the connection: " + text.Replace("Error: ", ""));
                lock (_lock) { if (test != null && ReferenceEquals(_test, test)) _test = null; }
            }
        }

        private async Task SendAsync(DiscordRpcConnection conn, PresenceActivity? activity, TestRequest? test, CancellationToken ct)
        {
            RpcReply reply;
            try
            {
                reply = await conn.SetActivityAsync(_pid, activity?.ToJson(), CommandTimeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is TimeoutException || ex is IOException || ex is ObjectDisposedException)
            {
                lock (_lock) { _limiter.Record(_clock.UtcNow); _detector.Forget(); }
                test?.Done.TrySetResult("Sending the test failed: " + ex.Message);
                if (test != null) test.Sent = true;
                SetStatus(PresenceStatusKind.Error, "Error: " + ex.Message, warn: true);
                await DropConnectionAsync().ConfigureAwait(false);
                return;
            }
            Interlocked.Increment(ref _sendCount);
            LastReply = reply.Raw;
            lock (_lock)
            {
                _limiter.Record(_clock.UtcNow);
                // Recorded even on an error reply, so the same rejected content is not resent in a loop.
                _detector.MarkSent(activity);
            }
            if (test != null)
            {
                test.Sent = true;
                test.Done.TrySetResult(reply.Ok
                    ? "Discord accepted the test presence. It shows on your profile for 20 seconds. Raw reply: " + Shorten(reply.Raw)
                    : "Discord answered with an error: " + DiscordRpcException.Describe(reply.Code, reply.Message) + ". Raw reply: " + Shorten(reply.Raw));
            }
            if (reply.Ok)
            {
                var who = conn.User.Display;
                SetStatus(PresenceStatusKind.Connected, string.IsNullOrEmpty(who) ? "Connected to Discord" : "Connected as " + who);
            }
            else
            {
                SetStatus(PresenceStatusKind.Error, "Error: " + DiscordRpcException.Describe(reply.Code, reply.Message), warn: true);
            }
        }

        private static string Shorten(string s) => s.Length <= 300 ? s : s.Substring(0, 300) + "...";

        private async Task ClearAndDropAsync(DiscordRpcConnection conn, CancellationToken ct)
        {
            bool needsClear;
            lock (_lock) needsClear = _detector.LastSent != null;
            if (needsClear && !conn.Closed.IsCompleted)
            {
                try
                {
                    await conn.SetActivityAsync(_pid, null, TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _sendCount);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }
            await DropConnectionAsync().ConfigureAwait(false);
        }

        private async Task DropConnectionAsync()
        {
            DiscordRpcConnection? conn;
            lock (_lock) { conn = _conn; _conn = null; _connClientId = null; _detector.Forget(); }
            if (conn != null) { try { await conn.DisposeAsync().ConfigureAwait(false); } catch { } }
        }

        /// <summary>Waits for an input change, the connection closing, or the delay, whichever is first.</summary>
        private async Task WaitAsync(Task wake, Task? closed, TimeSpan? delay, CancellationToken ct)
        {
            // A per-wait token: whatever finishes first, the other timers/registrations are released at once, so a long
            // session does not pile up pending delays.
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tasks = new System.Collections.Generic.List<Task>(4) { wake, Task.Delay(Timeout.Infinite, waitCts.Token) };
            if (closed != null) tasks.Add(closed);
            if (delay.HasValue) tasks.Add(_clock.Delay(delay.Value < TimeSpan.Zero ? TimeSpan.Zero : delay.Value, waitCts.Token));
            await Task.WhenAny(tasks).ConfigureAwait(false);
            try { waitCts.Cancel(); } catch { }
            ct.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Stops the loop and clears the activity best-effort. The whole call finishes within <paramref name="cap"/>.
        /// (Discord also clears an activity on its own when the pipe closes or the process exits.)
        /// </summary>
        public async Task StopAsync(TimeSpan cap, bool clear = true)
        {
            Task? loop; CancellationTokenSource? cts;
            lock (_lock) { loop = _loop; cts = _cts; _loop = null; _cts = null; }
            var work = Task.Run(async () =>
            {
                try { cts?.Cancel(); } catch { }
                if (loop != null) { try { await loop.ConfigureAwait(false); } catch { } }
                DiscordRpcConnection? conn;
                bool needsClear;
                lock (_lock) { conn = _conn; needsClear = _detector.LastSent != null; }
                if (clear && conn != null && needsClear && !conn.Closed.IsCompleted)
                {
                    try { await conn.SetActivityAsync(_pid, null, cap, CancellationToken.None).ConfigureAwait(false); } catch { }
                }
                await DropConnectionAsync().ConfigureAwait(false);
            });
            await Task.WhenAny(work, Task.Delay(cap)).ConfigureAwait(false);
            try { cts?.Dispose(); } catch { }
            lock (_lock) { _test?.Done.TrySetResult("Stopped."); _test = null; }
            SetStatus(PresenceStatusKind.Stopped, "Stopped");
        }

        public void Dispose()
        {
            try { StopAsync(TimeSpan.FromSeconds(1)).Wait(TimeSpan.FromMilliseconds(1200)); } catch { }
        }
    }
}

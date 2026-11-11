using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DdoDiscordPresence.Discord;
using DdoDiscordPresence.Presence;

namespace DdoDiscordPresence.Coordination
{
    /// <summary>
    /// One game client's heartbeat record. Dungeon Helper runs one plugin instance per game client (each in its own
    /// assembly load context, so no static is shared), and a player may also run several Dungeon Helpers; all of them meet
    /// through these records.
    /// </summary>
    internal sealed record ParticipantEntry
    {
        public int V { get; init; } = 1;
        public string InstanceId { get; init; } = "";
        public int Pid { get; init; }
        public DateTime StartedUtc { get; init; }
        public DateTime HeartbeatUtc { get; init; }
        /// <summary>Sharing is on with a usable application id: this instance may lead.</summary>
        public bool Eligible { get; init; }
        public string ApplicationId { get; init; } = "";
        public Contribution Contribution { get; init; } = new Contribution();
        /// <summary>Set by the leader: its Discord status line, so the other clients can show it.</summary>
        public string? LeaderStatus { get; init; }
    }

    /// <summary>The shared place where participants publish their records.</summary>
    internal interface IParticipantStore
    {
        void Write(ParticipantEntry entry);
        void Remove(string instanceId);
        IReadOnlyList<ParticipantEntry> ReadAll();
    }

    /// <summary>
    /// One small JSON file per participant in a per-user temp folder (Windows' %TEMP% is per user, as is the Discord
    /// client): written atomically (temp file + rename), read by everyone, deleted on logout or exit. Files older than a
    /// minute are cleaned up by whoever sees them, so a crashed client leaves nothing behind for long. No sockets, no
    /// shared memory, nothing that can deadlock between two hosts.
    /// </summary>
    internal sealed class FileParticipantStore : IParticipantStore
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        private readonly Func<DateTime> _now;

        public FileParticipantStore(string folder, Func<DateTime>? now = null)
        {
            Folder = folder;
            _now = now ?? (() => DateTime.UtcNow);
        }

        public string Folder { get; }

        /// <summary>
        /// %TEMP%\DdoDiscordPresence\&lt;hash of the application id&gt;: clients publishing through different Discord
        /// applications never merge. The environment variable DDO_DISCORD_PRESENCE_COORDINATION overrides the root (tests).
        /// </summary>
        public static string DefaultFolder(string applicationId)
        {
            var root = Environment.GetEnvironmentVariable("DDO_DISCORD_PRESENCE_COORDINATION");
            if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Path.GetTempPath(), "DdoDiscordPresence");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(applicationId ?? ""))).Substring(0, 12).ToLowerInvariant();
            return Path.Combine(root, "participants-" + hash);
        }

        private string FileFor(string id) => Path.Combine(Folder, Sanitize(id) + ".json");

        private static string Sanitize(string id)
        {
            var sb = new StringBuilder();
            foreach (var c in id) if (char.IsLetterOrDigit(c) || c == '-') sb.Append(c);
            return sb.Length == 0 ? "unknown" : sb.ToString();
        }

        public void Write(ParticipantEntry entry)
        {
            Directory.CreateDirectory(Folder);
            var path = FileFor(entry.InstanceId);
            var tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(entry, Options), new UTF8Encoding(false));
                File.Move(tmp, path, overwrite: true);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        public void Remove(string instanceId)
        {
            try { File.Delete(FileFor(instanceId)); } catch { }
        }

        public IReadOnlyList<ParticipantEntry> ReadAll()
        {
            var list = new List<ParticipantEntry>();
            if (!Directory.Exists(Folder)) return list;
            var now = _now();
            foreach (var file in Directory.EnumerateFiles(Folder))
            {
                try
                {
                    if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        if (now - File.GetLastWriteTimeUtc(file) > TimeSpan.FromMinutes(1)) File.Delete(file);
                        continue;
                    }
                    if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    var entry = JsonSerializer.Deserialize<ParticipantEntry>(File.ReadAllText(file), Options);
                    if (entry == null || string.IsNullOrEmpty(entry.InstanceId)) continue;
                    if (now - entry.HeartbeatUtc > Election.Forget) { try { File.Delete(file); } catch { } continue; }
                    list.Add(entry);
                }
                catch { /* half-written or locked: skip it this round */ }
            }
            return list;
        }
    }

    /// <summary>In-memory store for tests (several "processes" sharing one object).</summary>
    internal sealed class MemoryParticipantStore : IParticipantStore
    {
        private readonly Dictionary<string, ParticipantEntry> _entries = new Dictionary<string, ParticipantEntry>();
        public void Write(ParticipantEntry entry) { lock (_entries) _entries[entry.InstanceId] = entry; }
        public void Remove(string instanceId) { lock (_entries) _entries.Remove(instanceId); }
        public IReadOnlyList<ParticipantEntry> ReadAll() { lock (_entries) return _entries.Values.ToList(); }
    }

    /// <summary>
    /// Leader election, a pure function every participant evaluates on the same records: the eligible participant that
    /// started first (ties by instance id) and has a heartbeat younger than <see cref="LeaderStale"/>. A newcomer may lead
    /// only after <see cref="Grace"/>, by which time it has seen everyone's heartbeat, so two clients starting together
    /// still agree. A leader that exits deletes its record (takeover on the others' next tick, about 2 s); one that dies
    /// silently is replaced once its heartbeat is <see cref="LeaderStale"/> old (at most about 10 s).
    /// </summary>
    internal static class Election
    {
        public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan LeaderStale = TimeSpan.FromSeconds(8);
        /// <summary>A record without a heartbeat for this long no longer counts at all (its character's name drops out).</summary>
        public static readonly TimeSpan Expire = TimeSpan.FromSeconds(15);
        /// <summary>Records this old are deleted from the folder.</summary>
        public static readonly TimeSpan Forget = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

        public static ParticipantEntry? Elect(IEnumerable<ParticipantEntry> entries, DateTime nowUtc,
            TimeSpan? leaderStale = null, TimeSpan? grace = null)
        {
            var stale = leaderStale ?? LeaderStale;
            var g = grace ?? Grace;
            return entries
                .Where(e => e.Eligible && nowUtc - e.HeartbeatUtc <= stale && nowUtc - e.StartedUtc >= g)
                .OrderBy(e => e.StartedUtc).ThenBy(e => e.InstanceId, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        public static IReadOnlyList<ParticipantEntry> Live(IEnumerable<ParticipantEntry> entries, DateTime nowUtc, TimeSpan? expire = null) =>
            entries.Where(e => nowUtc - e.HeartbeatUtc <= (expire ?? Expire)).ToList();
    }

    /// <summary>What one tick decided, for the panel.</summary>
    internal sealed record NodeView
    {
        public bool IsLeader { get; init; }
        public string? LeaderId { get; init; }
        public string? LeaderStatus { get; init; }
        public IReadOnlyList<ParticipantEntry> Participants { get; init; } = Array.Empty<ParticipantEntry>();
        public PresenceResult Result { get; init; } = PresenceResult.Nothing("Starting");
    }

    /// <summary>
    /// One game client's part in the shared status: publishes its contribution, takes part in the election, and when it
    /// is the leader owns the Discord connection and sends the merged status. Only the leader's
    /// <see cref="PresenceService"/> ever connects; the others stand down without clearing (same pid in one host).
    /// </summary>
    internal sealed class PresenceNode
    {
        private readonly IParticipantStore _store;
        private readonly PresenceService _service;
        private readonly IClock _clock;
        private readonly TimeSpan _leaderStale, _grace, _expire;
        private bool _wasLeader;
        private bool _joined;

        public PresenceNode(IParticipantStore store, PresenceService service, IClock clock, string instanceId, int pid,
            TimeSpan? leaderStale = null, TimeSpan? grace = null, TimeSpan? expire = null)
        {
            _store = store;
            _service = service;
            _clock = clock;
            InstanceId = instanceId;
            Pid = pid;
            StartedUtc = clock.UtcNow;
            _leaderStale = leaderStale ?? Election.LeaderStale;
            _grace = grace ?? Election.Grace;
            _expire = expire ?? Election.Expire;
        }

        public string InstanceId { get; }
        public int Pid { get; }
        public DateTime StartedUtc { get; }
        public NodeView View { get; private set; } = new NodeView();

        /// <summary>One heartbeat: publish, elect, and (as leader) send the merged status.</summary>
        public NodeView Tick(GameSnapshot game, PresenceConfig config, bool foreground)
        {
            var now = _clock.UtcNow;
            var own = PresenceFormatter.Contribute(game, config, now, InstanceId, StartedUtc, foreground);
            var eligible = config.SharingEnabled && game.LoggedIn;
            var appId = config.EffectiveApplicationId;
            var mine = new ParticipantEntry
            {
                InstanceId = InstanceId, Pid = Pid, StartedUtc = StartedUtc, HeartbeatUtc = now, Eligible = eligible,
                ApplicationId = appId, Contribution = own, LeaderStatus = _wasLeader ? _service.Status.Text : null,
            };
            if (game.LoggedIn) { try { _store.Write(mine); _joined = true; } catch { } }
            else Leave();

            IReadOnlyList<ParticipantEntry> all;
            try { all = _store.ReadAll(); } catch { all = Array.Empty<ParticipantEntry>(); }
            var live = Election.Live(all.Where(e => e.InstanceId != InstanceId), now, _expire).ToList();
            if (game.LoggedIn) live.Add(mine);
            live = live.Where(e => e.ApplicationId == appId).ToList();

            var leader = Election.Elect(live, now, _leaderStale, _grace);
            var isLeader = leader?.InstanceId == InstanceId;
            var merged = PresenceFormatter.Merge(live.Select(e => e.Contribution).ToList(), leader?.InstanceId ?? InstanceId, config);
            if (!game.LoggedIn && !live.Any()) merged = PresenceResult.Nothing(own.Reason);

            if (isLeader)
            {
                _service.Configure(appId);
                _service.SetDesired(merged.Activity);
            }
            else if (leader != null)
            {
                _service.StandDown();     // someone else shows it; do not clear their status (same pid)
            }
            else
            {
                // Nobody leads (sharing off everywhere, or everyone just started): the last leader clears and disconnects.
                if (_wasLeader && config.SharingEnabled && game.LoggedIn) { /* within the start-up grace: keep what is shown */ }
                else _service.Configure(null);
            }
            _wasLeader = isLeader || (leader == null && _wasLeader && config.SharingEnabled && game.LoggedIn);

            View = new NodeView
            {
                IsLeader = isLeader,
                LeaderId = leader?.InstanceId,
                LeaderStatus = isLeader ? _service.Status.Text : leader?.LeaderStatus,
                Participants = live.OrderBy(e => e.StartedUtc).ThenBy(e => e.InstanceId, StringComparer.Ordinal).ToList(),
                Result = merged,
            };
            return View;
        }

        /// <summary>Logout or exit: the record goes at once, so the others drop this character and re-elect on their next tick.</summary>
        public void Leave()
        {
            if (!_joined) return;
            _joined = false;
            try { _store.Remove(InstanceId); } catch { }
        }

        /// <summary>
        /// Whether, on leaving, this leader should clear the status itself: only when no other live sharing client will take
        /// over (otherwise the new leader replaces it, and a clear from this pid could wipe theirs).
        /// </summary>
        public bool ShouldClearOnLeave()
        {
            var now = _clock.UtcNow;
            IReadOnlyList<ParticipantEntry> all;
            try { all = _store.ReadAll(); } catch { return true; }
            return !Election.Live(all, now, _expire).Any(e => e.InstanceId != InstanceId && e.Eligible);
        }

        public bool WasLeader => _wasLeader;
    }
}

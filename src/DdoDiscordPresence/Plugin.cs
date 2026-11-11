using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DdoDiscordPresence.Coordination;
using DdoDiscordPresence.Discord;
using DdoDiscordPresence.Game;
using DdoDiscordPresence.Presence;
using log4net;
using VoK.Sdk;
using VoK.Sdk.Ddo;
using VoK.Sdk.Plugins;

namespace DdoDiscordPresence
{
    /// <summary>
    /// DDO Discord Presence: shows the current quest (or town / public area) as the player's Discord status.
    ///
    /// Dungeon Helper creates one instance of this class per game client (each in its own assembly load context), all in
    /// one process; a player can also run several Dungeon Helpers. Every logged-in instance publishes its part through
    /// <see cref="PresenceNode"/>; exactly one (the leader) talks to Discord and shows the merged status.
    /// </summary>
    public sealed class Plugin : IDdoPlugin
    {
        public Guid PluginId => Guid.Parse("4b9e2c71-3d58-4a0f-8e26-91c5d7f3a604");
        public GameId Game => GameId.DDO;
        public string PluginKey => "e3a17f52-6c09-4d8b-b5e4-0f2a9c6d1b73";
        public string Name => "DDO Discord Presence";
        public string Description => "Shows your current DDO quest or area as your Discord status (Rich Presence)";
        /// <summary>Shown in Dungeon Helper's plugin list.</summary>
        public string Author => AuthorName;
        internal const string AuthorName = "longfreename";
        public Version Version => GetType().Assembly.GetName().Version ?? new Version(0, 0, 0);

        /// <summary>Heartbeat / coordination tick while logged in.</summary>
        internal static readonly TimeSpan TickInterval = Election.Heartbeat;
        /// <summary>How often the game itself is read.</summary>
        internal static readonly TimeSpan ReadInterval = TimeSpan.FromSeconds(5);
        /// <summary>After a map area change the SDK's values settle for a moment; read again after this delay.</summary>
        internal static readonly TimeSpan AreaSettle = TimeSpan.FromMilliseconds(1500);

        private static readonly ILog Log = LogManager.GetLogger("DdoDiscordPresence");

        private readonly object _lock = new object();
        private readonly object _nodeLock = new object();
        private readonly PlaceTracker _place = new PlaceTracker();
        private readonly string _instanceId = Guid.NewGuid().ToString("N");
        private IDdoGameDataProvider? _provider;
        private ConfigStore? _store;
        private PresenceConfig _config = PresenceConfig.NewInstall();
        private PresenceService? _service;
        private PresenceNode? _node;
        private ThrottledLog _log;
        private GameSnapshot _snapshot = GameSnapshot.LoggedOut;
        private GameDiagnostics _diag = new GameDiagnostics();
        // The difficulty the quest entry panel named. Kept until the character has been in a quest and left it.
        private uint? _portalDifficulty;
        private bool _portalBound;
        private string _lastReadingLine = "";
        // What the monsters said for the instance the character is in, kept while no monster is in sight.
        private string? _knownPlace;
        private Difficulty _knownDifficulty;
        private bool _knownCertain;
        private bool? _knownEpic;
        private bool _entryLoadTried;
        private NodeView _view = new NodeView();
        private DateTime? _lastAreaChangeUtc;
        private DateTime _lastReadUtc = DateTime.MinValue;
        private bool _forceRead;
        private Timer? _timer;
        private int _ticking;
        private PluginUI? _ui;
        private volatile bool _terminated;

        /// <summary>Dungeon Helper also creates instances only to list the plugin; the constructor therefore does nothing.</summary>
        public Plugin()
        {
            _log = new ThrottledLog((m, warn) => { if (warn) Log.Warn(m); else Log.Info(m); });
        }

        /// <summary>Fires (on any thread) when the presence, status, settings or diagnostics change.</summary>
        internal event Action? StateChanged;

        internal string Folder { get; private set; } = ".";
        internal PresenceConfig Config { get { lock (_lock) return _config; } }
        internal PresenceResult Result { get { lock (_lock) return _view.Result; } }
        internal NodeView View { get { lock (_lock) return _view; } }
        internal GameSnapshot Snapshot { get { lock (_lock) return _snapshot; } }
        internal GameDiagnostics Diagnostics { get { lock (_lock) return _diag; } }
        internal string? ConfigWarning { get; private set; }
        internal string InstanceId => _instanceId;

        public void Initialize(IDdoGameDataProvider gameDataProvider, string folder)
        {
            try
            {
                _provider = gameDataProvider;
                Folder = ResolveStorageFolder(folder);
                _store = new ConfigStore(Path.Combine(Folder, ConfigStore.FileName));
                var (cfg, warning) = _store.Load();
                ConfigWarning = warning;
                if (warning != null) _log.Warn(warning);
                lock (_lock) _config = cfg;

                // DDO_DISCORD_PRESENCE_PIPE_PREFIX: test hook (the test suite and render harness point it at a pipe that does
                // not exist, so they can never reach the real Discord).
                var prefix = Environment.GetEnvironmentVariable("DDO_DISCORD_PRESENCE_PIPE_PREFIX");
                var connector = string.IsNullOrWhiteSpace(prefix) ? new NamedPipeConnector() : new NamedPipeConnector(prefix!, 1);
                _service = new PresenceService(connector, SystemClock.Instance, _log, Environment.ProcessId);
                _service.StatusChanged += _ => RaiseChanged();
                _service.Start();
                _node = new PresenceNode(new FileParticipantStore(FileParticipantStore.DefaultFolder(cfg.EffectiveApplicationId)),
                    _service, SystemClock.Instance, _instanceId, Environment.ProcessId);

                // The toolbar icon is drawn first inside PluginUI: Dungeon Helper reads it while plugins start.
                _ui = new PluginUI(this);

                var ev = gameDataProvider?.EventProvider;
                if (ev != null)
                {
                    TryAdd(() => ev.OnLogin.AddHandler(_ => { OnLogin(); return Task.CompletedTask; }));
                    TryAdd(() => ev.OnLogout.AddHandler(() => { OnLogout(); return Task.CompletedTask; }));
                    TryAdd(() => ev.OnMapAreaChange.AddHandler(_ => { OnMapAreaChange(); return Task.CompletedTask; }));
                    TryAdd(() => ev.OnLevelup.AddHandler(_ => { RequestRead(AreaSettle); return Task.CompletedTask; }));
                    TryAdd(() => ev.OnPortalActivate.AddHandler(i => { OnPortal("open", i); return Task.CompletedTask; }));
                    TryAdd(() => ev.OnPortalActivateReadOnly.AddHandler(i => { OnPortal("open (read only)", i); return Task.CompletedTask; }));
                    TryAdd(() => ev.OnPortalClose.AddHandler(i => { OnPortal("close", i); return Task.CompletedTask; }));
                }

                // Dungeon Helper can start after login.
                ulong charId = 0;
                try { charId = gameDataProvider?.GetCurrentCharacterId() ?? 0; } catch { }
                if (charId != 0) OnLogin();
                else RunNode();

                _log.Info($"DDO Discord Presence {Version.ToString(3)} initialized; " +
                          (cfg.NeedsFirstRunAnswer ? "waiting for the first-run choice (sharing off)" : cfg.Enabled ? "sharing on" : "sharing off"));
            }
            catch (Exception ex)
            {
                try { Log.Error("DDO Discord Presence failed to initialize: " + ex.Message, ex); } catch { }
            }
        }

        private void TryAdd(Action register)
        {
            try { register(); } catch (Exception ex) { _log.Warn("Event not available: " + ex.Message); }
        }

        internal static string ResolveStorageFolder(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return ".";
            var full = Path.GetFullPath(folder);
            var leaf = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!leaf.Equals("win-x64", StringComparison.OrdinalIgnoreCase)) return full;
            return Directory.GetParent(full)?.FullName ?? full;
        }

        public IPluginUI? GetPluginUI() => _ui;

        // ------------------------------------------------------------------ game events

        private void OnLogin()
        {
            if (_terminated) return;
            lock (_lock)
            {
                _place.Reset();
                _forceRead = true;
                _timer ??= new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
                try { _timer.Change(TimeSpan.Zero, TickInterval); } catch { }
            }
        }

        private void OnLogout()
        {
            lock (_lock)
            {
                var t = _timer; _timer = null;
                try { t?.Dispose(); } catch { }
                _place.Reset();
                _snapshot = GameSnapshot.LoggedOut;
                _diag = new GameDiagnostics();
                _lastAreaChangeUtc = null;
            }
            // One last tick as logged out: removes this character's record and hands the status to another client, or
            // clears it when this was the only one. No timer runs while logged out.
            RunNode();
        }

        /// <summary>The quest entry panel: it is the one place the game names the difficulty of the instance.</summary>
        private void OnPortal(string what, VoK.Sdk.Ddo.IPortalInfo? info)
        {
            if (_terminated || info == null) return;
            uint? named = null; uint? instance = null; int quests = 0; bool bestowed = false;
            try { named = info.QuestGenericDifficulty; } catch { }
            try { instance = info.ActiveInstanceDid; } catch { }
            try { quests = info.AvailableQuests?.Count ?? 0; } catch { }
            try { bestowed = info.QuestBestowed; } catch { }
            Log.Info($"Quest entry panel {what}: difficulty property={(named.HasValue ? "0x" + named.Value.ToString("X8") : "-")} " +
                     $"({(named.HasValue ? DifficultyMap.FromDifficultyProperty(named.Value).ToString() : "-")}) " +
                     $"instance={(instance.HasValue ? "0x" + instance.Value.ToString("X8") : "-")} quests={quests} bestowed={bestowed}");
            if (named.HasValue && DifficultyMap.FromDifficultyProperty(named.Value) != Difficulty.Unknown)
            {
                lock (_lock) { _portalDifficulty = named; _portalBound = false; }
                RequestRead(AreaSettle);
            }
        }

        // What the entry panel said for the quest a character is in, kept in the plugin's folder so that a Dungeon
        // Helper restart in the middle of a run does not lose the difficulty. One tiny file per character:
        // "<quest id> <difficulty property> <UTC ticks>". Ignored after six hours or for any other quest.
        private static readonly TimeSpan EntryMaxAge = TimeSpan.FromHours(6);

        private string? EntryPath(ulong characterId) =>
            characterId == 0 || string.IsNullOrEmpty(Folder) ? null : Path.Combine(Folder, "DdoDiscordPresence.entry." + characterId.ToString("X16") + ".txt");

        private void SaveEntry(ulong characterId, uint questDid, uint difficultyProperty)
        {
            try
            {
                var path = EntryPath(characterId);
                if (path == null || questDid == 0) return;
                File.WriteAllText(path, questDid.ToString("X8") + " " + difficultyProperty.ToString("X8") + " " +
                                        DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { }
        }

        private uint? LoadEntry(ulong characterId, uint questDid)
        {
            try
            {
                var path = EntryPath(characterId);
                if (path == null || questDid == 0 || !File.Exists(path)) return null;
                var parts = File.ReadAllText(path).Split(' ');
                if (parts.Length != 3) return null;
                var quest = Convert.ToUInt32(parts[0], 16);
                var prop = Convert.ToUInt32(parts[1], 16);
                var at = new DateTime(long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
                var age = DateTime.UtcNow - at;
                if (quest != questDid || age < TimeSpan.Zero || age > EntryMaxAge) return null;
                return DifficultyMap.FromDifficultyProperty(prop) == Difficulty.Unknown ? (uint?)null : prop;
            }
            catch { return null; }
        }

        private void ForgetEntry(ulong characterId)
        {
            try { var path = EntryPath(characterId); if (path != null && File.Exists(path)) File.Delete(path); } catch { }
        }

        private void OnMapAreaChange()
        {
            lock (_lock) _lastAreaChangeUtc = DateTime.UtcNow;
            RequestRead(AreaSettle);
        }

        private void RequestRead(TimeSpan due)
        {
            lock (_lock)
            {
                if (_terminated || _timer == null) return;
                _forceRead = true;
                try { _timer.Change(due, TickInterval); } catch { }
            }
        }

        private void Tick()
        {
            if (_terminated || Interlocked.Exchange(ref _ticking, 1) == 1) return;
            try
            {
                ReloadConfigIfChanged();
                var now = DateTime.UtcNow;
                bool read;
                lock (_lock) read = _forceRead || now - _lastReadUtc >= ReadInterval;
                if (read)
                {
                    uint? portal;
                    lock (_lock) portal = _portalDifficulty;
                    var (snap, diag) = GameReader.Read(_provider, portal);
                    // Dungeon Helper restarted inside a quest: take back what the entry panel said for this run.
                    if (snap.InQuest && !portal.HasValue && !_entryLoadTried)
                    {
                        _entryLoadTried = true;
                        var saved = LoadEntry(diag.CharacterId, snap.InstanceQuestDid);
                        if (saved.HasValue)
                        {
                            lock (_lock) { _portalDifficulty = saved; _portalBound = true; }
                            portal = saved;
                            (snap, diag) = GameReader.Read(_provider, portal);
                        }
                    }
                    else if (snap.LoggedIn && !snap.InQuest) _entryLoadTried = false;
                    // Remember the monsters' verdict for this quest: rooms without monsters must not blank it.
                    if (snap.InQuest)
                    {
                        if (_knownPlace != snap.PlaceKey) { _knownPlace = snap.PlaceKey; _knownDifficulty = Difficulty.Unknown; _knownCertain = false; _knownEpic = null; }
                        if (snap.EpicSide.HasValue) _knownEpic = snap.EpicSide;
                        else if (_knownEpic.HasValue) snap = snap with { EpicSide = _knownEpic };
                        if (diag.MonsterDifficulty != Difficulty.Unknown && (diag.MonsterDifficultyCertain || !_knownCertain))
                        { _knownDifficulty = diag.MonsterDifficulty; _knownCertain = diag.MonsterDifficultyCertain; }
                        if (snap.Difficulty == Difficulty.Unknown && _knownDifficulty != Difficulty.Unknown)
                            snap = snap with { Difficulty = _knownDifficulty };
                        else if (snap.Difficulty != Difficulty.Reaper && _knownCertain && snap.Difficulty != _knownDifficulty)
                            snap = snap with { Difficulty = _knownDifficulty };
                    }
                    else _knownPlace = null;
                    var line = $"quest={(snap.InQuest ? snap.QuestName ?? "?" : "-")} shown difficulty={snap.Difficulty} " +
                               $"side={(snap.EpicSide == null ? "-" : snap.EpicSide == true ? "epic" : "heroic")} " +
                               $"levels={snap.QuestLevelHeroic?.ToString() ?? "-"}/{snap.QuestLevelEpic?.ToString() ?? "-"} " +
                               $"source={diag.DifficultySource} reaper={diag.ReaperRaw?.ToString() ?? "-"} " +
                               $"entry panel={(diag.PortalDifficulty.HasValue ? "0x" + diag.PortalDifficulty.Value.ToString("X8") : "-")} " +
                               $"monsters={diag.MonsterDifficulty}{(diag.MonsterDifficultyCertain ? "" : "?")}";
                    if (line != _lastReadingLine) { _lastReadingLine = line; Log.Info("Difficulty reading: " + line); }
                    lock (_lock)
                    {
                        if (_timer == null) return;       // logged out meanwhile
                        _forceRead = false;
                        _lastReadUtc = now;
                        _diag = diag;
                        if (snap.InQuest && portal.HasValue)
                        {
                            if (!_portalBound) SaveEntry(diag.CharacterId, snap.InstanceQuestDid, portal.Value);
                            _portalBound = true;
                        }
                        else if (snap.LoggedIn && !snap.InQuest && _portalBound)
                        {
                            _portalDifficulty = null; _portalBound = false;
                            ForgetEntry(diag.CharacterId);
                        }
                        // While a zone loads the map reports area 0: keep the last place instead of flickering.
                        if (snap.LoggedIn && snap.AreaDid == 0 && !snap.InQuest && _snapshot.LoggedIn) { }
                        else if (snap.LoggedIn)
                        {
                            var since = _place.Update(snap.PlaceKey, now, _lastAreaChangeUtc);
                            _snapshot = snap with { PlaceSinceUtc = since };
                        }
                        else { _snapshot = snap; _place.Reset(); }
                    }
                }
                RunNode();
            }
            catch (Exception ex) { _log.Warn("Update failed: " + ex.Message); }
            finally { Interlocked.Exchange(ref _ticking, 0); }
        }

        /// <summary>Publishes this client's part, elects, and (as leader) sends the merged status.</summary>
        private void RunNode()
        {
            var node = _node;
            if (node == null) return;
            GameSnapshot snap; PresenceConfig cfg;
            lock (_lock) { snap = _snapshot; cfg = _config; }
            NodeView view;
            var fg = IsForeground();
            lock (_nodeLock)
            {
                try { view = node.Tick(snap, cfg, fg); }
                catch (Exception ex) { _log.Warn("Coordination failed: " + ex.Message); return; }
            }
            lock (_lock) _view = view;
            RaiseChanged();
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        /// <summary>This client's game window is the foreground window (cheap; false when unknown).</summary>
        private bool IsForeground()
        {
            try
            {
                var h = _provider?.GetGameWindowHandle() ?? IntPtr.Zero;
                return h != IntPtr.Zero && h == GetForegroundWindow();
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ settings

        /// <summary>Another game client saved the shared settings: take them over so both follow the owner's toggles.</summary>
        private void ReloadConfigIfChanged()
        {
            var store = _store;
            if (store == null || !store.Changed()) return;
            var (cfg, warning) = store.Load();
            if (warning != null) _log.Warn(warning);
            bool changed;
            lock (_lock) { changed = !_config.SameAs(cfg); _config = cfg; }
            if (changed) { _log.Info("Settings changed by another game client; reloaded."); RaiseChanged(); }
        }

        /// <summary>One settings change: reload the latest file (another client may have saved), change, save atomically.</summary>
        internal void UpdateConfig(Action<PresenceConfig> change)
        {
            PresenceConfig next;
            try { ReloadConfigIfChanged(); } catch { }
            lock (_lock)
            {
                next = _config.Clone();
                change(next);
                next.Normalize();
                _config = next;
            }
            try { _store?.Save(next); ConfigWarning = null; }
            catch (Exception ex) { ConfigWarning = "Could not save settings: " + ex.Message; _log.Warn(ConfigWarning); }
            RunNode();
        }

        /// <summary>The first-run question: "Turn on" or "Not now". Either answer is saved and the question is not asked again.</summary>
        internal void AnswerFirstRun(bool turnOn) => UpdateConfig(c => { c.FirstRunAnswered = true; c.Enabled = turnOn; });

        /// <summary>The status line: settings first (off / paused), then the Discord connection or the client that holds it.</summary>
        internal (PresenceStatusKind Kind, string Text) StatusLine() =>
            PresenceStatusText.StatusLine(Config, View, _service?.Status, Snapshot.LoggedIn);

        /// <summary>The sample the Test button sends for 20 seconds.</summary>
        internal static PresenceActivity SampleActivity(PresenceConfig c, DateTime nowUtc) => PresenceStatusText.SampleActivity(c, nowUtc);

        internal Task<string> TestAsync()
        {
            var c = Config;
            var svc = _service;
            if (svc == null) return Task.FromResult("The plugin did not start; see the Dungeon Helper log.");
            if (!View.IsLeader && View.LeaderId != null)
                return Task.FromResult("Another game client is showing your status right now; use Test there, or close that client.");
            return svc.TestAsync(c.EffectiveApplicationId, SampleActivity(c, DateTime.UtcNow));
        }

        private void RaiseChanged()
        {
            try { StateChanged?.Invoke(); } catch { }
        }

        public void Terminate()
        {
            _terminated = true;
            lock (_lock)
            {
                var t = _timer; _timer = null;
                try { t?.Dispose(); } catch { }
            }
            var node = _node;
            var clear = true;
            try
            {
                if (node != null)
                {
                    node.Leave();
                    clear = node.ShouldClearOnLeave();   // another client takes over: do not wipe its status
                }
            }
            catch { }
            // The whole stop is capped at one second. (Discord also clears an activity itself when the pipe closes.)
            try { _service?.StopAsync(TimeSpan.FromSeconds(1), clear).Wait(TimeSpan.FromMilliseconds(1200)); } catch { }
            try { _log.Info("DDO Discord Presence stopped"); } catch { }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DdoDiscordPresence.Presence
{
    /// <summary>
    /// Saved settings (DdoDiscordPresence.json beside the plugin, shared by every game client in a Dungeon Helper).
    /// A new install shares nothing until the player answers the first-run question; the character name is off.
    /// </summary>
    internal sealed class PresenceConfig
    {
        /// <summary>
        /// The Discord application the plugin publishes through (named "DDO", with the art assets ddo, casual, normal,
        /// hard, elite and reaper uploaded). An application id is a public identifier, not a secret.
        ///
        /// Undocumented override for maintainers: an "ApplicationId" (17-20 digits) and/or "LargeImageKey" in
        /// DdoDiscordPresence.json replace the built-in values. They are never shown in the panel and are only written
        /// back when they differ from the defaults; an invalid id is ignored (one log line) and the built-in one used.
        /// </summary>
        public const string BuiltInApplicationId = "1335652699156381746";

        /// <summary>2: built-in application id and first-run question (1 = 0.1.x, the id was typed in by the user).</summary>
        public const int CurrentSchema = 2;

        public int SchemaVersion { get; set; } = CurrentSchema;
        /// <summary>Override only; empty = the built-in id.</summary>
        public string ApplicationId { get; set; } = "";
        /// <summary>Master switch. OFF on a new install until the first-run question is answered with "Turn on".</summary>
        public bool Enabled { get; set; }
        /// <summary>The first-run question was answered (either way), or the file predates it.</summary>
        public bool FirstRunAnswered { get; set; }
        /// <summary>Pause sharing: clears the activity at once and keeps it clear until turned off.</summary>
        public bool Paused { get; set; }
        public bool ShowQuestName { get; set; } = true;
        public bool ShowAreaNames { get; set; } = true;
        public bool ShowLevelClass { get; set; } = true;
        /// <summary>The playing character's name (every sharing character's when multi-boxing). Off by default.</summary>
        public bool ShowCharacterName { get; set; }
        public bool ShowPartySize { get; set; } = true;
        public bool ShowDifficulty { get; set; } = true;
        /// <summary>Also send the difficulty badge as the small image.</summary>
        public bool ShowDifficultyBadge { get; set; } = true;
        public bool ShowElapsed { get; set; } = true;
        /// <summary>While the quest or area name contains any of these (case-insensitive), nothing is shared.</summary>
        public List<string> HiddenPlaces { get; set; } = new List<string>();
        /// <summary>Override only: the Rich Presence art key of the large image.</summary>
        public string LargeImageKey { get; set; } = PresenceFormatter.DefaultLargeImageKey;

        [JsonIgnore] public bool HasValidOverride => IsValidApplicationId(ApplicationId);
        /// <summary>True when the file names an application id that is not usable (it is ignored).</summary>
        [JsonIgnore] public bool HasInvalidOverride => !string.IsNullOrWhiteSpace(ApplicationId) && !HasValidOverride;
        /// <summary>The id actually used: a valid override, else the built-in one.</summary>
        [JsonIgnore] public string EffectiveApplicationId => HasValidOverride ? ApplicationId.Trim() : BuiltInApplicationId;
        /// <summary>Kept for the formatter: there is always a usable id now.</summary>
        [JsonIgnore] public bool HasValidApplicationId => IsValidApplicationId(EffectiveApplicationId);
        [JsonIgnore] public bool SharingEnabled => Enabled && HasValidApplicationId;
        /// <summary>The panel shows the first-run question instead of the settings.</summary>
        [JsonIgnore] public bool NeedsFirstRunAnswer => !FirstRunAnswered;

        private static readonly Regex AppIdRx = new Regex(@"^[0-9]{17,20}$", RegexOptions.CultureInvariant);
        private static readonly Regex AssetKeyRx = new Regex(@"^[a-z0-9_\-]{1,32}$", RegexOptions.CultureInvariant);

        public static bool IsValidApplicationId(string? id) => id != null && AppIdRx.IsMatch(id.Trim());
        public static bool IsValidAssetKey(string? key) => key != null && AssetKeyRx.IsMatch(key);

        /// <summary>A settings object for a brand-new install: nothing shared until the player says so.</summary>
        public static PresenceConfig NewInstall() => new PresenceConfig { Enabled = false, FirstRunAnswered = false };

        /// <summary>Repairs nulls and out-of-range values in place (a hand-edited or older file).</summary>
        public PresenceConfig Normalize()
        {
            ApplicationId = (ApplicationId ?? "").Trim();
            if (ApplicationId == BuiltInApplicationId) ApplicationId = "";
            var key = (LargeImageKey ?? "").Trim().ToLowerInvariant();
            LargeImageKey = IsValidAssetKey(key) ? key : PresenceFormatter.DefaultLargeImageKey;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (var raw in HiddenPlaces ?? new List<string>())
            {
                var e = PresenceFormatter.CleanText(raw, requireLetters: false);
                if (string.IsNullOrEmpty(e)) continue;
                if (e!.Length > 100) e = e.Substring(0, 100);
                if (seen.Add(e) && list.Count < 50) list.Add(e);
            }
            HiddenPlaces = list;
            if (SchemaVersion <= 0 || SchemaVersion > CurrentSchema) SchemaVersion = CurrentSchema;
            return this;
        }

        public PresenceConfig Clone()
        {
            var c = (PresenceConfig)MemberwiseClone();
            c.HiddenPlaces = new List<string>(HiddenPlaces ?? new List<string>());
            return c;
        }

        /// <summary>Same content (used to notice a change made by another game client's instance).</summary>
        public bool SameAs(PresenceConfig other) => ConfigStore.ToJson(this) == ConfigStore.ToJson(other);
    }

    /// <summary>
    /// Loads and saves <see cref="PresenceConfig"/>. Saves are atomic (write a temp file in the same folder, then replace).
    /// A corrupt file never crashes the plugin and never turns sharing on: it is kept aside and the plugin starts with
    /// sharing OFF (and asks the first-run question again). Several game clients share the file: <see cref="Changed"/>
    /// tells an instance that another one saved it.
    /// </summary>
    internal sealed class ConfigStore
    {
        public const string FileName = "DdoDiscordPresence.json";
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
        };

        private (DateTime Utc, long Length) _seen;

        public ConfigStore(string path) { Path = path; }
        public string Path { get; }

        /// <summary>Returns the settings and, when the file could not be used as it is, a one-line note for the log.</summary>
        public (PresenceConfig Config, string? Warning) Load()
        {
            string text;
            try
            {
                if (!File.Exists(Path)) { _seen = default; return (PresenceConfig.NewInstall(), null); }
                text = File.ReadAllText(Path, Encoding.UTF8);
                _seen = Stamp();
            }
            catch (Exception ex)
            {
                return (PresenceConfig.NewInstall(), "Could not read the settings file (" + ex.Message + "); sharing is off until you turn it on.");
            }
            try
            {
                var node = JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var cfg = JsonSerializer.Deserialize<PresenceConfig>(text, Options);
                if (cfg == null || node is not JsonObject obj) throw new JsonException("empty settings");
                string? warning = null;
                // 0.1.x files (schema 1, or none) predate the first-run question: the player already chose, keep it.
                if (cfg.SchemaVersion < 2 || !HasKey(obj, nameof(PresenceConfig.FirstRunAnswered))) cfg.FirstRunAnswered = true;
                if (cfg.HasInvalidOverride)
                {
                    warning = "The settings file names an application id that is not valid; using the built-in one.";
                    cfg.ApplicationId = "";
                }
                cfg.SchemaVersion = PresenceConfig.CurrentSchema;
                return (cfg.Normalize(), warning);
            }
            catch (Exception ex) when (ex is JsonException || ex is NotSupportedException || ex is ArgumentException || ex is InvalidOperationException)
            {
                string? moved = null;
                try
                {
                    moved = Path + ".corrupt";
                    File.Copy(Path, moved, overwrite: true);
                }
                catch { moved = null; }
                var safe = PresenceConfig.NewInstall();
                try { Save(safe); } catch { }
                return (safe, "The settings file was unreadable" + (moved != null ? " (kept as " + System.IO.Path.GetFileName(moved) + ")" : "")
                              + "; started with defaults and sharing OFF.");
            }
        }

        private static bool HasKey(JsonObject o, string name)
        {
            foreach (var kv in o) if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// The JSON written to disk. The override keys are left out while they hold the defaults, so a normal player's file
        /// never contains them.
        /// </summary>
        internal static string ToJson(PresenceConfig config)
        {
            var c = config.Clone().Normalize();
            var node = JsonSerializer.SerializeToNode(c, Options)!.AsObject();
            if (string.IsNullOrEmpty(c.ApplicationId)) node.Remove(nameof(PresenceConfig.ApplicationId));
            if (c.LargeImageKey == PresenceFormatter.DefaultLargeImageKey) node.Remove(nameof(PresenceConfig.LargeImageKey));
            return node.ToJsonString(Options);
        }

        public void Save(PresenceConfig config)
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = ToJson(config);
            var tmp = Path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    w.Write(json);
                    w.Flush();
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, Path, overwrite: true);
                _seen = Stamp();
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        /// <summary>The file changed since this instance last loaded or saved it (another game client saved it).</summary>
        public bool Changed()
        {
            try { return File.Exists(Path) && Stamp() != _seen; }
            catch { return false; }
        }

        private (DateTime, long) Stamp()
        {
            var fi = new FileInfo(Path);
            return (fi.LastWriteTimeUtc, fi.Length);
        }
    }
}

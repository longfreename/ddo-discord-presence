using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using VoK.Sdk;
using VoK.Sdk.Ddo;
using VoK.Sdk.Plugins;
using VoK.Sdk.Properties;

namespace Harness
{
    public class Fake : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler;
        protected override object? Invoke(MethodInfo? m, object?[]? a)
        {
            if (m == null) return null;
            var r = Handler?.Invoke(m, a);
            if (r != null) return r;
            var t = m.ReturnType;
            if (t == typeof(void)) return null;
            if (t == typeof(Task)) return Task.CompletedTask;
            return t.IsValueType && Nullable.GetUnderlyingType(t) == null ? Activator.CreateInstance(t) : null;
        }
        public static T Make<T>(Func<MethodInfo, object?[]?, object?> h) where T : class
        {
            var p = Create<T, Fake>();
            ((Fake)(object)p).Handler = h;
            return p;
        }
    }

    public class Registrar : DispatchProxy
    {
        public readonly List<Delegate> Handlers = new List<Delegate>();
        protected override object? Invoke(MethodInfo? m, object?[]? a)
        {
            if (m?.Name == "AddHandler" && a?[0] is Delegate d) Handlers.Add(d);
            return null;
        }
    }

    internal sealed record FakeChar(string Name, bool InQuest, string Place, int Level, string Class, uint Quest, uint Area);

    internal static class Program
    {
        /// <summary>
        /// Harness [plugin dll] [output folder]. Loads the BUILT plugin with fake game clients and renders its panel:
        /// a first run on an empty folder (a fresh install), one character, and three characters multi-boxing.
        /// The Discord pipe and the coordination folder are pointed at private names, so nothing here can reach the real
        /// Discord or a running Dungeon Helper.
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            Environment.SetEnvironmentVariable("DDO_DISCORD_PRESENCE_PIPE_PREFIX", "ddp-harness-nodiscord-" + Guid.NewGuid().ToString("N") + "-");
            Environment.SetEnvironmentVariable("DDO_DISCORD_PRESENCE_COORDINATION", Path.Combine(Path.GetTempPath(), "ddp-harness", "coord-" + Guid.NewGuid().ToString("N")));
            var dll = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("build", "plugins", "DdoDiscordPresence", "win-x64", "DdoDiscordPresence.dll"));
            var outDir = Path.GetFullPath(args.Length > 1 ? args[1] : "out");
            Directory.CreateDirectory(outDir);
            var log = new List<string>();
            var ok = true;
            try
            {
                var asm = Assembly.LoadFrom(dll);
                var type = asm.GetType("DdoDiscordPresence.Plugin", throwOnError: true)!;
                log.Add($"loaded {Path.GetFileName(dll)} {asm.GetName().Version}");

                var quest = new FakeChar("Thalindra", true, "The Sharn Syndicate", 15, "Paladin", 0x70001234, 0x70004321);
                ok &= Shot(type, outDir, "settings-first-run.png", null, new[] { quest }, log);
                var settings = "{ \"SchemaVersion\": 2, \"Enabled\": true, \"FirstRunAnswered\": true, \"ShowCharacterName\": true, \"HiddenPlaces\": [\"Vale of Twilight\"] }";
                ok &= Shot(type, outDir, "settings-one-character.png", settings, new[] { quest }, log);
                ok &= Shot(type, outDir, "settings-multibox.png", settings, new[]
                {
                    quest,
                    new FakeChar("Borrik", true, "The Sharn Syndicate", 14, "Fighter", 0x70001234, 0x70004321),
                    new FakeChar("Vessa", false, "The Harbor", 9, "Rogue", 0, 0x70005555),
                }, log);
            }
            catch (Exception ex) { log.Add("FAILED: " + ex); ok = false; }
            File.WriteAllLines(Path.Combine(outDir, "harness.log"), log);
            return ok ? 0 : 1;
        }

        private static IDdoGameDataProvider Provider(FakeChar ch, ulong charId, int partyOthers)
        {
            var registrars = new Dictionary<string, Registrar>();
            var events = Fake.Make<IDdoEventProvider>((m, a) =>
            {
                if (!m.Name.StartsWith("get_", StringComparison.Ordinal) || !m.ReturnType.IsInterface) return null;
                var key = m.Name.Substring(4);
                if (!registrars.TryGetValue(key, out var r)) registrars[key] = r = (Registrar)DispatchProxy.Create(m.ReturnType, typeof(Registrar));
                return r;
            });
            IStringInfoProperty Str(string text) => Fake.Make<IStringInfoProperty>((m, a) => m.Name == "get_Text" ? text : null);
            IPropertyCollection Coll(Dictionary<uint, IProperty> props, Func<uint, object?>? i32 = null, Func<uint, object?>? u32 = null) =>
                Fake.Make<IPropertyCollection>((m, a) => m.Name switch
                {
                    "get_Properties" => (IReadOnlyDictionary<uint, IProperty>)props,
                    "GetInt32PropertyValue" => i32?.Invoke((uint)a![0]!),
                    "GetUInt32PropertyValue" => u32?.Invoke((uint)a![0]!),
                    _ => null,
                });
            var master = Fake.Make<IPropertyMaster>((m, a) =>
            {
                if (m.Name != "GetPropertyCollection") return null;
                var did = (uint)a![0]!;
                var props = new Dictionary<uint, IProperty>();
                if (ch.InQuest && did == ch.Quest) props[0x1000030Bu] = Str(ch.Place);
                if (did == ch.Area) props[0x00000643u] = Str(ch.InQuest ? "Hideout" : ch.Place);
                return Coll(props);
            });
            var party = Enumerable.Range(0, partyOthers).Select(i => Fake.Make<IPartyMember>((mm, _) => mm.Name == "get_Id" ? (ulong)(100 + i) : null)).ToList();
            return Fake.Make<IDdoGameDataProvider>((m, a) => m.Name switch
            {
                "get_EventProvider" => events,
                "GetCurrentCharacterId" => (ulong?)charId,
                "GetCurrentCharacterName" => ch.Name,
                "InTown" => (bool?)!ch.InQuest,
                "GetInstanceQuestDid" => (uint?)ch.Quest,
                "GetCurrentQuestDid" => (uint?)ch.Quest,
                "GetMapInfo" => Fake.Make<IMapInfo>((mm, _) => mm.Name == "get_IsValid" ? true : mm.Name == "get_AreaDid" ? ch.Area : null),
                "get_PropertyMaster" => master,
                "GetCurrentCharacter" => Fake.Make<IEntity>((mm, _) => mm.Name == "get_PropertyCollection" ? Coll(new(), i32: id => id == 0x10001585u ? ch.Level : null) : null),
                "GetPlayerSocialEntity" => Fake.Make<IDdoSocialEntity>((mm, _) => mm.Name switch
                {
                    "get_Valid" => true,
                    "get_Classes" => new List<IClassInfo> { Fake.Make<IClassInfo>((c, _) => c.Name == "get_Name" ? ch.Class : c.Name == "get_Levels" ? (uint?)(uint)ch.Level : null) },
                    _ => null,
                }),
                "get_VolumeProperties" => ch.InQuest ? Coll(new(), u32: id => id == 0x10001BC5u ? 3u : null) : Coll(new()),
                "GetPartyMembers" => (IReadOnlyList<IPartyMember>)party,
                _ => null,
            });
        }

        private static IEnumerable<Control> All(Control c)
        {
            foreach (Control child in c.Controls) { yield return child; foreach (var d in All(child)) yield return d; }
        }

        private static bool Shot(Type pluginType, string outDir, string name, string? settings, FakeChar[] chars, List<string> log)
        {
            var root = Path.Combine(Path.GetTempPath(), "ddp-harness", Guid.NewGuid().ToString("N"), "DdoDiscordPresence");
            var win = Path.Combine(root, "win-x64");
            Directory.CreateDirectory(win);
            var cfgPath = Path.Combine(root, "DdoDiscordPresence.json");
            if (settings != null) File.WriteAllText(cfgPath, settings);

            // One plugin instance per game client, as Dungeon Helper creates them, all sharing the plugin folder.
            var plugins = new List<IDdoPlugin>();
            for (var i = 0; i < chars.Length; i++)
            {
                var plugin = (IDdoPlugin)Activator.CreateInstance(pluginType)!;
                plugin.Initialize(Provider(chars[i], 0x0200000000100000UL + (ulong)i, i == 0 ? 3 : 0), win);
                plugins.Add(plugin);
                System.Threading.Thread.Sleep(150);          // clients start one after another
            }
            var first = plugins[0];
            log.Add($"[{name}] {first.Name} {first.Version} by {first.Author}; {chars.Length} game client(s)");
            var ui = first.GetPluginUI();
            ui.ToolbarImage.Save(Path.Combine(outDir, "toolbar-from-dll.png"), ImageFormat.Png);
            var form = (Form)ui.UserInterfaceForm;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.ClientSize = new Size(ui.MinSize.Item1, 1220);
            form.Show();
            var sw = Stopwatch.StartNew();
            var diagOpened = false;
            while (sw.ElapsedMilliseconds < 7000)
            {
                Application.DoEvents();
                if (!diagOpened && sw.ElapsedMilliseconds > 6000 && settings != null)
                {
                    diagOpened = true;
                    (All(form).OfType<Button>().FirstOrDefault(b => b.Text == "Show details"))?.PerformClick();
                }
                System.Threading.Thread.Sleep(20);
            }
            var controls = All(form).ToList();
            var textBoxes = controls.OfType<TextBox>().Select(t => t.Name).ToList();
            var idField = textBoxes.Any(n => n != "HiddenPlaces") || controls.Any(c => (c.Text ?? "").Contains("Application ID"));
            log.Add($"[{name}] text boxes: {string.Join(",", textBoxes)}; application id field present: {idField}");
            log.Add($"[{name}] first-run card visible: {controls.First(c => c.Name == "FirstRun").Visible}");
            log.Add($"[{name}] status: {controls.First(c => c.Name == "Status").Text}");
            using (var bmp = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                bmp.Save(Path.Combine(outDir, name), ImageFormat.Png);
            }
            log.Add($"[{name}] wrote {name}");
            form.Hide();
            foreach (var p in plugins) p.Terminate();
            log.Add($"[{name}] settings file after run: {(File.Exists(cfgPath) ? File.ReadAllText(cfgPath).Replace(Environment.NewLine, " ") : "(none written)")}");
            return !idField;
        }
    }
}

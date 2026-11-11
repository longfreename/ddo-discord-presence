using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using VoK.Sdk;
using VoK.Sdk.Ddo;
using VoK.Sdk.Properties;

namespace DdoDiscordPresence.Tests
{
    /// <summary>
    /// SDK doubles built on DispatchProxy (Hireling Commander's offline-world technique): any interface, answered by a
    /// handler; members the handler does not model return the type's default, so a new SDK member never breaks a test.
    /// </summary>
    public class FakeProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler;

        protected override object? Invoke(MethodInfo? m, object?[]? a)
        {
            if (m == null) return null;
            var r = Handler?.Invoke(m, a);
            return r ?? Default(m.ReturnType);
        }

        public static object? Default(Type t)
        {
            if (t == typeof(void)) return null;
            if (t == typeof(Task)) return Task.CompletedTask;
            return t.IsValueType && Nullable.GetUnderlyingType(t) == null ? Activator.CreateInstance(t) : null;
        }

        public static T Make<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
        {
            var p = DispatchProxy.Create<T, FakeProxy>();
            ((FakeProxy)(object)p).Handler = handler;
            return p;
        }
    }

    /// <summary>Records handlers added through IEventRegistrar / IEventRegistrar&lt;T&gt;.</summary>
    public class FakeRegistrar : DispatchProxy
    {
        public readonly List<Delegate> Handlers = new List<Delegate>();
        protected override object? Invoke(MethodInfo? m, object?[]? a)
        {
            if (m?.Name == "AddHandler" && a != null && a.Length == 1 && a[0] is Delegate d) lock (Handlers) Handlers.Add(d);
            return null;
        }
    }

    /// <summary>A small fake game: one character, a quest or a town, names in a fake property master.</summary>
    internal sealed class FakeGame
    {
        public ulong CharacterId = 0x0200000000ABCDEFUL;
        public string CharacterName = "Testchar";
        public bool? InTown = true;
        public uint InstanceQuestDid;
        public uint JournalQuestDid;
        public uint AreaDid = 0x70011111;
        public int Level = 12;
        public readonly Dictionary<uint, string> QuestNames = new Dictionary<uint, string>();
        public readonly Dictionary<uint, string> AreaNames = new Dictionary<uint, string>();
        public uint? VolumeDifficulty;
        public int? VolumeReaper;
        public int PartyOthers;
        public bool ThrowEverywhere;
        public readonly ConcurrentDictionary<string, FakeRegistrar> Events = new ConcurrentDictionary<string, FakeRegistrar>();

        public IDdoGameDataProvider Provider()
        {
            var events = FakeProxy.Make<IDdoEventProvider>((m, a) =>
            {
                if (m.Name.StartsWith("get_", StringComparison.Ordinal) && m.ReturnType.IsInterface)
                    return Events.GetOrAdd(m.Name.Substring(4), _ => (FakeRegistrar)DispatchProxy.Create(m.ReturnType, typeof(FakeRegistrar)));
                return null;
            });
            return FakeProxy.Make<IDdoGameDataProvider>((m, a) =>
            {
                if (ThrowEverywhere && m.Name != "get_EventProvider") throw new InvalidOperationException("client gone");
                switch (m.Name)
                {
                    case "get_EventProvider": return events;
                    case "GetCurrentCharacterId": return (ulong?)CharacterId;
                    case "GetCurrentCharacterName": return CharacterName;
                    case "InTown": return InTown;
                    case "GetInstanceQuestDid": return (uint?)InstanceQuestDid;
                    case "GetCurrentQuestDid": return (uint?)JournalQuestDid;
                    case "GetMapInfo": return FakeProxy.Make<IMapInfo>((mm, _) => mm.Name switch
                    {
                        "get_IsValid" => true,
                        "get_AreaDid" => AreaDid,
                        _ => null,
                    });
                    case "get_PropertyMaster": return Master();
                    case "GetCurrentCharacter": return FakeProxy.Make<IEntity>((mm, _) => mm.Name == "get_PropertyCollection" ? Collection(new Dictionary<uint, IProperty>(), level: Level) : null);
                    case "get_VolumeProperties": return Collection(new Dictionary<uint, IProperty>(), difficulty: VolumeDifficulty, reaper: VolumeReaper);
                    case "GetPartyMembers":
                        var list = new List<IPartyMember>();
                        for (var i = 0; i < PartyOthers; i++)
                        {
                            var id = (ulong)(1000 + i);
                            list.Add(FakeProxy.Make<IPartyMember>((mm, _) => mm.Name == "get_Id" ? id : null));
                        }
                        if (PartyOthers > 0) list.Add(FakeProxy.Make<IPartyMember>((mm, _) => mm.Name == "get_Id" ? CharacterId : null));   // self may be listed
                        return (IReadOnlyList<IPartyMember>)list;
                    default: return null;
                }
            });
        }

        private IPropertyMaster Master() => FakeProxy.Make<IPropertyMaster>((m, a) =>
        {
            if (m.Name != "GetPropertyCollection" || a == null) return null;
            var did = (uint)a[0]!;
            var props = new Dictionary<uint, IProperty>();
            if (QuestNames.TryGetValue(did, out var q)) props[Game.GameReader.Prop_Quest_Name] = StringInfo(Game.GameReader.Prop_Quest_Name, q);
            if (AreaNames.TryGetValue(did, out var n)) props[Game.GameReader.Prop_Area_Name] = StringInfo(Game.GameReader.Prop_Area_Name, n);
            return Collection(props);
        });

        private static IStringInfoProperty StringInfo(uint id, string text) => FakeProxy.Make<IStringInfoProperty>((m, a) => m.Name switch
        {
            "get_Text" => text,
            "get_PropertyId" => id,
            _ => null,
        });

        private static IPropertyCollection Collection(Dictionary<uint, IProperty> props, int? level = null, uint? difficulty = null, int? reaper = null) =>
            FakeProxy.Make<IPropertyCollection>((m, a) =>
            {
                switch (m.Name)
                {
                    case "get_Properties": return (IReadOnlyDictionary<uint, IProperty>)props;
                    case "GetInt32PropertyValue":
                        var id = (uint)a![0]!;
                        if (id == Game.GameReader.Prop_Character_TotalLevel) return level;
                        if (id == Game.GameReader.Prop_Dungeon_ReaperLevel) return reaper;
                        return null;
                    case "GetUInt32PropertyValue":
                        return (uint)a![0]! == Game.GameReader.Prop_Dungeon_DifficultyLevel ? difficulty : null;
                    default: return null;
                }
            });

        public async Task RaiseAsync(string name, params object[] args)
        {
            if (!Events.TryGetValue(name, out var reg)) return;
            Delegate[] hs; lock (reg.Handlers) hs = reg.Handlers.ToArray();
            foreach (var h in hs) if (h.DynamicInvoke(args) is Task t) await t;
        }
    }
}

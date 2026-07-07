using System.Reflection;

namespace XiHeadless.Brains;

/// Holds the capability instances for one bot session and resolves them by interface
/// type, so the registry can constructor-inject whatever a brain asks for.
public sealed class CapabilitySet
{
    public IPerception Perception { get; }
    public IChat Chat { get; }
    public ICombat Combat { get; }
    public IMagic Magic { get; }
    public INavigation Nav { get; }
    public IZoning Zoning { get; }
    public IEvents Events { get; }
    public IGear Gear { get; }
    public IDelivery Delivery { get; }
    public IGilGrant GilGrant { get; }
    public WorldApi World { get; }
    public IBazaar Bazaar { get; }
    public ICrafting Crafting { get; }
    public IShop Shop { get; }
    public IParty Party { get; }
    public IInventory Inventory { get; }
    public ILifecycle Lifecycle { get; }
    public IAuctionHouse Auction { get; }
    public IQuests Quests { get; }
    public IJobChange JobChange { get; }
    public ITradeNpc TradeNpc { get; }

    readonly Navigator _nav;

    public CapabilitySet(ISession s, NavMesh? mesh = null, Action? onLogout = null)
    {
        Perception = new Perception(s.State);
        Chat = new Chat(s);
        Combat = new Combat(s);
        Magic = new Magic(s);
        _nav = new Navigator(s, mesh);   // null-tolerant: no-ops MoveTo until a mesh is swapped in
        _nav.ReconcilePosition();        // initial zone-in: fix Y/Z order against the mesh
        Nav = _nav;
        Zoning = new Zoning(s, _nav);
        Events = new Events(s);
        Inventory = new Inventory(s);
        Gear = new Gear(s, Inventory);   // Gear queries the bag (Has/SlotOf) via the shared Inventory
        Delivery = new Delivery(s);
        GilGrant = new BotApi();   // HTTP client; auth/endpoint from deployment secrets (env)
        World = new WorldApi();     // read-only world session-count API (XIBOT_WORLD_URL); for GM/RMT self-stop
        Bazaar = new Bazaar(s);
        Crafting = new Crafting(s);
        Shop = new Shop(s);
        Party = new Party(s);
        Lifecycle = new Lifecycle(onLogout ?? (() => { }));
        Auction = new AuctionHouse(s);
        Quests = new Quests(Events);
        JobChange = new JobChange(s, Delivery);
        TradeNpc = new TradeNpc(s);
    }

    /// Hot-swap the navmesh after a zone change (the brain keeps its same INavigation/IZoning refs).
    public void SwapMesh(NavMesh? mesh) => _nav.SetMesh(mesh);

    public object? Resolve(Type t) =>
        t == typeof(IPerception) ? Perception :
        t == typeof(IChat)       ? Chat :
        t == typeof(ICombat)     ? Combat :
        t == typeof(IMagic)      ? Magic :
        t == typeof(INavigation) ? Nav :
        t == typeof(IZoning)     ? Zoning :
        t == typeof(IEvents)     ? Events :
        t == typeof(IGear)       ? Gear :
        t == typeof(IDelivery)   ? Delivery :
        t == typeof(IGilGrant)   ? GilGrant :
        t == typeof(WorldApi)    ? World :
        t == typeof(IBazaar)     ? Bazaar :
        t == typeof(ICrafting)   ? Crafting :
        t == typeof(IShop)       ? Shop :
        t == typeof(IParty)      ? Party :
        t == typeof(IInventory)  ? Inventory :
        t == typeof(ILifecycle)  ? Lifecycle :
        t == typeof(IAuctionHouse) ? Auction :
        t == typeof(IQuests)     ? Quests :
        t == typeof(IJobChange)  ? JobChange :
        t == typeof(ITradeNpc)   ? TradeNpc : null;
}

/// Discovers every IBrain in the assembly and constructs one by name, auto-injecting
/// the capabilities its constructor declares. Add a new brain = write a class that
/// implements IBrain; it's instantly selectable by name (arg or XIBOT_BRAIN env).
public static class BrainRegistry
{
    static readonly Dictionary<string, Type> _brains = Discover();

    static Dictionary<string, Type> Discover()
    {
        var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Assembly.GetExecutingAssembly().GetTypes())
        {
            if (t.IsAbstract || t.IsInterface || !typeof(IBrain).IsAssignableFrom(t)) continue;
            map[t.Name] = t;                                         // "MeleeBrain"
            if (t.Name.EndsWith("Brain")) map[t.Name[..^5]] = t;     // "Melee"
        }
        return map;
    }

    public static bool Exists(string name) => _brains.ContainsKey(name);

    public static IReadOnlyCollection<string> Names =>
        _brains.Keys.Where(k => k.EndsWith("Brain")).OrderBy(k => k).ToArray();

    public static IBrain Create(string name, CapabilitySet caps)
    {
        if (!_brains.TryGetValue(name, out var type))
            throw new ArgumentException($"No brain named '{name}'. Available: {string.Join(", ", Names)}");
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters()
            .Select(p => caps.Resolve(p.ParameterType)
                         ?? throw new InvalidOperationException($"{type.Name} wants {p.ParameterType.Name}, which isn't a known capability"))
            .ToArray();
        return (IBrain)ctor.Invoke(args);
    }
}

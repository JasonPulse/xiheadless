namespace XiHeadless.Game;

/// Item ids the ENGINE itself depends on (brains own their gear tables). Lives in Game so capabilities
/// (the junk seller) and routines (stealth travel) share one definition.
public static class Items
{
    public const ushort PrismPowder = 4164;   // -> Invisible (blocks sight aggro)
    public const ushort SilentOil = 4165;     // -> Sneak (blocks sound aggro)

    /// Never junk-sold, whatever a brain's keep set says: travel protection is engine behavior for every bot,
    /// and most brains' keep sets don't list the powders.
    public static readonly HashSet<ushort> NeverSell = new() { PrismPowder, SilentOil };
}

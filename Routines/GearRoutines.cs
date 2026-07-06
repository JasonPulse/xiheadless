namespace XiHeadless.Routines;

// ============================================================================
// Shared gear-set building. Every job brain used to hand-roll the SAME Equip()
// body (filter the per-job table by level -> EquipSet), plus the SAME BuyItems
// / Keep derivations off that table. Brains keep their per-job Gear table (legit
// config) and CALL these — no more ~17 copies of the loop.
// ============================================================================
public static class GearRoutines
{
    /// Build the level-appropriate set from a per-job gear table and equip it. Order (later wins per slot):
    /// optional basePieces (a shared armor set) -> table entries with lvl <= character level -> optional
    /// phaseWeapon (a sub-job weapon swap applied last). Returns (equipped, requested) so the caller keeps
    /// its own bespoke log line.
    public static async Task<(int n, int total)> EquipByLevel(
        IGear gear, IPerception p,
        (ushort item, byte slot, byte lvl)[] table,
        CancellationToken ct,
        (byte slot, ushort item)? phaseWeapon = null,
        IEnumerable<(byte slot, uint item)>? basePieces = null)
    {
        int lvl = p.World.MainJobLevel;
        var set = new List<(byte slot, uint item)>();
        if (basePieces != null) set.AddRange(basePieces);
        foreach (var g in table.Where(g => g.lvl <= lvl)) set.Add((g.slot, (uint)g.item));
        if (phaseWeapon is { } pw) set.Add((pw.slot, pw.item));
        int n = await gear.EquipSet(set, ct);
        return (n, set.Count);
    }

    /// AH buy list = every item in the gear table (ascending by level, cheapest-first).
    public static IEnumerable<ushort> BuyList((ushort item, byte slot, byte lvl)[] table) => table.Select(g => g.item);

    /// Never-sell set = the gear items plus any extras (seals, stealth stock, quest items).
    public static HashSet<ushort> KeepSet((ushort item, byte slot, byte lvl)[] table, params ushort[] extra) =>
        new HashSet<ushort>(table.Select(g => g.item).Concat(extra));
}

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
    // Char creation makes a WAR holding an Onion Sword — the ONLY weapon a broke fresh bot owns.
    public const ushort StarterSword = 16534;
    // Jobs that can wield a sword (fallback eligibility). Mage/club jobs are excluded — their KIT (nukes/
    // cures) carries them; MNK/PUP fight h2h natively and don't need it.
    static readonly HashSet<byte> SwordJobs = new()
        { Job.War, Job.Thf, Job.Pld, Job.Drk, Job.Brd, Job.Rng, Job.Sam, Job.Nin, Job.Drg, Job.Rdm, Job.Blu, Job.Cor, Job.Run };

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
        // STARTER-WEAPON FALLBACK (live fleet: a broke COR bot in its RDM phase owned NOTHING from its gear
        // list — 'equipped 0/3' — and punched con-4 rabbits BARE-FISTED to 4 deaths/h while the creation
        // Onion Sword sat in its bag). If the table yields no main-hand at this level and the job can wield
        // a sword, equip the starter sword; a no-op if it was sold/replaced (EquipSet skips missing items).
        // "Covered" must mean OWNED-and-equipped, not merely listed: the live COR listed a Bronze Knife it
        // never bought. n==0 = the whole set failed to land (the broke-fresh-bot signature) -> fall back.
        bool mainCovered = n > 0 && ((phaseWeapon?.slot == EquipSlot.Main)
                           || table.Any(g => g.lvl <= lvl && g.slot == EquipSlot.Main));
        if (!mainCovered && SwordJobs.Contains(p.World.MainJob))
        {
            await gear.EquipSet(new[] { ((byte)EquipSlot.Main, (uint)StarterSword) }, ct);
            Log.Info("[gear] no main-hand from the gear table — starter Onion Sword fallback");
        }
        return (n, set.Count);
    }

    /// AH buy list = every item in the gear table (ascending by level, cheapest-first).
    public static IEnumerable<ushort> BuyList((ushort item, byte slot, byte lvl)[] table) => table.Select(g => g.item);

    /// Never-sell set = the gear items plus any extras (seals, stealth stock, quest items) — PLUS the
    /// starter sword, always: it's the fallback weapon for a broke bot; junk-selling it disarms the char.
    public static HashSet<ushort> KeepSet((ushort item, byte slot, byte lvl)[] table, params ushort[] extra) =>
        new HashSet<ushort>(table.Select(g => g.item).Concat(extra).Append(StarterSword));
}

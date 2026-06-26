namespace XiHeadless.Brains;

/// WAR core game loop: buy a low-level gear set from the Auction House, carry it to the hunt zone,
/// equip what our level allows, then hunt — find a wild mob, con it, engage, build TP, weaponskill,
/// fight to the death, and on KO homepoint-revive and return. Re-equips periodically so each piece
/// comes online as we out-level its requirement. Gear guide (network-gnomes WAR guide, lv1-18 bracket):
/// Great Axe is the weapon (WS auto-picks off Great Axe skill).
public sealed class WarBrain(IPerception p, INavigation nav, ICombat combat, IZoning zoning, IGear gear, IAuctionHouse ah, IDelivery delivery) : IBrain
{
    const ushort HuntZone = 116;             // East Sarutabaruta (Windurst starting area, adjacent to Windurst Woods)
    const string AhZone = "Windurst Woods";  // where we buy gear (has the AH)
    const byte WepSkill = 6;                  // Great Axe — the WS auto-pick reads this skill
    const ushort Weapon = 16704;              // Butterfly Axe (Great Axe, lv5)

    const ushort EarlyWeapon = 16534;   // Onion Sword (lv1) — used until we can wield the axe at lv5

    // Non-weapon gear (item id, slot). The main-hand weapon is chosen by level in Equip() so we never
    // send two main-hand equips in one pass (which left the bot effectively unarmed). EquipSet applies
    // each piece; the server ignores any above our level, so we re-equip on level-up to bring them on.
    static readonly (ushort item, byte slot)[] Armor =
    {
        (13014, EquipSlot.Feet),   // Leaping Boots   (lv7) — AH version (Bounding Boots is the rare/ex upgrade)
        (17280, EquipSlot.Ranged), // Boomerang       (lv14)
        (13398, EquipSlot.Ear1),   // Physical Earring (lv10) — Optical Earring (14803) is mismarked Rare/Ex here, not AH-buyable
        (13194, EquipSlot.Waist),  // Warrior's Belt  (lv15)
        (13522, EquipSlot.Ring1),  // Courage Ring    (lv14)
    };

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(3000, ct);
        // Gil + inventory stream in over the first few seconds after zone-in; wait for gil before the
        // buy phase or every bid reads 0 ("out of budget") and we buy nothing.
        for (int i = 0; i < 30 && p.World.Gil == 0 && !ct.IsCancellationRequested; i++) await Task.Delay(500, ct);
        Console.WriteLine($"[war] char='{p.World.MyName}' job={p.World.MainJob}/{p.World.SubJob} lvl={p.World.MainJobLevel} gil={p.World.Gil} zone={zoning.CurrentZone}");

        // If we logged in INSIDE the Mog House (zone 0), step out to the city first — otherwise we can't
        // route anywhere (every travel needs a real source zone).
        if (zoning.CurrentZone == 0)
        {
            Console.WriteLine("[war] inside the Mog House (zone 0) — exiting to the city");
            await delivery.ExitMogHouse(ct);
            await Task.Delay(2500, ct);
            Console.WriteLine($"[war] after Mog House exit -> zone {zoning.CurrentZone}");
        }

        // 1) Gear up from the AH (must be in a MISC_AH zone), then carry it to the hunt zone.
        if (!Game.Zonelines.HasAuctionHouse(zoning.CurrentZone))
        {
            Console.WriteLine($"[war] traveling to {AhZone} for gear");
            await zoning.GoTo(AhZone, ct);
        }
        foreach (var item in new ushort[] { EarlyWeapon, Weapon }.Concat(Armor.Select(g => g.item)))
        {
            if (ct.IsCancellationRequested) return;
            await ShopRoutines.BuyFromAH(ah, p, item, ct);
        }

        // 2) Go to the hunt zone and equip what we can.
        if (zoning.CurrentZone != HuntZone && zoning.CurrentZone != 0) await zoning.ToZone(HuntZone, ct);
        await Equip(ct);

        Console.WriteLine($"[war] start skills: GreatAxe={gear.SkillLevel(WepSkill)} H2H={gear.SkillLevel(1)}");
        Console.WriteLine($"[war] hunting in zone {HuntZone}");

        byte lastLevel = p.World.MainJobLevel;
        while (!ct.IsCancellationRequested)
        {
            // Re-equip the moment we level up, so each piece comes online exactly when we meet its
            // requirement (e.g. the Butterfly Axe at lv5 -> Great Axe skill + WS start).
            if (p.World.MainJobLevel > lastLevel)
            {
                Console.WriteLine($"[war] LEVEL UP -> {p.World.MainJobLevel}, re-equipping");
                lastLevel = p.World.MainJobLevel;
                await Equip(ct);
            }

            // Dead (KO'd): stop, homepoint to revive (a zone change), then loop back to travel + re-gear.
            if (combat.Dead)
            {
                nav.Stop();
                Console.WriteLine("[war] KO'd — returning to home point to revive");
                await combat.Homepoint(ct);
                await Task.Delay(5000, ct);
                continue;
            }

            // Not in the hunt zone (e.g. just revived) — travel back and re-equip on arrival.
            if (zoning.CurrentZone != HuntZone && zoning.CurrentZone != 0)
            {
                Console.WriteLine($"[war] in zone {zoning.CurrentZone}, returning to hunt zone {HuntZone}");
                await zoning.ToZone(HuntZone, ct);
                await Equip(ct);
                continue;
            }

            var mob = p.Nearest(e => e.IsMob && e.Hpp > 0 && e.Y < 100
                && !e.Name.Contains("Quadav", StringComparison.OrdinalIgnoreCase)
                && !_skip.Contains(e.Id)
                && (p.World.NowMs - e.LastSeenMs) < 20000 && p.DistanceTo(e.X, e.Z) <= 50f);
            if (mob is null)
            {
                _skip.Clear();
                if (!nav.IsMoving) RoamStep();
                await Task.Delay(1000, ct);
                continue;
            }
            nav.Stop();

            int con = await combat.Consider(mob.Id, ct);
            if (con < _conMin || con > _conMax)
            {
                Console.WriteLine($"[war] skip 0x{mob.Id:X} '{mob.Name}' con={con} lvl={p.World.ConMobLevel} (want {_conMin}-{_conMax})");
                _skip.Add(mob.Id);
                continue;
            }
            Console.WriteLine($"[war] target 0x{mob.Id:X} '{mob.Name}' con={con} lvl={p.World.ConMobLevel} hpp={mob.Hpp} dist={p.DistanceTo(mob.X, mob.Z):F0}");

            nav.Follow(mob.Id);
            for (int i = 0; i < 80 && !ct.IsCancellationRequested; i++)
            {
                if (mob.Hpp == 0 || (p.World.NowMs - mob.LastSeenMs) > 20000 || p.DistanceTo(mob.X, mob.Z) <= 2.0f) break;
                await Task.Delay(250, ct);
            }
            nav.Stop();
            if (mob.Hpp == 0 || (p.World.NowMs - mob.LastSeenMs) > 20000) { Console.WriteLine("[war] target lost during approach"); continue; }

            nav.Face(mob.Id);
            Console.WriteLine($"[war] engage 0x{mob.Id:X} (idx {mob.Index}) at dist {p.DistanceTo(mob.X, mob.Z):F1}");
            await combat.Engage(mob.Id, ct);

            byte bestHpp = mob.Hpp;               // lowest mob HP we've driven it to
            long lastProgressMs = p.World.NowMs;  // last time the mob actually took damage
            while (!ct.IsCancellationRequested && mob.Hpp > 0 && p.World.Hpp > 0 && (p.World.NowMs - mob.LastSeenMs) < 20000)
            {
                float d = p.DistanceTo(mob.X, mob.Z);
                if (d > 2.5f) nav.Follow(mob.Id);
                else { nav.Stop(); nav.Face(mob.Id); }
                // Keep auto-attack alive: if engagement dropped (or the initial Engage never took, which
                // leaves TP and the mob's HP frozen), re-engage once we're back in melee range.
                if (!combat.Engaged && d <= 5f) { nav.Face(mob.Id); await combat.Engage(mob.Id, ct); }
                // WS off the ACTUALLY-equipped weapon's skill: Onion Sword (sword, 3) until lv5, then
                // Butterfly Axe (Great Axe, 6). So Fast Blade fires on the sword early and Shield Break
                // once the axe is on — instead of waiting on a skill the equipped weapon doesn't train.
                byte wep = p.World.MainJobLevel >= 5 ? WepSkill : (byte)3;
                var ws = CombatRoutines.BestWeaponSkill(wep, gear.SkillLevel(wep));
                if (ws is not null && combat.Engaged && combat.CanWeaponSkill && mob.Hpp is > 10 and < 100)
                {
                    Console.WriteLine($"[war] {ws} (tp={combat.Tp} skill{wep}={gear.SkillLevel(wep)}) on mob hpp={mob.Hpp}");
                    await combat.WeaponSkill(ws.Value, mob.Id, ct);
                }
                await Task.Delay(2000, ct);
                Console.WriteLine($"[war] fighting myHP%={p.World.Hpp} tp={combat.Tp} lvl={p.World.MainJobLevel} sword={gear.SkillLevel(3)} ga={gear.SkillLevel(6)} | mob hpp={mob.Hpp} dist={p.DistanceTo(mob.X, mob.Z):F0}");

                // Anti-stuck: if the mob hasn't lost HP for 15s we aren't actually damaging it (failed
                // engage / unreachable / untargetable) — abandon it, skip it, and go find another mob so
                // the session never dead-locks on one target.
                if (mob.Hpp < bestHpp) { bestHpp = mob.Hpp; lastProgressMs = p.World.NowMs; }
                else if (p.World.NowMs - lastProgressMs > 15000)
                {
                    Console.WriteLine($"[war] STUCK: no damage to 0x{mob.Id:X} '{mob.Name}' in 15s — abandoning + retargeting");
                    _skip.Add(mob.Id);
                    break;
                }
            }
            bool killed = mob.Hpp == 0;
            Console.WriteLine($"[war] fight ended: mob hpp={mob.Hpp} myHP%={p.World.Hpp} lvl={p.World.MainJobLevel}{(killed ? " (killed)" : "")}");
            if (combat.Engaged) combat.Disengage();
            // Range the zone instead of camping: after a few kills, relocate deeper to fresh hunting ground.
            if (killed && (++_kills % 4) == 0) { Console.WriteLine("[war] relocating deeper into the zone"); RoamStep(); }
            await Task.Delay(1000, ct);
        }
    }

    async Task Equip(CancellationToken ct)
    {
        // One main-hand weapon, chosen by level: Butterfly Axe at lv5+, else the Onion Sword.
        ushort weapon = p.World.MainJobLevel >= 5 ? Weapon : EarlyWeapon;
        var set = new List<(byte slot, uint item)> { (EquipSlot.Main, weapon) };
        set.AddRange(Armor.Select(g => (g.slot, (uint)g.item)));
        int n = await gear.EquipSet(set, ct);
        Console.WriteLine($"[war] equipped {n}/{set.Count} (lvl {p.World.MainJobLevel}, wep={weapon} sword={gear.SkillLevel(3)} ga={gear.SkillLevel(6)})");
    }

    // Con gate: EasyPrey only (2). At low level with sparse gear (most WAR armor is lv24+) and no cure/
    // flee, even DecentChallenge(3) mobs killed us repeatedly (deaths ate the EXP, stalling at lv9).
    // EasyPrey is slower per kill but near-deathless, so the bot actually climbs. Widen once gear/level
    // make Decent safe.
    const int _conMin = 2;
    const int _conMax = 2;
    readonly HashSet<uint> _skip = new();

    int _roam;
    int _kills;
    void RoamStep()
    {
        var w = p.World;
        for (int i = 0; i < 8; i++)
        {
            double ang = (_roam + i) * Math.PI / 4;
            // Big steps (100y) so we actually move into fresh parts of the zone rather than circling one
            // camp; the mob search radius is 50y, so each roam opens a new hunting area.
            float cx = w.X + (float)Math.Cos(ang) * 100f, cz = w.Z + (float)Math.Sin(ang) * 100f;
            nav.MoveTo(cx, cz);
            if (nav.IsMoving) { _roam += i + 1; Console.WriteLine($"[war] roaming -> ({cx:F0},{cz:F0})"); return; }
        }
        _roam++;
        Console.WriteLine("[war] roam: no reachable direction from here");
    }
}

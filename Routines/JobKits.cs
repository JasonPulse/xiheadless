using XiHeadless.Game;

namespace XiHeadless.Routines;

/// GENERIC per-job combat kits, injected by JobLifecycle when a brain didn't wire its own UseAbilities /
/// EmergencyHeal (most fleet brains are gear+quest configs with NO kit — live fleet day 1: a lvl-1 BRD pure
/// melee auto-attacked and lost to a con-2 rabbit 17x/h; with its songs it kills far above its level, per
/// the user). Everything self-gates: combat.UseAbility checks job/level/recast; magic checks Known/MP —
/// so kits list generously and the wrong-job entries no-op. The CURATED brains (WarBrain/BlmBrain) keep
/// their own rotations; this covers the other ~18.
public static class JobKits
{
    // "Mages use spells" (user) — the early scroll kit IS a caster's weapon. Per-job relevance comes from
    // SpellLevels (usable by ~lv12), so WHM resolves to Cure/Dia/Banish/Paralyze, BLM to Stone/Water/etc.
    // Bought by LevelGrind's existing AH buy phase (appended to BuyItems, cheapest essentials first) and
    // learned in the Equip pass — the proven WhmBrain arc (club + Dia + Cure -> 18), now engine-provided
    // so every advanced-job mage phase (SCH/GEO/SMN/RDM sub-arcs) gets it without brain-side lists.
    // buyable=false = the EX STARTING scrolls (granted by charCreate to mage-created chars: Cure_EX 4608,
    // Stone_EX 4607, Dia_EX 4606) — learn-only, never in the AH buy list (EX is unlistable) and listed
    // FIRST so a char holding its free starting scroll learns from it before ever bidding on the shop copy.
    static readonly (ushort scroll, Spell spell, bool buyable)[] ScrollKit =
    {
        (4608, Spell.Cure, false), (4606, Spell.Dia, false), (4607, Spell.Stone, false),
        (4609, Spell.Cure, true), (4631, Spell.Dia, true), (4636, Spell.Banish, true), (4666, Spell.Paralyze, true),
        (4767, Spell.Stone, true), (4777, Spell.Water, true), (4762, Spell.Aero, true), (4862, Spell.Blind, true),
        // BRD songs ARE its spells (user: lvl-1s with no songs were red flags) — SpellLevels filters to BRD.
        (4976, Spell.FoeRequiem, true), (4986, Spell.ArmysPaeon, true),
        (5002, Spell.ValorMinuet, true), (5007, Spell.SwordMadrigal, true),
    };

    // Spell-primary jobs: they level by CASTING, so their melee weapon skill is irrelevant and permanently
    // lags (they never swing to skill it). The skill-up dropback must NOT gate them onto con-0 melee prey —
    // past lv10 they fight the normal con band and cast (user 2026-07-31). Same set the For() switch treats as
    // casters. (BRD is NOT here — it melees with a dagger it does skill; its songs ride on top.)
    public static bool CastsPrimary(byte job) =>
        job is Job.Whm or Job.Blm or Job.Rdm or Job.Sch or Job.Geo or Job.Smn;

    static (ushort scroll, Spell spell, bool buyable)[] EssentialScrolls(byte job) =>
        ScrollKit.Where(s => SpellLevels.For((ushort)s.spell, job) is { } lvl && lvl <= 12).ToArray();

    /// Wire the generic kit into a grind config IF the brain left the defaults in place.
    public static void Apply(LevelGrind.Config g, byte job, ICombat combat, IMagic? magic, IPerception p, string tag,
                             IInventory? inv = null)
    {
        if (ReferenceEquals(g.UseAbilities, LevelGrind.Config.NoAbilities))
            g.UseAbilities = For(job, combat, magic, p, tag);
        // Casters past lv10 don't skill melee — keep the skill-up dropback off them so they hunt the normal
        // con band and cast, instead of stalling on con-0 melee prey forever (GEO: 299 kills, 3 levels).
        if (CastsPrimary(job) && p.World.MainJobLevel >= 10) g.SkipMeleeSkillup = true;
        // RNG/COR do their real damage by SHOOTING — the fight loop fires Shoot on cadence (user 2026-07-31).
        if (job is Job.Rng or Job.Cor) g.Ranged = true;
        // Self-funding default: if the brain wired NO bag policy at all, sell junk drops when the bag
        // fills (drops -> gil -> scrolls/gear is the whole broke-bot economy; a bag that silently fills
        // just bounces loot). Brains with an explicit OnBagFull (party farms) are untouched.
        if (!g.SellJunkWhenFull && g.OnBagFull is null) { g.SellJunkWhenFull = true; g.SellAtItems = Math.Min(g.SellAtItems, 22); }
        // Essential scrolls for this phase's job: buy (via the standard buy phase) + learn (in the Equip
        // pass). Applies to EVERY brain's mage phases — scroll learning is engine duty, not brain config.
        if (magic is not null && inv is not null && EssentialScrolls(job) is { Length: > 0 } scrolls)
        {
            g.BuyItems = scrolls.Where(s => s.buyable).Select(s => s.scroll).Where(s => !g.BuyItems.Contains(s)).Concat(g.BuyItems).ToArray();
            foreach (var (scroll, _, _) in scrolls) g.Keep.Add(scroll);
            var innerEquip = g.Equip;
            g.Equip = async ct =>
            {
                await innerEquip(ct);
                foreach (var (scroll, spell, _) in scrolls)
                    if (!magic.Known(spell) && inv.Has(scroll)
                        && SpellLevels.For((ushort)spell, p.World.MainJob) is { } lvl && p.World.MainJobLevel >= lvl)
                        await MagicRoutines.LearnFromScroll(inv, magic, p, scroll, spell, ct, tag);
            };
        }
        // Any job that can Cure (WHM/RDM main, or a WHM/RDM sub once set) self-heals below 50% — the one
        // shared MagicRoutines.EmergencyCure (level-gated Cure line selector, never a hardcoded tier).
        if (magic is not null && ReferenceEquals(g.EmergencyHeal, LevelGrind.Config.NoHeal))
            g.EmergencyHeal = ct => MagicRoutines.EmergencyCure(magic, p, ct, tag: tag);
    }

    // The per-beat rotation for `job`. One action per beat max (each firing path delays internally).
    static Func<uint, int, CancellationToken, Task> For(byte job, ICombat combat, IMagic? magic, IPerception p, string tag)
    {
        long lastSongMs = 0;
        uint lastMob = 0;
        bool diaDone = false, banishDone = false;   // WHM: Dia/Banish are ONE-per-fight (recasting a DoT wastes MP)
        return async (mob, con, ct) =>
        {
            var w = p.World;
            if (mob != lastMob) { lastMob = mob; diaDone = false; banishDone = false; }   // new fight — reset one-shot casts

            // Signature melee JAs (each self-gates on job/level/recast). Shared by the melee default AND a
            // SMN with no avatar granted yet (pre-grant fallback), so there's ONE copy.
            async Task<bool> TryMeleeJa()
            {
                if (con < 2) return false;
                foreach (var ab in MeleeJas)
                    if (await combat.UseAbility(ab, mob, ct)) { Log.Info($"[{tag}] {ab}"); return true; }
                return false;
            }

            switch (job)
            {
                // ---- BARD: songs ARE the kit. Foe Requiem (DoT, BEST castable tier) on the mob, re-sung on a
                // song-length cadence; melee carries the rest. (Lv-1 BRD + Requiem beats far above its level.)
                case Job.Brd:
                    if (magic is not null && w.NowMs - lastSongMs > 30_000
                        && magic.CastHighest(SpellLine.FoeRequiem, mob))   // tier selector — best known Requiem
                    {
                        lastSongMs = w.NowMs;
                        Log.Info($"[{tag}] singing Requiem on the mob");
                        await Task.Delay(3000, ct);
                    }
                    return;

                // ---- SUMMONER: the avatar tanks + DPS; the fragile SMN never melees (it lost 1v1 to con-4
                // Goblins, 2026-07-26). Keep the highest KNOWN avatar out (Cast on self), then Blood Pact:
                // Rage for burst, else Assault to keep the pet on the mob. Avatars arrive via GM !addspell as
                // the SMN levels (SmnBrain + Game.Avatars); EmergencyCure (WHM sub) still self-heals via its hook.
                case Job.Smn:
                {
                    Spell? best = null;
                    if (magic is not null)
                        foreach (var (sp, _) in Game.Avatars.Progression) if (magic.Known(sp)) best = sp;
                    if (best is null) { await TryMeleeJa(); return; }   // no avatar granted yet -> melee fallback
                    bool avatarOut = p.Nearest(e => (e.NamePrefix & 0x08) != 0
                                        && Game.Avatars.IsAvatarName(e.Name) && p.DistanceTo(e.X, e.Z) < 20f) is not null;
                    if (!avatarOut)
                    {
                        // Summon needs MP for the cast + ongoing perpetuation — don't summon on fumes.
                        if (magic is not null && w.Mpp >= 20) { magic.Cast(best.Value, w.MyId); Log.Info($"[{tag}] summoning {best}"); await Task.Delay(4000, ct); }
                        return;
                    }
                    if (await combat.UseAbility(Ability.BloodPactRage, mob, ct)) { Log.Info($"[{tag}] Blood Pact: Rage"); await Task.Delay(2000, ct); return; }
                    await combat.UseAbility(Ability.Assault, mob, ct);   // keep the avatar attacking the target
                    return;
                }

                // ---- CASTERS: the cheapest known nuke each beat (the BLM pattern, generalized). CastLowest
                // picks the lowest READY tier; the line list covers each caster's early book.
                case Job.Blm or Job.Rdm or Job.Sch or Job.Geo:
                    if (magic is null || w.Mpp < 10) { await TryMeleeJa(); return; }   // OOM -> auto-attack carries it
                    // Nukes repeat; the DoTs (Dia/Bio) are ONE per fight — recasting them wastes MP (user
                    // 2026-07-31). Dia LAST: it's also all a lvl-1-3 RDM has (Stone is RDM 4), so a baby RDM
                    // lands one Dia then melees instead of spamming it dry.
                    foreach (var line in new[] { SpellLine.Stone, SpellLine.Water, SpellLine.Aero, SpellLine.Bio, SpellLine.Banish, SpellLine.Dia })
                    {
                        if (line is SpellLine.Dia or SpellLine.Bio && diaDone) continue;
                        if (magic.CastLowest(line, mob))   // tier selector: cheapest ready tier of the line
                        {
                            if (line is SpellLine.Dia or SpellLine.Bio) diaDone = true;
                            await Task.Delay(3000, ct);
                            return;
                        }
                    }
                    return;

                // ---- WHM: Dia + Banish ONCE each, then MELEE (user 2026-07-31). A healer's mana is for staying
                // alive — one cheap DoT (Dia doesn't stack; recasting it is wasted MP) + one Banish + the melee
                // auto-attack beats spamming Banish every beat into OOM-death (WHM: 36 deaths, 16 at MP<=6%).
                // Banish keeps an MP floor so there's mana left for EmergencyCure; melee carries the rest.
                case Job.Whm:
                    if (magic is null) { await TryMeleeJa(); return; }
                    if (!diaDone && magic.CastLowest(SpellLine.Dia, mob)) { diaDone = true; await Task.Delay(3000, ct); return; }
                    if (!banishDone && w.Mpp >= 40 && magic.CastLowest(SpellLine.Banish, mob)) { banishDone = true; await Task.Delay(3000, ct); return; }
                    banishDone = true;          // don't re-probe Banish every beat once we've cast it or MP is low
                    await TryMeleeJa();
                    return;

                // ---- BLUE MAGE: blue magic IS the damage (GM-granted + set by BluBrain's grant loop). Cast the
                // strongest READY set spell at the mob (magic.Ready gates known+level+MP); Pollen to self-heal
                // when hurt; the equipped sword melee carries the rest. Set spells that aren't learned yet just
                // fail Ready and fall through.
                case Job.Blu:
                    if (magic is null) { await TryMeleeJa(); return; }
                    if (p.World.Hpp < 55 && magic.Ready(Spell.Pollen)) { magic.Cast(Spell.Pollen, w.MyId); Log.Info($"[{tag}] Pollen (self-heal)"); await Task.Delay(2500, ct); return; }
                    if (w.Mpp >= 10)
                        foreach (var sp in Game.BlueSpells.DamageByStrength)
                            if (magic.Ready(sp)) { magic.Cast(sp, mob); Log.Info($"[{tag}] {sp}"); await Task.Delay(3000, ct); return; }
                    await TryMeleeJa();
                    return;

                // ---- NINJA: keep Utsusemi shadows up (its survivability layer) on a ~25s cadence. Gated on
                // Known, NOT magic.Ready — ninjutsu's generated Mp field carries the Shihei TOOL id (1179), not
                // real MP, so Ready never passes for a ~0-MP NIN. NinBrain supplies Shihei + learns the scroll;
                // with no tool the cast just no-ops server-side and the katana melee + WS carries.
                case Job.Nin:
                    if (magic is not null && w.NowMs - lastSongMs > 25_000 && magic.Known(Spell.UtsusemiIchi))
                    { magic.Cast(Spell.UtsusemiIchi, w.MyId); lastSongMs = w.NowMs; Log.Info($"[{tag}] Utsusemi: Ichi (shadows)"); await Task.Delay(2000, ct); return; }
                    await TryMeleeJa();
                    return;

                // ---- PALADIN (tank): Provoke to hold hate (via the /WAR sub). PLD-ONLY on purpose — Provoke
                // is NOT in the shared MeleeJas because a DD /WAR sub firing it would steal hate off the tank
                // in a party. Self-gates on the /WAR sub level (Provoke = WAR 5); sword+shield melee mitigates.
                case Job.Pld:
                    if (await combat.UseAbility(Ability.Provoke, mob, ct)) { Log.Info($"[{tag}] Provoke"); return; }
                    await TryMeleeJa();
                    return;

                // ---- CORSAIR: Phantom Roll IS the kit — a self/party buff on a 60s recast (UseAbility
                // self-resolves the target). Roll when ready; the equipped gun/dagger + WS carry damage.
                case Job.Cor:
                    if (await combat.UseAbility(Ability.PhantomRoll, mob, ct)) { Log.Info($"[{tag}] Phantom Roll"); return; }
                    await TryMeleeJa();
                    return;

                // ---- DANCER: Curing Waltz to self-heal when hurt (TP-gated server-side), else Drain Samba as
                // the maintained melee self-buff; the dagger melee + WS (KillRoutine) is the real damage.
                case Job.Dnc:
                    if (p.World.Hpp < 55 && await combat.UseAbility(Ability.CuringWaltz, mob, ct)) { Log.Info($"[{tag}] Curing Waltz"); return; }
                    if (await combat.UseAbility(Ability.DrainSamba, mob, ct)) { Log.Info($"[{tag}] Drain Samba"); return; }
                    await TryMeleeJa();
                    return;

                // ---- RUNE FENCER: keep an elemental rune up (stacks to 3; recast is instant, so THROTTLE to
                // ~30s or it fires every beat instead of meleeing). Great-sword melee + WS carry the damage.
                case Job.Run:
                    if (w.NowMs - lastSongMs > 30_000 && await combat.UseAbility(Ability.RuneEnchantment, mob, ct))
                    { lastSongMs = w.NowMs; Log.Info($"[{tag}] Rune Enchantment"); return; }
                    await TryMeleeJa();
                    return;

                // ---- MELEE/other: fire the job's signature low/mid JAs (the shared TryMeleeJa). UseAbility
                // self-gates on job/level/recast, so the whole list is safe to try.
                default:
                    await TryMeleeJa();
                    return;
            }
        };
    }

    // Signature offensive/self JAs across the melee jobs (each no-ops unless the char's job/level holds it):
    // MNK Boost/Focus/Dodge, SAM Meditate/Third Eye, DRK Last Resort, THF (SA solo), DRG Jump/High Jump,
    // BST no early JA needed, RNG Sharpshot, NIN —, PLD Sentinel late, WAR handled by its curated brain.
    static readonly Ability[] MeleeJas =
    {
        Ability.Meditate, Ability.Jump, Ability.HighJump, Ability.Boost, Ability.Focus,
        Ability.LastResort, Ability.SneakAttack, Ability.Sharpshot, Ability.Dodge, Ability.ThirdEye,
    };
}

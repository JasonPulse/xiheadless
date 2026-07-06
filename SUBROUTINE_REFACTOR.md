# Subroutine Refactor — DONE (2026-07-02)

> The refactor this doc used to plan is COMPLETE. Kept as a map of where the logic lives now + the
> validation notes from the live runs. (History: `SubjobBrain` used to hold a 600-line inline farm loop that
> reimplemented combat/movement badly — no weapon skills, camp marches into goblins, field-reunion deadlocks.)

## Where the logic lives now (folder == namespace)

- **`Routines/KillRoutine.cs`** — THE fight loop (WS + JA + re-engage + 3D-melee + kite-step + ledge-pull).
  Every combat path calls this; job flavor comes via `KillRoutine.Hooks`.
- **`Routines/RoamController.cs`** — zone-agnostic FREE ROAM. Committed hops steered by CON (cached /checks;
  avoid known con>=5 within ~20y = server sight 15y / sound 8y), `CleanPull` gate, `CanReach`-gated hop
  candidates (a void candidate snaps back to your feet = silent freeze — the zone-in bug), stall-escape.
  NO camp anchors, NO per-zone corridors, ever.
- **`Routines/Reunion.cs`** — the ONE owner of duo split/reunite. Signals: `PartyMember.Zone` (0x0DD ZoneNo,
  parsed since 2026-07-02 — nonzero = partner in THAT zone and HP fields are invalid; do NOT read Hpp==0 as
  dead without Zone==0) + party chat (0x017, cross-zone) as the fleet message bus ("RALLY").
  Protocol: wait out own KO → route via staging town → hold at the grind-zone zone-in (the one spot every
  path lands on; fresh co-located spawns rebuild both views). `combat.Homepoint` is the DEATH prompt — it
  does NOTHING while alive; never design an alive-warp around it.
- **`Routines/LevelGrind.cs`** — the ONE grind loop, solo AND party. Party hooks: `Reunion`, `PartyDuty`
  (rescue the healer), `BeforePull` (tether ready-check), `PullLeash`, `PreferTarget` (item droppers — may
  bypass the NM name-skip, NEVER the con gate), `FixedZone` (item farms), `Done`, `OnBagFull` (in-place sell).
- **`Routines/PartySupport.cs`** — healer loop; Reunion replaces its old dead-tank/stuck homepoint regroups.
  Cure tier is LEVEL-GATED IN CODE (Cure 1 / II 11 / III 21) because generated `SpellInfo` has NO level
  field — `magic.Ready` is known+MP only. A lv17 WHM "Ready" for Cure III spam-failed every cast and the
  tank died at mob-8%. (Real fix someday: generate level requirements into Spells.cs.)
- **`Routines/PartyRoutines.WaitAllGood`** — the pre-pull tether (walk TOWARD the partner, never wait still;
  bail to the caller's aggro defense when hit; timeout → `reunion.Force()`).
- **Brains are config**: `SubjobBrain` = item goals + WAR hooks + quest chain; `WarBrain`/`WhmBrain` = solo
  configs; `PartyLeechBrain` = PartySupport + Reunion config. No nav/combat loops in brains.

## Live-validated (2026-07-02, WAR lv19 + WHM lv17 duo in Buburimu)

- Cross-zone party invite lands; ZoneNo tracks the partner authoritatively; RALLY chat coordinates both
  processes; reunion completed 3/3 including a REAL one-sided WAR death (WHM homepointed too, both re-entered
  together at the zone-in — the old mutual-invisibility deadlock did not occur).
- Free roam hops off the zone-in, cons what it meets, and the clean-pull gate skipped a winnable
  Goblin_Tinkerer because a con-5 Bull_Dhalmel stood 15y away (avoidance by CON, not by name/geography).
- Bull_Dhalmel (cup dropper) still cons 5 at WAR lv19 → the loop levels on con 2-4 until the droppers enter
  the band (con cache clears on level-up; `PreferTarget` grabs them the moment they're winnable).

## Rules that must survive future edits

- Con is the SOLE arbiter (band 2-4; skip <0 objects / >=5 too tough; sleep-lock Mandragora/Saplin is the
  only name skip). No kill-failure blacklists — the dirty-pull skip is CON-based (the neighbor's con) and
  clears on level-up/empty-view.
- Never pull until the healer is close + topped (WaitAllGood); never stand still while being hit.
- No field reunions; no solo zone entry for a Reunion-managed duo.
- Movement code must not fail silently: log committed hops, log boxed-in reversals, gate MoveTo targets with
  CanReach.

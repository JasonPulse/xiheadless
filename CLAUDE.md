# CLAUDE.md — XiHeadless (FFXI headless bot)

## PRIME DIRECTIVE: REUSE, DON'T REINVENT

**ALWAYS read the existing `Routines/` and `Capabilities/` BEFORE writing any new combat/movement/party/shop/
quest logic. Do NOT write new inline code in a brain when a routine/capability already does it.**

This is the #1 rule. The single biggest time-sink in this project has been brains re-implementing logic that
already existed — e.g. `SubjobBrain.KillTarget` was hand-written and **never used weapon skills**, while
`ICombat.WeaponSkill` + `CombatRoutines.BestWeaponSkill` + `LevelGrind` already did combat correctly (it leveled
the WAR to 18). Before adding a method, `grep` the codebase for it. If a routine is close but not exact, EXTEND
or parameterize it — never fork a second copy. (See memory: reuse_and_structure, no_duplicate_methods.)

Workflow for ANY change:
1. `grep`/read `Interfaces/`, `Capabilities/`, `Routines/` for an existing implementation.
2. If it exists → call it. If it's close → extend it. Only if truly absent → add it to the right `Routines/` file.
3. Never duplicate a method. De-dup by extracting to a shared routine + deleting the copies.

**MOVEMENT (hard rule): ALL walking goes through the existing `Navigation/` stack** (`INavigation.MoveTo/
Follow/CanReach/Face/Stop`). It already handles the navmesh, path following, and the vertical-layer/ground-Y
snapping that took a day to get right. Routines/brains may only CHOOSE destinations — never write parallel
movement, dead-reckoning, or height logic. Gate roam/kite destinations with `nav.CanReach` first (an off-mesh
target snaps back to the bot's own feet and "succeeds" with a zero-length path = silent freeze).

## Architecture (folder == namespace, with a global `using` per folder)

- `Interfaces/` — capability contracts (`ICombat`, `INavigation`, `IPerception`, `IParty`, `IZoning`, `IGear`, …).
- `Capabilities/` — implementations of those (one concern each): `Combat`, `Magic`, `Navigation` (in `Navigation/`),
  `Gear`, `Inventory`, `Shop`, `AuctionHouse`, `Party`, `Events`, `Zoning`, `Lifecycle`, …
- `Routines/` — **reusable multi-step behaviors composed from capabilities.** USE THESE:
  - `CombatRoutines` — `BestWeaponSkill(skillType, skillLevel)`. Weapon-skill selection lives here.
  - `LevelGrind` — the full solo grind loop (find → build TP → WeaponSkill → abilities → kill → rest → re-camp).
    `WarBrain` uses it. Party/dropper grinds should reuse/extend this, not re-implement a kill loop.
  - `HuntRoutines` (GoToCamp/RoamDeeper/ForceAdvance), `PartyRoutines` (AutoAccept/InviteIfPresent),
    `QuestRunner.Run(steps)`, `ShopRoutines`, `StealthRoutines`.
- `Brains/` — THIN. A brain supplies config (gear, camp, con band, ability rotation, item list) and composes
  routines. It should not contain its own combat/movement loops. (`WarBrain` is the model; `SubjobBrain` is the
  anti-pattern — pending refactor, see `SUBROUTINE_REFACTOR.md`.)
- `Game/` — data + parsers (`PacketParsers`, generated `Actions`/`Spells`, `HuntZones`, `QuestDefs`, `Vendors`).

## Key capabilities that ALREADY EXIST (don't rebuild)

- Combat: `ICombat.Consider` (/check con 0..7, -1=no reply), `Engage`/`Disengage`, `UseAbility(ability, target)`
  (self-vs-enemy auto-resolved), `WeaponSkill(ws, target)`, `CanWeaponSkill` (Tp≥1000), `Tp`, `Dead`, `Engaged`.
- `CombatRoutines.BestWeaponSkill(skillType, skillLevel)` → the strongest unlocked WS for the equipped weapon.
- Magic: `IMagic.Cast(spell, target)` — **target is the ActIndex/targid, looked up from the entity; passing 0 = silent no-op** (historic bug).
- Events auto-complete generically (`BotHost.AutoCompleteEvents`); `Game/NewCharCutscene` + `StartupBlockerEvents`
  are band-aids for the 0x32 event-start recv gap (delete once reception is reliable).

## Operational rules (hard constraints — from the user)

- **Check every running bot every ~5 minutes**, regardless of run length (catch a stuck/broken bot fast).
- **LOGOUT / RELAUNCH PROCEDURE (hard rule — do not violate):**
  - **To STOP a bot: send SIGTERM (`pkill -TERM`, NOT `pkill -9`).** SIGTERM triggers the graceful ~40s logout
    (`conn.Stop`) and the process exits **clean (exit 0)**; logs show `stopping -> cancel brain + graceful logout`.
    `kill -9` skips the logout = a crash. Let the 40s finish — never `-9` a healthy bot.
  - **Relaunch cooldown (user, 2026-07-02): a NORMAL graceful logout (SIGTERM → 40s logout → "session ended
    cleanly", exit 0) only needs ~1 MINUTE before re-logging the same account. The full ≥5 MINUTES applies to
    `kill -9` / crashes / any exit without the clean logout** (server still holds the session). So: SIGTERM →
    wait for exit + "session ended cleanly" in the log → wait ~70s (log mtime) → relaunch. Crash/kill-9 → ≥5 min.
  - **Why it matters — two failures, both caused by relaunching too soon:** (1) `0xA2 / 0x24` stale-session crash
    (exit 134) — server still holds the char online. (2) **JUNK CHARACTER creation** — the locked session makes the
    next login's lobby char-list read come back EMPTY, so the intended create-on-empty path provisions a NEW lvl-1
    char; `TrySelectBest` then picks the highest-charid slot, so the junk char outranks the real one and the bot logs
    into junk. (This session I made ~5 junk chars on rmtbot this way.) The real char is NOT deleted, but a GM must
    remove the junk chars to restore selection.
  - **Do NOT "fix" the junk-char behavior in code** — create-on-empty is intentional fleet-deploy behavior, and the
    bot takes ONLY `XIBOT_ACCOUNT`/`PASSWORD`/`brain` — **there is NO character-name env var and we are not to add one.**
    The fix is purely operational: clean 40s logout, and ≥5 min wait after any early end.
- **AMBIANCE bots never use GM commands.** Everything legit/in-game (farm/AH for items+gil; self-quest job unlocks). The ONE sanctioned exception (user, 2026-07-06): a single dedicated **central GM bot** (`GmBrain`, restricted `gmlevel` 1) that grants ONLY **job unlocks** (`!grantjob`) and **limit-breaks/level-caps** (`!setcap`) — the two things bots can't self-do (Maat at 70) — to fleet bots that request them via the localhost-only `GmIntake` queue (POST {player,kind,value}, port 8089). NEVER items/gil (those stay farmed/AH). All grants are runtime/post-login (no char-creation). The GM-grant endpoint is **localhost-only** — never widen that bind. Server commands live in `xiserver/scripts/commands/{grantjob,setcap}.lua`.
- **Con is the SOLE arbiter of what to fight — NO mob name allow/block lists** (no goblin-avoid, no Dhalmel/Sylvestre
  allow-list, no NM list). Engage the winnable con band (2–4), **skip ≥5 (too tough) / ≤1 (too weak) BY CON**, and
  **never blacklist a mob because a kill failed** (winnable mobs stay eligible; clear con-skips on level-up). That's
  how dangerous mobs (e.g. con-5 goblins) get avoided — by their con, not their name; they re-enter the band as you
  level. The ONLY name-based exclusions allowed: non-combat objects (??? / Field Manual / Hieroglyphics, which con
  can't judge) and sleep-lock mobs (Mandragora/Saplin = con-blind certain death).
- **Party: the WAR never pulls/advances until ALL members are "good"** (in range + HP/MP topped + not resting), and
  never stands still while being hit. Stick together.
- Run: `bash runbot.sh <Brain> [seconds] [KEY=VAL...]`; env `XIBOT_ACCOUNT/PASSWORD/LOG`. Brains auto-register by reflection.
- Don't `cd` into or modify the LSB server repo at `/Users/jasonclift/Code/Lua/Personal/xiserver` — it's READ-ONLY
  reference (read `sql/mob_spawn_points.sql`, `mob_groups.sql`, `mob_droplist.sql` to pick camps/verify drops; the
  spawn-coord frame matches the bot's reported pos).
- Verify before claiming: a log line ("Cure WAR") is a decision, not a confirmed effect — confirm the in-game result
  (HP rose, item dropped). Trust the user's live observation over the bot's logs.

## Current work

**SIGNET — all leveling bots should get it (TODO, user 2026-07-07).** Signet is cast by a nation's **gate
guards** (San d'Oria/Bastok/Windurst) and gives **regen + refresh while resting** (big leveling speedup —
faster HP/MP recovery between fights), conquest points, and killing-blow crystals. Every grinding bot should
acquire it and **re-acquire when it expires** (Signet is time-limited). CAVEAT: on this server it may be gated
behind the nation's **first mission / an intro cutscene** — check the leveling guide and verify the gate-guard
Signet menu is reachable before wiring it (may need a mission/CS step first). Not built yet; no code exists.

Party subjob farm: WAR (Subjob) + WHM (PartyLeech) must each get 3 Buburimu items (tail-542 Mighty_Rarab,
cup-541 Bull_Dhalmel @10%, robe-540 Bogy) + WHM to lv18, then confirm the quest engine (trade Vera Mhaura →
OtherAreas bit). Buburimu(118): grind NORTH out of Mhaura (Bull_Dhalmel/Zu/Sylvestre, few goblins); NW = goblin
death-zone, never go there. Pending fix: SubjobBrain must reuse `CombatRoutines`/`LevelGrind` (weapon skills +
abilities + clean engagement) instead of its broken inline `KillTarget`. Full plan: `SUBROUTINE_REFACTOR.md`.

**FLEET party doctrine (user spec 2026-07-08 — don't re-litigate; engine: SessionPlan/PartyFinder/
PartyCombat/FleetSchedule):**
- Travel to the level-appropriate hunt zone FIRST (HuntZonePlan = the leveling guide), THEN party there.
- Day plan seeded (charid+UTC date): Party/Solo/Upkeep days; session 3-8h; recruitment via zone /shout
  (NEVER /yell — region-wide), lenient LFP parsing (parties are OPEN — real players may join).
- Composition: full = Tank/Healer/Support/3DD; minimum start = Tank/Healer/DD (roles per bg-wiki
  Jobs_&_party_roles via Game/PartyRoles).
- Camp vs roam: party >3 ANCHORS at camp, only the puller leaves; exactly 3 roams.
- Puller: BRD ALWAYS pulls if present (pull-and-sleep the next mob — Foe Lullaby — for chain efficiency).
  Else THF(Trick Attack)+second tank-capable: SUB-TANK pulls, SATA line subtank-mob-TANK-thief (tank at the
  mob's back, thief behind the tank, SA+TA+WS plants hate on the tank). Else the MAIN TANK pulls AT RANGE
  (Provoke, else non-expendable boomerang Shoot) — the mob must never beat on the puller on the way home.
- Done-for-the-day: parties converge on the LATEST member end time (ENDAT on party chat); at done, announce
  DONE, KEEP PLAYING YOUR ROLE until all fellow members are DONE (10-min cap for humans), never log out
  engaged/attacked, goodbye -> Leave -> clean 40s logout. Nobody leaves mid-combat or strands the group.

**Party-cohesion mechanics (hard-won — don't re-litigate):**
- **NO Raise** — WHM Raise is a lv25 spell; our WHM is lower, so an in-place rez is IMPOSSIBLE. Do NOT add a
  "wait for the healer to Raise" death path (tried; the WHM can't cast it, so the WAR just lies dead ~2min then
  homepoints anyway). Death recovery = Home Point, then REUNITE.
- **One-sided-death desync** is the core hazard: when only the WAR dies, it Home Points to Mhaura + re-crosses to
  the Buburimu zone-in (~Z-223), ~200y from the live WHM still at camp (~Z-20). Their clients then hold STALE views
  of each other and never re-sync → mutual invisibility → the WAR loops rally/wait. Fix = on a WAR death the WHM
  ALSO Home Points to Mhaura (PartySupport dead-tank branch) so BOTH reset to town and cross back TOGETHER, landing
  at the zone-in co-located (fresh entity spawns). "Just get them together" (user).
- **KITE** (SubjobBrain.KillTarget): a mob whose HP won't drop in 3D melee is on a ledge/slope the server counts as
  out of reach — step ~7y away and it follows onto reachable ground (validated: mobHP then falls). Not a navmesh
  relocate. **Roam** = short ~22y north hops gated on StayWithWhm (never the old 150-600y Roam.Far march, which
  outran the WHM's ~50y view). **StayWithWhm** walks TOWARD the WHM (never stands still — a stationary WAR stops
  broadcasting, so the WHM's view goes stale); if it can't see the WHM at all it heads to the rally camp.

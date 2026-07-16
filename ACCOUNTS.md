# Accounts (user rule: EVERY account/password used by bots or tests is noted HERE)

Any username/password that doesn't exist **auto-creates on first login** (account + char via
create-on-empty). So a typo'd login = a new junk account — check this list before launching.

## Deployed (fleet/service — do NOT use for local testing)

| Account | Password | Char | Purpose |
|---|---|---|---|
| `rmtbot` | `rmtbotpass` | Zzshekashi (WHM/BLM) | RMT service bot (cluster CronJob). **Off-limits locally** (user, 2026-07-09). |
| `gmbot`* | `gmbotpass`* | Guge (GM lv1) | GM grant bot (cluster CronJob). *Convention-derived — verify if it differs. |
| `fleetbot01`-`fleetbot18` | `<account>pass` | auto-created at first wave | Afternoon wave (WAVE_OFFSET=0), brains round-robin from the 22-job pool. |
| `fleetbot19`-`fleetbot36` | `<account>pass` | auto-created at first wave | Evening wave (WAVE_OFFSET=18). |
| — | — | `fleetbot36` → **Griasha** (DRG plan, Bastok) | Auto-created 2026-07-09 by the local pre-flight rehearsal (this is the wave char that account will keep using). |

## Local test accounts

| Account | Password | Char | Purpose |
|---|---|---|---|
| `headlesstesting` | `password1234` | Zzthenenfen (id 30, PLD/WAR32, San d'Oria HP) | Original test account; trio-test TANK. |
| `bot1` | `bot1pass` | Zenku (id 38, THF20) | Trio-test DPS. |
| `testheal` | `testhealpass` | Zutu (id 47, WHM, Windurst) | Trio-test HEALER (created 2026-07-09, replaces rmtbot in local party tests). |

## Conventions

- Fleet/test passwords follow `<account>pass` (exception: headlesstesting).
- Char names are generated (NameGen, human-like, no fleet prefix) — the ACCOUNT is the stable identifier.
- One char per account (`TrySelectBest` picks the highest char id — a junk char OUTRANKS the real one; see
  CLAUDE.md logout/relaunch rules for how junk chars happen and why only a GM can remove them).

## Junk accounts (2026-07-16 shell-bug incident — chars deleted, empty rows remain)
A zsh word-splitting bug in a batch loop auto-created 8 junk accounts (login includes a trailing
" whm", password = login + "pass"). All 7 junk chars were deleted via the client path the same day;
the empty account rows below have no client deletion path — remove via SQL or leave (they own nothing):
`fleetbot03 whm`, `fleetbot09 whm`, `fleetbot10 whm`, `fleetbot15 whm`, `fleetbot20 whm`,
`fleetbot21 whm`, `fleetbot25 whm`, `fleetbot31 whm` (accounts.id 1047-1054).
NEVER log into these; never reuse the pattern. Account strings in scripts must be sourced from
ACCOUNTS.md or the DB — never assembled in shell loops.

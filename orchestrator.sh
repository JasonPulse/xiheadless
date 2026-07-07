#!/usr/bin/env bash
# ============================================================================
# FLEET ORCHESTRATOR — gates the GM and RMT bots on who is actually online,
# distinguishing REAL PLAYERS from our own bots so the two service bots don't
# deadlock each other (each counts as a session, so a naive "sessions > 0"
# gate keeps them both online forever).
#
# THE SEPARATOR (validated against live data):
#   The world server exposes counts, not identities:
#     GET {WORLD_URL}/api/sessions -> COUNT(*)                FROM accounts_sessions
#     GET {WORLD_URL}/api/ips      -> COUNT(DISTINCT client_addr) FROM accounts_sessions
#   ALL our bots (GM, RMT, every fleet bot) run on ONE host, so from the
#   server they share ONE client_addr. Each REAL PLAYER connects from their
#   own IP. Therefore:
#       real players present  <=>  ips >= 2
#   (the single bot-host IP is #1; any 2nd distinct IP is a player). Lingering
#   / ghost bot sessions after a crash stay on that one host IP, so they can
#   NEVER create a false "player" — which is why `sessions` can read 3 while
#   `ips` reads 1 with every bot stopped.
#
# THE GATES (break the mutual-session deadlock — neither counts the other):
#   RMT up  <=>  a real player is present            (ips >= 2)
#   GM  up  <=>  a real player OR a fleet bot present (ips >= 2 OR fleetOnline>=1)
#   fleetOnline = our leveling/farming bots running on this host (pgrep), which
#   EXCLUDES Gm and Rmt — so GM never stays up merely because RMT is up, and
#   RMT never stays up merely because GM is up. Two idle service bots -> both
#   gates read 0 -> both log out.
#
# Assumes: all bots run on THIS host (one source IP). If you ever distribute
# bots across hosts, switch the separator to an explicit account registry.
# ============================================================================
set -u
cd "$(dirname "$0")" || exit 1

# --- config (env-overridable) ------------------------------------------------
WORLD_URL="${XIBOT_WORLD_URL:-http://ffxi-world-service}"   # in-cluster; local test: http://172.25.75.80:8088
POLL_SEC="${ORCH_POLL_SEC:-30}"          # how often to re-evaluate
UP_HOLD_SEC="${ORCH_UP_HOLD_SEC:-30}"    # signal must hold this long before we START a bot (debounce)
DOWN_GRACE_SEC="${ORCH_DOWN_GRACE_SEC:-180}"  # keep a bot up this long after its gate drops (player zoning, session lag)
GM_ACCOUNT="${ORCH_GM_ACCOUNT:-headlesstesting}"   # account each service bot logs in on (own line each)
GM_PASSWORD="${ORCH_GM_PASSWORD:-}"                 # optional; runbot.sh defaults apply if unset
RMT_ACCOUNT="${ORCH_RMT_ACCOUNT:-rmtbot}"
RMT_PASSWORD="${ORCH_RMT_PASSWORD:-}"

# --- world API ---------------------------------------------------------------
api() { curl -s -m 5 "$WORLD_URL/api/$1" 2>/dev/null; }
uniq_ips()  { local v; v=$(api ips);      [[ "$v" =~ ^[0-9]+$ ]] && echo "$v" || echo -1; }
sessions()  { local v; v=$(api sessions); [[ "$v" =~ ^[0-9]+$ ]] && echo "$v" || echo -1; }

# fleet bots running on THIS host = `bash runbot.sh <Brain>` wrappers that are NOT Gm/Rmt.
# Key on "bash runbot.sh" (the wrapper's argv) NOT the bare "runbot.sh" — the latter also matches
# this very pipeline's own grep process (argv contains "runbot.sh"), a self-count false positive.
# pgrep keys on "bash runbot" (the wrapper prefix); the exclusion grep's pattern deliberately OMITS
# that prefix so this pipeline's own grep argv can't self-match the pgrep. Counts non-Gm/Rmt wrappers.
fleet_online() { pgrep -af "bash runbot" 2>/dev/null | grep -vcE "runbot\.sh (Gm|Rmt)\b" ; }

# --- per-bot process control (brain -> /tmp/<brain>.log, find dotnet via lsof) -
logpath() { echo "/tmp/orch_$(echo "$1" | tr '[:upper:]' '[:lower:]').log"; }
bot_running() { pgrep -f "bash runbot.sh $1\b" >/dev/null 2>&1; }

start_bot() { # $1=brain $2=account $3=password
  bot_running "$1" && return 0
  local log; log=$(logpath "$1")
  echo "[$(date +%T)] START $1 (account=$2) -> $log"
  XIBOT_ACCOUNT="$2" ${3:+XIBOT_PASSWORD="$3"} XIBOT_LOG="$log" bash runbot.sh "$1" >/dev/null 2>&1 &
}

stop_bot() { # $1=brain — GRACEFUL SIGTERM to the dotnet child (never kill -9)
  bot_running "$1" || return 0
  local log pid; log=$(logpath "$1"); pid=$(lsof -t "$log" 2>/dev/null | head -1)
  echo "[$(date +%T)] STOP $1 (graceful SIGTERM, ~40s logout)"
  [ -n "$pid" ] && kill -TERM "$pid" 2>/dev/null
  pkill -TERM -f "runbot.sh $1\b" 2>/dev/null
}

# --- hysteresis state --------------------------------------------------------
declare -A want_since=( [Gm]=0 [Rmt]=0 )   # first tick the gate WANTED it up (0 = not wanted)
declare -A drop_since=( [Gm]=0 [Rmt]=0 )   # first tick the gate WANTED it down while still running

# ensure(brain, want, account, password): apply hysteresis + start/stop
ensure() {
  local brain="$1" want="$2" acct="$3" pass="$4" now; now=$(date +%s)
  if [ "$want" -eq 1 ]; then
    drop_since[$brain]=0
    if bot_running "$brain"; then return; fi
    [ "${want_since[$brain]}" -eq 0 ] && want_since[$brain]=$now
    if [ $(( now - ${want_since[$brain]} )) -ge "$UP_HOLD_SEC" ]; then start_bot "$brain" "$acct" "$pass"; fi
  else
    want_since[$brain]=0
    if ! bot_running "$brain"; then return; fi
    [ "${drop_since[$brain]}" -eq 0 ] && drop_since[$brain]=$now
    if [ $(( now - ${drop_since[$brain]} )) -ge "$DOWN_GRACE_SEC" ]; then stop_bot "$brain"; drop_since[$brain]=0; fi
  fi
}

echo "[$(date +%T)] orchestrator up — world=$WORLD_URL poll=${POLL_SEC}s up-hold=${UP_HOLD_SEC}s down-grace=${DOWN_GRACE_SEC}s"
while true; do
  ips=$(uniq_ips); sess=$(sessions); fleet=$(fleet_online)
  if [ "$ips" -lt 0 ]; then echo "[$(date +%T)] world API unreachable ($WORLD_URL) — holding current state"; sleep "$POLL_SEC"; continue; fi

  # Subtract the ONE bot-host IP only when a bot is actually on the host (else a lone player, whose ips=1,
  # is missed). host_present = any of our bots online here. Remaining distinct IPs => real players.
  # (Caveat: stale accounts_sessions rows from a CRASHED bot can hold the host IP with no live process; the
  # UP_HOLD debounce + clean-logout row cleanup keep that from mis-starting RMT for long.)
  host_present=0
  if bot_running Gm || bot_running Rmt || [ "$fleet" -ge 1 ]; then host_present=1; fi
  player_ips=$(( ips - host_present )); [ "$player_ips" -lt 0 ] && player_ips=0
  players=$(( player_ips >= 1 ? 1 : 0 ))
  gm_want=$(( players == 1 || fleet >= 1 ? 1 : 0 ))
  rmt_want=$players

  echo "[$(date +%T)] ips=$ips sessions=$sess fleet=$fleet | players=$players -> GM want=$gm_want, RMT want=$rmt_want"
  ensure Gm  "$gm_want"  "$GM_ACCOUNT"  "$GM_PASSWORD"
  ensure Rmt "$rmt_want" "$RMT_ACCOUNT" "$RMT_PASSWORD"
  sleep "$POLL_SEC"
done

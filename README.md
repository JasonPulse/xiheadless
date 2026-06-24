# XiHeadless

A headless Final Fantasy XI player client (.NET 9) for a private [LandSandBoat](https://github.com/LandSandBoat/server) server.
It speaks the FFXI wire protocol directly — no game client — so a bot can log in, zone, fight,
craft, trade, and run a personal bazaar as an autonomous "player." Bots run one-per-character as
Kubernetes pods, each given an account + a *brain*.

## Architecture

Two layers:

- **Capabilities** (`Capabilities/`) — the verbs a bot can perform, each an interface + implementation
  backed by the connection: `IPerception`, `IChat`, `ICombat`, `IMagic`, `INavigation`, `IGear`,
  `IEvents`, `IZoning`, `IDelivery`, `IBazaar`, `ICrafting`, `IGilGrant`. These hold all the
  protocol/packet detail; they are the unit of reuse.
- **Brains** (`Brains/`) — a brain is one bot's top-level behavior, an imperative coroutine that
  composes capabilities (it constructor-injects only the ones it needs). One brain runs per bot.

Underneath sits a from-scratch protocol stack (`Net/`): the custom FFXI Blowfish cipher, the Huffman
packet (de)compressor, packet framing, the lobby/login handshake, and a persistent map-server session.
World state and packet parsing live in `Game/`; navmesh pathfinding (DotRecast) in `Navigation/`.

## Brains

| Brain | What it does |
|-------|--------------|
| `War` | Cons nearby mobs, navigates into melee, engages, weaponskills, and runs the death → home-point → return loop. |
| `Rmt` | "Gil seller" simulation: yells advertising (with a website URL), serves an in-bot order page, and mails purchased gil to buyers via the Mog House delivery box. |
| `Bazaar` | Prices its inventory and opens a personal bazaar. |
| `Equip` | Equips a weapon and reports inventory/skills. |
| `HomePoint` | Completes the new-character cutscene and sets a home point. |
| `Observe` | Read-only: logs nearby entities (debugging). |
| `Mnk` | Minimal melee example sharing the combat routines. |

## Configuration

A bot is configured by **three** environment variables — everything else is coded into the brain:

| Variable | Purpose |
|----------|---------|
| `XIBOT_ACCOUNT` | Account name |
| `XIBOT_PASSWORD` | Account password |
| `XIBOT_BRAIN` | Which brain to run (e.g. `War`, `Rmt`, `Bazaar`) |

The character is auto-selected from the account (an empty account auto-creates one). Optional
deployment settings: `XIBOT_API_URL` / `XIBOT_API_TOKEN` (the server's gil-grant endpoint) and
`XIBOT_NAVMESH_DIR` (mounted navmeshes, needed by navigating brains).

## Running

With Docker (image published to GHCR by CI):

```sh
docker run --rm \
  -e XIBOT_ACCOUNT=myaccount \
  -e XIBOT_PASSWORD=secret \
  -e XIBOT_BRAIN=War \
  ghcr.io/jasonpulse/xiheadless:latest
```

The `Rmt` brain serves its storefront on port `8088` (bound to all interfaces for in-cluster routing).

## Build & image

```sh
dotnet build XiHeadless.csproj
```

Pushes to `main` trigger `.github/workflows/docker.yml`, which builds the image and publishes it to
`ghcr.io/jasonpulse/xiheadless` (`:latest` + a `:sha` tag).

## Layout

```
Net/           protocol stack (crypto, compression, lobby + map connection)
Game/          world state, packet parsers, generated spell/action data
Capabilities/  the verbs (one file per capability)
Brains/        one file per brain + the shared runner/routines
Navigation/    navmesh loading + pathing (DotRecast)
res/           decompress/compress jump tables
```

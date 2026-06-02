# Pro-Imitator

A CounterStrikeSharp plugin that layers **per-bot personality presets** on top
of the rest of the ed0ard/CS2-Bot-Improver suite. When a bot is spawned via
`bot_add_ct "ZywOo"` (or any name listed in a profile's `MatchByName`), the
plugin reads `profiles/<pro>.json` and applies a coherent set of behavioural
biases each tick so the bot starts to *play* like that pro.

The goal is not to make bots aim-bot strong — `BotAI` and `BotAimImprover`
already cover raw mechanical skill. The goal is **identity**: when you
spectate a round of "Vitality vs Spirit on de_dust2", the bots should
visibly fragment into recognisable roles — donk rushing site, sh1ro
holding long, apEX coordinating from second-in, etc. — and play the round
the way a pro team would.

## What it does — and what it deliberately does **not**

| It does                                                                  | It does **not** |
|--------------------------------------------------------------------------|-----------------|
| Pick a profile based on the bot's in-game name at spawn                  | Override the global `bot_aim` style (use `bot_aim head` manually) |
| Apply trait-driven per-tick writes to `CCSBot` schema fields             | Patch the CS2 binary (that's `BotAI`'s job) |
| Buy the role's preferred weapon if the bot has the cash                  | Refund-then-buy (no fake money — see V4.7 spec below) |
| Swap to the role weapon when the bot is holding something else           | Hook native `PickNewAimSpot` (that's `BotAimImprover`'s job) |
| Track pre/post-plant phase and invert attacker/defender roles            | Override the engine's bombsite assignment |
| Hold the knife between angles when no opponent is in combat range        | Drive pathing or waypoint following (no nav-mesh override) |
| Detect a dropped bomb and harden CT defence around it                    | Distribute CTs across A / B / mid (engine handles that) |

The plugin is **additive** and only writes properties that are either left
alone by `BotState` or that `BotState` sets to the same value (e.g.
`PanicTimer = 0`). It never fights another plugin's intent.

## Installation

The same way as every other plugin in the suite — once built, drop the
output folder into `csgo/addons/counterstrikesharp/plugins/`:

```
ProImitator/
├── ProImitator.dll
├── ProImitator.deps.json
├── ProImitator.pdb
└── profiles/
    ├── _template.json
    ├── donk.json
    ├── ZywOo.json
    └── (… 25 more)
```

Files in `profiles/` whose name starts with `_` are skipped at load
(treated as examples / templates), so `_template.json` ships safely.

For local development there's a `toggle_cs2_install.ps1` helper that
copies the freshly-built DLL + profiles into the CS2 install:

```powershell
.\toggle_cs2_install.ps1 enable   # deploy to CS2
.\toggle_cs2_install.ps1 disable  # remove from CS2 (other plugins untouched)
.\toggle_cs2_install.ps1 status   # is it installed?
```

## Usage

### Public commands (no admin required)

These are plain game console commands — no `css_` prefix, so they don't
hit the CSS admin permission check. Same UX as `bot_aim` / `bot_nades`.

| Command | What it does |
|---|---|
| `pro_list` | List all profiles loaded from disk, with their roles |
| `pro_assigned` | List bots that currently have a profile attached |
| `pro_reload` | Re-read profiles from disk after editing JSON |
| `pro_debug` | Toggle verbose chat logging for behaviour events |

### Spawning a profiled bot

The bot's in-game name must match an entry in some profile's
`MatchByName` array. Case-insensitive.

```
bot_add_ct "ZywOo"
bot_add_t  "donk"
```

The plugin logs the attach on the server console:

```
[Pro-Imitator] attached profile 'ZywOo' to bot 'ZywOo' (slot 2)
```

For pre-rostered team setups see `Commands.txt` at the repo root.

### Quick match — Vitality (CT) vs Spirit (T) on Dust2

```
map de_dust2
```

(wait for the map to load)

```
sv_cheats 1; mp_warmuptime 0; mp_warmup_end; mp_maxrounds 24; mp_overtime_enable 1;
mp_match_can_clinch 1; bot_difficulty 3; mp_friendlyfire 0; mp_autoteambalance 0;
mp_limitteams 0; bot_kick; pro_reload;
bot_add_ct "apEX"; bot_add_ct "ZywOo"; bot_add_ct "ropz"; bot_add_ct "mezii"; bot_add_ct "flameZ";
mp_teamlogo_1 vita; mp_teamname_1 Team Vitality;
bot_add_t "donk"; bot_add_t "zont1x"; bot_add_t "magixx"; bot_add_t "tN1R"; bot_add_t "sh1ro";
mp_teamlogo_2 spir; mp_teamname_2 Spirit;
mp_restartgame 3; pro_assigned; jointeam 1
```

## Profiles

26 profiles ship with the plugin, covering the 2026 rosters of five
top-tier teams.

### Team Vitality (FR/EU)
| Bot | Role | Trait highlights |
|---|---|---|
| apEX | IGL, Support | Calm captain, Rifler, BombFocus |
| ZywOo | AWPer | Patient hold, AWPer |
| ropz | Lurker | Sneaks, Rifler, no BombFocus (flanks on his own) |
| mezii | Support | Anchor, Rifler, BombFocus |
| flameZ | Entry Fragger | AlwaysRushing, Rifler, BombFocus |

### FURIA Esports (BR)
| Bot | Role | Trait highlights |
|---|---|---|
| FalleN | IGL, Support | Veteran caller, Rifler, BombFocus |
| yuurih | Lurker, Entry Fragger | Sneaks, Rifler |
| KSCERATO | Entry Fragger, Support | AlwaysRushing, Rifler, BombFocus |
| YEKINDAR | Entry Fragger | Aggressive entry, BombFocus |
| molodoy | AWPer | Patient hold, AWPer |

### Team Falcons (SA)
| Bot | Role | Trait highlights |
|---|---|---|
| karrigan | IGL, Support | Strategic veteran, Rifler, BombFocus |
| NiKo | Entry Fragger, Lurker | Star rifler, Rifler, BombFocus |
| m0NESY | AWPer | Aggressive AWP variant |
| TeSeS | Support | Anchor, Rifler, BombFocus |
| kyousuke | Entry Fragger | Young aim freak, Rifler, BombFocus |

### Natus Vincere (UA/EU)
| Bot | Role | Trait highlights |
|---|---|---|
| Aleksib | IGL, Support | Tactical caller, Rifler, BombFocus |
| iM | Entry Fragger | Dynamic, Rifler, BombFocus |
| b1t | Lurker, Support | Sneaks, Rifler |
| w0nderful | AWPer | Patient hold, AWPer |
| makazze | Entry Fragger | Hyper-aggressive, Rifler, BombFocus |

### Team Spirit (CIS)
| Bot | Role | Trait highlights |
|---|---|---|
| magixx | IGL, Support | New captain, Rifler, BombFocus |
| sh1ro | AWPer | World-class closer, AWPer |
| tN1R | Lurker, Support | CT anchor / T lurker, Rifler |
| zont1x | Entry Fragger | Space-taker, Rifler, BombFocus |
| donk | Entry Fragger | Star entry, Rifler, BombFocus |

Role taxonomy follows the [dmarket CS2 roles guide](https://dmarket.com/blog/cs2-roles-guide/)
(Entry Fragger / Support / Lurker / AWPer / IGL). Multi-role bots use a
comma-separated string in their `Role` field; the primary role drives the
behavioural flag template.

## Architecture

The plugin is a single-file C# implementation (`ProImitator.cs`). Behaviour
is organised into five "versions" of features that build on each other:

### V1 — Foundation
Profile loading from JSON, per-bot slot tracking, basic trait flags:
`AlwaysRushing`, `NeverSneaks`, `NeverPolite`, `NoSafeTime`, `NoPanic`.
Each flag maps to one or two `CCSBot` schema writes per tick.

### V2 — Visual identity
Behavioural quirks that make the bot read as a specific player:
`NoCrouchWithRifle` (stand-spray identity), `NeverWaitsBetweenShots`,
`NoApproachPause`, and a probabilistic `CounterStrafeChance` that
zeroes lateral velocity on the false→true edge of `IsAimingAtEnemy`
so the bot's first shot lands like a real counter-strafe.

### V3 — Role weapons
`Rifler` and `AWPer` flags express the bot's main weapon. Per-tick logic
forces `use weapon_<name>` when the bot is holding the wrong slot. At
round-freeze a strict buy logic runs:

- If the bot has the role weapon's full price in cash AND doesn't already
  own any same-class weapon (rifle for Rifler, sniper for AWPer), buy it.
- No refund credits, no swap math — strict price-in-cash gate. This is
  the V3 spec the user asked for: *"juste la manière dont BotBuy fonctionne
  avec des préférences par rôle, pas de manip de thune"*.
- **V4.7 update**: if the bot already owns ANY rifle (e.g. a CT mezii
  carrying over an AK picked up off a dead T), the role buy is skipped.
  AK is universally preferred and a CT willingly keeps a picked-up AK
  over their default M4.

### V4 — Objective awareness (BombFocus)
The `BombFocus` flag biases the bot toward the round's win condition
instead of mid-round fragfest behaviour. Implementation went through
several calibration passes (V4.1 → V4.8) based on playtest feedback;
see the in-code banner comment in `ApplyPersonality` for the full
design discussion.

Current behaviour (V4.8):

| Phase | Side | Effect |
|---|---|---|
| Pre-plant | T attacker | `HurryTimer` 10s biased toward objective |
| Pre-plant | T attacker (bomb carrier) | `HurryTimer` 30s + 50/50 `IsRunning` coinflip on 3s windows |
| Pre-plant | CT defender | `SafeTime` 6s + `HurryTimer` explicitly cleared |
| Pre-plant | CT defender (within 1500u of a dropped bomb) | `SafeTime` 9s — hold the bomb perimeter harder |
| Post-plant | T defender | `SafeTime` 6s + `HurryTimer` cleared — hold the plant |
| Post-plant | CT attacker | `HurryTimer` 10s — push to retake / defuse |

Pre/post-plant flip is driven by `EventBombPlanted` setting a `_bombPlanted`
flag, reset on `EventRoundStart`. The dropped-bomb position is found each
tick by walking weapon_c4 entities for an unowned one.

The bomb-carrier extra push is a **slight** bias (HurryTimer 30s vs 10s
for other Ts) plus a 50/50 coinflip on whether to force `IsRunning=true`
for the next 3-second window. Earlier iterations (V4.1) forced
`IsRunning=true` permanently and wiped `SneakTimer / PoliteTimer /
IsWaitingBehindFriend` — that produced a hold-W zombie carrier that
*"se jetait sur le site"*. V4.4 dialled it back to subtle.

### V4.5 — KnifeRush + Observability + Dropped-bomb defence
- `KnifeRush` flag: hold the knife (≈20% movement bonus) whenever no
  alive opponent is within 1500u of the bot. As soon as an opponent
  enters that radius, the Rifler / AWPer per-tick switch pulls the
  primary. **V4.8 dropped the time window — the trait is now pure
  distance-gated**, no `KnifeRushSec` time parameter, behaviour applies
  any time during the round.
- `pro_debug` command: routes lifecycle / state-change events to the
  in-game chat as `[ProDBG]` lines. Server console gets the same lines
  via `Console.WriteLine`. Useful to verify that a feature is actually
  firing (e.g. *"the knife isn't appearing, is my code even running?"*).
- Dropped-bomb defence: CTs with BombFocus and within 1500u of a
  dropped (unowned) C4 entity get an extra-firm hold (SafeTime 9s).
  Denies T pickup attempts.

### V4.7 — Schema-level active weapon write
Earlier versions issued `slot3` (and briefly `use weapon_knife` via
`Server.ExecuteCommand`, which doesn't exist as a CS2 cvar) for the
knife switch. Playtests showed both commands lose the race against the
engine's bot-AI weapon selection, which runs every server tick.

V4.7 added a direct schema write to `CPlayer_WeaponServices.m_hActiveWeapon`
that bypasses the command queue entirely. The `slot3` command is kept
as a belt-and-braces fallback in case the schema field is ever renamed.
Wrapped in try/catch so a future CSS API change can't crash the server
tick on this polish feature.

## Trait reference

| Flag | Effect (per tick) |
|---|---|
| `AlwaysRushing` | `HurryTimer.Duration` and `Timestamp` maxed; `IsRunning` true |
| `NeverSneaks` | `SneakTimer` zeroed |
| `NeverPolite` | `PoliteTimer` zeroed; `IsWaitingBehindFriend` false |
| `NoSafeTime` | `SafeTime` zeroed |
| `NoPanic` | `PanicTimer` zeroed |
| `NoCrouchWithRifle` | `IsCrouching` forced false when active weapon is a rifle |
| `NeverWaitsBetweenShots` | `FireWeaponTimestamp` zeroed; `IsRapidFiring` true |
| `NoApproachPause` | `InhibitLookAroundTimestamp` zeroed; `CheckedHidingSpotCount` zeroed |
| `Rifler` | Per-tick switch to AK / M4 (AK preferred when owned); role-buy at freeze |
| `AWPer` | Per-tick switch to AWP / SSG08; role-buy at freeze |
| `CounterStrafeChance` | Probability-gated lateral-velocity stop on engagement onset |
| `KnifeRush` | Hold knife when no opponent within 1500u (V4.8 distance-only) |
| `BombFocus` | Objective bias: HurryTimer for attackers, SafeTime for defenders, pre/post-plant inverts |

All flags default to `false`. Profiles opt in to whatever fits the
role. See `profiles/_template.json` for the documented field-by-field
reference and `profiles/donk.json` for a worked example.

## Adding a new pro

1. Copy `profiles/_template.json` to e.g. `profiles/s1mple.json`.
2. Set `Name`, `MatchByName`, `Role`, `Description`.
3. Set behavioural flags to match the role template — the comments in
   `_template.json` document the per-role canonical trait set.
4. Run `pro_reload` to pick up the change without restarting CS2.

If you need a trait that isn't represented by an existing flag, the
recipe is:

1. Add a `bool` property to `ProProfile` in `ProImitator.cs` (default `false`).
2. Add an `if (prof.NewTrait) { … }` block to `ApplyPersonality` that writes
   the relevant `ref` field(s) on `CCSBot`.
3. Document it in `_template.json` so contributors see it.

That's it — no event hooks or service plumbing for a new personality
dimension, just a tiny per-tick write.

## What's intentionally out of scope

The plugin deliberately doesn't try to:

- **Override the engine's bombsite assignment** — CS2's nav system
  already assigns CT bots to A / B / mid at scenario init. Forcing a
  specific 2/2/1 split would fight that. Documented as future work
  in the V4 banner.
- **Map-specific path overrides** — no Dust2-specific `mid_doors`
  knowledge baked into the plugin. The 1500u distance threshold for
  KnifeRush / dropped-bomb defence is a tunable but it's the only
  spatial constant.
- **Memory patches** — those live in `BotAI`. ProImitator only writes
  schema fields and issues commands.
- **Aim style override per bot** — that would need a hook into
  `BotAimImprover` so a sibling plugin can opt a single bot into a
  different priority chain (head/jaw/body) without flipping the global
  mode. Suggested upstream change, not in this plugin.
- **Reaction-time scaling** — `AlertTimer` / `IgnoreEnemiesTimer`
  per-profile would be a clean extension if needed.

## Credits

By **Ouistiti**.

Layered on top of the [ed0ard/CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver)
suite — none of this would exist without the BotAI / BotState / BotBuy /
BotAimImprover plumbing that handles the heavy lifting.

Role taxonomy from the [dmarket CS2 roles guide](https://dmarket.com/blog/cs2-roles-guide/).

Roster information cross-referenced from HLTV team pages and Liquipedia,
2026 active rosters as of the time of writing.

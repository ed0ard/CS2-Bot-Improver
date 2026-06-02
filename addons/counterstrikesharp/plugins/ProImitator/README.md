# Pro-Imitator

A small CounterStrikeSharp plugin that layers **per-bot personality presets** on
top of the rest of the CS2-Bot-Improver suite. When a bot is spawned via
`bot_add_ct "donk"` (or any name listed in a profile's `MatchByName`), this
plugin reads `profiles/<pro>.json` and writes a handful of `CCSBot` properties
each tick so the bot starts to *feel* like that player.

## What it changes — and what it deliberately does **not**

| It does                                                                  | It does **not** |
|--------------------------------------------------------------------------|-----------------|
| Pick a profile based on the bot's in-game name at spawn                  | Override the global `bot_aim` style (use `bot_aim head` manually for donk) |
| Per-tick set timers like `HurryTimer`, `SneakTimer`, `PoliteTimer`       | Patch the CS2 binary (that's `BotAI`'s job) |
| Reset `SafeTime` / `PanicTimer` for profiled bots                        | Hook native `PickNewAimSpot` (that's `BotAimImprover`'s job) |
| Persist profile -> slot until team change                                | Drive pathing or waypoint following (no nav-mesh override) |

The whole plugin is **additive** and only writes properties that are either
left alone by `BotState` or that `BotState` sets to the same value (e.g.
`PanicTimer = 0`). It never fights another plugin's intent.

## Installation

The same way as every other plugin in the suite — once built, drop the output
folder into `csgo/addons/counterstrikesharp/plugins/`.

```
ProImitator/
├── ProImitator.dll
├── ProImitator.deps.json
├── ProImitator.pdb
└── profiles/
    ├── donk.json
    └── _template.json
```

`_template.json` is skipped at load (filenames starting with `_` are treated as
examples), so it ships safely.

## Usage

1. Make sure `BotAimImprover` is loaded and set the global aim style to match
   the profiled bot's character. For donk:
   ```
   bot_aim head
   ```
2. Spawn the pro by their exact nick:
   ```
   bot_add_ct "donk"
   ```
   The plugin logs the attach on the server console:
   ```
   [Pro-Imitator] attached profile 'donk' to bot 'donk' (slot 7)
   ```
3. Inspect at any time:
   ```
   css_pro_list       # all profiles loaded from disk
   css_pro_assigned   # which live bots currently have a profile
   css_pro_reload     # re-read profiles after editing JSON
   ```

## Adding a new pro

Copy `profiles/_template.json` to e.g. `profiles/zywoo.json`, set `Name` and
`MatchByName`, then toggle the behavior flags. Run `css_pro_reload` to pick up
the change without restarting the server.

If you need a trait that isn't represented by an existing flag, the recipe is:

1. Add a `bool` property to `ProProfile` in `ProImitator.cs` (default `false`).
2. Add an `if (prof.NewTrait) { ... }` block to `ApplyPersonality` that writes
   the `ref` field(s) on `CCSBot`.

That's it — no event hooks or service plumbing for a new personality dimension,
just a tiny per-tick write.

## V1 scope vs. what's intentionally left for later

V1 is deliberately small. Things on the wish-list:

- **Per-bot `bot_aim` style override**: would need to add a hook in
  `BotAimImprover` so a sibling plugin can opt a single bot into a different
  priority chain (head/jaw/body) without flipping the global mode.
- **Reaction-time scaling**: writing `AlertTimer` / `IgnoreEnemiesTimer` with
  profile-specific timescale.
- **Weapon affinity**: integrating with `BotBuy` so a profiled bot leans
  toward a specific buy when the user runs the coordinated-buy commands.

These all live in the same architectural niche (additive, profile-driven, no
fights with other plugins) — they just weren't needed to validate the V1
concept.

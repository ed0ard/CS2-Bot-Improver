<div align="center">

# Local Arena

**English** | [简体中文](README.zh-CN.md)

<br/>

<a href="https://github.com/numakkiyu/Local-Arena/releases"><img alt="Release" src="https://img.shields.io/github/v/release/numakkiyu/Local-Arena?display_name=tag&sort=semver"></a>
<img alt="Platform" src="https://img.shields.io/badge/platform-Windows-0078D4">
<a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/numakkiyu/Local-Arena"></a>

<br/>
<br/>

[Download a published build](https://github.com/numakkiyu/Local-Arena/releases) · [Report an issue](https://github.com/numakkiyu/Local-Arena/issues) · [Brand and independence](#brand-renaming-and-project-independence) · [Source attribution](#upstream-source-and-attribution)

</div>

> [!IMPORTANT]
> Local Arena is an independently developed and maintained Windows toolkit for local CS2 matches, player cosmetics, demos, diagnostics, and managed installation
>
> Local Arena is not affiliated with, endorsed by, or maintained by [ed0ard/CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver) or its maintainers. The upstream project does not provide support for Local Arena
>
> For any Local Arena build, Panel, installation, matching, cosmetics, diagnostics, crash, or update problem, [open an issue in this repository](https://github.com/numakkiyu/Local-Arena/issues). Do not submit Local Arena reports to the upstream project
>
> Selected enhanced-bot components remain derived from upstream AGPL-3.0 code. Their origin and authorship remain credited below, while development, releases, issue tracking, and user support are managed independently by Local Arena
>
> During the repository transition, existing installations intentionally retain the legacy executable name, `.csbip` state directory, icon, and Panel appearance so online updates, backups, presets, and match history remain compatible

<div align="center">

The current `main` branch targets **1.4.2.6**

**Guide:** [First installation](#four-step-first-installation) · [Existing installation](#updating-an-existing-installation) · [Launch modes](#choose-the-correct-mode) · [Cosmetics](#player-cosmetic-presets) · [Recovery](#installation-updates-and-recovery) · [Troubleshooting](#troubleshooting)

</div>

<p align="center">
  <img src="./Panel/src/assets/guide/01-overview.png" alt="Local Arena current Panel overview" width="100%">
</p>

---

## Brand Renaming and Project Independence

Following an explicit request from the upstream project author for clear brand separation, and to further clarify project ownership and maintenance responsibilities, this project will be formally renamed from **CS2BotImproverPlus** to **Local Arena** beginning with the next version

**Local Arena** is independently developed, released, and maintained. It is not affiliated with, authorized by, jointly maintained with, officially partnered with, or supported by **CS2-Bot-Improver** or its author. The project only uses, modifies, and redistributes selected open-source code under the [AGPL-3.0](https://github.com/numakkiyu/Local-Arena/blob/main/LICENSE) license, while continuing to preserve all required source attribution, authorship notices, and license declarations

Beginning with the next version, Local Arena will adopt a new UI design language and brand logo. To preserve user data, update, installation, and legacy-version compatibility, some older releases, historical interfaces, filenames, and compatibility identifiers may continue to display the former brand name during the transition

## What Local Arena Adds

- CT and T player loadouts with independent knives, gloves, and weapon skins
- Shared weapons can use one linked skin or separate CT and T skins
- Human-player music kit presets and compatible StatTrak or Souvenir options
- Three launch modes for online play, cosmetic preview, and enhanced bots
- A four-step installer that detects clean CS2, legacy compatible builds, and the original upstream plugin
- Transactional backups, installation verification, repair, rollback, and pristine-CS2 recovery
- Separate online updates for the Panel and plugin payload
- One-click diagnostic ZIP export that opens the output folder automatically
- A built-in guide with real screenshots and troubleshooting steps

## Before You Start

> [!WARNING]
> Close CS2 before installing, repairing, restoring, updating plugins, changing difficulty, or switching modes

- Local Arena is currently packaged for Windows
- Extract the complete ZIP to a normal folder before opening the Panel
- Keep the legacy executable, `addons`, `cfg`, `overrides`, and `plus-payload-manifest.json` together
- Do not run the Panel from inside the ZIP
- The correct game directory ends with `Counter-Strike Global Offensive\game\csgo`
- Cosmetic preview and enhanced-bot mode use `-insecure` and cannot enter official matchmaking
- For Linux builds and upstream-only installation, use the [upstream project](https://github.com/ed0ard/CS2-Bot-Improver)

## Four-Step First Installation

### 1. Choose the Panel language

The language selection only changes the Panel and does not write anything to CS2

Panel memory, settings, logs, update cache, and preserved presets are stored in the portable `.csbip` folder beside the Panel

<p align="center">
  <img src="./Panel/src/assets/guide/08-first-language.jpg" alt="Choose the Panel language" width="100%">
</p>

### 2. Confirm the `game/csgo` directory

The Panel searches Steam registry data, every `libraryfolders.vdf`, and the CS2 app manifest

- One valid installation is selected automatically
- Multiple installations require you to choose the one actually launched by Steam
- Use Browse only when automatic detection cannot find the correct installation
- Do not select the CS2 root, `game`, `bin`, or the Panel folder

<p align="center">
  <img src="./Panel/src/assets/guide/09-first-directory.jpg" alt="Select the CS2 game directory" width="100%">
</p>

### 3. Review the installation plan

The preview identifies the existing environment before changing files

| Detected environment | Panel action | Preserved data |
| --- | --- | --- |
| Clean CS2 | Install Local Arena | Existing files are backed up before replacement |
| Managed Local Arena | Update or repair Local Arena | Original backup and player presets are retained |
| Legacy installation | Adopt and update | Existing cosmetics and migration files are preserved |
| Original upstream plugin | Replace with Local Arena | The upstream installation is backed up first |
| Mixed or unknown plugins | Block automatic installation | Export diagnostics or return to pristine CS2 first |

<p align="center">
  <img src="./Panel/src/assets/guide/10-first-preview.jpg" alt="Review the installation plan" width="100%">
</p>

### 4. Install and enter the Panel

Installation uses a transaction journal, verifies every copied file, and rolls back completed steps if an operation fails

Do not launch CS2, close the Panel, or repeatedly click the install button while the transaction is running

<p align="center">
  <img src="./Panel/src/assets/guide/11-first-complete.jpg" alt="Installation completed" width="100%">
</p>

## Updating an Existing Installation

Prefer **Settings → Online Update** when it is available

For a manual package update, close CS2 and the old Panel, extract the new package into the existing portable Panel folder, and keep the hidden `.csbip` folder

If the new package must use a different folder, copy the complete old `.csbip` folder beside the new Panel before opening it so the original backups, installation records, presets, and logs remain connected

The installer can distinguish a managed Local Arena installation, a legacy installation, the original upstream plugin, and a partial mixed environment

It does not treat your cosmetic JSON, selected difficulty, or managed bot options as corrupted payload files

<p align="center">
  <img src="./Panel/src/assets/guide/12-first-mixed.jpg" alt="Existing or mixed plugin environment detection" width="100%">
</p>

If the environment is classified as mixed or unknown, do not manually overwrite it again

Export diagnostics, use **Restore pristine CS2** for recognized enhanced-plugin files, run Steam file verification, and then perform a clean first installation

## Choose the Correct Mode

| Mode | What is enabled | Matchmaking |
| --- | --- | :---: |
| Normal matchmaking | Enhanced-plugin loading is disabled | Available |
| Cosmetics preview | PlayerCosmetics only, with official normal bots | Blocked |
| Enhanced bots | Full upstream bot systems and player cosmetics | Blocked |

Always choose a mode in Overview before launching CS2 from the Panel

### Normal matchmaking

Use this mode for ordinary online play

The Panel removes the managed MetaMod search path and does not add `-insecure`

### Cosmetics preview

Use this mode when you only want to inspect your knife, gloves, guns, and music kit

Enhanced-bot AI, difficulty, buying, profiles, agents, and behavior systems are disabled, while official normal bots continue to work

### Enhanced bots

Use this mode for the complete Local Arena experience

It enables all synchronized upstream bot features, the selected difficulty, bot items, commands, and player cosmetics

## Player Cosmetic Presets

### CT and T weapons

The Weapon Presets page separates CT-only, T-only, and shared weapons

- CT-only and T-only weapons keep independent team presets
- Shared weapons link both teams by default
- Disable **Use the same skin for CT/T** to configure each side independently
- Enabling the link again copies the currently edited side to the other team
- Compatible catalog entries expose StatTrak or Souvenir controls
- StatTrak values are written back to the matching team preset

<p align="center">
  <img src="./Panel/src/assets/guide/02-weapon-presets.png" alt="CT and T weapon presets" width="100%">
</p>

### CT and T knives and gloves

Knife and glove dialogs share the current CT or T selection

Each team stores its own model, paint kit, wear, pattern seed, name tag, default knife, and supported StatTrak values

Only the default knife held by the player is guaranteed to receive the configured appearance, while dropped ground knives are not given live cosmetic rendering

### Bot presets

The Presets page controls bot aiming, grenade behavior, the dropped-knife key, and entry points for player knife and glove settings

<p align="center">
  <img src="./Panel/src/assets/guide/03-bot-presets.png" alt="Bot behavior and player knife presets" width="100%">
</p>

## Other Panel Pages

### Bot Items

Bot skins, profiles, agents, and music kits can be enabled independently without overwriting the human player's CT and T presets

<p align="center">
  <img src="./Panel/src/assets/guide/06-bot-items.png" alt="Bot item controls" width="100%">
</p>

### Commands

Commands are grouped by common actions, bot behavior, teams, coordinated purchases, and connection tasks

Select a category or search by purpose, then click a command to copy the exact console text

<p align="center">
  <img src="./Panel/src/assets/guide/07-commands.png" alt="Search and copy CS2 commands" width="100%">
</p>

The original upstream command collection remains available in [Commands.txt](https://github.com/ed0ard/CS2-Bot-Improver/blob/main/Commands.txt)

## Installation, Updates, and Recovery

### Installation health

**Settings → Installation and Recovery** shows the detected environment, installed version, managed-file health, backup location, and available actions

Changing cosmetics, CT/T presets, difficulty, or managed bot options must not be reported as payload corruption

<p align="center">
  <img src="./Panel/src/assets/guide/04-installation-recovery.jpg" alt="Installation and recovery page" width="49%">
  <img src="./Panel/src/assets/guide/13-health-repair.jpg" alt="Installation health and repair" width="49%">
</p>

### Online updates

The Panel and plugin payload are checked and installed separately

- Startup checks are non-blocking and cached for six hours
- Manual checks bypass the cache
- Plugin updates require the selected CS2 process to be closed
- Downloads are verified by signature, size, and SHA-256 before installation
- Player presets use the preserve-config policy and are not overwritten by repair or update

<p align="center">
  <img src="./Panel/src/assets/guide/05-online-update.png" alt="Panel and plugin online updates" width="100%">
</p>

### Recovery actions

| Action | Use it when | Result |
| --- | --- | --- |
| Verify installation | You want a fresh health result | Performs a read-only managed-file check |
| Repair installation | Managed files are missing or damaged | Reinstalls only affected payload files |
| Restore original state | A managed Local Arena installation must be rolled back | Restores installation-time backups and removes files created by Local Arena |
| Restore pristine CS2 | Local Arena or upstream enhanced plugins must be removed | Deletes recognized enhanced-plugin files, preserves unknown third-party files, then asks for Steam verification |
| Export diagnostics | A problem is reproducible or unclear | Creates a ZIP and opens its folder automatically |

Player cosmetic presets are copied to the portable `.csbip/presets` area before a managed restore

## Troubleshooting

### All directory and file states are red

The Panel has not found a valid `game/csgo` directory, so install and launch actions remain unavailable

Open **Settings → Directory**, select the folder that directly contains `gameinfo.gi` and `cfg`, then refresh the inspection

<p align="center">
  <img src="./Panel/src/assets/guide/15-directory-missing.jpg" alt="CS2 directory is missing" width="100%">
</p>

### The environment is mixed or unknown

The Panel found only part of Local Arena or the upstream plugin together with files whose ownership cannot be proven safely

Export diagnostics before deleting or overwriting anything, use **Restore pristine CS2** to remove recognized enhanced-plugin files, run Steam file verification, and then start a clean first installation

### Buttons are disabled or installation appears stuck

The selected CS2 installation is probably still running or another installation transaction owns the file lock

Close CS2 completely, wait for `cs2.exe` to disappear, keep the Panel open, and retry after the status refreshes

<p align="center">
  <img src="./Panel/src/assets/guide/14-process-lock.jpg" alt="CS2 process lock" width="100%">
</p>

### One or more managed files are reported as modified

Click **Verify installation** first

Only use **Repair installation** when a managed payload file is actually missing or damaged, and keep CS2 closed during repair

Cosmetic presets, difficulty selections, and supported bot option files are preserved and should not be counted as corruption

### Online update cannot connect or verification fails

The updater stops before installation when network, signature, size, hash, compatibility, or rollback validation fails

Keep the current version, confirm GitHub connectivity, retry a manual check, and export diagnostics if the same error repeats

<p align="center">
  <img src="./Panel/src/assets/guide/16-update-error.jpg" alt="Online update error details" width="100%">
</p>

### Cosmetics do not appear

- Use Cosmetics preview or Enhanced bots mode
- Confirm the current CT or T knife, glove, and weapon presets are enabled
- Verify and repair the managed installation before using recovery actions
- Normal matchmaking intentionally disables PlayerCosmetics

### Restore original state does not produce clean CS2

**Restore original state** returns a managed installation to its recorded pre-install state, which may itself contain a legacy compatible build or upstream plugin

Use **Restore pristine CS2** when all recognized enhanced-plugin files must be removed, then complete Steam file verification before launching the game

### CS2 freezes or crashes

Reopen the Panel immediately and use **Export diagnostics**

Send the ZIP together with the selected mode, map, game type, team, and exact reproduction steps

For team-selection crashes, include how long the game remained on the team selection screen before CT or T was chosen

## Upstream Source and Attribution

Local Arena distributes selected AGPL-3.0 enhanced-bot components derived from [ed0ard/CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver), including

1. More capable and human-like bot aim
2. Situation-aware grenade usage
3. Improved bot movement and stuck handling
4. Expanded weapon buying and economy management
5. Spraying, flicking, smoke spam, and anti-flash behavior
6. Bot knives, gloves, weapon skins, agents, music kits, avatars, and profiles
7. More organized and alert bot decision making
8. Professional and randomized player names based on HLTV data
9. Bot-friendly game-rule adjustments
10. Additional console commands and team lineups

The upstream project is a source dependency and attribution target, not a Local Arena support channel. For its original implementation, Linux instructions, and documentation, visit [ed0ard/CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver)

## Credits

- [ed0ard/CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver)
- [Metamod:Source](https://github.com/alliedmodders/metamod-source)
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace)
- [CS2-Bot-Randomizer](https://github.com/ed0ard/CS2-Bot-Randomizer)
- [CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider)
- [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller)
- [CS2-BotAI](https://github.com/ed0ard/CS2-BotAI)
- [CS2-Bot-Buy](https://github.com/ed0ard/CS2-Bot-Buy)
- [CS2-Bot-NadeSystem](https://github.com/ed0ard/CS2-Bot-NadeSystem)
- [RoundDamageRecap](https://github.com/YuGeYu/LBTV-CS2-Bot-Enhancer/tree/main/addons/counterstrikesharp/plugins/RoundDamageRecap)

## License

[AGPL-3.0](LICENSE)

---

<div align="center">

[Back to top](#local-arena)

</div>

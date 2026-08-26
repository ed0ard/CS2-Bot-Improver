<div align="center">

# CS2-Bot-Improver

[![Latest release](https://img.shields.io/github/v/release/ed0ard/CS2-Bot-Improver?display_name=tag&sort=semver)](https://github.com/ed0ard/CS2-Bot-Improver/releases/latest)
[![Release downloads](https://img.shields.io/github/downloads/ed0ard/CS2-Bot-Improver/total)](https://github.com/ed0ard/CS2-Bot-Improver/releases)
[![License: AGPL-3.0](https://img.shields.io/badge/license-AGPL--3.0-blue.svg)](LICENSE)
![Platforms: Windows and Linux](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-5c6bc0)

**English** · [简体中文](docs/README.zh-CN.md) · [Русский](docs/README.ru.md)

[Features](#features) · [Installation](#installation) · [Commands](#commands) · [Panel Guide](#panel-guide-windows-only) · [FAQ](#faq)

</div>

CS2-Bot-Improver enhances Counter-Strike 2 bots for offline matches and private games with friends. It improves their aim, movement, nade throwing, personalities, strategies, and can be installed on either a client or a dedicated server.

## **Your stars⭐ are my motivation to keep updating**

## Features

| Field | Improvements |
| --- | --- |
| **Aim and combat** | More accurate, human-like aim; spraying, flicking, smoke spamming, and anti-flash |
| **Grenades** | Situational Smoke, Flashbang, HE grenade, and Molotov throwing |
| **Movement** | Better movement and fixes for most bot-stuck situations |
| **Strategy** | Smarter, more organized bots with improved awareness and decision-making |
| **Economy** | Expanded weapon purchases and overhauled economy management |
| **Personalities** | Pro and random player names, with pro characteristics based on [HLTV](https://www.hltv.org/) stats |
| **Customization** | Per-bot knives, gloves, weapon skins, stickers, charms, agents, music kits, avatars, and profiles |
| **Game experience** | Bot names without prefixes, bot-friendly rules, and extra console commands for fun |

## Installation

Download the package for your operating system from the **[latest release](https://github.com/ed0ard/CS2-Bot-Improver/releases/latest)**.

### Windows

1. Download and extract **CS2BotImprover.zip**.
> [!NOTE]
> Running a dedicated server that is not only for bot matches?  
On Windows, please download **CS2BotImprover_rules_unchanged.zip** to preserve the standard game rules.
2. Move **Panel v1.4.3.exe** somewhere convenient.

   <img width="128" height="128" alt="CS2 Bot Improver Panel application icon" src="https://github.com/user-attachments/assets/7271dc7d-2436-484b-8359-6531f4abd710" />

3. Open your CS2 installation folder and navigate to `game/csgo`.

   <img width="405" height="256" alt="The game/csgo directory inside a CS2 installation" src="https://github.com/user-attachments/assets/ae2be90e-6742-4f1f-8e0c-096b728d5dbd" />

4. Copy all remaining files from the extracted package folder into `game/csgo`.

   <img width="540" height="181" alt="Copying the Windows package files into game/csgo" src="https://github.com/user-attachments/assets/6a8645fc-78e7-4f3a-92d3-5d1b6d913918" />

5. Open `Panel v1.4.3.exe`, select **Bot Mode**, then click **Launch CS2**.

   <img width="339" height="129" alt="Selecting Bot Mode and launching CS2 from the Panel" src="https://github.com/user-attachments/assets/dc806991-c940-43cf-a614-f49012fae4a7" />

### Linux

1. Download and extract **CS2BotImprover_for_Linux.zip**.
2. Move `Commands.txt` somewhere convenient.
3. Open your CS2 installation folder and navigate to `game/csgo`.

   <img width="405" height="256" alt="The game/csgo directory inside a CS2 installation" src="https://github.com/user-attachments/assets/ae2be90e-6742-4f1f-8e0c-096b728d5dbd" />

4. Copy all remaining files from the extracted package folder into `game/csgo`.

   <img width="535" height="180" alt="Copying the Linux package files into game/csgo" src="https://github.com/user-attachments/assets/9bda7b1d-43d3-49cf-a283-27b124b894e0" />

5. Add `-insecure` to your CS2 launch options.

   <img width="130" height="153" alt="Opening CS2 properties in Steam" src="https://github.com/user-attachments/assets/4c775e36-3fc3-4a19-9cb1-4f0c9327838c" /><br>
   <img width="625" height="423" alt="Adding -insecure to the CS2 launch options" src="https://github.com/user-attachments/assets/ac0b0c57-ee67-4e33-96fb-146d14714fc8" />

## Commands

### Aim

| Command | Description |
| --- | --- |
| `bot_aim mixed` | Select aiming spots dynamically based on situations. **(Default)** |
| `bot_aim head` | Prioritize aiming at the head. |
| `bot_aim body` | Prioritize aiming at the torso. |
| `bot_aim` | Show the current aim mode. |

### Nades

| Command | Description |
| --- | --- |
| `bot_nades off` | Disable bot nade usage. |
| `bot_nades less` | Use the same decision logic as normal mode with lower count limits. |
| `bot_nades normal` | Use count limits close to those of human players. **(Default)** |
| `bot_nades more` | Use the same decision logic as normal mode with higher count limits. |
| `bot_nades max` | Bots have minimal limitations and think less before throwing nades. |
| `bot_nades` | Show the current nade mode. |

### Bot AI modules

Every BotAI patch belongs to a documented module group. Toggle whole groups in the config file, or list individual patch names for fine-grained control:

`game/csgo/addons/counterstrikesharp/configs/plugins/BotAI/BotAI.json`

```json
{
  "ConfigVersion": 2,
  "Modules": {
    "Awareness": false,
    "BombInfo": false,
    "CombatForce": true,
    "Movement": true,
    "VisionAttention": true,
    "BombBehavior": true,
    "StateMachine": true
  },
  "DisabledPatches": []
}
```

| Module | What it does | Recommended |
| --- | --- | --- |
| `Awareness` | Superhuman perception: unlimited vision cones, global hearing, aggressive noise investigation. Makes bots wallhack-ish. | `false` for casual fair play |
| `BombInfo` | Global C4 intel: pickup/beeps heard anywhere and instant site knowledge on plant. Deletes the post-plant information game. | `false` for casual fair play |
| `CombatForce` | Forced combat: no fire-rate limit, hold-trigger sprays at any range, 100% dodge chance (dice removed). | `true` if you want strong bots; `false` if gunfights feel unfair |
| `Movement` | Movement quality fixes: strafing unlocked, dodge while reloading, anti-sniper movement. Vanilla randomness kept. | `true` |
| `VisionAttention` | Vision/attention enhancements: watching approach points, always noticing enemies. | `true` |
| `BombBehavior` | Plant/defuse behavioral fixes (stop pulling knife out, fight back while defusing). Pure realism. | `true` |
| `StateMachine` | Misc state fixes, including removing built-in flash avoidance so bots can be blinded like humans. | `true` |

- `DisabledPatches` lists individual patch names to skip on top of module toggles; unknown names are reported at load time.
- Legacy `CasualAwareness` (ConfigVersion 1) is still honored: `true` forces the Awareness group off even when `Modules.Awareness` is true. Remove the field to rely on `Modules` alone.
- Memory patches are applied at load time only — restart or reload the plugin after changing this file.

#### Per-patch reference

<details>
<summary><strong>All BotAI patches grouped by module</strong></summary>

| Patch | What it changes |
| --- | --- |
| **Awareness** ⛔ superhuman perception | |
| `InViewCone_RemoveOuterFOV` | Removes the outer vision-cone limit (bots see almost all around them) |
| `InViewCone_RemoveInnerFOV` | Removes the inner vision-cone limit |
| `OnAudibleEvent_GlobalHearRange` | Bots hear audible events from anywhere on the map |
| `InvestigateNoise_SkipSelfDefenseCheck` | Bots investigate noises without the self-defense check |
| **BombInfo** ⛔ global C4 intel | |
| `BombPickup_CT_GlobalHearRange` | CT bots hear the bomb pickup anywhere on the map |
| `BombBeep_CT_GlobalHearRange` | CT bots hear bomb beeps anywhere on the map |
| `OnBombPlanted_AllBotsLearnSite` | Every bot instantly learns the planted site when the bomb goes down |
| **CombatForce** forced combat | |
| `AttackState_SkipFireRateCheck` | Removes the fire-rate gate — bots can shoot every simulation tick |
| `AttackState_SkipSteadyFireShortcut` | No early exit from the steady-fire routine (longer sprays) |
| `AttackState_SkipZoomFireShortcut` | No early exit from the zoom-fire routine |
| `SprayAllDistances_ForceHoldTrigger` | Holds the trigger continuously at any range (no burst discipline) |
| `AttackState_DodgeChance100_Always` | Removes the dodge dice roll — always takes the high-skill dodge path |
| `AttackState_RetreatOnSniper_Disable` | Stops retreating just because an enemy sniper is visible |
| `AttackState_SkipSniperSpreadCheck` | Skips extra weapon spread when engaging snipers |
| **Movement** realism fixes (vanilla randomness kept) | |
| `AttackState_CanStrafe_jne` | Unlocks strafing while attacking |
| `AttackState_DodgeDuringReload` | Allows dodging while reloading |
| `SniperCrouchDodge_jb` | Crouch-dodge reaction against snipers |
| `SniperDodge_SkipIsSniper_DodgeA` | Any bot can use the anti-sniper dodge moves |
| `AllSkill_KeepMoving_WhenSeeSniper` | Keeps moving when spotting a sniper (all skill levels) |
| `LowSKill_JumpChance0` | Removes random jumping for low-skill bots (less silly randomness) |
| **VisionAttention** | |
| `Vision_SkipIsMovingGate` | Bots notice targets even while moving themselves |
| `Vision_AlwaysEnterApproachBody_Cave` | Always enter approach-body attention nodes (cave variant) |
| `Vision_AlwaysEnterApproachBody` | Always enter approach-body attention nodes |
| `Vision_AlwaysWatchApproachPoints_Cave` | Always watch approach points (cave variant) |
| `Vision_AlwaysWatchApproachPoints` | Always watch approach points |
| `Vision_AlwaysWatchApproachPoints_LoopEntry_Cave` | Always watch approach-point loop entries (cave variant) |
| `Vision_AlwaysWatchApproachPoints_LoopEntry` | Always watch approach-point loop entries |
| `Vision_ApproachBody_SkipSkillCheck` | Ignore the skill threshold before approach-body checks (Windows) |
| `Vision_ApproachBody_SkipHidingSpotCheck` | Watch bodies without hiding-spot filtering |
| `IsNoticable_AlwaysTrue` | Bots always notice enemies — stealth and flanking lose their effect |
| **BombBehavior** realism fixes | |
| `EscapeFromBomb_OnEnter_NoEquipKnife` | Don't pull the knife out when escaping a planted bomb |
| `EscapeFromBomb_OnUpdate_NoEquipKnife` | Same as above, enforced every update tick |
| `EscapeFromFlames_OnEnter_NoEquipKnife` | Don't pull the knife out when escaping Molotov flames |
| `PlantBombLookAtPriorityLow` | Lower look-at priority while planting (watch surroundings instead) |
| `DefuseBombLookAtPriorityLow` | Lower look-at priority while defusing |
| `DefuseBomb_SkipIsVisibleCheck` | Can start defusing without a line-of-sight ceremony |
| `TBot_BombsiteSearch_UseKnownPlantedSite` | T bots search the known planted site first |
| `CT_Defuse_EngageAndInvestigate` | CTs engage nearby enemies then continue the defuse |
| `DefuseBombState_OnEnter_EngageAndInvestigate` | Fight back when entering the defuse state |
| `DefuseBombState_OnUpdate_EngageAndInvestigate` | Fight back throughout the defuse state |
| **StateMachine** misc fixes | |
| `HasVisitedEnemySpawn` | Bots remember visited enemy spawns for smarter searching |
| `GameState_Reset` | Properly reset bot game state between rounds/phases |
| `Idle_IsSafeAlwaysFalse` | Idle bots no longer assume their spot is safe (stay alert) |
| `FlashbangAvoidance_Disable` | Removes built-in flash avoidance so bots can be blinded like humans |
| `Upkeep_BotCOS_ZeroDrift` | Removes cosine-based idle look drift (Windows) |
| `Upkeep_BotSIN_ZeroDrift` | Removes sine-based idle look drift (Windows) |

</details>

### Skins

| Command | Description |
| --- | --- |
| `br_reroll` | Reroll every bot's skins on their next spawn. |

### Buying

Enter a weapon name in the game console to give every bot this weapon from the next round.  
Enter `bot_buy` to restore normal purchase behavior.

<details>
<summary><strong>Show supported weapon names</strong></summary>

```text
elite     p250      fn57      deagle    cz75a     r8
bizon     p90       mp5sd     mp9       mp7       mac10     ump45
mag7      sawedoff  nova      xm1014
famas     galilar   m4a1      m4a1s     ak47      aug       sg556
ssg08     awp       scar20    g3sg1
negev     m249
```

</details>

### Pro teams

Copy a block of team commands from [Commands.txt](Commands.txt) and paste it into the game console. You can also add your own teams using the same format.

For example, the following block in `Commands.txt` adds Team Vitality to the CT side:

<img width="301" height="237" alt="Team Vitality commands in Commands.txt" src="https://github.com/user-attachments/assets/a895f3a6-58f8-47dc-b6f5-b60c1b32fecd" />

### Knives

Point at the ground and press `\` on your keyboard to generate all kinds of knives there.

### Flying Scoutsman

After a match begins, use the command `scouts_on` or `scouts_off` to enable or disable Flying Scoutsman mode.

## Panel Guide (Windows Only)

### Status lights

| Indicator | Meaning |
| --- | --- |
| 🟢 Green | No issues detected. |
| 🟡 Yellow | Restart CS2 to apply changes. |
| 🔴 Red | Files missing. Click the red light to view the list of missing files. |

<img width="481" height="82" alt="Green, yellow, and red Panel status indicators" src="https://github.com/user-attachments/assets/26a947e2-4e0e-423f-bce8-f220d88509a2" />

### Matchmaking & Bot Mode Toggle

Select your desired mode, then click **Launch CS2**.

<img width="472" height="179" alt="Online Mode and Bot Mode selector in the Panel" src="https://github.com/user-attachments/assets/3f9254fa-4cbe-4854-8fd1-0f35228fff77" />

### Settings

Click the <img width="31" height="32" alt="Settings" src="https://github.com/user-attachments/assets/7f94176b-79f1-4e22-9495-4589c4dea9eb" /> icon in the top-right corner to open **Settings**.

### Command browser

Click **Commands**, click a block to copy it automatically, or type keywords to search.

<img width="350" height="420" alt="Searchable command browser in the Panel" src="https://github.com/user-attachments/assets/957cfafb-900d-4450-b985-13d3e8efc375" />

## FAQ

<details>
<summary><strong>How to play bot matches with friends?</strong></summary>

1. Start a bot match, enter any required commands, and then run `status` in the console.

   <img width="597" height="141" alt="The steamid value shown by the status command" src="https://github.com/user-attachments/assets/792c4b4f-1d56-4a39-9186-b301cbff1846" />

2. Copy the text after `steamid:`, add `connect ` before it (don't forget the space between them) 
3. Send the full command to your friends and have them paste it into their consoles.

</details>

<details>
<summary><strong>How to manually change the difficulty level?</strong></summary>

1. Navigate to `game/csgo/overrides` in your CS2 installation folder.
2. Open `Low` for easy difficulty, `Medium` for mixed difficulty based on HLTV stats (**default**), or `High` for extreme difficulty.
3. Copy the selected `botprofile.vpk` into `game/csgo/overrides` before launching the game.

</details>

<details>
<summary><strong>How to manually switch back to normal online matchmaking mode?</strong></summary>

1. Navigate to `game/csgo/backup/Online` in your CS2 installation folder.
2. Copy `gameinfo.gi` into `game/csgo` (Replace the file in the destination).
3. Remove `-insecure` from your launch options.

To play with bots again, copy `gameinfo.gi` from `game/csgo/backup/WithBots` into `game/csgo`, and restore the launch option.

</details>

<details>
<summary><strong>How to manually disable bot weapon skins, agents, music kits, knives, and gloves?</strong></summary>

1. Navigate to `game/csgo/addons/counterstrikesharp/plugins` in your CS2 installation folder.
2. Rename `BotRandomizer` folder to `BotRandomizer_disabled`.
3. Open `addons/counterstrikesharp/configs/core.json` and set `FollowCS2ServerGuidelines` to `true`.

</details>

<details>
<summary><strong>How to manually disable bot Steam profiles?</strong></summary>

Navigate to `game/csgo/addons` in your CS2 installation folder and rename `BotHider` folder to `BotHider_disabled`.

</details>

<details>
<summary><strong>How to use the plugin normally on Workshop maps?</strong></summary>

Add `-disable_workshop_command_filtering` to your launch options.

</details>

<details>
<summary><strong>How to surf normally?</strong></summary>

Run `sv_standable_normal 0.7` in the game console.

</details>

### What are the supported-use and responsibility boundaries?

> [!WARNING]
> This project is intended for offline bot matches, self-hosted private games with friends, and private dedicated servers used for bot play. `BotRandomizer` applies cosmetics **only to bots**; it does not grant, falsify, or alter the Steam inventory, skins, or profile of a human player. **This boundary is designed to follow [Valve's CS2 community-server and GSLT rules](https://help.steampowered.com/en/faqs/view/07AF-502E-A104-BD4B).**
>
> The project is not intended or supported for Valve official matchmaking, [FACEIT](https://support.faceit.com/hc/en-us/articles/360015788779-What-is-deemed-to-be-a-cheat), or other third-party public community servers.
>
> The [AGPL-3.0 license](LICENSE) does not grant access to third-party services or authorize violations of their rules. To the maximum extent permitted by applicable law, anyone who uses or deploys the project outside the scope described above, modifies it to evade security controls, or otherwise violates third-party terms assumes all resulting risks and responsibilities, including GSLT or server sanctions, FACEIT or community-server bans, VAC or game bans. The maintainers and contributors disclaim liability for those consequences.

## Credits

- [Metamod:Source](https://github.com/alliedmodders/metamod-source)
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace)
- [CS2-Bullseye-Bot](https://github.com/ed0ard/CS2-Bullseye-Bot)
- [CS2-Bot-NadeSystem](https://github.com/ed0ard/CS2-Bot-NadeSystem)
- [CS2_ExecAfter_No_Admin](https://github.com/ed0ard/CS2_ExecAfter_No_Admin), forked from [kus](https://github.com/kus)
- [CS2-Bot-Randomizer](https://github.com/ed0ard/CS2-Bot-Randomizer)
- [CS2-Lib](https://github.com/ianlucas/cs2-lib) by [Lucas](https://github.com/ianlucas)
- [CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider) by [XBribo](https://github.com/XBribo)
- [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) by [XBribo](https://github.com/XBribo)
- [CSGOBetterBots](https://github.com/manicogaming/CSGOBetterBots/blob/master/addons/sourcemod/data/bot_info.json) by [manico](https://github.com/manicogaming)
- [CS2-Smarter-Bot](https://github.com/ed0ard/CS2-Smarter-Bot)
- [CS2-BotAI](https://github.com/ed0ard/CS2-BotAI), forked from [Austin](https://github.com/Austinbots)
- [CS2-Bot-Buy](https://github.com/ed0ard/CS2-Bot-Buy)
- [RoundDamageRecap](https://github.com/YuGeYu/LBTV-CS2-Bot-Enhancer/tree/main/addons/counterstrikesharp/plugins/RoundDamageRecap) by [YuGeYu](https://github.com/YuGeYu)
- [Apple-Style-GUI](https://github.com/ed0ard/Apple-Style-GUI)

## License

[GNU Affero General Public License v3.0](LICENSE)

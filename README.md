# CS2-Bot-Improver
CS2-Bot-Improver is a plugin for Counter-Strike 2 that improves bots' aim, movement, nade throwing, personalities, strategies, etc.

Aims to enhance your experience when playing against bots offline or with friends. It can be installed on both clients and servers.

## Your stars⭐ are my motivation to keep updating

## Features

1. Makes bots aim better and more human-like
2. Allows bots to throw nades deftly according to the situation
3. Fixes most bot stuck issues
4. Improves bots' movement
5. Allows bots to buy everything and overhauls their economy management
6. Refines bot behavior, allowing them to spray, flick, spam smokes and anti-flash
7. Assigns each bot their own agent model, music kit and avatar
8. Makes bots smarter, more organized, and more alert to their surroundings
9. Changes bot names to pro and random players. (the characteristics of each pro player are based on stats from [HLTV](https://www.hltv.org/))
10. Removes the prefix from bot names
11. Tweaks game rules to make them more friendly to bots
12. Adds some commands to make the game more fun

## Installation

1. Download the latest **CS2BotImprover.zip** in [Releases](https://github.com/ed0ard/CS2-Bot-Improver/releases) and unzip it

   (If you run a dedicated server that is not only for bot matches, please download **CS2BotImprover_rules_unchanged.zip**)
    
   (For Linux, please download **CS2BotImprover_for_Linux.zip**)

<img width="405" height="256" alt="snap_1" src="https://github.com/user-attachments/assets/ae2be90e-6742-4f1f-8e0c-096b728d5dbd" />

2. Open the root of CS2 and navigate to `game/csgo` directory

<img width="348" height="123" alt="snap_2" src="https://github.com/user-attachments/assets/c6dcfc51-0062-44a7-9c9b-e8f094b8d8b3" />

3. Copy all the files in `CS2BotImprover` and paste them into it

<img width="130" height="153" alt="snap_3" src="https://github.com/user-attachments/assets/4c775e36-3fc3-4a19-9cb1-4f0c9327838c" /><br>
<img width="625" height="423" alt="snap_4" src="https://github.com/user-attachments/assets/ac0b0c57-ee67-4e33-96fb-146d14714fc8" />

4. Add `-insecure` in launch options

## Commands

### Aim

`bot_aim mixed`  
Bots select aiming spots flexibly based on situations (default)

`bot_aim head`  
Bots prioritize aiming at the head

`bot_aim body`  
Bots prioritize aiming at the torso

`bot_aim`  
Check the current aim mode

`bot_aim_preset <low|medium|high>`  
Set the aim difficulty preset (low = loose/slow, high = tight/fast). Pins the preset and turns auto-detection off

`bot_aim_preset auto`  
Make the aim preset follow the server's bot difficulty automatically (the default; see [How to change the difficulty level](#how-to-change-the-difficulty-level))

`bot_aim_preset`  
Check the current preset and whether auto-detection is on

`bot_headshot_bias <0-100>`  
Percentage of bots that aim high (head/jaw). Higher = more headshots

`bot_aim_error <0..10>`  
Global aim-error multiplier (0 = perfect aim, 1 = preset default)

`bot_aim_lag <0|1>`  
Toggle human-like reaction lag + target lead/prediction (on by default)

`bot_aim_status`  
Show runtime diagnostics: bound state, current preset, measured headshot rate, and the last aim point

`bot_aim_reset_stats`  
Reset the measured headshot-rate counters

### Nades

`bot_nades off`  
Bots won't throw any nades

`bot_nades normal`  
Bots follow almost the same count limits as human players (default)

`bot_nades more`  
Bots use the same decision logic as normal mode with higher count limits (recommended)

`bot_nades max`  
Bots have minimal limitations and think less before throwing nades

`bot_nades`  
Shows the current nade throwing mode

### Buy

Input the weapon's name in your console to give every bot this weapon from the next round

The valid names of weapons:  
`elite`  
`p250`  
`fn57`  
`deagle`  
`cz75a`  
`r8`  
`bizon`  
`p90`  
`mp5sd`  
`mp9`  
`mp7`  
`mac10`  
`ump45`  
`mag7`  
`sawedoff`  
`nova`  
`xm1014`  
`famas`  
`galilar`  
`m4a1`  
`m4a1s`  
`ak47`  
`aug`  
`sg556`  
`ssg08`  
`awp`  
`scar20`  
`g3sg1`  
`negev`  
`m249`

`bot_buy`  
Bot would buy as usual

### Teams

To add pro teams to your match, copy from [Commands.txt](https://github.com/ed0ard/CS2-Bot-Improver/blob/main/Commands.txt) and paste them to your game console. You can also add new teams in this format.

For example, if you wanna add Vit to CT, copy the commands below.

<img width="301" height="237" alt="snap_5" src="https://github.com/user-attachments/assets/a895f3a6-58f8-47dc-b6f5-b60c1b32fecd" />

### Knives

Point at the ground and press `\` on your keyboard to generate all kinds of knives there.

### Flying Scoutsman

`scouts_on`  
`scouts_off`  
Input the command after a match begins to turn on/off Flying Scoutsman

## FAQ

### How to change the difficulty level

1. Open the root of CS2 and navigate to `game/csgo/overrides` directory  
2. Open the `Low` for easy difficulty, `Medium` for a mixed difficulty based on HLTV stats (default), and `High` for extreme difficulty  
3. Copy `botprofile.vpk` and paste it into `game/csgo/overrides` before launching the game

The aim plugin (BotAimImprover) automatically matches its aim preset (`low`/`medium`/`high`) to whichever `botprofile.vpk` you copied here, so the bots' aim stays in sync with their skill level. No extra steps are needed.

To override this, edit `game/csgo/addons/counterstrikesharp/configs/plugins/BotAimImprover/BotAimImprover.json` (generated on first launch):

- `Preset`: `low` | `medium` | `high`. Fallback used when auto-detection is off or can't identify the profile
- `AutoDetectDifficulty`: `true` (default) syncs the preset to the active `botprofile.vpk`; set to `false` to always use `Preset`
- `Overrides`: fine-tune individual values (aim error, reaction time, target lead, headshot fraction, etc.) on top of the preset

You can also switch presets live in console with `bot_aim_preset <low|medium|high|auto>`.

### How to play online matches normally

1. Open the root of CS2 and navigate to `game/csgo/backup/Online` directory  
2. Copy `gameinfo.gi` and paste it to `game/csgo` directory (Replace the file in the destination)  
3. Delete `-insecure` in your launch options  

After modification, if you wanna **play with bots again**, navigate to `game/csgo/backup/WithBots` directory, replace the file as above and add the launch option

### How to play bot matches with friends

1. Start a bot match and input the required commands. Then type `status` in the console  
<img width="597" height="141" alt="snap_6" src="https://github.com/user-attachments/assets/792c4b4f-1d56-4a39-9186-b301cbff1846" />

2. Copy the text after `steamid:`, add `connect ` before it (don’t forget the space between them)  
3. Send the full command to your friends and have them paste it into their consoles

### How to run the plugin well on workshop maps

Add `-disable_workshop_command_filtering` to your launch options

### How to disable agent skins and music kits

1. Open the root of CS2 and navigate to `game/csgo/addons/counterstrikesharp/plugins`  
2. Rename the `BotRandomizer` folder to `BotRandomizer_disabled`

### How to surf normally

Run `sv_standable_normal 0.7` in your game console

## Credits
[metamod-source](https://github.com/alliedmodders/metamod-source)  
[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)  
[Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace)  
[CS2-Bullseye-Bot](https://github.com/ed0ard/CS2-Bullseye-Bot)  
[CS2-Bot-NadeSystem](https://github.com/ed0ard/CS2-Bot-NadeSystem)  
[CS2_ExecAfter_No_Admin](https://github.com/ed0ard/CS2_ExecAfter_No_Admin) forked from [kus](https://github.com/kus)  
[CS2-Bot-Randomizer](https://github.com/ed0ard/CS2-Bot-Randomizer)  
[CS2-Smarter-Bot](https://github.com/ed0ard/CS2-Smarter-Bot)  
[CS2-BotAI](https://github.com/ed0ard/CS2-BotAI) forked from [Austin](https://github.com/Austinbots)  
[CS2-BotAI-for-Linux](https://github.com/Austinbots/CS2-BotAI)  
[CS2-Bot-Buy](https://github.com/ed0ard/CS2-Bot-Buy)  
[RoundDamageRecap](https://github.com/YuGeYu/LBTV-CS2-Bot-Enhancer/tree/main/addons/counterstrikesharp/plugins/RoundDamageRecap) by [YuGeYu](https://github.com/YuGeYu)  

## License
GPL-3.0

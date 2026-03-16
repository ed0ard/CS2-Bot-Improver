# CS2-Bot-Improver
CS2-Bot-Improver is a plugin for Counter-Strike 2 that improves bots' aim, navigation, personalities, strategies, etc.

Aims to enhance your experience when playing against bots offline or with friends. It can be installed on both clients and servers.

## Your stars⭐ are my motivation to keep updating

## Features

1. Make bots aim better
2. Alleviate bot stuck issues
3. Assign each bot their own agent model, music kit and avatar
4. Refine bot behavior, allowing them to spam smokes and anti-flash
5. Make bots smarter and more organized
6. Change bot names to pro and random players. (the characteristics of each pro player are set according to stats from [HLTV](https://www.hltv.org/))
7. Remove the prefix from bot names
8. Tweak game rules to make them more friendly to bots
9. Add some commands to make the game more fun

## Installation

1. Download the latest **CS2BotImprover.zip** in [Releases](https://github.com/ed0ard/CS2-Bot-Improver/releases) and unzip it

   (If you run a dedicated server that is not only for bot matches, please download **CS2BotImprover_rules_unchanged.zip**)

<img width="405" height="256" alt="snap_1" src="https://github.com/user-attachments/assets/ae2be90e-6742-4f1f-8e0c-096b728d5dbd" />

2. Open the root of CS2 and navigate to `game/csgo` directory

<img width="348" height="123" alt="snap_2" src="https://github.com/user-attachments/assets/c6dcfc51-0062-44a7-9c9b-e8f094b8d8b3" />

3. Copy all the files in `CS2BotImprover` and paste them into it

<img width="130" height="153" alt="snap_3" src="https://github.com/user-attachments/assets/4c775e36-3fc3-4a19-9cb1-4f0c9327838c" /><br>
<img width="625" height="423" alt="snap_4" src="https://github.com/user-attachments/assets/ac0b0c57-ee67-4e33-96fb-146d14714fc8" />

4. Add `-insecure` in launch options

## Commands

### Aim

`bot_aim_mixed`  
Bots would aim for head and body (recommended)

`bot_aim_head`  
Bots would only aim for head

`bot_aim_body`  
Bots would only aim for stomach (default)

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

`scouts`  
Input this command after a match begins

## FAQ

### How to toggle between high and medium difficulties

1. Open the root of CS2 and navigate to `game/csgo/overrides` directory  
2. Open the `medium_difficulty` for medium or the `mixed_aim` for high  
3. Copy `botprofile.vpk` and paste it into `game/csgo/overrides` before launching the game

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

### How to change bot combat style to match the selected mode better

1. Open the root of CS2 and navigate to `game/csgo/overrides` directory  
2. Open one of the folders which matches your selected aim or game mode  
3. Copy `botprofile.vpk` and paste it into `game/csgo/overrides` before launching the game

### How to run the plugin well on workshop maps

Add `-disable_workshop_command_filtering` to your launch options

## Credits
[metamod-source](https://github.com/alliedmodders/metamod-source)  
[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)  
[CS2-Bullseye-Bot](https://github.com/ed0ard/CS2-Bullseye-Bot)  
[CS2_ExecAfter_No_Admin](https://github.com/ed0ard/CS2_ExecAfter_No_Admin) forked from [kus](https://github.com/kus)  
[CS2-Bot-Randomizer](https://github.com/ed0ard/CS2-Bot-Randomizer)  
[CS2-Smarter-Bot](https://github.com/ed0ard/CS2-Smarter-Bot)  
[CS2-BotAI](https://github.com/ed0ard/CS2-BotAI) forked from [Austin](https://github.com/Austinbots)  

## License
GPL-3.0

<div align="center">

# CS2-Bot-Improver

[![最新版本](https://img.shields.io/github/v/release/ed0ard/CS2-Bot-Improver?display_name=tag&sort=semver)](https://github.com/ed0ard/CS2-Bot-Improver/releases/latest)
[![累计下载](https://img.shields.io/github/downloads/ed0ard/CS2-Bot-Improver/total)](https://github.com/ed0ard/CS2-Bot-Improver/releases)
[![许可证：AGPL-3.0](https://img.shields.io/badge/license-AGPL--3.0-blue.svg)](../LICENSE)
![支持平台：Windows 与 Linux](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-5c6bc0)

[English](../README.md) · **简体中文** · [Русский](README.ru.md)

[功能](#功能) · [安装](#安装) · [命令](#命令) · [Panel使用指南](#panel-使用指南仅限-windows) · [常见问题](#常见问题)

</div>

CS2-Bot-Improver 面向喜欢与人机博弈，或想和朋友一起挑战人机的CS2玩家。主要增强了人机的瞄准、移动、道具、个性与战术表现，可安装到游戏客户端或专用服务器，为离线人机对局和好友私人对局带来更好的体验。

## **你们的 Stars ⭐ 是我持续更新的动力**

## 功能

| 方面 | 改进内容 |
| --- | --- |
| **瞄准与战斗** | 更精准、更接近真人的瞄准表现，并改进扫射、甩狙、混烟和背闪行为 |
| **道具** | 根据战况使用烟雾弹、闪光弹、手雷和燃烧瓶 |
| **移动** | 改进移动表现，并修复大多数人机卡在地图各处的问题 |
| **战术** | 提升人机的局势感知与决策能力，使行动更聪明、更有组织性 |
| **经济** | 扩展可购买的武器与装备，并全面改进经济管理 |
| **个性** | 使用职业选手或随机玩家名称；职业选手的特征基于 [HLTV](https://www.hltv.org/) 数据 |
| **皮肤** | 为每个人机发放刀、手套、武器皮肤、印花、挂件、探员、音乐盒、头像和个人资料 |
| **游戏体验** | 移除人机名称前缀，提供更适合人机对局的规则，并增加实用、有趣的控制台指令 |

## 安装

请前往 **[最新版本](https://github.com/ed0ard/CS2-Bot-Improver/releases/latest)** 页面，下载与你的操作系统相对应的安装包。

### Windows

1. 下载并解压 **CS2BotImprover.zip**。

> [!NOTE]
> 如果你的Windows专用服务器并非只用于人机对局，请下载 **CS2BotImprover_rules_unchanged.zip**，以保留标准游戏规则。

2. 将 **Panel v1.4.3.exe** 放在方便使用的位置。

   <img width="128" height="128" alt="CS2 Bot Improver Panel 应用图标" src="https://github.com/user-attachments/assets/7271dc7d-2436-484b-8359-6531f4abd710" />

3. 在 Steam 库中打开 CS2 页面，点击齿轮图标，选择**管理 → 浏览本地文件**，然后依次进入 `game` 和 `csgo`文件夹。

   <img width="405" height="256" alt="CS2 安装目录中的 game/csgo 文件夹" src="https://github.com/user-attachments/assets/ae2be90e-6742-4f1f-8e0c-096b728d5dbd" />

4. 将解压后剩余的全部文件复制到 `game/csgo`文件夹。

   <img width="540" height="181" alt="将 Windows 安装包中的文件复制到 game/csgo" src="https://github.com/user-attachments/assets/6a8645fc-78e7-4f3a-92d3-5d1b6d913918" />

5. 打开 **Panel v1.4.3.exe**，选择**机器人模式**，然后点击**启动 CS2**。

   <img width="339" height="129" alt="在 Panel 中选择 Bot Mode 并启动 CS2" src="https://github.com/user-attachments/assets/dc806991-c940-43cf-a614-f49012fae4a7" />

### Linux

1. 下载并解压 **CS2BotImprover_for_Linux.zip**。
2. 将 `Commands.txt` 放在方便使用的位置。
3. 打开 CS2 安装目录，然后进入 `game/csgo`。

   <img width="405" height="256" alt="CS2 安装目录中的 game/csgo 文件夹" src="https://github.com/user-attachments/assets/ae2be90e-6742-4f1f-8e0c-096b728d5dbd" />

4. 将解压后剩余的全部文件复制到 `game/csgo`。

   <img width="535" height="180" alt="将 Linux 安装包中的文件复制到 game/csgo" src="https://github.com/user-attachments/assets/9bda7b1d-43d3-49cf-a283-27b124b894e0" />

5. 在 CS2 的启动项中添加 `-insecure`。

   <img width="130" height="153" alt="在 Steam 中打开 CS2 属性" src="https://github.com/user-attachments/assets/4c775e36-3fc3-4a19-9cb1-4f0c9327838c" /><br>
   <img width="625" height="423" alt="在 CS2 启动项中添加 -insecure" src="https://github.com/user-attachments/assets/ac0b0c57-ee67-4e33-96fb-146d14714fc8" />

## 命令

### 瞄准

| 命令 | 说明 |
| --- | --- |
| `bot_aim mixed` | 开启混合瞄准模式，灵活地选取瞄准点。**（默认）** |
| `bot_aim head` | 优先瞄准头部。 |
| `bot_aim body` | 优先瞄准躯干。 |
| `bot_aim` | 查看当前瞄准模式。 |

### 人机道具

| 命令 | 说明 |
| --- | --- |
| `bot_nades off` | 不让人机使用任何道具。 |
| `bot_nades less` | 使用与普通模式相同的决策逻辑，但采用更低的数量上限。 |
| `bot_nades normal` | 采用接近真人玩家的投掷物数量上限。**（默认）** |
| `bot_nades more` | 使用与普通模式相同的决策逻辑，但采用更高的数量上限。 |
| `bot_nades max` | 减少各种限制，解锁地狱道具。 |
| `bot_nades` | 查看当前道具模式。 |

### 人机饰品

| 命令 | 说明 |
| --- | --- |
| `br_reroll` | 重新随机所有人机的饰品，下次复活时生效。 |

### 人机武器

在游戏控制台中输入武器的英文名称，即可从下一回合开始让所有人机获得该武器。

输入 `bot_buy` 可恢复正常购买行为。

<details>
<summary><strong>查看支持的武器名称</strong></summary>

```text
elite     p250      fn57      deagle    cz75a     r8
bizon     p90       mp5sd     mp9       mp7       mac10     ump45
mag7      sawedoff  nova      xm1014
famas     galilar   m4a1      m4a1s     ak47      aug       sg556
ssg08     awp       scar20    g3sg1
negev     m249
```

</details>

### 加入人机职业队

从 [`Commands.txt`](../Commands.txt) 中复制对应战队的整段命令，再粘贴到游戏控制台中。Windows 用户也可以在 Panel 的**指令**中搜索并复制战队命令。你还可以按照相同格式添加自己的战队。

例如，以下被选中的命令会将 小蜜蜂 加入 CT 阵营：

<img width="301" height="237" alt="Commands.txt 中用于将 Team Vitality 加入 CT 阵营的命令" src="https://github.com/user-attachments/assets/a895f3a6-58f8-47dc-b6f5-b60c1b32fecd" />

### 刀具

切出刀按“G”即可把刀丢掉。

准星指向地面按键盘上的“\”即可生成全部种类的原皮刀。

### 跳狙飞人（Flying Scoutsman）

对局开始后，使用 `scouts_on` 或 `scouts_off` 开启或关闭 跳狙飞人 模式。

## Panel 使用指南（仅限 Windows）

### 状态指示灯

| 指示灯 | 含义 |
| --- | --- |
| 🟢 绿色 | 未检测到问题。 |
| 🟡 黄色 | 需要重启 CS2 才能应用更改。 |
| 🔴 红色 | 存在缺失文件，点击红灯可查看缺失文件列表。 |

<img width="481" height="82" alt="Panel 中的绿色、黄色和红色状态指示灯" src="https://github.com/user-attachments/assets/26a947e2-4e0e-423f-bce8-f220d88509a2" />

### 匹配与人机模式切换

**联机模式**用于正常的在线匹配；**机器人模式**用于人机对局以及与好友一起对战人机。

选择需要的模式，然后点击**启动 CS2**。

<img width="472" height="179" alt="Panel 中的 Online Mode 与 Bot Mode 选择器" src="https://github.com/user-attachments/assets/3f9254fa-4cbe-4854-8fd1-0f35228fff77" />

### 设置

点击右上角的 <img width="31" height="32" alt="设置" src="https://github.com/user-attachments/assets/7f94176b-79f1-4e22-9495-4589c4dea9eb" /> 绿色齿轮图标，打开**设置**。

### 指令库

点击**指令**后，可单击任意指令块自动复制，也可以在搜索框中输入关键词查找，敲击回车或者点击上下箭头选择指令。

<img width="350" height="420" alt="Panel 中可搜索的命令浏览器" src="https://github.com/user-attachments/assets/957cfafb-900d-4450-b985-13d3e8efc375" />

## 常见问题

<details>
<summary><strong>会导致 VAC 封禁吗？</strong></summary>

不会。从本仓库 [Releases](https://github.com/ed0ard/CS2-Bot-Improver/releases) 下载的正版插件不会导致 VAC 封禁。

如果你另外安装或使用了为真人玩家更换皮肤等不属于本项目的功能，则不在上述保证范围内，相关风险需要由使用者自行承担。

</details>

<details>
<summary><strong>如何和朋友一起对战人机？</strong></summary>

1. 进入人机对局，输入所需指令，然后在控制台中运行 `status`。

   <img width="597" height="141" alt="status 命令显示的 steamid 值" src="https://github.com/user-attachments/assets/792c4b4f-1d56-4a39-9186-b301cbff1846" />

2. 复制 `steamid:` 后面的文本，并在前面加上 `connect `（不要漏掉中间的空格）。
3. 将完整命令发送给好友，让他们粘贴到各自的控制台中。

</details>

<details>
<summary><strong>如何手动更改难度？</strong></summary>

1. 在 Steam 库中打开 CS2 页面，点击齿轮图标，选择**管理 → 浏览本地文件**，然后依次进入 `game/csgo/overrides`。
2. 根据需要打开对应文件夹：`Low` 为简单难度；`Medium` 为基于 HLTV 数据的混合难度（**默认**）；`High` 为极高（地狱）难度。
3. 启动游戏前，将其中的 `botprofile.vpk` 复制到 `game/csgo/overrides`。

</details>

<details>
<summary><strong>如何手动切换回正常的在线匹配模式？</strong></summary>

1. 在 Steam 库中打开 CS2 页面，点击齿轮图标，选择**管理 → 浏览本地文件**，然后依次进入 `game/csgo/backup/Online`。
2. 将 `gameinfo.gi` 复制到 `game/csgo`，并替换目标位置的文件。
3. 打开 CS2 的 Steam 属性，从启动项中移除 `-insecure`。

如又想游玩人机局，请将 `game/csgo/backup/WithBots` 中的 `gameinfo.gi` 复制到 `game/csgo`，并加回`-insecure`启动项。

</details>

<details>
<summary><strong>如何手动禁用机器人的武器皮肤、探员、音乐盒、刀具和手套？</strong></summary>

1. 在 CS2 安装目录中依次进入 `game/csgo/addons/counterstrikesharp/plugins`。
2. 将 `BotRandomizer` 文件夹重命名为 `BotRandomizer_disabled`。
3. 打开 `addons/counterstrikesharp/configs/core.json`，将 `FollowCS2ServerGuidelines` 设置为 `true`。

</details>

<details>
<summary><strong>如何手动禁用人机的 Steam 头像和个人资料？</strong></summary>

打开 CS2 安装目录中的 `game/csgo/addons`，将 `BotHider` 文件夹重命名为 `BotHider_disabled`。

</details>

<details>
<summary><strong>如何让插件在创意工坊地图上正常运行？</strong></summary>

在启动项中添加 `-disable_workshop_command_filtering`。

</details>

<details>
<summary><strong>如何正常进行滑翔（Surf）？</strong></summary>

在游戏控制台中运行 `sv_standable_normal 0.7`。

</details>

### 本项目的适用范围和责任边界是什么？

> [!WARNING]
> 本项目适用于离线机器人对局、由用户自行托管的好友私人对局，以及用于机器人对局的私人专用服务器。`BotRandomizer` 的自动饰品分配**仅以机器人**为处理对象，不会向真人玩家授予、伪造或改写其 Steam 库存、饰品或个人资料；**这一边界旨在遵循 [Valve 针对 CS2 社区服务器及 GSLT 的相关规定](https://help.steampowered.com/zh-cn/faqs/view/07AF-502E-A104-BD4B)。**
>
> 请勿将本项目用于 Valve 官方匹配、启用 VAC 的公共服务器、[FACEIT](https://support.faceit.com/hc/en-us/articles/360015788779-What-is-deemed-to-be-a-cheat)、其他第三方公共社区服务器，或用于规避任何反作弊或安全控制。进入上述服务前，请在 Panel 中切换回**联机模式**，或手动恢复正常游戏文件、移除 `-insecure`。
>
> [AGPL-3.0 许可证](../LICENSE)授予的权利不构成使用第三方服务或违反其规则的授权。在适用法律允许的最大范围内，任何人超出上述范围使用或部署本项目、修改本项目以规避安全控制，或以其他方式违反第三方条款的，均应自行承担由此产生的全部风险与责任，包括 GSLT 或服务器处罚、FACEIT 或社区服封禁、VAC 或游戏封禁。维护者和贡献者不对上述后果承担责任。

## 致谢

- [Metamod:Source](https://github.com/alliedmodders/metamod-source)
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace)
- [CS2-Bullseye-Bot](https://github.com/ed0ard/CS2-Bullseye-Bot)
- [CS2-Bot-NadeSystem](https://github.com/ed0ard/CS2-Bot-NadeSystem)
- [CS2_ExecAfter_No_Admin](https://github.com/ed0ard/CS2_ExecAfter_No_Admin)，fork 自 [kus](https://github.com/kus)
- [CS2-Bot-Randomizer](https://github.com/ed0ard/CS2-Bot-Randomizer)
- [CS2-Lib](https://github.com/ianlucas/cs2-lib)，作者 [Lucas](https://github.com/ianlucas)
- [CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider)，作者 [XBribo](https://github.com/XBribo)
- [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller)，作者 [XBribo](https://github.com/XBribo)
- [CSGOBetterBots](https://github.com/manicogaming/CSGOBetterBots/blob/master/addons/sourcemod/data/bot_info.json)，作者 [manico](https://github.com/manicogaming)
- [CS2-Smarter-Bot](https://github.com/ed0ard/CS2-Smarter-Bot)
- [CS2-BotAI](https://github.com/ed0ard/CS2-BotAI)，fork 自 [Austin](https://github.com/Austinbots)
- [CS2-Bot-Buy](https://github.com/ed0ard/CS2-Bot-Buy)
- [RoundDamageRecap](https://github.com/YuGeYu/LBTV-CS2-Bot-Enhancer/tree/main/addons/counterstrikesharp/plugins/RoundDamageRecap)，作者 [YuGeYu](https://github.com/YuGeYu)
- [Apple-Style-GUI](https://github.com/ed0ard/Apple-Style-GUI)

## 许可证

[GNU Affero General Public License v3.0](../LICENSE)

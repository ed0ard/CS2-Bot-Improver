<div align="center">

# CS2-Bot-Improver

[![Последний выпуск](https://img.shields.io/github/v/release/ed0ard/CS2-Bot-Improver?display_name=tag&sort=semver)](https://github.com/ed0ard/CS2-Bot-Improver/releases/latest)
[![Всего загрузок](https://img.shields.io/github/downloads/ed0ard/CS2-Bot-Improver/total)](https://github.com/ed0ard/CS2-Bot-Improver/releases)
[![Лицензия: AGPL-3.0](https://img.shields.io/badge/license-AGPL--3.0-blue.svg)](../LICENSE)
![Платформы: Windows и Linux](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-5c6bc0)

[English](../README.md) · [简体中文](README.zh-CN.md) · **Русский**

[Возможности](#возможности) · [Установка](#установка) · [Команды](#команды) · [Panel](#руководство-по-panel-только-для-windows) · [FAQ](#часто-задаваемые-вопросы)

</div>

CS2-Bot-Improver улучшает ботов Counter-Strike 2 для офлайн-матчей и закрытых игр с друзьями. Проект совершенствует их прицеливание, перемещение, использование гранат, индивидуальные особенности и тактику. Его можно установить как на игровой клиент, так и на выделенный сервер.

## **Ваши звёзды ⭐ мотивируют меня продолжать обновлять проект**

## Возможности

| Область | Улучшения |
| --- | --- |
| **Прицеливание и бой** | Более точное и естественное прицеливание, а также улучшения стрельбы очередями, резких переводов прицела, стрельбы сквозь дым и реакции на светошумовые гранаты |
| **Гранаты** | Ситуативное использование дымовых, светошумовых и осколочных гранат, а также коктейлей Молотова |
| **Перемещение** | Улучшенное перемещение и исправления большинства ситуаций, в которых боты застревают |
| **Тактика** | Более умные и организованные боты с улучшенными восприятием обстановки и принятием решений |
| **Экономика** | Расширенный выбор оружия и снаряжения при покупке, а также переработанное управление экономикой |
| **Индивидуальность** | Имена профессиональных и случайных игроков; характеристики профессионалов основаны на статистике [HLTV](https://www.hltv.org/) |
| **Персонализация** | Отдельные ножи, перчатки, скины оружия, наклейки, брелоки, агенты, музыкальные наборы, аватары и профили для каждого бота |
| **Игровой процесс** | Имена ботов без префиксов, правила, лучше подходящие для игры с ботами, и дополнительные консольные команды |

## Установка

Скачайте пакет для своей операционной системы со страницы **[последнего выпуска](https://github.com/ed0ard/CS2-Bot-Improver/releases/latest)**.

### Windows

1. Скачайте и распакуйте **CS2BotImprover.zip**.

> [!NOTE]
> Если выделенный сервер используется не только для матчей с ботами, скачайте для Windows архив **CS2BotImprover_rules_unchanged.zip**, чтобы сохранить стандартные правила игры.

2. Поместите **Panel v1.4.3.exe** в любое удобное место.

   <img width="128" height="128" alt="Значок приложения CS2 Bot Improver Panel" src="https://github.com/user-attachments/assets/7271dc7d-2436-484b-8359-6531f4abd710" />

3. Откройте папку установки CS2 и перейдите в каталог `game/csgo`.

   <img width="405" height="256" alt="Каталог game/csgo в папке установки CS2" src="https://github.com/user-attachments/assets/ae2be90e-6742-4f1f-8e0c-096b728d5dbd" />

4. Скопируйте все оставшиеся файлы из распакованного архива в `game/csgo`.

   <img width="540" height="181" alt="Копирование файлов пакета для Windows в game/csgo" src="https://github.com/user-attachments/assets/6a8645fc-78e7-4f3a-92d3-5d1b6d913918" />

5. Откройте **Panel v1.4.3.exe**, выберите **Bot Mode**, затем нажмите **Launch CS2**.

   <img width="339" height="129" alt="Выбор Bot Mode и запуск CS2 из Panel" src="https://github.com/user-attachments/assets/dc806991-c940-43cf-a614-f49012fae4a7" />

### Linux

1. Скачайте и распакуйте **CS2BotImprover_for_Linux.zip**.
2. Поместите `Commands.txt` в любое удобное место.
3. Откройте папку установки CS2 и перейдите в каталог `game/csgo`.

   <img width="405" height="256" alt="Каталог game/csgo в папке установки CS2" src="https://github.com/user-attachments/assets/ae2be90e-6742-4f1f-8e0c-096b728d5dbd" />

4. Скопируйте все оставшиеся файлы из распакованного архива в `game/csgo`.

   <img width="535" height="180" alt="Копирование файлов пакета для Linux в game/csgo" src="https://github.com/user-attachments/assets/9bda7b1d-43d3-49cf-a283-27b124b894e0" />

5. Добавьте `-insecure` в параметры запуска CS2.

   <img width="130" height="153" alt="Открытие свойств CS2 в Steam" src="https://github.com/user-attachments/assets/4c775e36-3fc3-4a19-9cb1-4f0c9327838c" /><br>
   <img width="625" height="423" alt="Добавление -insecure в параметры запуска CS2" src="https://github.com/user-attachments/assets/ac0b0c57-ee67-4e33-96fb-146d14714fc8" />

## Команды

### Прицеливание

| Команда | Описание |
| --- | --- |
| `bot_aim mixed` | Динамически выбирать точки прицеливания в зависимости от ситуации. **(По умолчанию)** |
| `bot_aim head` | В первую очередь целиться в голову. |
| `bot_aim body` | В первую очередь целиться в корпус. |
| `bot_aim` | Показать текущий режим прицеливания. |

### Гранаты

| Команда | Описание |
| --- | --- |
| `bot_nades off` | Запретить ботам использовать гранаты. |
| `bot_nades less` | Использовать логику обычного режима с более низкими ограничениями по количеству. |
| `bot_nades normal` | Использовать ограничения по количеству, близкие к ограничениям обычных игроков. **(По умолчанию)** |
| `bot_nades more` | Использовать логику обычного режима с более высокими ограничениями по количеству. |
| `bot_nades max` | Свести ограничения к минимуму и сократить время на принятие решения перед броском. |
| `bot_nades` | Показать текущий режим использования гранат. |

### Скины

| Команда | Описание |
| --- | --- |
| `br_reroll` | При следующем появлении заново выбрать косметические предметы для всех ботов. |

### Покупка оружия

Введите название оружия в игровой консоли, чтобы со следующего раунда выдать его всем ботам.

Введите `bot_buy`, чтобы восстановить обычное поведение при покупке.

<details>
<summary><strong>Показать поддерживаемые названия оружия</strong></summary>

```text
elite     p250      fn57      deagle    cz75a     r8
bizon     p90       mp5sd     mp9       mp7       mac10     ump45
mag7      sawedoff  nova      xm1014
famas     galilar   m4a1      m4a1s     ak47      aug       sg556
ssg08     awp       scar20    g3sg1
negev     m249
```

</details>

### Профессиональные команды

Скопируйте из [Commands.txt](../Commands.txt) блок для нужного профессионального состава и вставьте его в игровую консоль. В том же формате можно добавлять собственные составы.

Например, следующий блок из `Commands.txt` добавляет Team Vitality за сторону CT:

<img width="301" height="237" alt="Команды для добавления Team Vitality за сторону CT из Commands.txt" src="https://github.com/user-attachments/assets/a895f3a6-58f8-47dc-b6f5-b60c1b32fecd" />

### Ножи

Наведите прицел на землю и нажмите клавишу `\`, чтобы создать там все виды ножей.

### «Перелётные снайперы» (Flying Scoutsman)

После начала матча используйте `scouts_on` или `scouts_off`, чтобы включить или выключить режим Flying Scoutsman.

## Руководство по Panel (только для Windows)

### Индикаторы состояния

| Индикатор | Значение |
| --- | --- |
| 🟢 Зелёный | Проблем не обнаружено. |
| 🟡 Жёлтый | Перезапустите CS2, чтобы применить изменения. |
| 🔴 Красный | Отсутствуют файлы. Нажмите на красный индикатор, чтобы посмотреть их список. |

<img width="481" height="82" alt="Зелёный, жёлтый и красный индикаторы состояния Panel" src="https://github.com/user-attachments/assets/26a947e2-4e0e-423f-bce8-f220d88509a2" />

### Переключатель Matchmaking и Bot Mode

Выберите нужный режим, затем нажмите **Launch CS2**.

<img width="472" height="179" alt="Переключатель Online Mode и Bot Mode в Panel" src="https://github.com/user-attachments/assets/3f9254fa-4cbe-4854-8fd1-0f35228fff77" />

### Настройки

Нажмите значок <img width="31" height="32" alt="Настройки" src="https://github.com/user-attachments/assets/7f94176b-79f1-4e22-9495-4589c4dea9eb" /> в правом верхнем углу, чтобы открыть **Settings**.

### Просмотр команд

Откройте **Commands**: нажмите на блок, чтобы автоматически скопировать его содержимое, или введите ключевые слова для поиска.

<img width="350" height="420" alt="Поиск и просмотр команд в Panel" src="https://github.com/user-attachments/assets/957cfafb-900d-4450-b985-13d3e8efc375" />

## Часто задаваемые вопросы

<details>
<summary><strong>Как играть матчи с ботами вместе с друзьями?</strong></summary>

1. Запустите матч с ботами, введите необходимые команды, затем выполните `status` в консоли.

   <img width="597" height="141" alt="Значение steamid в выводе команды status" src="https://github.com/user-attachments/assets/792c4b4f-1d56-4a39-9186-b301cbff1846" />

2. Скопируйте текст после `steamid:` и добавьте перед ним `connect ` (не забудьте пробел).
3. Отправьте полную команду друзьям, чтобы они вставили её в свои консоли.

</details>

<details>
<summary><strong>Как вручную изменить уровень сложности?</strong></summary>

1. Перейдите в `game/csgo/overrides` в папке установки CS2.
2. Откройте `Low` для лёгкой сложности, `Medium` для смешанной сложности на основе статистики HLTV (**по умолчанию**) или `High` для экстремальной сложности.
3. До запуска игры скопируйте выбранный файл `botprofile.vpk` в `game/csgo/overrides`.

</details>

<details>
<summary><strong>Как вручную переключиться в обычный режим сетевой игры?</strong></summary>

1. Перейдите в `game/csgo/backup/Online` в папке установки CS2.
2. Скопируйте `gameinfo.gi` в `game/csgo`, заменив файл в папке назначения.
3. Удалите `-insecure` из параметров запуска.

Чтобы снова играть с ботами, скопируйте `gameinfo.gi` из `game/csgo/backup/WithBots` в `game/csgo` и верните параметр запуска.

</details>

<details>
<summary><strong>Как вручную отключить скины оружия, агентов, музыкальные наборы, ножи и перчатки ботов?</strong></summary>

1. Перейдите в `game/csgo/addons/counterstrikesharp/plugins` в папке установки CS2.
2. Переименуйте папку `BotRandomizer` в `BotRandomizer_disabled`.
3. Откройте `addons/counterstrikesharp/configs/core.json` и задайте параметру `FollowCS2ServerGuidelines` значение `true`.

</details>

<details>
<summary><strong>Как вручную отключить Steam-профили ботов?</strong></summary>

Перейдите в `game/csgo/addons` в папке установки CS2 и переименуйте папку `BotHider` в `BotHider_disabled`.

</details>

<details>
<summary><strong>Как правильно запустить плагин на картах из Мастерской?</strong></summary>

Добавьте `-disable_workshop_command_filtering` в параметры запуска.

</details>

<details>
<summary><strong>Как нормально играть на surf-картах?</strong></summary>

Выполните `sv_standable_normal 0.7` в игровой консоли.

</details>

### Каковы область поддерживаемого использования и границы ответственности?

> [!WARNING]
> Проект предназначен для офлайн-матчей с ботами, самостоятельно размещённых закрытых игр с друзьями и частных выделенных серверов, используемых для игры с ботами. `BotRandomizer` назначает косметические предметы **только ботам**; он не выдаёт, не подменяет и не изменяет инвентарь Steam, косметические предметы или профиль реального игрока. Эта граница предусмотрена для соблюдения [правил Valve для серверов сообщества CS2 и GSLT](https://help.steampowered.com/en/faqs/view/07AF-502E-A104-BD4B).
>
> Проект не предназначен и не поддерживается для официального матчмейкинга Valve, публичных серверов с защитой VAC, [FACEIT](https://support.faceit.com/hc/en-us/articles/360015788779-What-is-deemed-to-be-a-cheat) и других сторонних публичных серверов сообщества. Не используйте его для обхода античит-систем или иных средств защиты. Перед подключением к таким сервисам переключите Panel в **Online Mode** либо вручную восстановите стандартные файлы игры, удалите `-insecure`.
>
> Лицензия [AGPL-3.0](../LICENSE) не предоставляет доступ к сторонним сервисам и не разрешает нарушать их правила. В максимальной степени, разрешённой применимым законодательством, лицо, использующее или развёртывающее проект за пределами указанной области, изменяющее его для обхода средств защиты либо иным образом нарушающее условия третьих лиц, самостоятельно принимает все связанные риски и ответственность, включая санкции в отношении GSLT или сервера, блокировки FACEIT или серверов сообщества, VAC- или игровые блокировки. Разработчики и участники проекта не несут ответственности за такие последствия.

## Благодарности

- [Metamod:Source](https://github.com/alliedmodders/metamod-source)
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- [Ray-Trace](https://github.com/FUNPLAY-pro-CS2/Ray-Trace)
- [CS2-Bullseye-Bot](https://github.com/ed0ard/CS2-Bullseye-Bot)
- [CS2-Bot-NadeSystem](https://github.com/ed0ard/CS2-Bot-NadeSystem)
- [CS2_ExecAfter_No_Admin](https://github.com/ed0ard/CS2_ExecAfter_No_Admin), форк проекта [kus](https://github.com/kus)
- [CS2-Bot-Randomizer](https://github.com/ed0ard/CS2-Bot-Randomizer)
- [CS2-Lib](https://github.com/ianlucas/cs2-lib) от [Lucas](https://github.com/ianlucas)
- [CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider) от [XBribo](https://github.com/XBribo)
- [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) от [XBribo](https://github.com/XBribo)
- [CSGOBetterBots](https://github.com/manicogaming/CSGOBetterBots/blob/master/addons/sourcemod/data/bot_info.json) от [manico](https://github.com/manicogaming)
- [CS2-Smarter-Bot](https://github.com/ed0ard/CS2-Smarter-Bot)
- [CS2-BotAI](https://github.com/ed0ard/CS2-BotAI), форк проекта [Austin](https://github.com/Austinbots)
- [CS2-Bot-Buy](https://github.com/ed0ard/CS2-Bot-Buy)
- [RoundDamageRecap](https://github.com/YuGeYu/LBTV-CS2-Bot-Enhancer/tree/main/addons/counterstrikesharp/plugins/RoundDamageRecap) от [YuGeYu](https://github.com/YuGeYu)
- [Apple-Style-GUI](https://github.com/ed0ard/Apple-Style-GUI)

## Лицензия

[GNU Affero General Public License v3.0](../LICENSE)

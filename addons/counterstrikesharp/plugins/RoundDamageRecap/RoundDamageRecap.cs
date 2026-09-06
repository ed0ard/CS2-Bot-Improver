using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using System.Security.Cryptography;
using CS2BotImprover.Localization;

namespace RoundDamageRecap;

[MinimumApiVersion(304)]
public sealed class RoundDamageRecapPlugin : BasePlugin
{
    public override string ModuleName => "RoundDamageRecap";
    public override string ModuleVersion => "1.3.0";
    public override string ModuleAuthor => "YuGeYu (modified by ed0ard and unicbm)";
    public override string ModuleDescription => "Shows a round-end damage recap and current difficulty in chat.";

    private const string ChatColorGreen = "\u0004";
    private const string ChatColorLime = "\u0006";
    private const string ChatColorDefault = "\u0001";

    private readonly Dictionary<int, Dictionary<int, DamageEntry>> _damageByAttacker = new();
    private readonly Dictionary<int, PlayerSnapshot> _playersByKey = new();
    private DamageRecapStyle _damageStyle = DamageRecapStyle.Auto;
    private bool _announcedDifficultyThisMap;
    private PluginI18n? _i18n;

    private PluginI18n I18n => _i18n ??= new(ModuleDirectory);

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        AddCommand("css_damage_style", "Set round damage recap style: auto, classic or pw", OnDamageStyleCommand);
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _damageByAttacker.Clear();
            _playersByKey.Clear();
            _announcedDifficultyThisMap = false;
        });
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _damageByAttacker.Clear();
        SnapshotAllPlayers();
        AnnounceDifficultyOncePerMap();
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null)
        {
            return HookResult.Continue;
        }

        var key = GetPlayerKey(player);
        RemovePlayerStats(key);
        _playersByKey.Remove(key);
        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;

        if (!IsTrackablePlayer(attacker) || !IsTrackablePlayer(victim))
        {
            return HookResult.Continue;
        }

        if (attacker == null || victim == null)
        {
            return HookResult.Continue;
        }

        var attackerKey = GetPlayerKey(attacker);
        var victimKey = GetPlayerKey(victim);
        if (attackerKey == victimKey)
        {
            return HookResult.Continue;
        }

        SnapshotPlayer(attacker);
        SnapshotPlayer(victim);

        if (!_damageByAttacker.TryGetValue(attackerKey, out var victimEntries))
        {
            victimEntries = new Dictionary<int, DamageEntry>();
            _damageByAttacker[attackerKey] = victimEntries;
        }

        if (!victimEntries.TryGetValue(victimKey, out var entry))
        {
            entry = new DamageEntry();
            victimEntries[victimKey] = entry;
        }

        entry.TargetName = victim.PlayerName;
        entry.TotalDamage += Math.Max(0, @event.DmgHealth);
        entry.HitCount += 1;
        entry.LastKnownHealth = Math.Max(0, @event.Health);

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        SnapshotAllPlayers();

        foreach (var player in Utilities.GetPlayers().Where(IsEligibleRecipient))
        {
            PrintRecapForPlayer(player);
        }

        return HookResult.Continue;
    }

    private void OnDamageStyleCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (command.ArgCount > 1)
        {
            if (!TryParseDamageStyle(command.GetArg(1), out var style))
            {
                command.ReplyToCommand(I18n.Get(caller, "damage_style.usage"));
                return;
            }

            _damageStyle = style;
        }

        if (_damageStyle == DamageRecapStyle.Auto && caller is { IsValid: true })
        {
            var language = caller.GetLanguage();
            command.ReplyToCommand(
                I18n.Get(caller, "damage_style.auto", GetDamageStyleName(ResolveDamageStyle(caller)), language.Name));
            return;
        }

        command.ReplyToCommand(I18n.Get(caller, "damage_style.current", GetDamageStyleName(_damageStyle)));
    }

    private void PrintRecapForPlayer(CCSPlayerController player)
    {
        var enemyTeam = GetEnemyTeam(player.Team);
        if (enemyTeam == null)
        {
            return;
        }

        var enemyPlayers = Utilities.GetPlayers()
            .Where(p => IsTrackablePlayer(p) && p.Team == enemyTeam)
            .OrderByDescending(p => GetDamageBetween(player, p).TotalDamage + GetDamageBetween(p, player).TotalDamage)
            .ThenBy(p => p.PlayerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var damageStyle = ResolveDamageStyle(player);

        foreach (var enemy in enemyPlayers)
        {
            var toEntry = GetDamageBetween(player, enemy);
            var fromEntry = GetDamageBetween(enemy, player);
            var remainingHp = GetDisplayedHealth(enemy, toEntry);

            PrintDamageRecapLine(player, enemy.PlayerName, toEntry, fromEntry, remainingHp, damageStyle);
        }
    }

    private void PrintDamageRecapLine(
        CCSPlayerController player,
        string enemyName,
        DamageEntry dealt,
        DamageEntry taken,
        int remainingHp,
        DamageRecapStyle damageStyle)
    {
        if (damageStyle == DamageRecapStyle.Classic)
        {
            var hpStatus = remainingHp > 0 
                ? I18n.Get(player, "damage_recap.hp_left", remainingHp)
                : I18n.Get(player, "damage_recap.dead");
            
            PrintLbtvLine(
                player,
                I18n.Get(player, "damage_recap.classic", 
                    enemyName, hpStatus, 
                    dealt.TotalDamage, dealt.HitCount, 
                    taken.TotalDamage, taken.HitCount));
            return;
        }

        player.PrintToChat(
            I18n.Get(player, "damage_recap.perfect_world",
                dealt.HitCount, dealt.TotalDamage,
                taken.HitCount, taken.TotalDamage,
                Math.Max(0, remainingHp), enemyName));
    }

    private static bool TryParseDamageStyle(string value, out DamageRecapStyle style)
    {
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            style = DamageRecapStyle.Auto;
            return true;
        }

        if (value.Equals("classic", StringComparison.OrdinalIgnoreCase))
        {
            style = DamageRecapStyle.Classic;
            return true;
        }

        if (value.Equals("pw", StringComparison.OrdinalIgnoreCase))
        {
            style = DamageRecapStyle.PerfectWorld;
            return true;
        }

        style = DamageRecapStyle.Auto;
        return false;
    }

    private DamageRecapStyle ResolveDamageStyle(CCSPlayerController player)
    {
        if (_damageStyle != DamageRecapStyle.Auto)
        {
            return _damageStyle;
        }

        return player.GetLanguage().TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? DamageRecapStyle.PerfectWorld
            : DamageRecapStyle.Classic;
    }

    private static string GetDamageStyleName(DamageRecapStyle style)
    {
        return style switch
        {
            DamageRecapStyle.PerfectWorld => "pw",
            DamageRecapStyle.Classic => "classic",
            _ => "auto"
        };
    }

    private static void PrintLbtvLine(CCSPlayerController player, string text)
    {
        player.PrintToChat($" {ChatColorGreen}{text}{ChatColorDefault}");
    }

    private void AnnounceDifficultyOncePerMap()
    {
        if (_announcedDifficultyThisMap)
        {
            return;
        }

        var recipients = Utilities.GetPlayers()
            .Where(IsEligibleRecipient)
            .ToList();
        if (recipients.Count == 0)
        {
            return;
        }

        var difficulty = DetectDifficulty();
        foreach (var player in recipients)
        {
            var localizedName = I18n.Get(player, difficulty.LocalizationKey);
            var message = I18n.Get(player, "difficulty.message", localizedName, difficulty.Level);
            PrintLbtvLine(player, message);
        }

        _announcedDifficultyThisMap = true;
    }

    private DifficultyResult DetectDifficulty()
    {
        var overridesDir = FindOverridesDirectory();
        if (overridesDir == null)
        {
            return new DifficultyResult("difficulty.unknown_overrides", "?/3");
        }

        var activePath = Path.Combine(overridesDir, "botprofile.vpk");
        if (!File.Exists(activePath))
        {
            return new DifficultyResult("difficulty.unknown_profile", "?/3");
        }

        var activeHash = ComputeSha256(activePath);
        var knownProfiles = new[]
        {
            new DifficultyProfile("difficulty.low", "1/3", Path.Combine(overridesDir, "Low", "botprofile.vpk")),
            new DifficultyProfile("difficulty.medium", "2/3", Path.Combine(overridesDir, "Medium", "botprofile.vpk")),
            new DifficultyProfile("difficulty.high", "3/3", Path.Combine(overridesDir, "High", "botprofile.vpk"))
        };

        foreach (var profile in knownProfiles)
        {
            if (!File.Exists(profile.Path))
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(activeHash, ComputeSha256(profile.Path)))
            {
                return new DifficultyResult(profile.LocalizationKey, profile.Level);
            }
        }

        return new DifficultyResult("difficulty.custom", "?/3");
    }

    private static string? FindOverridesDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(Server.GameDirectory, "overrides"),
            Path.Combine(Server.GameDirectory, "csgo", "overrides"),
            Path.Combine(Server.GameDirectory, "game", "csgo", "overrides")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "botprofile.vpk")))
            {
                return candidate;
            }
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "overrides");
            if (File.Exists(Path.Combine(candidate, "botprofile.vpk")))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private static byte[] ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    private void SnapshotAllPlayers()
    {
        foreach (var player in Utilities.GetPlayers().Where(IsTrackablePlayer))
        {
            SnapshotPlayer(player);
        }
    }

    private void SnapshotPlayer(CCSPlayerController player)
    {
        var key = GetPlayerKey(player);
        _playersByKey[key] = new PlayerSnapshot(player.PlayerName, player.Team);
    }

    private DamageEntry GetDamageBetween(CCSPlayerController attacker, CCSPlayerController victim)
    {
        var attackerKey = GetPlayerKey(attacker);
        var victimKey = GetPlayerKey(victim);

        if (_damageByAttacker.TryGetValue(attackerKey, out var victims)
            && victims.TryGetValue(victimKey, out var entry))
        {
            return entry;
        }

        return DamageEntry.Empty;
    }

    private int GetDisplayedHealth(CCSPlayerController enemy, DamageEntry toEntry)
    {
        if (enemy.PawnIsAlive && enemy.PlayerPawn.Value is { IsValid: true } pawn)
        {
            return Math.Max(0, pawn.Health);
        }

        if (toEntry.HitCount > 0)
        {
            return toEntry.LastKnownHealth;
        }

        return 0;
    }

    private void RemovePlayerStats(int key)
    {
        _damageByAttacker.Remove(key);

        foreach (var victimEntries in _damageByAttacker.Values)
        {
            victimEntries.Remove(key);
        }
    }

    private static int GetPlayerKey(CCSPlayerController player)
    {
        return player.UserId ?? player.Slot;
    }

    private static CsTeam? GetEnemyTeam(CsTeam team)
    {
        return team switch
        {
            CsTeam.CounterTerrorist => CsTeam.Terrorist,
            CsTeam.Terrorist => CsTeam.CounterTerrorist,
            _ => null
        };
    }

    private static bool IsTrackablePlayer(CCSPlayerController? player)
    {
        return player is { IsValid: true, IsHLTV: false }
               && player.Team is CsTeam.CounterTerrorist or CsTeam.Terrorist;
    }

    private static bool IsEligibleRecipient(CCSPlayerController? player)
    {
        return IsTrackablePlayer(player) && player is { IsBot: false };
    }

    private sealed class DamageEntry
    {
        public static readonly DamageEntry Empty = new();

        public string TargetName { get; set; } = string.Empty;
        public int TotalDamage { get; set; }
        public int HitCount { get; set; }
        public int LastKnownHealth { get; set; } = 100;
    }

    private sealed record PlayerSnapshot(string Name, CsTeam Team);

    private sealed record DifficultyProfile(string LocalizationKey, string Level, string Path);

    private sealed record DifficultyResult(string LocalizationKey, string Level);

    private enum DamageRecapStyle
    {
        Auto,
        Classic,
        PerfectWorld
    }
}

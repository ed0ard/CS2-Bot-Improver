using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using RayTraceAPI;

namespace ProOpeningReplay;

[MinimumApiVersion(304)]
public sealed partial class ProOpeningReplayPlugin : BasePlugin
{
    public override string ModuleName => "Pro Opening Replay";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "GitHub Copilot";
    public override string ModuleDescription => "Replays extracted pro opening defaults for bots before first contact.";

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private ReplayConfig _config = new();
    private ReplayDataset? _dataset;
    private bool _nativeReplayAvailable;
    private static readonly PluginCapability<CRayTraceInterface> _rayTraceCapability = new("raytrace:craytraceinterface");
    private CRayTraceInterface? _rayTrace;
    private readonly List<ReplaySession> _sessions = [];
    private readonly List<RetakeMoveToSession> _retakeMoveTos = [];
    private readonly Vector _moveToArgVec = new(0f, 0f, 0f);
    private bool _moveToAvailable = true;
    private readonly Dictionary<int, int> _lastEnsuredWeaponDef = [];
    private readonly Dictionary<int, int> _lastReplayWeaponDef = [];
    private readonly Dictionary<int, LockTarget> _lastLockedWeaponTarget = [];
    private readonly HashSet<(int Slot, int DefIndex)> _preloadedReplayWeapons = [];
    private readonly Dictionary<uint, float> _manifestReplayProjectiles = [];
    // Bots that recently exited replay — suppress IsStuck for a grace period to prevent BotState's
    // unstuck logic from making them jump/spin while the pathfinder recalculates a valid route.
    private readonly Dictionary<CCSPlayerController, float> _handoffGraceExpiry = [];
    private readonly Dictionary<int, ReplayAssignment> _pendingAssignments = [];
    private readonly Dictionary<int, PreparedOpeningSession> _preparedOpeningSessions = [];
    private readonly Dictionary<int, string> _nativeReplayPreloadKeys = [];
    private readonly HashSet<int> _loadoutAppliedKeys = [];
    private readonly Dictionary<int, int> _roundLoadoutBudgets = [];
    private readonly Dictionary<int, EnemyWatchState> _enemyWatchStates = [];
    private readonly Dictionary<CsTeam, RoundEconomyIndex> _roundIndexes = [];
    private readonly Dictionary<CsTeam, SpawnReplayIndex> _spawnIndexes = [];
    private readonly Dictionary<int, float> _lastHurtTime = [];
    // Precomputed per-team retake candidate pools, populated in BuildRoundIndexes after the dataset
    // loads. Built once instead of per-bomb-plant so OnBombPlanted -> StartRetakeSessions stays cheap
    // (otherwise scanning 600+ rounds * ~10 players * ~9000 frames each on the main thread on every
    // plant causes a multi-frame server hitch).
    private readonly List<RetakeCandidate> _ctRetakeCandidates = [];
    private readonly List<RetakeCandidate> _tRetakeCandidates = [];
    private int _retakeCandidateRoundsWithPlant;

    // Dataset-derived site centroids computed in BuildRoundIndexes via k-means on PlantPos values.
    // Used for retake site classification instead of func_bomb_target (which is unreliable for brush entities).
    private readonly List<Vector> _datasetSiteCentroids = [];
    private readonly Random _random = new();
    private bool _roundPrepared;
    private bool _freezeEnded;
    private bool _openingReplayStartQueued;
    private float _roundStartTime = -1f;
    private float _freezePeriodStartTime = -1f;
    private CancellationTokenSource? _replayBundlePrewarmCancellation;
    private int _replayBundlePrewarmGeneration;
    private int _replayBundlePrewarmTotal;
    private int _replayBundlePrewarmCompleted;
    private int _replayBundlePrewarmFailed;
    // Bomb state for retake replay end conditions. Set on OnBombPlanted, cleared on OnRoundEnd.
    private float _bombPlantTime = -1f;
    private float _bombDetonationTime = -1f;
    private Vector? _bombPos;
    // Bombsite centers cached on round start. Used to end T opening replay when bot enters a site.
    private readonly List<Vector> _bombSiteCenters = [];
    // Approximate bombsite radius (CS units). Most sites span 200-400u; 250 is generous and matches
    // "foot inside the painted A/B area" intuition without firing while still in the choke leading in.
    private const float BombSiteRadius = 150f;
    // Bomb timer in seconds; competitive default is 40. Updated from the planted_c4 entity if available.
    private const float DefaultBombTimerSeconds = 40f;
    private const float ManifestProjectileProtectSeconds = 30f;
    private static readonly HashSet<string> PrimaryWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_ak47", "weapon_aug", "weapon_awp", "weapon_famas", "weapon_g3sg1", "weapon_galilar",
        "weapon_m4a1", "weapon_m4a1_silencer", "weapon_sg556", "weapon_ssg08", "weapon_scar20",
        "weapon_mac10", "weapon_mp5sd", "weapon_mp7", "weapon_mp9", "weapon_bizon", "weapon_p90", "weapon_ump45",
        "weapon_mag7", "weapon_nova", "weapon_sawedoff", "weapon_xm1014", "weapon_m249", "weapon_negev"
    };

    // Weapons that should never be given to bots (auto-snipers and LMGs are unrealistic for normal play).
    private static readonly HashSet<string> BannedWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_scar20", "weapon_g3sg1", "weapon_m249", "weapon_negev"
    };

    private static readonly HashSet<string> UtilityItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_flashbang", "weapon_hegrenade", "weapon_smokegrenade", "weapon_molotov", "weapon_incgrenade", "weapon_decoy", "weapon_taser"
    };

    private static readonly HashSet<string> ThrowableUtilityItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_flashbang", "weapon_hegrenade", "weapon_smokegrenade", "weapon_molotov", "weapon_incgrenade", "weapon_decoy"
    };

    private static readonly string[] ReplayProjectileDesignerNames =
    [
        "flashbang_projectile",
        "hegrenade_projectile",
        "smokegrenade_projectile",
        "molotov_projectile",
        "incendiarygrenade_projectile",
        "decoy_projectile"
    ];

    private static readonly HashSet<string> SecondaryWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_glock", "weapon_hkp2000", "weapon_usp_silencer", "weapon_elite", "weapon_p250", "weapon_tec9",
        "weapon_fiveseven", "weapon_deagle", "weapon_cz75a", "weapon_revolver"
    };

    private static readonly HashSet<string> DefaultPistols = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_glock", "weapon_hkp2000", "weapon_usp_silencer"
    };

    private static readonly Dictionary<string, string> GrenadeTypeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["smokegrenade_projectile"] = "weapon_smokegrenade",
        ["CSmokeGrenade"] = "weapon_smokegrenade",
        ["CSmokeGrenadeProjectile"] = "weapon_smokegrenade",
        ["SmokeGrenade"] = "weapon_smokegrenade",
        ["weapon_smokegrenade"] = "weapon_smokegrenade",
        ["molotov_projectile"] = "weapon_molotov",
        ["CMolotovGrenade"] = "weapon_molotov",
        ["CMolotovProjectile"] = "weapon_molotov",
        ["Molotov"] = "weapon_molotov",
        ["weapon_molotov"] = "weapon_molotov",
        ["incendiary_projectile"] = "weapon_incgrenade",
        ["CIncendiaryGrenade"] = "weapon_incgrenade",
        ["CIncendiaryGrenadeProjectile"] = "weapon_incgrenade",
        ["IncendiaryGrenade"] = "weapon_incgrenade",
        ["weapon_incgrenade"] = "weapon_incgrenade",
        ["hegrenade_projectile"] = "weapon_hegrenade",
        ["CHEGrenade"] = "weapon_hegrenade",
        ["CHEGrenadeProjectile"] = "weapon_hegrenade",
        ["HeGrenade"] = "weapon_hegrenade",
        ["weapon_hegrenade"] = "weapon_hegrenade",
        ["decoy_projectile"] = "weapon_decoy",
        ["CDecoyGrenade"] = "weapon_decoy",
        ["CDecoyProjectile"] = "weapon_decoy",
        ["DecoyGrenade"] = "weapon_decoy",
        ["weapon_decoy"] = "weapon_decoy",
        ["flashbang_projectile"] = "weapon_flashbang",
        ["CFlashbang"] = "weapon_flashbang",
        ["CFlashbangProjectile"] = "weapon_flashbang",
        ["Flashbang"] = "weapon_flashbang",
        ["weapon_flashbang"] = "weapon_flashbang"
    };

    private static readonly HashSet<string> RifleLikeWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_ak47", "weapon_aug", "weapon_famas", "weapon_galilar", "weapon_m4a1", "weapon_m4a1_silencer", "weapon_sg556"
    };

    private static readonly Dictionary<string, int> ItemPrices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon_glock"] = 0,
        ["weapon_hkp2000"] = 0,
        ["weapon_usp_silencer"] = 0,
        ["item_kevlar"] = 650,
        ["item_assaultsuit"] = 1_000,
        ["item_defuser"] = 400,
        ["weapon_taser"] = 200,
        ["weapon_elite"] = 300,
        ["weapon_p250"] = 300,
        ["weapon_tec9"] = 500,
        ["weapon_fiveseven"] = 500,
        ["weapon_deagle"] = 700,
        ["weapon_cz75a"] = 500,
        ["weapon_revolver"] = 600,
        ["weapon_mac10"] = 1_050,
        ["weapon_mp9"] = 1_250,
        ["weapon_mp7"] = 1_500,
        ["weapon_mp5sd"] = 1_500,
        ["weapon_ump45"] = 1_200,
        ["weapon_bizon"] = 1_400,
        ["weapon_p90"] = 2_350,
        ["weapon_nova"] = 1_050,
        ["weapon_xm1014"] = 2_000,
        ["weapon_sawedoff"] = 1_100,
        ["weapon_mag7"] = 1_300,
        ["weapon_galilar"] = 1_800,
        ["weapon_ak47"] = 2_700,
        ["weapon_sg556"] = 3_000,
        ["weapon_famas"] = 1_950,
        ["weapon_m4a1"] = 2_900,
        ["weapon_m4a1_silencer"] = 2_900,
        ["weapon_aug"] = 3_300,
        ["weapon_ssg08"] = 1_700,
        ["weapon_awp"] = 4_750,
        ["weapon_scar20"] = 5_000,
        ["weapon_g3sg1"] = 5_000,
        ["weapon_negev"] = 1_700,
        ["weapon_m249"] = 5_200,
        ["weapon_flashbang"] = 200,
        ["weapon_hegrenade"] = 300,
        ["weapon_smokegrenade"] = 300,
        ["weapon_molotov"] = 400,
        ["weapon_incgrenade"] = 500,
        ["weapon_decoy"] = 50
    };

    private static readonly Dictionary<string, int> WeaponDefIndexes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon_deagle"] = 1,
        ["weapon_elite"] = 2,
        ["weapon_fiveseven"] = 3,
        ["weapon_glock"] = 4,
        ["weapon_ak47"] = 7,
        ["weapon_aug"] = 8,
        ["weapon_awp"] = 9,
        ["weapon_famas"] = 10,
        ["weapon_g3sg1"] = 11,
        ["weapon_galilar"] = 13,
        ["weapon_m249"] = 14,
        ["weapon_m4a1"] = 16,
        ["weapon_mac10"] = 17,
        ["weapon_p90"] = 19,
        ["weapon_mp5sd"] = 23,
        ["weapon_ump45"] = 24,
        ["weapon_xm1014"] = 25,
        ["weapon_bizon"] = 26,
        ["weapon_mag7"] = 27,
        ["weapon_negev"] = 28,
        ["weapon_sawedoff"] = 29,
        ["weapon_tec9"] = 30,
        ["weapon_taser"] = 31,
        ["weapon_hkp2000"] = 32,
        ["weapon_mp7"] = 33,
        ["weapon_mp9"] = 34,
        ["weapon_nova"] = 35,
        ["weapon_p250"] = 36,
        ["weapon_scar20"] = 38,
        ["weapon_sg556"] = 39,
        ["weapon_ssg08"] = 40,
        ["weapon_knife"] = 42,
        ["weapon_knife_t"] = 42,
        ["weapon_bayonet"] = 42,
        ["weapon_m9_bayonet"] = 42,
        ["weapon_karambit"] = 42,
        ["weapon_butterfly"] = 42,
        ["weapon_flip"] = 42,
        ["weapon_gut"] = 42,
        ["weapon_tactical"] = 42,
        ["weapon_falchion"] = 42,
        ["weapon_push"] = 42,
        ["weapon_survival_bowie"] = 42,
        ["weapon_ursus"] = 42,
        ["weapon_gypsy_jackknife"] = 42,
        ["weapon_stiletto"] = 42,
        ["weapon_widowmaker"] = 42,
        ["weapon_skeleton"] = 42,
        ["weapon_kukri"] = 42,
        ["weapon_flashbang"] = 43,
        ["weapon_hegrenade"] = 44,
        ["weapon_smokegrenade"] = 45,
        ["weapon_molotov"] = 46,
        ["weapon_decoy"] = 47,
        ["weapon_incgrenade"] = 48,
        ["weapon_c4"] = 49,
        ["weapon_m4a1_silencer"] = 60,
        ["weapon_usp_silencer"] = 61,
        ["weapon_cz75a"] = 63,
        ["weapon_revolver"] = 64,
    };

    private static readonly MemoryFunctionWithReturn<
        IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, int, CSmokeGrenadeProjectile>
        SmokeProjectileCreate = new(
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? @"55 4C 89 C1 48 89 E5 41 57 45 89 CF 41 56 49 89 FE"
                : @"48 8B C4 48 89 58 ? 48 89 68 ? 48 89 70 ? 57 41 56 41 57 48 81 EC ? ? ? ? 48 8B B4 24 ? ? ? ? 4D 8B F8");

    private static readonly MemoryFunctionWithReturn<
        IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CHEGrenadeProjectile>
        HeProjectileCreate = new(
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "55 4C 89 C1 48 89 E5 41 57 49 89 D7"
                : "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 50 48 8B AC 24 80 00 00 00 49 8B F8");

    private static readonly MemoryFunctionWithReturn<
        IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CMolotovProjectile>
        MolotovProjectileCreate = new(
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "55 48 8D 05 ? ? ? ? 48 89 E5 41 57 41 56 41 55 41 54 49 89 FC 53 48 81 EC ? ? ? ? 4C 8D 35"
                : "48 8B C4 48 89 58 10 4C 89 40 18 48 89 48 08");

    private readonly record struct BotEnemyMemoryOffsets(int TargetSpot, int Enemy, int IsVisible);

    private static readonly BotEnemyMemoryOffsets LinuxBotEnemyMemoryOffsets = new(
        TargetSpot: 0x597C,
        Enemy: 0x59E8,
        IsVisible: 0x59EC);

    private static readonly BotEnemyMemoryOffsets WindowsBotEnemyMemoryOffsets = new(
        TargetSpot: 0x59A4,
        Enemy: 0x5A10,
        IsVisible: 0x5A14);

    private static readonly HashSet<int> PrimaryWeaponDefIndexes = new(
        PrimaryWeapons
            .Select(itemName => WeaponDefIndexes.GetValueOrDefault(itemName, -1))
            .Where(defIndex => defIndex >= 0));

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind);
        RegisterEventHandler<EventPlayerFootstep>(OnPlayerFootstep);
        RegisterEventHandler<EventPlayerSound>(OnPlayerSound);
        RegisterListener<Listeners.OnTick>(OnTick);
        // Reload the per-map dataset on every map change so de_dust2 -> de_inferno swaps in the right openings.
        RegisterListener<Listeners.OnMapStart>(_ => LoadDataset());

        LoadConfig();
        _nativeReplayAvailable = BotController.IsCompatible();
        LoadDataset();

        if (hotReload && !string.IsNullOrWhiteSpace(Server.MapName))
        {
            PrepareRound();
        }
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _rayTrace = TryGetRayTrace();
    }

    public override void Unload(bool hotReload)
    {
        ClearNativeBuySuppression();
        StopAllNativeReplays();
        ClearRetakeMoveTos(releaseBots: true);
        CancelReplayBundlePrewarm();
        _sessions.Clear();
        _pendingAssignments.Clear();
        _preparedOpeningSessions.Clear();
        _nativeReplayPreloadKeys.Clear();
        _loadoutAppliedKeys.Clear();
        _roundLoadoutBudgets.Clear();
        _lastHurtTime.Clear();
        _manifestReplayProjectiles.Clear();
        _enemyWatchStates.Clear();
        ClearNativeWeaponState();
        _roundPrepared = false;
    }

    [ConsoleCommand("css_proreplay_reload", "Reloads the pro opening replay config and dataset.")]
    public void ReloadCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        LoadConfig();
        BotController.ResetCompatibility();
        _nativeReplayAvailable = BotController.IsCompatible();
        ApplyNativeBuySuppressionForPendingAssignments();
        _nativeReplayPreloadKeys.Clear();
        _preparedOpeningSessions.Clear();
        LoadDataset();
        Reply(player, commandInfo, $"loaded {_dataset?.Rounds.Count ?? 0} rounds for {_dataset?.MapName ?? "no dataset"}");
    }

    [ConsoleCommand("css_proreplay_status", "Prints the current pro opening replay status.")]
    public void StatusCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        Reply(player, commandInfo,
            $"enabled={_config.Enabled}, native={(_nativeReplayAvailable ? "on" : BotController.Status)}, map={Server.MapName}, rounds={_dataset?.Rounds.Count ?? 0}, pending={_pendingAssignments.Count}, moveTo={_retakeMoveTos.Count}, active={_sessions.Count}, prewarm={Volatile.Read(ref _replayBundlePrewarmCompleted)}/{Volatile.Read(ref _replayBundlePrewarmTotal)} fail={Volatile.Read(ref _replayBundlePrewarmFailed)}");
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        StopAllNativeReplays();
        ClearRetakeMoveTos(releaseBots: true);
        _sessions.Clear();
        _pendingAssignments.Clear();
        _preparedOpeningSessions.Clear();
        _nativeReplayPreloadKeys.Clear();
        _loadoutAppliedKeys.Clear();
        _roundLoadoutBudgets.Clear();
        _lastHurtTime.Clear();
        _handoffGraceExpiry.Clear();
        _manifestReplayProjectiles.Clear();
        _enemyWatchStates.Clear();
        ClearNativeWeaponState();
        _roundPrepared = false;
        _freezeEnded = false;
        _openingReplayStartQueued = false;
        _roundStartTime = Server.CurrentTime;
        _freezePeriodStartTime = IsFreezePeriod() ? Server.CurrentTime : -1f;
        if (IsWarmupPeriod())
        {
            ClearNativeBuySuppression();
            return HookResult.Continue;
        }

        CaptureRoundLoadoutBudgets();

        // Cache bombsite centers for the "T entered the bombsite" opening end-condition. func_bomb_target
        // entities exist on every defusal map and have an AbsOrigin at the painted-area centroid.
        _bombSiteCenters.Clear();
        foreach (var site in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("func_bomb_target"))
        {
            if (site == null || !site.IsValid || site.AbsOrigin == null) continue;
            _bombSiteCenters.Add(new Vector(site.AbsOrigin.X, site.AbsOrigin.Y, site.AbsOrigin.Z));
        }

        if (!CanUseDataset())
        {
            ClearNativeBuySuppression();
            return HookResult.Continue;
        }

        ApplyNativeBuySuppressionForCurrentBots();
        ScheduleFreezePrepareAttempts();
        return HookResult.Continue;
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        _freezeEnded = true;
        if (IsWarmupPeriod())
        {
            ClearNativeBuySuppression();
            return HookResult.Continue;
        }

        if (!CanUseDataset())
        {
            return HookResult.Continue;
        }

        StopOpeningPrerollSessions();
        if (!OpeningSessionsCoverCurrentBots()
            && (!_roundPrepared || _pendingAssignments.Count == 0 || !AssignmentsCoverCurrentBots()))
        {
            PrepareRound(scheduleLoadout: false);
        }

        StartReplaySessions();
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        StopAllNativeReplays();
        ClearRetakeMoveTos(releaseBots: true);
        _sessions.Clear();
        _pendingAssignments.Clear();
        _preparedOpeningSessions.Clear();
        _nativeReplayPreloadKeys.Clear();
        _loadoutAppliedKeys.Clear();
        _roundLoadoutBudgets.Clear();
        _lastHurtTime.Clear();
        _manifestReplayProjectiles.Clear();
        _enemyWatchStates.Clear();
        ClearNativeWeaponState();
        ClearNativeBuySuppression();
        _openingReplayStartQueued = false;
        _roundPrepared = false;
        _freezeEnded = false;
        _roundStartTime = -1f;
        _freezePeriodStartTime = -1f;
        _bombPlantTime = -1f;
        _bombDetonationTime = -1f;
        _bombPos = null;
        return HookResult.Continue;
    }

    private void ScheduleFreezePrepareAttempts()
    {
        var firstDelay = Math.Max(0f, _config.MatchSelectionDelay);
        float[] extraDelays = [0.75f, 1.5f, 2.25f, 3.0f];

        AddTimer(firstDelay, () => PrepareRound());
        foreach (var extraDelay in extraDelays)
        {
            AddTimer(firstDelay + extraDelay, () =>
            {
                if (_freezeEnded
                    || OpeningSessionsCoverCurrentBots())
                {
                    return;
                }

                PrepareRound();
            });
        }
    }

    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        // End any opening sessions still running -- the execute phase is over.
        for (var sessionIndex = _sessions.Count - 1; sessionIndex >= 0; sessionIndex--)
        {
            EndSession(sessionIndex, "planted");
        }

        // Capture bomb state for retake end-conditions.
        _bombPlantTime = Server.CurrentTime;
        var c4 = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4").FirstOrDefault();
        if (c4 != null && c4.IsValid)
        {
            _bombPos = c4.AbsOrigin == null ? null : new Vector(c4.AbsOrigin.X, c4.AbsOrigin.Y, c4.AbsOrigin.Z);
            // CPlantedC4 exposes m_flTimerLength via TimerLength schema; fall back to mp_c4timer default
            // when the schema lookup fails for any reason.
            float timerLen;
            try { timerLen = c4.TimerLength > 0 ? c4.TimerLength : DefaultBombTimerSeconds; }
            catch { timerLen = DefaultBombTimerSeconds; }
            _bombDetonationTime = _bombPlantTime + timerLen;
        }
        else
        {
            // Fallback: use planter pos.
            var planter = @event.Userid;
            var planterPawn = planter?.PlayerPawn?.Value;
            if (planterPawn != null && planterPawn.AbsOrigin != null)
            {
                _bombPos = new Vector(planterPawn.AbsOrigin.X, planterPawn.AbsOrigin.Y, planterPawn.AbsOrigin.Z);
            }
            _bombDetonationTime = _bombPlantTime + DefaultBombTimerSeconds;
        }

        StartRetakeSessions();
        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        // Strict definition of "taking damage": only count damage dealt by a live enemy player.
        // Skip fall damage / world damage (attacker null or == victim) and friendly fire (same team).
        var victim = @event.Userid;
        var attacker = @event.Attacker;
        if (victim == null || !victim.IsValid)
        {
            return HookResult.Continue;
        }
        if (attacker == null || !attacker.IsValid || attacker == victim)
        {
            return HookResult.Continue;
        }
        if (attacker.Team == victim.Team)
        {
            return HookResult.Continue;
        }

        _lastHurtTime[PlayerKey(victim)] = Server.CurrentTime;
        return HookResult.Continue;
    }

    private HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        // Intentionally a no-op now. Strict end conditions: only "saw an enemy" or "hit by an enemy" end
        // a session. Being flashed does not end the replay -- pros routinely run through their own
        // pop flashes, and we don't want yellow "flashed" hand-offs polluting the log.
        return HookResult.Continue;
    }

    private HookResult OnPlayerFootstep(EventPlayerFootstep @event, GameEventInfo info)
    {
        TryEndReplayOnEnemySound(@event.Userid);
        return HookResult.Continue;
    }

    private HookResult OnPlayerSound(EventPlayerSound @event, GameEventInfo info)
    {
        TryEndReplayOnEnemySound(@event.Userid);
        return HookResult.Continue;
    }

    // Close-range threshold: only end replay when enemy is within this distance.
    // Farther sounds still register in bot's AI perception (IgnoreEnemiesTimer cleared after 20s)
    // but don't interrupt the replay route.
    private const float SoundEndReplayRange = 600f;

    /// <summary>
    /// After 20s of replay, if a replaying bot hears an enemy sound at close range, end the
    /// replay session so the bot's AI takes over for combat.
    /// </summary>
    private void TryEndReplayOnEnemySound(CCSPlayerController? soundSource)
    {
        if (soundSource == null || !soundSource.IsValid || !soundSource.PawnIsAlive) return;
        var sourcePawn = soundSource.PlayerPawn.Value;
        if (sourcePawn?.AbsOrigin == null) return;

        var rangeSq = SoundEndReplayRange * SoundEndReplayRange;
        for (var i = _sessions.Count - 1; i >= 0; i--)
        {
            var session = _sessions[i];
            if (session.Kind != ReplaySessionKind.Opening) continue;

            var elapsed = Server.CurrentTime - session.StartTime;
            if (elapsed < 20f) continue;

            // Must be enemy of the replaying bot
            if (!IsLiveEnemy(session.Player, soundSource)) continue;

            var botPawn = session.Player.PlayerPawn.Value;
            if (botPawn?.AbsOrigin == null) continue;

            var dx = botPawn.AbsOrigin.X - sourcePawn.AbsOrigin.X;
            var dy = botPawn.AbsOrigin.Y - sourcePawn.AbsOrigin.Y;
            var dz = botPawn.AbsOrigin.Z - sourcePawn.AbsOrigin.Z;
            if (dx * dx + dy * dy + dz * dz <= rangeSq)
            {
                PrimeBotForKnownEnemy(session.Player, soundSource, markVisible: false);
                EndSession(i, "heard_enemy");
            }
        }
    }

}

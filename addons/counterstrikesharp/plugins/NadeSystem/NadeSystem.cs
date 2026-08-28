using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using RayTraceAPI;

namespace NadeSystem;

// ═══════════════════════════════════════════════════════════════
//  Data model
//  Reads converted NadeLauncher JSON: <mapname>_<grenadeType>.json
//  Each file is a JSON array of GrenadeData entries.
// ═══════════════════════════════════════════════════════════════

public class Vec3
{
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
    [JsonPropertyName("z")] public float Z { get; set; }
}

public class GrenadeData
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("mapName")]
    public string MapName { get; set; } = "";

    // "flash" | "smoke" | "he" | "molotov"
    [JsonPropertyName("grenadeType")]
    public string GrenadeType { get; set; } = "";

    // Where the projectile spawns (recorded release point)
    [JsonPropertyName("projectilePosition")]
    public Vec3 ProjectilePosition { get; set; } = new();

    // Recorded velocity vector
    [JsonPropertyName("projectileVelocity")]
    public Vec3 ProjectileVelocity { get; set; } = new();

    // Landing position
    [JsonPropertyName("landingPosition")]
    public Vec3 LandingPosition { get; set; } = new();
    // Tags
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonIgnore] public string TeamTag { get; set; } = "";

    // ── Computed zone properties (not serialized) ────────────
    // Zone center = XY projection of projectilePosition onto the ground (Z kept as-is)
    [JsonIgnore] public float ZoneX => ProjectilePosition.X;
    [JsonIgnore] public float ZoneY => ProjectilePosition.Y;
    [JsonIgnore] public float ZoneZ => ProjectilePosition.Z;

    // Smoke = 150, Other nades = 100 (radius)
    [JsonIgnore]
    public float ZoneRadius => string.Equals(GrenadeType, "smoke",
        StringComparison.OrdinalIgnoreCase) ? 150f : 100f;
}

// ═══════════════════════════════════════════════════════════════
//  Cooldown record
// ═══════════════════════════════════════════════════════════════

public class CooldownEntry
{
    public string GrenadeId { get; set; } = "";
    public float  ExpiresAt { get; set; }
}

// ═══════════════════════════════════════════════════════════════
//  Per-round throw counter
// ═══════════════════════════════════════════════════════════════

public class RoundCounter
{
    public int Flash   { get; set; }
    public int Smoke   { get; set; }
    public int HE      { get; set; }
    public int Molotov { get; set; }
}

public sealed class NadeSystemConfig : BasePluginConfig
{
    [JsonPropertyName("Mode")]
    public string Mode { get; set; } = "normal";

    // coach_hard replay assist chance, expressed as a percentage per eligible
    // recorded lineup attempt. Native Valve throws are unaffected.
    [JsonPropertyName("CoachHardReplayChances")]
    public Dictionary<string, int> CoachHardReplayChances { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["smoke"] = 30,
        ["flash"] = 20,
        ["he"] = 35,
        ["molotov"] = 40,
    };
}

// ═══════════════════════════════════════════════════════════════
//  Plugin
// ═══════════════════════════════════════════════════════════════

public class NadeSystemPlugin : BasePlugin, IPluginConfig<NadeSystemConfig>
{
    public override string ModuleName    => "NadeSystem";
    public override string ModuleVersion => "1.2.0";
    public override string ModuleAuthor  => "ed0ard";

    // grenades folder lives inside the plugin directory
    private string DataDir => Path.Combine(ModuleDirectory, "grenades");
    // precache all the nades on this map
    private List<GrenadeData> _mapNades = new();
    private string _botNadesMode = "normal";
    public NadeSystemConfig Config { get; set; } = new();

    private static readonly HashSet<string> SupportedModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "off", "less", "normal", "more", "max",
        "native", "coach_easy", "coach_normal", "coach_hard",
    };

    private bool UsesReplayDatabase => _botNadesMode is "less" or "normal" or "more" or "max" or "coach_hard";
    private bool UsesLegacyReplayFeatures => _botNadesMode is "less" or "normal" or "more" or "max";
    private bool UsesNativeBotNades => _botNadesMode is "native" or "coach_easy" or "coach_normal" or "coach_hard";

    public void OnConfigParsed(NadeSystemConfig config)
    {
        Config = config ?? new NadeSystemConfig();
        var mode = (Config.Mode ?? "normal").Trim().ToLowerInvariant();
        if (!SupportedModes.Contains(mode))
        {
            Server.PrintToConsole($"[NadeSystem] Unknown configured mode '{Config.Mode}', using normal.");
            mode = "normal";
        }

        _botNadesMode = mode;

        var normalizedChances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Config.CoachHardReplayChances ?? new Dictionary<string, int>())
        {
            var grenadeType = (entry.Key ?? "").Trim().ToLowerInvariant();
            if (grenadeType is not ("smoke" or "flash" or "he" or "molotov"))
            {
                Server.PrintToConsole($"[NadeSystem] Unknown coach_hard grenade chance type '{entry.Key}', ignoring it.");
                continue;
            }

            normalizedChances[grenadeType] = Math.Clamp(entry.Value, 0, 100);
        }

        foreach (var grenadeType in new[] { "smoke", "flash", "he", "molotov" })
        {
            if (!normalizedChances.ContainsKey(grenadeType))
                normalizedChances[grenadeType] = grenadeType switch
                {
                    "smoke" => 30,
                    "flash" => 20,
                    "he" => 35,
                    "molotov" => 40,
                    _ => 0,
                };
        }

        Config.CoachHardReplayChances = normalizedChances;
    }
    // ── State ──────────────────────────────────────────────────
    private List<GrenadeData>     _db                = new();
    private List<CooldownEntry>   _cooldowns         = new();
    private HashSet<uint>         _replayBots        = new();
    private HashSet<uint>         _smokeCooldownBots = new();
    private int                   _tick              = 0;
    private bool                  _roundOver         = false;
    private float                 _freezeEndTime     = 0f;
    private Dictionary<uint, int> _roundSpendPerBot  = new();
    private HashSet<uint>         _poorBots          = new();
    // Information System
    private Dictionary<string, float> _probFailCooldown = new();
    // flash immunity
    private Dictionary<uint, float> _botFlashImmunityUntil = new();
    // Ray-Trace interface
    private static readonly PluginCapability<CRayTraceInterface> _rayTraceCapability =
        new("raytrace:craytraceinterface");
    // Special Nades
    private bool _defuseSmokeUsed    = false;
    private bool _defuseFlashUsed    = false;
    private bool _plantSmokeUsed     = false;
    // key = TeamNum (2=T, 3=CT)
    private Dictionary<int, RoundCounter> _roundCountByTeam = new();
    // Less Mode: per-bot round throw counter (key = bot Index)
    private Dictionary<uint, RoundCounter> _roundCountByBot = new();
    // key = bot Id, value = first continuous damage time
    private Dictionary<uint, float> _botMolotovDmgStart = new();
    // team-side cooldown: key = teamNum (2=T,3=CT), value = expiry time
    private Dictionary<int, float>  _molotovEscapeSmokeCooldown = new();
    // Normal and More modes
    private Dictionary<int, float> _retaliationCooldown      = new();
    // Normal Mode
    private Dictionary<int,  int>    _earlySmokeCountByTeam   = new();
    private Dictionary<uint, HashSet<string>> _botInFlashZone = new();
    // Normal Mode: post-throw probability window for flash
    // key = botIndex, value = (windowExpiresAt, blindRatio)
    private Dictionary<uint, (float ExpiresAt, float Ratio)> _botFlashRatioWindow = new();
    // ── Information system (sound trail + vision) ──────────────
    // Plain value-type coordinate: avoids allocating a CSS Vector (managed wrapper
    // + native memory) per recorded sound point.
    private readonly record struct SoundPoint(float X, float Y, float Z);
    // key = controller index, value = list of positions where this player made audible sound.
    // Only points within 100f of the player's current position are kept each tick.
    private Dictionary<uint, List<SoundPoint>> _soundPoints = new();
    // key = controller index, value = last weapon_fire time (global, all players)
    private Dictionary<uint, float> _botLastFireTime = new();
    // key = controller index, value = last grenade_thrown time
    private Dictionary<uint, float> _botLastGrenadeThrowTime = new();
    // Sound trail capture radius (a recorded sound point counts as "info" within this range)
    private const float SoundInfoRadius = 100f;
    // Footstep speed threshold (horizontal velocity above this makes audible footstep sound)
    private const float FootstepSpeedThreshold = 150f;
    // Max distance at which a sound point can be heard by an enemy.
    private const float SoundHearRadius = 1000f;
    // ── Static lookup tables ───────────────────────────────────
    // (mapName_teamTag) → seconds after freezeend within which smoke/flash may trigger
    // e.g. "de_dust2_T" → 13f  means T-side nades tagged "T" must trigger within 13s of freezeend
    private static readonly Dictionary<string, float> ThrowSchedule =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["de_dust2_T"]  = 13f,
        ["de_dust2_CT"] = 13f,
        ["de_ancient_T"] = 14f,
        ["de_ancient_CT"] = 14f,
        ["de_inferno_T"] = 15.5f,
        ["de_inferno_CT"] = 15.5f,
        ["de_mirage_T"] = 21f,
        ["de_mirage_CT"] = 21f,
        ["de_nuke_T"] = 14f,
        ["de_nuke_CT"] = 14f,
        ["de_anubis_T"] = 14f,
        ["de_anubis_CT"] = 14f,
        ["de_train_T"] = 17f,
        ["de_train_CT"] = 17f,
        ["de_vertigo_T"] = 11f,
        ["de_vertigo_CT"] = 11f,
        ["de_overpass_T"] = 20f,
        ["de_overpass_CT"] = 20f,
        ["de_cache_T"] = 15.5f,
        ["de_cache_CT"] = 15.5f,
    };
    // grenade type string → projectile entity designer name
    private static readonly Dictionary<string, string> TypeToProjectile =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["smoke"]   = "smokegrenade_projectile",
        ["flash"]   = "flashbang_projectile",
        ["he"]      = "hegrenade_projectile",
        ["molotov"] = "molotov_projectile",
        ["incgrenade"] = "molotov_projectile",
    };

    // cooldown after each successful replay (seconds)
    private static readonly Dictionary<string, float> CooldownSec =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["smoke"]   = 19f,
        ["flash"]   = 4f,
        ["he"]      = 5f,
        ["molotov"] = 10f,
        ["decoy"]   = 600f,  // per-round once
    };

    // T-side purchase cost
    private static readonly Dictionary<string, int> CostT =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["flash"]      = 200,
        ["smoke"]      = 300,
        ["he"]         = 300,
        ["molotov"]    = 400,
        ["incgrenade"] = 400,
        ["decoy"]      = 0,
    };

    // CT-side purchase cost
    private static readonly Dictionary<string, int> CostCT =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["flash"]      = 200,
        ["smoke"]      = 300,
        ["he"]         = 300,
        ["molotov"]    = 500,
        ["incgrenade"] = 500,
        ["decoy"]      = 0,
    };

    // ── Native grenade factory functions ──────────────────────
    //
    // CreateEntityByName produces a physically valid projectile but
    // does NOT call the C++ class constructor logic that arms the
    // grenade.  Flash detonates correctly via CreateEntityByName
    // HE, smoke, and molotov rely on internal state that
    // only the native Create() function establishes.
    //
    // Signatures working on Linux + Windows as of CS2 build examined.
    // These may need re-finding after CS2 updates.

    // CSmokeGrenadeProjectile::Create(pos, ang, vel, vel, owner, itemDef, team)
    private static readonly MemoryFunctionWithReturn<
        IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, int, CSmokeGrenadeProjectile>
        _smokeCreate = new(
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? @"55 4C 89 C1 48 89 E5 41 57 49 89 FF 41 56 45 89 CE 41 55"
                : @"48 8B C4 48 89 58 ? 48 89 68 ? 48 89 70 ? 57 41 56 41 57 48 81 EC ? ? ? ? 48 8B B4 24 ? ? ? ? 4D 8B F8");

    // CHEGrenadeProjectile::Create(pos, ang, vel, vel, owner, itemDef)
    private static readonly MemoryFunctionWithReturn<
        IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CHEGrenadeProjectile>
        _heCreate = new(
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "55 4C 89 C1 48 89 E5 41 57 49 89 FF 41 56 49 89 D6 48 89 F2 48 89 FE 41 55"
                : "48 89 ? 24 ? 48 89 ? 24 ? 48 89 ? 24 ? 57 48 83 EC ? 48 8B ? 24 ? 49 8B F8 4C 8B C2 0F 29 ? 24 ? 48 8B D1 48 8B D9 48 8D 0D ? ? ? ? 4C 8B CD E8 ? ? ? ? F3 0F 10 0D ? ? ? ? 48 8B C8 48 8B F0 E8 ? ? ? ? 48 8B D7 48 8B CE");

    // CMolotovProjectile::Create(pos, ang, vel, vel, owner, itemDef)
    private static readonly MemoryFunctionWithReturn<
        IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CMolotovProjectile>
        _molotovCreate = new(
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "55 48 8D 05 ? ? ? ? 48 89 E5 41 57 41 56 41 55 41 54 49 89 FC 53 48 81 EC ? ? ? ? 4C 8D 35"
                : "48 8B C4 48 89 58 10 4C 89 40 18 48 89 48 08");

    // ═══════════════════════════════════════════════════════════
    //  Load
    // ═══════════════════════════════════════════════════════════

    public override void Load(bool hotReload)
    {
        ApplyModeSettings();
        Directory.CreateDirectory(DataDir);
        LoadDb();

        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundFreezeEnd>(OnFreezeEnd);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterEventHandler<EventBombBegindefuse>(OnBombBeginDefuse);
        RegisterEventHandler<EventBombBeginplant>(OnBombBeginPlant);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventWeaponReload>(OnWeaponReload);
        RegisterEventHandler<EventWeaponZoom>(OnWeaponZoom);
        RegisterEventHandler<EventGrenadeThrown>(OnGrenadeThrown);
        RegisterEventHandler<EventPlayerJump>(OnPlayerJump);
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _db.Clear();
            LoadDb();
            _cooldowns.Clear();
            _roundCountByTeam.Clear();
            _replayBots.Clear();
        });
        
        AddCommand("bot_nades", "Control bot nade mode", CmdBotNades);
        
        Server.PrintToConsole($"[NadeSystem] Loaded — {_db.Count} grenades in DB.");
    }

    // Native and coach modes deliberately leave grenade creation to Valve's
    // CCSBot. Legacy replay modes keep native grenade purchasing disabled.
    private void ApplyModeSettings()
    {
        if (!UsesNativeBotNades)
        {
            Server.ExecuteCommand("bot_allow_grenades 0");
            Server.ExecuteCommand("sv_bot_buy_grenade_chance 0");
            return;
        }

        Server.ExecuteCommand("bot_allow_grenades 1");
        int chance = _botNadesMode switch
        {
            "coach_easy" => 20,
            "coach_normal" => 45,
            "coach_hard" => 70,
            _ => 35,
        };
        Server.ExecuteCommand($"sv_bot_buy_grenade_chance {chance}");

        // Keep native buys biased toward utility without forcing a lineup.
        int smoke = _botNadesMode switch
        {
            "coach_easy" => 4,
            "coach_normal" => 5,
            "coach_hard" => 6,
            _ => 5,
        };
        int flash = _botNadesMode switch
        {
            "coach_easy" => 2,
            "coach_normal" => 3,
            "coach_hard" => 4,
            _ => 3,
        };
        int he = _botNadesMode switch
        {
            "coach_easy" => 1,
            "coach_normal" => 3,
            "coach_hard" => 4,
            _ => 2,
        };
        int molotov = _botNadesMode switch
        {
            "coach_easy" => 2,
            "coach_normal" => 4,
            "coach_hard" => 6,
            _ => 3,
        };
        Server.ExecuteCommand($"sv_bot_buy_smoke_weight {smoke}");
        Server.ExecuteCommand($"sv_bot_buy_flash_weight {flash}");
        Server.ExecuteCommand($"sv_bot_buy_hegrenade_weight {he}");
        Server.ExecuteCommand($"sv_bot_buy_molotov_weight {molotov}");
        Server.ExecuteCommand("sv_bot_buy_decoy_weight 0");
    }

    // ═══════════════════════════════════════════════════════════
    //  DB I/O
    //  Reads every *.json in the grenades/ folder.
    //  Each file is a JSON array produced by convert_lineups.py.
    //  Expected filename convention: <mapname>_<grenadeType>.json
    //  but the mapName field inside each entry is authoritative.
    // ═══════════════════════════════════════════════════════════

    private void LoadDb()
    {
        int loaded = 0;
        foreach (var file in Directory.GetFiles(DataDir, "*.json"))
        {
            try
            {
                var text = File.ReadAllText(file);
                var list = JsonSerializer.Deserialize<List<GrenadeData>>(text);
                if (list == null) continue;
                foreach (var entry in list)
                {
                    entry.Description ??= "";
                    // Normalize once at load so hot paths can compare without ToLower()
                    entry.GrenadeType = (entry.GrenadeType ?? "").ToLowerInvariant();
                    // Rewrite grenadeType to "decoy" if description contains "decoy"
                    if (entry.Description.Contains("decoy", StringComparison.OrdinalIgnoreCase))
                        entry.GrenadeType = "decoy";
                    // Tags for nades that only trigger at round start
                    if (entry.Description.StartsWith("CT", StringComparison.OrdinalIgnoreCase))
                        entry.TeamTag = "CT";
                    else if (entry.Description.StartsWith("T", StringComparison.OrdinalIgnoreCase))
                        entry.TeamTag = "T";
                    else
                        entry.TeamTag = "";
                }
                _db.AddRange(list);
                loaded += list.Count;
            }
            catch (Exception ex)
            {
                Server.PrintToConsole(
                    $"[NadeSystem] Failed to load {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        // Pre-filter to current map
        _mapNades = _db
            .Where(g => string.Equals(g.MapName, Server.MapName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Server.PrintToConsole($"[NadeSystem] Loaded {loaded} grenades from {DataDir}");
    }

    // ═══════════════════════════════════════════════════════════
    //  Bot Zone Detection
    //
    //  Scanned every 4 ticks.
    // ═══════════════════════════════════════════════════════════

    private void CheckBotZones()
    {
        if (!UsesReplayDatabase) return;
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        if (rules?.GameRules?.FreezePeriod == true) return;
        // Don't throw nades if the round is over
        if (_roundOver) return;

        var mapNades = _mapNades;
        if (mapNades.Count == 0) return;

        // Materialize the controller list once per scan; every sub-check below
        // reuses it instead of re-walking the entity table.
        var allControllers = Utilities
            .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .ToList();

        bool hasLiveEnemyT  = HasLiveEnemyForTeam((int)CsTeam.Terrorist, allControllers);
        bool hasLiveEnemyCT = HasLiveEnemyForTeam((int)CsTeam.CounterTerrorist, allControllers);

        foreach (var bot in allControllers)
        {
            if (!bot.IsValid || !bot.IsBot) continue;
            var pawn = bot.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) continue;
            if (pawn.Bot == null) continue;
            // In case the bot has been taken over
            bool isTakenOver = bot.HasBeenControlledByPlayerThisRound;
            if (isTakenOver) continue;

            if (!bot.PawnIsAlive) continue;
            if (_replayBots.Contains((uint)bot.Index)) continue;

            var pos = pawn.AbsOrigin;
            if (pos == null) continue;

            foreach (var g in mapNades)
            {
                var gtype = g.GrenadeType; // lowercase since LoadDb
                float viewOffsetZ = 64f;
                // 2D distance check (XY plane only)
                float dx = pos.X - g.ZoneX;
                float dy = pos.Y - g.ZoneY;
                float dz = pos.Z+ viewOffsetZ - g.ProjectilePosition.Z;
                // DECOY: handled entirely here, bypasses all other checks
                if (gtype == "decoy")
                {
                    if (_botNadesMode == "off" || _botNadesMode == "coach_hard") continue;
                    if (IsOnCooldown(g.Id)) continue;
                    if (dx * dx + dy * dy > 200f * 200f) continue;
                    if (MathF.Abs(dz) > 85f) continue;
                    RegisterCooldown(g.Id, "decoy");
                    SpawnProjectile(bot, g);
                    // No _replayBots, no IncrementCount, no money deduction
                    break;
                }
                // Not DECOY
                if (dx * dx + dy * dy > g.ZoneRadius * g.ZoneRadius) continue;
                // Vertical distance check
                if (MathF.Abs(dz) > 85f) continue;
                if (IsOnCooldown(g.Id)) continue;
                if (gtype is "he" or "molotov" or "flash" && IsOnProbFailCooldown(g.Id)) continue;
                // Probability attempt cooldown
                if (gtype == "smoke" && _smokeCooldownBots.Contains((uint)bot.Index)) continue;
                if (_botNadesMode == "coach_hard"
                    && !PassesCoachHardReplayGate(bot, g, allControllers)) continue;
                // Smoke Overlap Check
                if (gtype == "smoke")
                {
                    float lx = g.LandingPosition.X, ly = g.LandingPosition.Y, lz = g.LandingPosition.Z;
                    bool tooClose = _cooldowns
                        .Where(c => c.ExpiresAt > Server.CurrentTime)
                        .Select(c => _mapNades.FirstOrDefault(d => d.Id == c.GrenadeId))
                        .Any(d => d != null
                               && string.Equals(d.GrenadeType, "smoke", StringComparison.OrdinalIgnoreCase)
                               && Dist3D(lx, ly, lz, d.LandingPosition.X, d.LandingPosition.Y, d.LandingPosition.Z) < 100f);
                    if (tooClose) continue;
                }

                bool hasLiveEnemy = bot.TeamNum == (int)CsTeam.Terrorist ? hasLiveEnemyT : hasLiveEnemyCT;
                if (!hasLiveEnemy) continue;
                // Direction Judge 90°
                // normal mode/ less mode/ more mode：smoke and flash
                // max mode：smoke
                bool doDirectionCheck = _botNadesMode == "normal" || _botNadesMode == "more" || _botNadesMode == "less" || _botNadesMode == "coach_hard"
                    ? (gtype == "smoke" || gtype == "flash")
                    : (gtype == "smoke");
                if (doDirectionCheck && !FacesThrowDirection(pawn, g)) continue;

                if (_botNadesMode == "max")
                {
                    if (gtype == "flash" && CanBlindAnyEnemy(bot, g, allControllers).Count == 0) continue;
                    // No HE/molotov within 1s of this bot firing.
                    if (gtype is "he" or "molotov" && FiredRecently(bot, 1f)) continue;
                    if (gtype is "he" or "molotov")
                    {
                        float lx = g.LandingPosition.X, ly = g.LandingPosition.Y, lz = g.LandingPosition.Z;
                        bool enemyIn400 = allControllers
                            .Any(p =>
                            {
                                if (!p.IsValid || (int)p.TeamNum == bot.TeamNum) return false;
                                var ep = GetActiveLivePawn(p)?.AbsOrigin;
                                if (ep == null) return false;
                                float ddx = ep.X - lx, ddy = ep.Y - ly, ddz = ep.Z - lz;
                                return ddx*ddx + ddy*ddy + ddz*ddz <= 300f * 300f;
                            });
                        // Throw directly if any enemy is in range
                        if (!enemyIn400) continue;

                        // Don't throw molotov into smoke
                        if (gtype == "molotov")
                        {
                            float now = Server.CurrentTime;
                            bool intoSmoke = _cooldowns.Any(cd =>
                            {
                                if (cd.ExpiresAt <= now) return false;
                                var s = _mapNades.FirstOrDefault(d => d.Id == cd.GrenadeId
                                    && string.Equals(d.GrenadeType, "smoke", StringComparison.OrdinalIgnoreCase));
                                if (s == null) return false;
                                float ddx = lx - s.LandingPosition.X;
                                float ddy = ly - s.LandingPosition.Y;
                                float ddz = lz - s.LandingPosition.Z;
                                return ddx*ddx + ddy*ddy + ddz*ddz < 200f * 200f;
                            });
                            if (intoSmoke) continue;
                        }
                    }
                    // smoke: no additional check beyond zone/overlap/direction above
                    TryReplay(bot, g, allControllers);
                }
                else //normal mode/ more mode
                {
                    if (gtype == "flash")
                    {
                        uint bidx = (uint)bot.Index;
                        if (!_botInFlashZone.TryGetValue(bidx, out var inZoneSet))
                        {
                            inZoneSet = new HashSet<string>();
                            _botInFlashZone[bidx] = inZoneSet;
                        }
                        // Already inside this zone, skip
                        if (inZoneSet.Contains(g.Id)) continue;
                        // Entering this zone, mark and allow replay
                        inZoneSet.Add(g.Id);
                        // 12s ratio window check
                        if (_botFlashRatioWindow.TryGetValue(bidx, out var window)
                            && Server.CurrentTime < window.ExpiresAt)
                        {
                            // within 12s window: apply ratio threshold
                            if (window.Ratio < 1f && Random.Shared.NextDouble() >= window.Ratio) break;
                        }
                        // Passed — compute new ratio and reset window after TryConditionalReplay succeeds
                        // We pass ratio computation into TryConditionalReplay via a pre-check here
                        var (blindable, total) = CountBlindableEnemies(bot, g, allControllers);
                        float ratio = GetFlashRatioThreshold(blindable, total);
                        if (ratio <= 0f) break; // 0% → never throw
                        _botFlashRatioWindow[bidx] = (Server.CurrentTime + 12f, ratio);

                        TryConditionalReplay(bot, g, allControllers);
                        break;
                    }

                    TryConditionalReplay(bot, g, allControllers);
                }
                break; // one grenade trigger per bot per scan
            }
            // Clear the flash zone marker for this bot
            if (_botInFlashZone.TryGetValue((uint)bot.Index, out var currentInZone))
            {
                float viewOffsetZLeave = 64f;
                currentInZone.RemoveWhere(gid =>
                {
                    var rec = mapNades.FirstOrDefault(x => x.Id == gid
                        && string.Equals(x.GrenadeType, "flash", StringComparison.OrdinalIgnoreCase));
                    if (rec == null) return true;
                    float dx  = pos.X - rec.ZoneX;
                    float dy  = pos.Y - rec.ZoneY;
                    float dz  = pos.Z + viewOffsetZLeave - rec.ProjectilePosition.Z;
                    // Clear the marker when we leave this zone
                    return dx*dx + dy*dy > rec.ZoneRadius * rec.ZoneRadius
                        || MathF.Abs(dz) > 85f;
                });
            }
        }
    }

    private static bool HasLiveEnemyForTeam(int teamNum, List<CCSPlayerController> allControllers)
    => allControllers
        .Any(p => p.IsValid && p.PawnIsAlive
            && ((int)p.TeamNum == 2 || (int)p.TeamNum == 3)
            && (int)p.TeamNum != teamNum);

    // Direction Judge 90°
    private bool FacesThrowDirection(CCSPlayerPawn pawn, GrenadeData g)
    {
        var eyeAngles = pawn.EyeAngles;
        if (eyeAngles == null) return true;
        float yawRad  = eyeAngles.Y * (MathF.PI / 180f);
        float botDirX = MathF.Cos(yawRad);
        float botDirY = MathF.Sin(yawRad);
        float velX    = g.ProjectileVelocity.X;
        float velY    = g.ProjectileVelocity.Y;
        float velLen  = MathF.Sqrt(velX * velX + velY * velY);
        if (velLen <= 0f) return true;
        float dot = botDirX * (velX / velLen) + botDirY * (velY / velLen);
        return dot >= 0f; // angle > 90°, skip
    }

    // Returns the pawn the controller is CURRENTLY operating (m_hPawn), only if alive.
    // When a dead human takes over a bot, the human's PlayerPawn (m_hPlayerPawn) still
    // points at their corpse while Pawn (m_hPawn) points at the live bot body.
    private CCSPlayerPawn? GetActiveLivePawn(CCSPlayerController p)
    {
        if (!p.IsValid) return null;
        var basePawn = p.Pawn?.Value;
        if (basePawn == null || !basePawn.IsValid) return null;
        if (basePawn.LifeState != 0) return null; // 0 = LIFE_ALIVE
        if (basePawn.Health <= 0) return null;
        return basePawn.As<CCSPlayerPawn>();
    }

    // ═══════════════════════════════════════════════════════════
    //  Information system: sound trail + vision
    //  Updated every tick for ALL players
    // ═══════════════════════════════════════════════════════════
    // Record a sound point at the player's current origin.
    private void RecordSoundPoint(CCSPlayerController? player, List<CCSPlayerController>? allPlayers = null)
    {
        if (player == null) return;
        var origin = GetActiveLivePawn(player)?.AbsOrigin;
        if (origin == null) return;

        // Only keep this sound point if at least one enemy is close enough to hear it.
        float ox = origin.X, oy = origin.Y, oz = origin.Z;
        float r2 = SoundHearRadius * SoundHearRadius;
        var candidates = allPlayers
            ?? Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller").ToList();
        bool audibleToEnemy = candidates
            .Any(e =>
            {
                if (!e.IsValid || (int)e.TeamNum == player.TeamNum) return false;
                var ep = GetActiveLivePawn(e)?.AbsOrigin;
                if (ep == null) return false;
                float dx = ep.X - ox, dy = ep.Y - oy, dz = ep.Z - oz;
                return dx*dx + dy*dy + dz*dz <= r2;
            });
        if (!audibleToEnemy) return;

        uint idx = (uint)player.Index;
        if (!_soundPoints.TryGetValue(idx, out var list))
        {
            list = new List<SoundPoint>();
            _soundPoints[idx] = list;
        }
        // Dedup: skip if within 1u of the last point. Repeated sound made in place
        if (list.Count > 0)
        {
            var last = list[^1];
            float ddx = ox - last.X, ddy = oy - last.Y, ddz = oz - last.Z;
            if (ddx * ddx + ddy * ddy + ddz * ddz < 1f) return;
        }
        list.Add(new SoundPoint(ox, oy, oz));
    }

    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var p = @event.Userid;
        if (p != null && p.IsValid)
            _botLastFireTime[(uint)p.Index] = Server.CurrentTime;
        RecordSoundPoint(p);
        return HookResult.Continue;
    }

    private HookResult OnWeaponReload(EventWeaponReload @event, GameEventInfo info)
    {
        RecordSoundPoint(@event.Userid);
        return HookResult.Continue;
    }

    private HookResult OnWeaponZoom(EventWeaponZoom @event, GameEventInfo info)
    {
        RecordSoundPoint(@event.Userid);
        return HookResult.Continue;
    }

    private HookResult OnGrenadeThrown(EventGrenadeThrown @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid && player.IsBot)
            _botLastGrenadeThrowTime[(uint)player.Index] = Server.CurrentTime;
        RecordSoundPoint(@event.Userid);
        return HookResult.Continue;
    }

    private HookResult OnPlayerJump(EventPlayerJump @event, GameEventInfo info)
    {
        RecordSoundPoint(@event.Userid);
        return HookResult.Continue;
    }

    // Per-tick maintenance of every player's sound trail.
    // Delete sound points that are now farther than SoundInfoRadius from the player.
    // Add a fresh point if the player is currently making footstep sound (speed > threshold).
    // Pruning + dead-player cleanup run every call; footstep recording is throttled by the caller to every 4 ticks.
    private void UpdateSoundTrails(bool recordFootsteps)
    {
        var allPlayers = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller").ToList();
        foreach (var p in allPlayers)
        {
            if (!p.IsValid) continue;
            uint idx = (uint)p.Index;

            var pawn = GetActiveLivePawn(p);
            if (pawn == null)
            {
                _soundPoints.Remove(idx);
                continue;
            }
            var origin = pawn.AbsOrigin;
            if (origin == null) continue;

            float cx = origin.X, cy = origin.Y, cz = origin.Z;

            // Delete all kinds of sound points outside the info radius (keep only what is "near here").
            if (_soundPoints.TryGetValue(idx, out var list) && list.Count > 0)
            {
                float r2 = SoundInfoRadius * SoundInfoRadius;
                list.RemoveAll(pt =>
                {
                    float dx = pt.X - cx, dy = pt.Y - cy, dz = pt.Z - cz;
                    return dx * dx + dy * dy + dz * dz > r2;
                });
            }

            // Footstep sound: horizontal speed above threshold.
            if (recordFootsteps)
            {
                var vel = pawn.AbsVelocity;
                if (vel != null)
                {
                    float speed2 = vel.X * vel.X + vel.Y * vel.Y;
                    if (speed2 > FootstepSpeedThreshold * FootstepSpeedThreshold)
                        RecordSoundPoint(p, allPlayers);
                }
            }
        }
    }

    // True if this player currently has any retained sound point near them
    // (Made audible sound here, now or while passing within SoundInfoRadius).
    private bool PlayerMadeAudibleSound(CCSPlayerController player)
    {
        if (!player.IsValid) return false;
        return _soundPoints.TryGetValue((uint)player.Index, out var list) && list.Count > 0;
    }

    // True if the given enemy currently sees the target via the official spotting system.
    // Reads the TARGET's SpottedByMask and checks the enemy's slot bit.
    // Falls back to FOV + RayTrace if the schema read is unavailable.
    private bool EnemySeesTarget(CCSPlayerController enemy, CCSPlayerController target)
    {
        if (!enemy.IsValid || !target.IsValid) return false;
        var targetPawn = GetActiveLivePawn(target);
        if (targetPawn == null || !targetPawn.IsValid) return false;

        try
        {
            var spotted = targetPawn.EntitySpottedState;
            if (spotted != null)
            {
                int slot = enemy.Slot; // entity index - 1
                if (slot >= 0)
                {
                    var mask = spotted.SpottedByMask; // uint[2]
                    int word = slot / 32;
                    int bit  = slot % 32;
                    if (word >= 0 && word < mask.Length)
                        return (mask[word] & (1u << bit)) != 0;
                }
            }
        }
        catch { /* fall through to geometric check */ }

        // Fallback: FOV + RayTrace from enemy eyes to target eyes.
        return EnemySeesTargetGeometric(enemy, target);
    }

    // Geometric vision fallback.
    private bool EnemySeesTargetGeometric(CCSPlayerController enemy, CCSPlayerController target)
    {
        var ep = GetActiveLivePawn(enemy);
        var tp = GetActiveLivePawn(target);
        if (ep?.AbsOrigin == null || ep.EyeAngles == null) return false;
        if (tp?.AbsOrigin == null) return false;

        float eyeX = ep.AbsOrigin.X, eyeY = ep.AbsOrigin.Y, eyeZ = ep.AbsOrigin.Z + 64f;
        float tx = tp.AbsOrigin.X, ty = tp.AbsOrigin.Y, tz = tp.AbsOrigin.Z + 64f;

        float dx = tx - eyeX, dy = ty - eyeY, dz = tz - eyeZ;
        float dist2 = dx * dx + dy * dy + dz * dz;
        if (dist2 > 1300f * 1300f) return false;

        float eYawRad   =  ep.EyeAngles.Y * MathF.PI / 180f;
        float ePitchRad = -ep.EyeAngles.X * MathF.PI / 180f;
        float fwdX = MathF.Cos(ePitchRad) * MathF.Cos(eYawRad);
        float fwdY = MathF.Cos(ePitchRad) * MathF.Sin(eYawRad);
        float fwdZ = MathF.Sin(ePitchRad);

        float yawToT   = MathF.Atan2(dy, dx);
        float eyeYaw   = MathF.Atan2(fwdY, fwdX);
        float deltaYaw = MathF.Abs(MathF.Atan2(MathF.Sin(yawToT - eyeYaw),
                                               MathF.Cos(yawToT - eyeYaw)));
        float pitchToT = MathF.Atan2(dz, MathF.Sqrt(dx * dx + dy * dy));
        float eyePitch = MathF.Atan2(fwdZ, MathF.Sqrt(fwdX * fwdX + fwdY * fwdY));
        float deltaPitch = MathF.Abs(pitchToT - eyePitch);
        if (deltaYaw <= 0.927f && deltaPitch <= MathF.PI / 4f) // Horizontal FOV 106° // Vertical FOV 90°
        {
            var tEye = new Vec3 { X = tx, Y = ty, Z = tz };
            return FlashHasLoS(tEye, eyeX, eyeY, eyeZ);
        }
        return false;
    }

    // General-purpose: does `enemy` have information (vision or sound) on `target`?
    private bool HasInformationOn(CCSPlayerController enemy, CCSPlayerController target)
    {
        if (PlayerMadeAudibleSound(target)) return true;
        if (EnemySeesTarget(enemy, target)) return true;
        return false;
    }

    // Coach hard may reuse a recorded lineup only when the Bot has current
    // information about an enemy near that lineup's landing area. The low roll
    // keeps native Valve behavior dominant and prevents lineup spam.
    private bool PassesCoachHardReplayGate(CCSPlayerController bot, GrenadeData g,
        List<CCSPlayerController> allControllers)
    {
        if (g.GrenadeType == "decoy") return false;
        if (IsOnProbFailCooldown(g.Id)) return false;

        var pawn = bot.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return false;
        if (pawn.IsDefusing) return false;
        if (!TryGetReadyCoachGrenade(pawn, g.GrenadeType, out _)) return false;
        if (FiredRecently(bot, 0.75f)) return false;
        if (GrenadeThrownRecently(bot, 1.5f)) return false;

        string chanceType = g.GrenadeType == "incgrenade" ? "molotov" : g.GrenadeType;
        int chance = Config.CoachHardReplayChances.TryGetValue(chanceType, out var configuredChance)
            ? configuredChance
            : chanceType switch
            {
                "smoke" => 30,
                "flash" => 20,
                "he" => 35,
                "molotov" => 40,
                _ => 0,
            };
        if (chance <= 0) return false;

        float lx = g.LandingPosition.X, ly = g.LandingPosition.Y, lz = g.LandingPosition.Z;
        bool hasCurrentInfo = allControllers.Any(enemy =>
        {
            if (!enemy.IsValid || (int)enemy.TeamNum == bot.TeamNum) return false;
            var enemyPawn = GetActiveLivePawn(enemy);
            var enemyPos = enemyPawn?.AbsOrigin;
            if (enemyPos == null) return false;
            if (Dist3D(lx, ly, lz, enemyPos.X, enemyPos.Y, enemyPos.Z) > 1200f) return false;
            return HasInformationOn(bot, enemy);
        });
        if (!hasCurrentInfo) return false;

        if (Random.Shared.Next(100) >= chance)
        {
            RegisterProbFailCooldown(g.Id);
            return false;
        }

        return true;
    }

    // Recently fired（used to suppress HE/molotov right after shooting）
    private bool FiredRecently(CCSPlayerController player, float seconds)
    {
        if (_botLastFireTime.TryGetValue((uint)player.Index, out float t))
            return Server.CurrentTime - t < seconds;
        return false;
    }

    private bool GrenadeThrownRecently(CCSPlayerController player, float seconds)
    {
        if (_botLastGrenadeThrowTime.TryGetValue((uint)player.Index, out float t))
            return Server.CurrentTime - t < seconds;
        return false;
    }

    // Coach hard only assists a throw that the native Bot has already selected.
    // This keeps eco rounds and weapon selection authoritative while allowing
    // the replay layer to choose a recorded trajectory.
    private static bool TryGetReadyCoachGrenade(CCSPlayerPawn pawn, string gtype,
        out CCSWeaponBase grenade)
    {
        grenade = null!;
        var active = pawn.WeaponServices?.ActiveWeapon?.Value;
        if (active == null || !active.IsValid) return false;

        var weapon = new CCSWeaponBase(active.Handle);
        string name = weapon.DesignerName?.ToLowerInvariant() ?? "";
        bool matches = gtype switch
        {
            "smoke" => name == "weapon_smokegrenade",
            "flash" => name == "weapon_flashbang",
            "he" => name == "weapon_hegrenade",
            "molotov" => name is "weapon_molotov" or "weapon_incgrenade",
            _ => false,
        };
        if (!matches || weapon.Clip1 <= 0) return false;

        grenade = weapon;
        return true;
    }

    private static bool ConsumeCoachGrenade(CCSPlayerPawn pawn, string gtype)
    {
        if (!TryGetReadyCoachGrenade(pawn, gtype, out var grenade)) return false;
        grenade.Clip1--;
        Utilities.SetStateChanged(grenade, "CBasePlayerWeapon", "m_iClip1");
        return true;
    }

    // ═══════════════════════════════════════════════════════════
    //  Grenade Replay
    // ═══════════════════════════════════════════════════════════

    private void TryReplay(CCSPlayerController bot, GrenadeData g, List<CCSPlayerController> allControllers)
    {
        if (_botNadesMode == "off") return;
        // In case the bot has been taken over
        bool isTakenOver = bot.HasBeenControlledByPlayerThisRound;
        if (isTakenOver) return;

        var gtype = g.GrenadeType; // lowercase since LoadDb

        var botPawn = bot.PlayerPawn?.Value;
        if (botPawn == null || !botPawn.IsValid) return;

        // ── Round limit checks ─────────────────────────────────
        // Less mode: per-bot limits (1 smoke, 1 molotov, 1 HE,
        // ammo_grenade_limit_flashbang flashes, total <= 4 per round).
        if (_botNadesMode == "less" || _botNadesMode == "coach_hard")
        {
            if (!LessModeAllows(gtype, (uint)bot.Index)) return;
        }
        else if (_botNadesMode == "normal")
        {
            int teamNum = bot.TeamNum;
            int teamSize = allControllers
                .Count(p => p.IsValid && p.IsBot && (int)p.TeamNum == teamNum);
            if (teamSize < 1) teamSize = 1;

            if (!_roundCountByTeam.TryGetValue(teamNum, out var teamCount))
                teamCount = new RoundCounter();

            // Purchase limit: flash + he + molotov <= 3 * bot count on this side.
            // Smoke is compulsory for each bot to buy.
            if (gtype is "flash" or "he" or "molotov")
            {
                int OptionalTotal = teamCount.Flash + teamCount.HE + teamCount.Molotov;
                if (OptionalTotal >= 3 * teamSize) return;
            }

            if (gtype == "flash")
            {
                var cv  = ConVar.Find("ammo_grenade_limit_flashbang");
                int max = (cv?.GetPrimitiveValue<int>() ?? 2) * teamSize;
                if (teamCount.Flash >= max) return;
            }
            else
            {
                int used = gtype switch
                {
                    "smoke"   => teamCount.Smoke,
                    "he"      => teamCount.HE,
                    "molotov" => teamCount.Molotov,
                    _         => 99,
                };
                if (used >= teamSize) return;
            }
        }
        // The only two differences between more and normal modes are the round limit and the early smoke limit
        else if (_botNadesMode == "max" || _botNadesMode == "more")
        {
            // no limits
        }

        // ── Account check ──────────────────────────────────────────────
        var money = bot.InGameMoneyServices;
        if (money == null && _botNadesMode != "coach_hard") return;

        bool isCT     = bot.TeamNum == (int)CsTeam.CounterTerrorist;
        var costTable = isCT ? CostCT : CostT;
        if (!costTable.TryGetValue(gtype, out int cost)) return;
        if (_botNadesMode != "coach_hard" && money!.Account < cost) return;

        // ── Round spend cap check ──────────────────────────────────────
        uint botIdx   = (uint)bot.Index;
        bool isPoor   = _poorBots.Contains((uint)bot.Index);
        int  spendCap = GetRoundSpendCap(isCT, isPoor);
        if (!_roundSpendPerBot.TryGetValue(botIdx, out int alreadySpent))
            alreadySpent = 0;
        // Expensure Limit
        if (_botNadesMode != "coach_hard" && alreadySpent + cost > spendCap) return;
        bool deductMoney = _botNadesMode != "coach_hard";

        // ── All checks passed — commit ─────────────────────────────────
        if (deductMoney)
        {
            money!.Account -= cost;
            Utilities.SetStateChanged(bot, "CCSPlayerController", "m_pInGameMoneyServices");
            _roundSpendPerBot[botIdx] = alreadySpent + cost;
        }

        // coach_hard never buys, refunds, or creates inventory. It consumes a
        // grenade that Valve's economy and weapon selection already provided.
        if (_botNadesMode == "coach_hard"
            && !ConsumeCoachGrenade(botPawn, gtype)) return;
        if (_botNadesMode == "coach_hard")
            _botLastGrenadeThrowTime[botIdx] = Server.CurrentTime;

        _replayBots.Add((uint)bot.Index);
        RegisterCooldown(g.Id, gtype);
        IncrementCount(gtype, bot.TeamNum);
        // Less mode: per-bot round count
        if (_botNadesMode == "less" || _botNadesMode == "coach_hard")
            IncrementBotCount(gtype, (uint)bot.Index);
        // Normal/Less Mode early smoke limit
        if ((_botNadesMode == "normal" || _botNadesMode == "less") && gtype == "smoke"
            && _freezeEndTime > 0f && Server.CurrentTime - _freezeEndTime < 5f)
        {
            _earlySmokeCountByTeam.TryGetValue(bot.TeamNum, out int cnt);
            _earlySmokeCountByTeam[bot.TeamNum] = cnt + 1;
        }
        SpawnProjectile(bot, g);

        // Allow bot to throw another grenade after this window
        AddTimer(1f, () => _replayBots.Remove((uint)bot.Index));
    }

    private void SpawnProjectile(CCSPlayerController bot, GrenadeData g)
    {
        // ── Item definition indices (weapon_def_index) ────────────
        // The native Create() functions require the item def index.
        static ushort GetItemIndex(string t) => t switch
        {
            "smoke"   => 45,
            "flash"   => 43,
            "he"      => 44,
            _         => 45,
        };

        var gtype    = g.GrenadeType.ToLowerInvariant();
        var origin   = new Vector(g.ProjectilePosition.X,
                                  g.ProjectilePosition.Y,
                                  g.ProjectilePosition.Z);
        var velocity = new Vector(g.ProjectileVelocity.X,
                                  g.ProjectileVelocity.Y,
                                  g.ProjectileVelocity.Z);

        // Angles derived from velocity (nade model orientation only, not trajectory)
        float yaw   =  MathF.Atan2(velocity.Y, velocity.X) * (180f / MathF.PI);
        float hDist =  MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
        float pitch = -MathF.Atan2(velocity.Z, hDist)      * (180f / MathF.PI);
        var angles  =  new QAngle(pitch, yaw, 0f);

        var teamNum  = bot.TeamNum;
        var itemDef  = (int)GetItemIndex(gtype);

        Server.NextFrame(() =>
        {
            try
            {
                var botPawn = bot.PlayerPawn?.Value;
                if (botPawn == null || !botPawn.IsValid)
                {
                    Server.PrintToConsole("[NadeSystem] bot pawn invalid, skipping replay");
                    return;
                }

                // ── FLASH — CreateEntityByName is sufficient ───────────
                // No native factory needed.
                if (gtype == "flash")
                {
                    var flash = Utilities.CreateEntityByName<CFlashbangProjectile>(
                        "flashbang_projectile");
                    if (flash == null)
                    {
                        Server.PrintToConsole("[NadeSystem] flash CreateEntityByName null");
                        return;
                    }
                    flash.TeamNum             = teamNum;
                    flash.Thrower.Raw         = botPawn.EntityHandle.Raw;
                    flash.OriginalThrower.Raw = botPawn.EntityHandle.Raw;
                    flash.OwnerEntity.Raw     = botPawn.EntityHandle.Raw;
                    flash.InitialPosition.X   = origin.X;
                    flash.InitialPosition.Y   = origin.Y;
                    flash.InitialPosition.Z   = origin.Z;
                    flash.InitialVelocity.X   = velocity.X;
                    flash.InitialVelocity.Y   = velocity.Y;
                    flash.InitialVelocity.Z   = velocity.Z;
                    flash.Elasticity          = 0.33f;
                    flash.Teleport(origin, angles, velocity);
                    flash.DispatchSpawn();
                    flash.Teleport(origin, angles, velocity);
                    // Flash Immunity
                    float immuneUntil = Server.CurrentTime + 2f;
                    foreach (var teammate in Utilities
                        .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
                    {
                        if (!teammate.IsValid || !teammate.IsBot) continue;
                        if ((int)teammate.TeamNum != (int)bot.TeamNum) continue;
                        _botFlashImmunityUntil[(uint)teammate.Index] = immuneUntil;
                    }
                    Server.PrintToConsole(
                        $"[NadeSystem] Replayed [flash] id={g.Id[..8]}... " +
                        $"bot=[{bot.PlayerName}] " +
                        $"origin=({origin.X:F0},{origin.Y:F0},{origin.Z:F0}) " +
                        $"vel=({velocity.X:F1},{velocity.Y:F1},{velocity.Z:F1})");
                    return;
                }

                // ── DECOY — CreateEntityByName ─────────────────────────────
                if (gtype == "decoy")
                {
                    var decoy = Utilities.CreateEntityByName<CFlashbangProjectile>("flashbang_projectile");
                    if (decoy == null)
                    {
                        Server.PrintToConsole("[NadeSystem] decoy CreateEntityByName null");
                        return;
                    }
                    decoy.TeamNum             = teamNum;
                    decoy.Thrower.Raw         = botPawn.EntityHandle.Raw;
                    decoy.OriginalThrower.Raw = botPawn.EntityHandle.Raw;
                    decoy.OwnerEntity.Raw     = botPawn.EntityHandle.Raw;
                    decoy.InitialPosition.X   = origin.X;
                    decoy.InitialPosition.Y   = origin.Y;
                    decoy.InitialPosition.Z   = origin.Z;
                    decoy.InitialVelocity.X   = velocity.X;
                    decoy.InitialVelocity.Y   = velocity.Y;
                    decoy.InitialVelocity.Z   = velocity.Z;
                    decoy.Elasticity          = 0.33f;
                    decoy.Teleport(origin, angles, velocity);
                    decoy.DispatchSpawn();
                    decoy.Teleport(origin, angles, velocity);
                    // Don't detonate
                    StartDecoyFlashLoop(bot, g, decoy, teamNum, angles);
                    Server.PrintToConsole(
                        $"[NadeSystem] Replayed [decoy] id={g.Id[..8]}... " +
                        $"bot=[{bot.PlayerName}] " +
                        $"origin=({origin.X:F0},{origin.Y:F0},{origin.Z:F0})");
                    return;
                }

                // ── SMOKE — native CSmokeGrenadeProjectile::Create() ───
                if (gtype == "smoke")
                {
                    var smoke = _smokeCreate.Invoke(
                        origin.Handle,
                        origin.Handle,
                        velocity.Handle,
                        velocity.Handle,
                        botPawn.Handle,
                        itemDef,
                        teamNum);
                    if (smoke == null || !smoke.IsValid)
                    {
                        Server.PrintToConsole("[NadeSystem] smoke native Create returned null");
                        return;
                    }
                    smoke.TeamNum             = teamNum;
                    smoke.Thrower.Raw         = botPawn.EntityHandle.Raw;
                    smoke.OriginalThrower.Raw = botPawn.EntityHandle.Raw;
                    smoke.OwnerEntity.Raw     = botPawn.EntityHandle.Raw;
                    Server.PrintToConsole(
                        $"[NadeSystem] Replayed [smoke] id={g.Id[..8]}... " +
                        $"bot=[{bot.PlayerName}] " +
                        $"origin=({origin.X:F0},{origin.Y:F0},{origin.Z:F0}) " +
                        $"vel=({velocity.X:F1},{velocity.Y:F1},{velocity.Z:F1})");
                    return;
                }

                // ── HE — native CHEGrenadeProjectile::Create() ────────
                if (gtype == "he")
                {
                    var he = _heCreate.Invoke(
                        origin.Handle,
                        origin.Handle,
                        velocity.Handle,
                        velocity.Handle,
                        botPawn.Handle,
                        itemDef);
                    if (he == null || !he.IsValid)
                    {
                        Server.PrintToConsole("[NadeSystem] HE native Create returned null");
                        return;
                    }
                    he.TeamNum             = teamNum;
                    he.Thrower.Raw         = botPawn.EntityHandle.Raw;
                    he.OriginalThrower.Raw = botPawn.EntityHandle.Raw;
                    he.OwnerEntity.Raw     = botPawn.EntityHandle.Raw;
                    Server.PrintToConsole(
                        $"[NadeSystem] Replayed [he] id={g.Id[..8]}... " +
                        $"bot=[{bot.PlayerName}] " +
                        $"origin=({origin.X:F0},{origin.Y:F0},{origin.Z:F0}) " +
                        $"vel=({velocity.X:F1},{velocity.Y:F1},{velocity.Z:F1})");
                    return;
                }

                // ── MOLOTOV — native CMolotovProjectile::Create() ─────
                if (gtype is "molotov" or "incgrenade")
                {
                    int molotovItemDef = (teamNum == (int)CsTeam.CounterTerrorist) ? 48 : 46;
                    
                    var molotov = _molotovCreate.Invoke(
                        origin.Handle,
                        origin.Handle,
                        velocity.Handle,
                        velocity.Handle,
                        botPawn.Handle,
                        molotovItemDef);
                    if (molotov == null || !molotov.IsValid)
                    {
                        Server.PrintToConsole("[NadeSystem] molotov native Create returned null");
                        return;
                    }
                    molotov.TeamNum             = teamNum;
                    molotov.Thrower.Raw         = botPawn.EntityHandle.Raw;
                    molotov.OriginalThrower.Raw = botPawn.EntityHandle.Raw;
                    molotov.OwnerEntity.Raw     = botPawn.EntityHandle.Raw;
                    Server.PrintToConsole(
                        $"[NadeSystem] Replayed [molotov] id={g.Id[..8]}... " +
                        $"bot=[{bot.PlayerName}] " +
                        $"origin=({origin.X:F0},{origin.Y:F0},{origin.Z:F0}) " +
                        $"vel=({velocity.X:F1},{velocity.Y:F1},{velocity.Z:F1})");
                    return;
                }

                Server.PrintToConsole(
                    $"[NadeSystem] Unknown grenadeType '{g.GrenadeType}' for id {g.Id}");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[NadeSystem] SpawnProjectile error: {ex.Message}");
            }
        });
    }
    // Prevent a flashbang from detonating
    private void StartDecoyFlashLoop(CCSPlayerController bot, GrenadeData g,
        CFlashbangProjectile flash, int teamNum, QAngle angles)
    {
        AddTimer(1f, () =>
        {
            if (!flash.IsValid) return;

            // Get current position and velocity
            var curPos = flash.AbsOrigin;
            var curVel = flash.AbsVelocity;
            if (curPos == null || curVel == null) return;

            float speed = MathF.Sqrt(curVel.X*curVel.X + curVel.Y*curVel.Y + curVel.Z*curVel.Z);

            // Kill old flash
            flash.AcceptInput("Kill");

            // Stop if velocity is near zero
            if (speed < 5f) return;

            // recreate a new flash with all the current state
            var botPawn = bot.PlayerPawn?.Value;
            if (botPawn == null || !botPawn.IsValid) return;

            var newOrigin = new Vector(curPos.X, curPos.Y, curPos.Z);
            var newVel    = new Vector(curVel.X, curVel.Y, curVel.Z);

            var newFlash = Utilities.CreateEntityByName<CFlashbangProjectile>("flashbang_projectile");
            if (newFlash == null) return;

            newFlash.TeamNum             = (byte)teamNum;
            newFlash.Thrower.Raw         = botPawn.EntityHandle.Raw;
            newFlash.OriginalThrower.Raw = botPawn.EntityHandle.Raw;
            newFlash.OwnerEntity.Raw     = botPawn.EntityHandle.Raw;
            newFlash.Elasticity          = 0.33f;
            newFlash.Teleport(newOrigin, angles, newVel);
            newFlash.DispatchSpawn();
            newFlash.Teleport(newOrigin, angles, newVel);

            // Cycle
            StartDecoyFlashLoop(bot, g, newFlash, teamNum, angles);
        });
    }

    // ═══════════════════════════════════════════════════════════
    //  Cooldown helpers
    // ═══════════════════════════════════════════════════════════

    private bool IsOnCooldown(string id)
        => _cooldowns.Any(c => c.GrenadeId == id && c.ExpiresAt > Server.CurrentTime);

    private void RegisterCooldown(string id, string gtype)
    {
        _cooldowns.RemoveAll(c => c.GrenadeId == id);
        float duration = CooldownSec.TryGetValue(gtype, out float s) ? s : 10f;
        _cooldowns.Add(new CooldownEntry
        {
            GrenadeId = id,
            ExpiresAt = Server.CurrentTime + duration,
        });
    }

    private void PruneCooldowns()
    {
        float now = Server.CurrentTime;
        _cooldowns.RemoveAll(c => c.ExpiresAt <= now);
    }
    // Information System cooldown
    private bool IsOnProbFailCooldown(string id)
        => _probFailCooldown.TryGetValue(id, out float t) && t > Server.CurrentTime;

    private void RegisterProbFailCooldown(string id)
        => _probFailCooldown[id] = Server.CurrentTime + 3f;

    private static float Dist3D(float x1, float y1, float z1, float x2, float y2, float z2)
    {
        float dx = x1-x2, dy = y1-y2, dz = z1-z2;
        return MathF.Sqrt(dx*dx + dy*dy + dz*dz);
    }
    // ═══════════════════════════════════════════════════════════
    //  Round count helpers
    // ═══════════════════════════════════════════════════════════

    private void IncrementCount(string gtype, int teamNum)
    {
        if (!_roundCountByTeam.TryGetValue(teamNum, out var counter))
            counter = new RoundCounter();
        switch (gtype.ToLower())
        {
            case "flash":   counter.Flash++;   break;
            case "smoke":   counter.Smoke++;   break;
            case "he":      counter.HE++;      break;
            case "molotov": counter.Molotov++; break;
        }
        _roundCountByTeam[teamNum] = counter;
    }

    // ── Less mode: per-bot counting ─────────────────────────────
    private void IncrementBotCount(string gtype, uint botIdx)
    {
        if (gtype == "incgrenade") gtype = "molotov";
        if (!_roundCountByBot.TryGetValue(botIdx, out var counter))
            counter = new RoundCounter();
        switch (gtype.ToLower())
        {
            case "flash":   counter.Flash++;   break;
            case "smoke":   counter.Smoke++;   break;
            case "he":      counter.HE++;      break;
            case "molotov": counter.Molotov++; break;
        }
        _roundCountByBot[botIdx] = counter;
    }

    // Less mode gate: true if this bot may still throw one more of gtype
    // this round. Per bot: <= 1 smoke, <= 1 molotov, <= 1 HE,
    // <= ammo_grenade_limit_flashbang flashes, and <= 4 nades total.
    private bool LessModeAllows(string gtype, uint botIdx)
    {
        if (gtype == "incgrenade") gtype = "molotov";
        if (!_roundCountByBot.TryGetValue(botIdx, out var c))
            c = new RoundCounter();
        // Total per-round cap
        if (c.Flash + c.Smoke + c.HE + c.Molotov >= 4) return false;
        switch (gtype)
        {
            case "flash":
                var cv  = ConVar.Find("ammo_grenade_limit_flashbang");
                int max = cv?.GetPrimitiveValue<int>() ?? 2;
                return c.Flash < max;
            case "smoke":   return c.Smoke   < 1;
            case "he":      return c.HE      < 1;
            case "molotov": return c.Molotov < 1;
            default:        return false;
        }
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _roundOver  = false;
        _freezeEndTime = 0f;
        _roundCountByTeam.Clear();
        _roundCountByBot.Clear();
        _cooldowns.Clear();
        _replayBots.Clear();
        _smokeCooldownBots.Clear();
        _roundSpendPerBot.Clear();
        _defuseSmokeUsed  = false;
        _defuseFlashUsed  = false;
        _plantSmokeUsed   = false;
        _botMolotovDmgStart.Clear();
        _earlySmokeCountByTeam.Clear();
        _botInFlashZone.Clear();
        _botFlashRatioWindow.Clear();
        _botFlashImmunityUntil.Clear();
        _molotovEscapeSmokeCooldown.Clear();
        _retaliationCooldown.Clear();
        // Information System
        _soundPoints.Clear();
        _botLastFireTime.Clear();
        _botLastGrenadeThrowTime.Clear();
        foreach (var key in _probFailCooldown.Where(kv => kv.Value <= Server.CurrentTime).Select(kv => kv.Key).ToList())
            _probFailCooldown.Remove(key);
        // Save money for poor bots
        _poorBots.Clear();
        if (!IsPistolRound())
        {
            foreach (var bot in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
            {
                if (!bot.IsValid || !bot.IsBot) continue;
                // Mark bots with < 2800 as poor
                if (bot.InGameMoneyServices?.Account < 2800)
                    _poorBots.Add((uint)bot.Index);
            }
        }
        return HookResult.Continue;
    }

    private HookResult OnFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        _freezeEndTime = Server.CurrentTime;
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        _roundOver = true;
        return HookResult.Continue;
    }

    // A dead player makes no more sound, so drop their trail immediately
    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid)
            _soundPoints.Remove((uint)player.Index);
        return HookResult.Continue;
    }

    private bool IsPistolRound()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
            if (gameRules == null) return false;

            int played    = gameRules.TotalRoundsPlayed;
            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            if (maxRounds   <= 0) maxRounds   = 24;

            int half   = maxRounds / 2;

            return played == 0
                || played == half;
        }
        catch { return false; }
    }

    private int GetRoundSpendCap(bool isCT, bool isPoor)
    {
        if (IsPistolRound()) return 800;
        // Poor bots get a lower spend cap
        if (isPoor) return 500;

        var costTable = isCT ? CostCT : CostT;
        int cap = costTable["flash"]
                + costTable["smoke"]
                + costTable["he"]
                + costTable["molotov"];
        return cap;
    }
    // Don't blind ourselves and our teammates
    private HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        if (!UsesLegacyReplayFeatures) return HookResult.Continue;
        var victim   = @event.Userid;

        if (victim is null || !victim.IsValid || !victim.IsBot)
            return HookResult.Continue;
        // In case the bot has been taken over
        bool isTakenOver = victim.HasBeenControlledByPlayerThisRound;
        if (isTakenOver)
            return HookResult.Continue;

        var pawn = victim.PlayerPawn?.Value;
        if (_botFlashImmunityUntil.TryGetValue((uint)victim.Index, out float immuneUntil)
            && Server.CurrentTime <= immuneUntil)
        {
            if (pawn != null && pawn.IsValid)
            {
                @event.BlindDuration = 0f;

                ref float blindStartTime = ref pawn.BlindStartTime;
                blindStartTime = 0f;

                ref float blindUntilTime = ref pawn.BlindUntilTime;
                blindUntilTime = 0f;

                ref float flashDuration = ref pawn.FlashDuration;
                flashDuration = 0f;

                ref float flashMaxAlpha = ref pawn.FlashMaxAlpha;
                flashMaxAlpha = 0f;
            }
        }
        return HookResult.Continue;
    }
    // bot_nades convar
    private void CmdBotNades(CCSPlayerController? player, CommandInfo info)
    {
        if (info.ArgCount < 2)
        {
            Server.PrintToConsole($"[NadeSystem] bot_nades = {_botNadesMode}");
            return;
        }
        var val = info.GetArg(1).Trim().ToLowerInvariant();
        if (!SupportedModes.Contains(val))
        {
            Server.PrintToConsole("\x0C[NadeSystem]\x01 Usage: bot_nades <off|less|normal|more|max|native|coach_easy|coach_normal|coach_hard>");
            return;
        }
        _botNadesMode = val;
        Config.Mode = val;
        ApplyModeSettings();
        Server.PrintToConsole($"[NadeSystem] bot_nades set to {_botNadesMode}");
    }
    // ═══════════════════════════════════════════════════════════
    //  Normal mode/ more mode decision system
    // ═══════════════════════════════════════════════════════════

    private void TryConditionalReplay(CCSPlayerController bot, GrenadeData g,
        List<CCSPlayerController> allControllers)
    {
        var pawn = bot.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return;
        if (!PassesSituationalCheck(bot, pawn, g, g.GrenadeType, allControllers))
        {
            // Probability attempt cooldown
            if (g.GrenadeType.Equals("smoke", StringComparison.OrdinalIgnoreCase))
            {
                _smokeCooldownBots.Add((uint)bot.Index);
                AddTimer(1f, () => _smokeCooldownBots.Remove((uint)bot.Index));
            }
            return;
        }
        TryReplay(bot, g, allControllers);
    }

    private bool PassesSituationalCheck(
        CCSPlayerController bot, CCSPlayerPawn pawn, GrenadeData g, string gtype,
        List<CCSPlayerController> allControllers)
    {
        //  He / Molotov decision
        if (gtype is "he" or "molotov")
        {
            // No HE/molotov within 1s of this bot firing.
            if (FiredRecently(bot, 1f)) return false;

            float lx = g.LandingPosition.X, ly = g.LandingPosition.Y, lz = g.LandingPosition.Z;
            var nearbyEnemies = allControllers
                .Where(p =>
                {
                    if (!p.IsValid || (int)p.TeamNum == bot.TeamNum) return false;
                    var ep = GetActiveLivePawn(p)?.AbsOrigin;
                    if (ep == null) return false;
                    float dx = ep.X - lx, dy = ep.Y - ly, dz = ep.Z - lz;
                    return dx*dx + dy*dy + dz*dz <= 200f * 200f;
                })
                .ToList();
            if (nearbyEnemies.Count == 0) return false;

            // Information gate (less / normal / more mode).
            if (_botNadesMode == "normal" || _botNadesMode == "more" || _botNadesMode == "less")
            {
                bool anyInfo = nearbyEnemies.Any(e => HasInformationOn(e, bot));
                if (!anyInfo)
                {
                    // No info on any nearby enemy: roll probability.
                    float prob;
                    if (_botNadesMode == "more")
                        prob = gtype == "he" ? 0.50f : 0.80f;   // more: HE 50%, molotov 80%
                    else
                        prob = gtype == "he" ? 0.20f : 0.60f;   // normal: HE 20%, molotov 60%
                    if (Random.Shared.NextDouble() >= prob)
                    {
                        RegisterProbFailCooldown(g.Id);
                        return false;
                    }
                }
            }
            //  Don't throw molotov into smoke
            if (gtype == "molotov")
            {
                float now = Server.CurrentTime;
                foreach (var cd in _cooldowns)
                {
                    if (cd.ExpiresAt <= now) continue;
                    var smokeRecord = _mapNades.FirstOrDefault(d =>
                        d.Id == cd.GrenadeId &&
                        string.Equals(d.GrenadeType, "smoke", StringComparison.OrdinalIgnoreCase));
                    if (smokeRecord == null) continue;
                    float sx = smokeRecord.LandingPosition.X;
                    float sy = smokeRecord.LandingPosition.Y;
                    float sz = smokeRecord.LandingPosition.Z;
                    float ddx = lx - sx, ddy = ly - sy, ddz = lz - sz;
                    if (ddx*ddx + ddy*ddy + ddz*ddz < 200f * 200f) return false;
                }
            }
        }

        // Flash decision
        if (gtype == "flash")
        {
            if (!PassesTeamAndScheduleCheck(bot, g)) return false;

            // Collect enemies that can actually be blinded by this flash.
            var blindableEnemies = CanBlindAnyEnemy(bot, g, allControllers);
            if (blindableEnemies.Count == 0) return false;

            // Information gate (less / normal / more mode): if no blindable enemy has info on this bot,
            if (_botNadesMode == "normal" || _botNadesMode == "more" || _botNadesMode == "less")
            {
                bool anyInfo = blindableEnemies.Any(e => HasInformationOn(e, bot));
                if (!anyInfo)
                {
                    // No info: normal: flash 80%, more: flash 100%.
                    float prob = _botNadesMode == "more" ? 1.00f : 0.80f;
                    if (Random.Shared.NextDouble() >= prob)
                    {
                        RegisterProbFailCooldown(g.Id);
                        return false;
                    }
                }
            }
        }

        // Smoke decision
        if (gtype == "smoke")
        {
            if (!PassesTeamAndScheduleCheck(bot, g)) return false;
            float lx = g.LandingPosition.X, ly = g.LandingPosition.Y, lz = g.LandingPosition.Z;

            //  Smoke Overlap Check < 250u
            bool tooClose = _cooldowns
                .Where(c => c.ExpiresAt > Server.CurrentTime)
                .Select(c => _mapNades.FirstOrDefault(d => d.Id == c.GrenadeId))
                .Any(d => d != null
                       && string.Equals(d.GrenadeType, "smoke", StringComparison.OrdinalIgnoreCase)
                       && Dist3D(lx, ly, lz, d.LandingPosition.X, d.LandingPosition.Y, d.LandingPosition.Z) < 250f);
            if (tooClose) return false;

            // Normal/Less mode: Don't throw all your smoke right after freezeend
            if ((_botNadesMode == "normal" || _botNadesMode == "less") && _freezeEndTime > 0f && Server.CurrentTime - _freezeEndTime < 5f)
            {
                _earlySmokeCountByTeam.TryGetValue(bot.TeamNum, out int cnt);
                if (cnt >= 1) return false;
            }

            // Smoke Effective Range
            bool anyEnemyClose = allControllers
                .Any(p =>
                {
                    if (!p.IsValid || (int)p.TeamNum == bot.TeamNum) return false;
                    var ep = GetActiveLivePawn(p)?.AbsOrigin;
                    if (ep == null) return false;
                    return Dist3D(lx, ly, lz, ep.X, ep.Y, ep.Z) <= 2200f;
                });
            if (!anyEnemyClose) return false;

            // If bomb is planted and no enemy nearby, don't throw
            var bombEntity = Utilities
                .FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
                .FirstOrDefault();
            if (bombEntity != null && bombEntity.IsValid)
            {
                bool enemyNearLanding = allControllers
                    .Any(p =>
                    {
                        if (!p.IsValid || (int)p.TeamNum == bot.TeamNum) return false;
                        var ep = GetActiveLivePawn(p)?.AbsOrigin;
                        if (ep == null) return false;
                        return Dist3D(lx, ly, lz, ep.X, ep.Y, ep.Z) <= 1000f;
                    });
                if (!enemyNearLanding) return false;
            }

            // Probability
            var allAlive = allControllers
                .Where(p => p.IsValid && p.PawnIsAlive
                    && ((int)p.TeamNum == 2 || (int)p.TeamNum == 3))
                .ToList();

            int totalFriends = allAlive.Count(p => (int)p.TeamNum == bot.TeamNum);
            int totalEnemies = allAlive.Count(p => (int)p.TeamNum != bot.TeamNum);
            if (totalFriends == 0 || totalEnemies == 0) return false;

            var botPos = pawn.AbsOrigin;
            int nearbyFriend = 0, nearbyEnemy = 0;
            if (botPos != null)
            {
                foreach (var p in allAlive)
                {
                    var pp = GetActiveLivePawn(p)?.AbsOrigin;
                    if (pp == null) continue;
                    if (Dist3D(botPos.X, botPos.Y, botPos.Z, pp.X, pp.Y, pp.Z) > 800f) continue;
                    if ((int)p.TeamNum == bot.TeamNum) nearbyFriend++;
                    else nearbyEnemy++;
                }
            }

            // (nearbyFriend+yourself) / totalFriends + nearbyEnemy / totalEnemies
            float threshold = (float)nearbyFriend / totalFriends * 0.5f
                            + (float)nearbyEnemy  / totalEnemies * 0.5f;
            if (threshold < 1f && Random.Shared.NextDouble() >= threshold) return false;
        }

        return true;
    }
    // Nades that only trigger at round start
    private bool PassesTeamAndScheduleCheck(CCSPlayerController bot, GrenadeData g)
    {
        if (string.IsNullOrEmpty(g.TeamTag)) return true;

        string botTeamTag = bot.TeamNum == (int)CsTeam.CounterTerrorist ? "CT" : "T";
        if (g.TeamTag != botTeamTag) return false;

        string scheduleKey = $"{Server.MapName.ToLower()}_{g.TeamTag}";
        if (ThrowSchedule.TryGetValue(scheduleKey, out float maxSecs))
        {
            if (_freezeEndTime <= 0f) return false;
            if (Server.CurrentTime - _freezeEndTime > maxSecs) return false;
        }

        return true;
    }

    // Returns all enemies that can be blinded by this flash (FOV + LoS).
    private List<CCSPlayerController> CanBlindAnyEnemy(CCSPlayerController bot, GrenadeData g,
        List<CCSPlayerController> allControllers)
    {
        var result = new List<CCSPlayerController>();
        float lx = g.LandingPosition.X, ly = g.LandingPosition.Y, lz = g.LandingPosition.Z;
        foreach (var p in allControllers)
        {
            if (!p.IsValid || (int)p.TeamNum == bot.TeamNum) continue;
            var ep = GetActiveLivePawn(p);
            if (ep?.AbsOrigin == null || ep.EyeAngles == null) continue;

            float viewZ = 64f;
            float eyeX = ep.AbsOrigin.X, eyeY = ep.AbsOrigin.Y, eyeZ = ep.AbsOrigin.Z + viewZ;

            float dx = lx - eyeX, dy = ly - eyeY, dz = lz - eyeZ;
            float dist2 = dx*dx + dy*dy + dz*dz;
            if (dist2 > 1300f * 1300f) continue;

            float eYawRad   =  ep.EyeAngles.Y * MathF.PI / 180f;
            float ePitchRad = -ep.EyeAngles.X * MathF.PI / 180f;
            float fwdX = MathF.Cos(ePitchRad) * MathF.Cos(eYawRad);
            float fwdY = MathF.Cos(ePitchRad) * MathF.Sin(eYawRad);
            float fwdZ = MathF.Sin(ePitchRad);

            float yawToFlash   = MathF.Atan2(dy, dx);
            float eyeYaw       = MathF.Atan2(fwdY, fwdX);
            float deltaYaw     = MathF.Abs(MathF.Atan2(MathF.Sin(yawToFlash - eyeYaw),
                                                        MathF.Cos(yawToFlash - eyeYaw)));
            float pitchToFlash = MathF.Atan2(dz, MathF.Sqrt(dx*dx + dy*dy));
            float eyePitch     = MathF.Atan2(fwdZ, MathF.Sqrt(fwdX*fwdX + fwdY*fwdY));
            float deltaPitch   = MathF.Abs(pitchToFlash - eyePitch);
            if (deltaYaw <= 0.927f && deltaPitch <= MathF.PI / 4f)  // H: ±53°, V: ±45°
            {
                // Raytrace check
                if (FlashHasLoS(g.LandingPosition, eyeX, eyeY, eyeZ))
                    result.Add(p);
            }
        }
        return result;
    }

    // Returns (blindableCount, totalEnemyCount)
    private (int blindable, int total) CountBlindableEnemies(CCSPlayerController bot, GrenadeData g,
        List<CCSPlayerController> allControllers)
    {
        float lx = g.LandingPosition.X, ly = g.LandingPosition.Y, lz = g.LandingPosition.Z;
        int blindable = 0, total = 0;
        foreach (var p in allControllers)
        {
            if (!p.IsValid || (int)p.TeamNum == bot.TeamNum) continue;
            var ep = GetActiveLivePawn(p);
            if (ep == null) continue;
            total++;
            if (ep.AbsOrigin == null || ep.EyeAngles == null) continue;

            float eyeX = ep.AbsOrigin.X, eyeY = ep.AbsOrigin.Y, eyeZ = ep.AbsOrigin.Z + 64f;
            float dx = lx - eyeX, dy = ly - eyeY, dz = lz - eyeZ;
            float dist2 = dx*dx + dy*dy + dz*dz;
            if (dist2 > 1300f * 1300f) continue;

            float eYawRad   =  ep.EyeAngles.Y * MathF.PI / 180f;
            float ePitchRad = -ep.EyeAngles.X * MathF.PI / 180f;
            float fwdX = MathF.Cos(ePitchRad) * MathF.Cos(eYawRad);
            float fwdY = MathF.Cos(ePitchRad) * MathF.Sin(eYawRad);
            float fwdZ = MathF.Sin(ePitchRad);

            float yawToFlash   = MathF.Atan2(dy, dx);
            float eyeYaw       = MathF.Atan2(fwdY, fwdX);
            float deltaYaw     = MathF.Abs(MathF.Atan2(MathF.Sin(yawToFlash - eyeYaw),
                                                        MathF.Cos(yawToFlash - eyeYaw)));
            float pitchToFlash = MathF.Atan2(dz, MathF.Sqrt(dx*dx + dy*dy));
            float eyePitch     = MathF.Atan2(fwdZ, MathF.Sqrt(fwdX*fwdX + fwdY*fwdY));
            float deltaPitch   = MathF.Abs(pitchToFlash - eyePitch);
            if (deltaYaw <= 0.927f && deltaPitch <= MathF.PI / 4f && FlashHasLoS(g.LandingPosition, eyeX, eyeY, eyeZ))
                blindable++;
        }
        return (blindable, total);
    }
    // Returns true if LandingPosition has unobstructed LoS to the given eye point.
    // Uses MASK_WORLD_ONLY, ignores players/props.
    private bool FlashHasLoS(Vec3 landing, float eyeX, float eyeY, float eyeZ)
    {
        try
        {
            var rt = _rayTraceCapability.Get();
            if (rt == null) // If raytrace interface is not loaded, return true
            {
                Server.PrintToConsole("[NadeSystem] FlashHasLoS: RayTrace not loaded, skipping");
                return true;
            }

            var start = new Vector(landing.X, landing.Y, landing.Z);
            var end   = new Vector(eyeX, eyeY, eyeZ);

            var opts = new TraceOptions(InteractionLayers.MASK_WORLD_ONLY);
            rt.TraceEndShape(start, end, null, opts, out TraceResult res);

            // fraction >= 0.99 → enemy can see the flash
            return res.Fraction >= 0.99f;
        }
        catch
        {
            return true;
        }
    }
    // Post-throw probability for flash for this bot in 12 seconds
    private float GetFlashRatioThreshold(int blindable, int total)
    {
        if (total == 0) return 0f;
        // 1/1, 2/2, 3/3, 4/4, 5/5, 4/5 → 100%
        if (blindable == total) return 1f;
        if (blindable == 4 && total == 5) return 1f;
        if (blindable == 3 && total == 4) return 0.9f;
        if (blindable == 2 && total == 3) return 0.8f;
        if (blindable == 3 && total == 5) return 0.7f;
        if (blindable == 2 && total == 4) return 0.6f;
        if (blindable == 1 && total == 2) return 0.6f;
        if (blindable == 2 && total == 5) return 0.5f;
        if (blindable == 1 && total == 3) return 0.3f;
        if (blindable == 1 && total == 4) return 0.2f;
        if (blindable == 1 && total == 5) return 0.1f;
        return 0f;
    }
    // ═══════════════════════════════════════════════════════════
    //  Special Nades
    // ═══════════════════════════════════════════════════════════
    // Defuse smoke/flash
    private void TrySpawnInstantGrenade(CCSPlayerController bot, Vector spawnPos, string gtype, Vector? velocity = null)
    {
        if (!UsesLegacyReplayFeatures) return;
        if (_botNadesMode == "off") return;
        // In case the bot has been taken over
        bool isTakenOver = bot.HasBeenControlledByPlayerThisRound;
        if (isTakenOver) return;
        bool hasLiveEnemy = Utilities
            .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Any(p => p.IsValid && p.PawnIsAlive
                && ((int)p.TeamNum == 2 || (int)p.TeamNum == 3)
                && (int)p.TeamNum != bot.TeamNum);
        if (!hasLiveEnemy) return;

        var money = bot.InGameMoneyServices;
        if (money == null) return;

        bool isCT     = bot.TeamNum == (int)CsTeam.CounterTerrorist;
        var costTable = isCT ? CostCT : CostT;
        if (!costTable.TryGetValue(gtype, out int cost)) return;
        if (money.Account < cost) return;

        uint botIdx  = (uint)bot.Index;
        bool isPoor   = _poorBots.Contains((uint)bot.Index);
        int  spendCap = GetRoundSpendCap(isCT, isPoor);
        if (!_roundSpendPerBot.TryGetValue(botIdx, out int alreadySpent))
            alreadySpent = 0;
        if (alreadySpent + cost > spendCap) return;
        money.Account -= cost;
        Utilities.SetStateChanged(bot, "CCSPlayerController", "m_pInGameMoneyServices");
        _roundSpendPerBot[botIdx] = alreadySpent + cost;

        var vel = velocity ?? new Vector(0f, 0f, 0f);
        Server.NextFrame(() =>
        {
            try
            {
                var botPawn = bot.PlayerPawn?.Value;
                if (botPawn == null || !botPawn.IsValid) return;

                int teamNum = bot.TeamNum;

                if (gtype == "smoke")
                {
                    var smoke = _smokeCreate.Invoke(
                        spawnPos.Handle, spawnPos.Handle,
                        vel.Handle, vel.Handle,
                        botPawn.Handle, 45, teamNum);
                    if (smoke == null || !smoke.IsValid) return;
                    smoke.TeamNum             = (byte)teamNum;
                    smoke.Thrower.Raw         = botPawn.EntityHandle.Raw;
                    smoke.OriginalThrower.Raw = botPawn.EntityHandle.Raw;
                    smoke.OwnerEntity.Raw     = botPawn.EntityHandle.Raw;
                }
                else if (gtype == "flash")
                {
                    var flash = Utilities.CreateEntityByName<CFlashbangProjectile>(
                        "flashbang_projectile");
                    if (flash == null) return;
                    flash.TeamNum             = (byte)teamNum;
                    flash.Thrower.Raw         = botPawn.EntityHandle.Raw;
                    flash.OriginalThrower.Raw = botPawn.EntityHandle.Raw;
                    flash.OwnerEntity.Raw     = botPawn.EntityHandle.Raw;
                    flash.InitialPosition.X   = spawnPos.X;
                    flash.InitialPosition.Y   = spawnPos.Y;
                    flash.InitialPosition.Z   = spawnPos.Z;
                    flash.InitialVelocity.X   = vel.X;
                    flash.InitialVelocity.Y   = vel.Y;
                    flash.InitialVelocity.Z   = vel.Z;
                    flash.Elasticity          = 0.33f;
                    var ang = new QAngle(-90f, 0f, 0f);
                    flash.Teleport(spawnPos, ang, vel);
                    flash.DispatchSpawn();
                    flash.Teleport(spawnPos, ang, vel);
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[NadeSystem] TrySpawnInstantGrenade error: {ex.Message}");
            }
        });
    }

    private HookResult OnBombBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
    {
        RecordSoundPoint(@event.Userid);
        if (!UsesLegacyReplayFeatures) return HookResult.Continue;

        var bot = @event.Userid;
        if (bot == null || !bot.IsValid || !bot.IsBot) return HookResult.Continue;
        if (bot.HasBeenControlledByPlayerThisRound) return HookResult.Continue;

        var pawn = bot.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid || !bot.PawnIsAlive)
            return HookResult.Continue;

        var pos = pawn.AbsOrigin;
        if (pos == null) return HookResult.Continue;
        var spawnPos = new Vector(pos.X, pos.Y, pos.Z + 5f);

        // Defuse smoke
        if (!_defuseSmokeUsed)
        {
            bool hasDefuser = false;
            if (pawn.ItemServices != null
                && pawn.ItemServices.Handle != nint.Zero)
            {
                hasDefuser = new CCSPlayer_ItemServices(pawn.ItemServices.Handle).HasDefuser;
            }

            if (hasDefuser || Random.Shared.NextDouble() < 0.33)
            {
                _defuseSmokeUsed = true;
                TrySpawnInstantGrenade(bot, spawnPos, "smoke");
            }
        }

        // Defuse flash
        if (!_defuseFlashUsed)
        {
            if (Random.Shared.NextDouble() < 0.20)
            {
                _defuseFlashUsed = true;
                // Don't flash yourself
                _botFlashImmunityUntil[(uint)bot.Index] = Server.CurrentTime + 2f;
                var flashVel = new Vector(0f, 0f, -800f);
                TrySpawnInstantGrenade(bot, spawnPos, "flash", flashVel);
            }
        }

        return HookResult.Continue;
    }

    // Plant smoke
    private HookResult OnBombBeginPlant(EventBombBeginplant @event, GameEventInfo info)
    {
        RecordSoundPoint(@event.Userid);
        if (!UsesLegacyReplayFeatures) return HookResult.Continue;

        if (_plantSmokeUsed) return HookResult.Continue;

        var bot = @event.Userid;
        if (bot == null || !bot.IsValid || !bot.IsBot) return HookResult.Continue;
        if (bot.HasBeenControlledByPlayerThisRound) return HookResult.Continue;

        if (Random.Shared.NextDouble() >= 0.33) return HookResult.Continue;

        var pawn = bot.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid || !bot.PawnIsAlive)
            return HookResult.Continue;

        var pos = pawn.AbsOrigin;
        if (pos == null) return HookResult.Continue;

        _plantSmokeUsed = true;
        TrySpawnInstantGrenade(bot, new Vector(pos.X, pos.Y, pos.Z + 5f), "smoke");
        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        HandleMolotovEscape(@event);
        HandleRetaliationHE(@event);

        return HookResult.Continue;
    }
    // Put out the fire
    private void HandleMolotovEscape(EventPlayerHurt @event)
    {
        if (!UsesLegacyReplayFeatures || _botNadesMode == "off") return;
        var victim = @event.Userid;
        if (victim == null || !victim.IsValid || !victim.IsBot) return;
        if (victim.HasBeenControlledByPlayerThisRound) return;

        var pawn = victim.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid || !victim.PawnIsAlive) return;

        string weapon = @event.Weapon ?? "";
        bool isMolotovDmg = weapon.Contains("inferno", StringComparison.OrdinalIgnoreCase)
                         || weapon.Contains("molotov", StringComparison.OrdinalIgnoreCase)
                         || weapon.Contains("incgrenade", StringComparison.OrdinalIgnoreCase);
        if (!isMolotovDmg)
        {
            _botMolotovDmgStart.Remove((uint)victim.Index);
            return;
        }

        int teamNum = victim.TeamNum;
        if (_molotovEscapeSmokeCooldown.TryGetValue(teamNum, out float expiry)
            && Server.CurrentTime < expiry) return;

        uint idx = (uint)victim.Index;
        float now = Server.CurrentTime;

        if (!_botMolotovDmgStart.TryGetValue(idx, out float start))
        {
            _botMolotovDmgStart[idx] = now;
            return;
        }

        if (now - start < 0.3f) return;

        _botMolotovDmgStart.Remove(idx);
        _molotovEscapeSmokeCooldown[teamNum] = now + 20f;

        var pos = pawn.AbsOrigin;
        if (pos == null) return;
        TrySpawnInstantGrenade(victim, new Vector(pos.X, pos.Y, pos.Z + 5f), "smoke");
    }
    // Revenge grenade
    private void HandleRetaliationHE(EventPlayerHurt @event)
    {
        if (!UsesLegacyReplayFeatures || _botNadesMode == "off") return;
        var victim = @event.Userid;
        if (victim == null || !victim.IsValid || !victim.IsBot) return;
        if (victim.HasBeenControlledByPlayerThisRound) return;

        var victimPawn = victim.PlayerPawn?.Value;
        if (victimPawn == null || !victimPawn.IsValid || !victim.PawnIsAlive) return;

        var attacker = @event.Attacker;
        if (attacker == null || !attacker.IsValid || attacker.IsBot || !attacker.PawnIsAlive) return;

        if (attacker.TeamNum == victim.TeamNum) return;   // only retaliate against the enemies
        if (_roundOver) return;

        // No retaliation HE/molotov within 1s of victim firing
        if (FiredRecently(victim, 1f)) return;

        string weapon = @event.Weapon ?? "";
        bool isHE      = weapon.Contains("hegrenade",  StringComparison.OrdinalIgnoreCase);
        bool isMolotov = weapon.Contains("molotov",    StringComparison.OrdinalIgnoreCase)
                    || weapon.Contains("incgrenade",  StringComparison.OrdinalIgnoreCase)
                    || weapon.Contains("inferno",     StringComparison.OrdinalIgnoreCase);
        if (!isHE && !isMolotov) return;

        var atkPos = GetActiveLivePawn(attacker)?.AbsOrigin;
        if (atkPos == null) return;

        string map = Server.MapName;
        int teamNum = victim.TeamNum;
        // less / normal / more mode: retaliation cooldown per team (7s)
        if (_botNadesMode == "normal" || _botNadesMode == "more" || _botNadesMode == "less")
        {
            if (_retaliationCooldown.TryGetValue(teamNum, out float cdExpiry)
                && Server.CurrentTime < cdExpiry) return;
        }
        // less / normal / more mode: limit total he+molotov spawned per hurt event
        int retaliationLimit = int.MaxValue;
        if (_botNadesMode == "normal" || _botNadesMode == "more" || _botNadesMode == "less")
        {
            var vPos = victimPawn.AbsOrigin;
            int aliveTeamSize = Utilities
                .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
                .Count(p =>
                {
                    if (!p.IsValid || (int)p.TeamNum != victim.TeamNum) return false;
                    if (vPos == null) return false;
                    var pp = GetActiveLivePawn(p)?.AbsOrigin;
                    if (pp == null) return false;
                    return Dist3D(vPos.X, vPos.Y, vPos.Z, pp.X, pp.Y, pp.Z) <= 800f;
                });
            retaliationLimit = aliveTeamSize < 1 ? 1 : aliveTeamSize;
        }
        int retaliationSpawned = 0;

        // Build candidate list (single pass: filter then sort)
        // primary  : satisfies both direction and distance check  -> first
        // secondary: nearest projectilePosition to victim         -> ascending
        var vPosForSort = victimPawn.AbsOrigin;
        var candidates = _mapNades
            .Where(g =>
            {
                string gt = g.GrenadeType; // lowercase since LoadDb
                if (gt != "he" && gt != "molotov" && gt != "incgrenade") return false;
                float d = Dist3D(atkPos.X, atkPos.Y, atkPos.Z,
                                 g.LandingPosition.X, g.LandingPosition.Y, g.LandingPosition.Z);
                if (d > 200f) return false;
                if (IsOnCooldown(g.Id)) return false;
                return true;
            })
            .OrderByDescending(g => FacesThrowDirection(victimPawn, g) ? 1 : 0)
            .ThenBy(g => vPosForSort == null ? 0f :
                Dist3D(vPosForSort.X, vPosForSort.Y, vPosForSort.Z,
                       g.ProjectilePosition.X, g.ProjectilePosition.Y, g.ProjectilePosition.Z))
            .ToList();

        // Loop-invariant purchase context; GetRoundSpendCap walks the entity
        // table for gamerules, so resolve it once instead of per candidate.
        var money = victim.InGameMoneyServices;
        if (money == null) return;
        bool isCT     = victim.TeamNum == (int)CsTeam.CounterTerrorist;
        var costTable = isCT ? CostCT : CostT;
        uint botIdx   = (uint)victim.Index;
        bool isPoor   = _poorBots.Contains(botIdx);
        int  spendCap = GetRoundSpendCap(isCT, isPoor);

        foreach (var g in candidates)
        {
            if (retaliationSpawned >= retaliationLimit) break;

            string gt = g.GrenadeType; // lowercase since LoadDb

            if (!costTable.TryGetValue(gt, out int cost)) continue;
            if (money.Account < cost) continue;
            // Less mode: enforce per-bot round limits (counts retaliation nades).
            if (_botNadesMode == "less" && !LessModeAllows(gt, botIdx)) continue;

            if (!_roundSpendPerBot.TryGetValue(botIdx, out int alreadySpent)) alreadySpent = 0;
            if (alreadySpent + cost > spendCap) continue;
            money.Account -= cost;
            Utilities.SetStateChanged(victim, "CCSPlayerController", "m_pInGameMoneyServices");
            _roundSpendPerBot[botIdx] = alreadySpent + cost;

            RegisterCooldown(g.Id, gt);
            SpawnProjectile(victim, g);
            // Normal mode: counts retaliation nades toward per-team round limit.
            if (_botNadesMode == "normal")
                IncrementCount(gt, victim.TeamNum);
            // Less mode: counts retaliation nades toward per-bot round limit.
            else if (_botNadesMode == "less")
                IncrementBotCount(gt, botIdx);
            retaliationSpawned++;
        }
        // Write cooldown after retaliation completes (less / normal / more)
        if ((_botNadesMode == "normal" || _botNadesMode == "more" || _botNadesMode == "less") && retaliationSpawned > 0)
            _retaliationCooldown[teamNum] = Server.CurrentTime + 7f;
    }
    // ═══════════════════════════════════════════════════════════
    //  Tick
    // ═══════════════════════════════════════════════════════════

    private void OnTick()
    {
        _tick++;
        UpdateSoundTrails(_tick % 4 == 0);
        if (_tick % 4   == 0) CheckBotZones();
        if (_tick % 256 == 0) PruneCooldowns();
    }
}

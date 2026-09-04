using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NadeSystem;

// ═══════════════════════════════════════════════════════════════
//  Plugin
// ═══════════════════════════════════════════════════════════════

public partial class NadeSystemPlugin : BasePlugin
{
    public override string ModuleName    => "NadeSystem";
    public override string ModuleVersion => "1.2.1";
    public override string ModuleAuthor  => "ed0ard & XBribo";

    // grenades folder lives inside the plugin directory
    private string DataDir => Path.Combine(ModuleDirectory, "grenades");
    // precache all the nades on this map
    private List<GrenadeData> _mapNades = new();
    // Static trigger-zone index rebuilt whenever the active map data is loaded
    private const float GrenadeZoneGridSize = 256f;
    private Dictionary<(int X, int Y), List<GrenadeData>> _grenadeZoneGrid = new();
    private string _botNadesMode = "normal"; // "off" | "less" | "normal" | "more" | "max"
    // ── State ──────────────────────────────────────────────────
    private List<GrenadeData>     _db                = new();
    private List<CooldownEntry>   _cooldowns         = new();
    private HashSet<uint>         _replayBots        = new();
    private HashSet<uint>         _smokeCooldownBots = new();
    private int                   _tick              = 0;
    private bool                  _roundOver         = false;
    private float                 _freezeEndTime     = 0f;
    private Dictionary<uint, int> _roundSpendPerBot  = new();
    private Dictionary<uint, int> _roundNadeMoneyPerBot = new();
    private HashSet<uint>         _poorBots          = new();
    // Information System
    private Dictionary<string, float> _probFailCooldown = new();
    // flash immunity
    private Dictionary<uint, float> _botFlashImmunityUntil = new();
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
    // ── Information system (sound events + vision) ─────────────
    // key = controller index, value = latest Valve player_sound state
    private Dictionary<uint, PlayerSoundState> _playerSounds = new();
    // key = controller index, value = last weapon_fire time (global, all players)
    private Dictionary<uint, float> _botLastFireTime = new();
    // Value-only state avoids retaining native Vector wrappers between callbacks
    private readonly record struct PlayerSoundState(
        float X,
        float Y,
        float Z,
        float RadiusSquared,
        float ExpiresAt);
    // Shared result prevents duplicate flash target ray traces in one decision
    private readonly record struct FlashTargetEvaluation(
        List<CCSPlayerController> BlindableEnemies,
        int TotalEnemies);
    // Current CS2 grenade throw events fade to silence at this distance
    private const float GrenadeThrowSoundRange = 1100f;
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

    // Character definition overrides for agents that use a non-default voice profile
    private static readonly Dictionary<ushort, string> AgentVoiceProfiles = new()
    {
        [4613] = "professional_epic",
        [4711] = "swat_epic",
        [4712] = "swat_fem",
        [4726] = "professional_epic",
        [4727] = "professional_fem",
        [4730] = "professional_fem",
        [4733] = "professional_epic",
        [4734] = "professional_epic",
        [4735] = "professional_epic",
        [4736] = "professional_epic",
        [4749] = "gendarmerie_male",
        [4750] = "gendarmerie_male",
        [4751] = "gendarmerie_fem_epic",
        [4752] = "gendarmerie_male",
        [4753] = "gendarmerie_male",
        [4756] = "swat_fem",
        [4757] = "seal_fem",
        [4771] = "seal_diver_01",
        [4772] = "seal_diver_02",
        [4773] = "jungle_male",
        [4774] = "jungle_male_epic",
        [4775] = "jungle_male",
        [4776] = "jungle_male",
        [4777] = "jungle_fem_epic",
        [4778] = "jungle_fem",
        [4780] = "jungle_male_epic",
        [4781] = "jungle_fem",
        [5108] = "leet_epic",
        [5308] = "fbihrt_epic",
        [5400] = "gsg9",
        [5404] = "seal_epic",
        [5504] = "balkan_epic",
    };

    // Playable grenade voice events extracted from the current CS2 response rules
    private static readonly Dictionary<
        string,
        (string He, string Flash, string Smoke, string Molotov, string Incendiary, string Decoy)>
        GrenadeVoiceEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["balkan"] = (
            "balkan.t_grenade01", "balkan.t_flashbang01", "balkan.t_smoke01",
            "balkan.t_molotov01", "balkan.t_molotov01", "balkan.t_decoy01"),
        ["balkan_epic"] = (
            "balkan_epic.throwing_grenade_01", "balkan_epic.throwing_flashbang_01",
            "balkan_epic.throwing_smoke_01", "balkan_epic.throwing_molotov_01",
            "balkan_epic.throwing_molotov_01", "balkan_epic.throwing_decoy_01"),
        ["fbihrt"] = (
            "fbihrt.ct_grenade01", "fbihrt.ct_flashbang01", "fbihrt.ct_smoke01",
            "fbihrt.ct_molotov02", "fbihrt.ct_molotov02", "fbihrt.ct_decoy01"),
        ["fbihrt_epic"] = (
            "fbihrt_epic.throwing_grenade_01", "fbihrt_epic.throwing_flashbang_01",
            "fbihrt_epic.throwing_smoke_01", "fbihrt_epic.throwing_molotov_01",
            "fbihrt_epic.throwing_fire_01", "fbihrt_epic.throwing_decoy_01"),
        ["gendarmerie_fem"] = (
            "gendarmerie_fem.ff1_throwing_grenade_01",
            "gendarmerie_fem.ff1_throwing_flashbang_01",
            "gendarmerie_fem.ff1_throwing_smoke_01",
            "gendarmerie_fem.ff1_throwing_molotov_01",
            "gendarmerie_fem.ff1_throwing_molotov_01",
            "gendarmerie_fem.ff1_throwing_decoy_01"),
        ["gendarmerie_fem_epic"] = (
            "gendarmerie_fem_epic.ff2_throwing_grenade_01",
            "gendarmerie_fem_epic.ff2_throwing_flashbang_01",
            "gendarmerie_fem_epic.ff2_throwing_smoke_01",
            "gendarmerie_fem_epic.ff2_throwing_molotov_01",
            "gendarmerie_fem_epic.ff2_throwing_fire_01",
            "gendarmerie_fem_epic.ff2_throwing_decoy_01"),
        ["gendarmerie_male"] = (
            "gendarmerie_male.fm1_throwing_grenade_01",
            "gendarmerie_male.fm1_throwing_flashbang_01",
            "gendarmerie_male.fm1_throwing_smoke_01",
            "gendarmerie_male.fm1_throwing_molotov_01",
            "gendarmerie_male.fm1_throwing_molotov_01",
            "gendarmerie_male.fm1_throwing_decoy_01"),
        ["gsg9"] = (
            "gsg9.ct_grenade01", "gsg9.ct_flashbang01", "gsg9.ct_smoke01",
            "gsg9.ct_molotov01", "gsg9.ct_molotov01", "gsg9.ct_decoy01"),
        ["jungle_fem"] = (
            "jungle_fem.aff1_throwing_grenade_01",
            "jungle_fem.aff1_throwing_flashbang_01",
            "jungle_fem.aff1_throwing_smoke_01",
            "jungle_fem.aff1_throwing_molotov_01",
            "jungle_fem.aff1_throwing_molotov_01",
            "jungle_fem.aff1_throwing_decoy_01"),
        ["jungle_fem_epic"] = (
            "jungle_fem_epic.aff1_throwing_grenade_01",
            "jungle_fem_epic.aff1_throwing_flashbang_01",
            "jungle_fem_epic.aff1_throwing_smoke_01",
            "jungle_fem_epic.aff1_throwing_molotov_01",
            "jungle_fem_epic.aff1_throwing_molotov_01",
            "jungle_fem_epic.aff1_throwing_decoy_01"),
        ["jungle_male"] = (
            "jungle_male.afm1_throwing_grenade_01",
            "jungle_male.afm1_throwing_flashbang_01",
            "jungle_male.afm1_throwing_smoke_01",
            "jungle_male.afm1_throwing_molotov_01",
            "jungle_male.afm1_throwing_molotov_01",
            "jungle_male.afm1_throwing_decoy_01"),
        ["jungle_male_epic"] = (
            "jungle_male_epic.afm2_throwing_grenade_01",
            "jungle_male_epic.afm2_throwing_flashbang_01",
            "jungle_male_epic.afm2_throwing_smoke_01",
            "jungle_male_epic.afm2_throwing_molotov_01",
            "jungle_male_epic.afm2_throwing_molotov_01",
            "jungle_male_epic.afm2_throwing_decoy_01"),
        ["leet"] = (
            "leet.t_grenade01", "leet.t_flashbang01", "leet.t_smoke01",
            "leet.t_molotov01", "leet.t_molotov01", "leet.t_decoy01"),
        ["leet_epic"] = (
            "leet_epic.throwing_grenade_01", "leet_epic.throwing_flashbang_01",
            "leet_epic.throwing_smoke_01", "leet_epic.throwing_molotov_01",
            "leet_epic.throwing_molotov_01", "leet_epic.throwing_decoy_01"),
        ["phoenix"] = (
            "phoenix.t_grenade02", "phoenix.t_flashbang01", "phoenix.t_smoke01",
            "phoenix.t_molotov01", "phoenix.t_molotov01", "phoenix.t_decoy01"),
        ["professional"] = (
            "professional.t_grenade01", "professional.t_flashbang01",
            "professional.t_smoke01", "professional.t_molotov01",
            "professional.t_molotov01", "professional.t_decoy01"),
        ["professional_epic"] = (
            "professional_epic.throwing_grenade_01",
            "professional_epic.throwing_flashbang_01",
            "professional_epic.throwing_smoke_01",
            "professional_epic.throwing_molotov_01",
            "professional_epic.throwing_molotov_01",
            "professional_epic.throwing_decoy_01"),
        ["professional_fem"] = (
            "professional_fem.throwing_grenade_02",
            "professional_fem.throwing_flashbang_01",
            "professional_fem.throwing_smoke_01",
            "professional_fem.throwing_molotov_01",
            "professional_fem.throwing_molotov_01",
            "professional_fem.throwing_decoy_02"),
        ["sas"] = (
            "sas.ct_grenade01", "sas.ct_flashbang01", "sas.ct_smoke01",
            "sas.ct_molotov01", "sas.ct_molotov01", "sas.ct_decoy01"),
        ["seal"] = (
            "seal.ct_grenade01", "seal.ct_flashbang01", "seal.ct_smoke01",
            "seal.ct_molotov01", "seal.ct_molotov01", "seal.ct_decoy01"),
        ["seal_diver_01"] = (
            "seal_diver_01.am1_throwing_grenade_01",
            "seal_diver_01.am1_throwing_flashbang_01",
            "seal_diver_01.am1_throwing_smoke_01",
            "seal_diver_01.am1_throwing_molotov_01",
            "seal_diver_01.am1_throwing_molotov_01",
            "seal_diver_01.am1_throwing_decoy_01"),
        ["seal_diver_02"] = (
            "seal_diver_02.am1_throwing_grenade_01",
            "seal_diver_02.am1_throwing_flashbang_01",
            "seal_diver_02.am1_throwing_smoke_01",
            "seal_diver_02.am1_throwing_molotov_01",
            "seal_diver_02.am1_throwing_molotov_01",
            "seal_diver_02.am1_throwing_decoy_01"),
        ["seal_epic"] = (
            "seal_epic.throwing_grenade_01", "seal_epic.throwing_flashbang_01",
            "seal_epic.throwing_smoke_01", "seal_epic.throwing_molotov_01",
            "seal_epic.throwing_fire_01", "seal_epic.throwing_decoy_01"),
        ["seal_fem"] = (
            "seal_fem.af1_throwing_grenade_01",
            "seal_fem.af1_throwing_flashbang_03",
            "seal_fem.af1_throwing_smoke_01",
            "seal_fem.af1_throwing_molotov_01",
            "seal_fem.af1_throwing_molotov_01",
            "seal_fem.af1_throwing_decoy_01"),
        ["swat"] = (
            "swat.ct_grenade01", "swat.ct_flashbang01", "swat.ct_smoke01",
            "swat.ct_molotov01", "swat.ct_molotov01", "swat.ct_decoy01"),
        ["swat_epic"] = (
            "swat_epic.throwing_grenade_01", "swat_epic.throwing_flashbang_01",
            "swat_epic.throwing_smoke_01", "swat_epic.throwing_molotov_01",
            "swat_epic.throwing_molotov_01", "swat_epic.throwing_decoy_01"),
        ["swat_fem"] = (
            "swat_fem.throwing_grenade_02", "swat_fem.throwing_flashbang_01",
            "swat_fem.throwing_smoke_01", "swat_fem.throwing_molotov_01",
            "swat_fem.throwing_molotov_01", "swat_fem.throwing_decoy_01"),
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

}

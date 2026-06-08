using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Core.Capabilities;
using RayTraceAPI;
using Microsoft.Extensions.Logging;

namespace BotAimImprover;

// ============================================================
// Config (configs/plugins/BotAimImprover/BotAimImprover.json)
// Pick a difficulty Preset; any non-null field in Overrides wins over it.
// ============================================================
public class BotAimConfig : BasePluginConfig
{
    public override int Version { get; set; } = 1;

    // "low" | "medium" | "high"
    public string Preset { get; set; } = "medium";

    // When true, the preset is auto-synced to the server's active bot difficulty
    // (overrides/botprofile.vpk matched against Low/Medium/High) on load and every
    // map start. `Preset` above is the fallback if detection fails. A manual
    // `bot_aim_preset <low|medium|high>` pins it (turns auto off until `bot_aim_preset auto`).
    public bool AutoDetectDifficulty { get; set; } = true;

    public BotAimOverrides Overrides { get; set; } = new();
}

public class BotAimOverrides
{
    // World-unit aim error (added to m_targetSpot). Settled = BASE; just-spotted
    // adds DECAY, fading over TAU seconds.
    public float? BaseErrMin { get; set; }
    public float? BaseErrMax { get; set; }
    public float? DecayErrMin { get; set; }
    public float? DecayErrMax { get; set; }
    public float? TauMin { get; set; }
    public float? TauMax { get; set; }
    public float? VertErrScale { get; set; }

    // Fraction of bots that aim head/jaw (the headshot-rate dial).
    public float? HighAimFraction { get; set; }

    // Reaction lag: aim at where the enemy was ReactMs ago, led by LeadK.
    public float? ReactMsMin { get; set; }
    public float? ReactMsMax { get; set; }
    public float? LeadKMin { get; set; }
    public float? LeadKMax { get; set; }
    public float? AccelKMin { get; set; }
    public float? AccelKMax { get; set; }
    public bool?  LagEnabled { get; set; }

    // Global multipliers / misc.
    public float? ErrorScale { get; set; }
    public float? PartRepickInterval { get; set; }
}

[MinimumApiVersion(305)]
public class BotAimImprover : BasePlugin, IPluginConfig<BotAimConfig>
{
    public override string ModuleName => "BotAimImprover";
    public override string ModuleVersion => "3.0.0";
    public override string ModuleAuthor => "ed0ard & htfy96";
    public override string ModuleDescription => "Unified smart bot aim: per-bot skill, dwell-decaying error, sticky parts, reaction lag.";

    public BotAimConfig Config { get; set; } = new();

    // ============================================================
    // Platform-specific memory layout. Verified offsets:
    //   Linux  libserver.so 2026-05-28 (PickNewAimSpot @ 0xb57170 + consumer 0xb4a650)
    //   Windows server.dll  2026-05-19 (from the original BotAimImprover v2.0.2)
    // Fields set to 0 are unknown on that platform and handled gracefully.
    // ============================================================
    private readonly struct Offsets
    {
        public readonly int TargetSpot;   // Vector(3) - base aim point (the bot shoots here)
        public readonly int TsVel;        // Vector(3) - m_targetSpotVelocity (engine extrapolates spot by this each frame)
        public readonly int TsTime;       // float     - m_targetSpotTime (curtime the spot was set; fTimeSinceAimSpot = now - this)
        public readonly int Enemy;        // CHandle
        public readonly int IsVisible;    // bool
        public readonly int PBot;         // CCSPlayerPawn->m_pBot (controller mapping; best-effort)
        public readonly int BotEye;       // CCSBot cached shoot origin (0 = use controller eye)
        public readonly int AimErrX, AimErrY, AimErrZ, AimError; // native aim-error (0 = don't touch)
        public readonly string Sig;
        public Offsets(int ts, int tsVel, int tsTime, int en, int vis, int pbot, int eye,
                       int ex, int ey, int ez, int aerr, string sig)
        { TargetSpot = ts; TsVel = tsVel; TsTime = tsTime; Enemy = en; IsVisible = vis; PBot = pbot; BotEye = eye;
          AimErrX = ex; AimErrY = ey; AimErrZ = ez; AimError = aerr; Sig = sig; }
    }

    private static readonly Offsets LINUX = new(
        ts: 0x597C, tsVel: 0x5988, tsTime: 0x59B8,   // all verified vs libserver.so 2026-05-28 (PickNewAimSpot writes)
        en: 0x59E8, vis: 0x59EC, pbot: 0x1568, eye: 0x100,
        ex: 0x59A0, ey: 0x59A4, ez: 0x59A8, aerr: 0x59BC,
        sig: "55 48 89 E5 41 55 41 54 53 48 89 FB 48 83 EC 58 8B 8F E8 59 00 00 83 F9 FF");

    private static readonly Offsets WINDOWS = new(
        ts: 0x59A4, tsVel: 0x59B0, tsTime: 0x59E0,   // all verified vs server.dll 2026-06-02 (PickNewAimSpot writes)
        en: 0x5A10, vis: 0x5A14, pbot: 0x1298, eye: 0x108 /*use controller*/,
        ex: 0x59C8, ey: 0x59CC, ez: 0x59D0, aerr: 0x59E4 /*neutralize native error*/,
        sig: "48 8B C4 55 57 48 8D 68 A1 48 81 EC A8 00 00 00 48 8B F9 0F 29 70 D8 8B 89 10 5A 00 00 83 F9 FF");

    private Offsets _off;

    // ============================================================
    // Derived aim points (enemy local frame). Index is the part id.
    // ============================================================
    private readonly struct AimPoint
    {
        public readonly string Name;
        public readonly float Frac;
        public readonly float Lateral;
        public readonly bool  FeetAbs;
        public AimPoint(string n, float f, float lat, bool feetAbs = false)
        { Name = n; Frac = f; Lateral = lat; FeetAbs = feetAbs; }
    }

    private static readonly AimPoint[] _aimPoints =
    {
        new("HEAD",           1.00f,  0f),   // 0
        new("NECK",           0.97f,  0f),   // 1
        new("JAW",            0.92f,  0f),   // 2
        new("CHEST",          0.82f,  0f),   // 3
        new("GUT",            0.67f,  0f),   // 4
        new("PELVIS",         0.60f,  0f),   // 5
        new("LEFT_CHEST",     0.82f, -8f),   // 6
        new("RIGHT_CHEST",    0.82f,  8f),   // 7
        new("LEFT_SHOULDER",  0.92f, -8f),   // 8
        new("RIGHT_SHOULDER", 0.92f,  8f),   // 9
        new("LEFT_GUT",       0.67f, -7f),   // 10
        new("RIGHT_GUT",      0.67f,  7f),   // 11
        new("LEFT_THIGH",     0.38f, -5f),   // 12
        new("RIGHT_THIGH",    0.38f,  5f),   // 13
        new("LEFT_SHIN",      0.15f, -5f),   // 14
        new("RIGHT_SHIN",     0.15f,  5f),   // 15
        new("FEET",           5.0f,   0f, true), // 16
    };

    private static readonly int[] _priorityHead =
        { 0, 1, 2, 3, 4, 5, 6, 7, 10, 11, 8, 9, 12, 13, 14, 15, 16 };
    private static readonly int[] _priorityJaw =
        { 2, 1, 0, 3, 4, 5, 6, 7, 10, 11, 8, 9, 12, 13, 14, 15, 16 };
    // Body order leads with GUT/PELVIS (low) so recoil climb tops out at the chest.
    private static readonly int[] _priorityBody =
        { 4, 5, 10, 11, 3, 6, 7, 8, 9, 2, 1, 0, 12, 13, 14, 15, 16 };

    // ============================================================
    // Resolved tuning (preset merged with overrides). Live-editable.
    // ============================================================
    private struct Tuning
    {
        public float BaseErrMin, BaseErrMax, DecayErrMin, DecayErrMax, TauMin, TauMax, VertErrScale;
        public float HighAimFraction;
        public float ReactMsMin, ReactMsMax, LeadKMin, LeadKMax;
        public float AccelKMin, AccelKMax;   // accel-prediction lead time (s); 0 = no accel prediction
        public float ErrorScale, PartRepickInterval;
        public bool  LagEnabled;
    }
    private Tuning _t;

    private static Tuning PresetFor(string name) => name.Trim().ToLowerInvariant() switch
    {
        "low" => new Tuning   // easy: loose, body-aiming, slow + low-lead reactions
        {
            BaseErrMin = 4.5f, BaseErrMax = 10f, DecayErrMin = 11f, DecayErrMax = 24f,
            TauMin = 0.45f, TauMax = 1.00f, VertErrScale = 0.80f, HighAimFraction = 0.12f,
            ReactMsMin = 200f, ReactMsMax = 320f, LeadKMin = 0.35f, LeadKMax = 0.65f,
            AccelKMin = 0.00f, AccelKMax = 0.00f,   // easy bots don't anticipate accel - get juked
            ErrorScale = 1f, PartRepickInterval = 0.85f, LagEnabled = true,
        },
        "high" => new Tuning  // hard: tight, head-prone, fast + near-full-lead reactions
        {
            BaseErrMin = 1.5f, BaseErrMax = 3.5f, DecayErrMin = 4f, DecayErrMax = 9f,
            TauMin = 0.28f, TauMax = 0.65f, VertErrScale = 0.80f, HighAimFraction = 0.55f,
            ReactMsMin = 90f, ReactMsMax = 180f, LeadKMin = 0.80f, LeadKMax = 1.00f,
            AccelKMin = 0.09f, AccelKMax = 0.14f,   // hard bots lead into strafe accel
            ErrorScale = 1f, PartRepickInterval = 0.60f, LagEnabled = true,
        },
        _ => new Tuning       // medium (default)
        {
            BaseErrMin = 2.5f, BaseErrMax = 6f, DecayErrMin = 6f, DecayErrMax = 15f,
            TauMin = 0.40f, TauMax = 0.90f, VertErrScale = 0.80f, HighAimFraction = 0.25f,
            ReactMsMin = 140f, ReactMsMax = 260f, LeadKMin = 0.55f, LeadKMax = 0.85f,
            AccelKMin = 0.04f, AccelKMax = 0.08f,   // some accel anticipation
            ErrorScale = 1f, PartRepickInterval = 0.75f, LagEnabled = true,
        },
    };

    private MemoryFunctionVoid<IntPtr>? _pickNewAimSpot;
    private static readonly PluginCapability<CRayTraceInterface> _rayTraceCapability =
        new("raytrace:craytraceinterface");

    private enum AimMode { MIXED, HEAD, BODY }
    private AimMode _aimMode = AimMode.MIXED;

    // weapon_awp is handled by the "always body" check in OrderFor; these only body-aim in MIXED mode.
    private static readonly HashSet<string> _bodyFirstWeapons = new()
    {
        "weapon_ssg08", "weapon_p90", "weapon_bizon",
        "weapon_nova", "weapon_xm1014", "weapon_sawedoff", "weapon_mag7", "weapon_revolver"
    };

    private enum AimBias { HEAD, JAW, BODY }

    private sealed class BotState
    {
        public Random Rng = new();
        public float   BaseErr, DecayErr, Tau, ReactionMs, LeadK, AccelK;
        public AimBias Bias;
        public float VisibleSince = -1f;
        public int   LastEnemyIdx = -1;
        public int   CurrentPart  = -1;
        public float PartChosenAt = -1f;
        public string? Weapon;            // resolved on part re-pick

        // Smoothed-error drift: a standard-normal offset that wanders (OU process)
        // instead of teleporting to a fresh random point every pick.
        public float OffX, OffY, OffZ;
        public float DriftT = -1f;        // last drift-update time

        // Cached bot eye (refreshed on each part re-pick) for angular error scaling.
        public float EyeX, EyeY, EyeZ;
        public bool  HasEye;
    }

    private readonly ConcurrentDictionary<IntPtr, BotState> _botState = new();
    private int _biasEpoch = 0;   // reshuffle seed for the per-round balanced bias bag

    // Per-entity position history (reaction lag). Keyed by pawn entity index.
    private struct Sample { public float T, PX, PY, PZ, VX, VY, VZ, Yaw, EyeZ; }

    private sealed class History
    {
        public const int CAP = 48;
        public readonly Sample[] Buf = new Sample[CAP];
        public int Count = 0, Head = 0;

        public void Push(in Sample s)
        {
            Buf[Head] = s;
            Head = (Head + 1) % CAP;
            if (Count < CAP) Count++;
        }

        // Most recent sample at or before targetT; oldest if none older. -1 if empty.
        public int IndexAt(float targetT)
        {
            int best = -1;
            for (int i = 0; i < Count; i++)
            {
                int idx = (Head - 1 - i + CAP) % CAP;
                best = idx;
                if (Buf[idx].T <= targetT) return idx;
            }
            return best;
        }
    }

    private readonly Dictionary<int, History> _history = new();
    private readonly bool[] _visBuf = new bool[_aimPoints.Length];  // reusable visibility mask

    // Diagnostics.
    private long   _hookCalls = 0, _writes = 0, _gateBail = 0, _botResolveFail = 0;
    private string _lastInfo = "(none yet)";
    private long   _botKills = 0, _botHsKills = 0;
    private string _detectedPreset = "(n/a)"; // last auto-detection result, for status
    private bool   _configApplied;            // OnConfigParsed/ApplyConfig has run at least once

    // ============================================================
    // Config lifecycle
    // ============================================================
    public void OnConfigParsed(BotAimConfig config)
    {
        Config = config;
        ApplyConfig();
    }

    private static void Set(ref float dst, float? v) { if (v.HasValue) dst = v.Value; }

    private void ApplyConfig()
    {
        var t = PresetFor(Config.Preset);
        var o = Config.Overrides ?? new BotAimOverrides();
        Set(ref t.BaseErrMin,  o.BaseErrMin);
        Set(ref t.BaseErrMax,  o.BaseErrMax);
        Set(ref t.DecayErrMin, o.DecayErrMin);
        Set(ref t.DecayErrMax, o.DecayErrMax);
        Set(ref t.TauMin,      o.TauMin);
        Set(ref t.TauMax,      o.TauMax);
        Set(ref t.VertErrScale, o.VertErrScale);
        Set(ref t.ReactMsMin,  o.ReactMsMin);
        Set(ref t.ReactMsMax,  o.ReactMsMax);
        Set(ref t.LeadKMin,    o.LeadKMin);
        Set(ref t.LeadKMax,    o.LeadKMax);
        Set(ref t.AccelKMin,   o.AccelKMin);
        Set(ref t.AccelKMax,   o.AccelKMax);
        Set(ref t.PartRepickInterval, o.PartRepickInterval);
        if (o.HighAimFraction.HasValue) t.HighAimFraction = Math.Clamp(o.HighAimFraction.Value, 0f, 1f);
        if (o.ErrorScale.HasValue)      t.ErrorScale      = Math.Clamp(o.ErrorScale.Value, 0f, 10f);
        if (o.LagEnabled.HasValue)      t.LagEnabled      = o.LagEnabled.Value;
        _t = t;
        _configApplied = true;
        _botState.Clear(); // re-roll personalities with new tuning
    }

    // ============================================================
    // Auto difficulty detection
    // Mirrors RoundDamageRecap: the active overrides/botprofile.vpk is SHA-256'd
    // and matched against the Low/Medium/High reference profiles. Pure filesystem,
    // no native offsets, so it is safe on both platforms.
    // ============================================================
    private void MaybeAutoApplyPreset(string ctx)
    {
        if (!Config.AutoDetectDifficulty) return;
        string? detected = DetectPresetFromProfile();
        _detectedPreset = detected ?? "unknown";
        if (detected == null)
        {
            Logger.LogInformation("[BotAimImprover] auto-difficulty: undetected ({Ctx}); keeping preset={Preset}", ctx, Config.Preset);
            return;
        }
        if (!string.Equals(detected, Config.Preset, StringComparison.OrdinalIgnoreCase))
        {
            Config.Preset = detected;
            ApplyConfig();
            Logger.LogInformation("[BotAimImprover] auto-difficulty -> preset={Preset} ({Ctx})", detected, ctx);
            Server.PrintToConsole($"[BotAimImprover] auto-difficulty: preset -> {detected} ({ctx})");
        }
    }

    private string? DetectPresetFromProfile()
    {
        try
        {
            string? overridesDir = FindOverridesDirectory();
            if (overridesDir == null) return null;

            string activePath = Path.Combine(overridesDir, "botprofile.vpk");
            if (!File.Exists(activePath)) return null;
            byte[] activeHash = ComputeSha256(activePath);

            foreach (var (preset, folder) in new[] { ("low", "Low"), ("medium", "Medium"), ("high", "High") })
            {
                string p = Path.Combine(overridesDir, folder, "botprofile.vpk");
                if (!File.Exists(p)) continue;
                if (CryptographicOperations.FixedTimeEquals(activeHash, ComputeSha256(p)))
                    return preset;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[BotAimImprover] auto-difficulty detection failed");
        }
        return null;
    }

    private static string? FindOverridesDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(Server.GameDirectory, "overrides"),
            Path.Combine(Server.GameDirectory, "csgo", "overrides"),
            Path.Combine(Server.GameDirectory, "game", "csgo", "overrides"),
        };
        foreach (var c in candidates)
            if (File.Exists(Path.Combine(c, "botprofile.vpk"))) return c;

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var c = Path.Combine(current.FullName, "overrides");
            if (File.Exists(Path.Combine(c, "botprofile.vpk"))) return c;
            current = current.Parent;
        }
        return null;
    }

    private static byte[] ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    // ============================================================
    // Plugin lifecycle
    // ============================================================
    public override void Load(bool hotReload)
    {
        bool win = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        _off = win ? WINDOWS : LINUX;
        if (!_configApplied) ApplyConfig(); // apply preset+overrides if OnConfigParsed hasn't run

        try
        {
            _pickNewAimSpot = new MemoryFunctionVoid<IntPtr>(_off.Sig);
            if (_pickNewAimSpot.Handle.ToInt64() == 0)
                throw new InvalidOperationException("PickNewAimSpot signature resolved to zero address.");

            _pickNewAimSpot.Hook(OnPickNewAimSpotPost, HookMode.Post);
            Logger.LogInformation("[BotAimImprover] Loaded ({Plat}). PickNewAimSpot=0x{Pna:X}, preset={Preset}",
                win ? "Windows" : "Linux", _pickNewAimSpot.Handle.ToInt64(), Config.Preset);
            Server.PrintToConsole($"[BotAimImprover] HOOK BOUND ({(win ? "Windows" : "Linux")}). " +
                $"PickNewAimSpot=0x{_pickNewAimSpot.Handle.ToInt64():X} preset={Config.Preset}");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BotAimImprover] Fatal error during Load() (signature broken?). Plugin inactive.");
            return;
        }

        RegisterEventHandler<EventPlayerDeath>((ev, _) =>
        {
            try
            {
                var atk = ev.Attacker;
                if (atk != null && atk.IsValid && atk.IsBot)
                {
                    _botKills++;
                    if (ev.Headshot) _botHsKills++;
                }
            }
            catch { }
            return HookResult.Continue;
        });

        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            foreach (var st in _botState.Values)
            {
                st.VisibleSince = -1f; st.LastEnemyIdx = -1; st.CurrentPart = -1; st.PartChosenAt = -1f;
            }
            AssignBalancedBiases();   // fresh, team-balanced aim priority each round
            _history.Clear();
            return HookResult.Continue;
        });

        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnMapStart>(_ => MaybeAutoApplyPreset("map start"));
        RegisterCommands();

        // Sync to the server's active bot difficulty now (GameDirectory is valid here).
        MaybeAutoApplyPreset("load");
    }

    public override void Unload(bool hotReload)
    {
        try { _pickNewAimSpot?.Unhook(OnPickNewAimSpotPost, HookMode.Post); }
        catch { }
    }

    private void RegisterCommands()
    {
        AddCommand("bot_aim", "Set bot aim mode: head, body, mixed", (caller, info) =>
        {
            string arg = info.ArgCount > 1 ? info.GetArg(1).Trim().ToLowerInvariant() : "";
            string reply = arg switch
            {
                "head"  => Set(AimMode.HEAD,  "HEAD (all bots head-first)"),
                "body"  => Set(AimMode.BODY,  "BODY (all bots body-first)"),
                "mixed" => Set(AimMode.MIXED, "MIXED (per-bot bias, default)"),
                _       => $"[BotAimImprover] Current aim mode: {_aimMode}. Valid: head, body, mixed",
            };
            Server.PrintToConsole(reply);
            string Set(AimMode m, string desc) { _aimMode = m; return $"[BotAimImprover] aim mode -> {desc}"; }
        });

        AddCommand("bot_aim_preset", "Difficulty preset: low, medium, high, auto", (caller, info) =>
        {
            if (info.ArgCount > 1)
            {
                string p = info.GetArg(1).Trim().ToLowerInvariant();
                if (p == "auto")
                {
                    Config.AutoDetectDifficulty = true;
                    MaybeAutoApplyPreset("manual auto");
                    Server.PrintToConsole($"[BotAimImprover] auto-difficulty ON (preset={Config.Preset}, detected={_detectedPreset})");
                }
                else if (p is "low" or "medium" or "high")
                {
                    Config.AutoDetectDifficulty = false; // manual pin wins until `bot_aim_preset auto`
                    Config.Preset = p;
                    ApplyConfig();
                    Server.PrintToConsole($"[BotAimImprover] preset -> {p} (auto-difficulty OFF, personalities re-rolled)");
                }
                else Server.PrintToConsole("[BotAimImprover] valid presets: low, medium, high, auto");
            }
            else Server.PrintToConsole($"[BotAimImprover] preset is {Config.Preset} (auto={(Config.AutoDetectDifficulty ? "ON" : "OFF")}, detected={_detectedPreset}). Usage: bot_aim_preset <low|medium|high|auto>");
        });

        AddCommand("bot_aim_error", "Global error multiplier (0=perfect, 1=default)", (caller, info) =>
        {
            if (info.ArgCount > 1 && float.TryParse(info.GetArg(1).Trim(), out float s))
            {
                _t.ErrorScale = Math.Clamp(s, 0f, 10f);
                Server.PrintToConsole($"[BotAimImprover] error scale -> {_t.ErrorScale:0.00}");
            }
            else Server.PrintToConsole($"[BotAimImprover] error scale is {_t.ErrorScale:0.00}. Usage: bot_aim_error <0..10>");
        });

        AddCommand("bot_headshot_bias", "Fraction of bots that aim high/head (0-100%)", (caller, info) =>
        {
            if (info.ArgCount > 1 && int.TryParse(info.GetArg(1).Trim(), out int pct))
            {
                _t.HighAimFraction = Math.Clamp(pct, 0, 100) / 100f;
                _botState.Clear();
                AssignBalancedBiases();
                Server.PrintToConsole($"[BotAimImprover] high-aim fraction -> {_t.HighAimFraction:P0} (re-rolled)");
            }
            else Server.PrintToConsole($"[BotAimImprover] high-aim fraction is {_t.HighAimFraction:P0}. Usage: bot_headshot_bias <0-100>");
        });

        AddCommand("bot_aim_lag", "Toggle reaction lag + lead (0/1)", (caller, info) =>
        {
            if (info.ArgCount > 1 && int.TryParse(info.GetArg(1).Trim(), out int v))
            {
                _t.LagEnabled = v != 0;
                Server.PrintToConsole($"[BotAimImprover] reaction lag -> {(_t.LagEnabled ? "ON" : "OFF")}");
            }
            else Server.PrintToConsole($"[BotAimImprover] reaction lag is {(_t.LagEnabled ? "ON" : "OFF")}. Usage: bot_aim_lag <0/1>");
        });

        AddCommand("bot_aim_status", "Show runtime diagnostics", (caller, info) =>
        {
            long addr = _pickNewAimSpot?.Handle.ToInt64() ?? 0;
            string plat = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux";
            Server.PrintToConsole(
                $"[BotAimImprover] {plat} sig=0x{addr:X} bound={(addr != 0)} preset={Config.Preset} " +
                $"auto={(Config.AutoDetectDifficulty ? "ON" : "OFF")}(detected={_detectedPreset}) | " +
                $"calls={_hookCalls} writes={_writes} gateBail={_gateBail} ctrlFail={_botResolveFail} | " +
                $"bots={_botState.Count} hist={_history.Count} mode={_aimMode} errScale={_t.ErrorScale:0.00} " +
                $"highAim={_t.HighAimFraction:P0} lag={(_t.LagEnabled ? "ON" : "OFF")}");
            float hsRate = _botKills > 0 ? 100f * _botHsKills / _botKills : 0f;
            Server.PrintToConsole($"[BotAimImprover] MEASURED bot headshot rate: {_botHsKills}/{_botKills} = {hsRate:0.0}%");
            Server.PrintToConsole($"[BotAimImprover] last write: {_lastInfo}");
        });

        AddCommand("bot_aim_reset_stats", "Reset measured headshot counters", (caller, info) =>
        {
            _botKills = 0; _botHsKills = 0;
            Server.PrintToConsole("[BotAimImprover] headshot counters reset.");
        });
    }

    // ============================================================
    // Core: Post-hook on PickNewAimSpot.
    // ============================================================
    private HookResult OnPickNewAimSpotPost(DynamicHook hook)
    {
        try
        {
            IntPtr pCCSBot = hook.GetParam<IntPtr>(0);
            if (pCCSBot == IntPtr.Zero) return HookResult.Continue;

            BotState st = _botState.GetOrAdd(pCCSBot, CreateState);
            _hookCalls++;

            float now = Server.CurrentTime;

            bool visible = ReadByte(pCCSBot + _off.IsVisible) != 0;
            int enemyRaw = ReadInt32(pCCSBot + _off.Enemy);
            if (!visible || enemyRaw == -1)
            {
                // Bias is assigned per round in AssignBalancedBiases (team-balanced), so a
                // bot's head/body tendency varies round to round without being re-rolled
                // mid-life (which would desync the two teams).
                st.VisibleSince = -1f; st.CurrentPart = -1; _gateBail++;
                return HookResult.Continue;
            }

            int enemyIdx = enemyRaw & 0x7FFF;
            if (enemyIdx <= 0 || enemyIdx >= 4096) return HookResult.Continue;

            CCSPlayerPawn? enemyPawn = Utilities.GetEntityFromIndex<CCSPlayerPawn>(enemyIdx);
            if (enemyPawn == null || !enemyPawn.IsValid || enemyPawn.Handle == IntPtr.Zero)
                return HookResult.Continue;

            if (st.VisibleSince < 0f || st.LastEnemyIdx != enemyIdx)
            {
                st.VisibleSince = now; st.LastEnemyIdx = enemyIdx;
                st.CurrentPart = -1; st.PartChosenAt = -1f;
            }
            float dwell = MathF.Max(0f, now - st.VisibleSince);

            PushHistory(enemyIdx, enemyPawn, now);

            // (a) Sticky part selection. Controller + weapon are only needed here,
            // so resolve them lazily on re-pick instead of every hook call.
            if (st.CurrentPart < 0 || (now - st.PartChosenAt) >= _t.PartRepickInterval)
            {
                var botController = ResolveBotController(pCCSBot);
                if (botController == null) _botResolveFail++;
                st.Weapon = botController?.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value?.DesignerName;

                int[] order = OrderFor(st);
                int chosen = -1;
                if (TryGetEye(pCCSBot, botController, out var eye))
                {
                    st.EyeX = eye.X; st.EyeY = eye.Y; st.EyeZ = eye.Z; st.HasEye = true;
                    ComputeVisiblePoints(eye, enemyPawn, _visBuf);
                    chosen = PickBestPoint(_visBuf, order);

                    // Lateral-jitter guard: if the part we're already aiming at is still
                    // visible, don't swap to a same-height left/right/centre variant just
                    // because it ranks higher in `order` - that only teleports the aim
                    // sideways across re-picks (LEFT_GUT<->RIGHT_GUT, side<->centre). Allow
                    // a switch only when the new part is a genuine vertical re-target
                    // (different height, e.g. gut->head), which keeps the headshot upgrade.
                    if (chosen >= 0 && st.CurrentPart >= 0 && st.CurrentPart < _visBuf.Length
                        && _visBuf[st.CurrentPart]
                        && _aimPoints[chosen].Frac == _aimPoints[st.CurrentPart].Frac)
                    {
                        chosen = st.CurrentPart;
                    }
                }
                if (chosen < 0) chosen = order[0];
                st.CurrentPart = chosen; st.PartChosenAt = now;
            }

            // (b) Prediction. Rather than baking a one-shot lead into m_targetSpot, we feed the
            // engine a base point + velocity + timestamp and let its own per-frame extrapolator
            // (m_targetSpotPredicted = m_targetSpot + (now - m_targetSpotTime)*m_targetSpotVelocity)
            // track the target every frame. That kills the stale-velocity lag AND lets us inject a
            // (lagged) acceleration term the engine itself lacks. Base/velocity sampled from history.
            float tx = 0, ty = 0, tz = 0;          // base part position (at baseTime)
            float vx = 0, vy = 0, vz = 0;          // predicted velocity the engine integrates
            float baseTime = now;
            bool lagged = false, havePoint = false;
            if (_t.LagEnabled && TryPredict(enemyIdx, st, now, st.CurrentPart,
                                            out tx, out ty, out tz, out vx, out vy, out vz, out baseTime))
                havePoint = lagged = true;
            else if (TryComputePartPos(enemyPawn, st.CurrentPart, out tx, out ty, out tz))
            {
                var lv = enemyPawn.AbsVelocity;     // no history: anchor at now, lead with live velocity
                vx = (lv?.X ?? 0f) * st.LeadK; vy = (lv?.Y ?? 0f) * st.LeadK; vz = (lv?.Z ?? 0f) * st.LeadK;
                baseTime = now; havePoint = true;
            }

            if (havePoint)
            {
                // (c) Angular error: the dwell-decaying magnitude is an angle, so the
                // world-space radius scales with distance. Constant world units would
                // make bots laser at range and miss up close (inverted from humans).
                float baseRadius = (st.BaseErr + st.DecayErr * MathF.Exp(-dwell / st.Tau)) * _t.ErrorScale;
                float dist = ErrRefDist;
                if (st.HasEye)
                {
                    float dx = tx - st.EyeX, dy = ty - st.EyeY, dz = tz - st.EyeZ;
                    dist = MathF.Max(1f, MathF.Sqrt(dx * dx + dy * dy + dz * dz));
                }
                float radius = baseRadius * (dist / ErrRefDist);

                // (d) Smoothed drift: an Ornstein-Uhlenbeck process keeps a standard-
                // normal offset that wanders continuously instead of teleporting to a
                // fresh random point every pick. e^(-dt/tau) keeps unit stationary
                // variance for any dt, so the per-axis sigma is just `radius * 0.5`
                // (matches the old uniform-disc per-axis spread, plus Gaussian fliers).
                float dt = st.DriftT < 0f ? DriftTau : Math.Clamp(now - st.DriftT, 0f, 0.5f);
                st.DriftT = now;
                float a = MathF.Exp(-dt / DriftTau);
                float b = MathF.Sqrt(MathF.Max(0f, 1f - a * a));
                st.OffX = a * st.OffX + b * Gauss(st.Rng);
                st.OffY = a * st.OffY + b * Gauss(st.Rng);
                st.OffZ = a * st.OffZ + b * Gauss(st.Rng);
                float sigma = radius * 0.5f;
                float ox = st.OffX * sigma;
                float oy = st.OffY * sigma;
                float oz = st.OffZ * sigma * _t.VertErrScale;

                unsafe
                {
                    float* d = (float*)(pCCSBot + _off.TargetSpot).ToPointer();
                    float px = d[0], py = d[1], pz = d[2];
                    d[0] = tx + ox; d[1] = ty + oy; d[2] = tz + oz;

                    // Feed the engine's per-frame extrapolator: the velocity it integrates and
                    // the timestamp the base point corresponds to (lagged into the past by the
                    // reaction time, so fTimeSinceAimSpot = now - baseTime leads it back to ~live).
                    if (_off.TsVel != 0)
                    {
                        float* v = (float*)(pCCSBot + _off.TsVel).ToPointer();
                        v[0] = vx; v[1] = vy; v[2] = vz;
                    }
                    if (_off.TsTime != 0) WriteFloat(pCCSBot + _off.TsTime, baseTime);

                    if (_off.AimErrX != 0)   // neutralize native aim-error where known (Linux)
                    {
                        WriteFloat(pCCSBot + _off.AimErrX, 0f);
                        WriteFloat(pCCSBot + _off.AimErrY, 0f);
                        WriteFloat(pCCSBot + _off.AimErrZ, 0f);
                        if (_off.AimError != 0) WriteFloat(pCCSBot + _off.AimError, 0f);
                    }
                    _writes++;
                    float spd = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
                    _lastInfo =
                        $"part={_aimPoints[st.CurrentPart].Name} dwell={dwell:0.00}s err={radius:0.0} " +
                        $"react={st.ReactionMs:0}ms leadK={st.LeadK:0.00} accelK={st.AccelK:0.00} predV={spd:0} " +
                        $"lagged={lagged} wpn={st.Weapon ?? "?"} native=({px:0},{py:0},{pz:0}) -> ours=({d[0]:0},{d[1]:0},{d[2]:0})";
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BotAimImprover] Exception in PostHook");
        }
        return HookResult.Continue;
    }

    private BotState CreateState(IntPtr pCCSBot)
    {
        var rng = new Random(pCCSBot.GetHashCode());
        AimBias bias = RollBias(rng);   // drawn first to preserve the RNG ordering of the other traits
        return new BotState
        {
            Rng      = rng,
            BaseErr  = Lerp(_t.BaseErrMin,  _t.BaseErrMax,  (float)rng.NextDouble()),
            DecayErr = Lerp(_t.DecayErrMin, _t.DecayErrMax, (float)rng.NextDouble()),
            Tau      = MathF.Max(0.05f, Lerp(_t.TauMin, _t.TauMax, (float)rng.NextDouble())),
            ReactionMs = Lerp(_t.ReactMsMin, _t.ReactMsMax, (float)rng.NextDouble()),
            LeadK      = Lerp(_t.LeadKMin,   _t.LeadKMax,   (float)rng.NextDouble()),
            AccelK     = Lerp(_t.AccelKMin,  _t.AccelKMax,  (float)rng.NextDouble()),
            Bias     = bias,
            // Seed the drift offset so the first shot already carries error.
            OffX = Gauss(rng), OffY = Gauss(rng), OffZ = Gauss(rng),
        };
    }

    // Roll a fresh head/jaw/body aim-priority bias from the current HighAimFraction.
    private AimBias RollBias(Random rng)
    {
        double roll = rng.NextDouble();
        float headCut = _t.HighAimFraction * 0.4f;
        return roll < headCut            ? AimBias.HEAD
             : roll < _t.HighAimFraction ? AimBias.JAW
             : AimBias.BODY;
    }

    // Assign head/jaw/body biases so both teams get the *same* mix and the head/jaw
    // count matches HighAimFraction (instead of each bot rolling independently, which
    // - on a 5-bot team - clusters by chance and makes one side much deadlier).
    //
    // A single shuffled "bag" of biases is built once and dealt to each team's bots in
    // a stable order, so rank k on team T and rank k on team CT get the identical bias.
    // The bag is reshuffled each call (round start / bias change), so the distribution
    // is randomized round to round without ever being lopsided between the teams.
    private void AssignBalancedBiases()
    {
        var byTeam = new Dictionary<int, List<(uint key, BotState st)>>();
        foreach (var ctrl in Utilities.GetPlayers())
        {
            if (ctrl == null || !ctrl.IsValid || !ctrl.IsBot) continue;
            var pawn = ctrl.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid || pawn.Handle == IntPtr.Zero) continue;
            int team = ctrl.TeamNum;
            if (team != 2 && team != 3) continue;   // T / CT only
            IntPtr pBot;
            try { pBot = ReadIntPtr(pawn.Handle + _off.PBot); } catch { continue; }
            if (pBot == IntPtr.Zero) continue;
            var st = _botState.GetOrAdd(pBot, CreateState);
            if (!byTeam.TryGetValue(team, out var list)) { list = new(); byTeam[team] = list; }
            list.Add((ctrl.Index, st));   // Index = stable per-bot ordering key
        }
        if (byTeam.Count == 0) return;

        int n = 0;
        foreach (var list in byTeam.Values) n = Math.Max(n, list.Count);
        if (n == 0) return;

        AimBias[] bag = BuildBiasBag(n, _biasEpoch++);
        foreach (var list in byTeam.Values)
        {
            list.Sort((a, b) => a.key.CompareTo(b.key));
            for (int i = 0; i < list.Count; i++) list[i].st.Bias = bag[i];
        }
    }

    // Build a length-n bag whose HEAD/JAW counts track HighAimFraction (head share is
    // HighAimFraction*0.4, matching RollBias), then Fisher-Yates shuffle it with a
    // per-round seed so which ranks aim high is randomized.
    private AimBias[] BuildBiasBag(int n, int seed)
    {
        float f = _t.HighAimFraction;
        int high = (int)MathF.Round(n * f);
        int head = (int)MathF.Round(n * f * 0.4f);
        if (head > high) head = high;
        int jaw = high - head;

        var bag = new AimBias[n];
        int idx = 0;
        for (int i = 0; i < head; i++) bag[idx++] = AimBias.HEAD;
        for (int i = 0; i < jaw;  i++) bag[idx++] = AimBias.JAW;
        for (; idx < n; idx++)        bag[idx]   = AimBias.BODY;

        var rng = new Random(seed);
        for (int i = n - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (bag[i], bag[j]) = (bag[j], bag[i]);
        }
        return bag;
    }

    private int[] OrderFor(BotState st)
    {
        if (st.Weapon == "weapon_awp") return _priorityBody;   // AWP always aims at the body, regardless of mode
        if (_aimMode == AimMode.HEAD) return _priorityHead;
        if (_aimMode == AimMode.BODY) return _priorityBody;
        if (st.Weapon != null && _bodyFirstWeapons.Contains(st.Weapon)) return _priorityBody;
        return st.Bias switch
        {
            AimBias.HEAD => _priorityHead,
            AimBias.JAW  => _priorityJaw,
            _            => _priorityBody,
        };
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // Reference distance at which the error knobs equal their nominal world units;
    // closer/farther scales the world radius so the *angular* spread stays constant.
    private const float ErrRefDist = 512f;
    // Time constant of the aim wander (seconds); smaller = twitchier, larger = floatier.
    private const float DriftTau = 0.30f;

    // Acceleration prediction: estimate accel from a velocity slope sampled `AccelLagS`
    // BEFORE the velocity sample (humans notice a change in speed later than speed itself),
    // over an `AccelDtS` window, clamped to `AccelMaxVel` per-axis so a noisy estimate can't
    // fling the aim. accelLag intentionally trails the (already reaction-lagged) velocity.
    private const float AccelLagS  = 0.05f;   // 50 ms further back than the velocity sample
    private const float AccelDtS   = 0.08f;   // finite-difference window for the slope
    private const float AccelMaxVel = 400f;   // u/s cap on the accel-derived velocity bump

    // Standard-normal sample (Box-Muller).
    private static float Gauss(Random r)
    {
        double u1 = 1.0 - r.NextDouble();   // in (0,1], avoids log(0)
        double u2 = r.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    // Eye: from CCSBot memory if that offset is known (Linux), else controller.
    private bool TryGetEye(IntPtr pCCSBot, CCSPlayerController? bot, out Vector eye)
    {
        if (_off.BotEye != 0) { eye = ReadVec3(pCCSBot + _off.BotEye); return true; }
        if (bot != null) return TryGetBotEyePosition(bot, out eye);
        eye = new Vector(0, 0, 0);
        return false;
    }

    private CCSPlayerController? ResolveBotController(IntPtr pCCSBot)
    {
        foreach (var ctrl in Utilities.GetPlayers())
        {
            if (ctrl == null || !ctrl.IsValid || !ctrl.IsBot) continue;
            var pawn = ctrl.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid || pawn.Handle == IntPtr.Zero) continue;
            IntPtr pBotPtr;
            try { pBotPtr = ReadIntPtr(pawn.Handle + _off.PBot); } catch { continue; }
            if (pBotPtr == pCCSBot) return ctrl;
        }
        return null;
    }

    private static int PickBestPoint(bool[] visibleMask, int[] order)
    {
        foreach (int idx in order) if (visibleMask[idx]) return idx;
        return -1;
    }

    // Fills `mask[i]` = part i visible from botEye. Reads the enemy's origin/yaw/eyeZ
    // once (they're identical across all parts) instead of per part.
    private void ComputeVisiblePoints(Vector botEye, CCSPlayerPawn enemyPawn, bool[] mask)
    {
        Array.Clear(mask, 0, mask.Length);
        var origin = enemyPawn.AbsOrigin;
        if (origin == null) return;
        float ox = origin.X, oy = origin.Y, oz = origin.Z;
        float eyeZ = enemyPawn.ViewOffset?.Z ?? 64.0f;
        float yaw  = enemyPawn.EyeAngles?.Y ?? 0.0f;
        for (int i = 0; i < _aimPoints.Length; i++)
            if (ComputePartPosCore(ox, oy, oz, yaw, eyeZ, i, out float x, out float y, out float z)
                && PointVisibleFromEye(botEye, x, y, z))
                mask[i] = true;
    }

    private static bool TryGetBotEyePosition(CCSPlayerController bot, out Vector eye)
    {
        eye = new Vector(0, 0, 0);
        var pawn = bot.PlayerPawn?.Value;
        var origin = pawn?.AbsOrigin;
        if (origin == null) return false;
        eye = new Vector(origin.X, origin.Y, origin.Z + (pawn!.ViewOffset?.Z ?? 64.0f));
        return true;
    }

    private static bool TryComputePartPos(CCSPlayerPawn enemyPawn, int idx,
                                          out float x, out float y, out float z)
    {
        x = y = z = 0;
        var origin = enemyPawn.AbsOrigin;
        if (origin == null) return false;
        float eyeZ = enemyPawn.ViewOffset?.Z ?? 64.0f;
        float yaw  = enemyPawn.EyeAngles?.Y ?? 0.0f;
        return ComputePartPosCore(origin.X, origin.Y, origin.Z, yaw, eyeZ, idx, out x, out y, out z);
    }

    private static bool ComputePartPosCore(float ox, float oy, float oz, float yawDeg, float eyeZ,
                                           int idx, out float x, out float y, out float z)
    {
        x = y = z = 0;
        if (idx < 0 || idx >= _aimPoints.Length) return false;
        ref readonly AimPoint p = ref _aimPoints[idx];
        double yawRad = yawDeg * Math.PI / 180.0;
        float rX = (float)Math.Sin(yawRad);
        float rY = (float)-Math.Cos(yawRad);
        if (p.FeetAbs) { x = ox; y = oy; z = oz + p.Frac; }
        else
        {
            x = ox + rX * p.Lateral;
            y = oy + rY * p.Lateral;
            z = oz + eyeZ * p.Frac;
        }
        return !(float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)
                 || float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z));
    }

    // Build the inputs for the engine's per-frame extrapolator from history:
    //   base point  = the chosen body part at the reaction-lagged sample (t = now - reaction)
    //   baseTime    = that sample's timestamp (so the engine leads it forward to ~live and beyond)
    //   velocity    = (sampled velocity + lagged-acceleration bump) * LeadK
    // The acceleration is sampled a further AccelLagS into the past than the velocity, so a bot
    // reacts to a change in speed later than to speed itself (and can be juked). LeadK < 1 makes
    // the engine under-lead so strafing beats the bot.
    private bool TryPredict(int enemyIdx, BotState st, float now, int part,
                            out float x, out float y, out float z,
                            out float vx, out float vy, out float vz, out float baseTime)
    {
        x = y = z = vx = vy = vz = 0; baseTime = now;
        if (!_history.TryGetValue(enemyIdx, out var hh) || hh.Count == 0) return false;
        float reactionS = st.ReactionMs * 0.001f;
        float tV = now - reactionS;
        int i = hh.IndexAt(tV);
        if (i < 0) return false;
        ref readonly Sample s = ref hh.Buf[i];
        baseTime = s.T;

        // Acceleration from a velocity slope ending AccelLagS before the velocity sample.
        float ax = 0, ay = 0, az = 0;
        if (st.AccelK > 0f)
        {
            int iA = hh.IndexAt(tV - AccelLagS);
            int iB = hh.IndexAt(tV - AccelLagS - AccelDtS);
            if (iA >= 0 && iB >= 0)
            {
                float adt = hh.Buf[iA].T - hh.Buf[iB].T;
                if (adt > 1e-3f)
                {
                    ax = (hh.Buf[iA].VX - hh.Buf[iB].VX) / adt;
                    ay = (hh.Buf[iA].VY - hh.Buf[iB].VY) / adt;
                    az = (hh.Buf[iA].VZ - hh.Buf[iB].VZ) / adt;
                }
            }
        }
        vx = (s.VX + Math.Clamp(ax * st.AccelK, -AccelMaxVel, AccelMaxVel)) * st.LeadK;
        vy = (s.VY + Math.Clamp(ay * st.AccelK, -AccelMaxVel, AccelMaxVel)) * st.LeadK;
        vz = (s.VZ + Math.Clamp(az * st.AccelK, -AccelMaxVel, AccelMaxVel)) * st.LeadK;
        return ComputePartPosCore(s.PX, s.PY, s.PZ, s.Yaw, s.EyeZ, part, out x, out y, out z);
    }

    private void OnTick()
    {
        float now = Server.CurrentTime;
        foreach (var ctrl in Utilities.GetPlayers())
        {
            try
            {
                if (ctrl == null || !ctrl.IsValid) continue;
                var pawn = ctrl.PlayerPawn?.Value;
                if (pawn == null || !pawn.IsValid || pawn.Handle == IntPtr.Zero) continue;
                if (pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;
                PushHistory((int)pawn.Index, pawn, now);
            }
            catch { }
        }
    }

    private void PushHistory(int idx, CCSPlayerPawn pawn, float now)
    {
        var origin = pawn.AbsOrigin;
        if (origin == null) return;
        var vel = pawn.AbsVelocity;
        var s = new Sample
        {
            T = now, PX = origin.X, PY = origin.Y, PZ = origin.Z,
            VX = vel?.X ?? 0f, VY = vel?.Y ?? 0f, VZ = vel?.Z ?? 0f,
            Yaw = pawn.EyeAngles?.Y ?? 0f, EyeZ = pawn.ViewOffset?.Z ?? 64f,
        };
        if (!_history.TryGetValue(idx, out var hh)) { hh = new History(); _history[idx] = hh; }
        hh.Push(in s);
    }

    private bool PointVisibleFromEye(Vector eye, float tx, float ty, float tz)
    {
        try
        {
            var rt = _rayTraceCapability.Get();
            if (rt == null) return true;
            var end  = new Vector(tx, ty, tz);
            var opts = new TraceOptions(InteractionLayers.MASK_WORLD_ONLY);
            rt.TraceEndShape(eye, end, null, opts, out TraceResult res);
            return res.Fraction >= 0.999f;
        }
        catch { return true; }
    }

    private static unsafe byte   ReadByte(IntPtr addr)   => *(byte*)addr.ToPointer();
    private static unsafe int    ReadInt32(IntPtr addr)  => *(int*)addr.ToPointer();
    private static unsafe IntPtr ReadIntPtr(IntPtr addr) => *(IntPtr*)addr.ToPointer();
    private static unsafe void   WriteFloat(IntPtr addr, float v) => *(float*)addr.ToPointer() = v;
    private static unsafe Vector ReadVec3(IntPtr addr)
    { float* f = (float*)addr.ToPointer(); return new Vector(f[0], f[1], f[2]); }
}

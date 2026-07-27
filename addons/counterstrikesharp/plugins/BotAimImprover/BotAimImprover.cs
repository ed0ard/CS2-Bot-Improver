using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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

[MinimumApiVersion(305)]
public class BotAimImprover : BasePlugin
{
    public override string ModuleName => "BotAimImprover";
    public override string ModuleVersion => "2.1.3";
    public override string ModuleAuthor => "ed0ard & htfy96 & XBribo";
    public override string ModuleDescription => "Restores intelligent aim part selection for CS2 bots.";

    // ============================================================
    // Full-body derived aim points. Each point is defined in the enemy's local frame:
    //   pos.xy = origin.xy + RIGHT * Lateral   (RIGHT = player's right, from yaw)
    //   pos.z  = origin.z  + eyeZ * Frac        (FeetAbsRise>0 means absolute z+rise)
    // Heights (Frac of live eyeZ) come from tm_phoenix/ctm_sas spine bone world heights;
    // lateral offsets from hitbox radii + measured shoulder/elbow widths.
    // Index in this array is the part id used everywhere else.
    // ============================================================
    private readonly struct AimPoint
    {
        public readonly string Name;
        public readonly float Frac;        // height as fraction of live eyeZ (ignored if FeetAbs)
        public readonly float Lateral;     // +right / -left, world units
        public readonly bool FeetAbs;     // true => z = origin.z + Frac (absolute rise), lateral 0
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
        new("FEET",           5.0f,   0f, true), // 16  // absolute z + 5
    };
    // Priority orders (values are indices into _aimPoints), highest priority first.
    // Tiers: core > centerline > side > shoulder > limb > feet.
    // Within a tier, higher points come first. Left/right of equal height share a tier

    private static readonly int[] _priorityHead =
    {
        0, 1, 2,         // HEAD, NECK, JAW
        3, 4, 5,         // CHEST, GUT, PELVIS
        6, 7, 10, 11,    // L_CHEST, R_CHEST, L_GUT, R_GUT
        8, 9,            // L_SHOULDER, R_SHOULDER
        12, 13, 14, 15,  // L_THIGH, R_THIGH, L_SHIN, R_SHIN
        16               // FEET
    };

    private static readonly int[] _priorityJaw =
    {
        2, 1, 0,         // JAW, NECK, HEAD
        3, 4, 5,         // CHEST, GUT, PELVIS
        6, 7, 10, 11,    // L_CHEST, R_CHEST, L_GUT, R_GUT
        8, 9,            // L_SHOULDER, R_SHOULDER
        12, 13, 14, 15,  // L_THIGH, R_THIGH, L_SHIN, R_SHIN
        16               // FEET
    };

    private static readonly int[] _priorityBody =
    {
        4, 5, 3,         // GUT, PELVIS, CHEST,
        10, 11, 6, 7,    // L_GUT, R_GUT, L_CHEST, R_CHEST
        8, 9,            // L_SHOULDER, R_SHOULDER
        2, 1, 0,         // JAW, NECK, HEAD
        12, 13, 14, 15,  // L_THIGH, R_THIGH, L_SHIN, R_SHIN
        16               // FEET
    };
    // ============================================================
    // Platform-specific memory layout (PickNewAimSpot hook + CCSBot fields).
    //   Linux  libserver.so 2026-05-28
    //   Windows server.dll  2026-07-09
    // ============================================================
    private readonly struct Offsets
    {
        public readonly int TargetSpot;   // Vector(3) m_targetSpot
        public readonly int Enemy;        // CHandle m_enemy
        public readonly int IsVisible;    // bool m_isEnemyVisible
        public readonly int PBot;         // CCSPlayerPawn->m_pBot
        public readonly string Sig;
        public Offsets(int ts, int en, int vis, int pbot, string sig)
        {
            TargetSpot = ts;
            Enemy = en;
            IsVisible = vis;
            PBot = pbot;
            Sig = sig;
        }
    }

    private static readonly Offsets LinuxOffsets = new(
        ts: 0x5974, en: 0x59E0, vis: 0x59E4, pbot: 0x1590,
        sig: "55 48 89 E5 41 55 41 54 53 48 89 FB 48 83 EC 58 8B 8F E0 59 00 00 83 F9 FF");

    private static readonly Offsets WindowsOffsets = new(
        ts: 0x599C,
        en: 0x5A08,
        vis: 0x5A0C,
        pbot: 0x12C0,
        sig: "48 8B C4 55 57 48 8D 68 ? 48 81 EC ? ? ? ? 48 8B F9 0F 29 70 ? 8B 89 ? ? ? ? 83 F9 FF"
    );

    private Offsets _off;

    private MemoryFunctionVoid<IntPtr>? _pickNewAimSpot;
    private static readonly PluginCapability<CRayTraceInterface> _rayTraceCapability =
        new("raytrace:craytraceinterface");

    // Cache: CCSBot* -> bot's UserId .
    // Cleared on round_start and per-bot on disconnect.
    private readonly ConcurrentDictionary<IntPtr, int> _botToControllerUserId = new();

    // Aim mode controlled by the `bot_aim` console command:
    //   Mixed = priority logic; snipers + spread weapons aim body-first, others head-first
    //   Head  = always head-first
    //   Body  = always body-first
    private enum AimMode { MIXED, HEAD, BODY }
    private AimMode _aimMode = AimMode.MIXED;

    // Weapons that aim body-first when in Mixed mode (snipers + high-spread / shotguns).
    private static readonly HashSet<string> _bodyFirstWeapons = new()
    {
        "weapon_awp", "weapon_ssg08", "weapon_p90", "weapon_bizon",
        "weapon_nova", "weapon_xm1014", "weapon_sawedoff", "weapon_mag7", "weapon_revolver"
    };

    // One-shot flag so we log a single confirmation that overrides are actually firing.
    private bool _firstOverrideLogged = false;

    // ============================================================
    // Lifecycle
    // ============================================================

    public override void Load(bool hotReload)
    {
        bool win = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        _off = win ? WindowsOffsets : LinuxOffsets;

        try
        {
            _pickNewAimSpot = new MemoryFunctionVoid<IntPtr>(_off.Sig);

            long pnaRuntime = _pickNewAimSpot.Handle.ToInt64();
            if (pnaRuntime == 0)
                throw new InvalidOperationException("PickNewAimSpot signature resolved to zero address.");

            _pickNewAimSpot.Hook(OnPickNewAimSpotPost, HookMode.Post);

            Logger.LogInformation("[BotAimImprover] Loaded ({Plat}). PickNewAimSpot=0x{Pna:X16}",
                win ? "Windows" : "Linux", pnaRuntime);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BotAimImprover] Fatal error during Load() (signature broken?). Plugin inactive.");
            return;
        }

        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            _botToControllerUserId.Clear();
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerDisconnect>((ev, _) =>
        {
            try
            {
                var disconnecter = ev.Userid;
                if (disconnecter != null && disconnecter.UserId.HasValue)
                {
                    int leavingUserId = disconnecter.UserId.Value;
                    foreach (var kv in _botToControllerUserId)
                        if (kv.Value == leavingUserId)
                            _botToControllerUserId.TryRemove(kv.Key, out int _);
                }
            }
            catch { /* non-fatal */ }
            return HookResult.Continue;
        });

        AddCommand("bot_aim", "Set bot aim mode: head, body, mixed", (caller, info) =>
        {
            string arg = info.ArgCount > 1 ? info.GetArg(1).Trim().ToLowerInvariant() : "";
            string reply;
            switch (arg)
            {
                case "head":
                    _aimMode = AimMode.HEAD;
                    reply = "[BotAimImprover] aim mode -> HEAD (always head-first)";
                    break;
                case "body":
                    _aimMode = AimMode.BODY;
                    reply = "[BotAimImprover] aim mode -> BODY (always body-first)";
                    break;
                case "mixed":
                    _aimMode = AimMode.MIXED;
                    reply = "[BotAimImprover] aim mode -> MIXED (default)";
                    break;
                default:
                    reply = $"[BotAimImprover] Current aim mode: {_aimMode}. Valid values: head, body, mixed";
                    break;
            }
            Server.PrintToConsole(reply);
        });
    }

    public override void Unload(bool hotReload)
    {
        try { _pickNewAimSpot?.Unhook(OnPickNewAimSpotPost, HookMode.Post); }
        catch { /* ignore */ }
    }

    // ============================================================
    // Core override logic (Post-hook on PickNewAimSpot)
    //
    // Native function already set m_targetSpot to GUT or HEAD based on
    // mp_damage_headshot_only. We re-pick based on visible enemy parts and the
    // bot's weapon, then overwrite only the 12 bytes of m_targetSpot.
    // ============================================================
    private HookResult OnPickNewAimSpotPost(DynamicHook hook)
    {
        try
        {
            IntPtr pCCSBot = hook.GetParam<IntPtr>(0);
            if (pCCSBot == IntPtr.Zero)
                return HookResult.Continue;

            // 1) Gate: enemy must be generally visible before we
            //    spend any raytraces. Otherwise the native used last-known position.
            if (ReadByte(pCCSBot + _off.IsVisible) == 0)
                return HookResult.Continue;

            // 2) Resolve enemy pawn from m_enemy CHandle.
            int enemyHandleRaw = ReadInt32(pCCSBot + _off.Enemy);
            if (enemyHandleRaw == -1)
                return HookResult.Continue;

            int enemyIdx = enemyHandleRaw & 0x7FFF;
            if (enemyIdx <= 0 || enemyIdx >= 4096)
                return HookResult.Continue;

            CCSPlayerPawn? enemyPawn = Utilities.GetEntityFromIndex<CCSPlayerPawn>(enemyIdx);
            if (enemyPawn == null || !enemyPawn.IsValid || enemyPawn.Handle == IntPtr.Zero)
                return HookResult.Continue;

            // 3) Resolve the bot's controller (for weapon + eye position).
            var botController = ResolveBotController(pCCSBot);
            if (botController == null || !TryGetBotEyePosition(botController, out var botEye))
                return HookResult.Continue;

            string? wpn = botController.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value?.DesignerName;

            // 4) Select the priority order based on aim mode and weapon.
            // head: awp -> others -> Head. body: all weapons -> Body.
            // mixed: body-first weapons -> Body, others -> Jaw.
            bool isBodyWeapon = wpn != null && _bodyFirstWeapons.Contains(wpn);
            int[] order = _aimMode switch
            {
                AimMode.HEAD => wpn == "weapon_awp" ? _priorityBody : _priorityHead,
                AimMode.BODY => _priorityBody,
                _ => isBodyWeapon ? _priorityBody : _priorityJaw, // MIXED
            };

            // 5) Walk the priority order and raytrace each point from the bot's
            // eye; the first visible point wins.
            int chosenIdx = -1;
            float rx = 0f, ry = 0f, rz = 0f;
            foreach (int idx in order)
            {
                if (!TryComputePartPos(enemyPawn, idx, out float x, out float y, out float z))
                    continue;
                if (!PointVisibleFromEye(botEye, x, y, z))
                    continue;
                chosenIdx = idx;
                rx = x; ry = y; rz = z;
                break;
            }
            if (chosenIdx < 0)
                return HookResult.Continue;

            // 6) Overwrite only m_targetSpot.xyz with human-like imprecision.
            // Offset scales with distance — tighter for headshots, looser for body.
            float distToTarget = MathF.Sqrt(
                (rx - botEye.X) * (rx - botEye.X) +
                (ry - botEye.Y) * (ry - botEye.Y) +
                (rz - botEye.Z) * (rz - botEye.Z));
            float jitterScale = chosenIdx switch
            {
                0 => 0.005f,  // HEAD:  0.5%  (2.5u @ 500u)
                1 or 2 => 0.007f,  // NECK/JAW: 0.7%
                12 or 13 or 14 or 15 or 16 => 0.015f,  // LIMBS: 1.5%
                _ => 0.01f,  // BODY: 1%
            };
            float jitter = distToTarget * jitterScale;
            rx += ((float)Random.Shared.NextDouble() * 2f - 1f) * jitter;
            ry += ((float)Random.Shared.NextDouble() * 2f - 1f) * jitter;
            rz += ((float)Random.Shared.NextDouble() * 2f - 1f) * jitter;

            unsafe
            {
                float* dst = (float*)(pCCSBot + _off.TargetSpot).ToPointer();
                dst[0] = rx; dst[1] = ry; dst[2] = rz;
            }

            // One-time confirmation that the override path actually runs end-to-end.
            if (!_firstOverrideLogged)
            {
                _firstOverrideLogged = true;
                Logger.LogInformation(
                    "[BotAimImprover] Active: first override (weapon={W} point={P}).",
                    wpn ?? "(null)", _aimPoints[chosenIdx].Name);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BotAimImprover] Exception in PostHook");
        }

        return HookResult.Continue;
    }

    /// Find the CCSPlayerController whose pawn's m_pBot field equals pCCSBot.
    /// Cache key: CCSBot pointer. Cache value: bot UserId.
    private CCSPlayerController? ResolveBotController(IntPtr pCCSBot)
    {
        // Cache hit -> look up by UserId.
        if (_botToControllerUserId.TryGetValue(pCCSBot, out int cachedUserId))
        {
            foreach (var ctrl in Utilities.GetPlayers())
                if (ctrl != null && ctrl.IsValid && ctrl.UserId.HasValue && ctrl.UserId.Value == cachedUserId)
                    return ctrl;
            _botToControllerUserId.TryRemove(pCCSBot, out int _); // stale
        }

        // Slow path: find which bot pawn's m_pBot points to pCCSBot.
        foreach (var ctrl in Utilities.GetPlayers())
        {
            if (ctrl == null || !ctrl.IsValid || !ctrl.IsBot || !ctrl.UserId.HasValue)
                continue;

            var pawn = ctrl.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid || pawn.Handle == IntPtr.Zero)
                continue;

            IntPtr pBotPtr;
            try { pBotPtr = ReadIntPtr(pawn.Handle + _off.PBot); }
            catch { continue; }

            if (pBotPtr == pCCSBot)
            {
                _botToControllerUserId[pCCSBot] = ctrl.UserId.Value;
                return ctrl;
            }
        }
        return null;
    }

    // Bot eye position = bot pawn origin + view offset Z.
    private static bool TryGetBotEyePosition(CCSPlayerController bot, out Vector eye)
    {
        eye = new Vector(0, 0, 0);
        var pawn = bot.PlayerPawn?.Value;
        var origin = pawn?.AbsOrigin;
        if (origin == null) return false;
        float ez = pawn!.ViewOffset?.Z ?? 64.0f;
        eye = new Vector(origin.X, origin.Y, origin.Z + ez);
        return true;
    }

    // Compute world position of derived point `idx` from the enemy pawn's schema fields.
    private static bool TryComputePartPos(CCSPlayerPawn enemyPawn, int idx,
                                          out float x, out float y, out float z)
    {
        x = y = z = 0;
        if (idx < 0 || idx >= _aimPoints.Length) return false;
        var origin = enemyPawn.AbsOrigin;
        if (origin == null) return false;

        ref readonly AimPoint p = ref _aimPoints[idx];
        float ox = origin.X, oy = origin.Y, oz = origin.Z;
        float eyeZ = enemyPawn.ViewOffset?.Z ?? 64.0f;

        float yawDeg = enemyPawn.EyeAngles?.Y ?? 0.0f;
        double yawRad = yawDeg * Math.PI / 180.0;
        float rX = (float)Math.Sin(yawRad);   // RIGHT vector x
        float rY = (float)-Math.Cos(yawRad);  // RIGHT vector y

        if (p.FeetAbs)
        {
            x = ox; y = oy; z = oz + p.Frac;   // absolute rise (FEET)
        }
        else
        {
            x = ox + rX * p.Lateral;
            y = oy + rY * p.Lateral;
            z = oz + eyeZ * p.Frac;
        }
        return !(float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)
                 || float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z));
    }

    // World-only LoS test from eye to target point. True if unobstructed (>= 0.999).
    private bool PointVisibleFromEye(Vector eye, float tx, float ty, float tz)
    {
        try
        {
            var rt = _rayTraceCapability.Get();
            if (rt == null) return true; // RayTrace not loaded -> don't block
            var end = new Vector(tx, ty, tz);
            var opts = new TraceOptions(InteractionLayers.MASK_WORLD_ONLY);
            rt.TraceEndShape(eye, end, null, opts, out TraceResult res);
            return res.Fraction >= 0.999f;
        }
        catch { return true; }
    }

    // ============================================================
    // Raw memory readers
    // ============================================================
    private static unsafe byte ReadByte(IntPtr addr) => *(byte*)addr.ToPointer();
    private static unsafe int ReadInt32(IntPtr addr) => *(int*)addr.ToPointer();
    private static unsafe IntPtr ReadIntPtr(IntPtr addr) => *(IntPtr*)addr.ToPointer();
}

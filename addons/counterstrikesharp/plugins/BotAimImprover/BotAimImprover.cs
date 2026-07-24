using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Core.Capabilities;
using RayTraceAPI;
using Microsoft.Extensions.Logging;
using MatchCore;


namespace BotAimImprover;

[MinimumApiVersion(305)]
public class BotAimImprover : BasePlugin
{
    public override string ModuleName => "BotAimImprover";
    public override string ModuleVersion => "2.2.1";
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
    private static readonly PluginCapability<CRayTraceInterface> _rayTraceCapability =
        new("raytrace:craytraceinterface");
    private const float AimUpdateInterval = 0.05f;

    // Aim mode controlled by the `bot_aim` console command:
    //   Mixed = priority logic; snipers + spread weapons aim body-first, others head-first
    //   Head  = always head-first
    //   Body  = always body-first
    private readonly record struct CachedAimTarget(
        IntPtr BotHandle,
        IntPtr EnemyHandle,
        int AimPointIndex);

    private BotAimMode _aimMode = BotAimMode.Mixed;
    private bool _managedAimActive;
    private float _nextAimUpdate;
    private float _lastErrorLog = float.NegativeInfinity;
    private long _overrideCount;
    private long _headPointCount;
    private long _bodyPointCount;
    private long _errorCount;
    private long _loggedOverrideCount;
    private long _loggedHeadPointCount;
    private long _loggedBodyPointCount;
    private long _loggedErrorCount;
    private readonly Dictionary<int, CachedAimTarget> _cachedTargets = new();
    private readonly Dictionary<int, int> _consecutiveErrorsByController = new();

    // One-shot flag so we log a single confirmation that overrides are actually firing.
    private bool _firstOverrideLogged = false;

    // ============================================================
    // Lifecycle
    // ============================================================

    public override void Load(bool hotReload)
    {
        AddCommand("bot_aim", "Set bot aim mode: head, body, mixed", OnAimCommand);
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            LogRoundCounters();
            _nextAimUpdate = 0;
            _cachedTargets.Clear();
            _consecutiveErrorsByController.Clear();
            return HookResult.Continue;
        });
        _managedAimActive = true;
        PublishRuntimeStatus();
        Logger.LogInformation("[BotAimImprover] Loaded with managed CCSBot schema targeting.");
    }

    public override void Unload(bool hotReload)
    {
        RemoveListener<Listeners.OnTick>(OnTick);
        _managedAimActive = false;
        _cachedTargets.Clear();
        _consecutiveErrorsByController.Clear();
    }

    private void OnAimCommand(CCSPlayerController? caller, CounterStrikeSharp.API.Modules.Commands.CommandInfo info)
    {
        string arg = info.ArgCount > 1 ? info.GetArg(1).Trim().ToLowerInvariant() : "";
        if (arg is "head" or "body" or "mixed")
        {
            _aimMode = arg switch
            {
                "head" => BotAimMode.Head,
                "body" => BotAimMode.Body,
                _ => BotAimMode.Mixed,
            };
        }

        PublishRuntimeStatus();
        string reply = !_managedAimActive
            ? $"[BotAimImprover] Managed aim override is inactive; requested mode {_aimMode} was not applied."
            : $"[BotAimImprover] aim mode -> {_aimMode}";
        Server.PrintToConsole(reply);
    }

    // ============================================================
    // Core override logic. CounterStrikeSharp 1.0.371 exposes CCSBot through
    // schema, so this path does not use signatures, function hooks, or offsets.
    // ============================================================
    private void OnTick()
    {
        float now = Server.CurrentTime;
        if (!_managedAimActive) return;
        bool refreshTargets = now >= _nextAimUpdate;
        if (refreshTargets) _nextAimUpdate = now + AimUpdateInterval;

        foreach (var controller in Utilities.GetPlayers())
        {
            if (controller == null || !controller.IsValid || !controller.IsBot || !controller.PawnIsAlive)
                continue;

            try
            {
                ApplyAim(controller, refreshTargets);
            }
            catch (Exception ex)
            {
                _errorCount++;
                int controllerIndex = (int)controller.Index;
                int consecutiveErrors = _consecutiveErrorsByController.GetValueOrDefault(controllerIndex) + 1;
                _consecutiveErrorsByController[controllerIndex] = consecutiveErrors;
                if (now - _lastErrorLog >= 5.0f || now < _lastErrorLog)
                {
                    _lastErrorLog = now;
                    Logger.LogError(ex, "[BotAimImprover] Managed aim update failed for {Player}", controller.PlayerName);
                }
                if (consecutiveErrors >= 8)
                {
                    _managedAimActive = false;
                    _cachedTargets.Clear();
                    Logger.LogCritical(
                        "[BotAimImprover] Managed targeting disabled after repeated schema failures; native BotAI remains active.");
                    PublishRuntimeStatus();
                    return;
                }
            }
        }
    }

    private void ApplyAim(CCSPlayerController controller, bool refreshTarget)
    {
        int controllerIndex = (int)controller.Index;
        var pawn = controller.PlayerPawn?.Value;
        var bot = pawn?.Bot;
        if (pawn == null || !pawn.IsValid || bot == null || bot.Handle == IntPtr.Zero || !bot.IsEnemyVisible)
        {
            _cachedTargets.Remove(controllerIndex);
            return;
        }

        var enemyPawn = bot.Enemy.Value;
        if (enemyPawn == null || !enemyPawn.IsValid || enemyPawn.Handle == IntPtr.Zero)
        {
            _cachedTargets.Remove(controllerIndex);
            return;
        }

        if (refreshTarget)
        {
            if (!TrySelectTarget(pawn, enemyPawn, out int chosenIdx, out string? weapon))
            {
                _cachedTargets.Remove(controllerIndex);
                return;
            }
            _cachedTargets[controllerIndex] = new CachedAimTarget(bot.Handle, enemyPawn.Handle, chosenIdx);

            if (!_firstOverrideLogged)
            {
                _firstOverrideLogged = true;
                Logger.LogInformation(
                    "[BotAimImprover] Active: first managed override (weapon={Weapon} point={Point}).",
                    weapon ?? "(none)", _aimPoints[chosenIdx].Name);
                PublishRuntimeStatus();
            }
        }

        if (!_cachedTargets.TryGetValue(controllerIndex, out var target) ||
            target.BotHandle != bot.Handle || target.EnemyHandle != enemyPawn.Handle)
            return;

        if (!TryComputePartPos(enemyPawn, target.AimPointIndex, out float x, out float y, out float z))
        {
            _cachedTargets.Remove(controllerIndex);
            return;
        }

        Vector targetSpot = bot.TargetSpot;
        targetSpot.X = x;
        targetSpot.Y = y;
        targetSpot.Z = z;

        Vector readback = bot.TargetSpot;
        if (!NearlyEqual(readback.X, x) || !NearlyEqual(readback.Y, y) || !NearlyEqual(readback.Z, z))
            throw new InvalidOperationException("CCSBot.TargetSpot write verification failed");

        _consecutiveErrorsByController.Remove(controllerIndex);
        _overrideCount++;
        if (target.AimPointIndex <= 2) _headPointCount++;
        else _bodyPointCount++;
    }

    private bool TrySelectTarget(
        CCSPlayerPawn pawn,
        CCSPlayerPawn enemyPawn,
        out int chosenIdx,
        out string? weapon)
    {
        chosenIdx = -1;
        weapon = null;
        if (!TryGetBotEyePosition(pawn, out var botEye)) return false;

        weapon = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
        int[] order = BotAimPolicy.SelectPriority(_aimMode, weapon) switch
        {
            BotAimPriority.Head => _priorityHead,
            BotAimPriority.Body => _priorityBody,
            _ => _priorityJaw,
        };

        foreach (int idx in order)
        {
            if (!TryComputePartPos(enemyPawn, idx, out float x, out float y, out float z) ||
                !PointVisibleFromEye(botEye, x, y, z))
                continue;
            chosenIdx = idx;
            break;
        }
        return chosenIdx >= 0;
    }

    private static bool NearlyEqual(float actual, float expected) =>
        float.IsFinite(actual) && Math.Abs(actual - expected) <= 0.01f;

    private void LogRoundCounters()
    {
        long overrides = _overrideCount - _loggedOverrideCount;
        long headPoints = _headPointCount - _loggedHeadPointCount;
        long bodyPoints = _bodyPointCount - _loggedBodyPointCount;
        long errors = _errorCount - _loggedErrorCount;
        if (overrides > 0 || errors > 0)
        {
            Logger.LogInformation(
                "[BotAimImprover] round counters: overrides={Overrides} head_points={HeadPoints} body_points={BodyPoints} errors={Errors} mode={Mode}",
                overrides,
                headPoints,
                bodyPoints,
                errors,
                _aimMode);
        }
        _loggedOverrideCount = _overrideCount;
        _loggedHeadPointCount = _headPointCount;
        _loggedBodyPointCount = _bodyPointCount;
        _loggedErrorCount = _errorCount;
        PublishRuntimeStatus();
    }

    private void PublishRuntimeStatus()
    {
        try
        {
            if (!PlusManagedPaths.TryResolveCsgoRoot(Server.GameDirectory, out var csgoRoot)) return;
            BotRuntimeStatusStore.WriteAim(
                csgoRoot,
                "managed_ccsbot_schema",
                _managedAimActive,
                _aimMode,
                _overrideCount,
                _headPointCount,
                _bodyPointCount,
                _errorCount);
        }
        catch (Exception error)
        {
            Logger.LogWarning(error, "[BotAimImprover] Failed to publish managed targeting status");
        }
    }

    // Bot eye position = bot pawn origin + view offset Z.
    private static bool TryGetBotEyePosition(CCSPlayerPawn pawn, out Vector eye)
    {
        eye = new Vector(0, 0, 0);
        var origin = pawn.AbsOrigin;
        if (origin == null) return false;
        float ez = pawn.ViewOffset?.Z ?? 64.0f;
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
}

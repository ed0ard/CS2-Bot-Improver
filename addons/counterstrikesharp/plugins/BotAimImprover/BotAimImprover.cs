using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Core.Capabilities;
using RayTraceAPI;
using Microsoft.Extensions.Logging;


namespace BotAimImprover;

[MinimumApiVersion(305)]
public class BotAimImprover : BasePlugin
{
    public override string ModuleName => "BotAimImprover";
    public override string ModuleVersion => "2.2.0";
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
    private enum AimMode { MIXED, HEAD, BODY }
    private readonly record struct CachedAimTarget(
        IntPtr BotHandle,
        IntPtr EnemyHandle,
        float X,
        float Y,
        float Z);

    private AimMode _aimMode = AimMode.MIXED;
    private bool _managedAimActive;
    private float _nextAimUpdate;
    private float _lastErrorLog = float.NegativeInfinity;
    private readonly Dictionary<int, CachedAimTarget> _cachedTargets = new();

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
        AddCommand("bot_aim", "Set bot aim mode: head, body, mixed", OnAimCommand);
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            _nextAimUpdate = 0;
            _cachedTargets.Clear();
            return HookResult.Continue;
        });
        _managedAimActive = true;
        Logger.LogInformation("[BotAimImprover] Loaded with managed CCSBot schema targeting.");
    }

    public override void Unload(bool hotReload)
    {
        RemoveListener<Listeners.OnTick>(OnTick);
        _managedAimActive = false;
        _cachedTargets.Clear();
    }

    private void OnAimCommand(CCSPlayerController? caller, CounterStrikeSharp.API.Modules.Commands.CommandInfo info)
    {
        string arg = info.ArgCount > 1 ? info.GetArg(1).Trim().ToLowerInvariant() : "";
        if (arg is "head" or "body" or "mixed")
        {
            _aimMode = arg switch
            {
                "head" => AimMode.HEAD,
                "body" => AimMode.BODY,
                _ => AimMode.MIXED,
            };
        }

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
                if (now - _lastErrorLog >= 5.0f || now < _lastErrorLog)
                {
                    _lastErrorLog = now;
                    Logger.LogError(ex, "[BotAimImprover] Managed aim update failed for {Player}", controller.PlayerName);
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
            if (!TrySelectTarget(pawn, bot, enemyPawn, out var selected, out int chosenIdx, out string? weapon))
            {
                _cachedTargets.Remove(controllerIndex);
                return;
            }
            _cachedTargets[controllerIndex] = selected;

            if (!_firstOverrideLogged)
            {
                _firstOverrideLogged = true;
                Logger.LogInformation(
                    "[BotAimImprover] Active: first managed override (weapon={Weapon} point={Point}).",
                    weapon ?? "(none)", _aimPoints[chosenIdx].Name);
            }
        }

        if (!_cachedTargets.TryGetValue(controllerIndex, out var target) ||
            target.BotHandle != bot.Handle || target.EnemyHandle != enemyPawn.Handle)
            return;

        Vector targetSpot = bot.TargetSpot;
        targetSpot.X = target.X;
        targetSpot.Y = target.Y;
        targetSpot.Z = target.Z;
    }

    private bool TrySelectTarget(
        CCSPlayerPawn pawn,
        CCSBot bot,
        CCSPlayerPawn enemyPawn,
        out CachedAimTarget target,
        out int chosenIdx,
        out string? weapon)
    {
        target = default;
        chosenIdx = -1;
        weapon = null;
        if (!TryGetBotEyePosition(pawn, out var botEye)) return false;

        weapon = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
        bool isBodyWeapon = weapon != null && _bodyFirstWeapons.Contains(weapon);
        int[] order = _aimMode switch
        {
            AimMode.HEAD => _priorityHead,
            AimMode.BODY => _priorityBody,
            _ => isBodyWeapon ? _priorityBody : _priorityJaw,
        };

        float targetX = 0f, targetY = 0f, targetZ = 0f;
        foreach (int idx in order)
        {
            if (!TryComputePartPos(enemyPawn, idx, out float x, out float y, out float z) ||
                !PointVisibleFromEye(botEye, x, y, z))
                continue;
            chosenIdx = idx;
            targetX = x;
            targetY = y;
            targetZ = z;
            break;
        }
        if (chosenIdx < 0) return false;

        target = new CachedAimTarget(bot.Handle, enemyPawn.Handle, targetX, targetY, targetZ);
        return true;
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

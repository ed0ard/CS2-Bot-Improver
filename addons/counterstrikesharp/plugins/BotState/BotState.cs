using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

public class BotState : BasePlugin
{
    public override string ModuleName        => "Smarter-Bot";
    public override string ModuleVersion     => "1.6.0";
    public override string ModuleAuthor      => "ed0ard";
    public override string ModuleDescription => "Make bots smarter";

    private const float ExpandedValue = 4000f;
    private const float NormalValue   = 100f;
    private const float RestoreDelay  = 1.0f;

    private bool _isExpanded = false;
    private ConVar? _smokeConVar;

    private readonly Random _random = new Random();

    private readonly Dictionary<int, bool> _prevIsAttacking = new();
    private readonly Dictionary<int, bool> _prevInAir       = new();
    private readonly Dictionary<int, float> _ladderExitTime = new();
    private readonly Dictionary<int, float> _lastLateralDir = new();

    private readonly Dictionary<int, float>  _airEnterTime    = new();
    private readonly Dictionary<int, bool>   _airBoostBlocked = new();
    private readonly Dictionary<int, bool>  _airHadValidSpeed = new();

    private readonly Dictionary<int, float>  _stuckStartTime  = new();
    private readonly Dictionary<int, Vector> _stuckStartPos   = new();
    private readonly Dictionary<int, bool>   _stuckJumpDone   = new();
    private readonly Dictionary<int, int>    _stuckJumpCount  = new();
    private readonly Dictionary<int, float>  _stuckMaxSpeed   = new();
    private bool _isFreezeTime = false;

    private readonly Dictionary<int, float>  _condCBoostTime  = new();
    private readonly Dictionary<int, bool>   _condCArmed      = new();
    private readonly Dictionary<int, bool>   _condCDone       = new();

    private readonly HashSet<int> _hasFiredThisAttack = new();
    private readonly Dictionary<int, float> _awpBoostUntil    = new();
    private readonly Dictionary<int, float> _awpBoostDirX     = new();
    private readonly Dictionary<int, float> _awpBoostDirY     = new();
//---------------------------------------------------------------------------------------
    public override void Load(bool hotReload)
    {
        _smokeConVar = ConVar.Find("bot_max_visible_smoke_length");
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventBombBegindefuse>(OnBombBeginDefuse);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterListener<Listeners.OnTick>(OnTick);
    }
//---------------------------------------------------------------------------------------
    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo _)
    {
        try
        {
            var victim = @event.Userid;
            if (victim == null || !victim.IsValid || !victim.IsBot) return HookResult.Continue;

            if (!_isExpanded)
            {
                _isExpanded = true;
                SetSmokeLength(ExpandedValue);
                AddTimer(RestoreDelay, () =>
                {
                    SetSmokeLength(NormalValue);
                    _isExpanded = false;
                });
            }
        }
        catch { }
        return HookResult.Continue;
    }

    private void SetSmokeLength(float value)
    {
        if (_smokeConVar != null)
            _smokeConVar.SetValue(value);
        else
            Server.ExecuteCommand($"bot_max_visible_smoke_length {value}");
    }

    public override void Unload(bool hotReload)
    {
        SetSmokeLength(NormalValue);
    }
//---------------------------------------------------------------------------------------
    private HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player is null || !player.IsValid || !player.IsBot)
            return HookResult.Continue;

        bool isImmune = _random.NextDouble() <= 0.7;
        
        if (isImmune)
        {
            @event.BlindDuration = 0f;
            var pawn = player.PlayerPawn?.Value;
            if (pawn != null && pawn.IsValid)
            {
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
//---------------------------------------------------------------------------------------
    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot)
            return HookResult.Continue;

        Server.NextFrame(() =>
        {
            if (player == null || !player.IsValid) return;
            ApplyBotState(player);
        });

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot) continue;
            ApplyBotState(player);
        }
        return HookResult.Continue;
    }
//---------------------------------------------------------------------------------------
    private void OnTick()
    {
        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot)
                continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid) 
                continue;

            var bot = pawn.Bot;
            if (bot == null) 
                continue;

            int idx = (int)player.Index;
            float now = Server.CurrentTime;

            ref bool isSleeping = ref bot.IsSleeping;
            isSleeping = false;

            ref bool allowActive = ref bot.AllowActive;
            allowActive = true;
            
            ref bool isRapidFiring = ref bot.IsRapidFiring;
            isRapidFiring = true;

            ref float fireWeaponTimestamp = ref bot.FireWeaponTimestamp;
            fireWeaponTimestamp = 0.0f;

            ref float duration = ref bot.IgnoreEnemiesTimer.Duration;
            duration = 0.0f;
  
            // Random combat crouch
            bool curIsAttacking = bot.IsAttacking;
            _prevIsAttacking.TryGetValue(idx, out bool prevIsAttacking);

            if (!curIsAttacking && prevIsAttacking)
                _hasFiredThisAttack.Remove(idx);
            
            bool justFired = curIsAttacking && _hasFiredThisAttack.Contains(idx)
                          && !prevIsAttacking == false;

            if (justFired && !_prevIsAttacking.GetValueOrDefault(idx))
                justFired = false;
                
            bool fireEdge = curIsAttacking && _hasFiredThisAttack.Remove(idx);

            if (fireEdge && !_isFreezeTime)
            {
                bool isDefusing = pawn.IsDefusing;
                if (!isDefusing)
                {
                    ref bool isCrouching = ref bot.IsCrouching;
                    isCrouching = _random.NextDouble() < 0.25;

                    CountdownTimer sneakTimer = bot.SneakTimer;

                    ref float sneakduration = ref sneakTimer.Duration;
                    sneakduration = 0.0f;

                    ref float sneaktimestamp = ref sneakTimer.Timestamp;
                    sneaktimestamp = 0.0f;

                    ref float sneaktimescale = ref sneakTimer.Timescale;
                    sneaktimescale = 1.0f;
                }
                // Sniper Peek
                string? wpn = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
                if (wpn == "weapon_awp" || wpn == "weapon_ssg08")
                {
                    _lastLateralDir.TryGetValue(idx, out float lastDir);
                    if (lastDir != 0f)
                    {
                        float yawS = pawn.EyeAngles.Y * MathF.PI / 180f;
                        float rx   = -MathF.Sin(yawS), ry = MathF.Cos(yawS);
                        float injX = rx * (-lastDir) * 200f;
                        float injY = ry * (-lastDir) * 200f;
                        pawn.AbsVelocity.X += injX;
                        pawn.AbsVelocity.Y += injY;
                    }
                }
            }

            if (!curIsAttacking)
            {
                float yawL2 = pawn.EyeAngles.Y * MathF.PI / 180f;
                float latX  = -MathF.Sin(yawL2), latY = MathF.Cos(yawL2);
                float latSpd = pawn.AbsVelocity.X * latX + pawn.AbsVelocity.Y * latY;
                if (MathF.Abs(latSpd) > 10f)
                {
                    float newDir = latSpd > 0f ? 1f : -1f;
                    float prevDir = _lastLateralDir.GetValueOrDefault(idx);
                    _lastLateralDir[idx] = newDir;
                }
            }
            _prevIsAttacking[idx] = curIsAttacking;

            // Ladder Stuck Issue Fix
            var moveServices = pawn.MovementServices as CCSPlayer_MovementServices;
            var ladderNormal = moveServices?.LadderNormal;

            bool nearLadder = pawn.MoveType == MoveType_t.MOVETYPE_LADDER
                        || (ladderNormal != null
                            && (ladderNormal.X != 0f || ladderNormal.Y != 0f || ladderNormal.Z != 0f));

            if (nearLadder) _ladderExitTime[idx] = Server.CurrentTime;

            bool inLadderCooldown = nearLadder
                || (_ladderExitTime.TryGetValue(idx, out float exitTime)
                    && Server.CurrentTime - exitTime < 5.0f);

            bool inAir = !inLadderCooldown
                    && (pawn.GroundEntity == null || !pawn.GroundEntity.IsValid);

            _prevInAir.TryGetValue(idx, out bool prevInAir);
            // Jump Crouch Forward
            if (inAir)
            {
                if (!prevInAir)
                {
                    _airEnterTime[idx]     = now;
                    _airBoostBlocked[idx]  = false;
                    _airHadValidSpeed[idx] = false;
                }

                _airBoostBlocked.TryGetValue(idx,  out bool blocked);
                _airEnterTime.TryGetValue(idx,     out float enterTime);
                _airHadValidSpeed.TryGetValue(idx, out bool hadValid);

                if (!blocked)
                {
                    var angles = pawn.EyeAngles;
                    float yaw  = angles.Y * MathF.PI / 180f;
                    float fwdX = MathF.Cos(yaw);
                    float fwdY = MathF.Sin(yaw);
                    float currentFwd = pawn.AbsVelocity.X * fwdX + pawn.AbsVelocity.Y * fwdY;

                    if (currentFwd > 0f)
                        _airHadValidSpeed[idx] = hadValid = true;

                    if (!hadValid && now - enterTime >= 1.0f)
                    {
                        _airBoostBlocked[idx] = blocked = true;

                        int jumpCount = _stuckJumpCount.GetValueOrDefault(idx);
                        _stuckJumpCount[idx] = jumpCount + 1;
                        float sideSign  = (jumpCount % 2 == 0) ? 1f : -1f;
                        float offsetRad = 15f * MathF.PI / 180f * sideSign;
                        float backYaw   = yaw + MathF.PI + offsetRad;

                        pawn.AbsVelocity.X = MathF.Cos(backYaw) * 100f;
                        pawn.AbsVelocity.Y = MathF.Sin(backYaw) * 100f;
                        if (!_isFreezeTime && !pawn.IsDefusing)
                            bot.IsCrouching = true;
                        // Record
                        _condCBoostTime[idx] = now;
                        _condCArmed[idx]     = true;
                        _condCDone[idx]      = false;
                    }

                    if (!blocked)
                    {
                        if (!_isFreezeTime && !pawn.IsDefusing)
                        {
                            ref bool isCrouching = ref bot.IsCrouching;
                            isCrouching = true;
                        }

                        const float targetFwd = 200f;
                        if (currentFwd < targetFwd)
                        {
                            float boost = targetFwd - currentFwd;
                            pawn.AbsVelocity.X += fwdX * boost;
                            pawn.AbsVelocity.Y += fwdY * boost;
                        }
                    }
                }
            }
            // Cancel Crouch
            if (prevInAir && !inAir)
            {
                ref bool isCrouching = ref bot.IsCrouching;
                isCrouching = false;
            }
            _prevInAir[idx] = inAir;
            // Face-the-wall Check
            if (_condCArmed.GetValueOrDefault(idx) && !_condCDone.GetValueOrDefault(idx))
            {
                _condCBoostTime.TryGetValue(idx, out float boostT);
                float condCElapsed = now - boostT;

                if (condCElapsed >= 1.0f)
                {
                    float spd2D = MathF.Sqrt(
                        pawn.AbsVelocity.X * pawn.AbsVelocity.X +
                        pawn.AbsVelocity.Y * pawn.AbsVelocity.Y);

                    if (condCElapsed >= 4.0f && spd2D <= 0f)
                    {
                        _condCDone[idx]  = true;
                        _condCArmed[idx] = false;

                        int jumpCount = _stuckJumpCount.GetValueOrDefault(idx);
                        _stuckJumpCount[idx] = jumpCount + 1;
                        float sideSign  = (jumpCount % 2 == 0) ? 1f : -1f;
                        float offsetRad = 15f * MathF.PI / 180f * sideSign;
                        float baseYaw   = pawn.EyeAngles.Y * MathF.PI / 180f;
                        float backYaw   = baseYaw + MathF.PI + offsetRad;

                        pawn.AbsVelocity.X = MathF.Cos(backYaw) * 100f;
                        pawn.AbsVelocity.Y = MathF.Sin(backYaw) * 100f;
                        if (!_isFreezeTime && !pawn.IsDefusing)
                            bot.IsCrouching = true;
                    }
                    else if (spd2D > 0f)
                    {
                        _condCArmed[idx] = false;
                        _condCDone[idx]  = false;
                    }
                }
            }

            // Normal Un-Stuck Process
            ref bool isStuck = ref bot.IsStuck;
            if (isStuck)
            {
                ref bool isRunning = ref bot.IsRunning;
                isRunning = true;

                ref float jumpTimestamp = ref bot.JumpTimestamp;
                jumpTimestamp = 0.0f;

                CountdownTimer stuckJumpTimer = bot.StuckJumpTimer;

                ref float stuckduration = ref stuckJumpTimer.Duration;
                stuckduration = 0.0f;

                ref float stucktimestamp = ref stuckJumpTimer.Timestamp;
                stucktimestamp = Server.CurrentTime;

                ref float stucktimescale = ref stuckJumpTimer.Timescale;
                stucktimescale = 1.0f;

                // Manual Stuck State
                float speed2D = MathF.Sqrt(
                    pawn.AbsVelocity.X * pawn.AbsVelocity.X +
                    pawn.AbsVelocity.Y * pawn.AbsVelocity.Y);

                var curPos = pawn.AbsOrigin!;

                if (!_stuckStartTime.ContainsKey(idx))
                {
                    _stuckStartTime[idx] = now;
                    _stuckStartPos[idx]  = new Vector(curPos.X, curPos.Y, curPos.Z);
                    _stuckJumpDone[idx]  = false;
                    _stuckMaxSpeed[idx]  = 0f;
                }

                if (speed2D > _stuckMaxSpeed.GetValueOrDefault(idx))
                    _stuckMaxSpeed[idx] = speed2D;

                float elapsed = now - _stuckStartTime.GetValueOrDefault(idx);
                var   sp      = _stuckStartPos.GetValueOrDefault(idx, new Vector(curPos.X, curPos.Y, curPos.Z));
                float dist2D  = MathF.Sqrt(
                    MathF.Pow(curPos.X - sp.X, 2) +
                    MathF.Pow(curPos.Y - sp.Y, 2));
                float maxSpd  = _stuckMaxSpeed.GetValueOrDefault(idx);

                bool condA = elapsed >= 1.0f && maxSpd <= 10f;
                bool condB = elapsed >= 3.0f && maxSpd > 10f && dist2D < 75f;

                if ((condA || condB) && !_stuckJumpDone.GetValueOrDefault(idx))
                {
                    _stuckJumpDone[idx] = true;

                    int jumpCount = _stuckJumpCount.GetValueOrDefault(idx);
                    _stuckJumpCount[idx] = jumpCount + 1;

                    float sideSign  = (jumpCount % 2 == 0) ? 1f : -1f;
                    float offsetRad = 15f * MathF.PI / 180f * sideSign;
                    float baseYaw   = pawn.EyeAngles.Y * MathF.PI / 180f;
                    float backYaw   = baseYaw + MathF.PI + offsetRad;

                    pawn.AbsVelocity.X = MathF.Cos(backYaw) * 100f;
                    pawn.AbsVelocity.Y = MathF.Sin(backYaw) * 100f;

                    if (!_isFreezeTime && !pawn.IsDefusing)
                        bot.IsCrouching = true;

                    // Reset
                    _stuckStartTime[idx] = now;
                    _stuckStartPos[idx]  = new Vector(curPos.X, curPos.Y, curPos.Z);
                    _stuckMaxSpeed[idx]  = 0f;
                    _stuckJumpDone[idx]  = false;
                }
            }
            else
            {
                // Clear
                _stuckStartTime.Remove(idx);
                _stuckStartPos.Remove(idx);
                _stuckJumpDone.Remove(idx);
                _stuckMaxSpeed.Remove(idx);
            }
        }
    }
//---------------------------------------------------------------------------------------
    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _isFreezeTime = true;
        _hasFiredThisAttack.Clear();
        return HookResult.Continue;
    }

    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var shooter = @event.Userid;
        
        if (shooter == null || !shooter.IsValid || !shooter.IsBot) return HookResult.Continue;

        int idx = (int)shooter.Index;
        var bot = shooter.PlayerPawn?.Value?.Bot;

        if (bot != null && bot.IsAttacking)
            _hasFiredThisAttack.Add(idx);

        return HookResult.Continue;
    }

    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot)
                continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            var bot = pawn.Bot;
            if (bot == null)
                continue;

            CountdownTimer hurryTimer = bot.HurryTimer;

            ref float duration = ref hurryTimer.Duration;
            duration = 40.0f;

            ref float timestamp = ref hurryTimer.Timestamp;
            timestamp = Server.CurrentTime;

            ref float timescale = ref hurryTimer.Timescale;
            timescale = 1.0f;

            ref bool isRunning = ref bot.IsRunning;
            isRunning = true;
        }
        return HookResult.Continue;
    }

    private HookResult OnBombBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
    {
        ResetLookAroundForBot(@event.Userid);

        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot) return HookResult.Continue;

        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return HookResult.Continue;

        var bot = pawn.Bot;
        if (bot == null) return HookResult.Continue;

        bool hasLivingEnemies = Utilities
            .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Any(p => p.IsValid && p.PawnIsAlive
                && ((int)p.TeamNum == 2 || (int)p.TeamNum == 3)
                && (int)p.TeamNum != (int)player.TeamNum);
        // Fake Defuse
        if (hasLivingEnemies)
        {
            if (_random.NextDouble() < 0.66)
            {
                float yaw  = pawn.EyeAngles.Y * MathF.PI / 180f;
                float rx   = -MathF.Sin(yaw);
                float ry   =  MathF.Cos(yaw);
                float side = _random.NextDouble() < 0.5 ? 1f : -1f;

                pawn.AbsVelocity.X += rx * side * 150f;
                pawn.AbsVelocity.Y += ry * side * 150f;
                pawn.AbsVelocity.Z += 255f;
            }
        }

        return HookResult.Continue;
    }

    private static void ResetLookAroundForBot(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || !player.IsBot) return;
        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return;
        var bot = pawn.Bot;
        if (bot == null) return;

        ref float inhibitLookAroundTimestamp = ref bot.InhibitLookAroundTimestamp;
        inhibitLookAroundTimestamp = 0f;

        ref int checkedHidingSpotCount = ref bot.CheckedHidingSpotCount;
        checkedHidingSpotCount = 0;
    }
//---------------------------------------------------------------------------------------
    private static void ApplyBotState(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return;

        var bot = pawn.Bot;
        if (bot == null) return;

        ref float safeTime = ref bot.SafeTime;
        safeTime = 0f;  

        ref bool hasVisitedEnemySpawn = ref bot.HasVisitedEnemySpawn;
        hasVisitedEnemySpawn = true;
    }
//---------------------------------------------------------------------------------------

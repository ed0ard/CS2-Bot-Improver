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

public partial class NadeSystemPlugin : BasePlugin
{
    // ═══════════════════════════════════════════════════════════
    //  Normal mode/ more mode decision system
    // ═══════════════════════════════════════════════════════════

    // * Runs situational checks before attempting a replay
    private void TryConditionalReplay(CCSPlayerController bot, GrenadeData g,
        List<CCSPlayerController> allControllers, FlashTargetEvaluation? flashEvaluation = null)
    {
        var pawn = bot.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return;
        if (!PassesSituationalCheck(bot, pawn, g, g.GrenadeType, allControllers, flashEvaluation))
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

    // * Evaluates combat, information, probability, and mode-specific rules
    private bool PassesSituationalCheck(
        CCSPlayerController bot, CCSPlayerPawn pawn, GrenadeData g, string gtype,
        List<CCSPlayerController> allControllers, FlashTargetEvaluation? flashEvaluation)
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
            var blindableEnemies = flashEvaluation?.BlindableEnemies
                ?? EvaluateFlashTargets(bot, g, allControllers).BlindableEnemies;
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
    // * Validates team tags and early-round throw schedules
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

    // * Evaluates living and blindable enemies once for a flash decision
    private FlashTargetEvaluation EvaluateFlashTargets(CCSPlayerController bot, GrenadeData g,
        List<CCSPlayerController> allControllers)
    {
        var blindableEnemies = new List<CCSPlayerController>();
        int totalEnemies = 0;
        float lx = g.LandingPosition.X, ly = g.LandingPosition.Y, lz = g.LandingPosition.Z;
        foreach (var p in allControllers)
        {
            if (!p.IsValid || (int)p.TeamNum == bot.TeamNum) continue;
            var ep = GetActiveLivePawn(p);
            if (ep == null) continue;
            totalEnemies++;
            if (ep.AbsOrigin == null || ep.EyeAngles == null) continue;

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
                    blindableEnemies.Add(p);
            }
        }
        return new FlashTargetEvaluation(blindableEnemies, totalEnemies);
    }
    // Returns true if LandingPosition has unobstructed LoS to the given eye point.
    // Uses Masks.SolidBrushOnly, ignores players/props
    // * Checks world-only line of sight from a flash to an eye position
    private bool FlashHasLoS(Vec3 landing, float eyeX, float eyeY, float eyeZ)
    {
        try
        {
            var start = new Vector(landing.X, landing.Y, landing.Z);
            var end   = new Vector(eyeX, eyeY, eyeZ);

            var opts = new TraceOptions { InteractsWith = Masks.SolidBrushOnly };
            var res = Trace.TraceEndShape(start, end, options: opts);

            // fraction >= 0.99 → enemy can see the flash
            return res.Fraction >= 0.99f;
        }
        catch
        {
            return true;
        }
    }
    // Post-throw probability for flash for this bot in 12 seconds
    // * Maps the blindable enemy ratio to a replay probability
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
}

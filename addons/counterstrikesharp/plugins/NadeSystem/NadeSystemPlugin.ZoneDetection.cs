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
    //  Bot Zone Detection
    //
    //  Scanned every 4 ticks.
    // ═══════════════════════════════════════════════════════════

    // * Detects bots entering configured grenade trigger zones
    private void CheckBotZones()
    {
        if (_botNadesMode == "off") return;
        // Don't throw nades if the round is over
        if (_roundOver) return;

        var mapNades = _mapNades;
        if (mapNades.Count == 0) return;

        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        if (rules?.GameRules?.FreezePeriod == true) return;

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

            IReadOnlyList<GrenadeData> nearbyNades = _grenadeZoneGrid.TryGetValue(
                GetGrenadeZoneGridCell(pos.X, pos.Y), out var indexedNades)
                ? indexedNades
                : Array.Empty<GrenadeData>();

            foreach (var g in nearbyNades)
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
                    if (_botNadesMode == "off") continue;
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
                bool doDirectionCheck = _botNadesMode == "normal" || _botNadesMode == "more" || _botNadesMode == "less"
                    ? (gtype == "smoke" || gtype == "flash")
                    : (gtype == "smoke");
                if (doDirectionCheck && !FacesThrowDirection(pawn, g)) continue;

                if (_botNadesMode == "max")
                {
                    if (gtype == "flash"
                        && EvaluateFlashTargets(bot, g, allControllers).BlindableEnemies.Count == 0) continue;
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
                        var flashEvaluation = EvaluateFlashTargets(bot, g, allControllers);
                        float ratio = GetFlashRatioThreshold(
                            flashEvaluation.BlindableEnemies.Count, flashEvaluation.TotalEnemies);
                        if (ratio <= 0f) break; // 0% → never throw
                        _botFlashRatioWindow[bidx] = (Server.CurrentTime + 12f, ratio);

                        TryConditionalReplay(bot, g, allControllers, flashEvaluation);
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

    // * Checks whether a team still has at least one living opponent
    private static bool HasLiveEnemyForTeam(int teamNum, List<CCSPlayerController> allControllers)
    => allControllers
        .Any(p => p.IsValid && p.PawnIsAlive
            && ((int)p.TeamNum == 2 || (int)p.TeamNum == 3)
            && (int)p.TeamNum != teamNum);

    // Direction Judge 90°
    // * Checks whether a bot faces the recorded throw direction
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
    // * Resolves the live pawn currently controlled by a player controller
    private CCSPlayerPawn? GetActiveLivePawn(CCSPlayerController p)
    {
        if (!p.IsValid) return null;
        var basePawn = p.Pawn?.Value;
        if (basePawn == null || !basePawn.IsValid) return null;
        if (basePawn.LifeState != 0) return null; // 0 = LIFE_ALIVE
        if (basePawn.Health <= 0) return null;
        return basePawn.As<CCSPlayerPawn>();
    }

}

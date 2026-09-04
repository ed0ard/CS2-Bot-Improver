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
    //  Special Nades
    // ═══════════════════════════════════════════════════════════
    // Defuse smoke/flash
    // * Spawns and charges an immediate situational grenade
    private void TrySpawnInstantGrenade(CCSPlayerController bot, Vector spawnPos, string gtype, Vector? velocity = null)
    {
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
        if (!HasLockedNadeMoney(botIdx, cost)) return;
        bool isPoor   = _poorBots.Contains((uint)bot.Index);
        int  spendCap = GetRoundSpendCap(isCT, isPoor);
        if (!_roundSpendPerBot.TryGetValue(botIdx, out int alreadySpent))
            alreadySpent = 0;
        bool deduct = alreadySpent < spendCap;

        if (deduct)
        {
            money.Account -= cost;
            Utilities.SetStateChanged(bot, "CCSPlayerController", "m_pInGameMoneyServices");
            _roundSpendPerBot[botIdx] = alreadySpent + cost;
        }
        SpendLockedNadeMoney(botIdx, cost);

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
                    AnnounceGrenadeThrow(bot, gtype);
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
                    AnnounceGrenadeThrow(bot, gtype);
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[NadeSystem] TrySpawnInstantGrenade error: {ex.Message}");
            }
        });
    }

    // * Triggers defensive smoke or flash support during a defuse
    private HookResult OnBombBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
    {
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
    // * Triggers smoke support when a bot starts planting
    private HookResult OnBombBeginPlant(EventBombBeginplant @event, GameEventInfo info)
    {
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

    // * Dispatches damage-driven grenade reactions
    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        HandleMolotovEscape(@event);
        HandleRetaliationHE(@event);

        return HookResult.Continue;
    }
    // Put out the fire
    // * Uses smoke to help a bot escape sustained fire damage
    private void HandleMolotovEscape(EventPlayerHurt @event)
    {
        if (_botNadesMode == "off") return;
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
    // * Selects and throws a retaliatory explosive grenade
    private void HandleRetaliationHE(EventPlayerHurt @event)
    {
        if (_botNadesMode == "off") return;
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
            if (!HasLockedNadeMoney(botIdx, cost)) continue;
            // Less mode: enforce per-bot round limits (counts retaliation nades).
            if (_botNadesMode == "less" && !LessModeAllows(gt, botIdx)) continue;

            if (!_roundSpendPerBot.TryGetValue(botIdx, out int alreadySpent)) alreadySpent = 0;
            bool deduct = alreadySpent < spendCap;
            if (deduct)
            {
                money.Account -= cost;
                Utilities.SetStateChanged(victim, "CCSPlayerController", "m_pInGameMoneyServices");
                _roundSpendPerBot[botIdx] = alreadySpent + cost;
            }
            SpendLockedNadeMoney(botIdx, cost);

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
}

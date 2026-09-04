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
    //  Cooldown helpers
    // ═══════════════════════════════════════════════════════════

    // * Checks whether a grenade record is still on cooldown
    private bool IsOnCooldown(string id)
        => _cooldowns.Any(c => c.GrenadeId == id && c.ExpiresAt > Server.CurrentTime);

    // * Starts the configured cooldown for a grenade record
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

    // * Removes expired replay and probability cooldowns
    private void PruneCooldowns()
    {
        float now = Server.CurrentTime;
        _cooldowns.RemoveAll(c => c.ExpiresAt <= now);
    }
    // Information System cooldown
    // * Checks whether a failed probability attempt is throttled
    private bool IsOnProbFailCooldown(string id)
        => _probFailCooldown.TryGetValue(id, out float t) && t > Server.CurrentTime;

    // * Throttles repeated probability attempts for a grenade record
    private void RegisterProbFailCooldown(string id)
        => _probFailCooldown[id] = Server.CurrentTime + 3f;

    // * Calculates Euclidean distance between two three-dimensional points
    private static float Dist3D(float x1, float y1, float z1, float x2, float y2, float z2)
    {
        float dx = x1-x2, dy = y1-y2, dz = z1-z2;
        return MathF.Sqrt(dx*dx + dy*dy + dz*dz);
    }
    // ═══════════════════════════════════════════════════════════
    //  Round count helpers
    // ═══════════════════════════════════════════════════════════

    // * Increments the per-team grenade count for the current round
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
    // * Increments the per-bot grenade count for less mode
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
    // * Enforces the per-bot grenade limits used by less mode
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
}

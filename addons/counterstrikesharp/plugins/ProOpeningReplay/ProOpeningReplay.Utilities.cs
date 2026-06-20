using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using RayTraceAPI;

namespace ProOpeningReplay;

public sealed partial class ProOpeningReplayPlugin
{
    private static bool IsGiveableItem(string itemName)
    {
        if (itemName.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase)
            || itemName.Equals("weapon_c4_explosive", StringComparison.OrdinalIgnoreCase)
            || itemName.Equals("weapon_knife", StringComparison.OrdinalIgnoreCase)
            || itemName.Equals("weapon_knife_t", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return itemName.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
            || itemName.StartsWith("item_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsableBot(CCSPlayerController player)
    {
        return player.IsValid
            && player.IsBot
            && player.PawnIsAlive
            && !player.HasBeenControlledByPlayerThisRound
            && IsReplayControllerSafe(player)
            && !IsReplayPawnTakenByController(player)
            && (player.Team == CsTeam.Terrorist || player.Team == CsTeam.CounterTerrorist)
            && player.PlayerPawn.Value is { IsValid: true };
    }

    private static bool IsReplayControllerSafe(CCSPlayerController player)
    {
        try
        {
            return !player.ControllingBot;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsReplayPawnTakenByController(CCSPlayerController replayTarget)
    {
        if (replayTarget.PlayerPawn is not { IsValid: true, Value.IsValid: true } replayPawn)
        {
            return true;
        }

        var replayPawnIndex = replayPawn.Value.Index;
        foreach (var controller in Utilities.GetPlayers())
        {
            if (controller is not { IsValid: true } || controller.Slot == replayTarget.Slot)
            {
                continue;
            }

            if (!TryGetControllingBotState(controller, out var controllingBot) || !controllingBot)
            {
                continue;
            }

            if (controller.PlayerPawn is { IsValid: true, Value.IsValid: true } controlledPawn
                && controlledPawn.Value.Index == replayPawnIndex)
            {
                return true;
            }

            if (controller.OriginalControllerOfCurrentPawn is { IsValid: true, Value.IsValid: true } original
                && original.Value.Slot == replayTarget.Slot)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetControllingBotState(CCSPlayerController player, out bool controllingBot)
    {
        controllingBot = false;
        if (player is not { IsValid: true })
        {
            return false;
        }

        try
        {
            controllingBot = player.ControllingBot;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNativeBuySuppressionTarget(CCSPlayerController player)
    {
        return player.IsValid
            && player.IsBot
            && player.Slot >= 0
            && !player.HasBeenControlledByPlayerThisRound
            && (player.Team == CsTeam.Terrorist || player.Team == CsTeam.CounterTerrorist);
    }

    private static bool IsRoundBudgetOwner(CCSPlayerController player)
    {
        return player.IsValid
            && player.PawnIsAlive
            && (player.Team == CsTeam.Terrorist || player.Team == CsTeam.CounterTerrorist)
            && player.PlayerPawn.Value is { IsValid: true };
    }

    private static bool IsLiveEnemy(CCSPlayerController player, CCSPlayerController candidate)
    {
        return candidate.IsValid
            && candidate.PawnIsAlive
            && candidate.Team != player.Team
            && (candidate.Team == CsTeam.Terrorist || candidate.Team == CsTeam.CounterTerrorist)
            && candidate.PlayerPawn.Value is { IsValid: true };
    }

    private static bool IsBotFlashed(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
        {
            return false;
        }

        return pawn.BlindUntilTime > Server.CurrentTime || (pawn.FlashDuration > 0.05f && pawn.FlashMaxAlpha > 32f);
    }

    private static int PlayerKey(CCSPlayerController player)
    {
        return player.UserId ?? (int)player.Index;
    }

    private static CCSGameRules? GameRules()
    {
        return Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules;
    }

    private static bool IsWarmupPeriod()
    {
        return GameRules()?.WarmupPeriod == true;
    }

    private static bool IsFreezePeriod()
    {
        return GameRules()?.FreezePeriod == true;
    }

    private int CurrentLiveFreezeTicks(ReplayPlayer replayPlayer)
    {
        var liveFreezeSeconds = CurrentLiveFreezeSeconds(ReplayFreezeSeconds(replayPlayer));
        return Math.Clamp((int)MathF.Round(liveFreezeSeconds * ReplayTickRate()), 0, replayPlayer.FreezeEndTickIndex);
    }

    private float CurrentLiveFreezeSeconds(float fallback)
    {
        var convarFreezeSeconds = ConVarNumber("mp_freezetime", 0f);
        var gameRulesFreezeSeconds = CurrentGameRulesFreezeSeconds(Math.Max(fallback, convarFreezeSeconds));
        if (gameRulesFreezeSeconds > 0.001f)
        {
            return gameRulesFreezeSeconds;
        }

        var liveFreezeSeconds = convarFreezeSeconds;
        if (liveFreezeSeconds <= 0.001f)
        {
            liveFreezeSeconds = fallback;
        }

        return Math.Max(0f, liveFreezeSeconds);
    }

    private static float CurrentLiveFreezeEndTime()
    {
        var gameRules = GameRules();
        if (gameRules == null)
        {
            return -1f;
        }

        const float maxReasonableRemainingSeconds = 90f;
        var now = Server.CurrentTime;

        try
        {
            var remaining = NormalizePhaseRemainingSeconds(gameRules.TimeUntilNextPhaseStarts, now);
            if (remaining > 0.001f && remaining <= maxReasonableRemainingSeconds)
            {
                return now + remaining;
            }
        }
        catch
        {
            // Fall through to RoundStartTime.
        }

        try
        {
            var roundStartTime = gameRules.RoundStartTime;
            var remaining = roundStartTime - now;
            if (remaining > 0.001f && remaining <= maxReasonableRemainingSeconds)
            {
                return roundStartTime;
            }
        }
        catch
        {
            return -1f;
        }

        return -1f;
    }

    private static bool TryReadFreezePhaseRemainingSeconds(out float seconds)
    {
        seconds = 0f;
        var gameRules = GameRules();
        if (gameRules == null)
        {
            return false;
        }

        try
        {
            seconds = NormalizePhaseRemainingSeconds(gameRules.TimeUntilNextPhaseStarts, Server.CurrentTime);
            return seconds > 0f && seconds <= 90f;
        }
        catch
        {
            return false;
        }
    }

    private float CurrentGameRulesFreezeSeconds(float referenceFreezeSeconds)
    {
        var gameRules = GameRules();
        if (gameRules == null)
        {
            return 0f;
        }

        const float maxReasonableFreezeSeconds = 90f;
        var candidates = new List<float>();
        var freezeTime = 0;
        try
        {
            freezeTime = gameRules.FreezeTime;
            if (freezeTime > 0)
            {
                referenceFreezeSeconds = Math.Max(referenceFreezeSeconds, freezeTime);
                candidates.Add(freezeTime);
            }
        }
        catch
        {
            // Fall through to timestamp/remaining-time checks.
        }

        var freezeStart = _freezePeriodStartTime;
        if (freezeStart >= 0f)
        {
            var now = Server.CurrentTime;
            var elapsed = Math.Max(0f, now - freezeStart);

            try
            {
                var remaining = NormalizePhaseRemainingSeconds(gameRules.TimeUntilNextPhaseStarts, now);
                var total = elapsed + remaining;
                var maxExpectedRemaining = Math.Max(10f, referenceFreezeSeconds + 10f);
                if (remaining > 0.001f
                    && remaining <= maxExpectedRemaining
                    && total > 0.001f
                    && total <= maxReasonableFreezeSeconds)
                {
                    candidates.Add(total);
                }
            }
            catch
            {
                // Older CSS builds may expose fewer game-rule fields.
            }

            if (candidates.Count > 0)
            {
                return candidates.Max();
            }

            try
            {
                var roundStartTime = gameRules.RoundStartTime;
                var total = roundStartTime - freezeStart;
                if (total > 0.001f && total <= maxReasonableFreezeSeconds)
                {
                    return total;
                }
            }
            catch
            {
                // Fall through to convar/fallback.
            }
        }

        return 0f;
    }

    private static float NormalizePhaseRemainingSeconds(float phaseTime, float now)
    {
        if (!float.IsFinite(phaseTime))
        {
            return 0f;
        }

        return phaseTime > now ? phaseTime - now : phaseTime;
    }

    private static float ConVarNumber(string name, float fallback)
    {
        var convar = ConVar.Find(name);
        if (convar == null)
        {
            return fallback;
        }

        try
        {
            return convar.GetPrimitiveValue<int>();
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            return convar.GetPrimitiveValue<float>();
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private float ReplayFreezeSeconds(ReplayPlayer replayPlayer)
        => replayPlayer.FreezeEndTime > 0.001f
            ? replayPlayer.FreezeEndTime
            : replayPlayer.FreezeEndTickIndex / (float)ReplayTickRate();

    private static void Reply(CCSPlayerController? player, CommandInfo commandInfo, string message)
    {
        if (player is { IsValid: true })
        {
            player.PrintToChat($"{ChatColors.Green}[ProReplay]{ChatColors.Default} {message}");
            return;
        }

        commandInfo.ReplyToCommand($"[ProReplay] {message}");
    }
}

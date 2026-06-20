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
    private void PrepareRound(bool scheduleLoadout = true)
    {
        if (!CanUseDataset() || _dataset == null)
        {
            return;
        }

        if (_loadoutAppliedKeys.Count == 0)
        {
            CaptureRoundLoadoutBudgets();
        }

        var playersByTeam = Utilities.GetPlayers()
            .Where(IsUsableBot)
            .GroupBy(player => player.Team)
            .ToDictionary(group => group.Key, group => group.OrderBy(PlayerKey).ToList());

        _pendingAssignments.Clear();

        foreach (var team in new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist })
        {
            if (!playersByTeam.TryGetValue(team, out var bots) || bots.Count == 0)
            {
                continue;
            }

            var assignments = BuildAssignmentsForTeam(team, bots);
            foreach (var botAssignment in assignments)
            {
                var assignment = new ReplayAssignment(botAssignment.Round, botAssignment.Player, botAssignment.Budget);
                _pendingAssignments[PlayerKey(botAssignment.Bot)] = assignment;
            }
        }

        _roundPrepared = _pendingAssignments.Count > 0;
        if (_roundPrepared)
        {
            ApplyNativeBuySuppressionForPendingAssignments();
        }
        else
        {
            ClearNativeBuySuppression();
        }
        PrepareOpeningSessionsForPendingAssignments();

        if (_roundPrepared)
        {
            StartReplaySessions();
        }
    }

    private bool AssignmentsCoverCurrentBots()
    {
        var keys = Utilities.GetPlayers()
            .Where(IsUsableBot)
            .Select(PlayerKey)
            .ToList();

        return keys.Count > 0 && keys.All(key => _pendingAssignments.ContainsKey(key));
    }

    private bool OpeningSessionsCoverCurrentBots()
    {
        var activeKeys = _sessions
            .Where(session => session.Kind == ReplaySessionKind.Opening)
            .Select(session => PlayerKey(session.Player))
            .ToHashSet();
        var keys = Utilities.GetPlayers()
            .Where(IsUsableBot)
            .Select(PlayerKey)
            .ToList();

        return keys.Count > 0 && keys.All(key => activeKeys.Contains(key));
    }

    private void ApplyLoadoutsForPendingAssignments(bool allowAfterFreezeEnd = false)
    {
        if (_freezeEnded && !allowAfterFreezeEnd)
        {
            return;
        }

        if (_pendingAssignments.Count == 0)
        {
            return;
        }

        var assignmentsByTeam = new Dictionary<CsTeam, List<BotReplayAssignment>>();
        foreach (var player in Utilities.GetPlayers().Where(IsUsableBot))
        {
            var key = PlayerKey(player);
            if (_loadoutAppliedKeys.Contains(key))
            {
                continue;
            }

            if (!_pendingAssignments.TryGetValue(key, out var assignment))
            {
                continue;
            }

            if (!assignmentsByTeam.TryGetValue(player.Team, out var list))
            {
                list = [];
                assignmentsByTeam[player.Team] = list;
            }

            list.Add(new BotReplayAssignment(player, assignment.Round, assignment.Player, assignment.Budget));
        }

        var appliedAny = false;
        foreach (var (_, assignments) in assignmentsByTeam)
        {
            appliedAny |= ApplyTeamLoadouts(assignments) > 0;
        }

        if (appliedAny)
        {
            RemoveUnownedReplayWeapons();
        }

        PreloadPreparedOpeningReplayWeapons(allowInventoryMutation: true);
    }

    private void ApplyNativeBuySuppressionForPendingAssignments()
    {
        ApplyNativeBuySuppressionForCurrentBots();
    }

    private void ApplyNativeBuySuppressionForCurrentBots()
    {
        if (!_config.SuppressNativeBotBuying || !_nativeReplayAvailable)
        {
            return;
        }

        IEnumerable<CCSPlayerController> players;
        try
        {
            players = Utilities.GetPlayers();
        }
        catch (NativeException)
        {
            return;
        }

        foreach (var player in players.Where(IsNativeBuySuppressionTarget))
        {
            BotController.SetBuySkip(player.Slot);
        }
    }

    private void ClearNativeBuySuppression()
    {
        if (_nativeReplayAvailable)
        {
            BotController.ClearAllBuyPlans();
        }
    }

    private void CaptureRoundLoadoutBudgets()
    {
        _roundLoadoutBudgets.Clear();
        var budgetOwners = Utilities.GetPlayers()
            .Where(IsRoundBudgetOwner)
            .ToList();

        foreach (var player in budgetOwners.Where(player => player.IsBot))
        {
            var money = player.InGameMoneyServices?.Account ?? 0;
            _roundLoadoutBudgets[PlayerKey(player)] = RoundMoneyDown(money + EstimateCurrentBudgetEquipment(player).TotalValue);
        }

        AddNearestGroundWeaponBudgets(budgetOwners);
    }

    private void AddNearestGroundWeaponBudgets(List<CCSPlayerController> budgetOwners)
    {
        if (budgetOwners.Count == 0)
        {
            return;
        }

        foreach (var itemName in ItemPrices.Keys.Where(name => name.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)))
        {
            if (!IsBudgetWeapon(itemName))
            {
                continue;
            }

            foreach (var weapon in Utilities.FindAllEntitiesByDesignerName<CBasePlayerWeapon>(itemName))
            {
                if (weapon == null || !weapon.IsValid || weapon.OwnerEntity.IsValid || weapon.AbsOrigin == null)
                {
                    continue;
                }

                var nearest = FindNearestBudgetOwner(weapon.AbsOrigin, budgetOwners);
                if (nearest is not { IsValid: true, IsBot: true })
                {
                    continue;
                }

                var key = PlayerKey(nearest);
                if (!_roundLoadoutBudgets.ContainsKey(key))
                {
                    continue;
                }

                _roundLoadoutBudgets[key] = RoundMoneyDown(_roundLoadoutBudgets[key] + BudgetItemValue(itemName));
            }
        }
    }

    private static CCSPlayerController? FindNearestBudgetOwner(Vector origin, List<CCSPlayerController> players)
    {
        CCSPlayerController? nearest = null;
        var bestDistance = float.MaxValue;
        foreach (var player in players)
        {
            var pawnOrigin = player.PlayerPawn.Value?.AbsOrigin;
            if (pawnOrigin == null)
            {
                continue;
            }

            var distance = DistanceSquared(origin, pawnOrigin);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            nearest = player;
        }

        return nearest;
    }

    private List<BotReplayAssignment> BuildAssignmentsForTeam(CsTeam team, List<CCSPlayerController> bots)
    {
        if (!_spawnIndexes.TryGetValue(team, out var index))
        {
            return [];
        }

        var botSpawns = GetBotSpawns(bots);
        if (botSpawns.Count != bots.Count)
        {
            return [];
        }

        var humanSpawns = GetHumanOccupiedSpawns();
        var humanSpawnBlockRadius = index.EffectiveHumanSpawnBlockRadius(_config.HumanSpawnBlockRadius);
        var currentEconomy = GetCurrentTeamEconomy(bots);
        var currentIsPistolRound = IsPistolRoundEconomy(currentEconomy);
        var assignments = index.SelectTeamAssignments(
            botSpawns,
            humanSpawns,
            humanSpawnBlockRadius,
            _config.EnforcePistolRoundMatching,
            currentIsPistolRound,
            _random);

        assignments ??= index.SelectMixedAssignments(
            botSpawns,
            humanSpawns,
            humanSpawnBlockRadius,
            _config.EnforcePistolRoundMatching,
            currentIsPistolRound,
            _random);

        return assignments ?? [];
    }

    private static bool IsPistolRoundEconomy(TeamEconomyState state)
    {
        // Pistol round signature: starting balance ~$800/player, no primary weapons, no armor pre-bought.
        // We check the per-bot averages so this works whether we read mid-buy or post-buy.
        if (state.PlayerCount == 0) return false;
        return state.AverageCash + (state.TotalEquipmentValue / state.PlayerCount) <= 1100
            && state.TotalPrimaryValue == 0;
    }

    private List<BotSpawn> GetBotSpawns(List<CCSPlayerController> bots)
    {
        var botSpawns = new List<BotSpawn>(bots.Count);
        foreach (var bot in bots)
        {
            var origin = bot.PlayerPawn.Value?.AbsOrigin;
            if (origin == null)
            {
                continue;
            }

            var budget = RoundMoneyDown(_roundLoadoutBudgets.GetValueOrDefault(PlayerKey(bot), bot.InGameMoneyServices?.Account ?? 0));
            botSpawns.Add(new BotSpawn(bot, new SpawnPosition(origin.X, origin.Y, origin.Z), budget));
        }

        return botSpawns;
    }

    private static List<SpawnPosition> GetHumanOccupiedSpawns()
    {
        return Utilities.GetPlayers()
            .Where(player => player.IsValid
                && !player.IsBot
                && !player.IsHLTV
                && player.PawnIsAlive
                && (player.Team == CsTeam.Terrorist || player.Team == CsTeam.CounterTerrorist)
                && player.PlayerPawn.Value is { IsValid: true }
                && player.PlayerPawn.Value.AbsOrigin != null)
            .Select(player =>
            {
                var origin = player.PlayerPawn.Value!.AbsOrigin!;
                return new SpawnPosition(origin.X, origin.Y, origin.Z);
            })
            .ToList();
    }

    private void StartReplaySessions()
    {
        var existingOpeningKeys = _sessions
            .Where(session => session.Kind == ReplaySessionKind.Opening)
            .Select(session => PlayerKey(session.Player))
            .ToHashSet();
        if (existingOpeningKeys.Count > 0 && OpeningSessionsCoverCurrentBots())
        {
            return;
        }

        if (existingOpeningKeys.Count == 0 && TryScheduleOpeningPrerollStart())
        {
            return;
        }

        if (existingOpeningKeys.Count == 0)
        {
            StopAllNativeReplays();
            _sessions.Clear();
        }
        var startTime = Server.CurrentTime;

        foreach (var player in Utilities.GetPlayers().Where(IsUsableBot))
        {
            var key = PlayerKey(player);
            if (existingOpeningKeys.Contains(key))
            {
                continue;
            }

            if (!_pendingAssignments.TryGetValue(key, out var assignment))
            {
                continue;
            }

            var prepared = _preparedOpeningSessions.TryGetValue(key, out var preparedSession)
                && IsPreparedForAssignment(preparedSession, assignment)
                ? preparedSession
                : null;
            var frames = prepared?.Frames ?? BuildSessionFrames(assignment.Player, ReplaySessionKind.Opening);
            var nativeStartTick = NativeReplayStartTick(assignment.Player, ReplaySessionKind.Opening);
            var frameTimeOffset = ReplayTimeOffset(nativeStartTick);

            if (frames.Count == 0)
            {
                continue;
            }

            var grenades = BuildSessionGrenades(assignment.Player, ReplaySessionKind.Opening, frameTimeOffset);

            var session = new ReplaySession(
                player,
                assignment.Round,
                assignment.Player,
                frames,
                grenades,
                startTime,
                frameTimeOffset: frameTimeOffset,
                nativeReplayStartTick: nativeStartTick);
            if (prepared != null)
            {
                session.NativeReplayPreloaded = prepared.NativeReplayPreloaded;
                session.ReplayWeaponsPreloaded = prepared.ReplayWeaponsPreloaded;
            }
            if (!TryStartNativeReplay(session))
            {
                continue;
            }
            ApplyReplayBudgetMoney(player, assignment);
            ApplyReplaySideEffects(session);
            Server.NextFrame(() => EnsurePrimaryOrSecondaryFallback(player));

            // Signal to other plugins (NadeSystem, BotState) that this bot is under replay control.
            // Written once per session start (not per tick) to avoid the client crash.
            var botCtrl = player.PlayerPawn.Value?.Bot;
            if (botCtrl != null)
                botCtrl.InhibitLookAroundTimestamp = startTime + 130f;

            _sessions.Add(session);
        }

    }

    private bool TryScheduleOpeningPrerollStart()
    {
        if (_freezeEnded || _roundStartTime < 0f || _pendingAssignments.Count == 0)
        {
            return false;
        }

        if (!_config.AlignOpeningFreezeEnd || !BotController.SupportsBoundedReplay)
        {
            return false;
        }

        if (_freezePeriodStartTime < 0f)
        {
            if (IsFreezePeriod())
            {
                _freezePeriodStartTime = Server.CurrentTime;
            }
            else
            {
                QueueOpeningReplayStartRetry(0.05f);
                return true;
            }
        }

        if (!TryGetOpeningPrerollSchedule(out var prerollSeconds, out var delay, out _))
        {
            return false;
        }

        QueueOpeningPrerollStart(delay, prerollSeconds);
        return true;
    }

    private void QueueOpeningReplayStartRetry(float delay)
    {
        if (_openingReplayStartQueued)
        {
            return;
        }

        _openingReplayStartQueued = true;
        AddTimer(Math.Max(0.01f, delay), () =>
        {
            _openingReplayStartQueued = false;
            if (!_freezeEnded && CanUseDataset())
            {
                StartReplaySessions();
            }
        });
    }

    private void QueueOpeningPrerollStart(float delay, float prerollSeconds)
    {
        if (_openingReplayStartQueued)
        {
            return;
        }

        _openingReplayStartQueued = true;
        AddTimer(Math.Max(0.01f, delay), () =>
        {
            _openingReplayStartQueued = false;
            if (!_freezeEnded && CanUseDataset())
            {
                StartOpeningPrerollSessions(prerollSeconds);
            }
        });
    }

    private bool TryGetOpeningPrerollSchedule(out float prerollSeconds, out float delaySeconds, out string reason)
    {
        prerollSeconds = 0f;
        delaySeconds = 0f;
        if (!_config.AlignOpeningFreezeEnd)
        {
            reason = "freeze alignment disabled";
            return false;
        }

        var maxReplayFreezeSeconds = _pendingAssignments.Values
            .Select(assignment => ReplayFreezeSeconds(assignment.Player))
            .DefaultIfEmpty(0f)
            .Max();
        if (maxReplayFreezeSeconds <= 0.001f)
        {
            reason = "assigned replays have no freeze preroll";
            return false;
        }

        var liveFreezeSeconds = CurrentLiveFreezeSeconds(maxReplayFreezeSeconds);
        var playbackPrerollSeconds = Math.Min(liveFreezeSeconds, maxReplayFreezeSeconds);
        if (playbackPrerollSeconds <= 0.001f)
        {
            reason = "computed preroll window is empty";
            return false;
        }

        var scheduleWindowSeconds = liveFreezeSeconds;
        if (TryReadFreezePhaseRemainingSeconds(out var phaseRemainingSeconds)
            && phaseRemainingSeconds > 0.001f)
        {
            scheduleWindowSeconds = phaseRemainingSeconds;
        }

        playbackPrerollSeconds = Math.Min(playbackPrerollSeconds, scheduleWindowSeconds);
        if (playbackPrerollSeconds <= 0.001f)
        {
            reason = "remaining freeze window is empty";
            return false;
        }

        prerollSeconds = playbackPrerollSeconds;
        delaySeconds = Math.Max(0f, scheduleWindowSeconds - playbackPrerollSeconds);
        reason = string.Empty;
        return true;
    }

    private void StartOpeningPrerollSessions(float prerollSeconds)
    {
        if (_freezeEnded || _pendingAssignments.Count == 0)
        {
            return;
        }

        StopAllNativeReplays();
        _sessions.Clear();
        var startTime = Server.CurrentTime;

        foreach (var player in Utilities.GetPlayers().Where(IsUsableBot))
        {
            var key = PlayerKey(player);
            if (!_pendingAssignments.TryGetValue(key, out var assignment))
            {
                continue;
            }

            var prepared = _preparedOpeningSessions.TryGetValue(key, out var preparedSession)
                && IsPreparedForAssignment(preparedSession, assignment)
                ? preparedSession
                : null;
            var frames = prepared?.Frames ?? BuildSessionFrames(assignment.Player, ReplaySessionKind.Opening);
            var holdBeforeTick = Math.Max(0, assignment.Player.FreezeEndTickIndex);
            var nativeStartTick = OpeningPrerollStartTick(assignment.Player, prerollSeconds);
            if (frames.Count == 0 || holdBeforeTick <= nativeStartTick)
            {
                continue;
            }

            var frameTimeOffset = ReplayTimeOffset(nativeStartTick);
            var grenades = BuildSessionGrenades(assignment.Player, ReplaySessionKind.Opening, frameTimeOffset);
            var session = new ReplaySession(
                player,
                assignment.Round,
                assignment.Player,
                frames,
                grenades,
                startTime,
                frameTimeOffset: frameTimeOffset,
                nativeReplayStartTick: nativeStartTick,
                nativeReplayPrerollActive: true,
                nativeReplayHoldBeforeTick: holdBeforeTick);
            if (prepared != null)
            {
                session.NativeReplayPreloaded = prepared.NativeReplayPreloaded;
                session.ReplayWeaponsPreloaded = prepared.ReplayWeaponsPreloaded;
            }

            if (!TryStartNativeReplay(session))
            {
                continue;
            }

            ApplyReplaySideEffects(session);
            var botCtrl = player.PlayerPawn.Value?.Bot;
            if (botCtrl != null)
            {
                botCtrl.InhibitLookAroundTimestamp = startTime + 130f;
            }

            _sessions.Add(session);
        }
    }

    private void StopOpeningPrerollSessions()
    {
        for (var sessionIndex = _sessions.Count - 1; sessionIndex >= 0; sessionIndex--)
        {
            var session = _sessions[sessionIndex];
            if (session.Kind != ReplaySessionKind.Opening || !session.NativeReplayPrerollActive)
            {
                continue;
            }

            var slot = session.NativeReplaySlot;
            StopNativeReplay(session);
            if (slot >= 0)
            {
                ClearNativeWeaponState(slot);
            }
            _sessions.RemoveAt(sessionIndex);
        }
    }

    private int OpeningPrerollStartTick(ReplayPlayer replayPlayer, float liveFreezeSeconds)
    {
        var freezeEndTick = Math.Max(0, replayPlayer.FreezeEndTickIndex);
        if (freezeEndTick == 0 || liveFreezeSeconds <= 0.001f)
        {
            return freezeEndTick;
        }

        var liveFreezeTicks = Math.Max(0, (int)MathF.Round(liveFreezeSeconds * ReplayTickRate()));
        return liveFreezeTicks >= freezeEndTick
            ? 0
            : freezeEndTick - liveFreezeTicks;
    }

    private static bool IsPreparedForAssignment(PreparedOpeningSession prepared, ReplayAssignment assignment)
        => ReferenceEquals(prepared.Round, assignment.Round)
            && ReferenceEquals(prepared.ReplayPlayer, assignment.Player);
}

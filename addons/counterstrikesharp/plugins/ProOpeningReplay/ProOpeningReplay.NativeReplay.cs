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
    private ReplayRound? SelectRoundForTeam(CsTeam team, List<CCSPlayerController> bots)
    {
        if (_dataset == null || !_roundIndexes.TryGetValue(team, out var index))
        {
            return null;
        }

        return index.SelectClosest(GetCurrentTeamEconomy(bots), _random);
    }

    private static TeamEconomyState GetCurrentTeamEconomy(List<CCSPlayerController> bots)
    {
        var totalCash = 0;
        var totalEquipment = 0;
        var totalPrimary = 0;
        var totalUtility = 0;
        var totalArmor = 0;

        foreach (var bot in bots)
        {
            totalCash += bot.InGameMoneyServices?.Account ?? 0;
            var values = EstimateCurrentEquipment(bot);
            totalEquipment += values.TotalValue;
            totalPrimary += values.PrimaryValue;
            totalUtility += values.UtilityValue;
            totalArmor += values.ArmorValue;
        }

        return new TeamEconomyState(
            bots.Count,
            totalCash,
            bots.Count == 0 ? 0 : totalCash / bots.Count,
            totalEquipment,
            totalPrimary,
            totalUtility,
            totalArmor);
    }

    private void PrepareOpeningSessionsForPendingAssignments()
    {
        _preparedOpeningSessions.Clear();
        if (_pendingAssignments.Count == 0)
        {
            return;
        }

        foreach (var player in Utilities.GetPlayers().Where(IsUsableBot))
        {
            if (_pendingAssignments.TryGetValue(PlayerKey(player), out var assignment))
            {
                var frames = BuildSessionFrames(assignment.Player, ReplaySessionKind.Opening);
                if (frames.Count == 0)
                {
                    continue;
                }

                var grenades = BuildSessionGrenades(assignment.Player, ReplaySessionKind.Opening);
                var nativePreloaded = false;
                _preparedOpeningSessions[PlayerKey(player)] = new PreparedOpeningSession(
                    assignment.Round,
                    assignment.Player,
                    frames,
                    grenades,
                    nativePreloaded);
            }
        }
    }

    private void PreloadPreparedOpeningReplayWeapons(bool allowInventoryMutation)
    {
        if (_preparedOpeningSessions.Count == 0)
        {
            return;
        }

        foreach (var player in Utilities.GetPlayers().Where(IsUsableBot))
        {
            var key = PlayerKey(player);
            if (!_preparedOpeningSessions.TryGetValue(key, out var prepared)
                || prepared.ReplayWeaponsPreloaded)
            {
                continue;
            }

            prepared.ReplayWeaponsPreloaded = PreloadReplayWeapons(
                player,
                prepared.ReplayPlayer,
                prepared.Frames,
                ReplaySessionKind.Opening,
                allowInventoryMutation);
        }
    }

    private bool PreloadNativeReplayBuffer(CCSPlayerController player, ReplayPlayer replayPlayer, ReplaySessionKind kind)
    {
        var slot = player.Slot;
        if (slot < 0 || slot >= 64)
        {
            return false;
        }

        var replayPath = ResolveReplayPath(replayPlayer, kind);
        if (string.IsNullOrWhiteSpace(replayPath) || !File.Exists(replayPath))
        {
            _nativeReplayPreloadKeys.Remove(slot);
            return false;
        }

        var replayKey = ReplayKeyForKind(replayPlayer, kind);
        var loadKey = NativeReplayLoadKey(replayPath, replayKey, _config.SuppressReplayAttackInput);
        if (_nativeReplayPreloadKeys.TryGetValue(slot, out var existing) && existing == loadKey)
        {
            return true;
        }

        if (!BotController.LoadReplayFromFile(slot, replayPath, 0, _config.SuppressReplayAttackInput, replayKey))
        {
            _nativeReplayPreloadKeys.Remove(slot);
            return false;
        }

        _nativeReplayPreloadKeys[slot] = loadKey;
        return true;
    }

    private static string NativeReplayLoadKey(string replayPath, string replayKey, bool suppressAttackInput)
        => $"{Path.GetFullPath(replayPath)}\n{replayKey}\n{suppressAttackInput}";

    private bool TryStartNativeReplay(ReplaySession session)
    {
        if (!_nativeReplayAvailable)
        {
            return false;
        }

        var slot = session.Player.Slot;
        if (slot < 0 || slot >= 64)
        {
            return false;
        }

        if (session.Kind == ReplaySessionKind.Retake && !session.ReplayWeaponsPreloaded)
        {
            session.ReplayWeaponsPreloaded = PreloadReplayWeapons(
                session.Player,
                session.ReplayPlayer,
                session.Frames,
                session.Kind,
                allowInventoryMutation: true);
        }

        if (!session.NativeReplayPreloaded
            && !PreloadNativeReplayBuffer(session.Player, session.ReplayPlayer, session.Kind))
        {
            return false;
        }
        session.NativeReplayPreloaded = true;

        var started = session.NativeReplayPrerollActive
            ? BotController.StartReplayUntil(slot, session.NativeReplayStartTick, session.NativeReplayHoldBeforeTick)
            : BotController.StartReplayAt(slot, session.NativeReplayStartTick);
        if (!started)
        {
            return false;
        }

        session.NativeReplayActive = true;
        session.NativeReplaySlot = slot;
        session.NativeReplayTickCount = BotController.GetReplayTotal(slot);
        session.NativeReplayLastCursor = -1;
        session.NativeReplayStallTicks = 0;
        session.NativeReplayDiagnosticLogged = false;
        ApplyOpeningReplayInitialPlacement(session);
        return true;
    }

    private void ApplyOpeningReplayInitialPlacement(ReplaySession session)
    {
        if (session.Kind != ReplaySessionKind.Opening || _freezeEnded)
        {
            return;
        }

        var pawn = session.Player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || !BotController.TryGetReplayTick(session.NativeReplaySlot, out var tick))
        {
            return;
        }

        var snapshot = tick.Pre;
        pawn.Teleport(
            new Vector(snapshot.OriginX, snapshot.OriginY, snapshot.OriginZ),
            new QAngle(0f, snapshot.Yaw, 0f),
            new Vector(snapshot.VelX, snapshot.VelY, snapshot.VelZ));
        pawn.EyeAngles.X = Math.Clamp(snapshot.Pitch, -89f, 89f);
        pawn.EyeAngles.Y = snapshot.Yaw;
        pawn.EyeAngles.Z = 0f;
        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_angEyeAngles");
    }

    private string ResolveReplayPath(ReplaySession session)
        => ResolveReplayPath(session.ReplayPlayer, session.Kind);

    private string ResolveReplayPath(ReplayPlayer replayPlayer, ReplaySessionKind kind)
    {
        var relativeOrAbsolute = ReplayPathForKind(replayPlayer, kind);
        if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(relativeOrAbsolute))
        {
            return relativeOrAbsolute;
        }

        var baseDirectory = string.IsNullOrWhiteSpace(_dataset?.BaseDirectory)
            ? ModuleDirectory
            : _dataset.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDirectory, relativeOrAbsolute.Replace('/', Path.DirectorySeparatorChar)));
    }

}

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
    private void StartRetakeSessions()
    {
        if (!_config.Enabled || _dataset == null || _dataset.Rounds.Count == 0)
        {
            return;
        }
        if (!CanUseDataset())
        {
            return;
        }

        ClearRetakeMoveTos(releaseBots: false);
        var startTime = Server.CurrentTime;
        var startedCount = 0;

        // Use the candidate pools precomputed in BuildRoundIndexes (when the dataset loaded).
        // Building them per-plant scanned every round + every player's full frame list and caused a
        // multi-100ms hitch on the main thread right when the round transitioned into post-plant.
        var ctCandidatesAll = _ctRetakeCandidates;
        var tCandidatesAll = _tRetakeCandidates;

        // Restrict the candidate pools to rounds where the pro plant happened at the SAME bombsite
        // as the current live plant. Uses dataset-derived centroids (k-means on PlantPos) to classify
        // each candidate and the live bomb into site clusters. This works for vertically-stacked sites
        // like de_nuke where simple distance thresholds fail.
        List<RetakeCandidate> ctCandidates;
        List<RetakeCandidate> tCandidates;
        var currentSiteIndex = ClassifyBySiteCentroids(_bombPos);
        if (currentSiteIndex >= 0 && _datasetSiteCentroids.Count >= 2)
        {
            ctCandidates = new List<RetakeCandidate>(ctCandidatesAll.Count);
            tCandidates = new List<RetakeCandidate>(tCandidatesAll.Count);
            foreach (var c in ctCandidatesAll)
            {
                if (ClassifyCandidateByCentroids(c) == currentSiteIndex) ctCandidates.Add(c);
            }
            foreach (var c in tCandidatesAll)
            {
                if (ClassifyCandidateByCentroids(c) == currentSiteIndex) tCandidates.Add(c);
            }
            // If site-specific filtering yields nothing (e.g. dataset doesn't have PlantPos for
            // this site), fall back to the full pool rather than skipping retake entirely.
            if (ctCandidates.Count == 0 && tCandidates.Count == 0)
            {
                ctCandidates = ctCandidatesAll;
                tCandidates = tCandidatesAll;
            }
        }
        else
        {
            ctCandidates = ctCandidatesAll;
            tCandidates = tCandidatesAll;
        }
        if (ctCandidates.Count == 0 && tCandidates.Count == 0)
        {
            return;
        }

        // Track which (Round, ProPlayer) candidates have already been handed out so two bots near
        // each other don't both pick the same pro path and end up walking in lockstep. We dedup at
        // the (Round, ProPlayer) level rather than the candidate-instance level just to be safe;
        // each pro player only contributes one candidate per round so they're equivalent here.
        var usedCandidates = new HashSet<(string, string)>();

        // Each bot picks the closest *unused* same-team candidate. Greedy nearest-neighbor: the bots\n        // we encounter first get the best matches, but with thousands of candidates per side that's\n        // rarely a meaningful difference.
        foreach (var player in Utilities.GetPlayers().Where(IsUsableBot))
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || pawn.AbsOrigin == null) continue;
            var pool = player.Team == CsTeam.CounterTerrorist ? ctCandidates : tCandidates;
            if (pool.Count == 0)
            {
                continue;
            }

            RetakeCandidate? best = null;
            float bestDistSq = float.MaxValue;
            foreach (var candidate in pool)
            {
                var key = (candidate.Round.Id, candidate.ProPlayer.SteamId);
                if (usedCandidates.Contains(key)) continue;
                var dx = candidate.StartFrame.X - pawn.AbsOrigin.X;
                var dy = candidate.StartFrame.Y - pawn.AbsOrigin.Y;
                var dz = candidate.StartFrame.Z - pawn.AbsOrigin.Z;
                var distSq = dx * dx + dy * dy + dz * dz;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = candidate;
                }
            }
            if (best == null) continue;
            usedCandidates.Add((best.Round.Id, best.ProPlayer.SteamId));

            var frames = BuildSessionFrames(best.ProPlayer, ReplaySessionKind.Retake);
            if (frames.Count == 0)
            {
                continue;
            }

            var target = new Vector(best.StartFrame.X, best.StartFrame.Y, best.StartFrame.Z);
            var moveTo = new RetakeMoveToSession(player, best.Round, best.ProPlayer, frames, target, startTime);
            if (IsAtRetakeMoveTarget(player, moveTo))
            {
                if (StartRetakeReplayFromMoveTo(moveTo))
                {
                    startedCount++;
                }
                continue;
            }

            if (!TryIssueRetakeMoveTo(moveTo))
            {
                continue;
            }
            _retakeMoveTos.Add(moveTo);
            startedCount++;
        }

    }

    private static List<ReplayFrame> BuildSessionFrames(ReplayPlayer player, ReplaySessionKind kind)
    {
        var start = kind == ReplaySessionKind.Retake ? player.RetakeStartFrame : player.StartFrame;
        var end = kind == ReplaySessionKind.Retake ? player.RetakeEndFrame : player.EndFrame;
        var duration = kind == ReplaySessionKind.Retake ? player.RetakeDuration : player.Duration;
        if (start == null || string.IsNullOrWhiteSpace(ReplayPathForKind(player, kind)))
        {
            return [];
        }

        var first = start.CloneAtTime(0f);
        var frames = new List<ReplayFrame> { first };
        if (end != null && duration > 0.001f)
        {
            frames.Add(end.CloneAtTime(duration));
        }
        return frames;
    }

    private static string ReplayPathForKind(ReplayPlayer player, ReplaySessionKind kind)
    {
        if (kind == ReplaySessionKind.Retake && !string.IsNullOrWhiteSpace(player.RetakeRecPath))
        {
            return player.RetakeRecPath;
        }
        return player.RecPath;
    }

    private static string ReplayKeyForKind(ReplayPlayer player, ReplaySessionKind kind)
    {
        if (kind == ReplaySessionKind.Retake && !string.IsNullOrWhiteSpace(player.RetakeRecKey))
        {
            return player.RetakeRecKey;
        }

        return player.RecKey;
    }

    private static List<ReplayGrenade> BuildSessionGrenades(ReplayPlayer player, ReplaySessionKind kind, float frameTimeOffset = 0f)
    {
        if (kind != ReplaySessionKind.Retake)
        {
            var freezeEndTime = Math.Max(0f, player.FreezeEndTime);
            return player.Grenades
                .Select(grenade => CloneGrenadeAtSessionTime(grenade, frameTimeOffset - freezeEndTime, 0))
                .OrderBy(grenade => grenade.Time)
                .ToList();
        }

        var startTime = Math.Max(0f, player.RetakeStartTime > 0.001f ? player.RetakeStartTime : player.RetakeStartFrame?.Time ?? 0f);
        var startTick = player.RetakeStartRelativeTick != 0 ? player.RetakeStartRelativeTick : player.RetakeStartFrame?.RelativeTick ?? 0;
        var endTime = player.RetakeDuration > 0.001f ? startTime + player.RetakeDuration : float.MaxValue;
        return player.Grenades
            .Where(grenade => grenade.Time + 0.001f >= startTime && grenade.Time <= endTime + 0.001f)
            .Select(grenade => CloneGrenadeAtSessionTime(grenade, startTime, startTick))
            .OrderBy(grenade => grenade.Time)
            .ToList();
    }

    private static ReplayGrenade CloneGrenadeAtSessionTime(ReplayGrenade grenade, float timeOffset, int tickOffset)
    {
        return new ReplayGrenade
        {
            RelativeTick = Math.Max(0, grenade.RelativeTick - tickOffset),
            Time = Math.Max(0f, grenade.Time - timeOffset),
            Type = grenade.Type,
            X = grenade.X,
            Y = grenade.Y,
            Z = grenade.Z,
            Pitch = grenade.Pitch,
            Yaw = grenade.Yaw,
            VelocityX = grenade.VelocityX,
            VelocityY = grenade.VelocityY,
            VelocityZ = grenade.VelocityZ
        };
    }

    private sealed record RetakeCandidate(ReplayRound Round, ReplayPlayer ProPlayer, ReplayFrame StartFrame);

    // Classify a world position by index of the nearest entry in _bombSiteCenters. Returns -1 if
    // we have no site centers cached or the position is null. Uses full 3D distance to correctly
    // distinguish vertically-stacked sites (e.g. de_nuke A/B share similar XY but differ in Z).
    private int ClassifyPlantSite(Vector? pos)
    {
        if (pos == null || _bombSiteCenters.Count == 0) return -1;
        var bestIndex = -1;
        var bestDistSq = float.MaxValue;
        for (var i = 0; i < _bombSiteCenters.Count; i++)
        {
            var c = _bombSiteCenters[i];
            var dx = c.X - pos.X;
            var dy = c.Y - pos.Y;
            var dz = c.Z - pos.Z;
            var d = dx * dx + dy * dy + dz * dz;
            if (d < bestDistSq) { bestDistSq = d; bestIndex = i; }
        }
        return bestIndex;
    }

    private int CandidatePlantSite(RetakeCandidate c)
    {
        var pp = c.Round.PlantPos;
        if (pp == null || _bombSiteCenters.Count == 0) return -1;
        var bestIndex = -1;
        var bestDistSq = float.MaxValue;
        for (var i = 0; i < _bombSiteCenters.Count; i++)
        {
            var sc = _bombSiteCenters[i];
            var dx = sc.X - pp.X;
            var dy = sc.Y - pp.Y;
            var dz = sc.Z - pp.Z;
            var d = dx * dx + dy * dy + dz * dz;
            if (d < bestDistSq) { bestDistSq = d; bestIndex = i; }
        }
        return bestIndex;
    }

    /// <summary>
    /// Returns true if the candidate's pro plant position is within thresholdSq of the live bomb.
    /// Candidates without PlantPos are excluded (return false) to avoid sending bots to random sites.
    /// </summary>
    private static bool CandidateMatchesBombPos(RetakeCandidate c, Vector bombPos, float thresholdSq)
    {
        var pp = c.Round.PlantPos;
        if (pp == null) return false;
        var dx = pp.X - bombPos.X;
        var dy = pp.Y - bombPos.Y;
        var dz = pp.Z - bombPos.Z;
        return dx * dx + dy * dy + dz * dz < thresholdSq;
    }

    /// <summary>
    /// Classifies a world position by nearest dataset-derived site centroid. Returns -1 if
    /// centroids are not available or pos is null.
    /// </summary>
    private int ClassifyBySiteCentroids(Vector? pos)
    {
        if (pos == null || _datasetSiteCentroids.Count < 2) return -1;
        var bestIndex = -1;
        var bestDistSq = float.MaxValue;
        for (var i = 0; i < _datasetSiteCentroids.Count; i++)
        {
            var c = _datasetSiteCentroids[i];
            var dx = c.X - pos.X;
            var dy = c.Y - pos.Y;
            var dz = c.Z - pos.Z;
            var d = dx * dx + dy * dy + dz * dz;
            if (d < bestDistSq) { bestDistSq = d; bestIndex = i; }
        }
        return bestIndex;
    }

    /// <summary>
    /// Classifies a retake candidate's PlantPos by nearest dataset-derived site centroid.
    /// Returns -1 if the candidate has no PlantPos or centroids aren't available.
    /// </summary>
    private int ClassifyCandidateByCentroids(RetakeCandidate c)
    {
        var pp = c.Round.PlantPos;
        if (pp == null || _datasetSiteCentroids.Count < 2) return -1;
        var bestIndex = -1;
        var bestDistSq = float.MaxValue;
        for (var i = 0; i < _datasetSiteCentroids.Count; i++)
        {
            var sc = _datasetSiteCentroids[i];
            var dx = sc.X - pp.X;
            var dy = sc.Y - pp.Y;
            var dz = sc.Z - pp.Z;
            var d = dx * dx + dy * dy + dz * dz;
            if (d < bestDistSq) { bestDistSq = d; bestIndex = i; }
        }
        return bestIndex;
    }

    private int NativeReplayStartTick(ReplayPlayer replayPlayer, ReplaySessionKind kind)
    {
        if (kind == ReplaySessionKind.Retake)
        {
            return Math.Max(0, replayPlayer.RetakeStartTickIndex);
        }

        if (!_config.AlignOpeningFreezeEnd || replayPlayer.FreezeEndTickIndex <= 0)
        {
            return 0;
        }

        return Math.Max(0, replayPlayer.FreezeEndTickIndex);
    }

    private float ReplayTimeOffset(int startTick)
        => startTick <= 0 ? 0f : startTick / (float)ReplayTickRate();

    private int ReplayTickRate()
        => Math.Max(1, _dataset?.TickRate ?? 64);
}

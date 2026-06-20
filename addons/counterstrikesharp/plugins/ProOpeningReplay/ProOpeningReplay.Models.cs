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

public static class NativeSignatures
{
    public static readonly MemoryFunctionVoid<nint, nint, int> CCSBotMoveTo = new(
        IsLinux
            ? "48 8B 06 48 89 87 E0 02 00 00 8B 46 08 48 8D B7 D8 02 00 00 89 97 EC 02 00 00 89 87 E8 02 00 00 E9 ? ? ? ?"
            : "F2 0F 10 02 F2 0F 11 81 E8 02 00 00 8B 42 08 48 8D 91 E0 02 00 00 89 81 F0 02 00 00 44 89 81 F4 02 00 00 E9 ? ? ? ?");

    private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
}

public sealed class ReplayConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Path template used to locate per-map opening manifests. The literal token {map} is replaced with the
    /// current Server.MapName at load time. Default convention: data/de_inferno_openings_manifest.json,
    /// data/de_dust2_openings_manifest.json, etc. Set to a fully-qualified path to override.
    /// </summary>
    public string DatasetPathTemplate { get; set; } = "data/{map}_openings_manifest.json";
    [Obsolete("Use DatasetPathTemplate.")]
    public string MapName { get; set; } = "";
    [Obsolete("Use DatasetPathTemplate.")]
    public string DatasetPath { get; set; } = "";
    public bool ApplyLoadouts { get; set; } = false;
    public bool SuppressNativeBotBuying { get; set; } = true;
    public bool PreserveUsefulEquipment { get; set; } = true;
    public bool TransferSavedUtility { get; set; } = true;
    /// <summary>
    /// Spawn recorded manifest grenade projectiles at their extracted release time/position/velocity.
    /// Native replay still drives the bot's throw animation and inventory consumption.
    /// </summary>
    public bool ThrowGrenades { get; set; } = true;
    public bool KillNativeReplayGrenadeProjectiles { get; set; } = true;
    public bool StopOnEnemyContact { get; set; } = true;
    public bool StopOpeningReplayWhenSeenByEnemy { get; set; } = true;
    public float EnemySeenHandoffSeconds { get; set; } = 1.0f;
    /// <summary>
    /// Hand off to the bot AI when the bot is flashed. Off by default: pros routinely run through their own pop
    /// flashes, and ending the replay on flash truncated openings noticeably early.
    /// </summary>
    public bool StopOnFlash { get; set; } = false;
    /// <summary>
    /// Hand off when an audible enemy footstep/sound is heard nearby. Off by default for the same reason as flash:
    /// hearing an enemy is not contact, and the engine's spotted-mask check still fires the moment LOS is established.
    /// </summary>
    public bool StopOnAudibleEnemyNoise { get; set; } = false;
    public float SpawnMatchTolerance { get; set; } = 24f;
    public float HumanSpawnBlockRadius { get; set; } = 48f;
    public float MatchSelectionDelay { get; set; } = 0f;
    public float LoadoutApplyDelay { get; set; } = 0f;
    public bool AlignOpeningFreezeEnd { get; set; } = true;
    public float HandoffDistance { get; set; } = 1800f;
    public float HandoffFovDegrees { get; set; } = 90f;
    public float FootstepHandoffDistance { get; set; } = 1150f;
    /// <summary>
    /// Maximum number of each grenade type the bot keeps beyond what the pro actually threw in the replay window.
    /// 0 means buy exactly as many as the pro threw (no leftovers); -1 disables the cap and copies the pro's full inventory.
    /// </summary>
    public int MaxUtilityBeyondThrown { get; set; } = 0;
    /// <summary>
    /// When true, the SelectClosest matcher refuses to assign a non-pistol-round demo to a pistol-round bot loadout.
    /// </summary>
    public bool EnforcePistolRoundMatching { get; set; } = true;
    /// <summary>
    /// During replay, suppress the bot AI's own enemy engagement (so its built-in shooting does not fight our pre-aim).
    /// Hand-off detection still runs in this plugin so it does not affect first-contact responsiveness.
    /// </summary>
    public bool SuppressBotEngagementWhileReplaying { get; set; } = true;

    /// <summary>
    /// Drop non-utility attack inputs from native replay so pro prefire shots do not create sound
    /// events that make other replaying bots hand off on heard-enemy noise. Throwable utility
    /// attack/release inputs are preserved so grenades are thrown by the native replay path.
    /// </summary>
    public bool SuppressReplayAttackInput { get; set; } = true;
    /// <summary>
    /// Pre-decompress every .cs2rec bundle referenced by the current map manifest after the dataset loads.
    /// This moves the Brotli cost out of freeze-end route startup.
    /// </summary>
    public bool PrewarmReplayBundles { get; set; } = true;
    public int PrewarmReplayBundleBatchSize { get; set; } = 1;
    public float PrewarmReplayBundleBatchDelay { get; set; } = 0.15f;
    public int ReplayBundleCacheMaxEntries { get; set; } = 1024;

    /// <summary>
    /// Keep the bot's native perception/sensor loop running during replay so it can already "see" enemies
    /// while we're driving its body. When the replay ends and AI hand-off happens, the bot already has its
    /// last-known-enemy populated and reacts immediately instead of needing a fresh sight acquisition.
    /// We still suppress engagement (no shooting/aiming-at-enemy) via SuppressBotEngagementWhileReplaying.
    /// </summary>
    public bool KeepBotPerceptionDuringReplay { get; set; } = true;

    /// <summary>
    /// Minimum end-distance threshold (CS units) for the save filter. A retake candidate is excluded
    /// if their trajectory ends FARTHER from the bomb than it started AND the end distance exceeds this
    /// value. This filters out pros who saved (ran away) after bomb plant. Set to 0 to disable.
    /// </summary>
    public float RetakeSaveFilterRadius { get; set; } = 1200f;
    public float RetakeMoveToReachThreshold { get; set; } = 80f;
    public float RetakeMoveToRefreshInterval { get; set; } = 0.1f;
    public float RetakeMoveToTimeout { get; set; } = 12f;
}

public sealed class ReplayDataset
{
    public string MapName { get; set; } = "de_dust2";
    public int TickRate { get; set; } = 64;
    public List<ReplayRound> Rounds { get; set; } = [];
    [JsonIgnore]
    public string BaseDirectory { get; set; } = string.Empty;
}

public sealed class ReplayRound
{
    public string Id { get; set; } = string.Empty;
    public string DemoPath { get; set; } = string.Empty;
    public int RoundNumber { get; set; }
    public int FreezeEndTick { get; set; }
    public List<TeamEconomy> TeamEconomies { get; set; } = [];
    public List<ReplayPlayer> Players { get; set; } = [];
    // Bomb plant info, populated by the pipeline only for rounds where the bomb actually got planted
    // within the captured window. Used to assemble retake/post-plant replays. Explicit JsonPropertyName
    // because PropertyNameCaseInsensitive=true on JsonSerializerOptions wasn't binding camelCase JSON
    // ("plantRelativeTick") to the PascalCase property at runtime -- the dataset was loading 632 rounds
    // but every PlantRelativeTick stayed null. Adding the attribute makes the binding deterministic.
    [JsonPropertyName("plantRelativeTick")]
    public int? PlantRelativeTick { get; set; }
    [JsonPropertyName("plantPos")]
    public PlantPosition? PlantPos { get; set; }
}

public sealed class PlantPosition
{
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
    [JsonPropertyName("z")] public float Z { get; set; }
}

public sealed class TeamEconomy
{
    public int TeamNum { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public int PlayerCount { get; set; }
    public int TotalStartBalance { get; set; }
    public int AverageStartBalance { get; set; }
    public int TotalEquipmentValue { get; set; }
    public int TotalPrimaryValue { get; set; }
    public int TotalUtilityValue { get; set; }
    public int TotalArmorValue { get; set; }
    public int TotalCashEquipmentValue { get; set; }
}

public sealed class ReplayPlayer
{
    public string SteamId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int TeamNum { get; set; }
    public int Slot { get; set; }
    public int StartBalance { get; set; }
    public int Balance { get; set; }
    public int EquipmentValue { get; set; }
    public int ArmorValue { get; set; }
    public bool HasHelmet { get; set; }
    public bool HasDefuser { get; set; }
    public List<string> Inventory { get; set; } = [];
    public List<int> InventoryDefIndexes { get; set; } = [];
    public List<FreezeInventorySnapshot> FreezeInventorySnapshots { get; set; } = [];
    public string RecPath { get; set; } = string.Empty;
    public string RecKey { get; set; } = string.Empty;
    public string RetakeRecPath { get; set; } = string.Empty;
    public string RetakeRecKey { get; set; } = string.Empty;
    public float Duration { get; set; }
    public int FreezeEndTickIndex { get; set; }
    public float FreezeEndTime { get; set; }
    [JsonPropertyName("retakeDuration")]
    public float RetakeDuration { get; set; }
    [JsonPropertyName("retakeStartTime")]
    public float RetakeStartTime { get; set; }
    [JsonPropertyName("retakeStartRelativeTick")]
    public int RetakeStartRelativeTick { get; set; }
    [JsonPropertyName("retakeStartTickIndex")]
    public int RetakeStartTickIndex { get; set; }
    public int FirstWeaponDefIndex { get; set; } = -1;
    public List<int> PreloadWeaponDefIndexes { get; set; } = [];
    public ReplayFrame? StartFrame { get; set; }
    public ReplayFrame? EndFrame { get; set; }
    public ReplayFrame? RetakeStartFrame { get; set; }
    public ReplayFrame? RetakeEndFrame { get; set; }
    public List<ReplayGrenade> Grenades { get; set; } = [];
}

public sealed class FreezeInventorySnapshot
{
    public int Tick { get; set; }
    public int RelativeTick { get; set; }
    public float Time { get; set; }
    public List<string> Inventory { get; set; } = [];
    public List<int> InventoryDefIndexes { get; set; } = [];
    public List<string> Added { get; set; } = [];
    public List<string> Removed { get; set; } = [];
    public List<int> AddedDefIndexes { get; set; } = [];
    public List<int> RemovedDefIndexes { get; set; } = [];
}

public sealed class ReplayFrame
{
    public int RelativeTick { get; set; }
    public float Time { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Pitch { get; set; }
    public float Yaw { get; set; }
    public long Buttons { get; set; }
    public int? ActiveWeaponDefIndex { get; set; }
    public string ActiveWeapon { get; set; } = string.Empty;

    public ReplayFrame CloneAtTime(float time)
    {
        return new ReplayFrame
        {
            RelativeTick = RelativeTick,
            Time = time,
            X = X,
            Y = Y,
            Z = Z,
            Pitch = Pitch,
            Yaw = Yaw,
            Buttons = Buttons,
            ActiveWeaponDefIndex = ActiveWeaponDefIndex,
            ActiveWeapon = ActiveWeapon
        };
    }
}

public sealed class ReplayGrenade
{
    public int RelativeTick { get; set; }
    public float Time { get; set; }
    public string Type { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Pitch { get; set; }
    public float Yaw { get; set; }
    public float VelocityX { get; set; }
    public float VelocityY { get; set; }
    public float VelocityZ { get; set; }
}

public sealed record ReplayAssignment(ReplayRound Round, ReplayPlayer Player, int Budget);

internal sealed class PreparedOpeningSession
{
    public PreparedOpeningSession(
        ReplayRound round,
        ReplayPlayer replayPlayer,
        List<ReplayFrame> frames,
        List<ReplayGrenade> grenades,
        bool nativeReplayPreloaded)
    {
        Round = round;
        ReplayPlayer = replayPlayer;
        Frames = frames;
        Grenades = grenades;
        NativeReplayPreloaded = nativeReplayPreloaded;
    }

    public ReplayRound Round { get; }
    public ReplayPlayer ReplayPlayer { get; }
    public List<ReplayFrame> Frames { get; }
    public List<ReplayGrenade> Grenades { get; }
    public bool NativeReplayPreloaded { get; set; }
    public bool ReplayWeaponsPreloaded { get; set; }
}

public sealed record BotReplayAssignment(CCSPlayerController Bot, ReplayRound Round, ReplayPlayer Player, int Budget);

public sealed record BotSpawn(CCSPlayerController Bot, SpawnPosition Spawn, int Budget);

public sealed record TeamEconomyState(
    int PlayerCount,
    int TotalCash,
    int AverageCash,
    int TotalEquipmentValue,
    int TotalPrimaryValue,
    int TotalUtilityValue,
    int TotalArmorValue)
{
    public int TotalCashEquipmentValue => TotalCash + TotalEquipmentValue;
}

public sealed record EquipmentValues(int TotalValue, int PrimaryValue, int UtilityValue, int ArmorValue);

public sealed class UtilityTransferState(
    BotReplayAssignment assignment,
    Dictionary<string, int> currentItems,
    Dictionary<string, int> targetItems)
{
    public BotReplayAssignment Assignment { get; } = assignment;
    public Dictionary<string, int> CurrentItems { get; } = currentItems;
    private Dictionary<string, int> TargetItems { get; } = targetItems;

    public int Missing(string itemName)
    {
        return Math.Max(0, TargetItems.GetValueOrDefault(itemName) - CurrentItems.GetValueOrDefault(itemName));
    }

    public int Surplus(string itemName)
    {
        return Math.Max(0, CurrentItems.GetValueOrDefault(itemName) - TargetItems.GetValueOrDefault(itemName));
    }
}

public readonly record struct SpawnPosition(float X, float Y, float Z)
{
    public static SpawnPosition FromFrame(ReplayFrame frame)
    {
        return new SpawnPosition(frame.X, frame.Y, frame.Z);
    }

    public bool Matches(SpawnPosition other, float tolerance)
    {
        return DistanceSquared(other) <= tolerance * tolerance;
    }

    public float DistanceTo(SpawnPosition other)
    {
        return MathF.Sqrt(DistanceSquared(other));
    }

    private float DistanceSquared(SpawnPosition other)
    {
        var deltaX = X - other.X;
        var deltaY = Y - other.Y;
        var deltaZ = Z - other.Z;
        return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ);
    }
}

public sealed record PlayerSpawnEntry(ReplayRound Round, ReplayPlayer Player, SpawnPosition Spawn);

public sealed record RoundSpawnEntry(ReplayRound Round, TeamEconomy Economy, List<PlayerSpawnEntry> Players);

public sealed class SpawnReplayIndex(int teamNum)
{
    private const int CandidatePoolSize = 5;
    private const float HumanSpawnBlockRadiusScale = 0.70f;
    private const float MinimumHumanSpawnBlockRadius = 24f;
    private readonly List<RoundSpawnEntry> _rounds = [];
    private float? _minimumSpawnDistance;

    public int RoundCount => _rounds.Count;

    public void Add(ReplayRound round, TeamEconomy economy)
    {
        var players = round.Players
            .Where(player => player.TeamNum == teamNum && player.StartFrame != null && !string.IsNullOrWhiteSpace(player.RecPath))
            .Select(player => new PlayerSpawnEntry(round, player, SpawnPosition.FromFrame(player.StartFrame!)))
            .ToList();

        if (players.Count == 0)
        {
            return;
        }

        _rounds.Add(new RoundSpawnEntry(round, economy, players));
        _minimumSpawnDistance = null;
    }

    public float EffectiveHumanSpawnBlockRadius(float configuredRadius)
    {
        if (configuredRadius <= 0f)
        {
            return configuredRadius;
        }

        var minimumSpawnDistance = MinimumSpawnDistance();
        if (!float.IsFinite(minimumSpawnDistance) || minimumSpawnDistance <= 0f)
        {
            return configuredRadius;
        }

        var dataDrivenRadius = MathF.Floor(minimumSpawnDistance * HumanSpawnBlockRadiusScale);
        return Math.Clamp(dataDrivenRadius, MinimumHumanSpawnBlockRadius, configuredRadius);
    }

    public List<BotReplayAssignment>? SelectTeamAssignments(
        List<BotSpawn> bots,
        List<SpawnPosition> humanOccupiedSpawns,
        float humanSpawnBlockRadius,
        bool enforcePistolRoundMatching,
        bool currentIsPistolRound,
        Random random)
    {
        if (bots.Count == 0 || bots.Count > 5)
        {
            return null;
        }

        var candidates = new List<(int Score, List<BotReplayAssignment> Assignments)>();

        foreach (var round in _rounds)
        {
            if (enforcePistolRoundMatching && currentIsPistolRound && !IsPistolReplayRound(round))
            {
                continue;
            }

            if (bots.Count == 5 && round.Players.Count != 5)
            {
                continue;
            }

            if (round.Players.Count < bots.Count)
            {
                continue;
            }

            var assignments = TryMatchRound(bots, round, humanOccupiedSpawns, humanSpawnBlockRadius);
            if (assignments == null)
            {
                continue;
            }

            candidates.Add((LoadoutBudgetScore(assignments), assignments));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var topCandidates = candidates
            .OrderBy(candidate => candidate.Score)
            .Take(CandidatePoolSize)
            .ToList();
        return topCandidates[random.Next(topCandidates.Count)].Assignments;
    }

    public List<BotReplayAssignment>? SelectMixedAssignments(
        List<BotSpawn> bots,
        List<SpawnPosition> humanOccupiedSpawns,
        float humanSpawnBlockRadius,
        bool enforcePistolRoundMatching,
        bool currentIsPistolRound,
        Random random)
    {
        if (bots.Count == 0)
        {
            return null;
        }

        var orderedBots = bots
            .OrderBy(bot => bot.Budget)
            .ThenBy(bot => PlayerKey(bot.Bot))
            .ToList();
        var totalBudget = orderedBots.Sum(bot => bot.Budget);
        var remainingLoadoutBudget = totalBudget;
        var selected = new List<BotReplayAssignment>(orderedBots.Count);
        var usedRoutes = new HashSet<string>(StringComparer.Ordinal);
        var usedSpawns = new List<SpawnPosition>();
        var candidates = BuildMixedCandidates(
                humanOccupiedSpawns,
                humanSpawnBlockRadius,
                enforcePistolRoundMatching,
                currentIsPistolRound)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        foreach (var bot in orderedBots)
        {
            var choice = SelectMixedCandidate(
                bot,
                candidates,
                usedRoutes,
                usedSpawns,
                humanSpawnBlockRadius,
                remainingLoadoutBudget,
                random);
            if (choice == null)
            {
                return null;
            }

            selected.Add(new BotReplayAssignment(bot.Bot, choice.Entry.Round, choice.Entry.Player, bot.Budget));
            usedRoutes.Add(RouteKey(choice.Entry));
            usedSpawns.Add(choice.Entry.Spawn);
            remainingLoadoutBudget -= choice.LoadoutValue;
        }

        return AllocateTeamBudgets(selected);
    }

    private IEnumerable<MixedReplayCandidate> BuildMixedCandidates(
        List<SpawnPosition> humanOccupiedSpawns,
        float humanSpawnBlockRadius,
        bool enforcePistolRoundMatching,
        bool currentIsPistolRound)
    {
        foreach (var round in _rounds)
        {
            if (enforcePistolRoundMatching && currentIsPistolRound && !IsPistolReplayRound(round))
            {
                continue;
            }

            foreach (var player in round.Players)
            {
                if (IsHumanOccupied(player.Spawn, humanOccupiedSpawns, humanSpawnBlockRadius))
                {
                    continue;
                }

                yield return new MixedReplayCandidate(
                    player,
                    ProOpeningReplayPlugin.ReplayLoadoutValue(player.Player));
            }
        }
    }

    private static MixedReplayCandidate? SelectMixedCandidate(
        BotSpawn bot,
        List<MixedReplayCandidate> candidates,
        HashSet<string> usedRoutes,
        List<SpawnPosition> usedSpawns,
        float spawnBlockRadius,
        int remainingLoadoutBudget,
        Random random)
    {
        var topCandidates = candidates
            .Where(candidate => !usedRoutes.Contains(RouteKey(candidate.Entry)))
            .Where(candidate => !IsHumanOccupied(candidate.Entry.Spawn, usedSpawns, spawnBlockRadius))
            .Where(candidate => candidate.LoadoutValue <= remainingLoadoutBudget)
            .OrderBy(candidate => Math.Abs(bot.Budget - candidate.LoadoutValue))
            .ThenByDescending(candidate => candidate.LoadoutValue)
            .Take(CandidatePoolSize)
            .ToList();

        if (topCandidates.Count == 0)
        {
            topCandidates = candidates
                .Where(candidate => !usedRoutes.Contains(RouteKey(candidate.Entry)))
                .Where(candidate => candidate.LoadoutValue <= remainingLoadoutBudget)
                .OrderBy(candidate => Math.Abs(bot.Budget - candidate.LoadoutValue))
                .ThenByDescending(candidate => candidate.LoadoutValue)
                .Take(CandidatePoolSize)
                .ToList();
        }

        if (topCandidates.Count == 0)
        {
            topCandidates = candidates
                .Where(candidate => !usedRoutes.Contains(RouteKey(candidate.Entry)))
                .OrderBy(candidate => Math.Abs(bot.Budget - candidate.LoadoutValue))
                .ThenByDescending(candidate => candidate.LoadoutValue)
                .Take(CandidatePoolSize)
                .ToList();
        }

        if (topCandidates.Count == 0)
        {
            topCandidates = candidates
                .OrderBy(candidate => Math.Abs(bot.Budget - candidate.LoadoutValue))
                .ThenByDescending(candidate => candidate.LoadoutValue)
                .Take(CandidatePoolSize)
                .ToList();
        }

        return topCandidates.Count == 0 ? null : topCandidates[random.Next(topCandidates.Count)];
    }

    private static string RouteKey(PlayerSpawnEntry entry)
        => $"{entry.Round.Id}\n{entry.Round.DemoPath}\n{entry.Round.RoundNumber}\n{entry.Player.SteamId}\n{entry.Player.Slot}";

    private static int LoadoutBudgetScore(List<BotReplayAssignment> assignments)
    {
        return assignments.Sum(assignment => Math.Max(0, assignment.Budget - ProOpeningReplayPlugin.ReplayLoadoutValue(assignment.Player)));
    }

    private static List<BotReplayAssignment>? TryMatchRound(
        List<BotSpawn> bots,
        RoundSpawnEntry round,
        List<SpawnPosition> humanOccupiedSpawns,
        float humanSpawnBlockRadius)
    {
        var orderedBots = bots
            .OrderBy(bot => bot.Budget)
            .ThenBy(bot => PlayerKey(bot.Bot))
            .ToList();
        var teamBudget = orderedBots.Sum(bot => bot.Budget);

        var allPlayers = round.Players
            .Select(player => new
            {
                Entry = player,
                LoadoutValue = ProOpeningReplayPlugin.ReplayLoadoutValue(player.Player),
                HumanBlocked = IsHumanOccupied(player.Spawn, humanOccupiedSpawns, humanSpawnBlockRadius)
            })
            .ToList();
        var players = allPlayers
            .Where(player => !player.HumanBlocked)
            .ToList();
        if (players.Count < orderedBots.Count)
        {
            return null;
        }

        players = players
            .OrderBy(player => player.LoadoutValue)
            .ThenBy(player => player.Entry.Player.SteamId, StringComparer.Ordinal)
            .ToList();
        if (players.Count < orderedBots.Count)
        {
            return null;
        }

        var usedPlayers = new bool[players.Count];
        var result = new BotReplayAssignment?[orderedBots.Count];
        List<BotReplayAssignment>? bestAssignments = null;
        var bestScore = int.MaxValue;

        TryAssignBot(0, 0, 0);
        return bestAssignments;

        void TryAssignBot(int botIndex, int currentLoadoutValue, int currentScore)
        {
            if (botIndex >= orderedBots.Count)
            {
                if (currentLoadoutValue > teamBudget)
                {
                    return;
                }

                var score = currentScore + (teamBudget - currentLoadoutValue);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestAssignments = AllocateTeamBudgets(result.Select(assignment => assignment!).ToList());
                }
                return;
            }
            if (currentLoadoutValue > teamBudget || currentScore >= bestScore)
            {
                return;
            }

            var bot = orderedBots[botIndex];
            for (var playerIndex = 0; playerIndex < players.Count; playerIndex++)
            {
                if (usedPlayers[playerIndex])
                {
                    continue;
                }

                var player = players[playerIndex];
                var loadoutValue = player.LoadoutValue;
                usedPlayers[playerIndex] = true;
                result[botIndex] = new BotReplayAssignment(bot.Bot, round.Round, player.Entry.Player, bot.Budget);
                TryAssignBot(
                    botIndex + 1,
                    currentLoadoutValue + loadoutValue,
                    currentScore + Math.Abs(bot.Budget - loadoutValue));
                result[botIndex] = null;
                usedPlayers[playerIndex] = false;
            }
        }
    }

    private static List<BotReplayAssignment> AllocateTeamBudgets(List<BotReplayAssignment> assignments)
    {
        var totalBudget = ProOpeningReplayPlugin.RoundMoneyDown(
            assignments.Sum(assignment => ProOpeningReplayPlugin.RoundMoneyDown(assignment.Budget)));
        var loadoutValues = assignments.ToDictionary(
            assignment => assignment,
            assignment => ProOpeningReplayPlugin.ReplayLoadoutValue(assignment.Player));
        var totalLoadout = loadoutValues.Values.Sum();
        var remaining = ProOpeningReplayPlugin.RoundMoneyDown(totalBudget - totalLoadout);
        var positiveSurplus = assignments.Sum(assignment =>
            ProOpeningReplayPlugin.RoundMoneyDown(Math.Max(
                0,
                ProOpeningReplayPlugin.RoundMoneyDown(assignment.Budget) - loadoutValues[assignment])));

        var allocated = new List<BotReplayAssignment>(assignments.Count);
        var distributed = 0;
        for (var i = 0; i < assignments.Count; i++)
        {
            var assignment = assignments[i];
            var loadoutValue = loadoutValues[assignment];
            var finalMoney = 0;
            if (remaining > 0)
            {
                if (positiveSurplus > 0)
                {
                    finalMoney = i == assignments.Count - 1
                        ? remaining - distributed
                        : remaining * ProOpeningReplayPlugin.RoundMoneyDown(Math.Max(
                            0,
                            ProOpeningReplayPlugin.RoundMoneyDown(assignment.Budget) - loadoutValue)) / positiveSurplus;
                }
                else
                {
                    finalMoney = i == assignments.Count - 1
                        ? remaining - distributed
                        : remaining / assignments.Count;
                }
                finalMoney = ProOpeningReplayPlugin.RoundMoneyDown(finalMoney);
            }

            distributed += finalMoney;
            allocated.Add(assignment with { Budget = loadoutValue + finalMoney });
        }

        return allocated;
    }

    private static bool IsHumanOccupied(SpawnPosition spawn, List<SpawnPosition> humanOccupiedSpawns, float radius)
    {
        return humanOccupiedSpawns.Any(humanSpawn => spawn.Matches(humanSpawn, radius));
    }

    private float MinimumSpawnDistance()
    {
        if (_minimumSpawnDistance.HasValue)
        {
            return _minimumSpawnDistance.Value;
        }

        var minimum = float.PositiveInfinity;
        foreach (var round in _rounds)
        {
            for (var i = 0; i < round.Players.Count; i++)
            {
                for (var j = i + 1; j < round.Players.Count; j++)
                {
                    var distance = round.Players[i].Spawn.DistanceTo(round.Players[j].Spawn);
                    if (distance > 0.001f && distance < minimum)
                    {
                        minimum = distance;
                    }
                }
            }
        }

        _minimumSpawnDistance = minimum;
        return minimum;
    }

    private static bool IsPistolReplayRound(RoundSpawnEntry round)
    {
        return RoundEconomyIndex.IsPistolRoundEconomy(round.Economy)
            && round.Players.All(player => !ProOpeningReplayPlugin.ReplayUsesPrimaryWeapon(player.Player));
    }

    private static int PlayerKey(CCSPlayerController player)
    {
        return player.UserId ?? player.Slot;
    }

    private sealed record MixedReplayCandidate(PlayerSpawnEntry Entry, int LoadoutValue);
}

public sealed record IndexedRound(ReplayRound Round, TeamEconomy Economy)
{
    public int SortEconomy => EffectiveEconomy(Economy);

    public static int EffectiveEconomy(TeamEconomy economy)
    {
        return economy.TotalCashEquipmentValue > 0
            ? economy.TotalCashEquipmentValue
            : economy.TotalStartBalance + economy.TotalEquipmentValue;
    }
}

public sealed class RoundEconomyIndex
{
    private const int PlayerCountWeight = 5_000;
    private const int EffectiveEconomyWeight = 2;
    private readonly Dictionary<int, List<IndexedRound>> _roundsByPlayerCount = [];

    public int Count => _roundsByPlayerCount.Values.Sum(rounds => rounds.Count);

    public void Add(ReplayRound round, TeamEconomy economy)
    {
        if (!_roundsByPlayerCount.TryGetValue(economy.PlayerCount, out var rounds))
        {
            rounds = [];
            _roundsByPlayerCount[economy.PlayerCount] = rounds;
        }

        rounds.Add(new IndexedRound(round, economy));
    }

    public void Sort()
    {
        foreach (var rounds in _roundsByPlayerCount.Values)
        {
            rounds.Sort((left, right) => left.SortEconomy.CompareTo(right.SortEconomy));
        }
    }

    public ReplayRound? SelectClosest(TeamEconomyState state, Random random)
    {
        var bestScore = int.MaxValue;
        var bestRounds = new List<ReplayRound>();

        foreach (var (playerCount, rounds) in _roundsByPlayerCount)
        {
            var playerCountPenalty = Math.Abs(playerCount - state.PlayerCount) * PlayerCountWeight;
            if (playerCountPenalty > bestScore)
            {
                continue;
            }

            InspectClosestEconomies(rounds, state, playerCountPenalty, ref bestScore, bestRounds);
        }

        return bestRounds.Count == 0 ? null : bestRounds[random.Next(bestRounds.Count)];
    }

    private static void InspectClosestEconomies(
        List<IndexedRound> rounds,
        TeamEconomyState state,
        int playerCountPenalty,
        ref int bestScore,
        List<ReplayRound> bestRounds)
    {
        if (rounds.Count == 0)
        {
            return;
        }

        var targetEconomy = state.TotalCashEquipmentValue;
        var rightIndex = LowerBound(rounds, targetEconomy);
        var leftIndex = rightIndex - 1;
        var inspectedAny = false;

        while (leftIndex >= 0 || rightIndex < rounds.Count)
        {
            var leftBound = leftIndex >= 0 ? LowerBoundScore(rounds[leftIndex], targetEconomy, playerCountPenalty) : int.MaxValue;
            var rightBound = rightIndex < rounds.Count ? LowerBoundScore(rounds[rightIndex], targetEconomy, playerCountPenalty) : int.MaxValue;

            if (inspectedAny && Math.Min(leftBound, rightBound) > bestScore)
            {
                break;
            }

            if (leftBound <= rightBound)
            {
                Inspect(rounds[leftIndex], state, ref bestScore, bestRounds);
                leftIndex--;
            }
            else
            {
                Inspect(rounds[rightIndex], state, ref bestScore, bestRounds);
                rightIndex++;
            }

            inspectedAny = true;
        }
    }

    private static int LowerBound(List<IndexedRound> rounds, int targetEconomy)
    {
        var low = 0;
        var high = rounds.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (rounds[middle].SortEconomy < targetEconomy)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static int LowerBoundScore(IndexedRound round, int targetEconomy, int playerCountPenalty)
    {
        return playerCountPenalty + Math.Abs(round.SortEconomy - targetEconomy) * EffectiveEconomyWeight;
    }

    private static void Inspect(IndexedRound round, TeamEconomyState state, ref int bestScore, List<ReplayRound> bestRounds)
    {
        var score = EconomyScore(round, state);
        if (score < bestScore)
        {
            bestScore = score;
            bestRounds.Clear();
            bestRounds.Add(round.Round);
            return;
        }

        if (score == bestScore)
        {
            bestRounds.Add(round.Round);
        }
    }

    public static int CalculateScore(TeamEconomy economy, TeamEconomyState state)
    {
        // All TOTAL deltas must be normalized to per-player before comparing -- otherwise a 2-bot live
        // team is forced to match against pro rounds whose 5-player totals are inherently 2.5x larger,
        // and the matcher ends up preferring force/eco rounds with low utility totals. That left small
        // teams visibly under-equipped (no nades) compared to full 5v5 lobbies.
        var statePlayers = Math.Max(1, state.PlayerCount);
        var roundPlayers = Math.Max(1, economy.PlayerCount);
        var effectiveEconomy = IndexedRound.EffectiveEconomy(economy);

        // Relax PlayerCount mismatch: a smaller team should still be allowed to pull execute templates
        // from a 5v5 pro round (they're the only data we have); we just nudge the matcher slightly
        // toward same-sized rounds when both options are available.
        var playerCountDelta = Math.Abs(economy.PlayerCount - state.PlayerCount) * (PlayerCountWeight / 10);

        var perEffective = Math.Abs(effectiveEconomy / roundPlayers - state.TotalCashEquipmentValue / statePlayers);
        var effectiveDelta = perEffective * statePlayers * EffectiveEconomyWeight;

        var perCash = Math.Abs(economy.TotalStartBalance / roundPlayers - state.TotalCash / statePlayers);
        var cashDelta = perCash * statePlayers;

        var averageCashDelta = Math.Abs(economy.AverageStartBalance - state.AverageCash) * statePlayers;

        var perEquip = Math.Abs(economy.TotalEquipmentValue / roundPlayers - state.TotalEquipmentValue / statePlayers);
        var equipmentDelta = perEquip * statePlayers;

        var perPrimary = Math.Abs(economy.TotalPrimaryValue / roundPlayers - state.TotalPrimaryValue / statePlayers);
        var primaryDelta = perPrimary * statePlayers / 2;

        var perUtility = Math.Abs(economy.TotalUtilityValue / roundPlayers - state.TotalUtilityValue / statePlayers);
        var utilityDelta = perUtility * statePlayers;

        var perArmor = Math.Abs(economy.TotalArmorValue / roundPlayers - state.TotalArmorValue / statePlayers);
        var armorDelta = perArmor * statePlayers / 2;

        return playerCountDelta + effectiveDelta + cashDelta + averageCashDelta + equipmentDelta + primaryDelta + utilityDelta + armorDelta;
    }

    public static bool IsPistolRoundEconomy(TeamEconomy economy)
    {
        if (economy.PlayerCount == 0) return false;
        return economy.AverageStartBalance + (economy.TotalEquipmentValue / economy.PlayerCount) <= 1100
            && economy.TotalPrimaryValue == 0;
    }

    private static int EconomyScore(IndexedRound round, TeamEconomyState state)
    {
        return CalculateScore(round.Economy, state);
    }
}

public enum ReplayWeaponSlot
{
    Other,
    Primary,
    Secondary,
    Utility,
    C4,
    Taser,
    Knife
}

public enum ReplaySessionKind { Opening, Retake }

public sealed class EnemyWatchState(int enemyKey, float visibleSince)
{
    public int EnemyKey { get; } = enemyKey;
    public float VisibleSince { get; } = visibleSince;
    public float LastSeenTime { get; set; } = visibleSince;
}

public enum BotMoveRouteType
{
    Default = 0,
    Fastest = 1,
    Safest = 2,
    Retreat = 3
}

public sealed class RetakeMoveToSession
{
    public RetakeMoveToSession(
        CCSPlayerController player,
        ReplayRound round,
        ReplayPlayer replayPlayer,
        List<ReplayFrame> frames,
        Vector target,
        float startTime)
    {
        Player = player;
        Round = round;
        ReplayPlayer = replayPlayer;
        Frames = frames;
        Target = target;
        StartTime = startTime;
        NextIssueTime = startTime;
    }

    public CCSPlayerController Player { get; }
    public ReplayRound Round { get; }
    public ReplayPlayer ReplayPlayer { get; }
    public List<ReplayFrame> Frames { get; }
    public Vector Target { get; }
    public float StartTime { get; }
    public float NextIssueTime { get; set; }
}

public sealed class ReplaySession
{
    public ReplaySession(
        CCSPlayerController player,
        ReplayRound round,
        ReplayPlayer replayPlayer,
        List<ReplayFrame> frames,
        List<ReplayGrenade> grenades,
        float startTime,
        ReplaySessionKind kind = ReplaySessionKind.Opening,
        float frameTimeOffset = 0f,
        int nativeReplayStartTick = 0,
        bool nativeReplayPrerollActive = false,
        int nativeReplayHoldBeforeTick = -1)
    {
        Player = player;
        // Snapshot the player name eagerly. After a player disconnects (or quickly switches teams) the
        // CBasePlayerController schema pointer goes null and reading PlayerName from EndSession's chat log
        // throws ArgumentNullException, killing the OnTick callback.
        PlayerName = SafeGetName(player);
        Round = round;
        ReplayPlayer = replayPlayer;
        Frames = frames;
        Grenades = grenades;
        StartTime = startTime;
        Kind = kind;
        FrameTimeOffset = frameTimeOffset;
        NativeReplayStartTick = Math.Max(0, nativeReplayStartTick);
        NativeReplayPrerollActive = nativeReplayPrerollActive;
        NativeReplayHoldBeforeTick = nativeReplayHoldBeforeTick;
        LastFrameTime = frames.Count == 0 ? 0f : frames[^1].Time - frameTimeOffset;
    }

    public CCSPlayerController Player { get; }
    public string PlayerName { get; }
    public ReplayRound Round { get; }
    public ReplayPlayer ReplayPlayer { get; }
    public List<ReplayFrame> Frames { get; }
    public List<ReplayGrenade> Grenades { get; }
    public float StartTime { get; }
    public float LastFrameTime { get; }
    public ReplaySessionKind Kind { get; }
    // Frames can start from a non-zero native tick; subtract this so elapsed=0 matches the sliced replay.
    public float FrameTimeOffset { get; }
    public int NativeReplayStartTick { get; }
    public bool NativeReplayPrerollActive { get; }
    public int NativeReplayHoldBeforeTick { get; }
    public int NextGrenadeIndex { get; set; }
    public int NextFreezeInventorySnapshotIndex { get; set; }
    public bool NativeReplayActive { get; set; }
    public bool NativeReplayPreloaded { get; set; }
    public bool ReplayWeaponsPreloaded { get; set; }
    public int NativeReplaySlot { get; set; } = -1;
    public int NativeReplayTickCount { get; set; }
    public int NativeReplayLastCursor { get; set; } = -1;
    public int NativeReplayStallTicks { get; set; }
    public bool NativeReplayDiagnosticLogged { get; set; }

    private static string SafeGetName(CCSPlayerController player)
    {
        try { return player.IsValid ? player.PlayerName : "<unknown>"; }
        catch { return "<unknown>"; }
    }
}

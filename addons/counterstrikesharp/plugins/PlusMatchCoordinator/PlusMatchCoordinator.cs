using BotHiderApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using MatchCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace PlusMatchCoordinator;

public sealed class PlusMatchCoordinatorPlugin : BasePlugin
{
    private static readonly PluginCapability<IBotHiderApi> BotHiderCapability = new("bothider:api");

    public override string ModuleName => "PLUS Match Coordinator";
    public override string ModuleVersion => "1.4.2.6-Preview.2";
    public override string ModuleAuthor => "CS2BotImproverPlus contributors";
    public override string ModuleDescription => "Offline MR12 match sessions, GOTV demos, and OpenRating statistics";

    private readonly object _gate = new();
    private readonly TradeTracker _trades = new();
    private readonly Dictionary<int, string> _playerIdsBySlot = new();
    private OpenRatingWeights _ratingWeights = OpenRatingWeights.ProxyV1;
    private string? _csgoRoot;
    private BotIdentityCatalog? _identityCatalog;
    private IBotHiderApi? _botHider;
    private MatchRequest? _request;
    private MatchStatistics? _statistics;
    private MatchRules? _rules;
    private DateTimeOffset _startedAt;
    private bool _roundHasOpeningKill;
    private bool _bombPlanted;
    private bool _live;
    private bool _finalizing;
    private bool _rosterValidated;
    private DateTimeOffset _liveNotBefore;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _rosterSyncTimer;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _activationTimer;
    private int _rosterSyncRemaining;
    private int _cleanRosterStableTicks;
    private int _boundRosterStableTicks;
    private ManagedRosterPhase _rosterSetupPhase;
    private int _activationRemaining;
    private int _mapGeneration;
    private string? _activatedSessionId;
    private bool _demoRecordingStarted;
    private bool _botHiderFailureLogged;
    private Task? _resultWriteTask;
    private CancellationTokenSource? _controlWatcherCancellation;
    private Task? _controlWatcherTask;
    private string? _acceptedControlSessionId;

    public override void Load(bool hotReload)
    {
        _ratingWeights = OpenRatingWeights.Load(Path.Combine(ModuleDirectory, "open-rating-3.0-proxy-v1.json"));
        try
        {
            _csgoRoot = PlusManagedPaths.ResolveCsgoRoot(Server.GameDirectory);
            Logger.LogInformation(
                "[PlusMatchCoordinator] Resolved CS2 content root: reported={ReportedDirectory}; resolved={CsgoRoot}",
                Server.GameDirectory, _csgoRoot);
            _identityCatalog = BotIdentityCatalog.Load(
                Path.Combine(_csgoRoot, "addons", "BotHider", "bot_info.json"));
        }
        catch (Exception error)
        {
            Logger.LogCritical(
                error,
                "[PlusMatchCoordinator] Cannot initialize managed CS2 paths or load the bot identity catalog; candidates={Candidates}",
                string.Join(", ", PlusManagedPaths.CandidateCsgoRoots(Server.GameDirectory)));
        }
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventRoundMvp>(OnRoundMvp);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        RegisterEventHandler<EventBombExploded>(OnBombExploded);
        RegisterEventHandler<EventCsWinPanelMatch>(OnWinPanelMatch);
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        ResolveBotHiderApi();
        Server.NextFrame(() => ScheduleActivation(Server.MapName));
    }

    public override void Unload(bool hotReload)
    {
        StopActivation();
        StopRosterSync();
        StopControlWatcher();
        for (var attempt = 0; attempt < 2 && _request != null; attempt++)
        {
            if (!_finalizing) FinalizeMatch(MatchState.Interrupted, "plugin_unloaded");
            try { _resultWriteTask?.Wait(TimeSpan.FromSeconds(2)); }
            catch (Exception error) { Logger.LogError(error, "[PlusMatchCoordinator] Result flush failed during unload"); }
        }
    }

    private void OnMapStart(string mapName)
    {
        _mapGeneration++;
        ScheduleActivation(mapName);
    }

    private void ScheduleActivation(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName)) return;
        if (_csgoRoot == null)
        {
            Logger.LogCritical("[PlusMatchCoordinator] Match activation disabled because the CS2 content root is unresolved");
            return;
        }
        StopActivation();
        var generation = _mapGeneration;
        _activationRemaining = 30;
        Logger.LogInformation(
            "[PlusMatchCoordinator] Watching for a match request on {Map}; game directory={GameDirectory}",
            mapName, Server.GameDirectory);
        _activationTimer = AddTimer(1.0f, () =>
        {
            if (generation != _mapGeneration || !Server.MapName.Equals(mapName, StringComparison.OrdinalIgnoreCase))
            {
                StopActivation();
                return;
            }
            if (_request != null || _activatedSessionId != null)
            {
                StopActivation();
                return;
            }
            LoadActiveRequest(mapName);
            _activationRemaining--;
            if (_request != null || _activatedSessionId != null || _activationRemaining <= 0)
                StopActivation();
        }, TimerFlags.REPEAT);
    }

    private void StopActivation()
    {
        _activationTimer?.Kill();
        _activationTimer = null;
        _activationRemaining = 0;
    }

    private bool ResolveBotHiderApi()
    {
        if (_botHider != null) return true;
        try
        {
            var api = BotHiderCapability.Get()
                ?? throw new InvalidOperationException("BotHider capability returned no API instance");
            if (!api.SetDisguise(true) || !api.SetNameSource(true))
                throw new InvalidOperationException("BotHider shared-memory commands were rejected");
            _botHider = api;
            _botHiderFailureLogged = false;
            Logger.LogInformation("[PlusMatchCoordinator] BotHider API connected");
            return true;
        }
        catch (Exception error)
        {
            if (!_botHiderFailureLogged)
                Logger.LogError(error, "[PlusMatchCoordinator] BotHider API is unavailable");
            _botHiderFailureLogged = true;
            return false;
        }
    }

    private void LoadActiveRequest(string mapName)
    {
        MatchRequest? request = null;
        try
        {
            var active = PlusManagedPaths.ActiveMatchPath(CsgoRoot);
            if (!File.Exists(active)) return;
            request = JsonSerializer.Deserialize<MatchRequest>(File.ReadAllText(active), MatchJson.Options);
            if (request == null || !request.MapId.Equals(mapName, StringComparison.OrdinalIgnoreCase)) return;
            if (_activatedSessionId == request.SessionId) return;
            ValidateTeam(request.PlayerTeam);
            ValidateTeam(request.OpponentTeam);
            ValidateRequestPaths(request);
            if (File.Exists(ManagedResultPath(request))) return;
            Logger.LogInformation(
                "[PlusMatchCoordinator] Accepted session {SessionId} on {Map}; opponent={Opponent}; demo={Demo}",
                request.SessionId, mapName, request.OpponentName, request.RecordDemo);
            StartSessionIfIdle(request);
        }
        catch (Exception error)
        {
            Logger.LogError(error, "[PlusMatchCoordinator] Match request rejected on {Map}", mapName);
            if (request != null)
            {
                var demo = new DemoStatus(
                    request.RecordDemo ? DemoState.Interrupted : DemoState.Disabled,
                    null,
                    0,
                    "REQUEST_REJECTED",
                    error.Message);
                if (!TryManagedResultPath(request, out var resultPath)) return;
                var managedDemo = ManagedDemoPath(request);
                WriteResult(resultPath, new MatchResult(
                    MatchJson.SchemaVersion, request.SessionId, MatchState.Interrupted,
                    request.MapId, request.CreatedAtUnix, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    0, 0, request.OpponentName, _ratingWeights.ModelVersion,
                    demo with { Path = request.RecordDemo ? managedDemo : null }, [], "request_rejected"));
            }
        }
    }

    private static void ValidateTeam(IEnumerable<MatchPlayer> team)
    {
        RosterSelector.ValidateTeam(team.Select(player => player.Name));
        foreach (var player in team)
        {
            if (player.Name.Length > 64 || player.Name.Any(char.IsControl) || player.Name.Contains('"') || player.Name.Contains(';'))
                throw new InvalidDataException($"Unsafe bot name: {player.Name}");
        }
    }

    private void ValidateRequestPaths(MatchRequest request)
    {
        if (request.SessionId.Length == 0 || request.SessionId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new InvalidDataException("Unsafe match session id");
        var gameRoot = CsgoRoot;
        var expectedResult = Path.Combine(gameRoot, ".csbip", "matches", request.SessionId, "result.json");
        var expectedDemo = Path.Combine(gameRoot, "demos", "csbip", request.SessionId + ".dem");
        if (!Path.GetFullPath(request.ResultPath).Equals(expectedResult, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(request.DemoPath).Equals(expectedDemo, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Match request paths are outside the managed session roots");
    }

    private string ManagedResultPath(MatchRequest request)
    {
        if (request.SessionId.Length == 0 || request.SessionId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new InvalidDataException("Unsafe match session id");
        return Path.Combine(CsgoRoot, ".csbip", "matches", request.SessionId, "result.json");
    }

    private string ManagedDemoPath(MatchRequest request) =>
        Path.Combine(CsgoRoot, "demos", "csbip", request.SessionId + ".dem");

    private bool TryManagedResultPath(MatchRequest request, out string path)
    {
        try { path = ManagedResultPath(request); return true; }
        catch { path = ""; return false; }
    }

    private string CsgoRoot => _csgoRoot
        ?? throw new InvalidOperationException("The CS2 content root was not resolved");

    private void StartSession(MatchRequest request)
    {
        lock (_gate)
        {
            _request = request;
            _statistics = new MatchStatistics();
            _rules = new MatchRules();
            _playerIdsBySlot.Clear();
            _startedAt = DateTimeOffset.UtcNow;
            _bombPlanted = false;
            _live = false;
            _finalizing = false;
            _rosterValidated = false;
            _demoRecordingStarted = false;
            _liveNotBefore = DateTimeOffset.UtcNow.AddSeconds(2);
            _activatedSessionId = request.SessionId;
            _rosterSetupPhase = ManagedRosterPhase.Cleaning;
            _cleanRosterStableTicks = 0;
            _boundRosterStableTicks = 0;
            _acceptedControlSessionId = null;
        }

        Server.ExecuteCommand("mp_autoteambalance 0");
        Server.ExecuteCommand("mp_limitteams 0");
        Server.ExecuteCommand("mp_maxrounds 24");
        Server.ExecuteCommand("mp_halftime 1");
        Server.ExecuteCommand("mp_overtime_enable 1");
        Server.ExecuteCommand("mp_overtime_maxrounds 6");
        Server.ExecuteCommand("bot_join_after_player 0");
        Server.ExecuteCommand("bot_quota_mode normal");
        Server.ExecuteCommand("bot_quota 0");
        Server.ExecuteCommand("bot_kick");

        PublishRosterPhase(request.SessionId, ManagedRosterPhase.Cleaning);
        StartRosterSync();
        StartControlWatcher(request);
    }

    private void StartSessionIfIdle(MatchRequest request)
    {
        lock (_gate)
        {
            if (_request != null || _finalizing) return;
        }
        StartSession(request);
    }

    private static void AddBot(string name, TeamSide side)
    {
        var safeName = name.Replace("\"", string.Empty, StringComparison.Ordinal);
        Server.ExecuteCommand($"bot_add_{(side == TeamSide.Ct ? "ct" : "t")} \"{safeName}\"");
    }

    private void EnsureInitialHumanSide()
    {
        var request = _request;
        if (request == null) return;
        var playerSide = request.PlayerSide == MatchSide.T ? TeamSide.T : TeamSide.Ct;
        var target = playerSide == TeamSide.Ct ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
        foreach (var human in Utilities.GetPlayers().Where(player => player is { IsValid: true, IsBot: false }))
        {
            if (human.Team != target) human.SwitchTeam(target);
        }
    }

    private void RegisterPlayers()
    {
        var request = _request;
        var statistics = _statistics;
        if (request == null || statistics == null) return;
        var playerSide = request.PlayerSide == MatchSide.T ? TeamSide.T : TeamSide.Ct;
        var opponentSide = playerSide == TeamSide.Ct ? TeamSide.T : TeamSide.Ct;
        foreach (var player in Utilities.GetPlayers().Where(player => player is { IsValid: true }))
        {
            if (!player.IsBot) _playerIdsBySlot[player.Slot] = "player-local";
            else _playerIdsBySlot.TryAdd(player.Slot, $"bot-{player.PlayerName}");
        }
        foreach (var player in request.PlayerTeam) statistics.Register(player, playerSide);
        foreach (var player in request.OpponentTeam) statistics.Register(player, opponentSide);
    }

    private void StartRosterSync()
    {
        _rosterSyncRemaining = Math.Max(_rosterSyncRemaining, 160);
        if (_rosterSyncTimer != null) return;
        _rosterSyncTimer = AddTimer(0.25f, SyncRosterTick, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void SyncRosterTick()
    {
        if (_request == null || _finalizing)
        {
            StopRosterSync();
            return;
        }
        _rosterSyncRemaining--;
        if (_rosterSyncRemaining <= 0)
        {
            Logger.LogError("[PlusMatchCoordinator] Roster setup timed out in phase {Phase}", _rosterSetupPhase);
            FinalizeMatch(MatchState.Interrupted, "roster_setup_timeout");
            return;
        }

        if (_rosterSetupPhase == ManagedRosterPhase.Cleaning)
        {
            var bots = CurrentBots();
            if (bots.Length > 0)
            {
                _cleanRosterStableTicks = 0;
                if (_rosterSyncRemaining % 4 == 0)
                {
                    Server.ExecuteCommand("bot_quota 0");
                    Server.ExecuteCommand("bot_kick");
                }
                return;
            }
            if (++_cleanRosterStableTicks < 2) return;
            _rosterSetupPhase = ManagedRosterPhase.Reconciling;
            PublishRosterPhase(_request.SessionId, _rosterSetupPhase);
            Logger.LogInformation("[PlusMatchCoordinator] Previous Bot roster cleared; creating requested teams");
        }

        if (_rosterSetupPhase is ManagedRosterPhase.Reconciling or ManagedRosterPhase.Binding)
        {
            if (!ReconcileRequestedRoster())
            {
                if (_rosterSetupPhase != ManagedRosterPhase.Reconciling)
                {
                    _rosterSetupPhase = ManagedRosterPhase.Reconciling;
                    PublishRosterPhase(_request.SessionId, _rosterSetupPhase);
                }
                _boundRosterStableTicks = 0;
                return;
            }
            if (_rosterSetupPhase != ManagedRosterPhase.Binding)
            {
                _rosterSetupPhase = ManagedRosterPhase.Binding;
                PublishRosterPhase(_request.SessionId, _rosterSetupPhase);
            }
        }

        EnsureInitialHumanSide();
        RegisterPlayers();
        if (!TryBindRequestedRoster(out var detail))
        {
            _boundRosterStableTicks = 0;
            if (_rosterSyncRemaining % 20 == 0)
                Logger.LogInformation("[PlusMatchCoordinator] Waiting for requested roster identities: {Detail}", detail);
            return;
        }
        if (++_boundRosterStableTicks < 2) return;
        CompleteRosterSetup();
    }

    private void StopRosterSync()
    {
        _rosterSyncTimer?.Kill();
        _rosterSyncTimer = null;
        _rosterSyncRemaining = 0;
    }

    private CCSPlayerController[] CurrentBots() => Utilities.GetPlayers()
        .Where(player => player is { IsValid: true, IsBot: true }
            && player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist)
        .OrderBy(player => player.Slot)
        .ToArray();

    private bool ReconcileRequestedRoster()
    {
        var request = _request;
        if (request == null || _finalizing) return false;
        var playerSide = request.PlayerSide == MatchSide.T ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
        var opponentSide = playerSide == CsTeam.CounterTerrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
        var playerRoster = request.PlayerTeam.Where(player => player.Kind == PlayerKind.Bot).ToArray();
        var opponentRoster = request.OpponentTeam.ToArray();
        var bots = CurrentBots();
        var playerBots = bots.Where(player => player.Team == playerSide).ToArray();
        var opponentBots = bots.Where(player => player.Team == opponentSide).ToArray();
        var action = RosterReconciler.Next(playerBots.Length, opponentBots.Length, playerRoster.Length, opponentRoster.Length);
        switch (action)
        {
            case RosterAction.RemovePlayerBot:
                RemoveBot(playerBots[^1]);
                return false;
            case RosterAction.RemoveOpponentBot:
                RemoveBot(opponentBots[^1]);
                return false;
            case RosterAction.AddPlayerBot:
                AddBot(playerRoster[playerBots.Length].Name, playerSide == CsTeam.Terrorist ? TeamSide.T : TeamSide.Ct);
                return false;
            case RosterAction.AddOpponentBot:
                AddBot(opponentRoster[opponentBots.Length].Name, opponentSide == CsTeam.Terrorist ? TeamSide.T : TeamSide.Ct);
                return false;
            default:
                return true;
        }
    }

    private static void RemoveBot(CCSPlayerController player)
    {
        if (player.UserId is int userId)
            Server.ExecuteCommand($"kickid {userId}");
        else
            Server.ExecuteCommand($"bot_kick \"{player.PlayerName.Replace("\"", string.Empty, StringComparison.Ordinal)}\"");
    }

    private void CompleteRosterSetup()
    {
        var request = _request;
        if (request == null || _finalizing || _rosterSetupPhase == ManagedRosterPhase.Ready) return;
        _rosterSetupPhase = ManagedRosterPhase.Ready;
        _rosterValidated = true;
        _liveNotBefore = DateTimeOffset.UtcNow.AddSeconds(2);
        EnsureInitialHumanSide();
        RegisterPlayers();
        StopRosterSync();
        StartDemoRecording(request);
        PublishRosterPhase(request.SessionId, ManagedRosterPhase.Ready);
        Logger.LogInformation(
            "[PlusMatchCoordinator] Roster validated and bound: {Players}",
            string.Join(",", request.PlayerTeam.Concat(request.OpponentTeam).Select(player => player.Name)));
        Server.ExecuteCommand("mp_warmup_end; mp_restartgame 3");
    }

    private string ManagedControlPath(MatchRequest request) =>
        Path.Combine(CsgoRoot, ".csbip", "matches", request.SessionId, "control.json");

    private void StartControlWatcher(MatchRequest request)
    {
        StopControlWatcher();
        var cancellation = new CancellationTokenSource();
        _controlWatcherCancellation = cancellation;
        _controlWatcherTask = Task.Run(async () =>
        {
            var path = ManagedControlPath(request);
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var control = JsonSerializer.Deserialize<MatchControlRequest>(
                            await File.ReadAllTextAsync(path, cancellation.Token), MatchJson.Options);
                        if (control is { SchemaVersion: MatchJson.SchemaVersion, Action: "finish_early" }
                            && control.SessionId == request.SessionId
                            && _acceptedControlSessionId != request.SessionId)
                        {
                            _acceptedControlSessionId = request.SessionId;
                            try { File.Delete(path); } catch { }
                            Server.NextFrame(() =>
                            {
                                if (_request?.SessionId == request.SessionId && !_finalizing)
                                {
                                    Logger.LogInformation(
                                        "[PlusMatchCoordinator] Early finish requested for {SessionId}", request.SessionId);
                                    FinalizeMatch(MatchState.Interrupted, "user_finished_early");
                                }
                            });
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception error)
                {
                    Logger.LogWarning(error, "[PlusMatchCoordinator] Cannot read match control request for {SessionId}", request.SessionId);
                }
                try { await Task.Delay(500, cancellation.Token); }
                catch (OperationCanceledException) { return; }
            }
        }, cancellation.Token);
    }

    private void StopControlWatcher()
    {
        _controlWatcherCancellation?.Cancel();
        _controlWatcherCancellation?.Dispose();
        _controlWatcherCancellation = null;
        _controlWatcherTask = null;
    }

    private bool TryBindRequestedRoster(out string detail)
    {
        detail = string.Empty;
        var request = _request;
        if (request == null) { detail = "no active request"; return false; }
        if (_identityCatalog == null) { detail = "bot_info.json was not loaded"; return false; }
        if (!ResolveBotHiderApi()) { detail = "BotHider API unavailable"; return false; }

        var playerSide = request.PlayerSide == MatchSide.T ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
        var opponentSide = playerSide == CsTeam.CounterTerrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
        var playerRoster = request.PlayerTeam.Where(player => player.Kind == PlayerKind.Bot).ToArray();
        var opponentRoster = request.OpponentTeam.ToArray();
        var bots = Utilities.GetPlayers()
            .Where(player => player is { IsValid: true, IsBot: true })
            .OrderBy(player => player.Slot)
            .ToArray();
        var playerBots = bots.Where(player => player.Team == playerSide).ToArray();
        var opponentBots = bots.Where(player => player.Team == opponentSide).ToArray();
        if (playerBots.Length != playerRoster.Length || opponentBots.Length != opponentRoster.Length)
        {
            detail = $"expected {playerRoster.Length}+{opponentRoster.Length} bots, found {playerBots.Length}+{opponentBots.Length}";
            return false;
        }

        return BindSide(playerBots, playerRoster, out detail)
            && BindSide(opponentBots, opponentRoster, out detail);
    }

    private bool BindSide(IReadOnlyList<CCSPlayerController> slots, IReadOnlyList<MatchPlayer> roster, out string detail)
    {
        detail = string.Empty;
        for (var index = 0; index < roster.Count; index++)
        {
            var slot = slots[index].Slot;
            var requested = roster[index];
            if (_botHider == null || !_botHider.IsManagedBot(slot))
            {
                detail = $"BotHider has not claimed slot {slot}";
                return false;
            }
            if (_identityCatalog == null || !_identityCatalog.TryGet(requested.Name, out var identity))
            {
                detail = $"missing bot_info identity for {requested.Name}";
                return false;
            }
            try
            {
                var crosshair = identity.CrosshairCode is null or "0" ? string.Empty : identity.CrosshairCode;
                var applied = (_botHider.GetPersonaName(slot).Equals(requested.Name, StringComparison.Ordinal)
                        || _botHider.SetPersonaName(slot, requested.Name))
                    && (_botHider.GetBotSteamId(slot) == identity.SteamId64
                        || _botHider.SetBotSteamId(slot, identity.SteamId64))
                    && (_botHider.GetCrosshairCode(slot).Equals(crosshair, StringComparison.Ordinal)
                        || _botHider.SetCrosshairCode(slot, crosshair))
                    && (_botHider.GetScoreboardFlair(slot) == identity.ScoreboardFlair
                        || _botHider.SetScoreboardFlair(slot, identity.ScoreboardFlair));
                var verified = _botHider.GetPersonaName(slot).Equals(requested.Name, StringComparison.Ordinal)
                    && _botHider.GetBotSteamId(slot) == identity.SteamId64
                    && _botHider.GetCrosshairCode(slot).Equals(crosshair, StringComparison.Ordinal)
                    && _botHider.GetScoreboardFlair(slot) == identity.ScoreboardFlair;
                if (!applied || !verified)
                {
                    detail = $"BotHider has not finished applying identity {requested.Name} for slot {slot}";
                    return false;
                }
                _playerIdsBySlot[slot] = requested.Id;
            }
            catch (Exception error)
            {
                detail = $"identity binding failed for {requested.Name} on slot {slot}: {error.Message}";
                return false;
            }
        }
        return true;
    }

    private void StartDemoRecording(MatchRequest request)
    {
        if (!request.RecordDemo || _demoRecordingStarted) return;
        Directory.CreateDirectory(Path.GetDirectoryName(ManagedDemoPath(request))!);
        var demoName = request.SessionId.Replace("\"", string.Empty, StringComparison.Ordinal);
        Server.ExecuteCommand("tv_enable 1");
        Server.ExecuteCommand($"tv_record \"demos/csbip/{demoName}\"");
        _demoRecordingStarted = true;
        Logger.LogInformation("[PlusMatchCoordinator] GOTV recording started for {SessionId}", request.SessionId);
        AddTimer(8.0f, () =>
        {
            if (_request?.SessionId == request.SessionId && !File.Exists(ManagedDemoPath(request)))
                Logger.LogError(
                    "[PlusMatchCoordinator] GOTV has not created the Demo file for {SessionId}: {Path}",
                    request.SessionId, ManagedDemoPath(request));
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_request == null || _finalizing) return HookResult.Continue;
        RegisterPlayers();
        if (!_live)
        {
            if (!_rosterValidated) return HookResult.Continue;
            if (DateTimeOffset.UtcNow < _liveNotBefore) return HookResult.Continue;
            _live = true;
            StopRosterSync();
            Logger.LogInformation("[PlusMatchCoordinator] Session {SessionId} is live", _request.SessionId);
        }
        _roundHasOpeningKill = false;
        _bombPlanted = false;
        _trades.Clear();
        _statistics?.StartRound();
        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var attacker = IdFor(@event.Attacker);
        var victim = IdFor(@event.Userid);
        if (_live && attacker != null && victim != null && attacker != victim && SideFor(@event.Attacker) != SideFor(@event.Userid))
            _statistics?.Damage(attacker, @event.DmgHealth);
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (!_live) return HookResult.Continue;
        var attackerId = IdFor(@event.Attacker);
        var victimId = IdFor(@event.Userid);
        if (attackerId == null || victimId == null || attackerId == victimId) return HookResult.Continue;

        var now = DateTimeOffset.UtcNow;
        var attackerSide = SideFor(@event.Attacker);
        var victimSide = SideFor(@event.Userid);
        var trade = _trades.RecordKill(attackerId, victimId, attackerSide, victimSide, now);
        var opening = !_roundHasOpeningKill;
        _roundHasOpeningKill = true;
        var after = SwingContext(attackerSide);
        var before = after with
        {
            CtAlive = after.CtAlive + (victimSide == TeamSide.Ct ? 1 : 0),
            TAlive = after.TAlive + (victimSide == TeamSide.T ? 1 : 0)
        };
        var victimPawn = @event.Userid?.PlayerPawn.Value;
        var economy = OpenRatingCalculator.EconomyKillAdjustment(
            victimPawn?.ArmorValue ?? 0,
            HighestValueWeaponClass(@event.Userid));
        _statistics?.Kill(attackerId, victimId, attackerSide, @event.Headshot, opening, trade.IsTrade, RoundSwingModel.Delta(before, after), economy);
        if (trade.TradedPlayerId != null) _statistics?.MarkTraded(trade.TradedPlayerId);

        var assister = IdFor(@event.Assister);
        if (assister != null) _statistics?.Assist(assister);
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (!_live) return HookResult.Continue;
        var winner = @event.Winner == (byte)CsTeam.Terrorist ? TeamSide.T : TeamSide.Ct;
        _statistics?.EndRound(winner);
        if (_rules?.AddRound(winner) == true) FinalizeMatch(MatchState.Finished, null);
        return HookResult.Continue;
    }

    private HookResult OnWinPanelMatch(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        AddTimer(0.25f, () =>
        {
            if (_request != null && _live && !_finalizing)
                FinalizeMatch(MatchState.Finished, null);
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo info)
    {
        if (!_live) return HookResult.Continue;
        var id = IdFor(@event.Userid);
        var player = _statistics?.Players.FirstOrDefault(value => value.PlayerId == id);
        if (player != null) player.Mvps++;
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        if (_request != null && !_finalizing) Server.NextFrame(RegisterPlayers);
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (_request != null && !_finalizing) Server.NextFrame(RegisterPlayers);
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true } player) _playerIdsBySlot.Remove(player.Slot);
        if (_request != null && @event.Userid is { IsBot: false }) FinalizeMatch(MatchState.Interrupted, "local_player_disconnected");
        return HookResult.Continue;
    }

    private void OnMapEnd()
    {
        _mapGeneration++;
        StopActivation();
        StopRosterSync();
        StopControlWatcher();
        if (_request != null && !_finalizing) FinalizeMatch(MatchState.Interrupted, "map_changed");
    }

    private void FinalizeMatch(MatchState state, string? reason)
    {
        MatchRequest? request;
        MatchStatistics? statistics;
        MatchRules? rules;
        lock (_gate)
        {
            if (_request == null || _finalizing) return;
            _finalizing = true;
            _live = false;
            request = _request;
            statistics = _statistics;
            rules = _rules;
        }

        StopRosterSync();
        StopControlWatcher();
        PublishRosterPhase(request.SessionId, ManagedRosterPhase.Inactive);

        if (_demoRecordingStarted)
        {
            Server.ExecuteCommand("tv_stoprecord");
            Logger.LogInformation("[PlusMatchCoordinator] GOTV recording stopped for {SessionId}", request.SessionId);
        }
        var players = statistics?.FinalizeRatings(_ratingWeights) ?? [];
        var playerSide = request.PlayerSide == MatchSide.T ? TeamSide.T : TeamSide.Ct;
        var playerScore = playerSide == TeamSide.Ct ? rules?.CtScore ?? 0 : rules?.TScore ?? 0;
        var opponentScore = playerSide == TeamSide.Ct ? rules?.TScore ?? 0 : rules?.CtScore ?? 0;
        var opponentName = request.OpponentName;
        var result = new MatchResult(
            MatchJson.SchemaVersion, request.SessionId, state, request.MapId,
            _startedAt.ToUnixTimeSeconds(), DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            playerScore, opponentScore, opponentName, _ratingWeights.ModelVersion,
            new DemoStatus(
                !request.RecordDemo ? DemoState.Disabled : _demoRecordingStarted ? DemoState.Validating : DemoState.Failed,
                request.RecordDemo ? ManagedDemoPath(request) : null,
                0,
                request.RecordDemo && !_demoRecordingStarted ? "DEMO_RECORDING_NOT_STARTED" : null,
                request.RecordDemo && !_demoRecordingStarted ? "Roster validation ended before GOTV recording could start" : null),
            players, reason);

        var resultPath = ManagedResultPath(request);
        _resultWriteTask = Task.Run(() =>
        {
            var written = WriteResult(resultPath, result);
            if (written)
                Logger.LogInformation(
                    "[PlusMatchCoordinator] Result written for {SessionId}: state={State}, score={PlayerScore}-{OpponentScore}, players={PlayerCount}, path={Path}",
                    result.SessionId, result.State, result.PlayerScore, result.OpponentScore, result.Players.Count, resultPath);
            lock (_gate)
            {
                if (written)
                {
                    _request = null;
                    _statistics = null;
                    _rules = null;
                    _playerIdsBySlot.Clear();
                    _activatedSessionId = null;
                    _demoRecordingStarted = false;
                    _rosterValidated = false;
                    _finalizing = false;
                }
                else
                {
                    _finalizing = false;
                }
            }
        });
    }

    private bool WriteResult(string path, MatchResult result)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            string? temporary = null;
            try
            {
                path = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                temporary = path + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
                var bytes = JsonSerializer.SerializeToUtf8Bytes(result, MatchJson.Options);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(true);
                }
                File.Move(temporary, path, true);
                return true;
            }
            catch (Exception error)
            {
                lastError = error;
                if (temporary != null) try { File.Delete(temporary); } catch { }
                Thread.Sleep(100 * (attempt + 1));
            }
        }
        Logger.LogError(lastError, "[PlusMatchCoordinator] Result write failed after retries: {Path}", path);
        return false;
    }

    private void PublishRosterPhase(string sessionId, ManagedRosterPhase phase)
    {
        try
        {
            ManagedMatchRuntimeStore.Write(CsgoRoot, sessionId, phase);
        }
        catch (Exception error)
        {
            Logger.LogError(
                error,
                "[PlusMatchCoordinator] Failed to publish roster phase {Phase} for {SessionId}",
                phase,
                sessionId);
        }
    }

    private string? IdFor(CCSPlayerController? player)
    {
        if (player is not { IsValid: true }) return null;
        if (_playerIdsBySlot.TryGetValue(player.Slot, out var id)) return id;
        id = player.IsBot ? $"bot-{player.PlayerName}" : "player-local";
        _playerIdsBySlot[player.Slot] = id;
        return id;
    }

    private static TeamSide SideFor(CCSPlayerController? player) =>
        player?.Team == CsTeam.Terrorist ? TeamSide.T : TeamSide.Ct;

    private RoundSwingContext SwingContext(TeamSide eventTeam)
    {
        var players = Utilities.GetPlayers().Where(player => player is { IsValid: true, PawnIsAlive: true }).ToArray();
        return new RoundSwingContext(
            _ratingWeights.MapSidePrior(_request?.MapId ?? ""),
            players.Count(player => player.Team == CsTeam.CounterTerrorist),
            players.Count(player => player.Team == CsTeam.Terrorist),
            players.Where(player => player.Team == CsTeam.CounterTerrorist).Sum(EquipmentValue),
            players.Where(player => player.Team == CsTeam.Terrorist).Sum(EquipmentValue),
            _bombPlanted,
            eventTeam);
    }

    private static WeaponClass HighestValueWeaponClass(CCSPlayerController? player)
    {
        var weapons = player?.PlayerPawn.Value?.WeaponServices?.MyWeapons;
        if (weapons == null) return WeaponClass.Other;
        return weapons
            .Select(handle => handle.Value?.DesignerName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => OpenRatingCalculator.ClassifyWeapon(name!))
            .OrderByDescending(OpenRatingCalculator.EquipmentValue)
            .DefaultIfEmpty(WeaponClass.Other)
            .FirstOrDefault();
    }

    private static int EquipmentValue(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return 0;
        var weapons = pawn.WeaponServices?.MyWeapons;
        var weaponValue = weapons == null ? 0 : weapons
            .Select(handle => handle.Value?.DesignerName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => OpenRatingCalculator.EquipmentValue(OpenRatingCalculator.ClassifyWeapon(name!)))
            .DefaultIfEmpty(0)
            .Max();
        return weaponValue + Math.Clamp(pawn.ArmorValue, 0, 100) * 10;
    }

    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        if (_live) _bombPlanted = true;
        return HookResult.Continue;
    }

    private HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        if (_live) _bombPlanted = false;
        return HookResult.Continue;
    }

    private HookResult OnBombExploded(EventBombExploded @event, GameEventInfo info)
    {
        if (_live) _bombPlanted = false;
        return HookResult.Continue;
    }
}

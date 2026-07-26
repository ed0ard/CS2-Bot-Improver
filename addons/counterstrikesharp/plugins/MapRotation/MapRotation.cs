using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;

namespace MapRotation;

[MinimumApiVersion(304)]
public sealed class MapRotationPlugin : BasePlugin
{
    public override string ModuleName => "MapRotation";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "YuGeYu";
    public override string ModuleDescription => "Optional fixed map rotation controller.";

    private const string Prefix = "[MapRotation]";
    private const int DefaultMaxRounds = 24;
    private const int DefaultOvertimeMaxRounds = 6;

    private static readonly RotationMap[] Rotation =
    [
        new("de_anubis", "阿努比斯"),
        new("de_overpass", "死亡游乐园"),
        new("de_inferno", "炼狱小镇"),
        new("de_mirage", "荒漠迷城"),
        new("de_dust2", "炙热沙城II"),
        new("de_nuke", "核子危机"),
        new("de_ancient", "远古遗迹"),
        new("de_train", "列车停放站"),
        new("de_vertigo", "殒命大厦"),
        new("de_cache", "死城之谜")
    ];

    private bool _enabled;
    private bool _changeScheduled;
    private bool _thanksSentThisMatch;
    private int _scheduleSerial;
    private string _currentMap = string.Empty;
    private int _currentIndex = -1;
    private int _lastRoundWinner;
    private int _lastRoundReason;
    private string _lastRoundMessage = string.Empty;

    public override void Load(bool hotReload)
    {
        _enabled = false;
        _changeScheduled = false;
        _thanksSentThisMatch = false;
        _scheduleSerial = 0;
        _currentMap = NormalizeMapName(Server.MapName);
        _currentIndex = FindMapIndex(_currentMap);

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventGameEnd>(OnGameEnd);

        AddCommand("css_map_rotation", "Show or toggle map rotation: css_map_rotation [0|1]", OnMapRotationCommand);
        AddCommand("css_map_next", "Change to the next map in the rotation.", OnMapNextCommand);

        Log($"Loaded. Default enabled=0. Rotation list: {FormatRotationList()}");
        LogMapState("Initial map state");
    }

    private void OnMapStart(string mapName)
    {
        _currentMap = NormalizeMapName(mapName);
        _currentIndex = FindMapIndex(_currentMap);
        _changeScheduled = false;
        _thanksSentThisMatch = false;
        _scheduleSerial++;
        _lastRoundWinner = 0;
        _lastRoundReason = 0;
        _lastRoundMessage = string.Empty;

        LogMapState("Map started");
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        _lastRoundWinner = @event.Winner;
        _lastRoundReason = @event.Reason;
        _lastRoundMessage = @event.Message ?? string.Empty;

        var matchState = ReadMatchState(@event.Winner, @event.Reason, @event.Message ?? string.Empty);

        if (!_enabled)
        {
            Log($"round_end: rotation disabled, {matchState}");
        }
        else
        {
            Log($"round_end: {matchState}");
        }

        var evaluationSerial = _scheduleSerial;
        AddTimer(0.5f, () => EvaluateRoundEndAfterScoreUpdate(evaluationSerial, _lastRoundWinner, _lastRoundReason, _lastRoundMessage));

        return HookResult.Continue;
    }

    private void EvaluateRoundEndAfterScoreUpdate(int evaluationSerial, int winner, int reason, string message)
    {
        if (evaluationSerial != _scheduleSerial)
        {
            Log($"round_end delayed-eval skipped because map/schedule state changed. evalSerial={evaluationSerial}, activeSerial={_scheduleSerial}");
            return;
        }

        var matchState = ReadMatchState(winner, reason, message);
        Log($"round_end delayed-eval: {matchState}");

        if (!matchState.IsMatchEnd)
        {
            return;
        }

        SendEndMatchThanksOnce();
        Log($"target score reached, rotating: score={matchState.HighScore}-{matchState.LowScore}, target={matchState.RequiredWinScore}");
        ScheduleNextMap($"final-round detection: {matchState.DetectionReason}", delaySeconds: 5.0f);
    }

    private HookResult OnGameEnd(EventGameEnd @event, GameEventInfo info)
    {
        var currentMap = GetCurrentMap();
        Log($"EventGameEnd fired. winner={@event.Winner}, roundWinner={_lastRoundWinner}, reason={_lastRoundReason}, current={currentMap}, enabled={BoolToInt(_enabled)}, scheduled={BoolToInt(_changeScheduled)}");

        var matchState = ReadMatchState(_lastRoundWinner, _lastRoundReason, _lastRoundMessage);
        Log($"EventGameEnd match-state: {matchState}");
        if (!matchState.IsMatchEnd)
        {
            return HookResult.Continue;
        }

        SendEndMatchThanksOnce();

        if (!_enabled)
        {
            return HookResult.Continue;
        }

        ScheduleNextMap($"EventGameEnd fallback winner={@event.Winner}", delaySeconds: 3.0f);

        return HookResult.Continue;
    }

    private void OnMapRotationCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount < 2)
        {
            ReplyStatus(command);
            return;
        }

        var arg = command.GetArg(1).Trim();
        if (arg is not ("0" or "1"))
        {
            command.ReplyToCommand($"{Prefix} Usage: css_map_rotation [0|1]");
            return;
        }

        var oldState = _enabled;
        _enabled = arg == "1";

        Log($"Command css_map_rotation by {DescribeCaller(player, command)}: old={BoolToInt(oldState)}, new={BoolToInt(_enabled)}");
        ReplyStatus(command);
    }

    private void OnMapNextCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (_changeScheduled)
        {
            command.ReplyToCommand($"{Prefix} changelevel is already scheduled.");
            Log($"Command css_map_next by {DescribeCaller(player, command)} ignored: change already scheduled.");
            return;
        }

        var currentMap = GetCurrentMap();
        var nextMap = GetNextMap(currentMap, logFallback: true);

        _changeScheduled = true;
        command.ReplyToCommand($"{Prefix} Changing from {currentMap} to {nextMap}.");
        Log($"Command css_map_next by {DescribeCaller(player, command)}: current={currentMap}, next={nextMap}");
        ExecuteChangeLevel(nextMap);
    }

    private void ScheduleNextMap(string reason, float delaySeconds)
    {
        var currentMap = GetCurrentMap();

        if (!_enabled)
        {
            Log($"Not scheduling changelevel because rotation is disabled. reason={reason}, current={currentMap}");
            return;
        }

        if (_changeScheduled)
        {
            Log($"Not scheduling changelevel because one is already scheduled. reason={reason}, current={currentMap}");
            return;
        }

        var nextMap = GetNextMap(currentMap, logFallback: true);
        _changeScheduled = true;
        var scheduleId = ++_scheduleSerial;

        Log($"Scheduling next map by {reason}: current={currentMap}, next={nextMap}, delay={delaySeconds:0.0}s, scheduleId={scheduleId}");
        AddTimer(delaySeconds, () =>
        {
            if (!_changeScheduled || scheduleId != _scheduleSerial)
            {
                Log($"Skipping scheduled changelevel: scheduleId={scheduleId}, activeScheduleId={_scheduleSerial}, scheduled={BoolToInt(_changeScheduled)}");
                return;
            }

            ExecuteChangeLevel(nextMap);
        });
    }

    private void SendEndMatchThanksOnce()
    {
        if (_thanksSentThisMatch)
        {
            Log("End-match thanks already sent, skipping duplicate.");
            return;
        }

        _thanksSentThisMatch = true;

        Log("Sending end-match thanks message.");
        Server.PrintToChatAll($"{Prefix} 感谢上游项目 ed0ard/CS2-Bot-Improver。");
        Server.PrintToChatAll($"{Prefix} 感谢所有提供帮助、测试和反馈的朋友们。");
    }

    private void ReplyStatus(CommandInfo command)
    {
        var currentMap = GetCurrentMap();
        var nextMap = GetNextMap(currentMap, logFallback: false);
        var indexText = _currentIndex >= 0 ? _currentIndex.ToString() : "not-in-list";

        command.ReplyToCommand($"{Prefix} enabled={BoolToInt(_enabled)}, current={currentMap}, index={indexText}, next={nextMap}");
    }

    private void LogMapState(string label)
    {
        var currentMap = GetCurrentMap();
        var nextMap = GetNextMap(currentMap, logFallback: false);

        Log($"{label}: {currentMap}, rotation enabled={BoolToInt(_enabled)}, index={_currentIndex}, next={nextMap}");
    }

    private string GetNextMap(string currentMap, bool logFallback)
    {
        var index = FindMapIndex(currentMap);
        if (index < 0)
        {
            if (logFallback)
            {
                Log($"Current map '{currentMap}' is not in rotation list, fallback next map = {Rotation[0].Name}");
            }

            return Rotation[0].Name;
        }

        return Rotation[(index + 1) % Rotation.Length].Name;
    }

    private void ExecuteChangeLevel(string mapName)
    {
        var command = $"changelevel {mapName}";
        Log($"Executing command: {command}");
        Server.ExecuteCommand(command);
    }

    private string GetCurrentMap()
    {
        var mapName = NormalizeMapName(Server.MapName);
        if (!string.IsNullOrWhiteSpace(mapName))
        {
            _currentMap = mapName;
            _currentIndex = FindMapIndex(mapName);
        }

        return string.IsNullOrWhiteSpace(_currentMap) ? "unknown" : _currentMap;
    }

    private static int FindMapIndex(string mapName)
    {
        return Array.FindIndex(Rotation, map => string.Equals(map.Name, mapName, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeMapName(string? mapName)
    {
        return (mapName ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static MatchState ReadMatchState(int winner, int reason, string message)
    {
        var gameRules = GetGameRules();
        var score = GetTeamScore();

        var rawMaxRounds = GetConVarInt("mp_maxrounds", DefaultMaxRounds);
        var maxRounds = rawMaxRounds > 0 ? rawMaxRounds : DefaultMaxRounds;
        var winLimit = GetConVarInt("mp_winlimit", 0);
        var overtimeEnabled = GetConVarInt("mp_overtime_enable", 0) != 0;
        var rawOvertimeMaxRounds = GetConVarInt("mp_overtime_maxrounds", DefaultOvertimeMaxRounds);
        var overtimeMaxRounds = rawOvertimeMaxRounds > 0 ? rawOvertimeMaxRounds : DefaultOvertimeMaxRounds;
        var played = gameRules?.TotalRoundsPlayed ?? -1;
        var knownScoreTotal = score.CtScore >= 0 && score.TScore >= 0 ? score.CtScore + score.TScore : -1;
        var totalRounds = Math.Max(played, knownScoreTotal);
        var hasScore = score.CtScore >= 0 && score.TScore >= 0;

        var isMatchEnd = false;
        var isTieBoundary = false;
        var regulationTieScore = Math.Max(1, maxRounds / 2);
        var overtimeHalfRounds = Math.Max(1, overtimeMaxRounds / 2);
        var requiredWinScore = hasScore ? GetRequiredWinScore(maxRounds, overtimeMaxRounds, score.CtScore, score.TScore) : -1;
        var highScore = hasScore ? Math.Max(score.CtScore, score.TScore) : -1;
        var lowScore = hasScore ? Math.Min(score.CtScore, score.TScore) : -1;
        var detectionReason = hasScore ? $"not-final ({highScore}-{lowScore}, target={requiredWinScore})" : "score unavailable";

        if (!hasScore)
        {
            isMatchEnd = false;
        }
        else if (IsTieBoundary(maxRounds, overtimeMaxRounds, score.CtScore, score.TScore))
        {
            isTieBoundary = true;
            isMatchEnd = false;
            detectionReason = $"tie boundary detected, not rotating ({highScore}-{lowScore}, target={requiredWinScore}, regulationTie={regulationTieScore}, overtimeHalf={overtimeHalfRounds})";
            Log($"tie boundary detected, not rotating: score={highScore}-{lowScore}, target={requiredWinScore}, regulationTie={regulationTieScore}, overtimeHalf={overtimeHalfRounds}");
        }
        else if (highScore >= requiredWinScore && highScore > lowScore)
        {
            isMatchEnd = true;
            detectionReason = $"target score reached ({highScore}-{lowScore}, target={requiredWinScore})";
        }
        else if (winLimit > 0 && highScore >= winLimit && highScore > lowScore)
        {
            isMatchEnd = true;
            detectionReason = $"winlimit reached ({highScore}-{lowScore}, mp_winlimit={winLimit})";
        }

        return new MatchState(
            score.CtScore,
            score.TScore,
            highScore,
            lowScore,
            requiredWinScore,
            maxRounds,
            regulationTieScore,
            overtimeHalfRounds,
            played,
            totalRounds,
            winLimit,
            overtimeEnabled,
            overtimeMaxRounds,
            winner,
            reason,
            message,
            isMatchEnd,
            isTieBoundary,
            detectionReason);
    }

    private static int GetRequiredWinScore(int maxRounds, int overtimeMaxRounds, int ctScore, int tScore)
    {
        var lowScore = Math.Min(ctScore, tScore);
        var regulationTieScore = Math.Max(1, maxRounds / 2);
        var overtimeHalfRounds = Math.Max(1, overtimeMaxRounds / 2);

        if (lowScore < regulationTieScore)
        {
            return regulationTieScore + 1;
        }

        var overtimeIndex = ((lowScore - regulationTieScore) / overtimeHalfRounds) + 1;
        return regulationTieScore + overtimeIndex * overtimeHalfRounds + 1;
    }

    private static bool IsTieBoundary(int maxRounds, int overtimeMaxRounds, int ctScore, int tScore)
    {
        if (ctScore != tScore)
        {
            return false;
        }

        var tieScore = ctScore;
        var regulationTieScore = Math.Max(1, maxRounds / 2);
        var overtimeHalfRounds = Math.Max(1, overtimeMaxRounds / 2);

        if (tieScore < regulationTieScore)
        {
            return false;
        }

        return (tieScore - regulationTieScore) % overtimeHalfRounds == 0;
    }

    private static CCSGameRules? GetGameRules()
    {
        try
        {
            return Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
        }
        catch
        {
            return null;
        }
    }

    private static TeamScore GetTeamScore()
    {
        try
        {
            var teams = Utilities
                .FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager")
                .Where(team => team.IsValid)
                .ToList();

            var ctScore = teams.FirstOrDefault(team => team.TeamNum == (byte)CsTeam.CounterTerrorist)?.Score ?? -1;
            var tScore = teams.FirstOrDefault(team => team.TeamNum == (byte)CsTeam.Terrorist)?.Score ?? -1;

            return new TeamScore(ctScore, tScore);
        }
        catch
        {
            return new TeamScore(-1, -1);
        }
    }

    private static int GetConVarInt(string name, int fallback)
    {
        try
        {
            return ConVar.Find(name)?.GetPrimitiveValue<int>() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string DescribeCaller(CCSPlayerController? player, CommandInfo command)
    {
        if (player is { IsValid: true })
        {
            return $"{player.PlayerName}#{player.UserId ?? player.Slot}";
        }

        return command.CallingContext.ToString();
    }

    private static int BoolToInt(bool value)
    {
        return value ? 1 : 0;
    }

    private static string FormatRotationList()
    {
        return string.Join(", ", Rotation.Select(map => $"{map.Name}({map.ChineseName})"));
    }

    private static void Log(string message)
    {
        Server.PrintToConsole($"{Prefix} {message}");
    }

    private sealed record RotationMap(string Name, string ChineseName);

    private sealed record TeamScore(int CtScore, int TScore);

    private sealed record MatchState(
        int CtScore,
        int TScore,
        int HighScore,
        int LowScore,
        int RequiredWinScore,
        int MaxRounds,
        int RegulationTieScore,
        int OvertimeHalfRounds,
        int TotalRoundsPlayed,
        int TotalRounds,
        int WinLimit,
        bool OvertimeEnabled,
        int OvertimeMaxRounds,
        int Winner,
        int Reason,
        string Message,
        bool IsMatchEnd,
        bool IsTieBoundary,
        string DetectionReason)
    {
        public override string ToString()
        {
            return $"current={Server.MapName}, score={CtScore}-{TScore}, highLow={HighScore}-{LowScore}, target={RequiredWinScore}, " +
                   $"maxRounds={MaxRounds}, regulationTie={RegulationTieScore}, overtimeHalf={OvertimeHalfRounds}, " +
                   $"played={TotalRoundsPlayed}, totalRounds={TotalRounds}, " +
                   $"mp_winlimit={WinLimit}, overtime={BoolToInt(OvertimeEnabled)}, mp_overtime_maxrounds={OvertimeMaxRounds}, " +
                   $"winner={Winner}, reason={Reason}, message='{Message}', boundary={(IsTieBoundary ? "tie" : "none")}, " +
                   $"isMatchEnd={BoolToInt(IsMatchEnd)}, detection='{DetectionReason}'";
        }
    }
}

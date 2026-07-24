using MatchCore;
using System.Text;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var selected = RosterSelector.SelectUnique(["a", "b", "b", "c", "d", "e", "f"], 5, ["a"], 42);
Check(selected.Count == 5 && selected.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 5, "roster selection must be unique");
Check(!selected.Contains("a", StringComparer.OrdinalIgnoreCase), "excluded player was selected");
Check(RosterSelector.CompleteTeam(["a", "a", "b"], ["a", "b", "c", "d", "e", "f"], seed: 1).Count == 5, "team fill failed");

var regulation = new MatchRules();
for (var i = 0; i < 13; i++) regulation.AddRound(regulation.SideForOriginalCtTeam());
Check(regulation.IsFinished && regulation.CtScore == 13, "MR12 regulation did not finish at 13");

var halftime = new MatchRules();
for (var i = 0; i < 12; i++) halftime.AddRound(halftime.SideForOriginalCtTeam());
Check(halftime.SideForOriginalCtTeam() == TeamSide.T, "original CT team did not swap at halftime");
halftime.AddRound(halftime.SideForOriginalCtTeam());
Check(halftime.IsFinished && halftime.CtScore == 13 && halftime.TScore == 0, "score did not follow the team across halftime");

var overtime = new MatchRules();
for (var i = 0; i < 12; i++) { overtime.AddRound(TeamSide.Ct); overtime.AddRound(TeamSide.T); }
Check(overtime.IsOvertime && !overtime.IsFinished, "12-12 must enter overtime");
for (var i = 0; i < 4; i++) overtime.AddRound(overtime.SideForOriginalCtTeam());
Check(overtime.IsFinished && overtime.CtScore == 16, "MR3 overtime did not finish at four wins");

Check(OpenRatingCalculator.IsTrade(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(5)), "five second trade window must be inclusive");
Check(!OpenRatingCalculator.IsTrade(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMilliseconds(5001)), "trade window exceeded five seconds");
var trades = new TradeTracker();
var first = trades.RecordKill("enemy", "teammate", TeamSide.T, TeamSide.Ct, DateTimeOffset.UnixEpoch);
var unrelated = trades.RecordKill("ct-other", "t-other", TeamSide.Ct, TeamSide.T, DateTimeOffset.UnixEpoch.AddSeconds(1));
var traded = trades.RecordKill("ct-trader", "enemy", TeamSide.Ct, TeamSide.T, DateTimeOffset.UnixEpoch.AddSeconds(2));
Check(!first.IsTrade && !unrelated.IsTrade, "unrelated kills must not be marked as trades");
Check(traded is { IsTrade: true, TradedPlayerId: "teammate" }, "trade must mark the original victim for KAST");
Check(OpenRatingCalculator.AssistDamageThreshold == 40, "assist threshold drifted");
Check(OpenRatingCalculator.ClassifyWeapon("weapon_awp") == WeaponClass.Awp, "AWP classification failed");
Check(OpenRatingCalculator.ClassifyWeapon("ak47") == WeaponClass.TierOneRifle, "rifle classification failed");
Check(OpenRatingCalculator.ClassifyWeapon("glock") == WeaponClass.StarterPistol, "starter pistol classification failed");

var before = new RoundSwingContext(0.1, 5, 5, 22000, 22000, false, TeamSide.Ct);
var after = before with { TAlive = 4 };
Check(RoundSwingModel.Delta(before, after) > 0, "CT opening kill should add positive swing");

var stats = new MatchStatistics();
stats.Register(new MatchPlayer("one", "one", PlayerKind.Human), TeamSide.Ct);
stats.Register(new MatchPlayer("two", "two", PlayerKind.Bot), TeamSide.T);
stats.StartRound();
stats.Damage("one", 100);
stats.Kill("one", "two", TeamSide.Ct, true, true, false, 0.2, 0.1);
stats.EndRound(TeamSide.Ct);
var result = stats.FinalizeRatings().Single(player => player.PlayerId == "one");
Check(result.KastRounds == 1 && result.RoundsSurvived == 1, "KAST/survival tracking failed");
var loser = stats.FinalizeRatings().Single(player => player.PlayerId == "two");
Check(loser.KastRounds == 0, "a round win or loss must not grant KAST without kill, assist, survival, or trade");
Check(result.Rating is { OpenRating: > 0, ModelVersion: "open-rating-3.0-proxy-v1" }, "OpenRating calculation failed");
var ratingJson = System.Text.Json.JsonSerializer.Serialize(result.Rating, MatchJson.Options);
Check(ratingJson.Contains("\"open_rating\"", StringComparison.Ordinal), "OpenRating JSON field is missing");
Check(!ratingJson.Contains("\"rating_plus\"", StringComparison.Ordinal), "legacy Rating Plus field leaked into new results");

var modelPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MatchCore", "open-rating-3.0-proxy-v1.json"));
var loadedWeights = OpenRatingWeights.Load(modelPath);
Check(loadedWeights.ModelVersion == "open-rating-3.0-proxy-v1", "OpenRating model file was not loaded");
Check(Math.Abs(loadedWeights.Kills - OpenRatingWeights.ProxyV1.Kills) < 1e-12
    && Math.Abs(loadedWeights.Intercept - OpenRatingWeights.ProxyV1.Intercept) < 1e-12,
    "OpenRating fallback weights drifted from the calibrated model");
Check(loadedWeights.MapSidePrior("de_nuke") > loadedWeights.MapSidePrior("de_anubis"), "map side priors were not loaded");

var identityPath = Path.Combine(Path.GetTempPath(), $"csbip-identities-{Environment.ProcessId}.json");
File.WriteAllText(identityPath, """
{
  "ZywOo": { "steamid": 1234, "crosshair_code": "CSGO-test", "scoreboard_flair": 969 }
}
""", Encoding.UTF8);
var identities = BotIdentityCatalog.Load(identityPath);
Check(identities.TryGet("zywoo", out var identity), "Bot identity lookup must be case-insensitive");
Check(identity.SteamId64 == BotIdentity.SteamId64Base + 1234, "Steam account ids must convert to SteamID64");
File.Delete(identityPath);

var caseConflictPath = Path.Combine(Path.GetTempPath(), $"csbip-identity-conflicts-{Environment.ProcessId}.json");
File.WriteAllText(caseConflictPath, """
{
  "HObbit": { "steamid": 1001, "crosshair_code": null, "scoreboard_flair": 0 },
  "Hobbit": { "steamid": 1002, "crosshair_code": null, "scoreboard_flair": 0 },
  "ZywOo": { "steamid": 1003, "crosshair_code": null, "scoreboard_flair": 0 }
}
""", Encoding.UTF8);
var caseConflicts = BotIdentityCatalog.Load(caseConflictPath);
Check(caseConflicts.TryGet("HObbit", out var upperIdentity) && upperIdentity.SteamAccountId == 1001,
    "exact bot identity lookup must preserve casing");
Check(caseConflicts.TryGet("Hobbit", out var titleIdentity) && titleIdentity.SteamAccountId == 1002,
    "case-distinct bot identities must both load");
Check(!caseConflicts.TryGet("hobbit", out _),
    "ambiguous case-insensitive identity lookup must fail instead of selecting the wrong player");
Check(caseConflicts.TryGet("zywoo", out var uniqueFallback) && uniqueFallback.SteamAccountId == 1003,
    "unique case-insensitive identity lookup must still work");
File.Delete(caseConflictPath);

Check(RosterReconciler.Next(5, 5, 4, 5) == RosterAction.RemovePlayerBot,
    "roster reconciliation must remove stale player-side bots before adding");
Check(RosterReconciler.Next(4, 6, 4, 5) == RosterAction.RemoveOpponentBot,
    "roster reconciliation must remove stale opponent-side bots");
Check(RosterReconciler.Next(3, 5, 4, 5) == RosterAction.AddPlayerBot,
    "roster reconciliation must add one missing teammate at a time");
Check(RosterReconciler.Next(4, 4, 4, 5) == RosterAction.AddOpponentBot,
    "roster reconciliation must add one missing opponent at a time");
Check(RosterReconciler.Next(4, 5, 4, 5) == RosterAction.None,
    "an exact roster must not be changed");

var gameInstall = Path.Combine(Path.GetTempPath(), $"csbip-game-root-{Environment.ProcessId}");
var reportedGameRoot = Path.Combine(gameInstall, "game");
var expectedCsgoRoot = Path.Combine(reportedGameRoot, "csgo");
Directory.CreateDirectory(expectedCsgoRoot);
File.WriteAllText(Path.Combine(expectedCsgoRoot, "gameinfo.gi"), "GameInfo {}", Encoding.UTF8);
try
{
    Check(PlusManagedPaths.ResolveCsgoRoot(reportedGameRoot) == Path.GetFullPath(expectedCsgoRoot),
        "CounterStrikeSharp game directory must resolve to the nested csgo content root");
    Check(PlusManagedPaths.ResolveCsgoRoot(expectedCsgoRoot) == Path.GetFullPath(expectedCsgoRoot),
        "an existing csgo content root must remain unchanged");
    Check(PlusManagedPaths.ActiveMatchPath(expectedCsgoRoot) == Path.Combine(expectedCsgoRoot, ".csbip", "match-active.json"),
        "active match path must live under the csgo content root");
    Check(!PlusManagedPaths.TryResolveCsgoRoot(Path.Combine(gameInstall, "missing"), out _),
        "a directory without gameinfo.gi must not be accepted as a CS2 content root");
}
finally
{
    Directory.Delete(gameInstall, recursive: true);
}

Check(BotBuyPolicy.NormalizeWeaponName("aug") == "weapon_aug", "short AUG purchase name must normalize");
Check(BotBuyPolicy.NormalizeWeaponName("weapon_aug") == "weapon_aug", "full AUG purchase name must normalize");
Check(BotBuyPolicy.NormalizeWeaponName("sg556") == "weapon_sg556", "short SG purchase name must normalize");
Check(BotBuyPolicy.NormalizeWeaponName("weapon_sg556") == "weapon_sg556", "full SG purchase name must normalize");
Check(BotBuyPolicy.SelectScopedRifleAction("aug", 0.059f, 0.9f) == ScopedRifleAction.Keep,
    "the upstream six-percent AUG keep window must be preserved");
Check(BotBuyPolicy.SelectScopedRifleAction("aug", 0.06f, 0.49f) == ScopedRifleAction.ReplaceWithM4A4,
    "AUG replacement must select M4A4 in the lower half");
Check(BotBuyPolicy.SelectScopedRifleAction("weapon_aug", 0.9f, 0.5f) == ScopedRifleAction.ReplaceWithM4A1S,
    "AUG replacement must select M4A1-S in the upper half");
Check(BotBuyPolicy.SelectScopedRifleAction("sg556", 0.9f, 0.0f) == ScopedRifleAction.ReplaceWithAk47,
    "SG 553 replacement must select AK-47");

Check(BotAimPolicy.SelectPriority(BotAimMode.Head, "weapon_ak47") == BotAimPriority.Head,
    "forced head mode must remain head-first for rifles");
Check(BotAimPolicy.SelectPriority(BotAimMode.Head, "weapon_awp") == BotAimPriority.Body,
    "forced head mode must preserve the upstream AWP body-first exception");
Check(BotAimPolicy.SelectPriority(BotAimMode.Mixed, "weapon_xm1014") == BotAimPriority.Body,
    "mixed mode must keep spread weapons body-first");
Check(BotAimPolicy.SelectPriority(BotAimMode.Mixed, "weapon_m4a1") == BotAimPriority.Jaw,
    "mixed mode must keep normal rifles jaw-first");

var staleRound = new BotCallbackGeneration(3, 8);
Check(staleRound.IsCurrent(3, 8), "matching callback generation must execute");
Check(!staleRound.IsCurrent(3, 9), "a prior-round callback must be cancelled");
Check(!staleRound.IsCurrent(4, 8), "a prior-map callback must be cancelled");

var readyRuntime = new ManagedMatchRuntime(
    ManagedMatchRuntime.CurrentSchemaVersion, "session-a", ManagedRosterPhase.Ready, 1);
Check(ManagedMatchRuntimeStore.IsPurchasingAllowed("session-a", readyRuntime),
    "managed purchases must resume after roster binding is ready");
Check(!ManagedMatchRuntimeStore.IsPurchasingAllowed("session-b", readyRuntime),
    "a stale runtime session must not enable purchasing");
Check(!ManagedMatchRuntimeStore.IsPurchasingAllowed(
        "session-a", readyRuntime with { RosterPhase = ManagedRosterPhase.Binding }),
    "purchasing must remain paused while roster identities are binding");

Console.WriteLine("MatchCore tests passed");

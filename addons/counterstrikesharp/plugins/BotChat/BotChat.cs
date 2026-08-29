// BotChat plugin: bots greet at match start ("gl", "hf", ...) and wrap up
// at match end ("gg", "ggs", ... , occasionally "ez").
//
// Convars:
//   botchat_enabled        - master switch (default 1)
//   botchat_start_enabled  - greetings at match start (default 1)
//   botchat_end_enabled    - goodbyes at match end (default 1)

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotChat;

public class BotChatPlugin : BasePlugin
{
    public override string ModuleName => "BotChat";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "ed0ard";
    public override string ModuleDescription =>
        "Bots say a greeting at match start and a goodbye at match end";

    // Messages a bot can greet with at match start (uniform pick).
    private static readonly (string message, int weight)[] StartMessages =
    {
        ("gl", 1), ("hf", 1), ("glhf", 1), ("hfhf", 1), ("good luck", 1), ("have fun", 1),
    };

    // Messages a bot can say at match end (weighted pick, ez is rare).
    private static readonly (string message, int weight)[] EndMessages =
    {
        ("gg", 45),
        ("ggs", 30),
        ("GG", 20),
        ("ez", 5),
    };

    private const int MaxSpeakersPerTeam = 5;

    // Random gap between two bot messages (seconds). Keeps chat from looking
    // like a scripted burst.
    private const float MinGap = 1.2f;
    private const float MaxGap = 3.0f;

    public FakeConVar<bool> Enabled = new("botchat_enabled", "Enable bot chat messages", true);
    public FakeConVar<bool> StartEnabled = new("botchat_start_enabled", "Bots greet at match start", true);
    public FakeConVar<bool> EndEnabled = new("botchat_end_enabled", "Bots say goodbye at match end", true);

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventBeginNewMatch>(OnBeginNewMatch);
        RegisterEventHandler<EventCsWinPanelMatch>(OnCsWinPanelMatch);
    }

    private HookResult OnBeginNewMatch(EventBeginNewMatch @event, GameEventInfo info)
    {
        if (!Enabled.Value || !StartEnabled.Value)
            return HookResult.Continue;

        SayAcrossTeams(StartMessages, uniform: true, baseDelay: 1.0f);
        return HookResult.Continue;
    }

    private HookResult OnCsWinPanelMatch(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        if (!Enabled.Value || !EndEnabled.Value)
            return HookResult.Continue;

        SayAcrossTeams(EndMessages, uniform: false, baseDelay: 1.5f);
        return HookResult.Continue;
    }

    // Picks 1..5 random bots per team (humans and taken-over bots excluded)
    // and makes each say one message, staggered in time.
    private void SayAcrossTeams((string message, int weight)[] pool, bool uniform, float baseDelay)
    {
        foreach (var team in new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist })
        {
            var bots = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
                .Where(p => p.IsValid
                    && p.IsBot
                    && !p.IsHLTV
                    && p.Team == team
                    && !p.HasBeenControlledByPlayerThisRound)
                .ToList();
            if (bots.Count == 0)
                continue;

            int count = Math.Min(MaxSpeakersPerTeam, bots.Count);
            int speakers = Random.Shared.Next(1, count + 1);

            // Fisher-Yates shuffle, take the first `speakers`.
            for (int i = bots.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (bots[i], bots[j]) = (bots[j], bots[i]);
            }

            float delay = baseDelay;
            for (int i = 0; i < speakers; i++)
            {
                var bot = bots[i];
                string message = PickMessage(pool, uniform);
                float scheduled = delay;
                AddTimer(scheduled, () => BotSay(bot, message));
                delay += MinGap + (float)Random.Shared.NextDouble() * (MaxGap - MinGap);
            }
        }
    }

    private static string PickMessage((string message, int weight)[] pool, bool uniform)
    {
        if (uniform)
            return pool[Random.Shared.Next(pool.Length)].message;

        int total = 0;
        foreach (var (_, weight) in pool)
            total += weight;

        int roll = Random.Shared.Next(total);
        foreach (var (message, weight) in pool)
        {
            if (roll < weight)
                return message;
            roll -= weight;
        }
        return pool[^1].message;
    }

    private static void BotSay(CCSPlayerController bot, string message)
    {
        if (bot == null || !bot.IsValid)
            return;
        try
        {
            bot.ExecuteClientCommandFromServer($"say {message}");
        }
        catch
        {
            Console.WriteLine($"[BotChat] failed to make bot {bot.PlayerName} say '{message}'");
        }
    }
}

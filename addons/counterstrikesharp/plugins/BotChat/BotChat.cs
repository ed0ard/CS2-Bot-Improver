// BotChat plugin: bots greet at match start ("gl", "hf", ...), wrap up at
// match end ("gg", "ggs", ... , occasionally "ez"), and react to kills:
// a headshot victim may say "ns", a killer says thanks when dying or when
// the round ends while still alive.
//
// Convars:
//   botchat_enabled              - master switch (default 1)
//   botchat_start_enabled        - greetings at match start (default 1)
//   botchat_end_enabled          - goodbyes at match end (default 1)
//   botchat_killreactions_enabled - kill reactions (ns / thanks) (default 1)

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
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "Fimall";
    public override string ModuleDescription =>
        "Bots greet at match start, say gg at match end, and chat about kills";

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

    // A headshot victim can compliment the shot (uniform pick).
    private static readonly (string message, int weight)[] NiceShotMessages =
    {
        ("ns", 1), ("nc", 1), ("nice shot", 1),
    };

    // A killer thanks someone (uniform pick).
    private static readonly (string message, int weight)[] ThanksMessages =
    {
        ("ty", 1), ("thanks", 1), ("thank u", 1), ("thx", 1),
        ("life game", 1), ("luck", 1), (":)", 1),
    };

    private const int MaxSpeakersPerTeam = 5;

    // Random gap between two bot messages (seconds). Keeps chat from looking
    // like a scripted burst.
    private const float MinGap = 1.2f;
    private const float MaxGap = 3.0f;

    // Base chance (0-1) a victim says "ns" after any kill. Headshot raises
    // it, and each kill type (blind, smoke, wallbang, jump) adds on top.
    private const double NiceShotBaseChance = 0.05;
    private const double NiceShotHeadshotBonus = 0.05;
    private const double NiceShotBuffPerKillType = 0.20;

    // Guards the end messages against double-firing: the final round's win
    // panel and the match win panel may both be dispatched by the engine.
    private bool _endSaid;

    // Bots that owe a thanks reply this round (their shot got complimented
    // with "ns"). They thank on their own death, or at round end if alive.
    private readonly HashSet<int> _owedThanks = new();

    public FakeConVar<bool> Enabled = new("botchat_enabled", "Enable bot chat messages", true);
    public FakeConVar<bool> StartEnabled = new("botchat_start_enabled", "Bots greet at match start", true);
    public FakeConVar<bool> EndEnabled = new("botchat_end_enabled", "Bots say goodbye at match end", true);
    public FakeConVar<bool> KillReactionsEnabled = new("botchat_killreactions_enabled", "Bots react to kills (ns / thanks)", true);

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(_ => _endSaid = false);
        RegisterEventHandler<EventBeginNewMatch>(OnBeginNewMatch);
        // cs_win_panel_round fires at the end of every round; FinalEvent = 1
        // marks the final round of the match. More reliable than
        // cs_win_panel_match in offline bot matches, where that event may
        // never be dispatched.
        RegisterEventHandler<EventCsWinPanelRound>(OnCsWinPanelRound);
        RegisterEventHandler<EventCsWinPanelMatch>(OnCsWinPanelMatch);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
    }

    private HookResult OnBeginNewMatch(EventBeginNewMatch @event, GameEventInfo info)
    {
        _endSaid = false;
        if (!Enabled.Value || !StartEnabled.Value)
            return HookResult.Continue;

        SayAcrossTeams(StartMessages, uniform: true, baseDelay: 1.0f);
        return HookResult.Continue;
    }

    private HookResult OnCsWinPanelRound(EventCsWinPanelRound @event, GameEventInfo info)
    {
        if (@event.FinalEvent == 0)
            return HookResult.Continue;

        SayEndMessages();
        return HookResult.Continue;
    }

    private HookResult OnCsWinPanelMatch(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        SayEndMessages();
        return HookResult.Continue;
    }

    private void SayEndMessages()
    {
        if (_endSaid)
            return;
        if (!Enabled.Value || !EndEnabled.Value)
            return;

        _endSaid = true;
        SayAcrossTeams(EndMessages, uniform: false, baseDelay: 1.5f);
    }

    // Clears per-round state.
    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _owedThanks.Clear();
        return HookResult.Continue;
    }

    // Round end: every bot that got complimented this round and is still
    // alive replies thanks individually.
    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (!Enabled.Value || !KillReactionsEnabled.Value)
            return HookResult.Continue;

        var aliveThankers = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(p => p.IsValid
                && p.IsBot
                && !p.IsHLTV
                && !p.HasBeenControlledByPlayerThisRound
                && p.PawnIsAlive
                && _owedThanks.Contains(p.Slot))
            .ToList();
        if (aliveThankers.Count == 0)
            return HookResult.Continue;

        // Every one of them talks, staggered like a natural conversation.
        float delay = 0.8f;
        foreach (var bot in aliveThankers)
        {
            string message = PickMessage(ThanksMessages, uniform: true);
            float scheduled = delay;
            AddTimer(scheduled, () => BotSay(bot, message));
            delay += MinGap + (float)Random.Shared.NextDouble() * (MaxGap - MinGap);
        }

        return HookResult.Continue;
    }

    // Death reactions: the headshot victim may compliment, the complimented
    // killer replies thanks after its own death.
    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        var attacker = @event.Attacker;

        // Log EVERY death to the server console so we can tell apart a missed
        // event from a failed probability roll.
        Console.WriteLine(
            $"[BotChat] death event: victim={victim?.PlayerName ?? "?"}(bot={victim?.IsBot}) " +
            $"killer={attacker?.PlayerName ?? "world"}(bot={attacker?.IsBot}) " +
            $"headshot={@event.Headshot} blind={@event.Attackerblind} smoke={@event.Thrusmoke} " +
            $"wall={@event.Penetrated} air={@event.Attackerinair}");

        if (!Enabled.Value || !KillReactionsEnabled.Value)
            return HookResult.Continue;

        if (victim == null || !victim.IsValid || !victim.IsBot || victim.IsHLTV)
            return HookResult.Continue;

        string victimName = victim.PlayerName;
        string attackerName = attacker != null && attacker.IsValid ? attacker.PlayerName : "world";

        // 1) Victim: compliment a kill (ns / nc / nice shot). Any kill can
        // trigger; headshot raises the chance. If it rolls in and the killer
        // is a bot, it owes a thanks reply (on its death or at round end).
        double chance = NiceShotBaseChance;
        if (@event.Headshot) chance += NiceShotHeadshotBonus;
        if (@event.Attackerblind) chance += NiceShotBuffPerKillType;
        if (@event.Thrusmoke) chance += NiceShotBuffPerKillType;
        if (@event.Penetrated > 0) chance += NiceShotBuffPerKillType;
        if (@event.Attackerinair) chance += NiceShotBuffPerKillType;

        double roll = Random.Shared.NextDouble();
        bool complimented = roll < chance;

        Console.WriteLine(
            $"[BotChat] ns roll: victim={victimName} killer={attackerName} " +
            $"chance={chance:P0} roll={roll:P2} -> {(complimented ? "ns" : "silent")} " +
            $"(headshot={@event.Headshot} blind={@event.Attackerblind} smoke={@event.Thrusmoke} " +
            $"wall={@event.Penetrated} air={@event.Attackerinair})");

        if (complimented)
        {
            if (attacker != null && attacker.IsValid && attacker.IsBot
                && !attacker.IsHLTV
                && !attacker.HasBeenControlledByPlayerThisRound
                && attacker.Slot != victim.Slot)
            {
                _owedThanks.Add(attacker.Slot);
                Console.WriteLine(
                    $"[BotChat] owed-thanks += slot {attacker.Slot} ({attacker.PlayerName})");
            }
            AddTimer(0.6f, () => BotSay(victim, PickMessage(NiceShotMessages, uniform: true)));
        }

        // 2) Complimented killer: reply thanks after dying (one reply only;
        // the owed entry is consumed here so the round end won't repeat it).
        if (attacker != null && attacker.IsValid && attacker.IsBot && !attacker.IsHLTV
            && !attacker.HasBeenControlledByPlayerThisRound
            && attacker.Slot != victim.Slot
            && _owedThanks.Remove(attacker.Slot))
        {
            Console.WriteLine(
                $"[BotChat] death-thanks: killer={attacker.PlayerName} (slot {attacker.Slot}) replies to {victimName}");
            AddTimer(0.6f, () => BotSay(attacker, PickMessage(ThanksMessages, uniform: true)));
        }

        return HookResult.Continue;
    }

    // Picks 1..N random bots from a given list and staggers one message each.
    private void SayRandom(
        List<CCSPlayerController> bots,
        (string message, int weight)[] pool,
        bool uniform,
        float baseDelay)
    {
        if (bots.Count == 0)
            return;

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

            SayRandom(bots, pool, uniform, baseDelay);
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
        {
            Console.WriteLine($"[BotChat] say skipped: bot invalid");
            return;
        }
        try
        {
            bot.ExecuteClientCommandFromServer($"say {message}");
            Console.WriteLine($"[BotChat] said '{message}' as {bot.PlayerName} (slot {bot.Slot})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BotChat] failed to make bot {bot.PlayerName} say '{message}': {ex.Message}");
        }
    }
}

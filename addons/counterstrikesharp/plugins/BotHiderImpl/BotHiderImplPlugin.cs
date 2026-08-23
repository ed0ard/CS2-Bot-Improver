using BotHiderApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using HarmonyLib;

namespace BotHiderImpl;

public class BotHiderImplPlugin : BasePlugin
{
    public override string ModuleName => "BotHiderImpl";
    public override string ModuleVersion => "0.3.8";
    public override string ModuleAuthor => "XBribo";
    public override string ModuleDescription =>
        "BotHider CSS Plugin";

    public static PluginCapability<IBotHiderApi> Capability { get; } =
        new("bothider:api");

    private SharedMemoryClient? _client;
    private IBotHiderApi? _api;
    private readonly string[] _appliedCrosshair = new string[64];
    private readonly uint[] _appliedScoreboardFlair = new uint[64];
    private readonly ulong[] _observedIncarnations = new ulong[64];
    private CounterStrikeSharp.API.Modules.Timers.Timer? _fastApplyTimer;
    private int _fastApplyRemaining;
    private Harmony? _harmony;

    public override void Load(bool hotReload)
    {
        // Inject the visible-write actions so SetPersonaName / SetBotSteamId
        // also update the scoreboard
        _client = new SharedMemoryClient(
            ApplyVisibleName,
            ApplyVisibleSid,
            ApplyVisibleScoreboardFlair,
            ApplyVisibleCrosshair);
        _api = new BotHiderCapabilityApi(_client);
        _client.TryConnect();
        Capabilities.RegisterPluginCapability(Capability, () => _api);

        // IsBot override
        IsBotPatch.Api = _client;
        _harmony = new Harmony("net.linyz.bothider.isbot");
        _harmony.PatchAll(typeof(BotHiderImplPlugin).Assembly);

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        AddTimer(2.0f, ApplyManagedSlots, TimerFlags.REPEAT);
        StartFastApplyWindow();
    }

    public override void Unload(bool hotReload)
    {
        // Undo the patch first
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;
        IsBotPatch.Api = null;
        _api = null;
        _fastApplyTimer?.Kill();
        _fastApplyTimer = null;
        _client?.Dispose();
    }

    // Clears presentation caches when a new map starts
    private void OnMapStart(string mapName)
    {
        ResetAppliedState();
        StartFastApplyWindow();
    }

    // Clears presentation caches when the current map ends
    private void OnMapEnd()
    {
        ResetAppliedState();
    }

    // Clears presentation caches for one disconnected slot
    private void OnClientDisconnect(int slot)
    {
        ResetAppliedSlot(slot, 0UL);
    }

    // Round start — respawn managed bots that ended the prior round dead.
    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        StartFastApplyWindow();
        AddTimer(0.3f, RespawnDeadManagedBots);
        return HookResult.Continue;
    }

    // Player connect full — start early retries while controllers settle
    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        StartFastApplyWindow();
        return HookResult.Continue;
    }

    // Player spawn — retry visible fields during freeze time
    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        StartFastApplyWindow();
        return HookResult.Continue;
    }

    // Player death — retry fields that engine lifecycle code may overwrite
    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        StartFastApplyWindow();
        return HookResult.Continue;
    }

    // Schedules managed bots to approve a newly started vote
    [GameEventHandler]
    public HookResult OnVoteOptions(EventVoteOptions @event, GameEventInfo info)
    {
        Server.NextFrame(AcceptVoteForManagedBots);
        return HookResult.Continue;
    }

    // Casts a yes vote from every valid managed bot
    private void AcceptVoteForManagedBots()
    {
        if (_client == null) return;

        var voteController = Utilities
            .FindAllEntitiesByDesignerName<CVoteController>("vote_controller")
            .FirstOrDefault(controller => controller.IsValid);
        if (voteController == null)
        {
            Server.PrintToConsole("[BotHider] automatic vote failed: vote controller not found");
            return;
        }

        Span<int> votesCast = voteController.VotesCast;
        Span<int> optionCounts = voteController.VoteOptionCount;
        int onlyTeam = voteController.OnlyTeamToVote;
        int accepted = 0;

        foreach (int slot in _client.GetManagedSlots())
        {
            if ((uint)slot >= (uint)votesCast.Length) continue;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid) continue;
            if (onlyTeam >= (int)CsTeam.Terrorist && (int)player.Team != onlyTeam) continue;

            votesCast[slot] = 0;
            accepted++;

            var voteEvent = new EventVoteCast(true)
            {
                Team = onlyTeam,
                Userid = player,
                VoteOption = 0
            };
            voteEvent.FireEvent(false);
        }

        if (accepted == 0) return;
        optionCounts[0] += accepted;
        Utilities.SetStateChanged(
            voteController, "CVoteController", "m_nVoteOptionCount");
        Server.PrintToConsole($"[BotHider] automatic yes votes cast={accepted}");
    }

    // Respawn any managed bot that is not alive
    private void RespawnDeadManagedBots()
    {
        if (_client == null) return;

        // Current team headcount across everyone, for balancing unassigned bots
        int tCount = 0, ctCount = 0;
        foreach (var pl in Utilities.GetPlayers())
        {
            if (pl == null || !pl.IsValid) continue;
            if (pl.Team == CsTeam.Terrorist) ++tCount;
            else if (pl.Team == CsTeam.CounterTerrorist) ++ctCount;
        }

        foreach (int slot in _client.GetManagedSlots())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid || player.PawnIsAlive) continue;

            // Dead but unassigned (team=None/Spectator): give it the smaller team first
            if (player.Team != CsTeam.Terrorist && player.Team != CsTeam.CounterTerrorist)
            {
                CsTeam target = (tCount <= ctCount) ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
                try
                {
                    player.SwitchTeam(target);
                    if (target == CsTeam.Terrorist) ++tCount; else ++ctCount;
                }
                catch (Exception e)
                {
                    Server.PrintToConsole($"[BotHider] SwitchTeam failed slot={slot}: {e.Message}");
                    continue;
                }
            }

            try
            {
                player.Respawn();
            }
            catch (Exception e)
            {
                Server.PrintToConsole($"[BotHider] respawn failed slot={slot}: {e.Message}");
            }
        }
    }

    // Set CCSPlayerController.m_iszPlayerName
    private static void ApplyVisibleName(int slot, string name)
    {
        Server.NextFrame(() =>
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid) return;
            player.PlayerName = name;
            Utilities.SetStateChanged(player, "CBasePlayerController", "m_iszPlayerName");
        });
    }

    // Write CBasePlayerController.m_steamID
    private static void ApplyVisibleSid(int slot, ulong sid)
    {
        Server.NextFrame(() =>
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid) return;
            try
            {
                Schema.SetSchemaValue(player.Handle, "CBasePlayerController", "m_steamID", sid);
                Utilities.SetStateChanged(player, "CBasePlayerController", "m_steamID");
            }
            catch (Exception e)
            {
                Server.PrintToConsole($"[BotHider] m_steamID write failed slot={slot}: {e.Message}");
            }
        });
    }

    // Write CCSPlayerController.m_szCrosshairCodes
    private static void ApplyVisibleCrosshair(int slot, string code)
    {
        Server.NextFrame(() =>
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid) return;
            try
            {
                player.CrosshairCodes = code;
                Utilities.SetStateChanged(player, "CCSPlayerController", "m_szCrosshairCodes");
            }
            catch (Exception e)
            {
                Server.PrintToConsole($"[BotHider] crosshair write failed slot={slot}: {e.Message}");
            }
        });
    }

    // Write CCSPlayerController_InventoryServices.m_rank
    private void ApplyVisibleScoreboardFlair(int slot, uint itemDefIndex)
    {
        Server.NextFrame(() =>
        {
            if (TryApplyScoreboardFlair(slot, itemDefIndex))
                _appliedScoreboardFlair[slot] = itemDefIndex;
        });
    }

    // Opens a short high-frequency apply window for early-round fields
    private void StartFastApplyWindow()
    {
        _fastApplyRemaining = Math.Max(_fastApplyRemaining, 80);
        if (_fastApplyTimer != null) return;
        _fastApplyTimer = AddTimer(0.25f, RunFastApplyTick, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    // Runs one early apply retry tick
    private void RunFastApplyTick()
    {
        ApplyManagedSlots();
        _fastApplyRemaining--;
        if (_fastApplyRemaining > 0) return;
        _fastApplyTimer?.Kill();
        _fastApplyTimer = null;
    }

    // Clears all cached presentation values
    private void ResetAppliedState()
    {
        for (int slot = 0; slot < _observedIncarnations.Length; slot++)
            ResetAppliedSlot(slot, 0UL);
    }

    // Clears cached presentation values for one native slot lifetime
    private void ResetAppliedSlot(int slot, ulong incarnation)
    {
        if (slot < 0 || slot >= _observedIncarnations.Length) return;
        _observedIncarnations[slot] = incarnation;
        _appliedCrosshair[slot] = string.Empty;
        _appliedScoreboardFlair[slot] = 0U;
    }

    // Timer body
    private void ApplyManagedSlots()
    {
        if (_client == null) return;
        int[] managedSlots = _client.GetManagedSlots();
        var managed = new bool[64];
        foreach (int slot in managedSlots)
            managed[slot] = true;
        for (int slot = 0; slot < managed.Length; slot++)
        {
            if (managed[slot]) continue;
            ResetAppliedSlot(slot, 0UL);
        }

        foreach (int slot in managedSlots)
        {
            ulong incarnation = _client.GetSlotIncarnation(slot);
            if (_observedIncarnations[slot] != incarnation)
                ResetAppliedSlot(slot, incarnation);

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid) continue;

            ReconcileVisibleIdentity(_client, slot, player);

            int ping = _client.GetPing(slot);
            if (ping > 0)
            {
                try
                {
                    // m_iPing not networked: write the field only, no SetStateChanged
                    Schema.SetSchemaValue(player.Handle, "CCSPlayerController", "m_iPing", ping);
                }
                catch (Exception e)
                {
                    Server.PrintToConsole($"[BotHider] m_iPing write failed slot={slot}: {e.Message}");
                }
            }

            string cross = _client.GetCrosshairCode(slot);
            if (_appliedCrosshair[slot] != cross ||
                !string.Equals(player.CrosshairCodes, cross, StringComparison.Ordinal))
            {
                try
                {
                    // Publish the crosshair code through the controller network state
                    player.CrosshairCodes = cross;
                    Utilities.SetStateChanged(player, "CCSPlayerController", "m_szCrosshairCodes");
                    _appliedCrosshair[slot] = cross;
                }
                catch (Exception e)
                {
                    Server.PrintToConsole($"[BotHider] crosshair write failed slot={slot}: {e.Message}");
                }
            }

            uint flair = _client.GetScoreboardFlair(slot);
            if (_appliedScoreboardFlair[slot] != flair ||
                !ScoreboardFlairMatches(player, flair))
            {
                if (TryApplyScoreboardFlair(slot, flair))
                    _appliedScoreboardFlair[slot] = flair;
            }
        }
    }

    // Restores the native published name and SteamID on the controller
    private static void ReconcileVisibleIdentity(SharedMemoryClient client, int slot,
                                                 CCSPlayerController player)
    {
        string name = client.GetPersonaName(slot);
        if (!string.Equals(player.PlayerName, name, StringComparison.Ordinal))
        {
            player.PlayerName = name;
            Utilities.SetStateChanged(player, "CBasePlayerController", "m_iszPlayerName");
        }

        ulong steamId = client.GetBotSteamId(slot);
        if (player.SteamID == steamId) return;
        try
        {
            Schema.SetSchemaValue(player.Handle, "CBasePlayerController", "m_steamID", steamId);
            Utilities.SetStateChanged(player, "CBasePlayerController", "m_steamID");
        }
        catch (Exception e)
        {
            Server.PrintToConsole($"[BotHider] m_steamID reconcile failed slot={slot}: {e.Message}");
        }
    }

    // Returns whether every scoreboard flair rank already matches
    private static bool ScoreboardFlairMatches(CCSPlayerController player, uint itemDefIndex)
    {
        var inventory = player.InventoryServices;
        if (inventory == null) return false;
        var ranks = inventory.Rank;
        if (ranks.Length == 0) return false;
        for (int index = 0; index < ranks.Length; index++)
        {
            if ((uint)ranks[index] != itemDefIndex) return false;
        }
        return true;
    }

    // Apply the scoreboard flair rank span for one player
    private static bool TryApplyScoreboardFlair(int slot, uint itemDefIndex)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player == null || !player.IsValid) return false;
        try
        {
            var inventory = player.InventoryServices;
            if (inventory == null) return false;
            var ranks = inventory.Rank;
            if (ranks.Length == 0) return false;
            for (int i = 0; i < ranks.Length; i++)
                SetScoreboardFlairRank(player, ranks, i, itemDefIndex);
            TrySetScoreboardStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
            return true;
        }
        catch (Exception e)
        {
            Server.PrintToConsole($"[BotHider] scoreboard flair write failed slot={slot}: {e.Message}");
            return false;
        }
    }

    // Writes one rank entry and marks that offset dirty
    private static void SetScoreboardFlairRank(CCSPlayerController player, Span<MedalRank_t> ranks,
                                               int index, uint itemDefIndex)
    {
        ranks[index] = (MedalRank_t)itemDefIndex;
        TrySetScoreboardStateChanged(
            player,
            "CCSPlayerController_InventoryServices",
            "m_rank",
            index * sizeof(uint));
    }

    // Calls SetStateChanged while tolerating schema differences
    private static void TrySetScoreboardStateChanged(CBaseEntity entity, string className,
                                                     string fieldName, int extraOffset = 0)
    {
        try
        {
            Utilities.SetStateChanged(entity, className, fieldName, extraOffset);
        }
        catch
        {
            // Scoreboard fields vary across game/CSS builds
        }
    }

    // bh_status — dump every managed slot's state
    [ConsoleCommand("bh_status", "List all BotHider-managed slots")]
    [CommandHelper(0, "", CommandUsage.CLIENT_AND_SERVER)]
    public void OnStatus(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_client == null) { cmd.ReplyToCommand("[BotHider] not initialized"); return; }
        // Hook/sig resolution line: ok only if every signature resolved
        var sigs = _client.GetSignatures();
        if (sigs.Length > 0)
        {
            bool allOk = sigs.All(s => s.Addr != 0);
            string detail = string.Join(" ", sigs.Select(s => $"{s.Name}={s.Addr:X16}"));
            cmd.ReplyToCommand($"[BotHider] hooks: {(allOk ? "ok" : "FAIL")} | {detail}");
        }
        var slots = _client.GetManagedSlots();
        cmd.ReplyToCommand($"[BotHider] managed slots: {slots.Length}");
        foreach (int s in slots)
        {
            var p = Utilities.GetPlayerFromSlot(s);
            string isBot = (p != null && p.IsValid) ? p.IsBot.ToString() : "n/a";
            cmd.ReplyToCommand(
                $"  slot={s} incarnation={_client.GetSlotIncarnation(s)} " +
                $"sid={_client.GetBotSteamId(s)}/{_client.GetBaseBotSteamId(s)} " +
                $"name='{_client.GetPersonaName(s)}'/'{_client.GetBasePersonaName(s)}' " +
                $"ping={_client.GetPing(s)} " +
                $"crosshair='{_client.GetCrosshairCode(s)}' " +
                $"avatar={_client.HasBotAvatar(s)}/{_client.GetConfiguredAvatarSize(s)}B " +
                $"isbot={isBot}");
        }
    }

    // bh_setsid <slot> <sid64> — set a bot's SteamID64
    [ConsoleCommand("bh_setsid", "Set a bot's SteamID64: bh_setsid <slot> <sid64>")]
    public void OnSetSid(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_client == null) { cmd.ReplyToCommand("[BotHider] not initialized"); return; }
        if (cmd.ArgCount < 3 || !int.TryParse(cmd.GetArg(1), out int slot)
            || !ulong.TryParse(cmd.GetArg(2), out ulong sid))
        { cmd.ReplyToCommand("usage: bh_setsid <slot> <sid64>"); return; }
        bool ok = _client.SetBotSteamId(slot, sid);
        cmd.ReplyToCommand($"[BotHider] SetBotSteamId({slot},{sid}) -> {ok}");
    }

    // bh_setname <slot> <name> — set a bot's persona name
    [ConsoleCommand("bh_setname", "Set a bot's name: bh_setname <slot> <name>")]
    public void OnSetName(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_client == null) { cmd.ReplyToCommand("[BotHider] not initialized"); return; }
        if (cmd.ArgCount < 3 || !int.TryParse(cmd.GetArg(1), out int slot))
        { cmd.ReplyToCommand("usage: bh_setname <slot> <name>"); return; }
        string name = cmd.GetArg(2);
        bool ok = _client.SetPersonaName(slot, name);
        string appliedName = ok ? _client.GetPersonaName(slot) : name;
        cmd.ReplyToCommand($"[BotHider] SetPersonaName({slot},'{appliedName}') -> {ok}");
    }

    // bh_setflair <slot> <item_def_index> — set a bot's scoreboard flair
    [ConsoleCommand("bh_setflair", "Set a bot's scoreboard flair: bh_setflair <slot> <item_def_index>")]
    public void OnSetFlair(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_client == null) { cmd.ReplyToCommand("[BotHider] not initialized"); return; }
        if (cmd.ArgCount < 3 || !int.TryParse(cmd.GetArg(1), out int slot)
            || !uint.TryParse(cmd.GetArg(2), out uint itemDefIndex))
        { cmd.ReplyToCommand("usage: bh_setflair <slot> <item_def_index>"); return; }
        bool ok = _client.SetScoreboardFlair(slot, itemDefIndex);
        cmd.ReplyToCommand($"[BotHider] SetScoreboardFlair({slot},{itemDefIndex}) -> {ok}");
    }

    // bh_setcrosshair <slot> <code> — set a bot's crosshair code
    [ConsoleCommand("bh_setcrosshair", "Set a bot's crosshair: bh_setcrosshair <slot> <code>")]
    public void OnSetCrosshair(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_client == null) { cmd.ReplyToCommand("[BotHider] not initialized"); return; }
        if (cmd.ArgCount < 3 || !int.TryParse(cmd.GetArg(1), out int slot))
        { cmd.ReplyToCommand("usage: bh_setcrosshair <slot> <code>"); return; }
        string code = cmd.GetArg(2);
        bool ok = _client.SetCrosshairCode(slot, code);
        cmd.ReplyToCommand($"[BotHider] SetCrosshairCode({slot},'{code}') -> {ok}");
    }

    // bh_setavatar <slot> <png_path|0> applies or clears a custom avatar
    [ConsoleCommand("bh_setavatar", "Set a bot avatar: bh_setavatar <slot> <png_path|0>")]
    [CommandHelper(2, "<slot> <png_path|0>", CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnSetAvatar(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_client == null)
        {
            cmd.ReplyToCommand("[BotHider] not initialized");
            return;
        }
        if (cmd.ArgCount < 3 || !int.TryParse(cmd.GetArg(1), out int slot))
        {
            cmd.ReplyToCommand("usage: bh_setavatar <slot> <png_path|0>");
            return;
        }

        string path = cmd.GetArg(2);
        bool ok = _client.TrySetBotAvatar(slot, path, out string error);
        cmd.ReplyToCommand(ok
            ? path == "0"
                ? $"[BotHider] avatar clear queued slot={slot}"
                : $"[BotHider] avatar queued slot={slot} bytes={_client.GetConfiguredAvatarSize(slot)}"
            : $"[BotHider] avatar rejected slot={slot}: {error}");
    }

    // bh_disguise <0|1> — toggle the m_bFakePlayer disguise
    [ConsoleCommand("bh_disguise", "Toggle disguise: bh_disguise <0|1>")]
    public void OnDisguise(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_client == null) { cmd.ReplyToCommand("[BotHider] not initialized"); return; }
        if (cmd.ArgCount < 2 || !int.TryParse(cmd.GetArg(1), out int v))
        { cmd.ReplyToCommand("usage: bh_disguise <0|1>"); return; }
        bool enabled = v != 0;
        bool ok = _client.SetDisguise(enabled);
        cmd.ReplyToCommand($"[BotHider] disguise -> {(enabled ? "ON" : "OFF")} ({ok})");
    }

    // bh_namesource <0|1> — 0=botprofile name (default), 1=bot_info.json name
    [ConsoleCommand("bh_namesource", "Set display-name source: bh_namesource <0|1> (0=botprofile 1=bot_info)")]
    public void OnNameSource(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_client == null) { cmd.ReplyToCommand("[BotHider] not initialized"); return; }
        if (cmd.ArgCount < 2 || !int.TryParse(cmd.GetArg(1), out int v))
        { cmd.ReplyToCommand("usage: bh_namesource <0|1> (0=botprofile 1=bot_info)"); return; }
        bool useBotInfo = v != 0;
        bool ok = _client.SetNameSource(useBotInfo);
        cmd.ReplyToCommand($"[BotHider] name source -> {(useBotInfo ? "bot_info" : "botprofile")} ({ok})");
    }
}

internal sealed class BotHiderCapabilityApi : IBotHiderApi
{
    private readonly SharedMemoryClient _client;

    public BotHiderCapabilityApi(SharedMemoryClient client)
    {
        _client = client;
    }

    // Returns whether the slot is managed by BotHider.
    public bool IsManagedBot(int slot) => _client.IsManagedBot(slot);

    // Returns the current synthetic SteamID64 for the slot.
    public ulong GetBotSteamId(int slot) => _client.GetBotSteamId(slot);

    // Returns all managed engine slots.
    public int[] GetManagedSlots() => _client.GetManagedSlots();

    // Returns the current persona name for the slot.
    public string GetPersonaName(int slot) => _client.GetPersonaName(slot);

    // Returns the current visible ping for the slot.
    public int GetPing(int slot) => _client.GetPing(slot);

    // Returns the current crosshair code for the slot.
    public string GetCrosshairCode(int slot) => _client.GetCrosshairCode(slot);

    // Returns whether native has applied a custom avatar to the bot
    public bool HasBotAvatar(int slot) => _client.HasBotAvatar(slot);

    // Returns the current scoreboard flair item definition index
    public uint GetScoreboardFlair(int slot) => _client.GetScoreboardFlair(slot);

    // Returns the resolved signature table.
    public (string Name, ulong Addr)[] GetSignatures() => _client.GetSignatures();

    // Updates the synthetic SteamID64 for a managed bot.
    public bool SetBotSteamId(int slot, ulong steamId64) =>
        _client.SetBotSteamId(slot, steamId64);

    // Updates the visible PlayerName through the existing callback path.
    public bool SetPersonaName(int slot, string name) =>
        _client.SetPersonaName(slot, name);

    // Updates the visible scoreboard flair through the C# rank writer
    public bool SetScoreboardFlair(int slot, uint itemDefIndex) =>
        _client.SetScoreboardFlair(slot, itemDefIndex);

    // Set crosshair code for a managed bot, empty or "0" to clear
    public bool SetCrosshairCode(int slot, string code) =>
        _client.SetCrosshairCode(slot, code);

    // Reads and applies a PNG avatar file or clears it with "0"
    public bool SetBotAvatar(int slot, string pngPath) =>
        _client.SetBotAvatar(slot, pngPath);

    // Toggles the global disguise behavior.
    public bool SetDisguise(bool enabled) => _client.SetDisguise(enabled);

    // Toggles the global display-name source behavior.
    public bool SetNameSource(bool useBotInfo) => _client.SetNameSource(useBotInfo);
}

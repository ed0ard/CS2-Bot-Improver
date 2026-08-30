using System.Collections.Concurrent;
using System.Text.Json;
using BotHiderApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace BotObserver;

public class BotObserverPlugin : BasePlugin
{
    public override string ModuleName => "Bot Observer";
    public override string ModuleVersion => "1.2.0";
    public override string ModuleAuthor => "CS2-Bot-Improver";
    public override string ModuleDescription => "Adds broadcast-style observer bots that appear as spectators on the scoreboard.";

    private readonly ConcurrentDictionary<int, CCSPlayerController> _observers = new();
    private readonly ConcurrentQueue<string> _pendingNames = new();
    private readonly Random _rng = new();
    private readonly HashSet<string> _namePool = new(StringComparer.OrdinalIgnoreCase);

    public override void Load(bool hotReload)
    {
        LoadPlayerNamesFromBotInfo();

        AddCommand("bot_add_spec", "Add a broadcast observer bot: bot_add_spec [name]", OnBotSpec);
        AddCommandListener("bot_kick", OnBotKick, HookMode.Pre);
        RegisterListener<Listeners.OnClientPutInServer>(OnClientPutInServer);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
    }

    public override void Unload(bool hotReload)
    {
        RemoveCommand("bot_add_spec", OnBotSpec);
        RemoveCommandListener("bot_kick", OnBotKick, HookMode.Pre);
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        LogObserverStateAtRoundStart();
        return HookResult.Continue;
    }

    private void LoadPlayerNamesFromBotInfo()
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "..", "BotHider", "bot_info.json"));
            if (!File.Exists(path))
            {
                Logger.LogWarning("[BotObserver] bot_info.json not found, observer name pool is empty.");
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("players", out var players))
                return;

            foreach (var entry in players.EnumerateObject())
            {
                if (entry.Value.TryGetProperty("player_name", out var nameElement) &&
                    nameElement.ValueKind == JsonValueKind.String)
                {
                    var name = nameElement.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        _namePool.Add(name);
                }
            }

            Logger.LogInformation("[BotObserver] Unified name pool: {Count} entries.", _namePool.Count);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "[BotObserver] Failed to load bot_info.json.");
        }
    }

    private void OnBotSpec(CCSPlayerController? caller, CommandInfo info)
    {
        var name = info.ArgCount > 1 ? info.GetArg(1).Trim() : PickDefaultName();

        if (string.IsNullOrWhiteSpace(name))
        {
            info.ReplyToCommand("[BotObserver] Invalid name.");
            return;
        }

        if (_observers.Values.Any(o => o.IsValid && o.PlayerName.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
            _pendingNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            info.ReplyToCommand($"[BotObserver] Observer \"{name}\" already exists.");
            return;
        }

        // Create a real CCSBot so the profile from botprofile.db applies, then
        // the bot is moved to Spectator and excluded from BotHider's lifecycle.
        _pendingNames.Enqueue(name);
        Server.ExecuteCommand($"bot_add_t {name}");
        info.ReplyToCommand($"[BotObserver] Adding observer \"{name}\"...");
    }

    private HookResult OnBotKick(CCSPlayerController? caller, CommandInfo info)
    {
        if (info.ArgCount < 2)
            return HookResult.Continue;

        var target = info.GetArg(1).Trim();
        var observer = _observers.Values.FirstOrDefault(o =>
            o.IsValid && o.PlayerName.Equals(target, StringComparison.OrdinalIgnoreCase));

        if (observer == null)
            return HookResult.Continue;

        KickObserver(observer);
        info.ReplyToCommand($"[BotObserver] Removed observer \"{target}\".");
        return HookResult.Handled;
    }

    private void OnClientDisconnect(int slot)
    {
        UnregisterObserverSlot(slot);

        var stale = _observers
            .Where(kv => !kv.Value.IsValid || kv.Value.Slot == slot)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in stale)
            _observers.TryRemove(key, out _);
    }

    private void OnClientPutInServer(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player == null || !player.IsValid || !player.IsBot || player.IsHLTV)
            return;

        if (!_pendingNames.TryDequeue(out var name))
            return;

        // Let the controller settle before switching teams (matches the proven 1.0.1 flow)
        AddTimer(0.3f, () => FinalizeObserver(slot, name), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void FinalizeObserver(int slot, string name)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player == null || !player.IsValid)
        {
            Logger.LogWarning("[BotObserver] Observer \"{Name}\" disconnected before setup.", name);
            return;
        }

        // Keep the slot out of BotHider's respawn/team logic from this point on
        RegisterObserverSlot(slot);

        ApplyObserverName(player, name);

        player.Connected = PlayerConnectedState.Connected;
        Utilities.SetStateChanged(player, "CBasePlayerController", "m_iConnected");

        if (player.TeamNum != (int)CsTeam.Spectator)
            player.ChangeTeam(CsTeam.Spectator);

        if (player.UserId.HasValue)
            _observers[player.UserId.Value] = player;

        Logger.LogInformation(
            "[BotObserver] \"{Name}\" is now an observer (slot {Slot}, team {Team}).",
            name, slot, player.TeamNum);
    }

    private void LogObserverStateAtRoundStart()
    {
        foreach (var observer in _observers.Values)
        {
            if (!observer.IsValid)
                continue;

            Logger.LogInformation(
                "[BotObserver] round check: name=\"{Name}\" slot={Slot} team={Team}.",
                observer.PlayerName, observer.Slot, observer.TeamNum);
        }
    }

    private void RegisterObserverSlot(int slot)
    {
        if (slot < 0)
            return;

        var api = new PluginCapability<IBotHiderApi>("bothider:api").Get();
        api?.SetObserverSlot(slot, true);
    }

    private void UnregisterObserverSlot(int slot)
    {
        if (slot < 0)
            return;

        var api = new PluginCapability<IBotHiderApi>("bothider:api").Get();
        api?.SetObserverSlot(slot, false);
    }

    private void ApplyObserverName(CCSPlayerController player, string name)
    {
        if (player == null || !player.IsValid)
            return;

        var api = new PluginCapability<IBotHiderApi>("bothider:api").Get();
        bool named = false;

        if (api != null && player.Slot >= 0 && api.IsManagedBot(player.Slot))
            named = api.SetPersonaName(player.Slot, name);

        if (!named)
        {
            player.PlayerName = name;
            Utilities.SetStateChanged(player, "CBasePlayerController", "m_iszPlayerName");
        }
    }

    private void KickObserver(CCSPlayerController observer)
    {
        UnregisterObserverSlot(observer.Slot);

        if (observer.UserId.HasValue)
            _observers.TryRemove(observer.UserId.Value, out _);

        if (observer.UserId.HasValue)
            Server.ExecuteCommand($"kickid {observer.UserId.Value} \"Observer removed\"");
        else
            Server.ExecuteCommand($"kick \"{observer.PlayerName.Replace("\"", "")}\"");
    }

    private string PickDefaultName()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var o in _observers.Values.Where(o => o.IsValid))
            used.Add(o.PlayerName);

        var allPlayers = Utilities.GetPlayers();
        if (allPlayers != null)
            foreach (var p in allPlayers)
                if (p != null && p.IsValid)
                    used.Add(p.PlayerName);

        foreach (var pending in _pendingNames)
            used.Add(pending);

        var free = _namePool.Where(n => !used.Contains(n)).ToList();
        return free.Count > 0 ? free[_rng.Next(free.Count)] : $"Observer {_observers.Count + 1}";
    }
}

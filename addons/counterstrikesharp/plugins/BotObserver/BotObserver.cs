using System.Collections.Concurrent;
using System.Text.Json;
using BotHiderApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotObserver;

public class BotObserverPlugin : BasePlugin
{
    public override string ModuleName => "Bot Observer";
    public override string ModuleVersion => "1.0.1";
    public override string ModuleAuthor => "CS2-Bot-Improver";
    public override string ModuleDescription => "Adds broadcast-style observer bots that appear as spectators on the scoreboard.";

    private readonly ConcurrentDictionary<int, CCSPlayerController> _observers = new();
    private readonly ConcurrentQueue<string> _pendingNames = new();
    private readonly Random _rng = new();
    private readonly HashSet<string> _namePool = new(StringComparer.OrdinalIgnoreCase);

    public override void Load(bool hotReload)
    {
        LoadPlayerNamesFromBotInfo();

        AddCommand("bot_spec", "Add a broadcast observer bot: bot_spec [name]", OnBotSpec);
        AddCommand("bot_specs", "List all observer bots", OnBotSpecs);
        AddCommandListener("bot_kick", OnBotKick, HookMode.Pre);
        RegisterListener<Listeners.OnClientPutInServer>(OnClientPutInServer);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
    }

    public override void Unload(bool hotReload)
    {
        RemoveCommand("bot_spec", OnBotSpec);
        RemoveCommand("bot_specs", OnBotSpecs);
        RemoveCommandListener("bot_kick", OnBotKick, HookMode.Pre);
    }

    private void LoadPlayerNamesFromBotInfo()
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "..", "BotHider", "bot_info.json"));
            if (!File.Exists(path))
            {
                Server.PrintToConsole("[BotObserver] bot_info.json not found, observer name pool is empty.");
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

            Server.PrintToConsole($"[BotObserver] Unified name pool: {_namePool.Count} entries.");
        }
        catch (Exception e)
        {
            Server.PrintToConsole($"[BotObserver] Failed to load bot_info.json: {e.Message}");
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

        _pendingNames.Enqueue(name);
        Server.ExecuteCommand("bot_add");
        info.ReplyToCommand($"[BotObserver] Adding observer \"{name}\"...");
    }

    private void OnBotSpecs(CCSPlayerController? caller, CommandInfo info)
    {
        var list = _observers.Values.Where(o => o.IsValid).Select(o => o.PlayerName).ToList();
        if (list.Count == 0)
        {
            info.ReplyToCommand("[BotObserver] No observer bots. Use bot_spec [name].");
            return;
        }

        info.ReplyToCommand($"[BotObserver] {list.Count} observer(s): {string.Join(", ", list)}");
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
        var stale = _observers.Where(kv => kv.Value == null || !kv.Value.IsValid).Select(kv => kv.Key).ToList();
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

        AddTimer(0.3f, () => FinalizeObserver(player, name));
    }

    private void FinalizeObserver(CCSPlayerController player, string name)
    {
        if (player == null || !player.IsValid)
        {
            Server.PrintToConsole($"[BotObserver] Observer \"{name}\" disconnected before setup.");
            return;
        }

        var api = new PluginCapability<IBotHiderApi>("bothider:api").Get();
        bool named = false;

        if (api != null && player.Slot >= 0 && api.IsManagedBot(player.Slot))
            named = api.SetPersonaName(player.Slot, name);

        if (!named)
        {
            player.PlayerName = name;
            Utilities.SetStateChanged(player, "CBasePlayerController", "m_iszPlayerName");
        }

        if (player.TeamNum != (int)CsTeam.Spectator)
            player.ChangeTeam(CsTeam.Spectator);

        if (player.UserId.HasValue)
            _observers[player.UserId.Value] = player;

        Server.PrintToConsole($"[BotObserver] \"{name}\" is now an observer.");
    }

    private void KickObserver(CCSPlayerController observer)
    {
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

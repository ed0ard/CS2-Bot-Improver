using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using BotHiderApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using Microsoft.Extensions.Logging;

namespace BotObserver;

public class BotObserverPlugin : BasePlugin
{
    public override string ModuleName => "Bot Observer";
    public override string ModuleVersion => "1.2.1";
    public override string ModuleAuthor => "CS2-Bot-Improver";
    public override string ModuleDescription => "Adds broadcast-style observer bots that appear as spectators on the scoreboard.";

    private const int MaxSetupAttempts = 5;

    private readonly ConcurrentDictionary<int, CCSPlayerController> _observers = new();
    private readonly ConcurrentDictionary<int, string> _pendingObservers = new();
    private readonly Random _rng = new();

    // Key: lowercase name, Value: canonical spelling from bot_info.json
    private readonly Dictionary<string, string> _namePool = new(StringComparer.OrdinalIgnoreCase);

    public override void Load(bool hotReload)
    {
        LoadPlayerNamesFromBotInfo();

        AddCommand("bot_add_spec", "Add a broadcast observer bot: bot_add_spec [name]", OnBotSpec);
        AddCommandListener("bot_kick", OnBotKick, HookMode.Pre);
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
                        _namePool[name] = name;
                }
            }

            Logger.LogInformation("[BotObserver] Unified name pool: {Count} entries.", _namePool.Count);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "[BotObserver] Failed to load bot_info.json.");
        }
    }

    // Fuzzy case-insensitive lookup; the applied name always follows bot_info.json
    private string ResolveCanonicalName(string input)
    {
        return _namePool.TryGetValue(input, out var canonical) ? canonical : input;
    }

    private void OnBotSpec(CCSPlayerController? caller, CommandInfo info)
    {
        var input = info.ArgCount > 1 ? info.GetArg(1).Trim() : PickDefaultName();

        if (string.IsNullOrWhiteSpace(input))
        {
            info.ReplyToCommand("[BotObserver] Invalid name.");
            return;
        }

        var name = ResolveCanonicalName(input);
        if (!ReferenceEquals(name, input))
            Logger.LogInformation("[BotObserver] Resolved \"{Input}\" to canonical \"{Name}\".", input, name);

        if (_observers.Values.Any(o => o.IsValid && o.PlayerName.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
            _pendingObservers.Values.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            info.ReplyToCommand($"[BotObserver] Observer \"{name}\" already exists.");
            return;
        }

        // Empty-shell fake client: never joins T/CT, so the engine never counts
        // a missing player and never grants shorthanded compensation.
        int slot = CreateFakeClientNative();
        if (slot < 0)
        {
            info.ReplyToCommand("[BotObserver] Failed to create fake client.");
            return;
        }

        if (!_pendingObservers.TryAdd(slot, name))
        {
            info.ReplyToCommand("[BotObserver] Failed to track the new fake client.");
            return;
        }

        info.ReplyToCommand($"[BotObserver] Creating observer \"{name}\"...");
        AddTimer(
            0.1f,
            () => TryInitializeObserver(slot, name, 0),
            TimerFlags.STOP_ON_MAPCHANGE);
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
        _pendingObservers.TryRemove(slot, out _);

        var stale = _observers
            .Where(kv => !kv.Value.IsValid || kv.Value.Slot == slot)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in stale)
            _observers.TryRemove(key, out _);
    }

    private void TryInitializeObserver(int slot, string name, int attempt)
    {
        if (!_pendingObservers.TryGetValue(slot, out var pendingName) ||
            !pendingName.Equals(name, StringComparison.Ordinal))
            return;

        var player = Utilities.GetPlayerFromSlot(slot);
        if (player == null || !player.IsValid)
        {
            RetryOrFailSetup(slot, name, attempt, "player controller is not ready");
            return;
        }

        // Exclude the slot from BotHider's respawn/team lifecycle before any
        // team work happens, so a round start can never pull the observer in.
        RegisterObserverSlot(slot);

        ApplyObserverName(player, name);
        ApplyObserverState(player);

        if (player.TeamNum == (int)CsTeam.Spectator)
        {
            _pendingObservers.TryRemove(slot, out _);

            if (player.UserId.HasValue)
                _observers[player.UserId.Value] = player;

            Logger.LogInformation(
                "[BotObserver] \"{Name}\" is now an observer (slot {Slot}, team {Team}).",
                name, slot, player.TeamNum);
            return;
        }

        if (attempt >= MaxSetupAttempts)
        {
            FailObserverSetup(slot, name, $"team remained {player.TeamNum}", player);
            return;
        }

        Logger.LogInformation(
            "[BotObserver] Moving \"{Name}\" to Spectator (slot {Slot}, current team {Team}, attempt {Attempt}/{Max}).",
            name, slot, player.TeamNum, attempt + 1, MaxSetupAttempts);
        player.ChangeTeam(CsTeam.Spectator);
        ApplyObserverState(player);

        AddTimer(
            0.1f,
            () => TryInitializeObserver(slot, name, attempt + 1),
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void RetryOrFailSetup(int slot, string name, int attempt, string reason)
    {
        if (attempt >= MaxSetupAttempts)
        {
            FailObserverSetup(slot, name, reason);
            return;
        }

        AddTimer(
            0.1f,
            () => TryInitializeObserver(slot, name, attempt + 1),
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void FailObserverSetup(
        int slot,
        string name,
        string reason,
        CCSPlayerController? player = null)
    {
        _pendingObservers.TryRemove(slot, out _);
        UnregisterObserverSlot(slot);
        Logger.LogWarning("[BotObserver] Failed to create observer \"{Name}\": {Reason}.", name, reason);

        player ??= Utilities.GetPlayerFromSlot(slot);
        if (player is not { IsValid: true })
            return;

        if (player.UserId.HasValue)
            Server.ExecuteCommand($"kickid {player.UserId.Value} \"Observer setup failed\"");
        else
            player.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
    }

    private static void ApplyObserverState(CCSPlayerController player)
    {
        player.Connected = PlayerConnectedState.Connected;
        Utilities.SetStateChanged(player, "CBasePlayerController", "m_iConnected");
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

        foreach (var pending in _pendingObservers.Values)
            used.Add(pending);

        var free = _namePool.Values.Where(n => !used.Contains(n)).ToList();
        return free.Count > 0 ? free[_rng.Next(free.Count)] : $"Observer {_observers.Count + 1}";
    }

    private unsafe int CreateFakeClientNative()
    {
        nint enginePtr = ValveInterface.Engine.Pointer;
        if (enginePtr == nint.Zero)
            return -1;

        nint vtable = Marshal.ReadIntPtr(enginePtr);
        nint cfcFnPtr = Marshal.ReadIntPtr(vtable + 52 * 8);

        nint addrPtr = Marshal.StringToHGlobalAnsi("loopback");
        nint retBuf = Marshal.AllocHGlobal(8);
        Marshal.WriteInt64(retBuf, -1);

        try
        {
            var createFakeClient =
                (delegate* unmanaged[Thiscall]<nint, nint, nint, nint>)cfcFnPtr;
            createFakeClient(enginePtr, retBuf, addrPtr);
            return Marshal.ReadInt32(retBuf);
        }
        catch
        {
            return -1;
        }
        finally
        {
            Marshal.FreeHGlobal(addrPtr);
            Marshal.FreeHGlobal(retBuf);
        }
    }
}

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using Common;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace BotAI;

public record PatchInfo(string Name, nint Address, List<byte> OriginalBytes);

public static class BotOffsets
{
    // Differs by platform: Windows = 0x5128, Linux  = 0x5100.
    public static readonly int m_gameState =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? 0x5100 : 0x5128;
    // Offsets inside CSGameState
    public const int m_isRoundOver = 0x08;
    public const int m_bombState = 0x0C;
    public const int m_plantedBombsite = 0x68;

}

[MinimumApiVersion(304)]
public class BotAI : BasePlugin
{
    public override string ModuleName => "Patches - Bot AI";
    public override string ModuleVersion => "1.8.8";
    public override string ModuleAuthor => "K4ryuu & Austin (updated by ed0ard & Misaka17032 & XBribo & AmagiReina)";
    public override string ModuleDescription =>
        "Improve and fix bots' behavior comprehensively";

    private readonly List<PatchInfo> _appliedPatches = [];
    private readonly bool _isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);



    public override void Load(bool hotReload)
    {
        Logger.LogInformation("Bot AI Patches loading...");
        var patchDefinitions = _isLinux ? LinuxPatchDefinitions.All : WindowsPatchDefinitions.All;

        foreach (var name in patchDefinitions.Keys)
        {
            if (ApplyPatch(name, _isLinux)) Logger.LogInformation($"{name}: applied.");
            else Logger.LogError($"{name}: FAILED.");
        }

        RegisterEventHandler<EventPlayerSpawn>((@event, info) =>
        {
            var player = @event.Userid;
            if (player?.IsValid != true || !player.IsBot) return HookResult.Continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn?.IsValid != true
                || player.Team <= CsTeam.Spectator
                || !pawn.BotAllowActive)
                return HookResult.Continue;

            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;

            if (gameRules == null || gameRules.BombPlanted) return HookResult.Continue;

            UpdateBotBombState(pawn, player.PlayerName);
            return HookResult.Continue;
        });

        Logger.LogInformation($"Applied {_appliedPatches.Count}/{patchDefinitions.Count} patches.");
    }

    public override void Unload(bool hotReload)
    {
        Logger.LogInformation("Bot AI Patches unloading...");
        foreach (var patch in _appliedPatches) RestorePatch(patch);
        _appliedPatches.Clear();
        Logger.LogInformation("All patches restored.");
    }

    // ── Patch machinery ───────────────────────────────────────────────────────

    private bool ApplyPatch(string name, bool linux = false)
    {
        try
        {
            var patchDefinitions = linux ? LinuxPatchDefinitions.All : WindowsPatchDefinitions.All;
            if (!patchDefinitions.TryGetValue(name, out var def)) return false;

            nint sigAddr = NativeAPI.FindSignature(GameUtils.GetModulePath("server"), def.signature);
            if (sigAddr == 0) { Logger.LogError($"'{name}': signature not found."); return false; }

            nint addr = sigAddr + def.patchOffset;
            var patchBytes = ParseHex(def.patch);
            if (patchBytes.Count == 0 || !IsValid(addr)) return false;

            var origBytes = new List<byte>();
            for (int i = 0; i < patchBytes.Count; i++)
                origBytes.Add(Marshal.ReadByte(addr, i));

            if (!ValidateOrig(name, origBytes, def.expectedOriginal))
            {
                Logger.LogError($"'{name}': byte mismatch. Expected [{def.expectedOriginal}] " +
                                $"got [{string.Join(" ", origBytes.Select(b => $"{b:X2}"))}].");
                return false;
            }

            if (!MemoryPatch.SetMemAccess(addr, patchBytes.Count)) return false;
            for (int i = 0; i < patchBytes.Count; i++) Marshal.WriteByte(addr, i, patchBytes[i]);

            _appliedPatches.Add(new PatchInfo(name, addr, origBytes));
            Logger.LogInformation($"'{name}' patched at 0x{addr:X} ({patchBytes.Count} bytes).");
            return true;
        }
        catch (Exception ex) { Logger.LogError($"'{name}': {ex.Message}"); return false; }
    }

    private void RestorePatch(PatchInfo p)
    {
        try
        {
            if (!IsValid(p.Address)) return;
            if (!MemoryPatch.SetMemAccess(p.Address, p.OriginalBytes.Count)) return;
            for (int i = 0; i < p.OriginalBytes.Count; i++)
                Marshal.WriteByte(p.Address, i, p.OriginalBytes[i]);
        }
        catch (Exception ex) { Logger.LogError($"Restore '{p.Name}': {ex.Message}"); }
    }

    private bool ValidateOrig(string name, List<byte> actual, string expectedHex)
    {
        try
        {
            var tokens = expectedHex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (actual.Count != tokens.Length) return false;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i] == "?") continue;
                if (actual[i] != Convert.ToByte(tokens[i], 16)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static bool IsValid(nint addr)
    {
        if (addr == nint.Zero) return false;
        try { Marshal.ReadByte(addr); return true; }
        catch { return false; }
    }

    private static List<byte> ParseHex(string hex) =>
        [.. hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
               .Where(t => t != "?")
               .Select(t => Convert.ToByte(t, 16))];

    private bool UpdateBotBombState(CCSPlayerPawn pawn, string playerName)
    {
        try
        {
            if (pawn?.Bot?.Handle is not { } handle || handle == nint.Zero) return false;
            if (!IsValid(handle)) return false;

            nint gsPtr = handle + BotOffsets.m_gameState;
            if (!IsValid(gsPtr)) return false;
            if (Marshal.ReadByte(gsPtr + BotOffsets.m_isRoundOver) != 0) return true;

            nint bombAddr = gsPtr + BotOffsets.m_bombState;
            if (!IsValid(bombAddr)) return false;
            if (!MemoryPatch.SetMemAccess(bombAddr, sizeof(int))) return false;
            if (Marshal.ReadInt32(bombAddr) != 0) Marshal.WriteInt32(bombAddr, 0);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"UpdateBotBombState({playerName}): {ex.Message}");
            return false;
        }
    }
}

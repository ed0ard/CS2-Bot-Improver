namespace BotHiderApi;

public static class BotHiderContract
{
    public const int MaxPlayerNameUtf8Bytes = 31;
}

public enum BotIdentityMode
{
    Player = 0,
    Bot = 1,
}

// Slot is the engine player slot (CCSPlayerController.Slot.Value)
public interface IBotHiderApi
{
    bool IsManagedBot(int slot);

    ulong GetBotSteamId(int slot);

    int[] GetManagedSlots();

    string GetPersonaName(int slot);

    int GetPing(int slot);

    string GetCrosshairCode(int slot);

    // Returns whether a custom avatar is currently applied to the managed bot
    bool HasBotAvatar(int slot);

    // Returns the current scoreboard flair item definition index
    uint GetScoreboardFlair(int slot);

    // Resolved hook/signature addresses (addr==0 means unresolved)
    (string Name, ulong Addr)[] GetSignatures();

    // returns false if the slot is out of range.
    bool SetBotSteamId(int slot, ulong steamId64);

    // Set crosshair code, empty or "0" to clear
    bool SetCrosshairCode(int slot, string code);

    // Reads and applies a PNG avatar file, or clears it when pngPath is "0"
    bool SetBotAvatar(int slot, string pngPath);

    // Normalizes the name to at most 31 UTF-8 bytes and rejects an empty result
    bool SetPersonaName(int slot, string name);

    // returns false if the slot/flair is invalid
    bool SetScoreboardFlair(int slot, uint itemDefIndex);

    // Changes the global managed-bot identity mode
    bool SetIdentityMode(BotIdentityMode mode);

    // Display-name source toggle; true=bot_info.json name, false=botprofile name (affects newly created bots)
    bool SetNameSource(bool useBotInfo);
}

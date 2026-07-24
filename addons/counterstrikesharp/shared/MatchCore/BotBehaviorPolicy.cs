namespace MatchCore;

public enum BotAimMode
{
    Mixed,
    Head,
    Body,
}

public enum BotAimPriority
{
    Head,
    Jaw,
    Body,
}

public static class BotAimPolicy
{
    private static readonly HashSet<string> BodyFirstWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_awp", "weapon_ssg08", "weapon_p90", "weapon_bizon",
        "weapon_nova", "weapon_xm1014", "weapon_sawedoff", "weapon_mag7", "weapon_revolver",
    };

    public static BotAimPriority SelectPriority(BotAimMode mode, string? weapon) => mode switch
    {
        // Keep the upstream exception: forcing head aim must not make AWP bots
        // chase the smallest hitbox and stop taking otherwise valid shots.
        BotAimMode.Head when string.Equals(weapon, "weapon_awp", StringComparison.OrdinalIgnoreCase)
            => BotAimPriority.Body,
        BotAimMode.Head => BotAimPriority.Head,
        BotAimMode.Body => BotAimPriority.Body,
        _ when weapon != null && BodyFirstWeapons.Contains(weapon) => BotAimPriority.Body,
        _ => BotAimPriority.Jaw,
    };
}

public enum ScopedRifleAction
{
    Ignore,
    Keep,
    ReplaceWithM4A4,
    ReplaceWithM4A1S,
    ReplaceWithAk47,
}

public static class BotBuyPolicy
{
    public const float ScopedRifleKeepChance = 0.06f;

    public static string? NormalizeWeaponName(string? weapon)
    {
        if (string.IsNullOrWhiteSpace(weapon)) return null;
        string normalized = weapon.Trim().ToLowerInvariant();
        if (normalized.StartsWith("weapon_", StringComparison.Ordinal))
            normalized = normalized[7..];
        return normalized switch
        {
            "aug" => "weapon_aug",
            "sg556" or "sg553" => "weapon_sg556",
            _ => null,
        };
    }

    public static ScopedRifleAction SelectScopedRifleAction(
        string? eventWeapon, float keepRoll, float replacementRoll)
    {
        string? weapon = NormalizeWeaponName(eventWeapon);
        if (weapon == null) return ScopedRifleAction.Ignore;
        if (keepRoll < ScopedRifleKeepChance) return ScopedRifleAction.Keep;
        if (weapon == "weapon_sg556") return ScopedRifleAction.ReplaceWithAk47;
        return replacementRoll < 0.5f
            ? ScopedRifleAction.ReplaceWithM4A4
            : ScopedRifleAction.ReplaceWithM4A1S;
    }
}

public readonly record struct BotCallbackGeneration(int Map, int Round)
{
    public bool IsCurrent(int currentMap, int currentRound) =>
        Map == currentMap && Round == currentRound;
}

using CounterStrikeSharp.API.Core;
using System.Text.Json.Serialization;

namespace BotAI;

/// <summary>
/// Module-level toggles. Each switch controls a whole group of related
/// patches; see BotAIPatchCategories.All for the exact membership.
/// </summary>
public sealed class ModuleToggles
{
    /// <summary>Superhuman perception: unlimited vision cones + global hearing + aggressive noise investigation. Default false for casual fair play.</summary>
    public bool Awareness { get; set; } = false;

    /// <summary>Global C4 intel: pickup/beeps heard anywhere on the map and instant site knowledge on plant. Default false — this removes the post-plant information game.</summary>
    public bool BombInfo { get; set; } = false;

    /// <summary>Forced combat behavior: no fire-rate limit, hold-trigger sprays at any range, 100% dodge chance. Default true (upstream behavior).</summary>
    public bool CombatForce { get; set; } = true;

    /// <summary>Movement quality fixes: strafing unlocked, reload dodging, anti-sniper movement. Keeps vanilla randomness. Default true.</summary>
    public bool Movement { get; set; } = true;

    /// <summary>Vision/attention enhancements: watching approach points, always noticing enemies. Default true.</summary>
    public bool VisionAttention { get; set; } = true;

    /// <summary>Bomb plant/defuse behavioral fixes (realism only, no information cheats). Default true.</summary>
    public bool BombBehavior { get; set; } = true;

    /// <summary>Misc state machine fixes, including removing the built-in flash avoidance so bots can be blinded like humans. Default true.</summary>
    public bool StateMachine { get; set; } = true;
}

public sealed class BotAIConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 2;

    /// <summary>
    /// Legacy switch kept for backward compatibility with ConfigVersion 1 files.
    /// null  -> Modules.Awareness decides.
    /// true  -> Awareness patches are skipped even when Modules.Awareness is true.
    /// false -> Awareness patches remain enabled for legacy compatibility.
    /// Omit the field to let Modules.Awareness control the group.
    /// </summary>
    [JsonPropertyName("CasualAwareness")]
    public bool? CasualAwareness { get; set; } = null;

    [JsonPropertyName("Modules")]
    public ModuleToggles Modules { get; set; } = new();

    /// <summary>
    /// Individual patch names to skip regardless of module toggles.
    /// Unknown names are logged as warnings at load time (typo protection).
    /// </summary>
    [JsonPropertyName("DisabledPatches")]
    public List<string> DisabledPatches { get; set; } = [];
}

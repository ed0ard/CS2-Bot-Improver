using System;
using System.Text.Json.Serialization;

namespace NadeSystem;

// ═══════════════════════════════════════════════════════════════
//  Data model
//  Reads converted NadeLauncher JSON: <mapname>_<grenadeType>.json
//  Each file is a JSON array of GrenadeData entries.
// ═══════════════════════════════════════════════════════════════

public class Vec3
{
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
    [JsonPropertyName("z")] public float Z { get; set; }
}

public class GrenadeData
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("mapName")]
    public string MapName { get; set; } = "";

    // "flash" | "smoke" | "he" | "molotov"
    [JsonPropertyName("grenadeType")]
    public string GrenadeType { get; set; } = "";

    // Where the projectile spawns (recorded release point)
    [JsonPropertyName("projectilePosition")]
    public Vec3 ProjectilePosition { get; set; } = new();

    // Recorded velocity vector
    [JsonPropertyName("projectileVelocity")]
    public Vec3 ProjectileVelocity { get; set; } = new();

    // Landing position
    [JsonPropertyName("landingPosition")]
    public Vec3 LandingPosition { get; set; } = new();

    // Tags
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonIgnore] public string TeamTag { get; set; } = "";

    // ── Computed zone properties (not serialized) ────────────
    // Zone center = XY projection of projectilePosition onto the ground (Z kept as-is)
    [JsonIgnore] public float ZoneX => ProjectilePosition.X;
    [JsonIgnore] public float ZoneY => ProjectilePosition.Y;
    [JsonIgnore] public float ZoneZ => ProjectilePosition.Z;

    // Smoke = 150, Other nades = 100 (radius)
    [JsonIgnore]
    public float ZoneRadius => string.Equals(GrenadeType, "smoke",
        StringComparison.OrdinalIgnoreCase) ? 150f : 100f;
}

// ═══════════════════════════════════════════════════════════════
//  Cooldown record
// ═══════════════════════════════════════════════════════════════

public class CooldownEntry
{
    public string GrenadeId { get; set; } = "";
    public float ExpiresAt { get; set; }
}

// ═══════════════════════════════════════════════════════════════
//  Per-round throw counter
// ═══════════════════════════════════════════════════════════════

public class RoundCounter
{
    public int Flash { get; set; }
    public int Smoke { get; set; }
    public int HE { get; set; }
    public int Molotov { get; set; }
}

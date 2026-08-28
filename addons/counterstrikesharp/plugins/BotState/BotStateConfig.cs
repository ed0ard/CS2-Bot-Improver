using CounterStrikeSharp.API.Core;
using System.Text.Json.Serialization;

namespace BotState;

public sealed class IdleRepathSettings
{
    /// <summary>Seconds of low movement before the plugin requests a native repath.</summary>
    [JsonPropertyName("Seconds")]
    public float Seconds { get; set; } = 2.5f;
}

public sealed class BotStateConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 1;

    /// <summary>
    /// When false, preserve the native five-second idle-repath threshold.
    /// When true, use IdleRepath.Seconds.
    /// </summary>
    [JsonPropertyName("EnableCustomIdleRepath")]
    public bool EnableCustomIdleRepath { get; set; } = false;

    [JsonPropertyName("IdleRepath")]
    public IdleRepathSettings IdleRepath { get; set; } = new();
}

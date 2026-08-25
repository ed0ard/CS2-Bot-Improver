using CounterStrikeSharp.API.Core;
using System.Text.Json.Serialization;

namespace BotAI;

public sealed class BotAIConfig : BasePluginConfig
{
    // Keep the extended awareness patches disabled by default for a fairer bot experience.
    [JsonPropertyName("CasualAwareness")]
    public bool CasualAwareness { get; set; } = true;
}

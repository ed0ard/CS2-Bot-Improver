using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatchCore;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManagedRosterPhase
{
    Inactive,
    Cleaning,
    Reconciling,
    Binding,
    Ready,
}

public sealed record ManagedMatchRuntime(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("roster_phase")] ManagedRosterPhase RosterPhase,
    [property: JsonPropertyName("updated_at_unix_ms")] long UpdatedAtUnixMs)
{
    public const int CurrentSchemaVersion = 1;
}

public static class ManagedMatchRuntimeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string RuntimePath(string csgoRoot) =>
        Path.Combine(csgoRoot, ".csbip", "match-runtime.json");

    public static void Write(string csgoRoot, string sessionId, ManagedRosterPhase phase)
    {
        string path = RuntimePath(csgoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            var state = new ManagedMatchRuntime(
                ManagedMatchRuntime.CurrentSchemaVersion,
                sessionId,
                phase,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static bool IsPurchasingAllowed(string csgoRoot)
    {
        string activePath = PlusManagedPaths.ActiveMatchPath(csgoRoot);
        if (!File.Exists(activePath)) return true;

        try
        {
            using var active = JsonDocument.Parse(File.ReadAllText(activePath));
            if (!active.RootElement.TryGetProperty("session_id", out var sessionElement)) return false;
            string? activeSession = sessionElement.GetString();
            if (string.IsNullOrWhiteSpace(activeSession)) return false;

            var runtime = JsonSerializer.Deserialize<ManagedMatchRuntime>(
                File.ReadAllText(RuntimePath(csgoRoot)), JsonOptions);
            return IsPurchasingAllowed(activeSession, runtime);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPurchasingAllowed(string? activeSession, ManagedMatchRuntime? runtime) =>
        runtime is
        {
            SchemaVersion: ManagedMatchRuntime.CurrentSchemaVersion,
            RosterPhase: ManagedRosterPhase.Ready,
        }
        && !string.IsNullOrWhiteSpace(activeSession)
        && string.Equals(activeSession, runtime.SessionId, StringComparison.Ordinal);
}

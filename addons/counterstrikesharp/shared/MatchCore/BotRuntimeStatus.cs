using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatchCore;

public sealed record AimRuntimeStatus(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("transport")] string Transport,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("override_count")] long OverrideCount,
    [property: JsonPropertyName("head_point_count")] long HeadPointCount,
    [property: JsonPropertyName("body_point_count")] long BodyPointCount,
    [property: JsonPropertyName("error_count")] long ErrorCount,
    [property: JsonPropertyName("updated_at_unix_ms")] long UpdatedAtUnixMs);

public sealed record PurchaseRuntimeStatus(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("purchase_observed")] long PurchaseObserved,
    [property: JsonPropertyName("purchase_replaced")] long PurchaseReplaced,
    [property: JsonPropertyName("purchase_failed")] long PurchaseFailed,
    [property: JsonPropertyName("updated_at_unix_ms")] long UpdatedAtUnixMs);

public static class BotRuntimeStatusStore
{
    public const int CurrentSchemaVersion = 1;

    public static string AimPath(string csgoRoot) =>
        Path.Combine(csgoRoot, ".csbip", "aim-runtime.json");

    public static string PurchasePath(string csgoRoot) =>
        Path.Combine(csgoRoot, ".csbip", "purchase-runtime.json");

    public static void WriteAim(
        string csgoRoot,
        string transport,
        bool active,
        BotAimMode mode,
        long overrideCount,
        long headPointCount,
        long bodyPointCount,
        long errorCount) =>
        WriteAtomic(AimPath(csgoRoot), new AimRuntimeStatus(
            CurrentSchemaVersion,
            transport,
            active,
            mode.ToString().ToLowerInvariant(),
            overrideCount,
            headPointCount,
            bodyPointCount,
            errorCount,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    public static void WritePurchase(
        string csgoRoot,
        long observed,
        long replaced,
        long failed) =>
        WriteAtomic(PurchasePath(csgoRoot), new PurchaseRuntimeStatus(
            CurrentSchemaVersion,
            observed,
            replaced,
            failed,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    private static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

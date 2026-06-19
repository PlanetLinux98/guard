using System.Text.Json.Serialization;

namespace GuardWui3.Models;

public sealed class AppListItem
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("publisher")] public string? Publisher { get; set; }
    [JsonPropertyName("installLocation")] public string? InstallLocation { get; set; }
    [JsonPropertyName("publisherUrl")] public string? PublisherUrl { get; set; }
}

public sealed class AppListFile
{
    [JsonPropertyName("exported")] public string? Exported { get; set; }
    [JsonPropertyName("machine")] public string? Machine { get; set; }
    [JsonPropertyName("apps")] public AppListItem[]? Apps { get; set; }
}

// Source-generated serialization: AOT- and trim-safe (replaces the WPF
// edition's reflection-based DataContractJsonSerializer).
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppListFile))]
[JsonSerializable(typeof(AppListItem))]
[JsonSerializable(typeof(AppSettingsManifest))]
[JsonSerializable(typeof(AppSettingsManifestEntry))]
public partial class GuardJsonContext : JsonSerializerContext
{
}

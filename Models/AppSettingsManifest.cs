using System.Text.Json.Serialization;

namespace GuardWui3.Models;

// Record of one copied settings folder. RootAnchor keeps the env-var form
// (%APPDATA% etc.), not the literal path, so a restore on a fresh install
// re-anchors to the new profile regardless of username.
public sealed class AppSettingsManifestEntry
{
    [JsonPropertyName("apps")] public string[]? Apps { get; set; }
    [JsonPropertyName("root")] public string? Root { get; set; }
    [JsonPropertyName("rootAnchor")] public string? RootAnchor { get; set; }
    [JsonPropertyName("folder")] public string? Folder { get; set; }
    [JsonPropertyName("sourcePath")] public string? SourcePath { get; set; }
    [JsonPropertyName("destRelativePath")] public string? DestRelativePath { get; set; }
    [JsonPropertyName("files")] public int Files { get; set; }
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("skippedFiles")] public int SkippedFiles { get; set; }
}

public sealed class AppSettingsManifest
{
    [JsonPropertyName("exported")] public string? Exported { get; set; }
    [JsonPropertyName("machine")] public string? Machine { get; set; }
    [JsonPropertyName("userProfile")] public string? UserProfile { get; set; }
    [JsonPropertyName("restoreNote")] public string? RestoreNote { get; set; }
    [JsonPropertyName("entries")] public AppSettingsManifestEntry[]? Entries { get; set; }
}

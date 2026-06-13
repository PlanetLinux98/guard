using System.Text.Json.Serialization;

namespace GuardWui3.Models;

// Machine-readable record of one copied settings folder. RootAnchor keeps the
// environment-variable form (%APPDATA% etc.) rather than the literal user path,
// so a restore on a fresh Windows install re-anchors to the NEW profile's
// folders regardless of the username.
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

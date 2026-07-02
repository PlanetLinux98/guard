using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GuardWui3.Models;

// The slice of GitHub's releases/latest API response the updater reads.
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = new();
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

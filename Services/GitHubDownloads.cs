using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GuardWui3.Models;

namespace GuardWui3.Services;

// Shared GitHub plumbing for the two release consumers (the GUARD updater and
// the winget bootstrap): one client recipe, one releases/latest fetch, one
// streamed download loop. Caller-specific validation (tag present, bundle
// asset present) stays with each caller.
public static class GitHubDownloads
{
    public static HttpClient CreateClient(TimeSpan timeout)
    {
        var c = new HttpClient();
        // GitHub's API rejects requests with no User-Agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("GUARD-Updater/" + GuardPaths.AppVersion);
        c.Timeout = timeout;
        return c;
    }

    // Null on any failure (offline, rate-limited, bad response); callers treat
    // that as "could not check". Only a REQUESTED cancellation rethrows - an
    // HttpClient timeout also surfaces as OperationCanceledException, and that
    // must stay a quiet null, not an exception up an async-void chain.
    public static async Task<GitHubRelease?> FetchLatestAsync(
        HttpClient http, string apiUrl, CancellationToken ct)
    {
        try
        {
            string json = await http.GetStringAsync(apiUrl, ct);
            return JsonSerializer.Deserialize(json, GuardJsonContext.Default.GitHubRelease);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            DebugLog.Log("github", "releases/latest fetch failed: " + apiUrl, ex);
            return null;
        }
    }

    public static async Task DownloadAssetAsync(
        HttpClient http, GitHubAsset asset, string destPath, Action<long>? onBytes, CancellationToken ct)
    {
        using var resp = await http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var src = await resp.Content.ReadAsStreamAsync(ct);
        using var dst = File.Create(destPath);
        var buf = new byte[81920];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            onBytes?.Invoke(done);
        }
    }
}

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

    // A release resolved WITHOUT the REST API, for when api.github.com refuses.
    // The unauthenticated API budget is 60 requests an hour per IP, shared with
    // everything else on that address, so a busy or carrier-grade NAT network
    // can spend it elsewhere and leave GUARD reporting "offline" while the
    // browser loads github.com perfectly well - the web pages draw on a
    // different budget. github.com/<repo>/releases/latest redirects to the
    // tagged release and .../releases/latest/download/<name> redirects to the
    // asset, neither of which is API-rate-limited. The cost is that asset names
    // must be known up front rather than read off the release, and there are no
    // release notes, so this stays the FALLBACK and the API stays primary.
    public static async Task<GitHubRelease?> FetchLatestByRedirectAsync(
        HttpClient http, string repoUrl, string[] assetNames, CancellationToken ct)
    {
        try
        {
            string tag = await ResolveLatestTagAsync(http, repoUrl, ct);
            if (tag.Length == 0) return null;

            var rel = new GitHubRelease
            {
                TagName = tag,
                HtmlUrl = repoUrl + "/releases/tag/" + tag,
            };
            foreach (string name in assetNames)
            {
                string url = repoUrl + "/releases/latest/download/" + name;
                long size = await ProbeAssetAsync(http, url, ct);
                // -1 means the release does not carry this asset, and the caller
                // decides what that costs (the updater refuses to install
                // without SHA256SUMS; the winget deps zip merely degrades). A 0
                // is a real asset whose length the CDN did not report, which
                // only costs the progress bar its percentage.
                if (size >= 0)
                    rel.Assets.Add(new GitHubAsset { Name = name, DownloadUrl = url, Size = size });
            }
            return rel;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            DebugLog.Log("github", "redirect fallback failed: " + repoUrl, ex);
            return null;
        }
    }

    // The tag /releases/latest lands on, or "" if it lands anywhere else (a repo
    // with no releases 404s rather than redirecting). Redirects are followed for
    // us, so the FINAL request's URI carries the answer; matching on the
    // /releases/tag/ marker rather than taking the last path segment keeps a
    // changed redirect chain from yielding a plausible-looking non-tag.
    private static async Task<string> ResolveLatestTagAsync(
        HttpClient http, string repoUrl, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, repoUrl + "/releases/latest");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return "";
        string path = resp.RequestMessage?.RequestUri?.AbsolutePath ?? "";
        const string marker = "/releases/tag/";
        int cut = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return cut < 0 ? "" : Uri.UnescapeDataString(path[(cut + marker.Length)..].Trim('/'));
    }

    // Content-Length for the progress bar, or -1 when the release has no such
    // asset. HEAD is followed all the way to the signed CDN URL, which is where
    // the length actually comes from; the github.com hop reports none.
    private static async Task<long> ProbeAssetAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, url);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        return resp.IsSuccessStatusCode ? resp.Content.Headers.ContentLength ?? 0 : -1;
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

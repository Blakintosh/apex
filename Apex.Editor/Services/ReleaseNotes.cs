using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Apex.Editor.Services;

/// <summary>One release's notes as GitHub publishes them: the Markdown body and the release's page.</summary>
public sealed record ReleaseNotes(string Version, string Body, string Url);

/// <summary>
/// The notes of the running version, read from its GitHub release so the release stays the one place they are
/// written (nothing is bundled to drift from it). <see cref="Fetch"/> is replaceable: the harness serves fixed notes.
/// </summary>
public static class ReleaseNotesFeed
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    /// <summary>Notes for a version (no leading "v"), or why they couldn't be read, in a sentence for the panel.</summary>
    public static Func<string, CancellationToken, Task<(ReleaseNotes? Notes, string? Error)>> Fetch { get; set; } = FromGitHub;

    private static async Task<(ReleaseNotes?, string?)> FromGitHub(string version, CancellationToken ct)
    {
        var repo = AppUpdates.Options.Repository;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Patience);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/tags/v{version}");
            request.Headers.UserAgent.ParseAdd($"apex/{version}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (null, $"There are no published notes for {version} yet.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return (null, "GitHub is limiting requests. Try again in a while.");
            if (!response.IsSuccessStatusCode)
                return (null, $"GitHub answered {(int)response.StatusCode}. Try again in a while.");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var root = doc.RootElement;
            var body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
            var url = root.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
            return string.IsNullOrWhiteSpace(body)
                ? (null, $"The release for {version} has no notes.")
                : (new ReleaseNotes(version, body, url), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "GitHub didn't answer in time.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            return (null, "GitHub couldn't be reached. Check your connection.");
        }
    }
}

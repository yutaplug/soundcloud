using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text.Json;
using SoundCloudDesktop.Models;

namespace SoundCloudDesktop.Services;

public sealed class SoundCloudApi : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private string _token = "";
    private string _userId = "";

    // Public identifier used by SoundCloud's web client. It is not a secret.
    private const string WebApiBase = "https://api-v2.soundcloud.com";
    private const string WebClientId = "Pb72ranhoyt6gw7hM7TkzUItXlMWSNSo";

    public SoundCloudApi()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 SoundCloudDesktop/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<UserProfile> GetCurrentUserAsync(string token, CancellationToken cancellationToken = default)
    {
        _token = CleanToken(token);
        using var document = await GetJsonAsync($"{WebApiBase}/me", cancellationToken);
        var root = document.RootElement;
        _userId = GetString(root, "id") ?? GetString(root, "urn")?.Split(':').Last() ?? "";
        return new UserProfile
        {
            Id = _userId,
            UserName = GetString(root, "username") ?? "SoundCloud listener",
            AvatarUrl = NormalizeImageUrl(GetString(root, "avatar_url") ?? "")
        };
    }

    public async Task<IReadOnlyList<Track>> GetLikedTracksAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_token) || string.IsNullOrWhiteSpace(_userId)) throw new InvalidOperationException("Log in first.");

        var tracks = new List<Track>();
        var nextUrl = $"{WebApiBase}/users/{Uri.EscapeDataString(_userId)}/track_likes?limit=200&offset=0&linked_partitioning=1";
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!string.IsNullOrWhiteSpace(nextUrl) && seenUrls.Add(nextUrl))
        {
            using var page = await GetJsonAsync(nextUrl, cancellationToken);
            var root = page.RootElement;
            var collection = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("collection", out var found) ? found : default;

            if (collection.ValueKind == JsonValueKind.Array)
                foreach (var item in collection.EnumerateArray()) tracks.Add(Track.FromJson(item));

            nextUrl = root.ValueKind == JsonValueKind.Object ? GetString(root, "next_href") : null;
        }

        return tracks;
    }

    public async Task<string> GetPlayableUrlAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(track.StreamUrl)) return await ResolveStreamAsync(track.StreamUrl, cancellationToken);
        if (track.Id <= 0) return "";

        return await ResolveStreamAsync($"{WebApiBase}/tracks/{track.Id}/stream", cancellationToken);
    }

    public async Task<string> DownloadStreamToTempFileAsync(string streamUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamUrl)) return "";
        var folder = Path.Combine(Path.GetTempPath(), "SoundCloudDesktop");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"track-{Guid.NewGuid():N}.mp3");

        try
        {
            using var response = await _http.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true);
            await source.CopyToAsync(destination, cancellationToken);
            return path;
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            throw;
        }
    }

    private async Task<string> ResolveStreamAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AddWebCredentials(url));
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", _token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
            return new Uri(request.RequestUri!, response.Headers.Location).ToString();
        if (!response.IsSuccessStatusCode) return "";

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return request.RequestUri?.ToString() ?? "";
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var resolved = root.ValueKind == JsonValueKind.String ? root.GetString() : GetString(root, "url") ?? GetString(root, "location");
        return string.IsNullOrWhiteSpace(resolved) ? "" : AddWebCredentials(resolved);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AddWebCredentials(url));
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", _token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var reason = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "The oauth_token was rejected. Paste a current token from SoundCloud.",
                HttpStatusCode.Forbidden => "SoundCloud’s web API denied this oauth_token (403). Refresh the token from your logged-in SoundCloud session and try again.",
                _ => $"SoundCloud returned {(int)response.StatusCode}."
            };
            throw new HttpRequestException(reason);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private string AddWebCredentials(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !IsApiHost(uri.Host)) return url;
        var separator = url.Contains('?') ? '&' : '?';
        var result = url.Contains("client_id=", StringComparison.OrdinalIgnoreCase) ? url : $"{url}{separator}client_id={Uri.EscapeDataString(WebClientId)}";
        if (!string.IsNullOrWhiteSpace(_token) && !result.Contains("oauth_token=", StringComparison.OrdinalIgnoreCase))
            result += $"&oauth_token={Uri.EscapeDataString(_token)}";
        return result;
    }

    private static bool IsApiHost(string host) =>
        host.Equals("api-v2.soundcloud.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("api.soundcloud.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("api-auth.soundcloud.com", StringComparison.OrdinalIgnoreCase);

    private static string CleanToken(string token)
    {
        var cleaned = token.Trim().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase).Replace("OAuth ", "", StringComparison.OrdinalIgnoreCase);
        if (cleaned.StartsWith("oauth_token=", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[12..].Split('&')[0];
        return cleaned;
    }

    private static string? GetString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString() : null;
    private static string NormalizeImageUrl(string url) => string.IsNullOrWhiteSpace(url) ? "" : url.Replace("-large.", "-t500x500.", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _http.Dispose();
}

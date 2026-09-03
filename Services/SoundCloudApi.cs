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

    public async Task<IReadOnlyList<Playlist>> GetLikedPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_token) || string.IsNullOrWhiteSpace(_userId)) throw new InvalidOperationException("Log in first.");

        var playlistIds = new List<long>();
        var directPlaylists = new List<Playlist>();
        var idsUrl = $"{WebApiBase}/me/playlist_likes/ids?limit=5000&linked_partitioning=1";
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!string.IsNullOrWhiteSpace(idsUrl) && seenUrls.Add(idsUrl))
        {
            using var page = await GetJsonAsync(idsUrl, cancellationToken);
            var root = page.RootElement;
            var collection = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("collection", out var found) ? found : default;

            if (collection.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in collection.EnumerateArray())
                {
                    var playlist = Playlist.FromJson(item);
                    if (playlist.Id > 0 && !string.Equals(playlist.Title, "Untitled playlist", StringComparison.Ordinal)) directPlaylists.Add(playlist);
                    else
                    {
                        var id = item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number) ? number : GetLong(item, "id");
                        if (id <= 0) id = GetLong(item, "playlist_id");
                        if (id > 0) playlistIds.Add(id);
                    }
                }
            }

            idsUrl = root.ValueKind == JsonValueKind.Object ? GetString(root, "next_href") : null;
        }

        var playlists = new List<Playlist>();
        playlists.AddRange(directPlaylists);
        foreach (var id in playlistIds.Distinct())
        {
            try
            {
                using var detail = await GetJsonAsync($"{WebApiBase}/playlists/{id}?representation=full", cancellationToken);
                var playlist = Playlist.FromJson(detail.RootElement);
                if (playlist.Id > 0) playlists.Add(playlist);
            }
            catch (HttpRequestException)
            {
                // A deleted or private playlist should not hide the rest of the liked library.
            }
        }

        return playlists;
    }

    public async Task<IReadOnlyList<Track>> GetPlaylistTracksAsync(Playlist playlist, CancellationToken cancellationToken = default)
    {
        if (playlist.Id <= 0) return Array.Empty<Track>();

        // The playlist detail endpoint can return only a small preview of the
        // tracks. The dedicated tracks endpoint is paginated and returns the
        // complete playlist.
        var entries = new List<Track>();
        var nextUrl = $"{WebApiBase}/playlists/{playlist.Id}/tracks?limit=200&offset=0&linked_partitioning=1";
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            while (!string.IsNullOrWhiteSpace(nextUrl) && seenUrls.Add(nextUrl))
            {
                using var page = await GetJsonAsync(nextUrl, cancellationToken);
                var root = page.RootElement;
                var collection = root.ValueKind == JsonValueKind.Array
                    ? root
                    : root.TryGetProperty("collection", out var found) ? found : default;

                if (collection.ValueKind == JsonValueKind.Array)
                    entries.AddRange(collection.EnumerateArray().Select(Track.FromJson));

                nextUrl = root.ValueKind == JsonValueKind.Object ? GetString(root, "next_href") : null;
            }
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404", StringComparison.OrdinalIgnoreCase))
        {
            // Some accounts/playlists do not expose the dedicated tracks route.
            // The detail response is compatible with those playlists and still
            // includes the complete track references when representation=full is used.
            entries.Clear();
            using var detail = await GetJsonAsync($"{WebApiBase}/playlists/{playlist.Id}?representation=full", cancellationToken);
            var root = detail.RootElement;
            var source = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("playlist", out var nested) ? nested : root;
            if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
                entries.AddRange(tracks.EnumerateArray().Select(Track.FromJson));
        }

        // Some playlist entries are returned as IDs or as objects containing
        // only an ID. Expand those entries so they do not appear as Untitled.
        var missingIds = entries
            .Where(track => track.Id > 0 && string.Equals(track.Title, "Untitled", StringComparison.Ordinal))
            .Select(track => track.Id)
            .Distinct()
            .ToList();
        var detailedTracks = await LoadTrackDetailsAsync(missingIds, cancellationToken);

        var result = new List<Track>(entries.Count);
        var seenIds = new HashSet<long>();
        foreach (var track in entries)
        {
            if (track.Id <= 0 || !seenIds.Add(track.Id)) continue;
            var resolvedTrack = detailedTracks.TryGetValue(track.Id, out var detailed) ? detailed : track;
            if (!string.Equals(resolvedTrack.Title, "Untitled", StringComparison.Ordinal)) result.Add(resolvedTrack);
        }

        return result;
    }

    private async Task<Dictionary<long, Track>> LoadTrackDetailsAsync(IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        var details = new Dictionary<long, Track>();
        using var limiter = new SemaphoreSlim(8);
        var tasks = ids.Select(async id =>
        {
            await limiter.WaitAsync(cancellationToken);
            try
            {
                using var document = await GetJsonAsync($"{WebApiBase}/tracks/{id}", cancellationToken);
                var track = Track.FromJson(document.RootElement);
                return (id, track);
            }
            catch (HttpRequestException)
            {
                return (id, new Track());
            }
            finally
            {
                limiter.Release();
            }
        }).ToArray();

        foreach (var (id, track) in await Task.WhenAll(tasks))
            if (track.Id > 0 && !string.Equals(track.Title, "Untitled", StringComparison.Ordinal)) details[id] = track;
        return details;
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
    private static long GetLong(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;
    private static string NormalizeImageUrl(string url) => string.IsNullOrWhiteSpace(url) ? "" : url.Replace("-large.", "-t67x67.", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _http.Dispose();
}

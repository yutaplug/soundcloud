using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text.Json;
using SoundCloudDesktop.Models;

namespace SoundCloudDesktop.Services;

public sealed class SoundCloudApi : IDisposable
{
    private sealed record HlsSegment(Uri Uri, long? Offset, long? Length);

    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly AppLog _log;
    private string _token = "";
    private string _userId = "";

    // Public identifier used by SoundCloud's web client. It is not a secret.
    private const string WebApiBase = "https://api-v2.soundcloud.com";
    private const string WebClientId = "Pb72ranhoyt6gw7hM7TkzUItXlMWSNSo";

    public SoundCloudApi(AppLog? log = null)
    {
        _log = log ?? new AppLog();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 SoundCloudDesktop/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _log.Info("SoundCloud API client initialized.");
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

        return await ResolveStreamAsync($"{WebApiBase}/tracks/{track.Id}/streams", cancellationToken);
    }

    public async Task<string> DownloadTrackToTempFileAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (track.Id <= 0) return "";

        var candidates = new[] { track.StreamUrl, track.FallbackStreamUrl }
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var endpoint in new[] { $"{WebApiBase}/tracks/{track.Id}/streams", $"{WebApiBase}/tracks/{track.Id}/stream" })
            if (!candidates.Contains(endpoint, StringComparer.OrdinalIgnoreCase)) candidates.Add(endpoint);

        Exception? lastError = null;
        foreach (var candidate in candidates)
        {
            try
            {
                _log.Info($"Track {track.Id}: resolving stream {AppLog.SafeUrl(candidate)}.");
                var resolvedUrl = await ResolveStreamAsync(candidate, cancellationToken);
                if (!string.IsNullOrWhiteSpace(resolvedUrl))
                {
                    _log.Info($"Track {track.Id}: resolved to {AppLog.SafeUrl(resolvedUrl)}.");
                    return await DownloadStreamToTempFileAsync(resolvedUrl, cancellationToken);
                }
                _log.Info($"Track {track.Id}: stream endpoint returned no playable URL.");
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                _log.Error($"Track {track.Id}: stream candidate failed.", ex);
            }
            catch (InvalidDataException ex)
            {
                lastError = ex;
                _log.Error($"Track {track.Id}: stream candidate returned invalid media data.", ex);
            }
        }

        if (lastError is not null) throw lastError;
        return "";
    }

    public async Task<string> DownloadStreamToTempFileAsync(string streamUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamUrl)) return "";
        if (IsHlsUrl(streamUrl)) return await DownloadHlsToTempFileAsync(streamUrl, cancellationToken);
        var folder = Path.Combine(Path.GetTempPath(), "SoundCloudDesktop");
        Directory.CreateDirectory(folder);
        string? path = null;
        var currentUrl = streamUrl;

        try
        {
            for (var redirect = 0; redirect < 4; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                request.Headers.Accept.Clear();
                request.Headers.Accept.ParseAdd("*/*");
                AddApiAuthorization(request);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                _log.Info($"Audio response {(int)response.StatusCode} {response.ReasonPhrase} from {AppLog.SafeUrl(currentUrl)}; content type '{response.Content.Headers.ContentType?.MediaType ?? "unknown"}'.");
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
                {
                    _log.Info($"Audio redirect to {AppLog.SafeUrl(new Uri(request.RequestUri!, response.Headers.Location).ToString())}.");
                    currentUrl = new Uri(request.RequestUri!, response.Headers.Location).ToString();
                    continue;
                }
                response.EnsureSuccessStatusCode();
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (IsHlsContentType(contentType))
                    return await DownloadHlsToTempFileAsync(currentUrl, cancellationToken);

                path = Path.Combine(folder, $"track-{Guid.NewGuid():N}{GetAudioExtension(currentUrl, contentType)}");
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true);
                await source.CopyToAsync(destination, cancellationToken);
                _log.Info($"Downloaded audio file with extension '{Path.GetExtension(path)}' and size {destination.Length:N0} bytes.");
                return path;
            }
            throw new HttpRequestException("SoundCloud redirected the audio stream too many times.");
        }
        catch
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
            throw;
        }
    }

    private async Task<string> ResolveStreamAsync(string url, CancellationToken cancellationToken)
    {
        var requestUrl = AddWebCredentials(url);
        _log.Info($"Resolving stream endpoint {AppLog.SafeUrl(requestUrl)}.");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", _token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        _log.Info($"Stream endpoint response {(int)response.StatusCode} {response.ReasonPhrase}; content type '{response.Content.Headers.ContentType?.MediaType ?? "unknown"}'.");
        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
        {
            _log.Info($"Stream endpoint redirected to {AppLog.SafeUrl(new Uri(request.RequestUri!, response.Headers.Location).ToString())}.");
            return new Uri(request.RequestUri!, response.Headers.Location).ToString();
        }
        if (!response.IsSuccessStatusCode) return "";

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (IsHlsContentType(contentType)) return request.RequestUri?.ToString() ?? "";
        if (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return request.RequestUri?.ToString() ?? "";
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase) &&
            !contentType.Contains("text", StringComparison.OrdinalIgnoreCase))
            return request.RequestUri?.ToString() ?? "";
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var resolved = root.ValueKind == JsonValueKind.String
            ? root.GetString()
            : GetString(root, "hls_aac_160_url") ?? GetString(root, "hls_aac_96_url") ?? GetString(root, "http_mp3_128_url") ?? GetString(root, "url") ?? GetString(root, "location");
        return string.IsNullOrWhiteSpace(resolved) ? "" : AddWebCredentials(resolved);
    }

    private async Task<string> DownloadHlsToTempFileAsync(string playlistUrl, CancellationToken cancellationToken)
    {
        var (playlist, playlistUri) = await ReadHlsPlaylistAsync(playlistUrl, cancellationToken);
        _log.Info($"HD playlist loaded from {AppLog.SafeUrl(playlistUri.ToString())}; {playlist.Length:N0} characters.");
        for (var depth = 0; depth < 3 && playlist.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase); depth++)
        {
            var variant = SelectHighestBitrateVariant(playlist, playlistUri);
            if (variant is null) throw new InvalidDataException("SoundCloud returned an empty HD audio playlist.");
            _log.Info($"Selecting HD playlist variant {AppLog.SafeUrl(variant.ToString())}.");
            (playlist, playlistUri) = await ReadHlsPlaylistAsync(variant.ToString(), cancellationToken);
            _log.Info($"HD media playlist loaded; {playlist.Length:N0} characters.");
        }

        if (playlist.Contains("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase) &&
            !playlist.Contains("METHOD=NONE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This SoundCloud stream is encrypted.");

        var segments = new List<HlsSegment>();
        long? pendingLength = null;
        long? pendingOffset = null;
        long previousRangeEnd = 0;
        foreach (var rawLine in playlist.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase))
            {
                var range = ParseByteRange(line["#EXT-X-BYTERANGE:".Length..]);
                pendingLength = range.Length > 0 ? range.Length : null;
                pendingOffset = pendingLength.HasValue ? range.Offset ?? previousRangeEnd : null;
                continue;
            }
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var segmentUri = ResolveHlsUri(playlistUri, line);
            segments.Add(new HlsSegment(segmentUri, pendingOffset, pendingLength));
            if (pendingLength.HasValue && pendingOffset.HasValue)
                previousRangeEnd = pendingOffset.Value + pendingLength.Value;
            pendingOffset = null;
            pendingLength = null;
        }
        if (segments.Count == 0) throw new InvalidDataException("SoundCloud returned no audio segments.");

        var folder = Path.Combine(Path.GetTempPath(), "SoundCloudDesktop");
        Directory.CreateDirectory(folder);
        var mapUri = ParseAttributeUri(playlist, "#EXT-X-MAP:", playlistUri);
        var path = Path.Combine(folder, $"track-{Guid.NewGuid():N}{(mapUri is null ? ".aac" : ".m4a")}");
        _log.Info($"HD playlist contains {segments.Count} segment(s), initialization file: {(mapUri is null ? "no" : "yes")}, byte ranges: {segments.Count(segment => segment.Length.HasValue)}.");
        try
        {
            await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true);
            var mapRange = ParseAttributeByteRange(playlist, "#EXT-X-MAP:");
            if (mapUri is not null)
                await CopyResponseToAsync(mapUri, destination, cancellationToken, mapRange.Offset, mapRange.Length > 0 ? mapRange.Length : null);
            foreach (var segment in segments)
                await CopyResponseToAsync(segment.Uri, destination, cancellationToken, segment.Offset, segment.Length);
            if (destination.Length == 0) throw new InvalidDataException("SoundCloud returned an empty HD audio stream.");
            _log.Info($"HD audio assembled as '{Path.GetExtension(path)}' with size {destination.Length:N0} bytes.");
            return path;
        }
        catch (Exception ex)
        {
            _log.Error("HD audio assembly failed.", ex);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            throw;
        }
    }

    private async Task<(string Playlist, Uri Uri)> ReadHlsPlaylistAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var current)) throw new InvalidDataException("SoundCloud returned an invalid HD audio URL.");
        for (var redirect = 0; redirect < 4; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("application/vnd.apple.mpegurl, application/x-mpegURL, */*");
            AddApiAuthorization(request);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
            {
                current = new Uri(current, response.Headers.Location);
                continue;
            }
            _log.Info($"HLS playlist response {(int)response.StatusCode} {response.ReasonPhrase} from {AppLog.SafeUrl(current.ToString())}.");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            _log.Info($"HLS playlist content type '{response.Content.Headers.ContentType?.MediaType ?? "unknown"}'.");
            return (content, current);
        }
        throw new HttpRequestException("SoundCloud redirected the HD audio playlist too many times.");
    }

    private static Uri? SelectHighestBitrateVariant(string playlist, Uri playlistUri)
    {
        Uri? bestUri = null;
        var bestBandwidth = -1L;
        var pendingBandwidth = 0L;
        foreach (var rawLine in playlist.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                pendingBandwidth = ParseAttributeLong(line, "BANDWIDTH");
                continue;
            }
            if (pendingBandwidth <= 0 || line.StartsWith('#')) continue;
            if (pendingBandwidth > bestBandwidth)
            {
                bestBandwidth = pendingBandwidth;
                bestUri = ResolveHlsUri(playlistUri, line);
            }
            pendingBandwidth = 0;
        }
        return bestUri;
    }

    private async Task CopyResponseToAsync(Uri url, Stream destination, CancellationToken cancellationToken, long? offset = null, long? length = null)
    {
        var current = url;
        for (var redirect = 0; redirect < 4; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("*/*");
            AddApiAuthorization(request);
            if (offset.HasValue && length.HasValue)
                request.Headers.Range = new RangeHeaderValue(offset.Value, offset.Value + length.Value - 1);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
            {
                current = new Uri(current, response.Headers.Location);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                _log.Error($"HLS media request returned {(int)response.StatusCode} {response.ReasonPhrase} for {AppLog.SafeUrl(current.ToString())}.");
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            if (offset.HasValue && length.HasValue && response.StatusCode != HttpStatusCode.PartialContent)
                await SkipAndCopyRangeAsync(source, destination, offset.Value, length.Value, cancellationToken);
            else if (length.HasValue)
                await CopyExactlyAsync(source, destination, length.Value, cancellationToken);
            else
                await source.CopyToAsync(destination, cancellationToken);
            return;
        }
        throw new HttpRequestException("SoundCloud redirected an HD audio segment too many times.");
    }

    private static long ParseAttributeLong(string line, string name)
    {
        var marker = $"{name}=";
        var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return 0;
        start += marker.Length;
        var end = line.IndexOf(',', start);
        var value = end < 0 ? line[start..] : line[start..end];
        return long.TryParse(value, out var result) ? result : 0;
    }

    private static (long Length, long? Offset) ParseByteRange(string value)
    {
        var parts = value.Trim().Split('@', 2);
        return long.TryParse(parts[0], out var length) && length > 0
            ? (length, parts.Length == 2 && long.TryParse(parts[1], out var offset) ? offset : null)
            : (0, null);
    }

    private static (long Length, long? Offset) ParseAttributeByteRange(string playlist, string marker)
    {
        var line = playlist
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.StartsWith(marker, StringComparison.OrdinalIgnoreCase));
        if (line is null) return (0, null);
        var rangeMarker = "BYTERANGE=";
        var start = line.IndexOf(rangeMarker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return (0, null);
        var value = line[(start + rangeMarker.Length)..].Trim();
        var end = value.IndexOf(',');
        if (end >= 0) value = value[..end];
        return ParseByteRange(value.Trim('"'));
    }

    private static async Task SkipAndCopyRangeAsync(Stream source, Stream destination, long offset, long length, CancellationToken cancellationToken)
    {
        var remainingToSkip = offset;
        var buffer = new byte[64 * 1024];
        while (remainingToSkip > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remainingToSkip)), cancellationToken);
            if (read == 0) throw new InvalidDataException("SoundCloud returned an incomplete audio range.");
            remainingToSkip -= read;
        }
        await CopyExactlyAsync(source, destination, length, cancellationToken);
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, long length, CancellationToken cancellationToken)
    {
        var remaining = length;
        var buffer = new byte[64 * 1024];
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0) throw new InvalidDataException("SoundCloud returned an incomplete audio range.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private static Uri? ParseAttributeUri(string playlist, string marker, Uri playlistUri)
    {
        var line = playlist
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.StartsWith(marker, StringComparison.OrdinalIgnoreCase));
        if (line is null) return null;
        var uriMarker = "URI=";
        var start = line.IndexOf(uriMarker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var value = line[(start + uriMarker.Length)..].Trim().Trim('"');
        return Uri.TryCreate(value, UriKind.Absolute, out _) || value.Length > 0
            ? ResolveHlsUri(playlistUri, value)
            : null;
    }

    private static Uri ResolveHlsUri(Uri playlistUri, string reference)
    {
        var resolved = new Uri(playlistUri, reference);
        if (Uri.TryCreate(reference, UriKind.Absolute, out _) ||
            string.IsNullOrWhiteSpace(playlistUri.Query) ||
            !string.IsNullOrWhiteSpace(resolved.Query))
            return resolved;

        var builder = new UriBuilder(resolved) { Query = playlistUri.Query.TrimStart('?') };
        return builder.Uri;
    }

    private static string GetAudioExtension(string url, string contentType)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (extension is ".aac" or ".adts" or ".m4a" or ".mp4" or ".mp3" or ".ogg" or ".opus" or ".wav") return extension;
        }
        if (contentType.Contains("mp4", StringComparison.OrdinalIgnoreCase) || contentType.Contains("m4a", StringComparison.OrdinalIgnoreCase)) return ".m4a";
        if (contentType.Contains("aac", StringComparison.OrdinalIgnoreCase)) return ".aac";
        if (contentType.Contains("ogg", StringComparison.OrdinalIgnoreCase)) return ".ogg";
        return ".mp3";
    }

    private static bool IsHlsUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    private static bool IsHlsContentType(string contentType) =>
        contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || contentType.Contains("m3u8", StringComparison.OrdinalIgnoreCase);

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

    private void AddApiAuthorization(HttpRequestMessage request)
    {
        if (request.RequestUri is not null && IsApiHost(request.RequestUri.Host) && !string.IsNullOrWhiteSpace(_token))
            request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", _token);
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

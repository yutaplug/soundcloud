using System.Text.Json;

namespace SoundCloudDesktop.Models;

public sealed class Track
{
    public long Id { get; init; }
    public string Title { get; init; } = "Untitled";
    public string Artist { get; init; } = "Unknown artist";
    public string ArtworkUrl { get; init; } = "";
    public string PlayerArtworkUrl => string.IsNullOrWhiteSpace(ArtworkUrl)
        ? ""
        : ArtworkUrl.Replace("-t67x67.", "-t500x500.", StringComparison.OrdinalIgnoreCase);
    public string StreamUrl { get; set; } = "";
    public string FallbackStreamUrl { get; init; } = "";
    public string PermalinkUrl { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public bool IsPlayable => !string.IsNullOrWhiteSpace(StreamUrl);

    public string DurationText => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    public static Track FromJson(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var numericId))
            return new Track { Id = numericId };
        if (item.ValueKind == JsonValueKind.String && long.TryParse(item.GetString(), out var stringId))
            return new Track { Id = stringId };
        if (item.ValueKind != JsonValueKind.Object) return new Track();

        var source = item.TryGetProperty("track", out var nested) ? nested : item;
        if (source.ValueKind == JsonValueKind.Number && source.TryGetInt64(out var nestedId))
            return new Track { Id = nestedId };
        if (source.ValueKind != JsonValueKind.Object) return new Track();
        var user = source.TryGetProperty("user", out var userJson) ? userJson : default;
        var artwork = GetString(source, "artwork_url");
        if (string.IsNullOrWhiteSpace(artwork)) artwork = GetString(user, "avatar_url");
        var streamUrls = GetPreferredStreamUrls(source);
        var legacyStreamUrl = GetString(source, "stream_url");
        var streamUrl = streamUrls.FirstOrDefault() ?? legacyStreamUrl;
        var fallbackStreamUrl = streamUrls.FirstOrDefault(url => !string.Equals(url, streamUrl, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(fallbackStreamUrl) && !string.Equals(legacyStreamUrl, streamUrl, StringComparison.OrdinalIgnoreCase))
            fallbackStreamUrl = legacyStreamUrl;

        return new Track
        {
            Id = GetLong(source, "id"),
            Title = GetString(source, "title") ?? "Untitled",
            Artist = GetString(user, "username") ?? "Unknown artist",
            ArtworkUrl = NormalizeImageUrl(artwork ?? ""),
            StreamUrl = streamUrl ?? "",
            FallbackStreamUrl = fallbackStreamUrl ?? "",
            PermalinkUrl = GetString(source, "permalink_url") ?? "",
            Duration = TimeSpan.FromMilliseconds(Math.Max(0, GetLong(source, "duration")))
        };
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static long GetLong(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return 0;
        return value.TryGetInt64(out var number) ? number : long.TryParse(value.ToString(), out number) ? number : 0;
    }

    private static string NormalizeImageUrl(string url) =>
        string.IsNullOrWhiteSpace(url) ? "" : url.Replace("-large.", "-t67x67.", StringComparison.OrdinalIgnoreCase);

    private static List<string> GetPreferredStreamUrls(JsonElement source)
    {
        var candidates = new List<(string Url, int Score)>();
        AddCandidate(candidates, GetString(source, "download_url"), 550);
        AddCandidate(candidates, GetString(source, "hls_aac_160_url"), 500);
        AddCandidate(candidates, GetString(source, "hls_aac_96_url"), 490);
        AddCandidate(candidates, GetString(source, "http_mp3_128_url"), 400);

        if (source.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object &&
            media.TryGetProperty("transcodings", out var transcodings) && transcodings.ValueKind == JsonValueKind.Array)
        {
            foreach (var transcoding in transcodings.EnumerateArray())
            {
                var format = transcoding.TryGetProperty("format", out var formatJson) ? formatJson : default;
                var protocol = GetString(format, "protocol") ?? GetString(transcoding, "protocol") ?? "";
                var mime = GetString(format, "mime_type") ?? GetString(transcoding, "mime_type") ?? "";
                var preset = GetString(transcoding, "preset") ?? "";
                var url = GetString(transcoding, "url");
                var isHdAudio = mime.Contains("aac", StringComparison.OrdinalIgnoreCase) ||
                                mime.Contains("mp4", StringComparison.OrdinalIgnoreCase) ||
                                preset.Contains("aac", StringComparison.OrdinalIgnoreCase) ||
                                preset.Contains("m4a", StringComparison.OrdinalIgnoreCase) ||
                                preset.Contains("mp4", StringComparison.OrdinalIgnoreCase);
                var score = protocol.Equals("hls", StringComparison.OrdinalIgnoreCase) && isHdAudio
                    ? preset.Contains("160", StringComparison.OrdinalIgnoreCase) ? 500 : 490
                    : protocol.Equals("progressive", StringComparison.OrdinalIgnoreCase) && mime.Contains("mpeg", StringComparison.OrdinalIgnoreCase) ? 400
                    : protocol.Equals("progressive", StringComparison.OrdinalIgnoreCase) ? 390
                    : 100;
                AddCandidate(candidates, url, score);
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddCandidate(List<(string Url, int Score)> candidates, string? url, int score)
    {
        if (!string.IsNullOrWhiteSpace(url)) candidates.Add((url, score));
    }
}

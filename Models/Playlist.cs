using System.Text.Json;

namespace SoundCloudDesktop.Models;

public sealed class Playlist
{
    public long Id { get; init; }
    public string Title { get; init; } = "Untitled playlist";
    public string Creator { get; init; } = "Unknown creator";
    public string ArtworkUrl { get; init; } = "";
    public int TrackCount { get; init; }
    public string TrackCountText => TrackCount == 1 ? "1 track" : $"{TrackCount:N0} tracks";

    public static Playlist FromJson(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var numericId))
            return new Playlist { Id = numericId };
        if (item.ValueKind != JsonValueKind.Object) return new Playlist();

        var source = item.TryGetProperty("playlist", out var nested) ? nested : item;
        var user = source.TryGetProperty("user", out var userJson) ? userJson : default;
        var artwork = GetString(source, "artwork_url");
        if (string.IsNullOrWhiteSpace(artwork)) artwork = GetString(user, "avatar_url");
        var count = GetInt(source, "track_count");
        if (count == 0 && source.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array) count = tracks.GetArrayLength();

        return new Playlist
        {
            Id = GetLong(source, "id"),
            Title = GetString(source, "title") ?? "Untitled playlist",
            Creator = GetString(user, "username") ?? GetString(source, "username") ?? "Unknown creator",
            ArtworkUrl = NormalizeImageUrl(artwork ?? ""),
            TrackCount = Math.Max(0, count)
        };
    }

    private static string? GetString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString() : null;
    private static long GetLong(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;
    private static int GetInt(JsonElement element, string name) => (int)Math.Clamp(GetLong(element, name), 0, int.MaxValue);
    private static string NormalizeImageUrl(string url) => string.IsNullOrWhiteSpace(url) ? "" : url.Replace("-large.", "-t67x67.", StringComparison.OrdinalIgnoreCase);
}

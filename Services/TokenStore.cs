using System.IO;

namespace SoundCloudDesktop.Services;

public sealed class TokenStore
{
    private static readonly string LegacyFilePath = Path.Combine(AppContext.BaseDirectory, "oauth_token.txt");
    public string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoundCloudDesktop",
        "oauth_token.txt");

    public string? TryLoad()
    {
        try
        {
            var token = ReadToken(FilePath);
            if (!string.IsNullOrWhiteSpace(token))
            {
                DeleteFile(LegacyFilePath);
                return token;
            }

            // Migrate tokens created by older builds that stored them beside
            // the executable.
            token = ReadToken(LegacyFilePath);
            if (string.IsNullOrWhiteSpace(token)) return null;
            if (TrySave(token)) DeleteFile(LegacyFilePath);
            return token;
        }
        catch
        {
            return null;
        }
    }

    public bool TrySave(string token)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, token.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Delete()
    {
        DeleteFile(FilePath);
        DeleteFile(LegacyFilePath);
    }

    private static string? ReadToken(string path)
    {
        if (!File.Exists(path)) return null;
        var token = File.ReadAllText(path).Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static void DeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

public sealed class AppLog
{
    private const long MaximumFileSize = 1_500_000;
    private readonly object _sync = new();

    public string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoundCloudDesktop",
        "playback.log");

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? exception = null)
    {
        var detail = exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}";
        Write("ERROR", detail);
    }

    public string ReadAll()
    {
        lock (_sync)
        {
            try { return File.Exists(FilePath) ? File.ReadAllText(FilePath) : "No log entries yet."; }
            catch (Exception ex) { return $"Unable to read the log: {ex.Message}"; }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }
    }

    public static string SafeUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}"
            : "<invalid url>";
    }

    private void Write(string level, string message)
    {
        lock (_sync)
        {
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {Sanitize(message)}{Environment.NewLine}");
                TrimIfNeeded();
            }
            catch
            {
                // Logging must never interfere with playback.
            }
        }
    }

    private void TrimIfNeeded()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length <= MaximumFileSize) return;
            using var source = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            source.Seek(info.Length - MaximumFileSize, SeekOrigin.Begin);
            using var reader = new StreamReader(source);
            var retained = reader.ReadToEnd();
            var firstLine = retained.IndexOf('\n');
            if (firstLine >= 0) retained = retained[(firstLine + 1)..];
            File.WriteAllText(FilePath, "[Earlier entries trimmed]" + Environment.NewLine + retained);
        }
        catch
        {
            // Logging must never interfere with playback.
        }
    }

    private static string Sanitize(string value)
    {
        var sanitized = value;
        foreach (var marker in new[] { "oauth_token=", "access_token=", "client_id=", "signature=", "policy=", "key-pair-id=" })
        {
            var searchFrom = 0;
            while (true)
            {
                var start = sanitized.IndexOf(marker, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (start < 0) break;
                var valueStart = start + marker.Length;
                var end = sanitized.IndexOfAny(new[] { '&', ' ', '\r', '\n', '"', '\'' }, valueStart);
                if (end < 0) end = sanitized.Length;
                sanitized = sanitized[..valueStart] + "<redacted>" + sanitized[end..];
                searchFrom = valueStart + "<redacted>".Length;
            }
        }
        return sanitized;
    }
}

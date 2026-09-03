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

using System.IO;

namespace SoundCloudDesktop.Services;

public sealed class TokenStore
{
    public string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "oauth_token.txt");

    public string? TryLoad()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var token = File.ReadAllText(FilePath).Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
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
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}

namespace Mambo.Core.Persistence;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mambo"));
        Directory.CreateDirectory(Root);
    }
    public string Root { get; }
    public string Settings => Path.Combine(Root, "settings.json");
    public string QueryCache => DirectoryPath("cache", "query");
    public string Images => DirectoryPath("cache", "images");
    public string BulletChatCache => DirectoryPath("cache", "bullet-chat");
    public string BulletChatHistory => Path.Combine(Root, "bullet-chat-history.json");
    public string Outbox => Path.Combine(DirectoryPath("outbox"), "stop-reports.json");
    public string Logs => DirectoryPath("logs");
    public string ShaderCache => DirectoryPath("mpv", "shader-cache");
    private string DirectoryPath(params string[] parts)
    {
        var path = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }
}

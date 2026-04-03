using System.Text.Json;
using System.Text.Json.Serialization;

namespace Revue;

public static class CommentsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static string CommentsPath(string repoRoot) =>
        Path.Combine(repoRoot, ".revue", "comments.json");

    public static List<Comment> Load(string repoRoot)
    {
        var path = CommentsPath(repoRoot);
        if (!File.Exists(path)) return [];
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<Comment>>(json, JsonOpts) ?? [];
    }

    public static void Save(string repoRoot, List<Comment> comments)
    {
        var path = CommentsPath(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(comments, JsonOpts));
    }
}

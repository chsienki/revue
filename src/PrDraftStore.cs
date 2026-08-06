using System.Text.Json;

namespace Revue;

/// <summary>
/// Load/save for the draft PR description. The body lives in
/// <c>.revue/pr.md</c> and its metadata in <c>.revue/pr.json</c> -- two files
/// rather than front-matter so the body stays clean markdown that
/// <c>gh pr create --body-file</c> can consume as-is.
/// </summary>
public static class PrDraftStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static string BodyPath(string repoRoot) => Path.Combine(repoRoot, ".revue", "pr.md");
    private static string MetaPath(string repoRoot) => Path.Combine(repoRoot, ".revue", "pr.json");

    /// <summary>The stored draft, or null when nothing has been written yet.</summary>
    public static PrDraft? Load(string repoRoot)
    {
        var bodyPath = BodyPath(repoRoot);
        if (!File.Exists(bodyPath)) return null;

        string body;
        try { body = File.ReadAllText(bodyPath); }
        catch { return null; }

        var meta = LoadMeta(repoRoot);
        return new PrDraft(
            Title: meta?.Title ?? "",
            Body: body,
            Base: meta?.Base,
            Head: meta?.Head,
            Branch: meta?.Branch,
            Generated: meta?.Generated,
            GeneratedBy: meta?.GeneratedBy);
    }

    public static void Save(string repoRoot, PrDraft draft)
    {
        Directory.CreateDirectory(Path.Combine(repoRoot, ".revue"));
        File.WriteAllText(BodyPath(repoRoot), draft.Body);
        // The body isn't duplicated into the metadata file -- pr.md is the one
        // copy, so the two can't disagree.
        File.WriteAllText(MetaPath(repoRoot), JsonSerializer.Serialize(draft with { Body = "" }, JsonOpts));
    }

    public static bool Delete(string repoRoot)
    {
        var removed = false;
        foreach (var path in new[] { BodyPath(repoRoot), MetaPath(repoRoot) })
        {
            try { if (File.Exists(path)) { File.Delete(path); removed = true; } }
            catch { /* best-effort */ }
        }
        return removed;
    }

    /// <summary>
    /// True when the draft was written against a different review context, so
    /// the UI can say so rather than presenting it as current.
    /// </summary>
    public static bool IsStale(PrDraft draft, string @base, string head, string? branch)
    {
        if (draft.Base is { Length: > 0 } b && b != @base) return true;
        if (draft.Head is { Length: > 0 } h && h != head) return true;
        if (draft.Branch is { Length: > 0 } br && branch is { Length: > 0 } && br != branch) return true;
        return false;
    }

    private static PrDraft? LoadMeta(string repoRoot)
    {
        var path = MetaPath(repoRoot);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<PrDraft>(File.ReadAllText(path), JsonOpts); }
        catch { return null; }
    }
}

namespace Revue;

/// <summary>
/// The set of git repositories a single revue instance is serving. One instance
/// can host several repos at once; the frontend picks the active one and every
/// repo-scoped endpoint resolves its target through this registry. Access is
/// serialized so concurrent requests (and the single-instance hand-off that
/// adds a repo from another process) stay consistent.
/// </summary>
public sealed class RepoRegistry
{
    // Windows paths are case-insensitive; POSIX paths are not.
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly object _lock = new();
    private readonly List<Repo> _repos = [];

    /// <summary>A registered repo. <see cref="Path"/> is the canonical git root.</summary>
    public sealed record Repo(string Path, string DefaultBase);

    /// <summary>
    /// Resolves the git root at or above <paramref name="startPath"/>, registers
    /// it if new (validating git and excluding .revue/), and returns its wire
    /// info. Adding an already-registered repo is a no-op that returns the
    /// existing entry. The first repo added becomes the primary.
    /// </summary>
    public RepoInfo Add(string startPath)
    {
        var root = GitHelper.FindRepoRoot(startPath);
        lock (_lock)
        {
            var existing = _repos.FirstOrDefault(r => PathComparer.Equals(r.Path, root));
            if (existing is null)
            {
                var defaultBase = GitHelper.ResolveDefaultBase(root); // throws if git is unusable here
                GitHelper.EnsureRevueIgnored(root);
                existing = new Repo(root, defaultBase);
                _repos.Add(existing);
            }
            return ToInfo(existing);
        }
    }

    /// <summary>
    /// Removes a repo by path. Refuses to remove the last remaining repo (an
    /// instance with zero repos has nothing to show). Returns false if the path
    /// is unknown or it's the only repo left.
    /// </summary>
    public bool Remove(string path)
    {
        lock (_lock)
        {
            if (_repos.Count <= 1) return false;
            var idx = _repos.FindIndex(r => PathComparer.Equals(r.Path, path));
            if (idx < 0) return false;
            _repos.RemoveAt(idx);
            return true;
        }
    }

    /// <summary>
    /// Resolves a repo by path. A null/empty/unknown path falls back to the
    /// primary (first-registered) repo so callers that omit the parameter keep
    /// working. Returns null only when no repos are registered.
    /// </summary>
    public Repo? Resolve(string? path)
    {
        lock (_lock)
        {
            if (_repos.Count == 0) return null;
            if (string.IsNullOrEmpty(path)) return _repos[0];
            return _repos.FirstOrDefault(r => PathComparer.Equals(r.Path, path)) ?? _repos[0];
        }
    }

    /// <summary>Path of the primary (first-registered) repo, or null if empty.</summary>
    public string? PrimaryPath
    {
        get { lock (_lock) { return _repos.Count > 0 ? _repos[0].Path : null; } }
    }

    /// <summary>All registered repos with unique, minimal display names.</summary>
    public IReadOnlyList<RepoInfo> List()
    {
        lock (_lock) { return _repos.Select(ToInfo).ToList(); }
    }

    /// <summary>
    /// Finds the repo whose comment store contains <paramref name="commentId"/>.
    /// Used by reply/delete-by-id calls that don't name a repo (e.g. the Copilot
    /// skill posting a reply), so the mutation lands in the right store even when
    /// the instance hosts several repos. Returns null if no store has that id.
    /// </summary>
    public Repo? FindByCommentId(string commentId)
    {
        List<Repo> snapshot;
        lock (_lock) { snapshot = [.. _repos]; }
        foreach (var r in snapshot)
        {
            try
            {
                if (CommentsStore.Load(r.Path).Any(c => c.Id == commentId))
                    return r;
            }
            catch { /* unreadable store shouldn't abort the search */ }
        }
        return null;
    }

    // Caller must hold _lock: display name depends on the sibling set.
    private RepoInfo ToInfo(Repo r) => new(r.Path, DisplayName(r.Path), r.DefaultBase);

    // Caller must hold _lock.
    private string DisplayName(string path)
    {
        var mine = Segments(path);
        // Grow the trailing-segment suffix until it's unique among the siblings.
        for (var take = 1; take < mine.Length; take++)
        {
            var label = Suffix(mine, take);
            var unique = _repos.All(other =>
                PathComparer.Equals(other.Path, path)
                || !PathComparer.Equals(Suffix(Segments(other.Path), take), label));
            if (unique) return label;
        }
        return Suffix(mine, mine.Length);
    }

    private static string[] Segments(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    private static string Suffix(string[] segments, int take) =>
        string.Join('/', segments.Skip(Math.Max(0, segments.Length - take)));
}

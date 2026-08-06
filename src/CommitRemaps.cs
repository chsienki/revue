namespace Revue;

/// <summary>
/// Remembers where commits went when history was rewritten, so a UI pinned to a
/// sha that no longer exists can follow the rebase instead of stranding the user
/// on a commit that only survives in the object database.
///
/// Deliberately in-memory and bounded: this only has to outlive the rewrite long
/// enough for the open tabs to notice, and a rewrite nobody was watching is not
/// worth remembering.
/// </summary>
public sealed class CommitRemapLog
{
    private const int LimitPerRepo = 200;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, string>> _byRepo = new(PathComparer);

    /// <summary>Records that <paramref name="oldSha"/> was rewritten as <paramref name="newSha"/>.</summary>
    public void Record(string repo, string oldSha, string newSha)
    {
        if (string.IsNullOrWhiteSpace(oldSha) || string.IsNullOrWhiteSpace(newSha)) return;
        if (oldSha.Equals(newSha, StringComparison.OrdinalIgnoreCase)) return;

        lock (_lock)
        {
            if (!_byRepo.TryGetValue(repo, out var map))
                _byRepo[repo] = map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // A rewrite of an already-rewritten commit shouldn't leave the first
            // mapping pointing at a sha that has itself moved on, so collapse the
            // chain as it's built rather than walking it on every lookup.
            foreach (var key in map.Where(kv => kv.Value.Equals(oldSha, StringComparison.OrdinalIgnoreCase))
                                   .Select(kv => kv.Key).ToList())
                map[key] = newSha;

            map[oldSha] = newSha;

            if (map.Count > LimitPerRepo)
                foreach (var key in map.Keys.Take(map.Count - LimitPerRepo).ToList())
                    map.Remove(key);
        }
    }

    /// <summary>
    /// Where <paramref name="sha"/> ended up, or null if it was never rewritten.
    /// Accepts abbreviated shas so callers can pass what the UI happens to hold.
    /// </summary>
    public string? Resolve(string repo, string sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return null;
        lock (_lock)
        {
            if (!_byRepo.TryGetValue(repo, out var map)) return null;
            if (map.TryGetValue(sha, out var exact)) return exact;
            foreach (var (oldSha, newSha) in map)
                if (oldSha.StartsWith(sha, StringComparison.OrdinalIgnoreCase)
                    || sha.StartsWith(oldSha, StringComparison.OrdinalIgnoreCase))
                    return newSha;
            return null;
        }
    }
}

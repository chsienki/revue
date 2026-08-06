namespace Revue;

/// <summary>
/// A revue version newer than the running one that the user could switch to.
/// <see cref="ExePath"/> is null when the version is only <em>pinned</em> by the
/// installed plugin and its binary hasn't been downloaded yet -- applying it then
/// means running <see cref="BootstrapScript"/> first.
/// </summary>
public record UpdateTarget(string Version, string? ExePath, string? BootstrapScript)
{
    public bool NeedsDownload => ExePath is null;
}

/// <summary>
/// Finds revue versions installed on this machine. The plugin's pinned VERSION
/// file is tracked alongside the downloaded binaries because
/// <c>copilot plugin update</c> only refreshes skill files -- the binary itself
/// doesn't land until something runs bootstrap. Waiting for the binary would mean
/// waiting for a launch, which is the manual step this exists to remove.
/// </summary>
public static class InstalledVersions
{
    private static string ExeName => OperatingSystem.IsWindows() ? "revue.exe" : "revue";

    /// <summary>
    /// The highest installed version above <paramref name="current"/>, preferring a
    /// ready-to-run binary over a pin of the same version. Null when nothing is newer.
    /// </summary>
    public static UpdateTarget? FindNewerThan(string current)
    {
        var candidates = new List<UpdateTarget>();
        candidates.AddRange(CachedBinaries());
        candidates.AddRange(PluginVersions());

        return candidates
            .Where(c => IsNewer(c.Version, current))
            .OrderByDescending(c => ParseVersion(c.Version))
            .ThenBy(c => c.NeedsDownload)
            .FirstOrDefault();
    }

    public static bool IsNewer(string candidate, string current)
    {
        var c = ParseVersion(candidate);
        var v = ParseVersion(current);
        if (c is not null && v is not null) return c > v;
        return string.Compare(candidate, current, StringComparison.Ordinal) > 0;
    }

    // Version strings carry build metadata when they come from a running binary
    // ("0.14.0+af20486"); the pin and cache directory names never do.
    private static Version? ParseVersion(string raw)
    {
        var core = raw.Split('+')[0].Split('-')[0].TrimStart('v');
        return Version.TryParse(core, out var v) ? v : null;
    }

    // Bootstrap lays binaries out as <base>/<version>/revue[.exe].
    private static IEnumerable<UpdateTarget> CachedBinaries()
    {
        foreach (var baseDir in CacheBases())
        {
            foreach (var versionDir in SafeDirectories(baseDir))
            {
                var name = Path.GetFileName(versionDir);
                if (ParseVersion(name) is null) continue;
                var exe = Path.Combine(versionDir, ExeName);
                if (File.Exists(exe)) yield return new UpdateTarget(name, exe, null);
            }
        }
    }

    private static IEnumerable<string> CacheBases()
    {
        // The running binary usually sits in one of these layouts already, which
        // finds the right base regardless of platform path conventions.
        var exe = Environment.ProcessPath;
        var versionDir = exe is null ? null : Path.GetDirectoryName(exe);
        if (versionDir is not null && ParseVersion(Path.GetFileName(versionDir)) is not null)
        {
            var baseDir = Path.GetDirectoryName(versionDir);
            if (baseDir is not null) yield return baseDir;
        }

        yield return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "revue")
            : Path.Combine(HomeDir(), ".cache", "revue");
    }

    // The installed plugin carries a VERSION pin, and (for a local dev install)
    // sometimes the binary itself sitting next to it.
    private static IEnumerable<UpdateTarget> PluginVersions()
    {
        foreach (var dir in PluginRevueDirs())
        {
            var versionFile = Path.Combine(dir, "VERSION");
            if (!File.Exists(versionFile)) continue;

            string version;
            try { version = File.ReadAllText(versionFile).Trim(); }
            catch { continue; }
            if (version.Length == 0 || ParseVersion(version) is null) continue;

            var bundled = Path.Combine(dir, ExeName);
            yield return File.Exists(bundled)
                ? new UpdateTarget(version, bundled, null)
                : new UpdateTarget(version, null, FindBootstrap(dir));
        }
    }

    /// <summary>
    /// Directories of an installed revue plugin. Both the plugin root and its
    /// nested skill directory carry a VERSION file in practice, and a plugin
    /// installed from a local path lands under a name of the user's choosing, so
    /// this looks for a revue skill under every installed plugin rather than
    /// assuming one fixed layout.
    /// </summary>
    private static IEnumerable<string> PluginRevueDirs()
    {
        var home = Environment.GetEnvironmentVariable("COPILOT_HOME");
        if (string.IsNullOrEmpty(home)) home = Path.Combine(HomeDir(), ".copilot");
        var root = Path.Combine(home, "installed-plugins");

        foreach (var owner in SafeDirectories(root))
        {
            foreach (var plugin in SafeDirectories(owner))
            {
                if (Path.GetFileName(plugin).Equals("revue", StringComparison.OrdinalIgnoreCase))
                    yield return plugin;

                var skill = Path.Combine(plugin, "skills", "revue");
                if (Directory.Exists(skill)) yield return skill;
            }
        }
    }

    // The plugin's own bootstrap script, so downloading reuses its platform
    // detection, caching and old-version cleanup instead of duplicating them.
    private static string? FindBootstrap(string dir)
    {
        var scriptName = OperatingSystem.IsWindows() ? "bootstrap.ps1" : "bootstrap.sh";
        foreach (var candidate in new[]
        {
            Path.Combine(dir, "scripts", scriptName),
            Path.Combine(dir, "skills", "revue", "scripts", scriptName),
        })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string HomeDir() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path) : []; }
        catch { return []; }
    }
}

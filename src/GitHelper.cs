namespace Revue;

public static class GitHelper
{
    public static string RunGit(string[] args, string cwd)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = System.Diagnostics.Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(stderr.Trim());
        return stdout;
    }

    /// <summary>
    /// Returns the current branch name, or null if HEAD is detached (or git fails).
    /// </summary>
    public static string? GetCurrentBranch(string repoRoot)
    {
        try
        {
            var name = RunGit(["rev-parse", "--abbrev-ref", "HEAD"], repoRoot).Trim();
            // Detached HEAD reports literal "HEAD" — treat as no branch
            if (string.IsNullOrEmpty(name) || name == "HEAD") return null;
            return name;
        }
        catch { return null; }
    }

    public static string ResolveDefaultBase(string repoRoot)
    {
        foreach (var r in new[] { "upstream/main", "origin/main", "main" })
        {
            try { RunGit(["rev-parse", "--verify", r], repoRoot); return r; }
            catch { }
        }
        return "HEAD~1";
    }

    public static List<DiffFile> ParseDiff(string rawDiff)
    {
        var result = new List<DiffFile>();
        if (string.IsNullOrWhiteSpace(rawDiff)) return result;

        var lines = rawDiff.Split('\n');
        string? currentFile = null;
        var patchLines = new List<string>();
        int additions = 0, deletions = 0;

        void Flush()
        {
            if (currentFile == null) return;
            result.Add(new DiffFile(currentFile, string.Join('\n', patchLines), additions, deletions));
            currentFile = null; patchLines.Clear(); additions = 0; deletions = 0;
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git "))
            {
                Flush();
                var parts = line.Split(' ');
                var bPart = parts.Length >= 4 ? parts[3] : parts[^1];
                currentFile = bPart.StartsWith("b/") ? bPart[2..] : bPart;
                patchLines.Add(line);
            }
            else if (currentFile != null)
            {
                patchLines.Add(line);
                if (line.StartsWith("+") && !line.StartsWith("+++")) additions++;
                else if (line.StartsWith("-") && !line.StartsWith("---")) deletions++;
            }
        }
        Flush();
        return result;
    }

    public static bool HasWorkingTreeChanges(string repoRoot)
    {
        var output = RunGit(["status", "--porcelain"], repoRoot);
        return !string.IsNullOrWhiteSpace(output);
    }

    public static string GetMergeBase(string a, string b, string repoRoot)
    {
        return RunGit(["merge-base", a, b], repoRoot).Trim();
    }

    /// <summary>
    /// For a tracking ref like "upstream/main", checks if the local ref is behind
    /// the remote by comparing local rev to `git ls-remote`. Returns the remote
    /// name and branch, plus how many commits behind, or null if up-to-date/not a remote ref.
    /// </summary>
    public static RefStatus? CheckRefStatus(string @ref, string repoRoot)
    {
        // Only check remote-tracking refs (e.g. "upstream/main", "origin/develop")
        var slash = @ref.IndexOf('/');
        if (slash <= 0) return null;

        var remote = @ref[..slash];
        var branch = @ref[(slash + 1)..];

        // Verify it's actually a remote
        try { RunGit(["remote", "get-url", remote], repoRoot); }
        catch { return null; }

        string localSha;
        try { localSha = RunGit(["rev-parse", @ref], repoRoot).Trim(); }
        catch { return null; }

        string remoteSha;
        try
        {
            var lsRemote = RunGit(["ls-remote", remote, $"refs/heads/{branch}"], repoRoot).Trim();
            if (string.IsNullOrEmpty(lsRemote)) return null;
            remoteSha = lsRemote.Split('\t')[0];
        }
        catch { return null; }

        if (localSha == remoteSha)
            return new RefStatus(remote, branch, 0);

        // Count how many commits the remote is ahead (requires the remote sha to be fetchable)
        // We can't count without fetching, so just report "behind" with -1
        return new RefStatus(remote, branch, -1);
    }

    public static void FetchRef(string remote, string branch, string repoRoot)
    {
        RunGit(["fetch", remote, branch], repoRoot);
    }

    public static List<string> GetUntrackedFiles(string repoRoot)
    {
        var output = RunGit(["ls-files", "--others", "--exclude-standard"], repoRoot);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public static string GetUntrackedFileDiff(string file, string repoRoot)
    {
        var fullPath = Path.Combine(repoRoot, file.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath)) return "";
        var content = File.ReadAllText(fullPath);
        var lines = content.Split('\n');
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"diff --git a/{file} b/{file}");
        sb.AppendLine("new file mode 100644");
        sb.AppendLine("--- /dev/null");
        sb.AppendLine($"+++ b/{file}");
        sb.AppendLine($"@@ -0,0 +1,{lines.Length} @@");
        foreach (var line in lines)
            sb.AppendLine($"+{line}");
        return sb.ToString();
    }
}

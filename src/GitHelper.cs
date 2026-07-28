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

    /// <summary>
    /// Returns the commits in the range base..head (newest first), with full
    /// metadata including the multiline body. Uses ASCII unit/record separators
    /// (US 0x1f, RS 0x1e) so multiline bodies parse unambiguously.
    /// </summary>
    public static List<CommitInfo> GetCommits(string @base, string head, string repoRoot)
    {
        var raw = RunGit(["log", "--format=%H%x1f%s%x1f%an%x1f%aI%x1f%b%x1e", $"{@base}..{head}"], repoRoot);
        var result = new List<CommitInfo>();
        foreach (var record in raw.Split('\x1e'))
        {
            var trimmed = record.Trim('\n');
            if (trimmed.Length == 0) continue;
            var parts = trimmed.Split('\x1f', 5);
            if (parts.Length < 5) continue;
            result.Add(new CommitInfo(
                Hash: parts[0],
                Subject: parts[1],
                Body: parts[4].TrimEnd('\n'),
                Author: parts[2],
                Date: parts[3]));
        }
        return result;
    }

    /// <summary>
    /// Sentinel file-path prefix used to represent a commit message as a virtual
    /// diff file. Colons are illegal in Windows paths, so this can never collide
    /// with a real path under review.
    /// </summary>
    public const string CommitFilePrefix = "revue::commit::";

    /// <summary>
    /// Builds a virtual unified-diff file for each commit in base..head whose
    /// patch body is the commit message itself (one '+' line per message line).
    /// The frontend renders these through the same diff2html pipeline as real
    /// files and decorates the header using the attached CommitMeta.
    /// </summary>
    public static List<DiffFile> BuildCommitMessageDiffs(string @base, string head, string repoRoot)
    {
        var commits = GetCommits(@base, head, repoRoot);
        var result = new List<DiffFile>(commits.Count);
        foreach (var c in commits)
        {
            var lines = new List<string> { c.Subject };
            if (!string.IsNullOrEmpty(c.Body))
            {
                lines.Add("");
                lines.AddRange(c.Body.Split('\n'));
            }
            var file = CommitFilePrefix + c.Hash;
            var sb = new System.Text.StringBuilder();
            sb.Append("diff --git a/").Append(file).Append(" b/").Append(file).Append('\n');
            sb.Append("new file mode 100644\n");
            sb.Append("--- /dev/null\n");
            sb.Append("+++ b/").Append(file).Append('\n');
            sb.Append("@@ -0,0 +1,").Append(lines.Count).Append(" @@\n");
            foreach (var line in lines) sb.Append('+').Append(line).Append('\n');
            result.Add(new DiffFile(
                File: file,
                Patch: sb.ToString(),
                Additions: lines.Count,
                Deletions: 0,
                Commit: new CommitMeta(c.Hash, c.Subject, c.Author, c.Date)));
        }
        return result;
    }

    public static List<string> GetUntrackedFiles(string repoRoot)
    {
        var output = RunGit(["ls-files", "--others", "--exclude-standard"], repoRoot);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>
    /// Walks up from <paramref name="start"/> until it finds a directory
    /// containing a .git entry (dir for normal clones, file for worktrees) and
    /// returns that directory's full path. Throws if none is found.
    /// </summary>
    public static string FindRepoRoot(string start)
    {
        var p = new DirectoryInfo(Path.GetFullPath(start));
        while (true)
        {
            if (Directory.Exists(Path.Combine(p.FullName, ".git"))
                || File.Exists(Path.Combine(p.FullName, ".git")))
                return p.FullName;
            if (p.Parent == null)
                throw new InvalidOperationException($"No git repository found at or above {start}");
            p = p.Parent;
        }
    }

    /// <summary>
    /// Ensures the local-only .revue/ directory is excluded via
    /// .git/info/exclude (never .gitignore, which would be committed). Handles
    /// worktrees where .git is a file pointing at the real git dir.
    /// </summary>
    public static void EnsureRevueIgnored(string repoRoot)
    {
        const string entry = ".revue/";
        string gitDir;
        var dotGit = Path.Combine(repoRoot, ".git");
        if (File.Exists(dotGit))
        {
            // Worktree: .git is a file containing "gitdir: /path/to/git/dir"
            var content = File.ReadAllText(dotGit).Trim();
            if (content.StartsWith("gitdir:"))
                gitDir = Path.GetFullPath(content["gitdir:".Length..].Trim(), repoRoot);
            else
                return;
        }
        else if (Directory.Exists(dotGit))
        {
            gitDir = dotGit;
        }
        else
        {
            return;
        }

        var exclude = Path.Combine(gitDir, "info", "exclude");
        if (File.Exists(exclude))
        {
            var lines = File.ReadAllLines(exclude);
            if (lines.Any(l => l.Trim() == entry)) return;
            File.AppendAllText(exclude, $"\n{entry}\n");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
            File.WriteAllText(exclude, $"{entry}\n");
        }
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

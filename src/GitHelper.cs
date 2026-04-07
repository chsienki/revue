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

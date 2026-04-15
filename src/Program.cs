using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Revue;

// ── Version flag ──────────────────────────────────────────────────────────────
if (args.Length > 0 && args[0] is "--version" or "-v")
{
    var ver = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
    Console.WriteLine(ver);
    return;
}

// ── Repo root ────────────────────────────────────────────────────────────────
var startPath = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var repoRoot = FindRepoRoot(startPath);
GitHelper.ResolveDefaultBase(repoRoot); // validate git works
EnsureGitignore(repoRoot);
var defaultBase = GitHelper.ResolveDefaultBase(repoRoot);

// ── Static content ───────────────────────────────────────────────────────────
// Serve from disk when available (dev), fall back to embedded resource (published)
var staticDir = TryFindStaticDir();
if (staticDir != null)
    Console.WriteLine($"static →  {staticDir} (dev mode)");

// ── Port ─────────────────────────────────────────────────────────────────────
var port = FindFreePort(7878);
var url = $"http://127.0.0.1:{port}";

// ── Builder ──────────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(url);

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

var app = builder.Build();
app.UseCors();

// ── Routes ───────────────────────────────────────────────────────────────────

// GET /
app.MapGet("/", () =>
{
    if (staticDir != null)
        return Results.File(Path.Combine(staticDir, "index.html"), "text/html");
    return Results.Bytes(LoadEmbeddedResource("index.html"), "text/html");
});

// GET /manifest.json
app.MapGet("/manifest.json", () =>
{
    if (staticDir != null)
        return Results.File(Path.Combine(staticDir, "manifest.json"), "application/manifest+json");
    return Results.Bytes(LoadEmbeddedResource("manifest.json"), "application/manifest+json");
});

// GET /icon.svg
app.MapGet("/icon.svg", () =>
{
    if (staticDir != null)
        return Results.File(Path.Combine(staticDir, "icon.svg"), "image/svg+xml");
    return Results.Bytes(LoadEmbeddedResource("icon.svg"), "image/svg+xml");
});

// GET /api/config
var fullVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
// Strip source link metadata (e.g. "0.2.0+d52ebea.full-sha" → "0.2.0")
var version = fullVersion.Split('+')[0];
var commitHash = fullVersion.Contains('+') ? fullVersion.Split('+')[1].Split('.')[0] : null;

// ── Background update check ──────────────────────────────────────────────────
string? latestVersion = null;
string? updateCommand = null;
_ = Task.Run(async () =>
{
    try
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("revue-update-check");
        http.Timeout = TimeSpan.FromSeconds(10);

        // Use the GitHub API to get the latest release (avoids redirect-following issues)
        var response = await http.GetAsync("https://api.github.com/repos/chsienki/revue/releases/latest");
        if (!response.IsSuccessStatusCode) return;

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var tagName = doc.RootElement.GetProperty("tag_name").GetString();
        if (tagName is null) return;

        var latest = tagName.TrimStart('v');
        if (latest != version && IsNewerVersion(latest, version))
        {
            latestVersion = latest;
            updateCommand = "copilot plugin update revue@chsienki";
        }
    }
    catch
    {
        // Silently ignore — update check is best-effort
    }
});

app.MapGet("/api/config", () => Results.Json(new
{
    defaultBase,
    repoRoot,
    version,
    commitHash,
    latestVersion,
    updateCommand,
}, jsonOpts));

// GET /api/branches
app.MapGet("/api/branches", () =>
{
    try
    {
        var raw = GitHelper.RunGit(["branch", "-a", "--format=%(refname:short)"], repoRoot);
        var branches = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .Select(b => b.Trim())
                          .Where(b => b.Length > 0)
                          .Distinct()
                          .ToList();
        return Results.Json(branches, jsonOpts);
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

// GET /api/changes-hash?base=X&head=Y — lightweight fingerprint for polling
app.MapGet("/api/changes-hash", (string? @base, string? head) =>
{
    try
    {
        var b = @base ?? defaultBase;
        var h = head ?? "HEAD";
        string stat;
        if (h == "HEAD")
        {
            var mergeBase = GitHelper.GetMergeBase(b, "HEAD", repoRoot);
            stat = GitHelper.RunGit(["diff", "--stat", mergeBase], repoRoot);
            var untracked = GitHelper.RunGit(["ls-files", "--others", "--exclude-standard"], repoRoot);
            stat += untracked;
        }
        else
        {
            stat = GitHelper.RunGit(["diff", "--stat", $"{b}...{h}"], repoRoot);
        }
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(stat)))[..16];
        return Results.Json(new { hash }, jsonOpts);
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

// GET /api/log?base=X&head=Y
app.MapGet("/api/log", (string? @base, string? head) =>
{
    @base ??= defaultBase;
    head ??= "HEAD";
    try
    {
        var raw = GitHelper.RunGit(
            ["log", "--oneline", $"{@base}..{head}"], repoRoot);
        var commits = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                         .Select(l =>
                         {
                             var sp = l.IndexOf(' ');
                             return new { hash = l[..sp], message = l[(sp + 1)..] };
                         }).ToList();

        if (head == "HEAD" && GitHelper.HasWorkingTreeChanges(repoRoot))
            commits.Insert(0, new { hash = "~working~", message = "Working tree changes" });

        return Results.Json(commits, jsonOpts);
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

// GET /api/diff?base=X&head=Y&ignoreWhitespace=true
app.MapGet("/api/diff", (string? @base, string? head, bool? ignoreWhitespace) =>
{
    @base ??= defaultBase;
    head ??= "HEAD";
    var wsFlag = ignoreWhitespace == true ? "-w" : null;
    try
    {
        string raw;
        if (head == "HEAD")
        {
            var mergeBase = GitHelper.GetMergeBase(@base, "HEAD", repoRoot);
            var args = new List<string> { "diff", "--unified=5" };
            if (wsFlag != null) args.Add(wsFlag);
            args.Add(mergeBase);
            raw = GitHelper.RunGit([.. args], repoRoot);

            // Append untracked files
            foreach (var f in GitHelper.GetUntrackedFiles(repoRoot))
                raw += GitHelper.GetUntrackedFileDiff(f, repoRoot);
        }
        else
        {
            var args = new List<string> { "diff", "--unified=5" };
            if (wsFlag != null) args.Add(wsFlag);
            args.Add($"{@base}...{head}");
            raw = GitHelper.RunGit([.. args], repoRoot);
        }
        var files = GitHelper.ParseDiff(raw);
        return Results.Json(files, jsonOpts);
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

// GET /api/file-diff?base=X&head=Y&file=F&ignoreWhitespace=true&context=5
app.MapGet("/api/file-diff", (string? @base, string? head, string? file, bool? ignoreWhitespace, int? context) =>
{
    @base ??= defaultBase;
    head ??= "HEAD";
    if (string.IsNullOrWhiteSpace(file))
        return Results.BadRequest("file parameter required");
    var wsFlag = ignoreWhitespace == true ? "-w" : null;
    var contextLines = context ?? 5;
    try
    {
        string raw;
        if (head == "HEAD")
        {
            var mergeBase = GitHelper.GetMergeBase(@base, "HEAD", repoRoot);
            var args = new List<string> { "diff", $"--unified={contextLines}" };
            if (wsFlag != null) args.Add(wsFlag);
            args.AddRange([mergeBase, "--", file]);
            raw = GitHelper.RunGit([.. args], repoRoot);

            // If no diff found, check if it's an untracked file
            if (string.IsNullOrWhiteSpace(raw) && GitHelper.GetUntrackedFiles(repoRoot).Contains(file))
                raw = GitHelper.GetUntrackedFileDiff(file, repoRoot);
        }
        else
        {
            var args = new List<string> { "diff", $"--unified={contextLines}" };
            if (wsFlag != null) args.Add(wsFlag);
            args.AddRange([$"{@base}...{head}", "--", file]);
            raw = GitHelper.RunGit([.. args], repoRoot);
        }
        var files = GitHelper.ParseDiff(raw);
        return Results.Json(files.FirstOrDefault(), jsonOpts);
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

// GET /api/comments
app.MapGet("/api/comments", () =>
    Results.Json(CommentsStore.Load(repoRoot), jsonOpts));

// POST /api/comments
app.MapPost("/api/comments", async (HttpRequest req) =>
{
    CommentRequest? cr;
    try { cr = await req.ReadFromJsonAsync<CommentRequest>(jsonOpts); }
    catch { return Results.BadRequest("Invalid JSON"); }
    if (cr == null) return Results.BadRequest("Empty body");

    var comments = CommentsStore.Load(repoRoot);

    if (!string.IsNullOrEmpty(cr.Id))
    {
        // update existing
        var idx = comments.FindIndex(c => c.Id == cr.Id);
        if (idx >= 0)
        {
            comments[idx] = comments[idx] with
            {
                Body = cr.Body,
                Resolved = cr.Resolved,
            };
            CommentsStore.Save(repoRoot, comments);
            return Results.Json(comments[idx], jsonOpts);
        }
    }

    // create new
    var comment = new Comment(
        Id: cr.Id ?? Guid.NewGuid().ToString(),
        File: cr.File,
        Line: cr.Line,
        LineContent: cr.LineContent,
        Base: cr.Base,
        Head: cr.Head,
        Side: cr.Side,
        Body: cr.Body,
        Author: cr.Author ?? "user",
        Created: DateTime.UtcNow.ToString("o"),
        Resolved: cr.Resolved,
        Replies: []
    );
    comments.Add(comment);
    CommentsStore.Save(repoRoot, comments);
    return Results.Json(comment, jsonOpts);
});

// POST /api/comments/{id}/replies
app.MapPost("/api/comments/{id}/replies", async (string id, HttpRequest req) =>
{
    ReplyRequest? rr;
    try { rr = await req.ReadFromJsonAsync<ReplyRequest>(jsonOpts); }
    catch { return Results.BadRequest("Invalid JSON"); }
    if (rr == null) return Results.BadRequest("Empty body");

    var comments = CommentsStore.Load(repoRoot);
    var idx = comments.FindIndex(c => c.Id == id);
    if (idx < 0) return Results.NotFound();

    var reply = new Reply(
        Id: Guid.NewGuid().ToString(),
        Author: rr.Author,
        Body: rr.Body,
        Created: DateTime.UtcNow.ToString("o")
    );
    var replies = comments[idx].Replies ?? [];
    replies.Add(reply);
    comments[idx] = comments[idx] with { Replies = replies };
    CommentsStore.Save(repoRoot, comments);
    return Results.Json(reply, jsonOpts);
});

// DELETE /api/comments/{id}
app.MapDelete("/api/comments/{id}", (string id) =>
{
    var comments = CommentsStore.Load(repoRoot);
    var before = comments.Count;
    comments.RemoveAll(c => c.Id == id);
    if (comments.Count == before)
        return Results.NotFound();
    CommentsStore.Save(repoRoot, comments);
    return Results.Ok();
});

// POST /api/comments/delete-batch
app.MapPost("/api/comments/delete-batch", async (HttpRequest req) =>
{
    List<string>? ids;
    try { ids = await req.ReadFromJsonAsync<List<string>>(jsonOpts); }
    catch { return Results.BadRequest("Invalid JSON"); }
    if (ids == null || ids.Count == 0) return Results.BadRequest("Empty list");

    var comments = CommentsStore.Load(repoRoot);
    var idSet = new HashSet<string>(ids);
    comments.RemoveAll(c => idSet.Contains(c.Id));
    CommentsStore.Save(repoRoot, comments);
    return Results.Ok();
});

// GET /api/ref-status?ref=X — check if a remote-tracking ref is behind its remote
app.MapGet("/api/ref-status", (string @ref) =>
{
    try
    {
        var status = GitHelper.CheckRefStatus(@ref, repoRoot);
        if (status == null)
            return Results.Json(new { tracking = false }, jsonOpts);
        return Results.Json(new { tracking = true, remote = status.Remote, branch = status.Branch, behind = status.Behind }, jsonOpts);
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

// POST /api/fetch?remote=X&branch=Y — fetch a specific branch from a remote
app.MapPost("/api/fetch", (string remote, string branch) =>
{
    try
    {
        GitHelper.FetchRef(remote, branch, repoRoot);
        return Results.Ok();
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

// ── Start ────────────────────────────────────────────────────────────────────
Console.WriteLine($"revue  →  {url}");
Console.WriteLine($"repo   →  {repoRoot}");

// Open browser after 800ms
_ = Task.Run(async () =>
{
    await Task.Delay(800);
    OpenBrowser(url);
});

app.Run();

// ── Helpers ──────────────────────────────────────────────────────────────────
static string FindRepoRoot(string start)
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

static void EnsureGitignore(string repoRoot)
{
    const string entry = ".revue/";
    // Resolve the actual .git directory (handles worktrees where .git is a file)
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

static int FindFreePort(int preferred)
{
    for (var port = preferred; port < preferred + 100; port++)
    {
        try
        {
            using var s = new TcpListener(IPAddress.Loopback, port);
            s.Start(); s.Stop();
            return port;
        }
        catch (SocketException) { }
    }
    throw new InvalidOperationException("No free port found");
}

static byte[] LoadEmbeddedResource(string name)
{
    var assembly = Assembly.GetExecutingAssembly();
    using var stream = assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"Embedded resource '{name}' not found");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
}

static string? TryFindStaticDir()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir != null)
    {
        var candidate = Path.Combine(dir.FullName, "static");
        if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "index.html")))
            return candidate;
        dir = dir.Parent;
    }
    return null;
}

static void OpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows())
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
        else if (OperatingSystem.IsMacOS())
            System.Diagnostics.Process.Start("open", url);
        else
            System.Diagnostics.Process.Start("xdg-open", url);
    }
    catch { /* best-effort */ }
}

static bool IsNewerVersion(string candidate, string current)
{
    if (Version.TryParse(candidate, out var c) && Version.TryParse(current, out var v))
        return c > v;
    return string.Compare(candidate, current, StringComparison.Ordinal) > 0;
}

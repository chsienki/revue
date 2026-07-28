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

// ── Launch repo ───────────────────────────────────────────────────────────────
var startPath = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : Directory.GetCurrentDirectory();
string launchRoot;
try { launchRoot = GitHelper.FindRepoRoot(startPath); }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.Exit(1); return; }

// ── Single-instance hand-off ──────────────────────────────────────────────────
// If a revue instance is already running on this machine, register this repo
// with it and open the browser focused on that repo instead of starting a
// second server. Idea #1: one instance targets multiple directories.
var instanceFile = InstanceFilePath();
var runningBaseUrl = await TryHandOffAsync(instanceFile, launchRoot);
if (runningBaseUrl != null)
{
    var handoffUrl = $"{runningBaseUrl}/#repo={Uri.EscapeDataString(launchRoot)}";
    Console.WriteLine($"revue already running  →  {runningBaseUrl}");
    Console.WriteLine($"added repo             →  {launchRoot}");
    OpenBrowser(handoffUrl);
    return;
}

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

// ── Repo registry ────────────────────────────────────────────────────────────
var registry = new RepoRegistry();
try { registry.Add(launchRoot); }
catch (Exception ex) { Console.Error.WriteLine($"Failed to open {launchRoot}: {ex.Message}"); Environment.Exit(1); return; }

// Resolves the target repo for a request, or short-circuits with an error when
// none is registered / the git operation throws.
IResult WithRepo(string? repo, Func<RepoRegistry.Repo, IResult> fn)
{
    var r = registry.Resolve(repo);
    if (r is null) return Results.Problem("No repository registered");
    try { return fn(r); }
    catch (Exception ex) { return Results.Problem(ex.Message); }
}

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

// GET /api/ping — identifies this port as a revue instance for hand-off probing
app.MapGet("/api/ping", () => Results.Json(new { app = "revue", version }, jsonOpts));

// GET /api/repos — the repos this instance is serving (for the selector)
app.MapGet("/api/repos", () =>
    Results.Json(new { repos = registry.List(), primary = registry.PrimaryPath }, jsonOpts));

// POST /api/repos — register a repo (used by the single-instance hand-off)
app.MapPost("/api/repos", async (HttpRequest req) =>
{
    AddRepoRequest? body;
    try { body = await req.ReadFromJsonAsync<AddRepoRequest>(jsonOpts); }
    catch { return Results.BadRequest("Invalid JSON"); }
    if (body == null || string.IsNullOrWhiteSpace(body.Path))
        return Results.BadRequest("path required");
    try { return Results.Json(registry.Add(body.Path), jsonOpts); }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

// DELETE /api/repos?path=X — stop serving a repo (the × in the selector)
app.MapDelete("/api/repos", (string? path) =>
{
    if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest("path required");
    return registry.Remove(path)
        ? Results.Ok()
        : Results.Problem("Cannot remove: unknown repo or last remaining repo");
});

// GET /api/config?repo=X
app.MapGet("/api/config", (string? repo) =>
{
    var r = registry.Resolve(repo);
    return Results.Json(new
    {
        version,
        commitHash,
        latestVersion,
        updateCommand,
        repos = registry.List(),
        repo = r?.Path,
        repoRoot = r?.Path,
        defaultBase = r?.DefaultBase,
        currentBranch = r == null ? null : GitHelper.GetCurrentBranch(r.Path),
    }, jsonOpts);
});

// GET /api/current-branch?repo=X — lightweight endpoint for polling branch changes
app.MapGet("/api/current-branch", (string? repo) =>
{
    var r = registry.Resolve(repo);
    return Results.Json(new { currentBranch = r == null ? null : GitHelper.GetCurrentBranch(r.Path) }, jsonOpts);
});

// GET /api/branches?repo=X
app.MapGet("/api/branches", (string? repo) => WithRepo(repo, r =>
{
    var raw = GitHelper.RunGit(["branch", "-a", "--format=%(refname:short)"], r.Path);
    var branches = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                      .Select(b => b.Trim())
                      .Where(b => b.Length > 0)
                      .Distinct()
                      .ToList();
    return Results.Json(branches, jsonOpts);
}));

// GET /api/changes-hash?repo=Z&base=X&head=Y — lightweight fingerprint for polling
app.MapGet("/api/changes-hash", (string? repo, string? @base, string? head) => WithRepo(repo, r =>
{
    var b = @base ?? r.DefaultBase;
    var h = head ?? "HEAD";
    string stat;
    if (h == "HEAD")
    {
        var mergeBase = GitHelper.GetMergeBase(b, "HEAD", r.Path);
        stat = GitHelper.RunGit(["diff", "--stat", mergeBase], r.Path);
        var untracked = GitHelper.RunGit(["ls-files", "--others", "--exclude-standard"], r.Path);
        stat += untracked;
    }
    else
    {
        stat = GitHelper.RunGit(["diff", "--stat", $"{b}...{h}"], r.Path);
    }
    var hash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(stat)))[..16];
    return Results.Json(new { hash }, jsonOpts);
}));

// GET /api/log?repo=Z&base=X&head=Y
app.MapGet("/api/log", (string? repo, string? @base, string? head) => WithRepo(repo, r =>
{
    var b = @base ?? r.DefaultBase;
    var h = head ?? "HEAD";
    var raw = GitHelper.RunGit(["log", "--oneline", $"{b}..{h}"], r.Path);
    var commits = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(l =>
                     {
                         var sp = l.IndexOf(' ');
                         return new { hash = l[..sp], message = l[(sp + 1)..] };
                     }).ToList();

    // Surface the working-tree pseudo-commit whenever head points at the current
    // checkout -- whether spelled "HEAD", the branch name, or HEAD's SHA -- so a
    // review deep-linked with an explicit head still reaches uncommitted changes.
    if ((h == "HEAD" || GitHelper.ResolvesToHead(h, r.Path)) && GitHelper.HasWorkingTreeChanges(r.Path))
        commits.Insert(0, new { hash = "~working~", message = "Working tree changes" });

    return Results.Json(commits, jsonOpts);
}));

// GET /api/diff?repo=Z&base=X&head=Y&ignoreWhitespace=true
app.MapGet("/api/diff", (string? repo, string? @base, string? head, bool? ignoreWhitespace) => WithRepo(repo, r =>
{
    var b = @base ?? r.DefaultBase;
    var h = head ?? "HEAD";
    var wsFlag = ignoreWhitespace == true ? "-w" : null;
    string raw;
    if (h == "HEAD")
    {
        var mergeBase = GitHelper.GetMergeBase(b, "HEAD", r.Path);
        var gitArgs = new List<string> { "diff", "--unified=5" };
        if (wsFlag != null) gitArgs.Add(wsFlag);
        gitArgs.Add(mergeBase);
        raw = GitHelper.RunGit([.. gitArgs], r.Path);

        // Append untracked files
        foreach (var f in GitHelper.GetUntrackedFiles(r.Path))
            raw += GitHelper.GetUntrackedFileDiff(f, r.Path);
    }
    else
    {
        var gitArgs = new List<string> { "diff", "--unified=5" };
        if (wsFlag != null) gitArgs.Add(wsFlag);
        gitArgs.Add($"{b}...{h}");
        raw = GitHelper.RunGit([.. gitArgs], r.Path);
    }
    var files = GitHelper.ParseDiff(raw);
    // Prepend a virtual diff file per commit message so commit messages
    // flow through the same render/comment/viewed pipeline as real files.
    // Skip when base == head (working-tree-only view: no commits to list).
    if (!string.Equals(b, h, StringComparison.Ordinal))
    {
        try
        {
            var commitDiffs = GitHelper.BuildCommitMessageDiffs(b, h, r.Path);
            files.InsertRange(0, commitDiffs);
        }
        catch { /* best-effort; a bad ref shouldn't break the file diff response */ }
    }
    return Results.Json(files, jsonOpts);
}));

// GET /api/file-diff?repo=Z&base=X&head=Y&file=F&ignoreWhitespace=true&context=5
app.MapGet("/api/file-diff", (string? repo, string? @base, string? head, string? file, bool? ignoreWhitespace, int? context) => WithRepo(repo, r =>
{
    if (string.IsNullOrWhiteSpace(file))
        return Results.BadRequest("file parameter required");
    var b = @base ?? r.DefaultBase;
    var h = head ?? "HEAD";
    var wsFlag = ignoreWhitespace == true ? "-w" : null;
    var contextLines = context ?? 5;
    string raw;
    if (h == "HEAD")
    {
        var mergeBase = GitHelper.GetMergeBase(b, "HEAD", r.Path);
        var gitArgs = new List<string> { "diff", $"--unified={contextLines}" };
        if (wsFlag != null) gitArgs.Add(wsFlag);
        gitArgs.AddRange([mergeBase, "--", file]);
        raw = GitHelper.RunGit([.. gitArgs], r.Path);

        // If no diff found, check if it's an untracked file
        if (string.IsNullOrWhiteSpace(raw) && GitHelper.GetUntrackedFiles(r.Path).Contains(file))
            raw = GitHelper.GetUntrackedFileDiff(file, r.Path);
    }
    else
    {
        var gitArgs = new List<string> { "diff", $"--unified={contextLines}" };
        if (wsFlag != null) gitArgs.Add(wsFlag);
        gitArgs.AddRange([$"{b}...{h}", "--", file]);
        raw = GitHelper.RunGit([.. gitArgs], r.Path);
    }
    var files = GitHelper.ParseDiff(raw);
    return Results.Json(files.FirstOrDefault(), jsonOpts);
}));

// GET /api/comments?repo=X
app.MapGet("/api/comments", (string? repo) => WithRepo(repo, r =>
    Results.Json(CommentsStore.Load(r.Path), jsonOpts)));

// POST /api/comments?repo=X
app.MapPost("/api/comments", async (string? repo, HttpRequest req) =>
{
    var r = registry.Resolve(repo);
    if (r is null) return Results.Problem("No repository registered");

    CommentRequest? cr;
    try { cr = await req.ReadFromJsonAsync<CommentRequest>(jsonOpts); }
    catch { return Results.BadRequest("Invalid JSON"); }
    if (cr == null) return Results.BadRequest("Empty body");

    var comments = CommentsStore.Load(r.Path);

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
            CommentsStore.Save(r.Path, comments);
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
        Replies: [],
        // Capture the current git branch so we can filter comments per-branch.
        // null when detached HEAD or git fails — such comments show on all branches.
        Branch: GitHelper.GetCurrentBranch(r.Path)
    );
    comments.Add(comment);
    CommentsStore.Save(r.Path, comments);
    return Results.Json(comment, jsonOpts);
});

// POST /api/comments/{id}/replies?repo=X
app.MapPost("/api/comments/{id}/replies", async (string id, string? repo, HttpRequest req) =>
{
    ReplyRequest? rr;
    try { rr = await req.ReadFromJsonAsync<ReplyRequest>(jsonOpts); }
    catch { return Results.BadRequest("Invalid JSON"); }
    if (rr == null) return Results.BadRequest("Empty body");

    // Prefer an explicit repo; otherwise find the store that owns this id so
    // external callers (e.g. the Copilot skill) can reply without naming one.
    var r = !string.IsNullOrEmpty(repo)
        ? registry.Resolve(repo)
        : registry.FindByCommentId(id) ?? registry.Resolve(null);
    if (r is null) return Results.NotFound();

    var comments = CommentsStore.Load(r.Path);
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
    CommentsStore.Save(r.Path, comments);
    return Results.Json(reply, jsonOpts);
});

// DELETE /api/comments/{id}?repo=X
app.MapDelete("/api/comments/{id}", (string id, string? repo) =>
{
    var r = !string.IsNullOrEmpty(repo)
        ? registry.Resolve(repo)
        : registry.FindByCommentId(id) ?? registry.Resolve(null);
    if (r is null) return Results.NotFound();

    var comments = CommentsStore.Load(r.Path);
    var before = comments.Count;
    comments.RemoveAll(c => c.Id == id);
    if (comments.Count == before)
        return Results.NotFound();
    CommentsStore.Save(r.Path, comments);
    return Results.Ok();
});

// POST /api/comments/delete-batch?repo=X
app.MapPost("/api/comments/delete-batch", async (string? repo, HttpRequest req) =>
{
    var r = registry.Resolve(repo);
    if (r is null) return Results.Problem("No repository registered");

    List<string>? ids;
    try { ids = await req.ReadFromJsonAsync<List<string>>(jsonOpts); }
    catch { return Results.BadRequest("Invalid JSON"); }
    if (ids == null || ids.Count == 0) return Results.BadRequest("Empty list");

    var comments = CommentsStore.Load(r.Path);
    var idSet = new HashSet<string>(ids);
    comments.RemoveAll(c => idSet.Contains(c.Id));
    CommentsStore.Save(r.Path, comments);
    return Results.Ok();
});

// GET /api/ref-status?repo=Z&ref=X — check if a remote-tracking ref is behind its remote
app.MapGet("/api/ref-status", (string? repo, string @ref) => WithRepo(repo, r =>
{
    var status = GitHelper.CheckRefStatus(@ref, r.Path);
    if (status == null)
        return Results.Json(new { tracking = false }, jsonOpts);
    return Results.Json(new { tracking = true, remote = status.Remote, branch = status.Branch, behind = status.Behind }, jsonOpts);
}));

// POST /api/fetch?repo=Z&remote=X&branch=Y — fetch a specific branch from a remote
app.MapPost("/api/fetch", (string? repo, string remote, string branch) => WithRepo(repo, r =>
{
    GitHelper.FetchRef(remote, branch, r.Path);
    return Results.Ok();
}));

// ── Instance file ────────────────────────────────────────────────────────────
// Record our port so a later `revue <other-repo>` launch can find and reuse us.
WriteInstanceFile(instanceFile, port);
app.Lifetime.ApplicationStopping.Register(() => DeleteInstanceFile(instanceFile, port));
AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteInstanceFile(instanceFile, port);

// ── Start ────────────────────────────────────────────────────────────────────
Console.WriteLine($"revue  →  {url}");
Console.WriteLine($"repo   →  {launchRoot}");

// Open browser after 800ms
_ = Task.Run(async () =>
{
    await Task.Delay(800);
    OpenBrowser(url);
});

app.Run();

// ── Helpers ──────────────────────────────────────────────────────────────────
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

// Path of the per-user file that records the running instance's port. Same
// location for the writer (a fresh instance) and the reader (a later launch
// probing for an existing instance) since both run as the same user.
static string InstanceFilePath()
{
    var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (string.IsNullOrEmpty(baseDir))
        baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
    return Path.Combine(baseDir, "revue", "instance.json");
}

static void WriteInstanceFile(string path, int port)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { port, pid = Environment.ProcessId }));
    }
    catch { /* best-effort: hand-off simply won't find us */ }
}

// Only delete the file if it still points at our port, so we never clobber a
// newer instance's registration during a racy restart.
static void DeleteInstanceFile(string path, int port)
{
    try
    {
        if (!File.Exists(path)) return;
        if (TryReadInstancePort(path) == port) File.Delete(path);
    }
    catch { /* best-effort */ }
}

static int? TryReadInstancePort(string path)
{
    try
    {
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("port", out var p) && p.TryGetInt32(out var port))
            return port;
    }
    catch { /* unreadable / malformed → treat as no instance */ }
    return null;
}

// If a healthy revue instance is already running, register this repo with it
// and return its base URL; otherwise return null (caller starts a fresh server).
static async Task<string?> TryHandOffAsync(string instanceFile, string repoRoot)
{
    var port = TryReadInstancePort(instanceFile);
    if (port is null) return null;

    var baseUrl = $"http://127.0.0.1:{port}";
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    try
    {
        // Confirm the port actually belongs to a revue instance, not something
        // else that grabbed it after a crash left a stale file behind.
        var ping = await http.GetAsync($"{baseUrl}/api/ping");
        if (!ping.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await ping.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("app", out var appEl) || appEl.GetString() != "revue")
            return null;

        var payload = JsonSerializer.Serialize(new { path = repoRoot });
        var resp = await http.PostAsync($"{baseUrl}/api/repos",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
        return resp.IsSuccessStatusCode ? baseUrl : null;
    }
    catch
    {
        // Connection refused / timeout → stale file, start fresh.
        return null;
    }
}

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Revue;

// ── Version ──────────────────────────────────────────────────────────────────
var fullVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
// Strip source link metadata (e.g. "0.2.0+d52ebea.full-sha" → "0.2.0")
var version = fullVersion.Split('+')[0];
var commitHash = fullVersion.Contains('+') ? fullVersion.Split('+')[1].Split('.')[0] : null;

if (args.Length > 0 && args[0] is "--version" or "-v")
{
    Console.WriteLine(fullVersion);
    return;
}

// ── Args ─────────────────────────────────────────────────────────────────────
// Positional args are repos to serve; a replacement spawned by an outgoing
// instance also gets --takeover <pid> --port <n> so it can inherit that port.
var repoArgs = new List<string>();
int? takeoverPid = null;
int? requestedPort = null;
for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (arg == "--takeover" && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid)) { takeoverPid = pid; i++; }
    else if (arg == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) { requestedPort = p; i++; }
    else if (!arg.StartsWith('-')) repoArgs.Add(arg);
}
if (repoArgs.Count == 0) repoArgs.Add(Directory.GetCurrentDirectory());

// ── Launch repos ─────────────────────────────────────────────────────────────
var launchRoots = new List<string>();
string? firstRepoError = null;
foreach (var path in repoArgs)
{
    try
    {
        var root = GitHelper.FindRepoRoot(path);
        if (!launchRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) launchRoots.Add(root);
    }
    catch (Exception ex)
    {
        // An inherited repo that has since disappeared shouldn't sink the restart.
        firstRepoError ??= ex.Message;
        Console.Error.WriteLine($"Skipping {path}: {ex.Message}");
    }
}
if (launchRoots.Count == 0)
{
    Console.Error.WriteLine(firstRepoError ?? "No repository to serve");
    Environment.Exit(1);
    return;
}
var launchRoot = launchRoots[0];

// ── Single-instance hand-off ──────────────────────────────────────────────────
// One instance serves several repos, so a later launch normally registers its
// repo with the running server and exits. A *newer* binary instead takes over:
// it inherits the repo set, asks the old instance to quit, and binds its port,
// so installing an update applies itself the next time revue is launched.
var instanceFile = InstanceFilePath();
var portPreference = requestedPort ?? 7878;
// A process that exists to replace another must land on that port or not at all:
// starting a second server elsewhere would leave the user's tabs and every later
// hand-off pointing at the wrong one.
var claimingPort = takeoverPid is not null;

if (takeoverPid is not null)
{
    // Spawned as a replacement: the outgoing instance is on its way out.
    WaitForProcessExit(takeoverPid.Value, TimeSpan.FromSeconds(20));
}
else
{
    var running = await ProbeInstanceAsync(instanceFile);
    if (running is not null)
    {
        var (runningPort, runningPid, runningVersion, baseUrl) = running.Value;
        if (!InstalledVersions.IsNewer(version, runningVersion))
        {
            // Same or older: hand this repo off and let the running instance keep serving.
            if (await RegisterRepoAsync(baseUrl, launchRoot))
            {
                Console.WriteLine($"revue already running  →  {baseUrl}");
                Console.WriteLine($"added repo             →  {launchRoot}");
                OpenBrowser($"{baseUrl}/#repo={Uri.EscapeDataString(launchRoot)}");
                return;
            }
        }
        else
        {
            Console.WriteLine($"replacing revue {runningVersion} (pid {runningPid}) with {version}");
            foreach (var repo in await FetchReposAsync(baseUrl))
            {
                if (!launchRoots.Contains(repo, StringComparer.OrdinalIgnoreCase)) launchRoots.Add(repo);
            }
            await RequestShutdownAsync(baseUrl);
            WaitForProcessExit(runningPid, TimeSpan.FromSeconds(20));
            portPreference = runningPort;
            claimingPort = true;
        }
    }
}

// ── Static content ───────────────────────────────────────────────────────────
// Serve from disk when available (dev), fall back to embedded resource (published)
var staticDir = TryFindStaticDir();
if (staticDir != null)
    Console.WriteLine($"static →  {staticDir} (dev mode)");

// ── Port ─────────────────────────────────────────────────────────────────────
// Prefer the outgoing instance's port so open tabs and armed sessions reconnect
// to the same address.
if (!WaitForPortFree(portPreference, TimeSpan.FromSeconds(20)))
{
    if (claimingPort)
    {
        Console.Error.WriteLine($"port {portPreference} is still in use — the instance being replaced is still running, so this one is stopping rather than starting a second server");
        Environment.Exit(1);
        return;
    }
    Console.Error.WriteLine($"port {portPreference} busy — falling back to the next free port");
}
var port = FindFreePort(portPreference);
var url = $"http://127.0.0.1:{port}";

// ── Builder ──────────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(url);

// Same-origin only: the UI is served from this very origin, and the agent
// hand-off endpoints hand free-text straight to a privileged Copilot session,
// so a page on another origin must not be able to reach them.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(url, $"http://localhost:{port}").AllowAnyMethod().AllowAnyHeader()));

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

// ── Repo registry ────────────────────────────────────────────────────────────
var registry = new RepoRegistry();
var agentQueue = new AgentRequestQueue();
foreach (var root in launchRoots)
{
    try { registry.Add(root); }
    catch (Exception ex) { Console.Error.WriteLine($"Failed to open {root}: {ex.Message}"); }
}
if (registry.PrimaryPath is null)
{
    Console.Error.WriteLine($"Failed to open {launchRoot}");
    Environment.Exit(1);
    return;
}

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

// CORS alone doesn't protect these routes: a cross-origin POST with no custom
// headers is a "simple request" the browser delivers before applying the policy,
// which would let any page the user visits shut revue down or make it spawn a
// process. Non-browser callers (the hand-off, curl) send no Origin at all.
var allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    url,
    $"http://localhost:{port}",
};
app.Use(async (ctx, next) =>
{
    var origin = ctx.Request.Headers.Origin.ToString();
    if (!string.IsNullOrEmpty(origin) && !allowedOrigins.Contains(origin))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});
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
        if (latest != version && InstalledVersions.IsNewer(latest, version))
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

// ── Installed-version watch ──────────────────────────────────────────────────
// A newer revue can be installed while this one runs -- `copilot plugin update`
// rewrites the plugin's VERSION pin long before any binary is downloaded. Watch
// for both so the UI can offer to switch without the user launching anything.
UpdateTarget? updateReady = InstalledVersions.FindNewerThan(version);
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(60));
        try { updateReady = InstalledVersions.FindNewerThan(version); }
        catch { /* best-effort */ }
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
        updateReady = UpdateReadyPayload(),
        repos = registry.List(),
        repo = r?.Path,
        repoRoot = r?.Path,
        defaultBase = r?.DefaultBase,
        currentBranch = r == null ? null : GitHelper.GetCurrentBranch(r.Path),
    }, jsonOpts);
});

// GET /api/update-status — polled by the UI so a version installed after startup
// surfaces without a refresh
app.MapGet("/api/update-status", () => Results.Json(new
{
    version,
    latestVersion,
    updateCommand,
    updateReady = UpdateReadyPayload(),
}, jsonOpts));

object? UpdateReadyPayload()
{
    var target = updateReady;
    if (target is null) return null;
    // A pin with no bootstrap script can't be applied, so don't offer it.
    if (target.NeedsDownload && target.BootstrapScript is null) return null;
    return new { version = target.Version, needsDownload = target.NeedsDownload };
}

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
    // The PR description goes first: it's what a reviewer reads before the
    // commits and the code.
    try
    {
        var prDiff = GitHelper.BuildPrMessageDiff(r.Path, b, h);
        if (prDiff is not null) files.Insert(0, prDiff);
    }
    catch { /* best-effort */ }
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
        Branch: GitHelper.GetCurrentBranch(r.Path),
        // Anchor the comment to the commit it was written against, so it can be
        // scoped to the commit range being viewed and so the exact text it
        // refers to stays recoverable after rebases move the lines.
        Commit: GitHelper.ResolveCommentCommit(cr.File, cr.Base, cr.Head, r.Path)
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

// POST /api/comments/remap-commits?repo=X
// Body: { "<old-sha>": "<new-sha>", ... }
// Rewriting history (rebase, amend) leaves comments anchored to shas that are
// no longer in the range, which hides them. Only whoever did the rewrite knows
// how the commits map, so they repoint the comments here.
app.MapPost("/api/comments/remap-commits", async (string? repo, HttpRequest req) =>
{
    var r = registry.Resolve(repo);
    if (r is null) return Results.Problem("No repository registered");

    Dictionary<string, string>? map;
    try { map = await req.ReadFromJsonAsync<Dictionary<string, string>>(jsonOpts); }
    catch { return Results.BadRequest("Invalid JSON"); }
    if (map is null || map.Count == 0) return Results.BadRequest("Empty map");

    // Callers naturally deal in short shas, so match on prefix and store the
    // expanded form.
    var pairs = map
        .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
        .Select(kv => (Old: kv.Key.Trim(), New: GitHelper.ExpandSha(kv.Value.Trim(), r.Path)))
        .ToList();

    string? Remap(string? sha)
    {
        if (sha is null || sha == GitHelper.WorkingTreeCommit) return sha;
        foreach (var (oldSha, newSha) in pairs)
            if (sha.StartsWith(oldSha, StringComparison.OrdinalIgnoreCase)) return newSha;
        return sha;
    }

    var comments = CommentsStore.Load(r.Path);
    var updated = 0;
    for (var i = 0; i < comments.Count; i++)
    {
        var c = comments[i];
        // A commit-message comment carries the sha in its file path as well, so
        // both have to move together or it would anchor to one commit and render
        // against another.
        var file = c.File.StartsWith(GitHelper.CommitFilePrefix, StringComparison.Ordinal)
            ? GitHelper.CommitFilePrefix + Remap(c.File[GitHelper.CommitFilePrefix.Length..])
            : c.File;
        var commit = Remap(c.Commit);
        if (file == c.File && commit == c.Commit) continue;
        comments[i] = c with { File = file, Commit = commit };
        updated++;
    }
    if (updated > 0) CommentsStore.Save(r.Path, comments);
    return Results.Json(new { updated }, jsonOpts);
});

// ── PR description draft ─────────────────────────────────────────────────────
// Copilot's answer to "what would you write up as the PR body right now?",
// kept current as the review changes it and reviewable as a virtual diff file.

// GET /api/pr?repo=X
app.MapGet("/api/pr", (string? repo, string? @base, string? head) => WithRepo(repo, r =>
{
    var draft = PrDraftStore.Load(r.Path);
    if (draft is null) return Results.Json(new { draft = (PrDraft?)null }, jsonOpts);
    var b = @base ?? r.DefaultBase;
    var h = head ?? "HEAD";
    return Results.Json(new
    {
        draft,
        stale = PrDraftStore.IsStale(draft, b, h, GitHelper.GetCurrentBranch(r.Path)),
    }, jsonOpts);
}));

// PUT /api/pr?repo=X — the agent writes a fresh draft
app.MapPut("/api/pr", async (string? repo, string? @base, string? head, HttpRequest req) =>
{
    var r = registry.Resolve(repo);
    if (r is null) return Results.Problem("No repository registered");

    PrDraftRequest? body;
    try { body = await req.ReadFromJsonAsync<PrDraftRequest>(jsonOpts); }
    catch { return Results.BadRequest("Invalid JSON"); }
    if (body is null || string.IsNullOrWhiteSpace(body.Title))
        return Results.BadRequest("title required");

    var draft = new PrDraft(
        Title: body.Title.Trim(),
        Body: body.Body ?? "",
        Base: @base ?? r.DefaultBase,
        Head: head ?? "HEAD",
        Branch: GitHelper.GetCurrentBranch(r.Path),
        Generated: DateTime.UtcNow.ToString("o"),
        GeneratedBy: "copilot");
    PrDraftStore.Save(r.Path, draft);
    return Results.Json(draft, jsonOpts);
});

// DELETE /api/pr?repo=X
app.MapDelete("/api/pr", (string? repo) => WithRepo(repo, r =>
    PrDraftStore.Delete(r.Path) ? Results.Ok() : Results.NotFound()));

// ── Agent handshake ──────────────────────────────────────────────────────────
// Lets the browser wake the Copilot session that launched revue: the UI queues a
// request, the session's long-poll on /api/agent/wait claims it. Requests live
// only as long as this process, which is the lifetime they're meaningful for.

// POST /api/agent/requests?repo=X — "these comments are ready to address", or
// "redraft the PR description"
app.MapPost("/api/agent/requests", async (string? repo, string? @base, string? head, HttpRequest req) =>
{
    var r = registry.Resolve(repo);
    if (r is null) return Results.Problem("No repository registered");

    AgentRequestBody? body;
    try { body = await req.ReadFromJsonAsync<AgentRequestBody>(jsonOpts); }
    catch { return Results.BadRequest("Invalid JSON"); }

    var branch = GitHelper.GetCurrentBranch(r.Path);
    var kind = body?.Kind == "pr-draft" ? "pr-draft" : "comments";

    var ids = body?.CommentIds ?? [];
    // A redraft is about the description, not the comment threads: handing it
    // the unresolved set would look identical to "the user hit Send" and get
    // the code edited without anyone asking.
    if (kind == "comments" && ids.Count == 0)
    {
        // No explicit selection: everything unresolved that applies to this branch.
        ids = CommentsStore.Load(r.Path)
            .Where(c => !c.Resolved)
            .Where(c => branch is null || c.Branch is null || c.Branch == branch)
            .Select(c => c.Id)
            .ToList();
        if (ids.Count == 0) return Results.BadRequest("No comments to address");
    }

    return Results.Json(agentQueue.Enqueue(
        r.Path, branch, body?.Note, ids, kind,
        @base ?? r.DefaultBase, head ?? "HEAD"), jsonOpts);
});

// GET /api/agent/wait?repo=X&timeout=N — long-poll claimed by an attached session
app.MapGet("/api/agent/wait", async (string? repo, string? @base, string? head, int? timeout, HttpContext ctx) =>
{
    var r = registry.Resolve(repo);
    if (r is null) return Results.Problem("No repository registered");

    var seconds = Math.Clamp(timeout ?? 3600, 1, 3600);
    AgentWaitResult result;
    try { result = await agentQueue.WaitAsync(r.Path, TimeSpan.FromSeconds(seconds), ctx.RequestAborted); }
    catch (OperationCanceledException) { return Results.Empty; }

    if (result.Restarting)
        return Results.Json(new { restarting = true, port, version, repo = r.Path }, jsonOpts);

    var request = result.Request;
    if (request is null)
        return Results.Json(new { request = (AgentRequest?)null, timedOut = true, repo = r.Path }, jsonOpts);

    // Hydrate the comments so the session gets file/line/lineContent/body/replies
    // in one shot rather than re-reading comments.json itself.
    var byId = CommentsStore.Load(r.Path).ToDictionary(c => c.Id);
    var comments = request.CommentIds
        .Where(byId.ContainsKey)
        .Select(id => byId[id])
        .ToList();

    // Both kinds end with a redraft, so both get the material for one: the
    // current draft and the commits it should describe. The range comes from
    // the request (what the user was reviewing when they queued it), not from
    // the waiter's own query string -- a waiter armed at launch knows nothing
    // about a range the user picked later.
    var b = request.Base ?? @base ?? r.DefaultBase;
    var h = request.Head ?? head ?? "HEAD";
    PrDraft? prDraft = null;
    List<CommitInfo> commits = [];
    try { prDraft = PrDraftStore.Load(r.Path); } catch { /* best-effort */ }
    try { if (!string.Equals(b, h, StringComparison.Ordinal)) commits = GitHelper.GetCommits(b, h, r.Path); }
    catch { /* best-effort */ }

    return Results.Json(new
    {
        request,
        comments,
        prDraft,
        commits,
        @base = b,
        head = h,
        repo = r.Path,
        timedOut = false,
    }, jsonOpts);
});

// POST /api/agent/requests/{id}/complete — session reports a round finished
app.MapPost("/api/agent/requests/{id}/complete", async (string id, HttpRequest req) =>
{
    AgentCompleteBody? body = null;
    try { if (req.ContentLength > 0) body = await req.ReadFromJsonAsync<AgentCompleteBody>(jsonOpts); }
    catch { return Results.BadRequest("Invalid JSON"); }

    var done = agentQueue.Complete(id, body?.Summary);
    return done is null ? Results.NotFound() : Results.Json(done, jsonOpts);
});

// DELETE /api/agent/requests/{id} — UI escape hatch for a round whose session died
app.MapDelete("/api/agent/requests/{id}", (string id) =>
    agentQueue.Cancel(id) ? Results.Ok() : Results.NotFound());

// GET /api/agent/status?repo=X — polled by the UI's agent panel
app.MapGet("/api/agent/status", (string? repo) => WithRepo(repo, r =>
    Results.Json(agentQueue.Status(r.Path), jsonOpts)));

// ── Restart ──────────────────────────────────────────────────────────────────
// Switching to a newly installed revue without anyone hunting down the process.
// Both routes go through BeginShutdown so attached sessions are always told to
// reconnect before the socket closes.

var shutdownStarted = 0;
var restartStarted = 0;
void BeginShutdown(string reason)
{
    if (Interlocked.Exchange(ref shutdownStarted, 1) != 0) return;
    Console.WriteLine($"shutting down: {reason}");
    agentQueue.SignalShutdown();
    _ = Task.Run(async () =>
    {
        // Give the restart signal and this request's own response time to flush.
        await Task.Delay(500);
        app.Lifetime.StopApplication();
    });
}

// POST /api/shutdown — quit so a newer instance can take this port
app.MapPost("/api/shutdown", () =>
{
    BeginShutdown("replaced by a newer instance");
    return Results.Json(new { stopping = true, port, version }, jsonOpts);
});

// POST /api/restart — start the newer installed revue as our replacement, then quit
app.MapPost("/api/restart", async () =>
{
    // Claim the restart before doing any work: a second tab (or a second click)
    // would otherwise spawn a second replacement, and both would fight over the
    // port with one of them drifting off to become an orphan server.
    if (Interlocked.Exchange(ref restartStarted, 1) != 0)
        return Results.Json(new { restarting = true, version = updateReady?.Version, port }, jsonOpts);

    var target = updateReady;
    if (target is null)
    {
        Interlocked.Exchange(ref restartStarted, 0);
        return Results.Problem("No newer version of revue is installed");
    }

    var replacement = target.ExePath;
    if (replacement is null)
    {
        if (target.BootstrapScript is null)
        {
            Interlocked.Exchange(ref restartStarted, 0);
            return Results.Problem($"revue {target.Version} is pinned but its download script is missing");
        }
        try { replacement = await RunBootstrapAsync(target.BootstrapScript); }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref restartStarted, 0);
            return Results.Problem($"Failed to download revue {target.Version}: {ex.Message}");
        }
        if (replacement is null || !File.Exists(replacement))
        {
            Interlocked.Exchange(ref restartStarted, 0);
            return Results.Problem($"Failed to download revue {target.Version}");
        }
    }

    var arguments = new List<string> { "--takeover", Environment.ProcessId.ToString(), "--port", port.ToString() };
    arguments.AddRange(registry.List().Select(r => r.Path));

    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo(replacement) { UseShellExecute = false };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        System.Diagnostics.Process.Start(psi);
    }
    catch (Exception ex)
    {
        Interlocked.Exchange(ref restartStarted, 0);
        return Results.Problem($"Failed to start revue {target.Version}: {ex.Message}");
    }

    BeginShutdown($"restarting into {target.Version}");
    return Results.Json(new { restarting = true, version = target.Version, port }, jsonOpts);
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
foreach (var root in registry.List()) Console.WriteLine($"repo   →  {root.Path}");

// A replacement inherits open tabs that reload themselves, so a new window would
// just be noise.
if (takeoverPid is null)
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(800);
        OpenBrowser(url);
    });
}

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

// Only delete the file if it's still ours: during a restart the replacement
// reuses our port, so matching on the port alone would delete its registration.
static void DeleteInstanceFile(string path, int port)
{
    try
    {
        if (!File.Exists(path)) return;
        var instance = TryReadInstance(path);
        if (instance?.Port == port && instance?.Pid == Environment.ProcessId) File.Delete(path);
    }
    catch { /* best-effort */ }
}

static (int Port, int Pid)? TryReadInstance(string path)
{
    try
    {
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("port", out var p) || !p.TryGetInt32(out var port)) return null;
        var pid = doc.RootElement.TryGetProperty("pid", out var pidEl) && pidEl.TryGetInt32(out var parsed) ? parsed : 0;
        return (port, pid);
    }
    catch { /* unreadable / malformed → treat as no instance */ }
    return null;
}

// Blocks until the process is gone, so a replacement never probes a port its
// predecessor is still tearing down.
static void WaitForProcessExit(int pid, TimeSpan timeout)
{
    try
    {
        using var proc = System.Diagnostics.Process.GetProcessById(pid);
        proc.WaitForExit((int)timeout.TotalMilliseconds);
    }
    catch { /* already gone, or not ours to wait on */ }
}

static bool WaitForPortFree(int port, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (true)
    {
        try
        {
            using var s = new TcpListener(IPAddress.Loopback, port);
            s.Start(); s.Stop();
            return true;
        }
        catch (SocketException)
        {
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(200);
        }
    }
}

// Runs the installed plugin's own bootstrap script so platform detection,
// caching and old-version cleanup keep living in one place. The script prints
// the executable path as its last line.
static async Task<string?> RunBootstrapAsync(string script)
{
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = OperatingSystem.IsWindows() ? "pwsh" : "bash",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    if (OperatingSystem.IsWindows()) { psi.ArgumentList.Add("-File"); }
    psi.ArgumentList.Add(script);

    using var proc = System.Diagnostics.Process.Start(psi)
        ?? throw new InvalidOperationException("could not start the bootstrap script");
    var stdout = await proc.StandardOutput.ReadToEndAsync();
    var stderr = await proc.StandardError.ReadToEndAsync();
    await proc.WaitForExitAsync();

    if (proc.ExitCode != 0)
        throw new InvalidOperationException(stderr.Trim() is { Length: > 0 } e ? e : $"bootstrap exited with {proc.ExitCode}");

    return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Trim())
        .LastOrDefault(l => l.Length > 0);
}

// Probes the recorded instance and confirms the port really belongs to revue,
// so a stale file left by a crash can't make us talk to an unrelated server.
static async Task<(int Port, int Pid, string Version, string BaseUrl)?> ProbeInstanceAsync(string instanceFile)
{
    var instance = TryReadInstance(instanceFile);
    if (instance is null) return null;

    var baseUrl = $"http://127.0.0.1:{instance.Value.Port}";
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    try
    {
        var ping = await http.GetAsync($"{baseUrl}/api/ping");
        if (!ping.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await ping.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("app", out var appEl) || appEl.GetString() != "revue")
            return null;
        var runningVersion = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "0.0.0" : "0.0.0";
        return (instance.Value.Port, instance.Value.Pid, runningVersion, baseUrl);
    }
    catch
    {
        // Connection refused / timeout → stale file, start fresh.
        return null;
    }
}

static async Task<bool> RegisterRepoAsync(string baseUrl, string repoRoot)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        var payload = JsonSerializer.Serialize(new { path = repoRoot });
        var resp = await http.PostAsync($"{baseUrl}/api/repos",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
        return resp.IsSuccessStatusCode;
    }
    catch { return false; }
}

// The repo set the outgoing instance was serving, so a takeover doesn't quietly
// drop repos the user added from other terminals.
static async Task<List<string>> FetchReposAsync(string baseUrl)
{
    var repos = new List<string>();
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"{baseUrl}/api/repos"));
        if (doc.RootElement.TryGetProperty("repos", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
            {
                if (el.TryGetProperty("path", out var p) && p.GetString() is { Length: > 0 } path)
                    repos.Add(path);
            }
        }
    }
    catch { /* best-effort: we still serve the repo we were launched with */ }
    return repos;
}

static async Task RequestShutdownAsync(string baseUrl)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try { await http.PostAsync($"{baseUrl}/api/shutdown", new StringContent("")); }
    catch { /* it may drop the connection as it goes down */ }
}

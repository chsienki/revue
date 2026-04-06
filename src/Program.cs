using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Revue;

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
app.MapGet("/api/config", () => Results.Json(new
{
    defaultBase,
    repoRoot,
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

// GET /api/file-diff?base=X&head=Y&file=F&ignoreWhitespace=true
app.MapGet("/api/file-diff", (string? @base, string? head, string? file, bool? ignoreWhitespace) =>
{
    @base ??= defaultBase;
    head ??= "HEAD";
    if (string.IsNullOrWhiteSpace(file))
        return Results.BadRequest("file parameter required");
    var wsFlag = ignoreWhitespace == true ? "-w" : null;
    try
    {
        string raw;
        if (head == "HEAD")
        {
            var mergeBase = GitHelper.GetMergeBase(@base, "HEAD", repoRoot);
            var args = new List<string> { "diff", "--unified=5" };
            if (wsFlag != null) args.Add(wsFlag);
            args.AddRange([mergeBase, "--", file]);
            raw = GitHelper.RunGit([.. args], repoRoot);
        }
        else
        {
            var args = new List<string> { "diff", "--unified=5" };
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
        Created: DateTime.UtcNow.ToString("o"),
        Resolved: cr.Resolved
    );
    comments.Add(comment);
    CommentsStore.Save(repoRoot, comments);
    return Results.Json(comment, jsonOpts);
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
    var gi = Path.Combine(repoRoot, ".gitignore");
    if (File.Exists(gi))
    {
        var lines = File.ReadAllLines(gi);
        if (lines.Any(l => l.Trim() == entry)) return;
        File.AppendAllText(gi, $"\n{entry}\n");
    }
    else
    {
        File.WriteAllText(gi, $"{entry}\n");
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

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Revue;

// ── Repo root ────────────────────────────────────────────────────────────────
var startPath = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var repoRoot = FindRepoRoot(startPath);
GitHelper.ResolveDefaultBase(repoRoot); // validate git works
EnsureGitignore(repoRoot);
var defaultBase = GitHelper.ResolveDefaultBase(repoRoot);

// ── Static dir ───────────────────────────────────────────────────────────────
var staticDir = FindStaticDir();

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
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(staticDir),
    RequestPath = "/static",
});

// ── Routes ───────────────────────────────────────────────────────────────────

// GET /
app.MapGet("/", () => Results.File(
    Path.Combine(staticDir, "index.html"), "text/html"));

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
        return Results.Json(commits, jsonOpts);
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

// GET /api/diff?base=X&head=Y
app.MapGet("/api/diff", (string? @base, string? head) =>
{
    @base ??= defaultBase;
    head ??= "HEAD";
    try
    {
        var raw = GitHelper.RunGit(
            ["diff", "--unified=5", $"{@base}...{head}"], repoRoot);
        var files = GitHelper.ParseDiff(raw);
        return Results.Json(files, jsonOpts);
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

// GET /api/file-diff?base=X&head=Y&file=F
app.MapGet("/api/file-diff", (string? @base, string? head, string? file) =>
{
    @base ??= defaultBase;
    head ??= "HEAD";
    if (string.IsNullOrWhiteSpace(file))
        return Results.BadRequest("file parameter required");
    try
    {
        var raw = GitHelper.RunGit(
            ["diff", "--unified=5", $"{@base}...{head}", "--", file], repoRoot);
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
        if (Directory.Exists(Path.Combine(p.FullName, ".git")))
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

static string FindStaticDir()
{
    // 1. Relative to assembly (published)
    var asmDir = Path.Combine(AppContext.BaseDirectory, "static");
    if (Directory.Exists(asmDir)) return asmDir;

    // 2. Walk up from cwd to find a static/ sibling of src/
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir != null)
    {
        var candidate = Path.Combine(dir.FullName, "static");
        if (Directory.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }

    // 3. Relative to source file location (dotnet run)
    var srcDir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
    if (srcDir != null)
    {
        // go up from bin/Debug/net9.0 → src → project root
        var up = new DirectoryInfo(srcDir);
        for (int i = 0; i < 5 && up != null; i++, up = up.Parent!)
        {
            var c = Path.Combine(up.FullName, "static");
            if (Directory.Exists(c)) return c;
        }
    }

    throw new InvalidOperationException("Cannot find static/ directory");
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

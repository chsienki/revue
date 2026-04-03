# Copilot Instructions — revue

revue is a local web-based git diff reviewer written in C# (ASP.NET Core minimal API) with a vanilla JS/HTML frontend. It lets developers browse PR diffs in the browser, leave inline comments, and ask Copilot to respond to those comments.

## Tech stack

- **Backend**: C# 12, .NET 9, ASP.NET Core minimal API (no controllers)
- **Frontend**: Single HTML file (`static/index.html`), vanilla JS, [diff2html](https://diff2html.xyz/) via CDN
- **No build step for frontend** — just HTML/JS served as static files
- **Git operations**: All via `System.Diagnostics.Process` calling the `git` CLI — no libgit2 or similar

## Project layout

```
src/
├── Program.cs        # WebApplication setup, all API endpoints, startup helpers
├── GitHelper.cs      # RunGit(), ResolveDefaultBase(), ParseDiff()
├── CommentsStore.cs  # Load/Save for .revue/comments.json
└── Models.cs         # Comment, CommentRequest, DiffFile records
static/
└── index.html        # Entire frontend — layout, styles, JS all in one file
skill/
└── skills/revue/
    ├── SKILL.md              # Copilot skill — triggered by "review my revue comments"
    └── scripts/read_comments.sh
```

## Key design decisions

- **Single static file frontend** — intentional. No npm, no build step. All JS is inline in `index.html`.
- **Minimal API style** — all routes registered with `app.MapGet/MapPost/MapDelete` in `Program.cs`. No controllers.
- **Comments are local-only** — saved to `{repoRoot}/.revue/comments.json`, always gitignored.
- **Port auto-detection** — tries 7878, increments if busy.
- **Static dir resolution** — `FindStaticDir()` in `Program.cs` checks assembly dir first (for published binaries), then walks up from cwd (for `dotnet run`).

## Comment schema (JSON)

```json
{
  "id": "uuid",
  "file": "src/Foo.cs",
  "line": 42,
  "lineContent": "    var x = Foo();",
  "base": "upstream/main",
  "head": "HEAD",
  "side": "right",
  "body": "Why is this needed?",
  "created": "2026-04-02T00:00:00Z",
  "resolved": false
}
```

## API endpoints

All return JSON (camelCase). Errors return `Results.Problem(...)`.

- `GET /api/config` → `{ defaultBase, repoRoot }`
- `GET /api/branches` → `string[]`
- `GET /api/log?base=X&head=Y` → `[{ hash, message }]`
- `GET /api/diff?base=X&head=Y` → `DiffFile[]`
- `GET /api/file-diff?base=X&head=Y&file=F` → `DiffFile`
- `GET /api/comments` → `Comment[]`
- `POST /api/comments` → accepts `CommentRequest`, returns `Comment`
- `DELETE /api/comments/{id}` → 200 or 404

## Frontend conventions

- The frontend fetches all data from `/api/*` endpoints
- `diff2html` is loaded from CDN and used in side-by-side mode
- The left panel is a file list; clicking a file loads its diff via `/api/file-diff`
- Inline comments are overlaid on diff rows by matching line numbers
- Comments are stored by posting to `/api/comments`
- Dark theme throughout (VS Code-like colors)

## How to build and run

```bash
cd src
dotnet build
dotnet run -- /path/to/repo
```

## Common tasks for Copilot

- **Add a new API endpoint**: Add a `app.MapGet(...)` call in `Program.cs`, add any new model to `Models.cs`
- **Change diff rendering**: Edit the JS in `static/index.html` — look for `diff2html` usage
- **Change comment storage format**: Edit `CommentsStore.cs` and update `Models.cs`
- **Add keyboard shortcuts**: Edit the `keydown` handler in `static/index.html`
- **Add a new git operation**: Add a method to `GitHelper.cs` using `RunGit()`

## What NOT to do

- Don't introduce npm, webpack, or any frontend build tooling
- Don't add NuGet packages unless absolutely necessary — keep deps minimal
- Don't use ASP.NET controllers — keep everything in minimal API style
- Don't commit `.revue/` — it's local-only user data

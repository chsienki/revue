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

## Theme system

- CSS variables are defined in `[data-theme="dark"]` and `[data-theme="light"]` selectors on the `<html>` element
- Theme is applied early via `applyTheme()` called inline in `<script>` before DOM ready (prevents flash)
- `applyTheme()` also swaps the highlight.js CDN stylesheet between `github-dark` and `github`
- System preference is detected via `window.matchMedia('(prefers-color-scheme: light)')`
- User override is stored as a cookie (`revue_theme`): values `auto`, `dark`, `light`
- When adding new colors, add them to BOTH `[data-theme="dark"]` and `[data-theme="light"]` blocks
- Avoid hardcoded colors in CSS — use `var(--name)` variables so theming works

## CSS architecture

- All CSS is inline in `static/index.html` within a single `<style>` block
- Font sizes use `rem` units (not `px`) for accessibility — scales with browser default font size
- diff2html styles are overridden with `!important` because the CDN stylesheet has high specificity
- Sticky line numbers use `position: sticky; left: 0` with `border-collapse: separate; border-spacing: 0` (not `collapse` — it breaks sticky positioning)
- The `#diffview` has a `#loading-overlay` sibling to `#diffview-content` — the overlay must NOT be inside the content div because `innerHTML` clears it
- Topbar uses `align-items: baseline` for text alignment across different font sizes; buttons/selects match via `font-family: inherit; line-height: normal`

## Frontend state management

- All UI state lives in a global `state` object
- `state.logBase`/`state.logHead` track branch selector values (used for the git log query)
- `state.base`/`state.head` track the actual diff range (modified by commit selection)
- `state.rangeStart`/`state.rangeEnd` track commit range selection
- Branch selector changes reset both log and diff state plus the range
- User preferences (`ignoreWhitespace`, `diffLayout`, `showResolved`, `theme`) are persisted as cookies via `savePref()`/`loadPref()`

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
- `GET /api/diff?base=X&head=Y&ignoreWhitespace=bool` → `DiffFile[]`
- `GET /api/file-diff?base=X&head=Y&file=F&ignoreWhitespace=bool` → `DiffFile`
- `GET /api/comments` → `Comment[]`
- `POST /api/comments` → accepts `CommentRequest`, returns `Comment`
- `DELETE /api/comments/{id}` → 200 or 404

## Frontend conventions

- The frontend fetches all data from `/api/*` endpoints
- `diff2html` is loaded from CDN and used in side-by-side mode (configurable)
- The left panel is a hierarchical file tree; clicking a file loads its diff via `/api/file-diff`
- Inline comments are overlaid on diff rows by matching line numbers
- Comments are stored by posting to `/api/comments`
- Loading overlay with spinner shown during async diff operations (use double `requestAnimationFrame` to ensure paint before heavy work)

## How to build and run

```bash
cd src
dotnet build
dotnet run -- /path/to/repo
```

## Browser inspection with Chrome DevTools MCP

The user has a Chrome DevTools MCP server configured at `~/.copilot/mcp-config.json` that connects to Edge via `--browserUrl http://127.0.0.1:9222`.

To launch revue with both user and Copilot able to inspect the same browser:

1. **Launch Edge with remote debugging** using a separate profile and app mode (so it doesn't merge into the user's existing Edge, and gets its own taskbar icon):
   ```powershell
   $profileDir = "$env:USERPROFILE\.cache\revue-debug-profile"
   Start-Process "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" -ArgumentList "--remote-debugging-port=9222","--user-data-dir=$profileDir","--app=http://127.0.0.1:7878"
   ```
2. **Start revue** as usual (`dotnet run -- /path/to/repo`)
3. **Connect via MCP** — use `chrome-devtools-navigate_page` to `http://127.0.0.1:7878`

This gives the user a fully normal Edge window (movable, resizable, F12 DevTools) while Copilot can inspect via MCP tools.

**Important notes:**
- Do NOT use `--isolated` — enterprise policy blocks sign-in on fresh profiles
- Must use `--user-data-dir` to prevent merging into the user's existing Edge instance
- The `--remote-debugging-port=9222` flag is only picked up when Edge launches as a new instance (not when merging)
- If the debug port isn't responding, check that no other Edge instance claimed the profile first

## Common tasks for Copilot

- **Add a new API endpoint**: Add a `app.MapGet(...)` call in `Program.cs`, add any new model to `Models.cs`
- **Change diff rendering**: Edit the JS in `static/index.html` — look for `diff2html` usage
- **Change comment storage format**: Edit `CommentsStore.cs` and update `Models.cs`
- **Add keyboard shortcuts**: Edit the `keydown` handler in `static/index.html`
- **Add a new git operation**: Add a method to `GitHelper.cs` using `RunGit()`
- **Add a new theme color**: Add the variable to BOTH `[data-theme="dark"]` and `[data-theme="light"]` in `index.html`
- **Add a new setting**: Add HTML in `#settings-panel`, wire up in `init()` alongside other settings, persist with `savePref()`/`loadPref()`

## What NOT to do

- Don't introduce npm, webpack, or any frontend build tooling
- Don't add NuGet packages unless absolutely necessary — keep deps minimal
- Don't use ASP.NET controllers — keep everything in minimal API style
- Don't commit `.revue/` — it's local-only user data
- Don't use `px` for font sizes — use `rem` for accessibility
- Don't hardcode colors — use CSS variables so both themes work
- Don't put the loading overlay inside `#diffview-content` — it gets cleared by `innerHTML`

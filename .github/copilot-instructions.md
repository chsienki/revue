# Copilot Instructions — revue

revue is a local web-based git diff reviewer written in C# (ASP.NET Core minimal API) with a vanilla JS/HTML frontend. It lets developers browse PR diffs in the browser, leave inline comments, and ask Copilot to respond to those comments.

## Tech stack

- **Backend**: C# 12, .NET 10, ASP.NET Core minimal API (no controllers)
- **Frontend**: Single HTML file (`static/index.html`), vanilla JS, [diff2html](https://diff2html.xyz/) via CDN
- **No build step for frontend** — just HTML/JS served as static files
- **Git operations**: All via `System.Diagnostics.Process` calling the `git` CLI — no libgit2 or similar

## Project layout

```
src/
├── Program.cs        # WebApplication setup, all API endpoints, startup helpers
├── GitHelper.cs      # RunGit(), ResolveDefaultBase(), ParseDiff(), GetUntrackedFiles()
├── CommentsStore.cs  # Load/Save for .revue/comments.json
└── Models.cs         # Comment, Reply, CommentRequest, ReplyRequest, DiffFile records
static/
├── index.html        # Entire frontend — layout, styles, JS all in one file
├── icon.svg          # App icon (emoji 🎭 on purple gradient, used for favicon + manifest)
└── manifest.json     # Web app manifest for standalone app mode
skills/revue/
├── SKILL.md              # Copilot skill — triggered by "review my revue comments"
├── VERSION               # Expected binary version (must match VERSION at repo root)
└── scripts/
    ├── bootstrap.ps1     # Downloads platform-specific binary from GitHub Releases
    ├── bootstrap.sh      # Bash fallback for bootstrap
    └── read_comments.sh
plugin.json               # Copilot CLI plugin manifest (at repo root for marketplace install)
VERSION                   # Release version (source of truth, copied to skills/revue/VERSION)
install.cs                # Local dev build + install as Copilot CLI plugin
```

## Key design decisions

- **Single static file frontend** — intentional. No npm, no build step. All JS is inline in `index.html`.
- **Minimal API style** — all routes registered with `app.MapGet/MapPost/MapDelete` in `Program.cs`. No controllers.
- **Comments are local-only** — saved to `{repoRoot}/.revue/comments.json`, excluded via `.git/info/exclude` (not `.gitignore`).
- **Port auto-detection** — tries 7878, increments if busy.
- **Static dir resolution** — `FindStaticDir()` in `Program.cs` checks assembly dir first (for published binaries), then walks up from cwd (for `dotnet run`).
- **Untracked files** — `git diff` doesn't show untracked files; `GitHelper.GetUntrackedFiles()` + `GetUntrackedFileDiff()` synthesize diff output for them.

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

## diff2html quirks and fixes

- **Scroll gap**: `.d2h-code-side-line` is `display: inline-block` with `overflow: visible`, inflating `scrollWidth`. Fix: `overflow: hidden` on `.d2h-code-side-line` / `.d2h-code-line` — NOT on `.d2h-diff-table` (that breaks sticky positioning).
- **Line number gaps (SxS)**: diff2html renders linenumber cells as `inline-block` which don't fill row height. Fix: `display: table-cell !important` on `.d2h-code-side-linenumber`.
- **Line number gaps (inline)**: Inline linenumber cells have two stacked `.line-num1`/`.line-num2` divs. Fix: `display: flex` on `.d2h-code-linenumber` with `vertical-align: top; padding: 2px 0` to fill the row.
- **Line number hover**: Use `--accent` (solid blue) not `--accent2` (pale blue) for hover background — white text on pale blue is unreadable in light mode.

## Diff layout modes

- **Side-by-side**: Per-file rendering. Files with only additions or only deletions auto-switch to inline (no empty panel). Modified files render side-by-side.
- **Inline**: All files rendered together in one diff2html call using `line-by-line` format.
- When rendering per-file in SxS mode, each file gets its own `Diff2HtmlUI` instance.

## Commit messages as virtual diff files

Commit messages flow through **the same pipeline as real file diffs** -- they are not a parallel UI. The backend synthesises a unified-diff patch per commit (one `+` line per message line) and returns it from `/api/diff` ahead of the real files; the frontend then renders, comment-injects, viewed-toggles, and tree-lists them with the same code that handles any other file.

- **Sentinel path.** `GitHelper.CommitFilePrefix = "revue::commit::"`. A commit message's "file" path is `revue::commit::<full-sha>`. Colons are illegal in Windows paths so this never collides with a real file. Frontend uses a single `isCommitFile(file)` predicate to recognise these.
- **Backend.** `GitHelper.BuildCommitMessageDiffs(base, head, repoRoot)` builds them from `GitHelper.GetCommits(...)` (which parses `git log --format=%H%x1f%s%x1f%an%x1f%aI%x1f%b%x1e` -- ASCII US/RS separators handle multiline bodies unambiguously). `/api/diff` prepends them when `base != head` (working-tree-only views have no commits in the range).
- **DiffFile shape.** `DiffFile` has an optional `Commit: CommitMeta?` field (hash, subject, author, date). The body lives inside the patch itself, so `CommitMeta` deliberately omits it to keep the wire payload small.
- **Frontend rendering.** Commit-sentinel files are tagged with the `.revue-commit` class on their `.d2h-file-wrapper` so CSS can suppress noise that doesn't make sense for a message (the `+` line prefix, the `+N -0` stats, the new-file `CHANGED` tag, the `@@ -0,0 +1,N @@` hunk-info row) and bold the subject line. `decorateCommitHeader(meta, wrapper)` runs after `Diff2HtmlUI.draw()` and replaces the `.d2h-file-name` text with `<icon> <short-sha>  <subject>` plus an author/date span.
- **Expand context.** `attachExpandButtons` is skipped for commit files -- there's no "more context" to fetch from a fixed text.
- **File tree.** `renderFileList()` partitions `state.files` into `commitFiles` (rendered as a flat list under a `Commits (N)` header at the top) and `regularFiles` (rendered as the existing hierarchical dir tree below). Both use `commentCountForFile()` for badges.
- **User toggle.** `state.showCommitMessages` (default true, persisted as the `showCommitMessages` cookie, surfaced as the *Show commit messages* checkbox in the settings panel) controls visibility. When off, `loadFiles()` filters commit-sentinel entries out of `state.files` after fetch; any comments on hidden commits then naturally fall into the orphaned `Other comments` section so they're never silently lost.

## Comment system

- Comments have an `author` field (`"user"` for humans, `"copilot"` for Copilot)
- Comments support threaded `replies` — each reply has `id`, `author`, `body`, `created`
- Comments are side-aware: stored with `side: "left"` (old) or `"right"` (new) and only rendered on the matching panel
- Comments are **branch-aware**: stored with `branch` set to `git rev-parse --abbrev-ref HEAD` at creation time. The UI hides comments from other branches by default (since `.revue/comments.json` is local-only and survives branch switches). A "Show all branches" toggle in the filelist footer / settings panel reveals them with a branch badge. Legacy comments without a `branch` field are always shown.
- The current branch is exposed via `/api/config` (initial load) and `/api/current-branch` (polled every 5s) so live `git checkout` in another terminal updates the filter automatically.
- In SxS mode, inserting a comment row on one side also inserts a spacer row on the opposite side, with a `ResizeObserver` to keep heights in sync
- `renderAllDiffs()` saves/restores `#diffview` scroll position so comment actions don't jump to top
- "Orphaned comments" (on files not in current diff) render as a virtual "Other comments" file at the end of the diff view. The orphaned-section header pretty-prints commit-sentinel files as `Commit <short-sha> (not in current range)`.
- **Commit-message comments** use `file = "revue::commit::<full-sha>"`, `side = "right"`, and `line` indexed into the rendered message (subject = 1, blank separator = 2, body lines = 3+). They are otherwise the same shape as any other comment.

## Viewed (collapse) toggle

The native `<label class="d2h-file-collapse"><input class="d2h-file-collapse-input">Viewed</label>` markup that diff2html embeds in every `.d2h-file-header` is the single "viewed" control for both file diffs and commit messages. We don't add a custom widget:

- CSS forces the native control visible (the CDN-bundled stylesheet starts it `display: none`).
- `wireViewedCheckbox(file, wrapper)` clones-and-replaces the input to drop any handler diff2html attached via `fileContentToggle()`, restores its checked state from `state.viewedFiles`, and on change adds/removes the `.revue-viewed` class on the wrapper + persists to the `viewedFiles` cookie.
- The `.revue-viewed` class hides `.d2h-file-diff`, dims the header, and hides the wrap toggle (which would be a dead action while collapsed).

## Frontend state management

- All UI state lives in a global `state` object
- `state.logBase`/`state.logHead` are the **branch context** (what the topbar selectors show). They survive commit-range overrides so "clear range" / back-navigation restores the underlying branch diff.
- `state.base`/`state.head` are the **resolved diff endpoints** actually passed to `/api/diff`. When no range is active they mirror `logBase`/`logHead`; when a range is active they're derived from it (single commit → `<hash>^..<hash>`; range → `<older>^..<newer>`; working tree → `HEAD..HEAD`).
- `state.rangeStart`/`state.rangeEnd` track commit range selection. Stored normalized to `rangeStart = older`, `rangeEnd = newer` so derivation and URL serialization are deterministic.
- `state.files` is the **complete** ordered list returned by `/api/diff` -- commit-sentinel files (when `state.showCommitMessages` is true) followed by real files. Treat it as the single source of truth; downstream code (`renderFileList`, `renderAllDiffs`, `computeGloballyInjectableIds`, `commentCountForFile`) handles commits and real files uniformly except where cosmetics differ.
- `state.currentBranch` is the live git branch (or `null` when detached HEAD); `state.showAllBranches` toggles the branch filter
- Branch selector changes reset both log and diff state plus the range
- User preferences (`ignoreWhitespace`, `diffLayout`, `showResolved`, `showCommitMessages`, `theme`, `showAllBranches`) are persisted as cookies via `savePref()`/`loadPref()`
- Per-wrapper state (`wrappedFiles`, `viewedFiles`) is a `Set` persisted as a newline-separated cookie. Both file paths and commit-message sentinels (`revue::commit::<sha>`) coexist in `viewedFiles`.

## URL hash state

The `location.hash` carries the **per-review** context so a refresh restores it, browser back/forward steps through review contexts, and copy-pasting the URL is a deeplink. Format:

```
#base=<branch>&head=<branch>&range=<older..newer>&file=<encoded-path>
```

- `base`, `head` are the **branch context** (the topbar selectors), never the resolved diff endpoints. Both are always present.
- `range` is optional; when present it overrides the branch context to scope the diff to a single commit (`<sha>`), a commit range (`<older>..<newer>`, normalized via `state.commits` ordering when written), or working-tree-only changes (`~working~`). Resolved `state.base`/`state.head` are then derived from `range`, leaving the branch context intact for "clear range" / back-navigation.
- `file` is the currently selected file; sentinels (`revue::commit::<sha>`) and real paths both fit. Restored after `loadFiles()` via `jumpToFile()`.

**What's *not* in the hash:** personal display preferences (`theme`, `diffLayout`, `ignoreWhitespace`, `showResolved`, `showAllBranches`, `showCommitMessages`, `windowSize`) and per-user file state (`wrappedFiles`, `viewedFiles`) stay in cookies. A deeplink shouldn't impose the sender's preferences on the recipient.

**History strategy:**
- `pushState` for context switches (base/head selector change, commit-range selection / clear) -- earn a back-button entry.
- `replaceState` for incidental updates (file click, initial post-load URL canonicalization).

**Implementation:** see `parseHashState` / `buildHashFromState` / `applyHashToState` / `writeHashFromState` near the top of `static/index.html`. A `_applyingHash` flag short-circuits `writeHashFromState` while a `hashchange` listener is applying state from the URL, preventing infinite write↔parse loops.

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
  "branch": "feature/foo",
  "body": "Why is this needed?",
  "author": "user",
  "created": "2026-04-02T00:00:00Z",
  "resolved": false,
  "replies": [
    {
      "id": "uuid",
      "author": "copilot",
      "body": "This initializes the Foo subsystem.",
      "created": "2026-04-02T00:01:00Z"
    }
  ]
}
```

For commit-message comments, `file` is the sentinel `revue::commit::<full-sha>`, `side` is always `"right"`, and `line` indexes into the rendered commit message (subject = 1, blank = 2, body lines = 3+).

## API endpoints

All return JSON (camelCase). Errors return `Results.Problem(...)`.

- `GET /api/config` → `{ defaultBase, repoRoot, version, commitHash, latestVersion, updateCommand, currentBranch }`
- `GET /api/current-branch` → `{ currentBranch }` (cheap, polled to detect `git checkout`)
- `GET /api/branches` → `string[]`
- `GET /api/log?base=X&head=Y` → `[{ hash, message }]` (short subject only; for the topbar Commits picker)
- `GET /api/diff?base=X&head=Y&ignoreWhitespace=bool` → `DiffFile[]` (includes untracked files when head=HEAD, **and** a virtual diff per commit message in `base..head` prepended ahead of the real files when `base != head`; each commit entry has its `commit` field populated with `CommitMeta`)
- `GET /api/file-diff?base=X&head=Y&file=F&ignoreWhitespace=bool` → `DiffFile`
- `GET /api/comments` → `Comment[]`
- `POST /api/comments` → accepts `CommentRequest` (include `author`), returns `Comment`
- `POST /api/comments/{id}/replies` → accepts `{ author, body }`, returns `Reply`
- `DELETE /api/comments/{id}` → 200 or 404

## Frontend conventions

- The frontend fetches all data from `/api/*` endpoints
- `diff2html` is loaded from CDN and used in side-by-side mode (configurable)
- The left panel is a hierarchical file tree with collapsible folders; clicking a file scrolls to its diff
- Folder rows have bold text and `--bg2` background to distinguish from files
- Inline comments are overlaid on diff rows by matching line numbers and side
- Comments are stored by posting to `/api/comments`
- Loading overlay with spinner shown during async diff operations (use double `requestAnimationFrame` to ensure paint before heavy work)
- Static files (`/`, `/manifest.json`, `/icon.svg`) each need explicit `app.MapGet()` routes — revue doesn't use `app.UseStaticFiles()`

## How to build and run

```bash
cd src
dotnet build
dotnet run -- /path/to/repo
```

For development with hot reload:
```bash
cd src
DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER=true dotnet watch --no-launch-profile -- /path/to/repo
```

To install as a Copilot plugin (from marketplace):
```bash
copilot plugin marketplace add chsienki/copilot-marketplace
copilot plugin install revue@chsienki
```

To install locally for development:
```bash
dotnet install.cs
```

To update the plugin:
```bash
copilot plugin update revue@chsienki
```

## Releasing a new version

The release process is tag-driven. A GitHub Actions workflow builds platform-specific binaries and creates a GitHub Release.

### Steps to cut a release

1. **Bump the version** in all three places (they must match):
   - `VERSION` (repo root — source of truth)
   - `skills/revue/VERSION` (copied into installed plugin, read by bootstrap)
   - `plugin.json` `"version"` field
2. **Commit** the version bump: `git commit -m "Bump version to X.Y.Z"`
3. **Push and tag**:
   ```bash
   git push && git tag vX.Y.Z && git push origin vX.Y.Z
   ```
4. The `release.yml` workflow triggers on `v*` tags and:
   - Validates all version sources match the tag
   - Builds self-contained binaries for 6 RIDs (win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64)
   - Generates a changelog from commit messages since the previous tag
   - Creates a GitHub Release with binary assets (.zip for Windows, .tar.gz for Linux/Mac)

### How updates reach users

1. User runs `copilot plugin update revue@chsienki` — pulls new skill files (including updated `VERSION`)
2. Next time revue launches, the bootstrap script (`skills/revue/scripts/bootstrap.ps1` or `bootstrap.sh`) detects the version mismatch
3. Bootstrap downloads the matching binary from GitHub Releases to the local cache
4. The revue web UI also checks for updates on startup (hits GitHub Releases API in background) and shows a blue info banner if a newer version exists

### Version infrastructure

- `VERSION` → read by `Revue.csproj` via MSBuild target `SetVersionFromFile` to set `Version` and `InformationalVersion`
- `InformationalVersion` format: `X.Y.Z+<short-git-hash>` (e.g., `0.4.0+bc3fafa`)
- The binary supports `--version` flag which prints the InformationalVersion
- Bootstrap scripts check `--version` output to verify cached binaries match the expected version
- The `/api/config` endpoint exposes `version`, `commitHash`, `latestVersion`, and `updateCommand`

### Marketplace

The plugin is listed in the `chsienki/copilot-marketplace` repo. The marketplace `version` field is informational only — the real version comes from `plugin.json` in this repo. You don't need to update the marketplace for every release, but you can if you want `copilot plugin marketplace browse chsienki` to show accurate numbers.

## Browser inspection with Chrome DevTools MCP

The user has a Chrome DevTools MCP server configured at `~/.copilot/mcp-config.json` that connects to Edge via `--browserUrl http://127.0.0.1:9222`.

To launch revue with both user and Copilot able to inspect the same browser:

1. **Launch Edge with remote debugging** using a separate profile and app mode (so it doesn't merge into the user's existing Edge, and gets its own taskbar icon):
   ```powershell
   $profileDir = "$env:USERPROFILE\.cache\inspect-debug-profile"
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
- **Add a new static file**: Add the file to `static/`, add an explicit `app.MapGet()` route in `Program.cs`
- **Cut a new release**: Bump version in `VERSION`, `skills/revue/VERSION`, and `plugin.json`, commit, push, tag with `vX.Y.Z`, push the tag

## What NOT to do

- Don't introduce npm, webpack, or any frontend build tooling
- Don't add NuGet packages unless absolutely necessary — keep deps minimal
- Don't use ASP.NET controllers — keep everything in minimal API style
- Don't commit `.revue/` — it's local-only user data
- Don't use `px` for font sizes — use `rem` for accessibility
- Don't hardcode colors — use CSS variables so both themes work
- Don't put the loading overlay inside `#diffview-content` — it gets cleared by `innerHTML`
- Don't put `overflow: hidden` on `.d2h-diff-table` — it breaks sticky line numbers
- Don't modify `.gitignore` — use `.git/info/exclude` for local ignores

## Ideas backlog

<!-- Mirrored from copilot-context/ideas/inbox.md. Newest at the bottom. -->

- 2026-05-06: Single instance targeting multiple directories -- selector + directory/repo name
- 2026-06-09: NativeAOT desktop binary that wraps the webapp for fast launch, not tied to the browser (likely requires 'single instance, multi repo' work first)

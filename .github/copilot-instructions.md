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
├── Program.cs        # WebApplication setup, all API endpoints, single-instance hand-off, startup helpers
├── GitHelper.cs      # RunGit(), FindRepoRoot(), EnsureRevueIgnored(), ResolveDefaultBase(), ParseDiff(), GetUntrackedFiles()
├── RepoRegistry.cs   # In-memory set of repos this instance serves; Add/Remove/Resolve/List + unique display names
├── AgentRequests.cs  # AgentRequestQueue — per-repo queue + long-poll waiters behind the "Send to Copilot" button
├── InstalledVersions.cs # Finds newer revue versions on this machine (plugin pin, bootstrap cache, dev bundle)
├── CommentsStore.cs  # Load/Save for .revue/comments.json
└── Models.cs         # Comment, Reply, CommentRequest, ReplyRequest, DiffFile, RepoInfo, AddRepoRequest, AgentRequest records
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
- **Multi-repo, single instance** — one running server reviews several repos; launching `revue <path>` while an instance is already running hands the repo off to it rather than starting a second server. See "Multi-repo & single instance".
- **Comments are local-only** — saved to `{repoRoot}/.revue/comments.json`, excluded via `.git/info/exclude` (not `.gitignore`).
- **Port auto-detection** — tries 7878, increments if busy.
- **Static dir resolution** — `FindStaticDir()` in `Program.cs` checks assembly dir first (for published binaries), then walks up from cwd (for `dotnet run`).
- **Untracked files** — `git diff` doesn't show untracked files; `GitHelper.GetUntrackedFiles()` + `GetUntrackedFileDiff()` synthesize diff output for them.

## Multi-repo & single instance

One revue instance serves a **set** of repos, not just one. `RepoRegistry`
(`RepoRegistry.cs`) holds them; `GitHelper` and `CommentsStore` are stateless
(they take `repoRoot`), so multi-repo just means resolving the target repo per
request.

- **Repo identity is its git-root path.** Every repo-scoped endpoint takes an
  optional `?repo=<path>`; an absent/unknown value falls back to the **primary**
  (first-registered) repo, keeping old single-repo deeplinks and the skill's
  repo-less reply/delete calls working. Comment mutations by id (`/replies`,
  `DELETE`) with no `repo` search every store for the id (`FindByCommentId`).
- **Single-instance hand-off.** A fresh instance writes
  `%LOCALAPPDATA%/revue/instance.json` (OS cache dir elsewhere) with its
  port+pid and deletes it on shutdown -- only when both still match, so a dying
  instance can't delete the file its replacement just wrote for the same port. A
  later `revue <path>` reads that file, probes `GET /api/ping` (verifying
  `app == "revue"` so a stale file can't hijack an unrelated port), `POST
  /api/repos {path}` to register the repo, opens the browser at `#repo=<path>`,
  and exits. A dead/stale file self-heals.
- **Newer wins.** The hand-off compares `/api/ping`'s version with its own. Same
  or older hands off as above (an old terminal can never downgrade a running
  instance). Newer **takes over**: it inherits the repo set via `GET /api/repos`,
  `POST /api/shutdown`s the old instance, waits for that pid to exit and the port
  to free, then binds the same port. That's what makes installing an update
  actually apply -- otherwise the hand-off would keep the stale server alive
  forever. See "Restarting into a new version".
- **Args.** Every positional arg is a repo to serve (`revue <p1> <p2> …`), which
  is how a replacement inherits the set. `--takeover <pid> --port <n>` marks a
  process spawned as a replacement: it skips the hand-off probe entirely, waits
  for that pid, reuses that port, and doesn't open a browser (the tabs that are
  already open reload themselves).
- **Frontend.** `state.repo` (active path) + `state.repos` (the list) drive the
  topbar repo dropdown. `withRepo(path)` auto-appends `repo=<state.repo>` to
  every `/api/*` call except the globals (`config`/`ping`/`repos`). `selectRepo`
  reloads that repo's config, branches, and diff. The poll loop refreshes
  `/api/repos` so a hand-off from another terminal appears live.
- **Removing a repo** (× in the dropdown) calls `DELETE /api/repos?path=`; the
  registry refuses to remove the last one.

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
- **Comment activity applies itself.** The 5s poll re-renders when the comment JSON changes, so a Copilot reply or a comment left in another tab appears on its own. The changes banner is reserved for things that can't be applied safely under the user: files rewritten on disk and branch switches. The one exception is typing — `renderAllDiffs()` re-hydrates an open *new comment* box but not an open edit/reply box, so a refresh arriving while `.revue-comment-edit` / `.revue-reply-input` is on screen sets `state.commentsDirty` and lands when that box closes (`flushLiveComments()`).

## Agent hand-off ("Send to Copilot")

The UI can wake the Copilot session that launched revue, so the user never has to alt-tab
back to the terminal to say "address my comments".

- **`AgentRequestQueue` (`AgentRequests.cs`)** holds requests per repo, `pending → working → done`.
  Deliberately in-memory: a request only means anything while both this server and the CLI
  session that launched it are alive.
- **The handshake.** The browser `POST`s a request; a session claims it by long-polling
  `GET /api/agent/wait` (up to 1h). Because the Copilot CLI notifies its agent when a
  background shell command finishes, a blocking `curl` is an event-driven wake-up that costs
  nothing while idle. The agent replies to the comments, `POST`s `.../complete` with a
  one-line summary, and re-arms.
- **`WaitAsync` honours `HttpContext.RequestAborted`** so an abandoned curl doesn't leave a
  phantom waiter inflating the "Copilot attached" indicator.
- **Requests queue before anyone attaches** — clicking Send with no session running leaves a
  `pending` request that the next launch's waiter claims immediately.
- **Stuck rounds** (session died mid-work) are cleared by the panel's Cancel button
  (`DELETE /api/agent/requests/{id}`) rather than a server-side heartbeat.
- The agent replies but **never resolves** comments; resolving stays the user's call.

## Restarting into a new version

`copilot plugin update` only refreshes skill files -- the binary lands later, when
something runs bootstrap. So a running revue watches for a newer version itself
(`InstalledVersions.cs`, rescanned every 60s) and offers to switch, which means
the user never has to hunt down the process.

- **Three sources, in one ranking.** The plugin's pinned `VERSION`
  (`$COPILOT_HOME/installed-plugins/*/revue/**/VERSION`), the bootstrap cache
  (`<base>/<version>/revue[.exe]`, version = directory name), and the dev-install
  bundle (`<plugin>/skills/revue/revue[.exe]` + adjacent `VERSION`). Highest
  version above the running one wins, preferring a ready binary over a pin of the
  same version. The cache base is found from the running exe's own grandparent
  first, so it works regardless of platform path conventions.
- **`updateReady` vs `latestVersion`.** `updateReady` means "on this machine,
  switchable right here" (`needsDownload` when only the pin exists);
  `latestVersion` means "published on GitHub", which still needs a plugin update.
  Both come from `/api/config` and `/api/update-status`.
- **Applying always takes a click.** Nothing restarts on its own -- pulling the
  server out from under a review in progress is worse than running a version behind.
- **`POST /api/restart`** runs the plugin's own bootstrap script first when the
  version is only pinned (so RID detection, caching and old-version cleanup stay
  in one place), spawns `<newExe> --takeover <pid> --port <n> <repos…>`, then
  shuts down. It never re-execs itself, so it works the same from `dotnet run` as
  from a released binary.
- **`BeginShutdown` is the single exit path** for both `/api/shutdown` and
  `/api/restart`: `AgentRequestQueue.SignalShutdown()` releases every long-poll
  with `{ restarting: true, port }` so attached Copilot sessions reconnect instead
  of concluding revue died, then the app stops after a short flush delay.
- **The frontend reloads itself.** The poll compares the server's reported version
  with `state.version` every tick and reloads on a change -- a restart can be fast
  enough that no request ever fails, so an observed outage is not a reliable
  signal. `watchForRestart()` additionally covers the case where the server does
  stay down for a moment.

## Viewed (collapse) toggle

The native `<label class="d2h-file-collapse"><input class="d2h-file-collapse-input">Viewed</label>` markup that diff2html embeds in every `.d2h-file-header` is the single "viewed" control for both file diffs and commit messages. We don't add a custom widget:

- CSS forces the native control visible (the CDN-bundled stylesheet starts it `display: none`).
- `wireViewedCheckbox(file, wrapper)` clones-and-replaces the input to drop any handler diff2html attached via `fileContentToggle()`, restores its checked state from `state.viewedFiles`, and on change adds/removes the `.revue-viewed` class on the wrapper + persists to the `viewedFiles` cookie.
- The `.revue-viewed` class hides `.d2h-file-diff`, dims the header, and hides the wrap toggle (which would be a dead action while collapsed).

## Frontend state management

- All UI state lives in a global `state` object
- `state.repo` is the **active repo's git-root path**; `state.repos` is the list this instance serves (`{ path, name, defaultBase }`). Both drive the topbar repo dropdown; `state.repo` is threaded into every repo-scoped API call by `withRepo()` and (when >1 repo) into the URL hash.
- `state.logBase`/`state.logHead` are the **branch context** (what the topbar selectors show). They survive commit-range overrides so "clear range" / back-navigation restores the underlying branch diff.
- `state.base`/`state.head` are the **resolved diff endpoints** actually passed to `/api/diff`. When no range is active they mirror `logBase`/`logHead`; when a range is active they're derived from it (single commit → `<hash>^..<hash>`; range → `<older>^..<newer>`; working tree → `HEAD..HEAD`).
- `state.rangeStart`/`state.rangeEnd` track commit range selection. Stored normalized to `rangeStart = older`, `rangeEnd = newer` so derivation and URL serialization are deterministic.
- `state.files` is the **complete** ordered list returned by `/api/diff` -- commit-sentinel files (when `state.showCommitMessages` is true) followed by real files. Treat it as the single source of truth; downstream code (`renderFileList`, `renderAllDiffs`, `computeGloballyInjectableIds`, `commentCountForFile`) handles commits and real files uniformly except where cosmetics differ.
- `state.currentBranch` is the live git branch (or `null` when detached HEAD); `state.showAllBranches` toggles the branch filter
- `state.agent` is the last `/api/agent/status` snapshot (`attached`, `waiters`, `pending`, `queued`, `active`, `last`) driving the "Send to Copilot" panel; `state.commentsDirty` defers a live comment refresh while the user is mid-edit. Neither is persisted — both are session state, not review context.
- `state.version` is the version of the server this page loaded from, and `state.restarting` suppresses polling while a restart is in flight. A poll that reports a different `version` means the frontend is stale, so the page reloads.
- Branch selector changes reset both log and diff state plus the range
- User preferences (`ignoreWhitespace`, `diffLayout`, `showResolved`, `showCommitMessages`, `theme`, `showAllBranches`) are persisted as cookies via `savePref()`/`loadPref()`
- Per-wrapper state (`wrappedFiles`, `viewedFiles`) is a `Set` persisted as a newline-separated cookie. Both file paths and commit-message sentinels (`revue::commit::<sha>`) coexist in `viewedFiles`.

## URL hash state

The `location.hash` carries the **per-review** context so a refresh restores it, browser back/forward steps through review contexts, and copy-pasting the URL is a deeplink. Format:

```
#repo=<encoded-path>&base=<branch>&head=<branch>&range=<older..newer>&file=<encoded-path>
```

- `repo` is the active repo's git-root path, present **only when the instance serves more than one repo** (single-repo deeplinks stay clean and back-compatible). Switching it is a full context switch, so the `hashchange` listener routes a changed `repo` through `selectRepo` (which restores the rest of the context from the same hash).
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

Repo-scoped endpoints take an optional `?repo=<git-root-path>` (default: primary repo).

A middleware rejects any request carrying an `Origin` header that isn't this
server's own (403). CORS alone isn't enough: a cross-origin POST with no custom
headers is a "simple request" the browser delivers before the policy applies, so
without this a page the user visits could shut revue down or make it spawn a
process. Non-browser callers (the hand-off, the skill's curl) send no `Origin`
and are unaffected.

- `GET /api/ping` → `{ app: "revue", version }` (identifies the port as revue for single-instance hand-off)
- `GET /api/repos` → `{ repos: [{ path, name, defaultBase }], primary }`
- `POST /api/repos` → accepts `{ path }`, registers the repo (used by hand-off), returns `RepoInfo`
- `DELETE /api/repos?path=X` → 200, or Problem if unknown / last remaining repo
- `GET /api/config?repo=X` → `{ version, commitHash, latestVersion, updateCommand, updateReady, repos, repo, repoRoot, defaultBase, currentBranch }` (defaultBase/currentBranch/repoRoot are for the resolved repo; `updateReady` is `{ version, needsDownload }` or absent)
- `GET /api/update-status` → `{ version, latestVersion, updateCommand, updateReady }` (polled every tick; the frontend reloads when `version` changes under it)
- `POST /api/shutdown` → `{ stopping: true, port, version }`, then exits — how a newer instance claims the port
- `POST /api/restart` → `{ restarting: true, version, port }`, or Problem when nothing newer is installed / the download fails
- `GET /api/current-branch?repo=X` → `{ currentBranch }` (cheap, polled to detect `git checkout`)
- `GET /api/branches?repo=X` → `string[]`
- `GET /api/log?repo=Z&base=X&head=Y` → `[{ hash, message }]` (short subject only; for the topbar Commits picker)
- `GET /api/diff?repo=Z&base=X&head=Y&ignoreWhitespace=bool` → `DiffFile[]` (includes untracked files when head=HEAD, **and** a virtual diff per commit message in `base..head` prepended ahead of the real files when `base != head`; each commit entry has its `commit` field populated with `CommitMeta`)
- `GET /api/file-diff?repo=Z&base=X&head=Y&file=F&ignoreWhitespace=bool` → `DiffFile`
- `GET /api/comments?repo=X` → `Comment[]`
- `POST /api/comments?repo=X` → accepts `CommentRequest` (include `author`), returns `Comment`
- `POST /api/comments/{id}/replies?repo=X` → accepts `{ author, body }`, returns `Reply` (repo optional: falls back to searching all stores for the id)
- `DELETE /api/comments/{id}?repo=X` → 200 or 404 (repo optional: searches all stores)
- `POST /api/agent/requests?repo=X` → accepts `{ note, commentIds }` (omit `commentIds` for "everything unresolved on this branch"), returns the queued `AgentRequest`
- `GET /api/agent/wait?repo=X&timeout=N` → long-poll (N clamped to 1–3600s): `{ request, comments, repo, timedOut: false }` once claimed, `{ timedOut: true, repo }` on expiry, or `{ restarting: true, port, version, repo }` when the server is switching versions. `comments` is the hydrated `Comment[]` so the caller needn't read `comments.json`.
- `POST /api/agent/requests/{id}/complete` → accepts `{ summary }`, returns the completed `AgentRequest` (no repo param — id is globally unique)
- `DELETE /api/agent/requests/{id}` → 200 or 404 (drops a stuck round)
- `GET /api/agent/status?repo=X` → `AgentStatusInfo` `{ attached, waiters, pending, queued, active, last }`

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

- **Add a new API endpoint**: Add a `app.MapGet(...)` call in `Program.cs`, add any new model to `Models.cs`. If it's repo-scoped, take a `string? repo` param and resolve the target with `WithRepo(repo, r => ...)` (or `registry.Resolve(repo)`) rather than closing over a single repo root — `withRepo()` in `index.html` auto-sends the active repo.
- **Change diff rendering**: Edit the JS in `static/index.html` — look for `diff2html` usage
- **Change comment storage format**: Edit `CommentsStore.cs` and update `Models.cs`
- **Add keyboard shortcuts**: Edit the `keydown` handler in `static/index.html`
- **Add a new git operation**: Add a method to `GitHelper.cs` using `RunGit()`
- **Add a new theme color**: Add the variable to BOTH `[data-theme="dark"]` and `[data-theme="light"]` in `index.html`
- **Add a new setting**: Add HTML in `#settings-panel`, wire up in `init()` alongside other settings, persist with `savePref()`/`loadPref()`
- **Add a new static file**: Add the file to `static/`, add an explicit `app.MapGet()` route in `Program.cs`
- **Change what Copilot receives from the Send button**: `AgentRequestQueue` in `AgentRequests.cs` for queue semantics, the `/api/agent/wait` handler in `Program.cs` for the payload shape, and `skills/revue/SKILL.md` Capability 3 for what the agent does with it — all three have to agree.
- **Change how a new version is found**: `InstalledVersions.cs`. Adding an install layout means adding a source there, not a special case in `Program.cs`.
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
- Don't put comment activity behind the changes banner — comments apply live; the banner is only for on-disk file changes and branch switches
- Don't have Copilot resolve comments it addressed — replying is its job, resolving is the user's
- Don't add a state-changing endpoint that works without a preflight (no custom header, no JSON body) and assume CORS protects it — the origin middleware is what actually does

## Ideas backlog

<!-- Mirrored from copilot-context/ideas/inbox.md. Newest at the bottom. -->

- 2026-05-06: Single instance targeting multiple directories -- selector + directory/repo name
- 2026-06-09: NativeAOT desktop binary that wraps the webapp for fast launch, not tied to the browser (likely requires 'single instance, multi repo' work first)

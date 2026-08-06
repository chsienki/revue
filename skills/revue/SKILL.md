---
name: revue
description: "Launch the revue code review server and respond to inline review comments"
---

# revue — Copilot Skill

## What is revue?

**revue** is a local web-based git diff reviewer. When a developer reviews a pull request locally, they can click on diff lines to leave inline comments. These comments are stored in `.revue/comments.json` at the root of the git repository.

This skill provides two capabilities:

1. **Launch** the revue server so the user can browse diffs and leave comments in the browser
2. **Review** the comments the user left and respond to them as a thoughtful code reviewer

---

## Capability 1: Launch the Revue Server

### Trigger Phrases

- "launch revue"
- "start revue"
- "open revue"
- "run revue"

### What To Do

Start the revue server for the **current repository** so the user can review diffs in the browser.

**IMPORTANT: revue is NOT an npm package, pip package, or system tool. It is a .NET binary that must be bootstrapped via the script bundled with this skill. Do NOT try npx, npm, pip, brew, or any package manager. Follow these steps exactly:**

1. Run the **bootstrap script** bundled with this skill to ensure the revue binary is downloaded and up-to-date. The bootstrap script is in the `scripts/` subdirectory next to this SKILL.md file.
   - **Windows (PowerShell)**:
     ```powershell
     $copilotHome = if ($env:COPILOT_HOME) { $env:COPILOT_HOME } else { "$env:USERPROFILE\.copilot" }
     $bootstrapScript = Get-ChildItem -Path "$copilotHome\installed-plugins" -Filter "bootstrap.ps1" -Recurse | Where-Object { $_.FullName -match 'revue' } | Select-Object -First 1 -ExpandProperty FullName
     $revueExe = & pwsh -File $bootstrapScript
     ```
   - **macOS/Linux (bash)**:
     ```bash
     copilot_home="${COPILOT_HOME:-$HOME/.copilot}"
     bootstrap_script=$(find "$copilot_home/installed-plugins" -path '*/revue/scripts/bootstrap.sh' -type f | head -1)
     revue_exe=$(bash "$bootstrap_script")
     ```
   The script prints the path to the revue executable as its last line of output. Capture this path.

2. Start the server as a **detached background process** using the path returned by the bootstrap script:
   ```
   <path-to-revue-exe> <current-repo-root>
   ```
   - `<current-repo-root>` is the git root of whatever repo the user is currently working in (use `git rev-parse --show-toplevel` to find it).
   - If the user is already in the revue repo itself, omit the trailing argument — it defaults to the current directory.
3. What happens next depends on the version already running:
   - **No instance running** → a new server starts (first free port from **7878**), registers this repo, and opens the browser.
   - **Same or newer version running** → the launch hands this repo off to it (registering it) and opens the browser focused on it, then the launched process **exits immediately**. It does *not* start a second server. One revue instance serves multiple repos, switchable from the topbar repo dropdown (each with an × to stop reviewing it).
   - **This binary is newer** → it takes over instead: it inherits the running instance's repos, asks it to quit, and rebinds the same port. This is how an update applies itself, so a freshly installed revue never sits behind an old running one. Open tabs reload themselves and attached sessions are told to reconnect.
4. **Arm the wait** (see Capability 3). This is what lets the user hit **Send to Copilot** in the browser instead of coming back to the terminal, so do it every time you launch.
5. Tell the user the server is running, they can leave inline comments, and that hitting **🤖 Send N comments to Copilot** in the left panel will bring you back automatically — no need to return to the CLI.

### Important

- **Do NOT use npx, npm, pip, or any package manager to find or run revue.**
- The bootstrap script handles downloading the correct platform-specific binary automatically.
- Start it as a **detached** background process. A fresh instance stays alive until the user stops it, so don't wait for it to exit. If revue was already running at the same or a newer version, the launched process instead hands the repo off and exits right away — that quick exit is expected, not a failure.

### Open against the right base branch

revue shows the diff of `base..head`. With no base given it auto-detects one (`upstream/main` → `origin/main` → `main` → `HEAD~1`), which is frequently **not** what the user wants — changes are often meant to be reviewed against a feature or release branch, not `main`. You (the agent) usually know the intended base (the branch the work targets or was cut from), so point revue at it rather than accepting the default.

The base and head live in the **URL hash**, so open the browser to a deeplink that names them:

```
http://127.0.0.1:7878/#base=<base-branch>&head=HEAD
```

- `<base-branch>` is the ref to compare against — e.g. `release/2.0`, `origin/develop`, `upstream/main`. It must be a ref that exists in the repo (local branch, `remote/branch`, tag, or sha).
- `head` defaults to `HEAD` (the user's current branch / working tree); you can usually omit it.
- The port is `7878` unless it was busy at launch (then the next free port revue picked).
- In a multi-repo instance, also append `&repo=<url-encoded-repo-root>` so the base applies to the repo you mean (see Capability 1's hand-off note).

So the launch flow is: start the server (above), then open the browser to the deeplink with the base you intend — e.g. for work targeting `release/2.0`:

```
http://127.0.0.1:7878/#base=release/2.0&head=HEAD
```

revue also opens a tab on the auto-detected base at startup; opening this deeplink is what scopes the review to the branch you actually want. Only fall back to the default when you genuinely don't know the base.

---

## Capability 2: Review and Respond to Comments

### Trigger Phrases

- "review my comments"
- "address my review comments"
- "respond to my revue comments"
- "what did I comment on?"
- "go through my inline comments"
- "help me with my PR comments"

### What To Do

1. **Determine the current git branch** by running `git rev-parse --abbrev-ref HEAD` in the repo. Comments are tagged with the branch they were created on; you should ignore comments from other branches.
2. Read `.revue/comments.json` from the current repository root
3. For each **unresolved** comment (`"resolved": false`) **whose `branch` field matches the current branch (or is missing — legacy comments)**, show:
   - The **file path** and **line content** (the actual code the comment targets)
   - The **comment body** (what the developer wrote)
4. Respond to each comment as a thoughtful code reviewer would:
   - If it's a question → answer it
   - If it's a concern → address it with suggestions or an explanation
   - If it's a TODO → acknowledge and propose concrete next steps
   - If it's pointing out a bug → explain the issue and suggest a fix
   - **If the comment targets a commit message** (see "Commit-message comments" below) → either propose a rewritten commit message and (with permission) run `git commit --amend` / `git rebase -i` to apply it, or reply via the API explaining how to apply the suggested change
5. After addressing all comments, offer a brief summary

If you notice comments tagged with a different branch, mention them briefly so the user knows they exist (e.g. "I noticed 3 comments from branch `feature-x` — not addressing those since you're on `main`. Switch to that branch if you want me to look at them.") but don't act on them.

### Commit-message comments

Revue lets the user comment directly on the lines of a commit message in the
review set. These comments are stored with `file` set to a sentinel value
`revue::commit::<full-sha>` (the colons are illegal in Windows paths so this
never collides with a real file). The `line` field is the 1-based line number
within the commit message itself: line 1 is the subject, line 2 is the blank
separator, lines 3+ are body lines.

When you see such a comment:

1. Run `git show --no-patch --format=%B <sha>` to retrieve the current commit
   message, and identify the line the user is asking about.
2. Address the feedback (rewording the subject, clarifying a body paragraph,
   adding a missing co-author trailer, etc.).
3. Either:
   - **Apply the change**, with the user's permission, via `git commit --amend`
     (for the most-recent commit) or `git rebase -i <sha>^` + `reword` for older
     commits. Amending rewrites the sha, so immediately repoint the affected
     comments with `POST /api/comments/remap-commits` (see "The `commit` field")
     — otherwise they vanish from the UI — and post a reply noting the new short sha.
   - **Or just propose** the rewritten message in your reply and let the user
     run the rebase themselves.

### Locating Comments in Code (Important!)

**The `line` field in a comment may be stale.** If you or the user have edited the file since the comment was placed, lines may have shifted. Always use `lineContent` as the ground truth for finding what code a comment refers to:

1. Open the file and search for the `lineContent` string — that's the actual line the user commented on.
2. If the content at the stored `line` number doesn't match `lineContent`, search nearby lines (the content likely shifted due to insertions/deletions above it).
3. When making edits to address multiple comments in the same file, be aware that each edit shifts subsequent line numbers. **Read all comments for a file first**, locate them by content, then make edits. Don't re-read the stored `line` after editing — it will be wrong.

### Comment Schema

The comments file is at `.revue/comments.json` (relative to git repo root). Each comment:

```json
{
  "id": "uuid",
  "file": "src/Foo.cs",
  "line": 42,
  "lineContent": "    return null;",
  "base": "upstream/main",
  "head": "HEAD",
  "side": "right",
  "branch": "feature/null-checks",
  "commit": "9f1c2b7d4e5a6b8c0d1e2f3a4b5c6d7e8f901234",
  "body": "Should this ever return null? Seems risky.",
  "author": "user",
  "created": "2024-01-15T10:30:00+00:00",
  "resolved": false,
  "replies": [
    {
      "id": "uuid",
      "author": "copilot",
      "body": "Good catch — this should throw instead.",
      "created": "2024-01-15T10:35:00+00:00"
    }
  ]
}
```

The `branch` field captures the git branch the user was checked out on when they wrote the comment. It may be missing for legacy comments or comments authored in a detached-HEAD state — treat missing as "applies to all branches".

### The `commit` field

`commit` is the full sha of the newest commit in the range the user was reviewing when
they wrote the comment — or `"working"` when the range held no commits, meaning they
were commenting on uncommitted changes. For a comment on a commit message it is that
commit's own sha. It may be missing on legacy comments.

Two things it buys you:

- **It anchors the comment.** `line` drifts as code moves, and even `lineContent` can
  become ambiguous. `git show <commit>:<file>` gives you the file exactly as it was when
  the comment was written, so you can always recover what the user was looking at.
- **It scopes the review.** The UI only shows comments anchored to commits in the range
  on screen (a *Show all commits* toggle lifts that, mirroring *Show resolved*), so
  picking a single commit shows only the feedback written against it.

**If you rewrite history, repoint the comments.** After a rebase, amend, squash or
cherry-pick, the old shas are no longer in the range and every comment anchored to them
disappears from the UI. You are the only one who knows how the commits map, so say so:

```bash
curl -sS -X POST "http://127.0.0.1:7878/api/comments/remap-commits?repo=<url-encoded-repo-root>" \
  -H "Content-Type: application/json" \
  -d '{"<old-sha>":"<new-sha>","<old-sha-2>":"<new-sha-2>"}'
```

Short shas are fine on both sides. This also rewrites the `revue::commit::<sha>` paths of
comments on commit messages, so those follow their commit too. Do it as part of the rebase,
not as a follow-up the user has to ask for.

### Replying to Comments

When responding to comments, **post replies via the API** rather than just printing them in the chat. This lets the user see the replies in the revue UI.

The revue server runs on `http://127.0.0.1:7878` by default. To reply to a comment:

```
POST http://127.0.0.1:7878/api/comments/{comment-id}/replies
Content-Type: application/json

{ "author": "copilot", "body": "Your reply text here" }
```

Use `curl` or equivalent to post replies. Always use `"author": "copilot"` so the UI shows the reply as coming from Copilot (🤖).

Since one instance can serve several repos, the reply endpoint locates the comment by its id across every registered repo, so you don't need to tell it which repo the comment belongs to.

Replies show up in the open revue UI within a few seconds on their own — the user doesn't need to reload anything.

---

## Capability 3: Wait for "Send to Copilot"

The user shouldn't have to alt-tab back to the terminal to say "address my comments".
The revue UI has a **🤖 Send N comments to Copilot** button; clicking it queues a request
that you claim by long-polling the server. Because a background shell command finishing
wakes you up, that click is what brings you back — with your full session context intact.

### Arming the wait

Right after launching revue (Capability 1), start this as an **async background** shell
command and then carry on / end your turn. Do not run it in the foreground and do not
wait on it synchronously.

```bash
curl -sS --max-time 3660 "http://127.0.0.1:7878/api/agent/wait?repo=<url-encoded-repo-root>&timeout=3600"
```

- Use the port revue actually picked (7878 unless it was busy).
- On Windows, invoke it as `curl.exe` — in Windows PowerShell `curl` is an alias for
  `Invoke-WebRequest` and won't understand these flags.
- `repo` is the git root you launched; the queue is per-repo. If you're reviewing several
  repos in one instance, arm one waiter per repo.
- The call blocks for up to an hour and costs nothing while it waits.
- While it's armed, the UI shows a green **Copilot attached** dot, so the user knows the
  button will actually reach someone.

### Handling the wake-up

When the background command finishes, look at what it printed:

| Response | What it means | What to do |
|---|---|---|
| `{"timedOut":true,...}` | Nobody clicked within the hour. | Silently arm a new waiter. Don't narrate it. |
| `{"request":{...},"comments":[...]}` | The user hit Send, or a draft was auto-queued. | Arm a fresh waiter, then address the round (below). Check `request.kind`: `"pr-draft"` means redraft the PR description only (Capability 4), `"comments"` means the usual. |
| `{"restarting":true,"port":N}` | revue is switching to a newly installed version. | Wait for `GET /api/ping` on that port to answer again (poll ~1s apart for up to 30s), then arm a new waiter. Not an exit. |
| Empty output / `connection refused` | Either revue exited, or a restart dropped the socket before it could answer. | Ping `/api/ping` a few times over ~10s. If it answers, arm a new waiter; if not, revue is gone — don't re-arm, and mention it only if the user is waiting on it. |

Note that the first waiter you arm is often claimed within seconds by an automatic
`pr-draft` round rather than by anything the user did — revue queues one when a session
attaches and no draft exists yet. That is normal, and it is exactly why re-arming has to
happen immediately: otherwise the user's first click has nobody listening.

For a real request:

1. **Arm the next waiter before you do anything else.** A waiter is single-use: the
   moment one claims a request, the session is no longer attached. If you leave
   re-arming until the end of the round, every click the user makes while you work
   lands in a queue nobody is listening to, and it only gets picked up whenever you
   happen to re-arm — which looks exactly like the button doing nothing. Arm first,
   then work. Two waiters can be armed at once, so there is no downside.
2. The payload is self-contained — `comments` holds the full comment objects
   (file, line, lineContent, body, replies, branch), so you don't need to read
   `.revue/comments.json`.
3. `request.note` is free-text the user typed alongside the button. Treat it as extra
   instruction for this round (e.g. "just answer the questions, don't change code").
4. Address every comment exactly as in Capability 2: locate code by `lineContent`, read all
   comments for a file before editing it, make the changes, and post a reply per comment via
   `POST /api/comments/{id}/replies` with `"author": "copilot"`.
5. **Do not resolve the comments.** The user resolves them once they've checked the work.
6. Report the round finished:
   ```bash
   curl -sS -X POST "http://127.0.0.1:7878/api/agent/requests/<request-id>/complete" \
     -H "Content-Type: application/json" \
     -d '{"summary":"Fixed the null check and answered 2 questions."}'
   ```
   The summary shows up under the button, so keep it to one line.
7. **Redraft the PR description** (Capability 4). Addressing comments changes what the
   change *is*, so the description has to keep up.
8. Check whether the waiter you armed in step 1 has already fired. If it has, handle that
   round the same way rather than ending your turn on it.

### Don't leave the session unattended

The whole point is that the user never has to come back to the terminal. That only holds
while a waiter is armed, so treat "there is always exactly one waiter armed" as the
invariant:

- Arm one at launch, and a fresh one the instant each wake-up arrives — **before** the work,
  not after.
- A `timedOut` response, a restart signal, or a round you just finished all mean the same
  thing: there is no waiter now. Arm one.
- Never end a turn without a waiter armed while revue is still running. If you're unsure,
  `GET /api/agent/status?repo=…` and check `attached` is true.
- The one case where you *don't* re-arm is revue actually being gone (`/api/ping` stops
  answering).

### Do the round in the foreground

Only the `curl` wait is a background command. Everything the wake-up triggers is
**normal foreground work in the main session**, visible in the CLI as it happens:

- **Do not hand the round to a sub-agent or background agent.** No `task` tool, no
  detached shell doing the edits. The user is watching the terminal to see what you
  are changing on their behalf; work that happens in another context window is
  invisible to them and can't be interrupted or corrected mid-flight.
- **Say what you got before you start.** Open the round with a one-line summary of
  what arrived — how many comments, which files, plus `request.note` if the user typed
  one. That is the user's confirmation the click landed.
- **Use your ordinary tools for the work** (read, edit, run tests), so each step shows
  up in the transcript like any other request.
- **Report per comment as you go**, not just in the final summary — which comment you
  are on, what you concluded, what you changed.

The UI reflects each stage on its own (queued → *Copilot is working…* → the summary), and
your replies appear in the diff without the user reloading anything. That is the *user's*
view; it is not a substitute for showing the work in the terminal.

---

## Capability 4: Keep the PR description current

revue shows a **draft PR description** at the top of the review, above the commits. It
answers one question: *if you opened the PR right now, what would you write?* It is always
yours — revue never synthesises one from commit subjects, because a mechanical summary is
not what you'd actually write and there'd be nothing worth critiquing.

The draft lives in `.revue/pr.md` (body) and `.revue/pr.json` (title + the base/head/branch
it was written against). Write it through the API rather than editing the files, so that
metadata stays right:

```bash
curl -sS -X PUT "http://127.0.0.1:7878/api/pr?base=<base>&head=<head>" \
  -H "Content-Type: application/json" \
  -d '{"title":"Send comments to Copilot from the review UI","body":"...markdown..."}'
```

Take `<base>` and `<head>` from the wait payload's `base` / `head` fields — they're the
range the user was actually reviewing when they queued the round. Stamping a different
range marks the draft stale, and no redraft will clear that badge.

The title must be a single line; embedded newlines are flattened to spaces.

### When to write it

- On a `"kind":"pr-draft"` request — a redraft is the *whole* job. Don't touch code.
- At the end of **every** comment round, after posting your replies.
- When the user comments on the draft itself (see below).

The `/api/agent/wait` payload gives you what you need either way: `prDraft` (the current
draft, or null), `commits` (the range with full messages), and `base`/`head` (the range the
user was reviewing — use these when you `PUT`, not the repo defaults).

### What it should say

Write the description, not a changelog:

- **Lead with why.** What problem does this solve, what was wrong before? A reviewer who
  reads only the first paragraph should understand the point of the branch.
- **Then what changed**, at the level of design decisions and trade-offs — not a file list,
  which the diff already shows.
- **Call out anything that needs attention**: risky bits, follow-ups, deliberate omissions.
- **Reflect the review.** If a comment thread settled a question, the description should
  read as though that decision was always the plan.
- **Follow the user's writing conventions** — the same ones that apply to commit messages
  and code comments. Present tense, no "this PR"/"previously"/"we now" framing, no
  restating the diff, no filler headings for their own sake.
- **Keep it tight.** A few short paragraphs beats a template with empty sections.

### Comments on the draft

Comments whose `file` is `revue::pr::draft` are feedback on the description itself. `line`
indexes into the rendered text: title = 1, blank = 2, body from 3. Rewrite the draft to
take the feedback on board, `PUT` it, then reply to the comment saying what you changed.

### Opening or updating the real PR

Once the user is happy, and **only when they ask**:

```bash
# new PR
gh pr create --title "<title from pr.json>" --body-file .revue/pr.md

# existing PR
gh pr edit --body-file .revue/pr.md
```

Never create or edit a PR just because a draft exists — the draft is a proposal, not consent.

---

## Notes

- Only address **unresolved** comments unless the user asks for resolved ones too
- **Don't resolve comments yourself** — reply, and leave resolving to the user, who resolves
  once they've checked the work.
- **Filter to the current branch**: only consider comments whose `branch` matches the current git branch (`git rev-parse --abbrev-ref HEAD`), or has no `branch` field. Comments from other branches are stale review artifacts left over from when the user was checked out elsewhere.
- **Repoint comments after rewriting history**: a rebase, amend or squash orphans every comment anchored to an old sha. Call `POST /api/comments/remap-commits` with the old→new mapping as part of the rewrite.
- The `.revue/` directory is gitignored — comments are local only
- Comments are tied to a specific `base`/`head` diff range; mention this context when relevant
- When replying, always use `author: "copilot"` so the UI distinguishes user vs Copilot comments

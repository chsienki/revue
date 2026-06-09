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
3. The server automatically finds a free port starting at **7878** and opens the browser.
4. Tell the user the server is running and they can leave inline comments in the browser. Remind them to come back and say "address my revue comments" when they're done.

### Important

- **Do NOT use npx, npm, pip, or any package manager to find or run revue.**
- The bootstrap script handles downloading the correct platform-specific binary automatically.
- The server **must** be started as a detached process (it needs to stay alive).
- Don't wait for the server to exit — it runs until the user stops it.

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
     commits. After amending, the comment hash will become stale -- post a reply
     via the API noting the new short SHA so the user can re-fetch in the UI.
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

### Replying to Comments

When responding to comments, **post replies via the API** rather than just printing them in the chat. This lets the user see the replies in the revue UI.

The revue server runs on `http://127.0.0.1:7878` by default. To reply to a comment:

```
POST http://127.0.0.1:7878/api/comments/{comment-id}/replies
Content-Type: application/json

{ "author": "copilot", "body": "Your reply text here" }
```

Use `curl` or equivalent to post replies. Always use `"author": "copilot"` so the UI shows the reply as coming from Copilot (🤖).

After posting all replies, tell the user to check the revue UI for your responses.

---

## Notes

- Only address **unresolved** comments unless the user asks for resolved ones too
- **Filter to the current branch**: only consider comments whose `branch` matches the current git branch (`git rev-parse --abbrev-ref HEAD`), or has no `branch` field. Comments from other branches are stale review artifacts left over from when the user was checked out elsewhere.
- The `.revue/` directory is gitignored — comments are local only
- Comments are tied to a specific `base`/`head` diff range; mention this context when relevant
- When replying, always use `author: "copilot"` so the UI distinguishes user vs Copilot comments

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

**IMPORTANT: revue is NOT an npm package, pip package, or system tool. It is a .NET binary bundled with this skill. Do NOT try npx, npm, pip, brew, or any package manager. Follow these steps exactly:**

1. Find the `revue` executable bundled with this skill. It is in the same directory as this SKILL.md file, inside the Copilot installed plugins directory. **This is always the first step — do not skip it or try alternatives.**
   - **Windows**: `Get-ChildItem -Path "$env:USERPROFILE\.copilot\installed-plugins" -Filter "revue.exe" -Recurse | Select-Object -First 1 -ExpandProperty FullName`
   - **macOS/Linux**: `find ~/.copilot/installed-plugins -name revue -type f | head -1`
2. Start the server as a **detached background process** so it keeps running while the user continues chatting:
   ```
   <path-to-revue-exe> <current-repo-root>
   ```
   - `<current-repo-root>` is the git root of whatever repo the user is currently working in (use `git rev-parse --show-toplevel` to find it).
   - If the user is already in the revue repo itself, omit the trailing argument — it defaults to the current directory.
3. The server automatically finds a free port starting at **7878** and opens the browser.
4. Tell the user the server is running and they can leave inline comments in the browser. Remind them to come back and say "address my revue comments" when they're done.

### Important

- **Do NOT use npx, npm, pip, or any package manager to find or run revue.**
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

1. Read `.revue/comments.json` from the current repository root
2. For each **unresolved** comment (`"resolved": false`), show:
   - The **file path** and **line number**
   - The **line content** (the actual code on that line)
   - The **comment body** (what the developer wrote)
3. Respond to each comment as a thoughtful code reviewer would:
   - If it's a question → answer it
   - If it's a concern → address it with suggestions or an explanation
   - If it's a TODO → acknowledge and propose concrete next steps
   - If it's pointing out a bug → explain the issue and suggest a fix
4. After addressing all comments, offer a brief summary

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
- The `.revue/` directory is gitignored — comments are local only
- Comments are tied to a specific `base`/`head` diff range; mention this context when relevant
- When replying, always use `author: "copilot"` so the UI distinguishes user vs Copilot comments

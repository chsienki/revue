# revue — Copilot Skill

## What is revue?

**revue** is a local web-based git diff reviewer. When a developer reviews a pull request locally, they can click on diff lines to leave inline comments. These comments are stored in `.revue/comments.json` at the root of the git repository.

## Your Role

When the user asks you to "review my comments", "address my revue comments", "respond to my review comments", or similar phrases, you should:

1. Read `.revue/comments.json` from the current repository root (or use the helper script below)
2. For each **unresolved** comment, show:
   - The **file path** and **line number**
   - The **line content** (the actual code on that line)
   - The **comment body** (what the developer wrote)
3. Respond to each comment as a thoughtful code reviewer would:
   - If it's a question → answer it
   - If it's a concern → address it with suggestions or an explanation
   - If it's a TODO → acknowledge and propose concrete next steps
   - If it's pointing out a bug → explain the issue and suggest a fix
4. After addressing all comments, offer a brief summary

## Reading the Comments File

The comments file is at: `.revue/comments.json` (relative to git repo root)

Use the helper script to get a nicely formatted summary:
```
bash .revue/../skill/skills/revue/scripts/read_comments.sh
```

Or read the file directly and parse the JSON. Each comment object looks like:
```json
{
  "id": "uuid",
  "file": "src/Foo.cs",
  "line": 42,
  "line_content": "    return null;",
  "base": "upstream/main",
  "head": "HEAD",
  "side": "right",
  "body": "Should this ever return null? Seems risky.",
  "created": "2024-01-15T10:30:00+00:00",
  "resolved": false
}
```

## Trigger Phrases

This skill activates when the user says things like:
- "review my comments"
- "address my review comments"
- "respond to my revue comments"
- "what did I comment on?"
- "go through my inline comments"
- "help me with my PR comments"

## Notes

- Only address **unresolved** comments (`"resolved": false`) unless the user asks for resolved ones too
- The `.revue/` directory is gitignored — comments are local only
- Comments are tied to a specific `base`/`head` diff range; mention this context when relevant

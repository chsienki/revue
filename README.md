# revue

A lightweight local web-based git diff reviewer with GitHub Copilot CLI integration.

Open any git repo in a browser-based side-by-side diff viewer, leave inline comments on any line, then ask Copilot to address them.

## Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- `git` on your PATH

## Build

```bash
cd src
dotnet build
```

## Run

```bash
# From inside a git repo:
dotnet run --project /path/to/revue/src

# Or point at a specific repo:
dotnet run --project /path/to/revue/src -- /path/to/your/repo
```

revue will:
- Auto-detect the git repo root (walks up from the given path)
- Start a local server (default port 7878, auto-increments if busy)
- Open your browser automatically
- Print the URL to stdout

## Publish (self-contained binary)

```bash
# Linux
dotnet publish src -c Release -r linux-x64 --self-contained -o dist/linux

# macOS (Apple Silicon)
dotnet publish src -c Release -r osx-arm64 --self-contained -o dist/mac

# Windows
dotnet publish src -c Release -r win-x64 --self-contained -o dist/win
```

Then run the resulting `revue` binary directly — no dotnet install needed.

## Features

### Diff viewer
- Side-by-side or inline diff layout (toggle in Settings)
- Syntax highlighting via highlight.js
- Horizontally sticky line numbers
- Loading overlay with spinner during diff updates

### Theme support
- Light and dark themes (VS Code-inspired colors)
- Defaults to system preference via `prefers-color-scheme`
- Override in Settings → Theme (auto / dark / light)
- Persisted via cookie

### File browser
- Hierarchical collapsible file tree with folder grouping
- Single-child folder chains collapsed (e.g., `src/components` instead of nested)
- File change stats (`+/-` counts)
- Resizable file list panel (drag the right edge)

### Commit range selection
- Click the "Commits ▾" button to see all commits in range
- Click a commit to view just that commit's changes
- Shift-click to select a range of commits (GitHub-style)
- Range indicator badge shows selected SHA(s) with a clear button
- Uses three-dot diff semantics (`base...head`)

### Inline comments
- Click any line number to open a comment box
- Comments appear inline as highlighted threads
- Resolve or delete comments
- Toggle resolved comment visibility with the checkbox
- Comments stored locally in `.revue/comments.json` (gitignored)

### Settings
- **Theme**: Auto (system) / Dark / Light
- **Ignore whitespace**: Strips whitespace-only changes from diffs
- **Diff layout**: Side-by-side or inline

### Accessibility
- All font sizes use `rem` units — scales with browser default font size

## Usage

1. **Select range**: Use the `base` and `head` dropdowns to pick your diff range. Defaults to `upstream/main → HEAD` (falls back to `origin/main` → `main`).

2. **Browse files**: The left panel shows a hierarchical file tree of all changed files. Click a file to load its diff.

3. **Commits**: Click "Commits ▾" to see all commits in range. Click a commit to view its changes; shift-click to select a range.

4. **Leave comments**: Click any line number in the diff to open a comment box. Type your comment and save.

5. **Manage comments**: Comments appear inline as yellow-highlighted threads. Resolve or delete them. Toggle resolved comment visibility with the checkbox.

## Comments format

Comments are stored in `.revue/comments.json` at the repo root — automatically gitignored, never committed. This is the file Copilot reads.

```json
[
  {
    "id": "uuid",
    "file": "src/Compiler/Foo.cs",
    "line": 42,
    "lineContent": "    var result = DoThing();",
    "base": "upstream/main",
    "head": "HEAD",
    "side": "right",
    "body": "Why is this cast needed here?",
    "created": "2026-04-02T...",
    "resolved": false
  }
]
```

## Copilot CLI Integration

Install the revue skill:

```bash
copilot plugin install ./skill
```

Then after leaving comments in revue, open a Copilot CLI session in your repo and say:

```
review my revue comments
```

Copilot will read your unresolved comments and respond to each one — answering questions, addressing concerns, and suggesting fixes inline.

## Project structure

```
revue/
├── src/
│   ├── Revue.csproj        # net9.0 web SDK project
│   ├── Program.cs          # ASP.NET Core minimal API + startup
│   ├── GitHelper.cs        # git subprocess calls + diff parser
│   ├── CommentsStore.cs    # .revue/comments.json read/write
│   └── Models.cs           # Comment, CommentRequest, DiffFile records
├── static/
│   └── index.html          # Single-page diff viewer (vanilla JS, diff2html CDN)
├── skill/
│   ├── plugin.json         # Copilot CLI plugin manifest
│   └── skills/revue/
│       ├── SKILL.md        # Copilot skill definition
│       └── scripts/
│           └── read_comments.sh
└── .github/
    └── copilot-instructions.md
```

## API

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/config` | Default base ref + repo root |
| GET | `/api/branches` | All local + remote branches |
| GET | `/api/log?base=X&head=Y` | Git log between two refs |
| GET | `/api/diff?base=X&head=Y` | Unified diff, all files |
| GET | `/api/file-diff?base=X&head=Y&file=F` | Diff for a single file |
| GET | `/api/comments` | Load all comments |
| POST | `/api/comments` | Add or update a comment |
| DELETE | `/api/comments/{id}` | Delete a comment |

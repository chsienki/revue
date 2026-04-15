#!/usr/bin/env bash
# read_comments.sh — print a human-readable summary of unresolved revue comments

set -euo pipefail

# Find .revue/comments.json — check cwd, then git root
COMMENTS_FILE=""

if [ -f ".revue/comments.json" ]; then
  COMMENTS_FILE=".revue/comments.json"
elif GIT_ROOT=$(git rev-parse --show-toplevel 2>/dev/null); then
  CANDIDATE="$GIT_ROOT/.revue/comments.json"
  if [ -f "$CANDIDATE" ]; then
    COMMENTS_FILE="$CANDIDATE"
  fi
fi

if [ -z "$COMMENTS_FILE" ]; then
  echo "No .revue/comments.json found in current directory or git root."
  echo "Run revue and leave some inline comments first."
  exit 0
fi

# Check if jq is available
if ! command -v jq &>/dev/null; then
  echo "=== revue comments (raw) ==="
  cat "$COMMENTS_FILE"
  exit 0
fi

TOTAL=$(jq 'length' "$COMMENTS_FILE")
UNRESOLVED=$(jq '[.[] | select(.resolved == false)] | length' "$COMMENTS_FILE")

echo "=== revue — Code Review Comments ==="
echo "Total: $TOTAL  |  Unresolved: $UNRESOLVED"
echo ""

# Print unresolved comments
jq -r '
  .[] | select(.resolved == false) |
  "📍 \(.file):\(.line)\n" +
  "   Code: \(.line_content | ltrimstr("    ") | ltrimstr("\t"))\n" +
  "   Comment: \(.body)\n" +
  "   Diff: \(.base) → \(.head)\n" +
  "   ID: \(.id)\n" +
  "---"
' "$COMMENTS_FILE"

if [ "$UNRESOLVED" -eq 0 ]; then
  echo "✅ All comments are resolved!"
fi

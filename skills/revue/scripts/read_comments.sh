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

# Determine current branch so we can filter out comments from other branches.
CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "")
if [ "$CURRENT_BRANCH" = "HEAD" ]; then CURRENT_BRANCH=""; fi

if [ -n "$CURRENT_BRANCH" ]; then
  ON_BRANCH=$(jq --arg b "$CURRENT_BRANCH" '[.[] | select(.resolved == false) | select((.branch // "") == "" or .branch == $b)] | length' "$COMMENTS_FILE")
  OTHER_BRANCH=$(jq --arg b "$CURRENT_BRANCH" '[.[] | select(.resolved == false) | select((.branch // "") != "" and .branch != $b)] | length' "$COMMENTS_FILE")
else
  ON_BRANCH=$UNRESOLVED
  OTHER_BRANCH=0
fi

echo "=== revue — Code Review Comments ==="
echo "Total: $TOTAL  |  Unresolved: $UNRESOLVED  |  On branch '$CURRENT_BRANCH': $ON_BRANCH"
if [ "$OTHER_BRANCH" -gt 0 ]; then
  echo "($OTHER_BRANCH unresolved comment(s) hidden — created on other branches)"
fi
echo ""

# Print unresolved comments for the current branch (or all if detached HEAD)
if [ -n "$CURRENT_BRANCH" ]; then
  jq -r --arg b "$CURRENT_BRANCH" '
    .[] | select(.resolved == false) | select((.branch // "") == "" or .branch == $b) |
    "📍 \(.file):\(.line)\n" +
    "   Code: \(.lineContent | ltrimstr("    ") | ltrimstr("\t"))\n" +
    "   Comment: \(.body)\n" +
    "   Diff: \(.base) → \(.head)\n" +
    "   ID: \(.id)\n" +
    "---"
  ' "$COMMENTS_FILE"
else
  jq -r '
    .[] | select(.resolved == false) |
    "📍 \(.file):\(.line)\n" +
    "   Code: \(.lineContent | ltrimstr("    ") | ltrimstr("\t"))\n" +
    "   Comment: \(.body)\n" +
    "   Diff: \(.base) → \(.head)\n" +
    "   ID: \(.id)\n" +
    "---"
  ' "$COMMENTS_FILE"
fi

if [ "$ON_BRANCH" -eq 0 ]; then
  echo "✅ No unresolved comments on this branch!"
fi

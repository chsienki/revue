#!/usr/bin/env bash
# bootstrap.sh — Download the platform-specific revue binary if needed.
# Prints the path to the revue executable on success.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SKILL_DIR="${1:-$(dirname "$SCRIPT_DIR")}"

# ── Read expected version ─────────────────────────────────────────────────────
VERSION_FILE="$SKILL_DIR/VERSION"
if [ ! -f "$VERSION_FILE" ]; then
    echo "ERROR: VERSION file not found at $VERSION_FILE" >&2
    exit 1
fi

EXPECTED_VERSION="$(cat "$VERSION_FILE" | tr -d '[:space:]')"
if [ -z "$EXPECTED_VERSION" ]; then
    echo "ERROR: VERSION file is empty" >&2
    exit 1
fi

# ── Detect platform ──────────────────────────────────────────────────────────
case "$(uname -s)" in
    Linux*)  OS="linux" ;;
    Darwin*) OS="osx" ;;
    *)       echo "ERROR: Unsupported OS: $(uname -s)" >&2; exit 1 ;;
esac

case "$(uname -m)" in
    x86_64|amd64)  ARCH="x64" ;;
    arm64|aarch64) ARCH="arm64" ;;
    *)             echo "ERROR: Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

RID="$OS-$ARCH"
EXE_NAME="revue"
ARCHIVE_EXT="tar.gz"

# ── Check for bundled binary (local dev install) ─────────────────────────────
BUNDLED_EXE="$SKILL_DIR/$EXE_NAME"
if [ -x "$BUNDLED_EXE" ]; then
    BUNDLED_VERSION="$("$BUNDLED_EXE" --version 2>/dev/null || true)"
    if echo "$BUNDLED_VERSION" | grep -q "^$EXPECTED_VERSION"; then
        echo "$BUNDLED_EXE"
        exit 0
    fi
fi

# ── Cache directory ──────────────────────────────────────────────────────────
CACHE_BASE="${XDG_CACHE_HOME:-$HOME/.cache}/revue"
CACHE_DIR="$CACHE_BASE/$EXPECTED_VERSION"
CACHED_EXE="$CACHE_DIR/$EXE_NAME"

# ── Check if already cached ──────────────────────────────────────────────────
if [ -x "$CACHED_EXE" ]; then
    ACTUAL_VERSION="$("$CACHED_EXE" --version 2>/dev/null || true)"
    if echo "$ACTUAL_VERSION" | grep -q "^$EXPECTED_VERSION"; then
        echo "$CACHED_EXE"
        exit 0
    fi
    echo "Cached binary version mismatch (expected $EXPECTED_VERSION, got $ACTUAL_VERSION). Re-downloading..." >&2
fi

# ── Download from GitHub Releases ────────────────────────────────────────────
OWNER="chsienki"
REPO="revue"
TAG="v$EXPECTED_VERSION"
ASSET_NAME="revue-$RID.$ARCHIVE_EXT"
DOWNLOAD_URL="https://github.com/$OWNER/$REPO/releases/download/$TAG/$ASSET_NAME"

echo "Downloading revue $EXPECTED_VERSION for $RID..." >&2
echo "  $DOWNLOAD_URL" >&2

mkdir -p "$CACHE_DIR"

TEMP_FILE="$(mktemp)"
trap 'rm -f "$TEMP_FILE"' EXIT

if command -v curl &>/dev/null; then
    curl -fSL "$DOWNLOAD_URL" -o "$TEMP_FILE"
elif command -v wget &>/dev/null; then
    wget -q "$DOWNLOAD_URL" -O "$TEMP_FILE"
else
    echo "ERROR: Neither curl nor wget found" >&2
    exit 1
fi

tar -xzf "$TEMP_FILE" -C "$CACHE_DIR"
chmod +x "$CACHED_EXE"

if [ ! -x "$CACHED_EXE" ]; then
    echo "ERROR: Download succeeded but $EXE_NAME not found in extracted archive" >&2
    exit 1
fi

echo "revue $EXPECTED_VERSION installed to $CACHE_DIR" >&2

# ── Clean up old versions ────────────────────────────────────────────────────
for dir in "$CACHE_BASE"/*/; do
    dir_name="$(basename "$dir")"
    if [ "$dir_name" != "$EXPECTED_VERSION" ]; then
        echo "Removing old version: $dir_name" >&2
        rm -rf "$dir"
    fi
done

echo "$CACHED_EXE"

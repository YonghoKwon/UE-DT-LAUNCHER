#!/bin/sh
# Shared helper: locate the UeDtLauncher CLI binary for the tool scripts.
# Resolution order: $UE_DT_LAUNCHER_BIN -> repo publish output -> PATH.

find_launcher_bin() {
    if [ -n "${UE_DT_LAUNCHER_BIN:-}" ] && [ -x "$UE_DT_LAUNCHER_BIN" ]; then
        printf '%s' "$UE_DT_LAUNCHER_BIN"
        return 0
    fi

    script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
    repo_bin="$script_dir/../publish/linux-x64/UeDtLauncher"
    if [ -x "$repo_bin" ]; then
        printf '%s' "$repo_bin"
        return 0
    fi

    if command -v UeDtLauncher >/dev/null 2>&1; then
        command -v UeDtLauncher
        return 0
    fi

    echo "ERROR: UeDtLauncher binary not found." >&2
    echo "Build it with ./scripts/publish-linux-x64.sh or set UE_DT_LAUNCHER_BIN=/path/to/UeDtLauncher" >&2
    return 1
}

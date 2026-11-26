#!/bin/bash
set -euo pipefail
export LC_ALL=C
cd /dt/store
phase=$1

usage() {
    local sample
    # The publisher may rename a directory while du is traversing it.
    sample=$(du -sb -- *) || return
    printf '%s\n' "$sample" | awk -v phase="$phase" '{ print phase, $2, $1 }'
}

if [[ "$phase" == approving ]]; then
    /dt/tools/entrypoint.sh admin approve "$2" >/dev/null &
    pid=$!
    while kill -0 "$pid" 2>/dev/null; do
        if sample=$(usage 2>/dev/null); then
            printf '%s\n\n' "$sample"
        fi
        sleep 0.05
    done
    wait "$pid"
else
    usage
fi

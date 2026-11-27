#!/usr/bin/env bash
# Read the storage measurements and thresholds shared by both server installers.
set -euo pipefail
[[ $# == 1 ]] || exit 2
LC_ALL=C df -Pk -- "$1" | awk '
    NR == 2 && $3 ~ /^[0-9]+$/ && $4 ~ /^[0-9]+$/ && $5 ~ /^[0-9]+%$/ {
        sub(/%/, "", $5)
        print $3, $4, $5, 70, 85
        found = 1
    }
    END { if (!found) exit 1 }
'

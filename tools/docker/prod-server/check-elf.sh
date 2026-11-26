#!/bin/bash
set -euo pipefail
export LC_ALL=C
server=/dt/publish/server-linux-x64
export DOTNET_BUNDLE_EXTRACT_BASE_DIR=$(mktemp -d)
echo 'Single-file apphost and extracted native files:'
"$server/UeDtLauncher.DistributionServer" --help

check() {
    local path file requirements version maximum=0 count=0 sqlite
    sqlite=$(find "$@" -iname '*sqlite*' -print)
    if [[ -n "$sqlite" ]]; then
        printf 'Unexpected SQLite files:\n%s\n' "$sqlite" >&2
        return 1
    fi
    for path in "$@"; do
        while IFS= read -r -d '' file; do
            [[ $(od -An -tx1 -N4 "$file") == ' 7f 45 4c 46' ]] || continue
            ((count+=1))
            sha256sum "$file"
            requirements=$(readelf --wide --version-info "$file")
            printf '%s\n' "$requirements"
            version=$(awk '/Version needs section/{ needs=1 } needs' <<<"$requirements" |
                grep -oE 'GLIBC_[0-9]+(\.[0-9]+)+' | cut -d_ -f2 | sort -V | tail -1) || true
            maximum=$(printf '%s\n%s\n' "$maximum" "${version:-0}" | sort -V | tail -1)
        done < <(find "$path" -type f -print0 | sort -z)
    done
    printf 'SUMMARY ELF=%s sqlite=0 maxGLIBC=%s\n' "$count" "$maximum"
    ((count > 0)) && [[ $(printf '2.28\n%s\n' "$maximum" | sort -V | tail -1) == '2.28' ]]
}

check "$server" "$DOTNET_BUNDLE_EXTRACT_BASE_DIR"
echo 'Multi-file publish:'
check /mnt/server-multi

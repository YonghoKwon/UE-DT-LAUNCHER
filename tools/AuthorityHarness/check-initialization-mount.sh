#!/usr/bin/env bash
# Run inside a disposable Linux container with a published server and private fixture mounts.
set -euo pipefail
server=${1:?published DistributionServer executable}
child=${2:?authority or .authority-init.v1}
store=/dt/store
case "$child" in authority|.authority-init.v1) ;; *) exit 2 ;; esac
target="$store/$child"
store_mount=$(findmnt -n -o ID --mountpoint "$store")
target_mount=$(findmnt -n -o ID --mountpoint "$target")
[[ -n "$store_mount" && -n "$target_mount" && "$store_mount" != "$target_mount" ]]
[[ -z $(ls -A "$target") && $(ls -A "$store") == "$child" ]]
findmnt -n -o ID,TARGET,FSTYPE,OPTIONS --mountpoint "$store"
findmnt -n -o ID,TARGET,FSTYPE,OPTIONS --mountpoint "$target"
stat -c 'device=%d %n' "$store" "$target"

work=$(mktemp -d)
trap 'rm -f -- "$work/server.json" "$work/stderr"; rmdir -- "$work"' EXIT
printf '{"root":"%s"}\n' "$store" > "$work/server.json"
inventory() {
    (cd "$store" && find . -printf '%y %m %u:%g %T@ %P\n' | LC_ALL=C sort &&
        find . -type f -print0 | LC_ALL=C sort -z | xargs -0 -r sha256sum)
}
before=$(inventory)
exit_code=0
"$server" list --config "$work/server.json" > "$work/stderr" 2>&1 || exit_code=$?
[[ $exit_code == 1 ]]
grep -F 'ERROR [authority-mount-mismatch]' "$work/stderr"
[[ $(inventory) == "$before" ]]
printf '%s exit=%s; directory inventory, permissions, timestamps and file hashes unchanged\n' "$child" "$exit_code"

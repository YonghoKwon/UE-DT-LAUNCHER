#!/usr/bin/env bash
set -euo pipefail

case "${1:?mode required}" in
    ext4) exec sleep infinity ;;
    xfs|cleanup) ;;
    *) exit 2 ;;
esac

disk="/disk/${UEDT_RUN:?run identifier required}.xfs.img"
mountpoint=/xfs
mkdir -p "$mountpoint"

owned_loop() {
    local devices
    devices=$(losetup --associated "$disk" --noheadings --output NAME)
    if [[ "$devices" == *$'\n'* ]]; then
        echo "Multiple associations for the test disk: $devices" >&2
        return 1
    fi
    printf '%s\n' "$devices"
}

cleanup() {
    local device
    device=$(owned_loop)
    if mountpoint -q "$mountpoint"; then
        [[ -n "$device" && $(findmnt -n -o SOURCE --mountpoint "$mountpoint") == "$device" ]]
        umount "$mountpoint"
    fi
    if [[ -n "$device" ]]; then
        losetup -d "$device"
        [[ -z $(owned_loop) ]]
    fi
    rm -f -- "$disk"
    test ! -e "$disk"
    printf 'Cleaned backing file %s and association %s\n' "$disk" "${device:-none}"
}

if [[ "$1" == cleanup ]]; then
    cleanup
    exit
fi

if [[ ! -f "$disk" ]]; then
    # This named volume belongs to the run. Leave enough disk for mkfs with a sparse file.
    truncate -s 2G "$disk"
    device=$(losetup --find --show "$disk")
    mkfs.xfs "$device"
else
    # After container restart the old mount namespace is gone. Reuse the same disk.
    device=$(owned_loop)
    if [[ -z "$device" ]]; then
        device=$(losetup --find --show "$disk")
    fi
fi
mount -t xfs "$device" "$mountpoint"
findmnt -no TARGET,SOURCE,FSTYPE,OPTIONS --mountpoint "$mountpoint"
exec sleep infinity

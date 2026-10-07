#!/usr/bin/env bash
set -euo pipefail
# Test-only extracted executable. No apt install or system nginx service.
root=$(realpath -m "${1:?new folder under isolated validation workspace}")
case "$root" in /tmp/uedt-data-safety.*/*) ;; *) exit 2 ;; esac
test ! -e "$root"
mkdir -m 700 -p "$root/packages" "$root/native"
cd "$root/packages"
apt-get download nginx-light=1.18.0-6ubuntu14
for package in ./*.deb; do dpkg-deb -x "$package" "$root/native"; done
"$root/native/usr/sbin/nginx" -V

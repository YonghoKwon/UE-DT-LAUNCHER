#!/usr/bin/env bash
set -euo pipefail
# Downloads/extracts Ubuntu packages into a NEW test folder only. No apt install,
# maintainer scripts, global library paths, services or accounts are changed.
root=$(realpath -m "${1:?new private native-library folder}")
case "$root" in /tmp/uedt-data-safety.*/*) ;; *) echo 'Use the isolated validation workspace' >&2; exit 2 ;; esac
test ! -e "$root"
mkdir -m 700 -p "$root/packages" "$root/native"
cd "$root/packages"
# Main-repository versions are retained in Ubuntu Jammy, unlike outdated update URLs.
apt-get download libfontconfig1=2.13.1-4.2ubuntu5 libfreetype6=2.11.1+dfsg-1build1 libpng16-16 libbrotli1 libbz2-1.0
for package in ./*.deb; do dpkg-deb -x "$package" "$root/native"; done
echo "NATIVE_LIB_DIR=$root/native/usr/lib/x86_64-linux-gnu"

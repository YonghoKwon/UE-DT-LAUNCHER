#!/usr/bin/env bash
set -eu
dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
exec python3 -B "$dir/dt-setup.py" "$@"

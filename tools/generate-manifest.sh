#!/bin/sh
# Linux wrapper for the launcher's generate-manifest command.
#
# Usage:
#   ./tools/generate-manifest.sh \
#     --package-dir /srv/ue-dt-updates/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/files \
#     --base-url   https://updates.example.com/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/files \
#     --entry-point Windows/m7at10_dt.exe \
#     --version 1.0.0 \
#     --platform windows-x64 \
#     --app-id ue-dt-simulator \
#     --output /srv/ue-dt-updates/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/manifest.json
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$script_dir/launcher-bin.sh"

launcher=$(find_launcher_bin)
exec "$launcher" generate-manifest "$@"

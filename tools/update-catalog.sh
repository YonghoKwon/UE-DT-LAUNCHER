#!/bin/sh
# Linux wrapper for the launcher's update-catalog command.
#
# Usage:
#   ./tools/update-catalog.sh \
#     --catalog /srv/ue-dt-updates/catalogs/general/catalog.json \
#     --project-id ue-dt-simulator \
#     --display-name "UE-DT Simulator" \
#     --version 1.0.0 \
#     --environment prod \
#     --channel stable \
#     --platform windows-x64 \
#     --manifest-url https://updates.example.com/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/manifest.json \
#     --allowed-profiles general,developer \
#     --set-latest
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$script_dir/launcher-bin.sh"

launcher=$(find_launcher_bin)
exec "$launcher" update-catalog "$@"

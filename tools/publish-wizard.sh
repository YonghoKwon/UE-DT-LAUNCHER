#!/bin/sh
set -eu
# ZIP and external release.json must already be complete in the same incoming job directory.
server=${UE_DT_DISTRIBUTION_BIN:-UeDtLauncher.DistributionServer}
config=${UE_DT_DISTRIBUTION_CONFIG:-/etc/ue-dt-distribution/server.json}
if [ "$#" -ne 1 ] || [ ! -d "$1" ]; then
    echo "Usage: $0 /srv/ue-dt-distribution/incoming/upload-id" >&2
    echo "Upload ZIP + external release.json; ZIP-only input is no longer accepted." >&2
    exit 2
fi
"$server" ingest "$1" --config "$config"
echo "Inspect the returned job ID, then approve explicitly:"
echo "$server inspect <job-id> --config $config"
echo "$server approve <job-id> --config $config"

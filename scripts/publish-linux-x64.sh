#!/usr/bin/env bash
set -euo pipefail

CONFIGURATION="${1:-Release}"
OUTPUT_DIR="${2:-publish/linux-x64}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$REPO_ROOT/src/UeDtLauncher/UeDtLauncher.csproj"
OUTPUT="$REPO_ROOT/$OUTPUT_DIR"

echo "Publishing UE-DT-LAUNCHER for Linux x64..."
echo "Project: $PROJECT"
echo "Output : $OUTPUT"

dotnet publish "$PROJECT" \
  -c "$CONFIGURATION" \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o "$OUTPUT"

chmod +x "$OUTPUT/UeDtLauncher"

echo ""
echo "Done. Run:"
echo "  $OUTPUT/UeDtLauncher"

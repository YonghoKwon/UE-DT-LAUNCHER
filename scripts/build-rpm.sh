#!/usr/bin/env bash
set -euo pipefail
VERSION="${1:-1.0.0}"
[[ "$VERSION" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || { echo 'Invalid numeric release version' >&2; exit 2; }
[[ "${UE_DT_SKIP_DOTNET_PUBLISH:-0}" == 0 ]] || { echo 'Unverified prepublished payload reuse is not supported' >&2; exit 2; }
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE="${UE_DT_RPM_OUTPUT_ROOT:-$ROOT/artifacts/linux-rpm}"
mkdir -p "$BASE"
OUT=$(mktemp -d "$BASE/run.XXXXXXXX")
PAYLOAD="$OUT/ue-dt-launcher-$VERSION"
mkdir -p "$PAYLOAD" "$OUT/rpmbuild/SOURCES" "$OUT/rpmbuild/SPECS" "$OUT/artifacts"
dotnet publish "$ROOT/src/UeDtLauncher/UeDtLauncher.csproj" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:LauncherVersion="$VERSION" -o "$PAYLOAD/gui"
dotnet publish "$ROOT/src/UeDtLauncher.Agent/UeDtLauncher.Agent.csproj" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:LauncherVersion="$VERSION" -o "$PAYLOAD/agent"
dotnet publish "$ROOT/src/UeDtLauncher/UeDtLauncher.csproj" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:LauncherVersion="$VERSION" -p:LauncherEdition=Developer -o "$PAYLOAD/developer"
cp "$PAYLOAD/gui/UeDtLauncher" "$PAYLOAD/UeDtLauncher"
cp "$PAYLOAD/agent/UeDtLauncher.Agent" "$PAYLOAD/UeDtLauncher.Agent"
cp "$PAYLOAD/developer/UeDtLauncher.Developer" "$PAYLOAD/UeDtLauncher.Developer"
cp "$ROOT/packaging/linux/ue-dt-launcher-agent.service" "$PAYLOAD/"
cp "$ROOT/examples/configs/distribution-agent-linux.config.json" "$PAYLOAD/launcher.config.json"
tar -C "$OUT" -czf "$OUT/rpmbuild/SOURCES/ue-dt-launcher-$VERSION.tar.gz" "ue-dt-launcher-$VERSION"
cp "$ROOT/packaging/linux/ue-dt-launcher.spec" "$OUT/rpmbuild/SPECS/"
rpmbuild --define "_topdir $OUT/rpmbuild" --define "launcher_version $VERSION" -bb "$OUT/rpmbuild/SPECS/ue-dt-launcher.spec"
RPM="$OUT/rpmbuild/RPMS/x86_64/ue-dt-launcher-$VERSION-1.x86_64.rpm"
test -f "$RPM"
# Data-only extraction: no scriptlet, install, account or service changes.
python3 "$ROOT/tools/verify-rpm-payload.py" --rpm "$RPM" --payload "$PAYLOAD" --version "$VERSION" --output "$OUT/artifacts/package-result.json"
cp "$RPM" "$OUT/artifacts/"
DEV_RPM="$OUT/rpmbuild/RPMS/x86_64/ue-dt-launcher-developer-$VERSION-1.x86_64.rpm"
python3 "$ROOT/tools/verify-rpm-payload.py" --rpm "$DEV_RPM" --payload "$PAYLOAD" --version "$VERSION" --developer --output "$OUT/artifacts/developer-package-result.json"
cp "$DEV_RPM" "$OUT/artifacts/"
(cd "$OUT/artifacts" && sha256sum "$(basename "$RPM")" "$(basename "$DEV_RPM")" > SHA256SUMS.txt)
if [ -n "${GITHUB_ENV:-}" ]; then printf 'UE_DT_RPM_PACKAGE_DIR=%s\nUE_DT_RPM_PATH=%s\nUE_DT_DEVELOPER_RPM_PATH=%s\n' "$OUT/artifacts" "$OUT/artifacts/$(basename "$RPM")" "$OUT/artifacts/$(basename "$DEV_RPM")" >> "$GITHUB_ENV"; fi
printf 'RPM_PACKAGE_DIR=%s\nRPM_PATH=%s\n' "$OUT/artifacts" "$OUT/artifacts/$(basename "$RPM")"

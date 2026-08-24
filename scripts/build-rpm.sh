#!/usr/bin/env bash
set -euo pipefail
VERSION="${1:-1.0.0}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/artifacts/linux-rpm"
PAYLOAD="$OUT/ue-dt-launcher-$VERSION"
mkdir -p "$PAYLOAD" "$OUT/rpmbuild/SOURCES" "$OUT/rpmbuild/SPECS"
if [ "${UE_DT_SKIP_DOTNET_PUBLISH:-0}" != "1" ]; then
    dotnet publish "$ROOT/src/UeDtLauncher/UeDtLauncher.csproj" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:LauncherVersion="$VERSION" -o "$PAYLOAD/gui"
    dotnet publish "$ROOT/src/UeDtLauncher.Agent/UeDtLauncher.Agent.csproj" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:LauncherVersion="$VERSION" -o "$PAYLOAD/agent"
else
    [ -x "$PAYLOAD/gui/UeDtLauncher" ] && [ -x "$PAYLOAD/agent/UeDtLauncher.Agent" ] || {
        echo "Prepublished Linux payload is missing under $PAYLOAD" >&2; exit 1;
    }
fi
cp "$PAYLOAD/gui/UeDtLauncher" "$PAYLOAD/UeDtLauncher"
cp "$PAYLOAD/agent/UeDtLauncher.Agent" "$PAYLOAD/UeDtLauncher.Agent"
cp "$ROOT/packaging/linux/ue-dt-launcher-agent.service" "$PAYLOAD/"
cp "$ROOT/examples/configs/developer-linux-launcher.config.json" "$PAYLOAD/launcher.config.json"
tar -C "$OUT" -czf "$OUT/rpmbuild/SOURCES/ue-dt-launcher-$VERSION.tar.gz" "ue-dt-launcher-$VERSION"
cp "$ROOT/packaging/linux/ue-dt-launcher.spec" "$OUT/rpmbuild/SPECS/"
rpmbuild --define "_topdir $OUT/rpmbuild" --define "launcher_version $VERSION" -bb "$OUT/rpmbuild/SPECS/ue-dt-launcher.spec"
echo "RPM artifacts: $OUT/rpmbuild/RPMS/x86_64"

#!/usr/bin/env bash
set -euo pipefail
# Uses the existing SDK; no account/service installation or privileged system changes.
source_repo=$(realpath "${1:?source repository}")
sdk=${2:-/tmp/ue-dt-dotnet/dotnet}
test -x "$sdk"
work=$(mktemp -d /tmp/uedt-data-safety.XXXXXX)
case "$work" in /tmp/uedt-data-safety.*) ;; *) exit 2 ;; esac
test -d "$source_repo/src/UeDtLauncher.Core"
printf 'VALIDATION_ROOT=%s\n' "$work"
for folder in src tools examples installer packaging scripts .github docs; do
    rsync -rt --exclude=bin --exclude=obj --exclude=logs "$source_repo/$folder/" "$work/$folder/"
done
for file in Directory.Build.props UeDtLauncher.sln README.md AGENTS.md IMPROVEMENTS.md PROJECT_GOALS.md; do cp "$source_repo/$file" "$work/$file"; done
cd "$work"
export PATH="$(dirname "$sdk"):$PATH"
if test -n "${3:-}"; then export LD_LIBRARY_PATH="$(realpath "$3")"; fi
dotnet test src/UeDtLauncher.Tests/UeDtLauncher.Tests.csproj -c Release --logger 'console;verbosity=minimal' | tee tests.log
for project in UeDtLauncher UeDtLauncher.Agent UeDtLauncher.DistributionServer; do
    dotnet publish "src/$project/$project.csproj" -c Release -r linux-x64 --self-contained true \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "publish/$project" | tee "$project-publish.log"
done
dotnet publish tools/RuntimeFaultHarness/RuntimeFaultHarness.csproj -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/harness | tee harness-publish.log
publish/harness/UeDtLauncher.RuntimeFaultHarness runtime-data-smoke "$work/data-process" "$work/publish/UeDtLauncher/UeDtLauncher" | tee data-smoke.log
printf 'PASS: Linux tests, publications and real published runtime-host synthetic data smoke\n'

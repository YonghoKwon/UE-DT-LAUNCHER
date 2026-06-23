#!/bin/sh
# One-step release publish for Linux: copy package files into the server layout,
# generate manifest.json, update the catalog, and optionally sync to a remote server.
#
# Usage:
#   ./tools/publish-release.sh \
#     --package-dir /home/builds/ue-dt-simulator/1.0.0/windows-x64 \
#     --server-root /srv/ue-dt-updates \
#     --base-url-root https://updates.example.com \
#     --project-id ue-dt-simulator \
#     --display-name "UE-DT Simulator" \
#     --version 1.0.0 \
#     --environment prod \
#     --channel stable \
#     --platform windows-x64 \
#     --entry-point Windows/m7at10_dt.exe \
#     --catalog-profile general \
#     [--allowed-profiles general,developer] [--notes "..."] [--set-latest] \
#     [--clean-files] [--no-copy] [--remote user@host:/srv/ue-dt-updates]
#
# --remote requires rsync (preferred) or scp+ssh on this machine.
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$script_dir/launcher-bin.sh"

PACKAGE_DIR= SERVER_ROOT= BASE_URL_ROOT= PROJECT_ID= DISPLAY_NAME= VERSION=
ENVIRONMENT= CHANNEL= PLATFORM= ENTRY_POINT= CATALOG_PROFILE=
ALLOWED_PROFILES= NOTES= SET_LATEST=0 CLEAN_FILES=0 NO_COPY=0 REMOTE=

while [ $# -gt 0 ]; do
    case "$1" in
        --package-dir)      PACKAGE_DIR=$2; shift 2 ;;
        --server-root)      SERVER_ROOT=$2; shift 2 ;;
        --base-url-root)    BASE_URL_ROOT=$2; shift 2 ;;
        --project-id)       PROJECT_ID=$2; shift 2 ;;
        --display-name)     DISPLAY_NAME=$2; shift 2 ;;
        --version)          VERSION=$2; shift 2 ;;
        --environment)      ENVIRONMENT=$2; shift 2 ;;
        --channel)          CHANNEL=$2; shift 2 ;;
        --platform)         PLATFORM=$2; shift 2 ;;
        --entry-point)      ENTRY_POINT=$2; shift 2 ;;
        --catalog-profile)  CATALOG_PROFILE=$2; shift 2 ;;
        --allowed-profiles) ALLOWED_PROFILES=$2; shift 2 ;;
        --notes)            NOTES=$2; shift 2 ;;
        --set-latest)       SET_LATEST=1; shift ;;
        --clean-files)      CLEAN_FILES=1; shift ;;
        --no-copy)          NO_COPY=1; shift ;;
        --remote)           REMOTE=$2; shift 2 ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

for required in PACKAGE_DIR SERVER_ROOT BASE_URL_ROOT PROJECT_ID DISPLAY_NAME VERSION ENVIRONMENT CHANNEL PLATFORM ENTRY_POINT CATALOG_PROFILE; do
    eval "value=\${$required}"
    if [ -z "$value" ]; then
        echo "ERROR: missing required argument: --$(printf '%s' "$required" | tr '_' '-' | tr '[:upper:]' '[:lower:]')" >&2
        exit 1
    fi
done

case "$ENVIRONMENT" in prod|dev) ;; *) echo "ERROR: --environment must be prod or dev" >&2; exit 1 ;; esac
case "$CHANNEL" in stable|beta|dev) ;; *) echo "ERROR: --channel must be stable, beta, or dev" >&2; exit 1 ;; esac
case "$PLATFORM" in windows-x64|linux-x64) ;; *) echo "ERROR: --platform must be windows-x64 or linux-x64" >&2; exit 1 ;; esac
case "$CATALOG_PROFILE" in general|developer) ;; *) echo "ERROR: --catalog-profile must be general or developer" >&2; exit 1 ;; esac

[ -d "$PACKAGE_DIR" ] || { echo "ERROR: package dir does not exist: $PACKAGE_DIR" >&2; exit 1; }
[ -n "$ALLOWED_PROFILES" ] || ALLOWED_PROFILES=$CATALOG_PROFILE

launcher=$(find_launcher_bin)

BASE_URL_ROOT=${BASE_URL_ROOT%/}
release_relative="projects/$PROJECT_ID/$ENVIRONMENT/$CHANNEL/$VERSION/$PLATFORM"
release_dir="$SERVER_ROOT/$release_relative"
files_dir="$release_dir/files"
manifest_path="$release_dir/manifest.json"
catalog_path="$SERVER_ROOT/catalogs/$CATALOG_PROFILE/catalog.json"
manifest_url="$BASE_URL_ROOT/$release_relative/manifest.json"
files_base_url="$BASE_URL_ROOT/$release_relative/files"

mkdir -p "$release_dir"

if [ "$NO_COPY" -eq 0 ]; then
    if [ "$CLEAN_FILES" -eq 1 ] && [ -d "$files_dir" ]; then
        rm -rf "$files_dir"
    fi
    mkdir -p "$files_dir"
    cp -a "$PACKAGE_DIR/." "$files_dir/"
    echo "Package copied"
    echo "  From: $PACKAGE_DIR"
    echo "  To  : $files_dir"
elif [ ! -d "$files_dir" ]; then
    echo "ERROR: --no-copy was specified, but files directory does not exist: $files_dir" >&2
    exit 1
fi

"$launcher" generate-manifest \
    --package-dir "$files_dir" \
    --output "$manifest_path" \
    --base-url "$files_base_url" \
    --entry-point "$ENTRY_POINT" \
    --version "$VERSION" \
    --channel "$CHANNEL" \
    --platform "$PLATFORM" \
    --app-id "$PROJECT_ID"

set -- \
    --catalog "$catalog_path" \
    --project-id "$PROJECT_ID" \
    --display-name "$DISPLAY_NAME" \
    --version "$VERSION" \
    --environment "$ENVIRONMENT" \
    --channel "$CHANNEL" \
    --platform "$PLATFORM" \
    --manifest-url "$manifest_url" \
    --allowed-profiles "$ALLOWED_PROFILES"
[ -n "$NOTES" ] && set -- "$@" --notes "$NOTES"
[ "$SET_LATEST" -eq 1 ] && set -- "$@" --set-latest

"$launcher" update-catalog "$@"

if [ -n "$REMOTE" ]; then
    remote_host=${REMOTE%%:*}
    remote_root=${REMOTE#*:}
    if [ "$remote_host" = "$REMOTE" ] || [ -z "$remote_root" ]; then
        echo "ERROR: --remote must look like user@host:/srv/ue-dt-updates" >&2
        exit 1
    fi

    echo "Uploading release to $REMOTE ..."
    if command -v rsync >/dev/null 2>&1; then
        ssh "$remote_host" "mkdir -p '$remote_root/$release_relative' '$remote_root/catalogs/$CATALOG_PROFILE'"
        rsync -az --delete "$release_dir/" "$remote_host:$remote_root/$release_relative/"
        rsync -az "$SERVER_ROOT/catalogs/$CATALOG_PROFILE/" "$remote_host:$remote_root/catalogs/$CATALOG_PROFILE/"
    elif command -v scp >/dev/null 2>&1; then
        ssh "$remote_host" "mkdir -p '$remote_root/$release_relative' '$remote_root/catalogs/$CATALOG_PROFILE'"
        scp -r "$release_dir/." "$remote_host:$remote_root/$release_relative/"
        scp -r "$SERVER_ROOT/catalogs/$CATALOG_PROFILE/." "$remote_host:$remote_root/catalogs/$CATALOG_PROFILE/"
    else
        echo "ERROR: --remote requires rsync or scp on this machine." >&2
        exit 1
    fi
    echo "Upload completed."
fi

echo "Release published"
echo "  ServerRoot : $SERVER_ROOT"
echo "  Catalog    : $catalog_path"
echo "  ReleaseDir : $release_dir"
echo "  Manifest   : $manifest_path"
echo "  ManifestUrl: $manifest_url"
echo "  FilesUrl   : $files_base_url"

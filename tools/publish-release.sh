#!/bin/sh
set -eu
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$script_dir/launcher-bin.sh"
launcher=$(find_launcher_bin)

PACKAGE_DIR= SERVER_ROOT= BASE_URL_ROOT= PROJECT_ID= DISPLAY_NAME= VERSION=
ENVIRONMENT=prod CHANNEL=stable PLATFORM= ENTRY_POINT= CATALOG_PROFILE=general
ALLOWED_PROFILES= NOTES= PRIVATE_KEY=${UE_DT_SIGNING_PRIVATE_KEY:-} KEY_ID=${UE_DT_SIGNING_KEY_ID:-} REMOTE=
SET_LATEST=0 DRY_RUN=0 REPLACE=0 ALLOW_UNSIGNED=0
while [ $# -gt 0 ]; do
    case "$1" in
        --package-dir) PACKAGE_DIR=$2; shift 2 ;; --server-root) SERVER_ROOT=$2; shift 2 ;;
        --base-url-root) BASE_URL_ROOT=$2; shift 2 ;; --project-id) PROJECT_ID=$2; shift 2 ;;
        --display-name) DISPLAY_NAME=$2; shift 2 ;; --version) VERSION=$2; shift 2 ;;
        --environment) ENVIRONMENT=$2; shift 2 ;; --channel) CHANNEL=$2; shift 2 ;;
        --platform) PLATFORM=$2; shift 2 ;; --entry-point) ENTRY_POINT=$2; shift 2 ;;
        --catalog-profile) CATALOG_PROFILE=$2; shift 2 ;; --allowed-profiles) ALLOWED_PROFILES=$2; shift 2 ;;
        --notes) NOTES=$2; shift 2 ;; --private-key) PRIVATE_KEY=$2; shift 2 ;; --key-id) KEY_ID=$2; shift 2 ;;
        --set-latest) SET_LATEST=1; shift ;; --dry-run) DRY_RUN=1; shift ;;
        --replace|--clean-files) REPLACE=1; shift ;; --allow-unsigned) ALLOW_UNSIGNED=1; shift ;;
        --no-copy) echo "ERROR: --no-copy is not supported; use update-catalog." >&2; exit 1 ;;
        --remote) REMOTE=$2; shift 2 ;; *) echo "ERROR: unknown argument: $1" >&2; exit 1 ;;
    esac
done
for required in PACKAGE_DIR SERVER_ROOT BASE_URL_ROOT PROJECT_ID VERSION PLATFORM ENTRY_POINT; do
    eval "value=\${$required}"
    [ -n "$value" ] || { echo "ERROR: missing required option for $required" >&2; exit 1; }
done
[ -n "$DISPLAY_NAME" ] || DISPLAY_NAME=$PROJECT_ID
[ -n "$ALLOWED_PROFILES" ] || ALLOWED_PROFILES=$CATALOG_PROFILE
set -- publish-release --package-dir "$PACKAGE_DIR" --server-root "$SERVER_ROOT" --base-url-root "$BASE_URL_ROOT" \
    --project-id "$PROJECT_ID" --display-name "$DISPLAY_NAME" --version "$VERSION" --environment "$ENVIRONMENT" \
    --channel "$CHANNEL" --platform "$PLATFORM" --entry-point "$ENTRY_POINT" --catalog-profile "$CATALOG_PROFILE" \
    --allowed-profiles "$ALLOWED_PROFILES"
[ -n "$NOTES" ] && set -- "$@" --notes "$NOTES"
[ -n "$PRIVATE_KEY" ] && set -- "$@" --private-key "$PRIVATE_KEY"
[ -n "$KEY_ID" ] && set -- "$@" --key-id "$KEY_ID"
[ "$SET_LATEST" -eq 1 ] && set -- "$@" --set-latest
[ "$DRY_RUN" -eq 1 ] && set -- "$@" --dry-run
[ "$REPLACE" -eq 1 ] && set -- "$@" --replace
[ "$ALLOW_UNSIGNED" -eq 1 ] && set -- "$@" --allow-unsigned
"$launcher" "$@"
[ "$DRY_RUN" -eq 1 ] && exit 0
[ -z "$REMOTE" ] && exit 0
remote_host=${REMOTE%%:*}; remote_root=${REMOTE#*:}
[ "$remote_host" != "$REMOTE" ] && [ -n "$remote_root" ] || { echo "ERROR: invalid --remote" >&2; exit 1; }
release_relative="projects/$PROJECT_ID/$ENVIRONMENT/$CHANNEL/$VERSION/$PLATFORM"
release_dir="$SERVER_ROOT/$release_relative"; catalog_dir="$SERVER_ROOT/catalogs/$CATALOG_PROFILE"
ssh "$remote_host" "mkdir -p '$remote_root/$release_relative' '$remote_root/catalogs/$CATALOG_PROFILE'"
if command -v rsync >/dev/null 2>&1; then
    rsync -az --delete "$release_dir/" "$remote_host:$remote_root/$release_relative/"
    rsync -az "$catalog_dir/" "$remote_host:$remote_root/catalogs/$CATALOG_PROFILE/"
else
    scp -r "$release_dir/." "$remote_host:$remote_root/$release_relative/"
    scp -r "$catalog_dir/." "$remote_host:$remote_root/catalogs/$CATALOG_PROFILE/"
fi
echo "Remote release and catalog upload completed."

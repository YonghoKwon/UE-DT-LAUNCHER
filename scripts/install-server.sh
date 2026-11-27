#!/usr/bin/env bash
# Draft for a reviewed RHEL 8.4 deployment. Service activation is separate.
set -euo pipefail
umask 0077
if [[ $# != 6 || $1 != --apply ]]; then
    printf 'Usage: %s --apply SERVER_PUBLISH LAUNCHER_PUBLISH CONFIG_SOURCE KEY_SOURCE NGINX_SOURCE\n' "$0" >&2
    exit 2
fi
die() { printf '%s\n' "$*" >&2; exit 1; }
[[ $EUID == 0 ]] || die 'Run as administrator.'
# shellcheck disable=SC1091
source /etc/os-release
[[ $ID == rhel && $VERSION_ID == 8.4 ]] || die 'Expected RHEL 8.4.'

DT_BASE=/dt
store=$DT_BASE/store
config=$DT_BASE/config
keys=$DT_BASE/keys
server=$DT_BASE/publish/server-linux-x64
launcher=$DT_BASE/publish/linux-x64
tools=$DT_BASE/tools
unit=/etc/systemd/system/ue-dt-distribution.service
site=/etc/nginx/conf.d/ue-dt-distribution.conf
service=uedt-distribution
upload=uedt-upload
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)

check_directory() {
    [[ ! -L "$1" && ( ! -e "$1" || -d "$1" ) ]] || die "Unsafe directory: $1"
    if [[ -d "$1" ]]; then
        local mode
        mode=$(stat -c %a "$1")
        [[ $(stat -c %u "$1") == 0 && $(( 8#$mode & 8#022 )) == 0 ]] ||
            die "Directory must be controlled by root: $1"
    fi
}
for path in "$DT_BASE" "$DT_BASE/publish" "$config" "$keys" "$server" "$launcher" "$tools" /etc/nginx/conf.d /etc/systemd/system; do
    check_directory "$path"
done
[[ -d "$DT_BASE" && ! -L "$store" && ( ! -e "$store" || -d "$store" ) ]] || die "Prepare a canonical store under $DT_BASE"

check_storage() {
    local filesystem source percent warning stop usage
    read -r filesystem source < <(findmnt -no FSTYPE,SOURCE --target "$1")
    printf 'Filesystem %s source %s at %s\n' "$filesystem" "$source" "$1"
    [[ $filesystem == xfs || $filesystem == ext4 ]] || die 'The store requires XFS or ext4.'
    df -Pk -- "$1"
    usage=$(bash "$script_dir/server-disk-usage.sh" "$1") || die 'Could not check disk capacity.'
    read -r _ _ percent warning stop <<< "$usage"
    if (( percent >= stop )); then die 'Storage is at or above the 85% stop threshold.'; fi
    if (( percent >= warning )); then echo 'Storage is at or above the 70% warning threshold.' >&2; fi
}
if [[ -d "$store" ]]; then check_storage "$store"; else check_storage "$DT_BASE"; fi

lock=$DT_BASE/.server-install.lock
[[ ! -L "$lock" && ( ! -e "$lock" || -f "$lock" ) ]] || die "Unsafe lock: $lock"
exec 9>>"$lock"
flock -n 9 || die 'Another server installation is active.'
work=$(mktemp -d "$DT_BASE/.server-install.XXXXXXXX")
declare -a pending_controls=()
temp_file=
finish() {
    local code=$? target pending
    set +e
    if (( code != 0 )); then
        for target in "${pending_controls[@]}"; do
            pending=$target.uedt-install-pending
            if [[ ! -L "$pending" && ! -L "$target" && -f "$pending" && "$pending" -ef "$target" ]]; then
                rm -- "$target" || echo "Rollback requires administrator review: $target" >&2
            fi
            if [[ ! -L "$pending" && -f "$pending" && ! -e "$target" ]]; then rm -- "$pending"; fi
        done
    fi
    if [[ -n $temp_file ]]; then rm -f -- "$temp_file"; fi
    rm -rf -- "$work"
    exit "$code"
}
trap finish EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

selinux=0
command -v selinuxenabled >/dev/null || die 'Install the SELinux context tools.'
if selinuxenabled; then selinux=1; fi
check_context() {
    local context type
    if (( selinux )); then
        context=$(matchpathcon -n "$1") || die "No SELinux fcontext: $1"
        IFS=: read -r _ _ type _ <<< "$context"
        case "$type" in
            ''|default_t|unlabeled_t|file_t|root_t) die "Configure a deployment SELinux fcontext for $1 (got $context)" ;;
        esac
        printf 'SELinux expected context %s for %s\n' "$context" "$1"
    fi
}
label_path() {
    if (( selinux )); then restorecon -- "$1"; fi
}

test -x "$2/UeDtLauncher.DistributionServer"
test -x "$3/UeDtLauncher"
python3 - "$4/server.json" "$4/access-policy.json" "$store" "$config" "$keys" <<'PY'
import json
import sys
from urllib.parse import urlsplit
file, policy, store, config, keys = sys.argv[1:]
with open(file, encoding="utf-8") as source:
    settings = json.load(source)
with open(policy, encoding="utf-8") as source:
    if not isinstance(json.load(source).get("clients"), list):
        sys.exit("Invalid access policy client list.")
expected = dict(root=store, authenticationMode="request-signature-v1", listenUrl="http://127.0.0.1:18500",
                signingKeyPath=keys + "/signing.pem", policyPath=config + "/access-policy.json")
if any(settings.get(name) != value for name, value in expected.items()):
    sys.exit("Review the store, credential paths and signed HTTP mode.")
origin = urlsplit(settings["publicUrl"])
if (origin.scheme != "http" or not origin.hostname or origin.username is not None
        or origin.path not in ("", "/") or origin.query or origin.fragment
        or origin.hostname in ("localhost", "127.0.0.1", "host.docker.internal")):
    sys.exit("Set publicUrl to the approved company HTTP origin.")
PY
openssl pkey -in "$5/signing.pem" -pubout -outform DER -out "$work/from-private.der"
openssl pkey -pubin -in "$5/release-public.pem" -outform DER -out "$work/public.der"
cmp -s "$work/from-private.der" "$work/public.der" || die 'Signing key pair does not match.'
cat > "$work/ue-dt-distribution.service" <<UNIT
[Unit]
Description=UE-DT distribution server
After=network.target

[Service]
User=$service
Group=$upload
SupplementaryGroups=$service
UMask=0027
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=$tools/server-home/bundle
ExecStart=$server/UeDtLauncher.DistributionServer serve --config $config/server.json
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
ReadWritePaths=$store
Restart=on-failure

[Install]
WantedBy=multi-user.target
UNIT

declare -a sources=() destinations=() modes=() owners=() groups=() directories=()
check_file() {
    local source=$1 target=$2 mode=$3 owner=$4 group=$5
    [[ -f "$source" && ! -L "$source" ]] || die "Expected a regular input file: $source"
    if [[ -e "$target" || -L "$target" ]]; then
        if [[ -L "$target" || ! -f "$target" ]] || ! cmp -s -- "$source" "$target"; then
            die "File conflict: $target"
        fi
        [[ $(stat -c '%a:%U:%G' "$target") == "$mode:$owner:$group" ]] ||
            die "Existing file access differs from the approved layout: $target"
    fi
}
plan_file() {
    check_file "$@"
    check_context "$2"
    sources+=("$1"); destinations+=("$2"); modes+=("$3"); owners+=("$4"); groups+=("$5")
}
plan_tree() {
    local source=$1 destination=$2 path relative mode
    [[ -d "$source" && ! -L "$source" ]] || die "Expected an unlinked publish directory: $source"
    source=$(realpath -e -- "$source")
    find "$source" -mindepth 1 -print0 > "$work/tree"
    while IFS= read -r -d '' path; do
        relative=${path#"$source"/}
        [[ "$relative" != "$path" && ! -L "$path" ]] || die "Unsafe publish path: $path"
        if [[ -d "$path" ]]; then
            check_directory "$destination/$relative"
            check_context "$destination/$relative"
            directories+=("$destination/$relative")
        else
            mode=644
            if [[ -x "$path" ]]; then mode=755; fi
            plan_file "$path" "$destination/$relative" "$mode" root root
        fi
    done < "$work/tree"
}
plan_tree "$2" "$server"
plan_tree "$3" "$launcher"
plan_file "$4/server.json" "$config/server.json" 640 root "$service"
plan_file "$4/access-policy.json" "$config/access-policy.json" 640 root "$service"
plan_file "$5/signing.pem" "$keys/signing.pem" 400 "$service" "$service"
plan_file "$5/release-public.pem" "$keys/release-public.pem" 644 root "$service"
for path in "$store" "$store/incoming" "$store/upload-status" "$store/authority" "$config" "$keys" "$server" "$launcher" "$tools" "$tools/server-home"; do
    check_context "$path"
done

check_control() {
    local source=$1 target=$2 pending
    pending=$target.uedt-install-pending
    check_file "$source" "$target" 644 root root
    check_context "$target"
    if [[ -e "$pending" || -L "$pending" ]]; then
        check_file "$source" "$pending" 644 root root
        [[ ! -e "$target" || "$pending" -ef "$target" ]] || die "Pending control file conflicts with $target"
        pending_controls+=("$target")
    fi
}
check_control "$6" "$site"
check_control "$work/ue-dt-distribution.service" "$unit"
cp -- "$6" "$work/candidate-site.conf"
cat > "$work/verify-nginx.conf" <<NGINX
error_log stderr;
pid "$work/nginx.pid";
events {}
http {
    access_log off;
    include "$work/candidate-site.conf";
}
NGINX
# Verify the candidate before publication and the effective configuration after.
nginx -t -c "$work/verify-nginx.conf" -p "$work/"

getent group "$service" >/dev/null || groupadd --system "$service"
getent group "$upload" >/dev/null || groupadd --system "$upload"
getent passwd "$service" >/dev/null || useradd --system -g "$service" -M -s /sbin/nologin "$service"
ensure_directory() {
    [[ ! -L "$1" && ( ! -e "$1" || -d "$1" ) ]] || die "Unsafe directory: $1"
    if [[ ! -e "$1" ]]; then
        mkdir -- "$1"
        chown -- "$2:$3" "$1"
        chmod -- "$4" "$1"
        label_path "$1"
    fi
}
ensure_directory "$DT_BASE/publish" root root 755
ensure_directory "$server" root root 755
ensure_directory "$launcher" root root 755
ensure_directory "$config" root "$service" 750
ensure_directory "$keys" root "$service" 750
ensure_directory "$tools" root root 755
ensure_directory "$tools/server-home" "$service" "$service" 700
for path in "${directories[@]}"; do ensure_directory "$path" root root 755; done

install_file() {
    local source=$1 target=$2 mode=$3 owner=$4 group=$5
    check_file "$source" "$target" "$mode" "$owner" "$group"
    if [[ -f "$target" ]]; then return; fi
    temp_file=$(mktemp "$(dirname -- "$target")/.uedt-install.XXXXXXXX")
    install -o "$owner" -g "$group" -m "$mode" -- "$source" "$temp_file"
    ln -T -- "$temp_file" "$target"
    rm -- "$temp_file"
    temp_file=
}
for index in "${!sources[@]}"; do
    install_file "${sources[index]}" "${destinations[index]}" "${modes[index]}" "${owners[index]}" "${groups[index]}"
    label_path "${destinations[index]}"
done

publish_control() {
    local source=$1 target=$2 pending
    pending=$target.uedt-install-pending
    if [[ -e "$target" ]]; then
        if [[ "$pending" -ef "$target" ]]; then label_path "$target"; fi
        return
    fi
    if [[ ! -f "$pending" ]]; then
        install_file "$source" "$pending" 644 root root
        pending_controls+=("$target")
    fi
    ln -T -- "$pending" "$target"
    label_path "$target"
}
publish_control "$work/candidate-site.conf" "$site"
nginx -t

if [[ ! -d "$store" || -z $(ls -A "$store") ]]; then
    bash "$script_dir/prepare-server-store.sh" "$store"
    label_path "$store"
fi
check_storage "$store"
runuser -u "$service" -- env DOTNET_BUNDLE_EXTRACT_BASE_DIR="$tools/server-home/bundle" \
    "$server/UeDtLauncher.DistributionServer" list --config "$config/server.json" >/dev/null
[[ -d "$store/incoming" && ! -L "$store/incoming" ]] || die 'Unsafe incoming directory.'
chgrp "$upload" "$store/incoming"
chmod 2770 "$store/incoming"
ensure_directory "$store/upload-status" "$service" "$upload" 750
label_path "$store/incoming"
label_path "$store/upload-status"

publish_control "$work/ue-dt-distribution.service" "$unit"
systemctl daemon-reload
for target in "${pending_controls[@]}"; do
    pending=$target.uedt-install-pending
    if [[ -f "$pending" && "$pending" -ef "$target" ]]; then rm -- "$pending"; fi
done
printf 'Review SELinux policy and bind addresses before enabling the service.\n'

#!/usr/bin/env bash
# Draft for the reviewed RHEL 8.4 initial deployment.
set -euo pipefail
umask 0077

if [[ $# != 6 || $1 != --apply ]]; then
    printf 'Usage: %s --apply SERVER_PUBLISH LAUNCHER_PUBLISH CONFIG_SOURCE KEY_SOURCE NGINX_SOURCE\n' "$0" >&2
    exit 2
fi
[[ $EUID == 0 ]] || { echo 'Run as the Linux administrator.' >&2; exit 1; }
source /etc/os-release
[[ "$ID" == rhel && "$VERSION_ID" == 8.4 ]] || { echo 'Expected RHEL 8.4.' >&2; exit 1; }

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

for item in "$DT_BASE" "$DT_BASE/publish" "$tools"; do
    [[ ! -L "$item" && ( ! -e "$item" || -d "$item" ) ]] || exit 1
done
for target in "$config" "$keys" "$server" "$launcher" "$unit" "$site"; do
    [[ ! -e "$target" && ! -L "$target" ]] || { printf 'Destination already exists: %s\n' "$target" >&2; exit 1; }
done
[[ -d "$DT_BASE" && ! -L "$store" ]] || { echo 'Prepare the deployment base and a canonical store path.' >&2; exit 1; }
if [[ -e "$store" ]]; then
    [[ -d "$store" && -z $(find "$store" -mindepth 1 -maxdepth 1 -print -quit) ]] || exit 1
fi

check_storage() {
    local target=$1 filesystem source rest percent
    read -r filesystem source rest < <(findmnt -no FSTYPE,SOURCE --target "$target")
    printf 'Filesystem %s source %s at %s\n' "$filesystem" "$source" "$target"
    [[ $filesystem == xfs || $filesystem == ext4 ]] || { echo 'The store requires XFS or ext4.' >&2; return 1; }
    df -Pk -- "$target"
    percent=$(df -Pk -- "$target" | awk 'NR==2 {sub(/%/, "", $5); print $5}')
    [[ $percent =~ ^[0-9]+$ ]] || return 1
    if (( percent >= 85 )); then echo 'Storage is at or above the 85% stop threshold.' >&2; return 1; fi
    if (( percent >= 70 )); then echo 'Storage is at or above the 70% warning threshold.' >&2; fi
}
if [[ -d "$store" ]]; then check_storage "$store"; else check_storage "$DT_BASE"; fi

test -x "$2/UeDtLauncher.DistributionServer"
test -x "$3/UeDtLauncher"
test -s "$4/access-policy.json"
test -s "$5/signing.pem"
test -s "$5/release-public.pem"
test -s "$6"
python3 - "$4/server.json" "$store" "$config" "$keys" <<'PY'
import json
import sys
from urllib.parse import urlsplit

file, store, config, keys = sys.argv[1:]
with open(file, encoding="utf-8") as source:
    settings = json.load(source)
expected = dict(root=store, authenticationMode="request-signature-v1", listenUrl="http://127.0.0.1:18500",
                signingKeyPath=keys + "/signing.pem", policyPath=config + "/access-policy.json")
if any(settings.get(name) != value for name, value in expected.items()):
    sys.exit("Review the configured store, key and policy paths and signed HTTP mode.")
origin = urlsplit(settings["publicUrl"])
if (origin.scheme != "http" or not origin.hostname or origin.username is not None
        or origin.path not in ("", "/") or origin.query or origin.fragment
        or origin.hostname in ("localhost", "127.0.0.1", "host.docker.internal")):
    sys.exit("Set publicUrl to the approved company HTTP origin.")
PY

getent group "$service" >/dev/null || groupadd --system "$service"
getent group "$upload" >/dev/null || groupadd --system "$upload"
getent passwd "$service" >/dev/null || useradd --system -g "$service" -M -s /sbin/nologin "$service"
bash "$script_dir/prepare-server-store.sh" "$store"
check_storage "$store"

install -d -m 0755 "$server" "$launcher" "$tools"
cp -R --no-preserve=ownership -- "$2/." "$server/"
cp -R --no-preserve=ownership -- "$3/." "$launcher/"
chmod -R u=rwX,go=rX "$server" "$launcher"
install -m 0644 "$script_dir/prepare-server-store.sh" "$tools/prepare-server-store.sh"
install -d -o root -g "$service" -m 0750 "$config" "$keys"
install -o root -g "$service" -m 0640 "$4/server.json" "$4/access-policy.json" "$config/"
install -o "$service" -g "$service" -m 0400 "$5/signing.pem" "$keys/signing.pem"
install -o root -g "$service" -m 0644 "$5/release-public.pem" "$keys/release-public.pem"

# Prepare the initial HEAD at service identity before enabling uploader access.
install -d -o "$service" -g "$service" -m 0700 "$tools/server-home"
runuser -u "$service" -- env DOTNET_BUNDLE_EXTRACT_BASE_DIR="$tools/server-home/bundle" \
    "$server/UeDtLauncher.DistributionServer" list --config "$config/server.json" >/dev/null
install -d -o "$service" -g "$upload" -m 2770 "$store/incoming"
install -d -o "$service" -g "$upload" -m 0750 "$store/upload-status"

cat > "$unit" <<UNIT
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
chmod 0644 "$unit"
install -m 0644 "$6" "$site"
restorecon -R "$config" "$keys" "$store" "$server" "$launcher" "$tools" "$unit" "$site"
nginx -t
systemctl daemon-reload
printf 'Review SELinux policy and bind addresses before enabling the service.\n'

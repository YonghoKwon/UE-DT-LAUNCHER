#!/bin/bash
set -euo pipefail
root=/dt/store
config=/dt/config
keys=/dt/keys
tools=/dt/tools
launcher=/dt/publish/linux-x64/UeDtLauncher
server=/dt/publish/server-linux-x64/UeDtLauncher.DistributionServer
export HOME=$tools/server-home
export DOTNET_BUNDLE_EXTRACT_BASE_DIR=$HOME/bundle

as_dist() {
    setpriv --reuid=uedt-distribution --regid=uedt-upload --groups="$(id -g uedt-distribution)" "$@"
}

case "${1:-}" in
    seed)
        umask 0077
        if [[ -n $(ls -A "$config") || -n $(ls -A "$keys") ]]; then
            echo 'Fixture config and key volumes must both be empty before seeding.' >&2
            exit 1
        fi
        chown root:uedt-distribution "$config" "$keys"
        chmod 0750 "$config" "$keys"
        install -g uedt-distribution -m 0640 "$tools/server.json" "$tools/access-policy.json" "$config/"
        "$launcher" generate-signing-key --private-key "$keys/signing.pem" \
            --public-key "$keys/release-public.pem" >/dev/null
        chown uedt-distribution:uedt-distribution "$keys/signing.pem"
        chmod 0400 "$keys/signing.pem"
        chmod 0644 "$keys/release-public.pem"
        ;;
    policy)
        umask 0077
        pending=$(mktemp "$config/.access-policy.XXXXXX")
        trap 'rm -f -- "$pending"' EXIT
        cat >"$pending"
        chgrp uedt-distribution "$pending"
        chmod 0640 "$pending"
        mv -T -- "$pending" "$config/access-policy.json"
        ;;
    dist)
        umask 0077
        if [[ -z "$(ls -A "$root")" ]]; then
            chown uedt-distribution:uedt-upload "$root"
            chmod 0711 "$root"
            echo "Initializing empty $root with the published server"
        fi
        # Run the product's root validation before changing any child permissions.
        as_dist "$server" list --config "$config/server.json" >/dev/null
        chgrp uedt-upload "$root/incoming"
        chmod 2770 "$root/incoming"
        install -d -m 0750 -o uedt-distribution -g uedt-upload "$root/upload-status"
        umask 0027
        exec setpriv --reuid=uedt-distribution --regid=uedt-upload --groups="$(id -g uedt-distribution)" \
            "$server" serve --config "$config/server.json"
        ;;
    admin)
        shift
        umask 0077
        as_dist "$server" "$@" --config "$config/server.json"
        ;;
    nginx)
        exec nginx -g 'daemon off;'
        ;;
    sshd)
        umask 0077
        install -d -m 0755 /dt/upload-jail/dev
        if [[ ! -c /dt/upload-jail/dev/null ]]; then
            mknod -m 0666 /dt/upload-jail/dev/null c 1 3
        fi
        test -s "$tools/ssh_host_ed25519_key" ||
            ssh-keygen -q -t ed25519 -N '' -f "$tools/ssh_host_ed25519_key"
        exec /usr/sbin/sshd -D -e -f /etc/ssh/uedt-sshd_config
        ;;
    health)
        status=$(curl --noproxy '*' --max-time 2 -sS -o /dev/null -w '%{http_code}' \
            "http://127.0.0.1:$2/api/v1/catalog")
        test "$status" = 401
        ;;
    *) exit 2 ;;
esac

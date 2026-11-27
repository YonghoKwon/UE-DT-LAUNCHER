#!/usr/bin/env bash
# Run only as root in the isolated dt-setup.Dockerfile container, with a free loop device.
set -euo pipefail
[[ $# == 2 && -f $1 && -f $2 && -d /dt && -z $(ls -A /dt) ]]
root=$(mktemp -d /var/tmp/uedt-dt-setup.XXXXXXXX)
chmod 0755 "$root"
loop=
cleanup() {
    local code=$?
    set +e
    if [[ -x /dt/tools/dt-setup-cron.py || -f /dt/tools/dt-setup-cron.py ]]; then
        runuser -u dt2prod -- python3 -B /dt/tools/dt-setup-cron.py stop
    fi
    nginx -s quit >/dev/null 2>&1
    if [[ -n $loop ]]; then
        umount /dt && losetup -d "$loop"
        if losetup "$loop" > /dev/null 2>&1; then code=1; fi
    fi
    exit "$code"
}
trap cleanup EXIT
truncate -s 2G "$root/dt.xfs"
loop=$(losetup --find --show "$root/dt.xfs")
mkfs.xfs -q "$loop"
mount -o noatime "$loop" /dt
chown dt2prod:dt2prod /dt
chmod 0755 /dt
findmnt -nro FSTYPE,SOURCE,OPTIONS -M /dt
nginx -v
getconf GNU_LIBC_VERSION
python3 --version

printf '127.0.0.1 updates.test\n' >> /etc/hosts
install -d -o dt2prod -g dt2prod /dt/{keys,publish,tools,incoming,ue-dt-updates}
chmod 0700 /dt/keys
install -d /dt/ue-dt-updates/catalogs/{general,developer} /dt/ue-dt-updates/projects/demo/dev
printf '{"legacy":true}\n' > /dt/ue-dt-updates/catalogs/general/catalog.json
printf '{}\n' > /dt/ue-dt-updates/catalogs/developer/catalog.json
printf '{}\n' > /dt/ue-dt-updates/projects/demo/dev/dev.json
printf 'keep\n' > /dt/incoming/original
printf 'legacy key file\n' > /dt/keys/unrelated
htpasswd -cb /dt/ue-dt-updates/.htpasswd-developer legacy fixture >/dev/null
cat >/etc/nginx/nginx.conf <<'NGINX'
user nginx;
worker_processes 1;
pid /run/nginx.pid;
error_log /var/log/nginx/error.log;
events { worker_connections 128; }
http { include /etc/nginx/conf.d/*.conf; }
NGINX
cat >/etc/nginx/conf.d/ue-dt-updates.conf <<'NGINX'
server {
    listen 80 default_server;
    root /dt/ue-dt-updates;
    location ~ ^/(catalogs/general/|projects/[^/]+/prod/stable/) {
        add_header Cache-Control no-store;
        try_files $uri =404;
    }
    location ~ ^/(catalogs/developer/|projects/[^/]+/dev/) {
        auth_basic "Developer updates";
        auth_basic_user_file /dt/ue-dt-updates/.htpasswd-developer;
        try_files $uri =404;
    }
    location / { return 404; }
}
NGINX
nginx -t
nginx
crond

stage_bundle() {
    mkdir "$2"
    tar -xzf "$1" --strip-components=1 -C "$2"
    cp "$2/dt-setup.conf.example" "$2/dt-setup.conf"
    sed -i 's/updates.example.com/updates.test/' "$2/dt-setup.conf"
    chown dt2prod:dt2prod "$2"
    chmod 0755 "$2"
}
one=$root/one
two=$root/two
stage_bundle "$1" "$one"
stage_bundle "$2" "$two"

as_user() {
    runuser -u dt2prod -- "$@"
}
setup() {
    local bundle=$1
    shift
    as_user "$bundle/dt-setup.sh" "$@"
}
code() {
    curl --noproxy '*' -sS -o /dev/null -w '%{http_code}' "http://updates.test$1"
}
inventory() {
    find /dt /home/dt2prod /var/spool/cron -printf '%p %y %m %U %G %l\n' | sort
    find /dt /home/dt2prod /var/spool/cron -type f -print0 | sort -z | xargs -0r sha256sum
}
expect_failure() {
    if "$@"; then
        echo "Unexpected success: $*" >&2
        exit 1
    fi
}

# Faults affect only the fixture's subprocesses. The production CLI has no test switches.
bad=$root/shims
mkdir "$bad"
bad_command() {
    local name=$1
    shift
    printf '#!/bin/bash\n%s\n' "$*" > "$bad/$name"
    chmod 0755 "$bad/$name"
}
fault_install() {
    inventory > "$root/before"
    expect_failure as_user env PATH="$bad:$PATH" "$one/dt-setup.sh" install
    inventory > "$root/after"
    diff -u "$root/before" "$root/after"
    echo "PASS unchanged $1"
}

# Verify the exact bundled programs load on Rocky and both payloads pass ELF inspection.
as_user "$one/linux-x64/UeDtLauncher" --build-info
as_user "$one/server-linux-x64/UeDtLauncher.DistributionServer" --help
inventory > "$root/before"
setup "$one" check
inventory > "$root/after"
diff -u "$root/before" "$root/after"
echo 'PASS check read-only'

bad_command getconf 'echo glibc 2.27'
fault_install ABI
rm "$bad/getconf"
bad_command findmnt 'echo "ext4 /dev/loop1 rw"'
fault_install filesystem
rm "$bad/findmnt"
bad_command ss 'if [[ "$*" == *18500* ]]; then echo "LISTEN 0 5 127.0.0.1:18500 0.0.0.0:*"; else exec /usr/sbin/ss "$@"; fi'
fault_install occupied-backend-port
rm "$bad/ss"
bad_command ss 'if [[ "$*" != *":80" ]]; then exec /usr/sbin/ss "$@"; fi'
fault_install no-nginx-listener
rm "$bad/ss"
bad_command pgrep 'exit 1'
fault_install no-boot-mechanism
rm "$bad/pgrep"
chmod 0555 /dt
fault_install no-write-access
chmod 0755 /dt
fallocate -l 1740M /dt/disk-fixture
fault_install disk-threshold
rm /dt/disk-fixture
touch /dt/config-conflict
mkdir /dt/config
chown dt2prod:dt2prod /dt/config
chmod 0700 /dt/config
mv /dt/config-conflict /dt/config/server.json
fault_install unmanaged-config
rm /dt/config/server.json
rmdir /dt/config
mv "$one/ABI.txt" "$one/ABI.good"
echo bad > "$one/ABI.txt"
fault_install corrupted-bundle
mv "$one/ABI.good" "$one/ABI.txt"
inventory > "$root/before"
expect_failure "$one/dt-setup.sh" install
inventory > "$root/after"
diff -u "$root/before" "$root/after"
echo 'PASS wrong UID unchanged'

bad_command crontab 'if [[ $1 == "-" ]]; then exit 1; fi; exec /usr/bin/crontab "$@"'
fault_install boot-registration-rollback
rm "$bad/crontab"

bad_command curl 'if [[ "$*" == *"127.0.0.1:18500"* ]]; then printf 500; exit; fi; exec /usr/bin/curl "$@"'
fault_install backend-readiness-rollback
rm "$bad/curl"

bad_command crontab 'if [[ $1 == "-" ]]; then kill -KILL "$PPID"; exit 1; fi; exec /usr/bin/crontab "$@"'
expect_failure as_user env PATH="$bad:$PATH" "$one/dt-setup.sh" install
rm "$bad/crontab"
[[ -f /dt/.dt-setup/pending.json ]]
expect_failure setup "$one" check
echo 'PASS interrupted install left a recovery record'

fallocate -l 1740M /dt/disk-fixture
expect_failure setup "$one" install
[[ ! -e /dt/.dt-setup/pending.json && ! -e /dt/publish/dt-current ]]
[[ ! -L /dt/ue-dt-updates && -f /dt/incoming/original ]]
rm /dt/disk-fixture
echo 'PASS interrupted install recovered before a fresh install was denied for disk space'

setup "$one" install
[[ ! -e /dt/.dt-setup/pending.json ]]
[[ -L /dt/ue-dt-updates && -f /dt/.dt-legacy/incoming/original && ! -e /dt/incoming ]]
[[ $(stat -c %a /dt/keys/signing.pem) == 400 ]]
[[ $(code /catalogs/general/catalog.json) == 200 ]]
[[ $(code /catalogs/developer/catalog.json) == 401 ]]
[[ $(code /api/v1/catalog) == 404 ]]
as_user crontab -l | grep -Fx '@reboot /usr/bin/python3 -B /dt/tools/dt-setup-cron.py boot # uedt-dt-setup'
authority() {
    sha256sum /dt/store/authority/HEAD /dt/keys/signing.pem /dt/keys/release-public.pem
}
authority > "$root/authority"
setup "$one" install
authority | diff -u "$root/authority" -
as_user python3 -B /dt/tools/dt-setup-cron.py stop
cron_line=$(as_user crontab -l | cut -d ' ' -f 2-)
as_user bash -c "$cron_line"
for _ in {1..40}; do
    if [[ $(curl --noproxy '*' -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18500/api/v1/catalog) == 401 ]]; then
        break
    fi
    sleep .1
done
[[ $(curl --noproxy '*' -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:18500/api/v1/catalog) == 401 ]]
echo 'PASS install, repeat install, and cron boot command'

# This container uses nginx's own reload signal in place of the host's systemctl.
root_bin=$root/root-shims
install -d "$root_bin"
cat > "$root_bin/systemctl" <<'SHIM'
#!/bin/bash
[[ $# == 2 && $1 == reload && $2 == nginx ]] || exit 1
exec nginx -s reload
SHIM
chmod 0755 "$root_bin/systemctl"

apply_request() {
    local bundle=$1 mode=$2 file line
    setup "$bundle" admin-request
    file=$bundle/nginx/$mode/ue-dt-updates.conf
    while IFS= read -r line; do
        if [[ $line == *'include /etc/nginx/uedt-signed-proxy.inc;'* ]]; then
            sed 's/^/        /' "$bundle/nginx/uedt-signed-proxy.inc"
        else
            printf '%s\n' "$line"
        fi
    done < "$file" > /root/ue-dt-updates.candidate
    awk '/^Apply \(five lines/{p=1; next} p && n++<5' "$bundle/admin-request.txt" > "$root/apply"
}
wait_http() {
    local path=$1 expected=$2
    for _ in {1..40}; do
        if [[ $(code "$path") == "$expected" ]]; then return; fi
        sleep .1
    done
    echo "Failed waiting for $path HTTP $expected" >&2
    return 1
}

original=/etc/nginx/conf.d/ue-dt-updates.conf
cp "$original" "$root/original"
apply_request "$one" coexist
printf '\ninvalid_directive;\n' >> /root/ue-dt-updates.candidate
expect_failure env PATH="$root_bin:$PATH" bash "$root/apply"
cmp "$root/original" "$original"
[[ $(code /catalogs/general/catalog.json) == 200 ]]
echo 'PASS generated nginx commands restored the original after nginx -t failed'

apply_request "$one" coexist
env PATH="$root_bin:$PATH" bash "$root/apply"
wait_http /api/v1/catalog 401
setup "$one" verify
[[ $(code /catalogs/developer/catalog.json) == 401 ]]
[[ $(code /projects/demo/dev/dev.json) == 401 ]]
[[ $(code /catalogs/general/.secret) == 404 ]]
[[ $(curl --noproxy '*' -sS -u legacy:fixture 'http://updates.test/catalogs/developer/catalog.json') == '{}' ]]
curl --noproxy '*' -sSI 'http://updates.test/catalogs/general/catalog.json' | grep -i 'Cache-Control: no-store'
echo 'PASS coexist and legacy auth/cache behavior'

active=$(readlink /dt/publish/dt-current)
bad_command curl 'if [[ "$(readlink /dt/publish/dt-current)" != "'"$active"'" ]]; then printf 500; exit; fi; exec /usr/bin/curl "$@"'
expect_failure as_user env PATH="$bad:$PATH" "$two/dt-setup.sh" update
rm "$bad/curl"
[[ $(readlink /dt/publish/dt-current) == "$active" && ! -e /dt/.dt-setup/pending.json ]]
setup "$one" verify
echo 'PASS failed update restored the previous publish and service'

setup "$two" update
[[ $(readlink /dt/publish/dt-current) != "$active" ]]
authority | diff -u "$root/authority" -
setup "$two" verify
setup "$two" rollback
[[ $(readlink /dt/publish/dt-current) == "$active" ]]
authority | diff -u "$root/authority" -
setup "$one" verify
echo 'PASS update and rollback with unchanged authority/key hashes'

sed -i 's/"coexist"/"cutover"/' "$one/dt-setup.conf"
apply_request "$one" cutover
reload_output=$(env PATH="$root_bin:$PATH" bash "$root/apply")
printf '%s\n' "$reload_output"
wait_http /catalogs/general/catalog.json 404
setup "$one" verify
backup=$(sed -n 's/^backup=//p' <<<"$reload_output")
awk '/^Rollback \(substitute/{p=1; next} p' "$one/admin-request.txt" |
    sed "s@/etc/nginx/ue-dt-updates.before.REPLACE_WITH_PRINTED_SUFFIX@$backup@" > "$root/root-rollback"
env PATH="$root_bin:$PATH" bash "$root/root-rollback"
wait_http /catalogs/general/catalog.json 200
sed -i 's/"cutover"/"coexist"/' "$one/dt-setup.conf"
setup "$one" verify
echo 'PASS cutover and administrator rollback'

setup "$one" rollback
[[ ! -L /dt/ue-dt-updates && -f /dt/incoming/original && ! -e /dt/publish/dt-current ]]
[[ -z $(ss -H -ltn 'sport = :18500') ]]
[[ $(code /catalogs/general/catalog.json) == 200 ]]
authority | diff -u "$root/authority" -
setup "$one" install
setup "$one" verify
authority | diff -u "$root/authority" -
echo 'PASS initial rollback and reinstall preserve authority and keys'

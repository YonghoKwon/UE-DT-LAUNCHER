#!/usr/bin/env bash
set -euo pipefail
# Requires published Linux binaries, nginx, openssl, curl and zip (or Python 3). Keeps evidence under $root.
server=$(realpath "${1:?distribution server executable}")
launcher=$(realpath "${2:?launcher executable}")
nginx=$(realpath "${3:?nginx executable}")
root=$(mktemp -d /tmp/uedt-distribution-e2e.XXXXXX)
server_pid= nginx_pid=
trap 'test -z "$nginx_pid" || kill "$nginx_pid" 2>/dev/null || true; test -z "$server_pid" || kill "$server_pid" 2>/dev/null || true' EXIT
mkdir -p "$root/server/incoming" "$root/client" "$root/nginx" "$root/game"
printf '#!/bin/sh\nprintf "UE_DT_FAKE_GAME_OK\\n"\n' > "$root/game/game.sh"
chmod +x "$root/game/game.sh"
"$launcher" generate-signing-key --private-key "$root/sign.pem" --public-key "$root/public.pem" > "$root/key.log"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$root/tls.key" -out "$root/tls.crt" -days 1 -subj /CN=localhost -addext subjectAltName=DNS:localhost >/dev/null 2>&1
cat > "$root/server.json" <<EOF
{"root":"$root/server","publicUrl":"https://localhost:19443","listenUrl":"http://127.0.0.1:18520","signingKeyPath":"$root/sign.pem","policyPath":"$root/policy.json"}
EOF
cat > "$root/policy.json" <<EOF
{"clients":[{"id":"pc-a","addresses":["127.0.0.1"],"grants":[{"projectId":"demo","environment":"prod","channel":"stable","versions":[]}]}]}
EOF
for version in 1.0.0 2.0.0; do
    upload="$root/server/incoming/$version"
    mkdir -p "$upload"
    if command -v zip >/dev/null 2>&1; then
        (cd "$root/game" && zip -q "$upload/Linux.zip" game.sh)
    else
        (cd "$root/game" && python3 -m zipfile -c "$upload/Linux.zip" game.sh)
    fi
    "$launcher" release-metadata --zip "$upload/Linux.zip" --project-id demo --version "$version" --platform linux-x64 --entry-point game.sh --output "$upload/release.json" >> "$root/intake.log"
    "$server" ingest "$upload" --config "$root/server.json" > "$root/job.json"
    job=$(sed -n 's/.*"id": "\([a-z0-9]*\)".*/\1/p' "$root/job.json")
    test -n "$job"
    "$server" approve "$job" --config "$root/server.json" >> "$root/intake.log"
done
bearer=$("$server" token-issue pc-a --config "$root/server.json")
export UE_DT_AGENT_DATA_ROOT="$root/client/agent"
UE_DT_CREDENTIAL_TOKEN="$bearer" "$launcher" credential set --name e2e > "$root/credential.log"
"$server" serve --config "$root/server.json" > "$root/server.log" 2>&1 & server_pid=$!
cat > "$root/nginx.conf" <<EOF
pid $root/nginx.pid;
error_log $root/nginx/error.log;
events { worker_connections 128; }
http {
 access_log $root/nginx/access.log;
 client_body_temp_path $root/nginx/body;
 proxy_temp_path $root/nginx/proxy;
 fastcgi_temp_path $root/nginx/fastcgi;
 uwsgi_temp_path $root/nginx/uwsgi;
 scgi_temp_path $root/nginx/scgi;
 server {
  listen 127.0.0.1:19443 ssl;
  ssl_certificate $root/tls.crt;
  ssl_certificate_key $root/tls.key;
  location = /api/v1/catalog {
   proxy_pass http://127.0.0.1:18520;
   proxy_set_header X-Distribution-Client-IP \$remote_addr;
   proxy_set_header Authorization \$http_authorization;
  }
  location = /_auth {
   internal;
   proxy_pass http://127.0.0.1:18520/internal/authorize;
   proxy_pass_request_body off;
   proxy_set_header Content-Length "";
   proxy_set_header X-Original-URI \$request_uri;
   proxy_set_header X-Distribution-Client-IP \$remote_addr;
   proxy_set_header Authorization \$http_authorization;
  }
  location ^~ /releases/ {
   auth_request /_auth;
   proxy_pass http://127.0.0.1:18520;
   proxy_set_header X-Distribution-Client-IP \$remote_addr;
   proxy_set_header Authorization \$http_authorization;
  }
  location / { return 404; }
 }
}
EOF
"$nginx" -t -p "$root/nginx" -c "$root/nginx.conf"
"$nginx" -p "$root/nginx" -c "$root/nginx.conf" -g 'daemon off;' > "$root/nginx.log" 2>&1 & nginx_pid=$!
for attempt in {1..30}; do
    status=$(curl -s --cacert "$root/tls.crt" --resolve localhost:19443:127.0.0.1 -o /dev/null -w '%{http_code}' https://localhost:19443/api/v1/catalog || true)
    test "$status" != 401 || break
    sleep 1
done
test "$status" = 401
file=/releases/demo/prod/stable/1.0.0/linux-x64/files/game.sh
check() {
    expected=$1; path=$2; shift 2
    actual=$(curl -s --cacert "$root/tls.crt" --resolve localhost:19443:127.0.0.1 -H "Authorization: Bearer $bearer" "$@" -o /dev/null -w '%{http_code}' "https://localhost:19443$path")
    test "$actual" = "$expected" || { echo "Expected $expected, got $actual: $path" >&2; exit 1; }
}
check 200 /api/v1/catalog
check 206 "$file" -H 'Range: bytes=0-7'
check 403 /releases/demo/dev/dev/1.0.0/linux-x64/files/game.sh
check 403 "$file" --interface 127.0.0.2 -H 'X-Distribution-Client-IP: 127.0.0.1' -H 'X-Forwarded-For: 127.0.0.1'
check 404 /incoming/1.0.0/Linux.zip
for version in 1.0.0 2.0.0; do
cat > "$root/client/config.json" <<EOF
{"schemaVersion":2,"distributionServerUrl":"https://localhost:19443","projectId":"demo","clientProfile":"developer","environment":"prod","channel":"stable","versionPolicy":"exact","requestedVersion":"$version","targetPlatform":"linux-x64","installDir":"$root/client/apps","stateRootDir":"$root/client/state","requireSignedManifests":true,"security":{"credentialName":"e2e","customCaCertificatePath":"$root/tls.crt","allowedDownloadHosts":["localhost"],"trustedSigningKeys":[{"keyId":"release-1","publicKeyPath":"$root/public.pem"}]}}
EOF
    "$launcher" run --config "$root/client/config.json" > "$root/run-$version.log" 2>&1
    grep -q UE_DT_FAKE_GAME_OK "$root/run-$version.log"
done
test -x "$root/client/apps/demo/prod/stable/1.0.0/linux-x64/game.sh"
test -x "$root/client/apps/demo/prod/stable/2.0.0/linux-x64/game.sh"
printf damaged > "$root/client/apps/demo/prod/stable/2.0.0/linux-x64/game.sh"
"$launcher" run --config "$root/client/config.json" --repair > "$root/repair.log" 2>&1
grep -q UE_DT_FAKE_GAME_OK "$root/repair.log"
"$server" token-revoke pc-a --config "$root/server.json"
check 401 "$file"
echo "PASS: HTTPS, sidecar approval, IP/header denial, Range, token revocation, two Linux versions, executable permissions and repair. Evidence: $root"
if [ "${UE_DT_E2E_HOLD:-0}" = 1 ]; then
    echo "Keeping isolated server running for desktop GUI verification: $root"
    wait "$server_pid"
fi

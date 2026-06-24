#!/usr/bin/env bash
# Interactive release-publish wizard for the UE-DT update server.
#
# A release operator uploads a packaged .zip to the incoming directory, then runs
# this wizard over SSH. It discovers the existing directory structure, lets the
# operator pick project / environment / channel / version / platform from menus
# (avoiding typos like "windows-64"), unzips into the correct server path, and
# delegates manifest-gen + catalog-update to publish-release.sh (--no-copy).
#
# Usage:
#   ./tools/publish-wizard.sh [path/to/package.zip]
#
# Every prompt reads from stdin so the wizard is testable by piping answers, e.g.
#   printf '1\n...\n' | ./tools/publish-wizard.sh /dt/incoming/build.zip
set -euo pipefail

# CDPATH= is a one-shot env assignment for the `cd` command (not a var assign).
# shellcheck disable=SC1007
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
# shellcheck source=tools/launcher-bin.sh
. "$script_dir/launcher-bin.sh"

# --- choose(): numbered menu helper ------------------------------------------
# Usage: result=$(choose "Prompt text" "opt1" "opt2" ... )
# Prints a numbered menu (1..N) to stderr, reads a number from stdin, re-prompts
# on invalid/empty input, and echoes the chosen value to stdout.
choose() {
    local prompt=$1
    shift
    local options=("$@")
    local count=${#options[@]}
    local i reply
    if [ "$count" -eq 0 ]; then
        echo "ERROR: choose() called with no options" >&2
        return 1
    fi
    while true; do
        {
            printf '%s\n' "$prompt"
            for i in "${!options[@]}"; do
                printf '  %d) %s\n' "$((i + 1))" "${options[$i]}"
            done
            printf '선택 (1-%d): ' "$count"
        } >&2
        if ! read -r reply; then
            echo >&2
            echo "ERROR: no more input (EOF)" >&2
            return 1
        fi
        if printf '%s' "$reply" | grep -Eq '^[0-9]+$' \
            && [ "$reply" -ge 1 ] && [ "$reply" -le "$count" ]; then
            printf '%s' "${options[$((reply - 1))]}"
            return 0
        fi
        echo "  잘못된 입력입니다. 1-$count 사이 숫자를 입력하세요." >&2
    done
}

# ask(): prompt with optional default, echo answer (may be empty if no default).
ask() {
    local prompt=$1 default=${2:-} reply
    if [ -n "$default" ]; then
        printf '%s [%s]: ' "$prompt" "$default" >&2
    else
        printf '%s: ' "$prompt" >&2
    fi
    if ! read -r reply; then
        echo >&2
        reply=
    fi
    if [ -z "$reply" ] && [ -n "$default" ]; then
        reply=$default
    fi
    printf '%s' "$reply"
}

# ask_required(): like ask() but re-prompts until non-empty.
ask_required() {
    local prompt=$1 default=${2:-} reply
    while true; do
        reply=$(ask "$prompt" "$default")
        if [ -n "$reply" ]; then
            printf '%s' "$reply"
            return 0
        fi
        echo "  값을 입력해야 합니다." >&2
    done
}

# ask_entry_point(): prompt for an entry-point relative to "$DEST/files" and verify it exists.
# Rejects absolute/parent paths and missing files so a typo is caught before delegating.
ask_entry_point() {
    local reply
    while true; do
        reply=$(ask_required "entry-point (files/ 기준 상대경로, 예: Linux/run.sh)")
        case "$reply" in
            /* | ../* | */../*)
                echo "  상대경로만 가능합니다('/'나 '..'로 시작/포함 불가)." >&2; continue ;;
        esac
        if [ -f "$DEST/files/$reply" ]; then
            printf '%s' "$reply"
            return 0
        fi
        echo "  files/ 아래에 그 파일이 없습니다: $reply" >&2
    done
}

# confirm(): yes/no prompt. $2 = default ('y' or 'n'). Returns 0 for yes.
confirm() {
    local prompt=$1 default=${2:-n} reply hint
    if [ "$default" = "y" ]; then hint="[Y/n]"; else hint="[y/N]"; fi
    printf '%s %s: ' "$prompt" "$hint" >&2
    if ! read -r reply; then
        echo >&2
        reply=
    fi
    [ -z "$reply" ] && reply=$default
    case "$reply" in
        [Yy] | [Yy][Ee][Ss]) return 0 ;;
        *) return 1 ;;
    esac
}

# --- 0. Config ---------------------------------------------------------------
env_file="$script_dir/publish.env"

# Defaults. Companies may use /dt/ue-dt-updates instead of /srv/ue-dt-updates.
DEFAULT_SERVER_ROOT=/srv/ue-dt-updates
DEFAULT_BASE_URL_ROOT=http://127.0.0.1

# Capture whatever came from the real environment before sourcing publish.env,
# so an explicit env var always wins over the saved file.
ENV_SERVER_ROOT=${SERVER_ROOT:-}
ENV_BASE_URL_ROOT=${BASE_URL_ROOT:-}
ENV_INCOMING_DIR=${INCOMING_DIR:-}

if [ -f "$env_file" ]; then
    # shellcheck source=/dev/null
    . "$env_file"
fi

# Re-apply environment overrides on top of the sourced file.
[ -n "$ENV_SERVER_ROOT" ] && SERVER_ROOT=$ENV_SERVER_ROOT
[ -n "$ENV_BASE_URL_ROOT" ] && BASE_URL_ROOT=$ENV_BASE_URL_ROOT
[ -n "$ENV_INCOMING_DIR" ] && INCOMING_DIR=$ENV_INCOMING_DIR

config_changed=0
if [ -z "${SERVER_ROOT:-}" ]; then
    SERVER_ROOT=$(ask "서버 루트(SERVER_ROOT)" "$DEFAULT_SERVER_ROOT")
    config_changed=1
fi
if [ -z "${BASE_URL_ROOT:-}" ]; then
    BASE_URL_ROOT=$(ask "클라이언트 접속 주소(BASE_URL_ROOT)" "$DEFAULT_BASE_URL_ROOT")
    config_changed=1
fi
if [ -z "${INCOMING_DIR:-}" ]; then
    INCOMING_DIR=$(ask "업로드(incoming) 디렉터리(INCOMING_DIR)" "$SERVER_ROOT/incoming")
    config_changed=1
fi

BASE_URL_ROOT=${BASE_URL_ROOT%/}

if [ "$config_changed" -eq 1 ]; then
    if confirm "이 설정을 $env_file 에 저장할까요?" "y"; then
        cat >"$env_file" <<EOF
# publish-wizard.sh / publish-release.sh 설정 (자동 생성)
SERVER_ROOT="$SERVER_ROOT"
BASE_URL_ROOT="$BASE_URL_ROOT"
INCOMING_DIR="$INCOMING_DIR"
EOF
        echo "저장됨: $env_file" >&2
    fi
fi

launcher=$(find_launcher_bin)

if ! command -v unzip >/dev/null 2>&1; then
    echo "ERROR: 'unzip' 명령을 찾을 수 없습니다. 설치 후 다시 실행하세요 (예: sudo dnf install unzip)." >&2
    exit 1
fi

projects_root="$SERVER_ROOT/projects"

# --- 1. Zip select -----------------------------------------------------------
ZIP=""
if [ "$#" -ge 1 ] && [ -n "${1:-}" ]; then
    case "$1" in
        *.zip)
            if [ -r "$1" ]; then
                ZIP=$1
            else
                echo "ERROR: zip 파일을 읽을 수 없습니다: $1" >&2
                exit 1
            fi
            ;;
        *)
            echo "ERROR: 인자는 .zip 파일이어야 합니다: $1" >&2
            exit 1
            ;;
    esac
fi

if [ -z "$ZIP" ]; then
    zip_candidates=()
    # Collect *.zip from INCOMING_DIR and the current directory.
    for dir in "$INCOMING_DIR" "."; do
        [ -d "$dir" ] || continue
        while IFS= read -r f; do
            [ -n "$f" ] && zip_candidates+=("$f")
        done < <(find "$dir" -maxdepth 1 -type f -iname '*.zip' 2>/dev/null | sort)
    done
    if [ "${#zip_candidates[@]}" -eq 0 ]; then
        echo "ERROR: zip 파일을 찾을 수 없습니다." >&2
        echo "  찾은 위치: $INCOMING_DIR 와 현재 디렉터리(.)" >&2
        echo "  zip 경로를 인자로 직접 지정할 수도 있습니다: $0 /path/to/build.zip" >&2
        exit 1
    fi
    ZIP=$(choose "업로드할 ZIP 파일을 선택하세요:" "${zip_candidates[@]}")
fi
echo "ZIP: $ZIP" >&2

# --- 2. Project --------------------------------------------------------------
NEW_PROJECT_LABEL='[새 프로젝트 입력]'
project_options=()
if [ -d "$projects_root" ]; then
    while IFS= read -r d; do
        [ -n "$d" ] && project_options+=("$(basename "$d")")
    done < <(find "$projects_root" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | sort)
fi
project_options+=("$NEW_PROJECT_LABEL")

project_choice=$(choose "프로젝트를 선택하세요:" "${project_options[@]}")
if [ "$project_choice" = "$NEW_PROJECT_LABEL" ]; then
    while true; do
        proj=$(ask_required "새 projectId (소문자/숫자/하이픈, 예: ue-dt-simulator)")
        if ! printf '%s' "$proj" | grep -Eq '^[a-z0-9][a-z0-9-]*$'; then
            echo "  projectId는 소문자/숫자/하이픈만 가능하며 소문자·숫자로 시작해야 합니다(공백·슬래시·'..' 불가)." >&2
            continue
        fi
        break
    done
    disp=$(ask_required "표시 이름(displayName)" "$proj")
else
    proj=$project_choice
    disp=$(ask_required "표시 이름(displayName)" "$proj")
fi

proj_dir="$projects_root/$proj"

# --- 3/4/5. Environment / Channel / Platform (fixed sets, mark existing) -----

# existing_dir <parent> <value> -> 0 if that child dir exists on disk.
existing_dir() {
    [ -d "$1/$2" ]
}

# build_options <parent> <value...> : echoes options, appending " ✓" to those
# whose directory already exists under <parent>. Stores a parallel map of
# decorated->raw in the global assoc array RAW_OF.
declare -A RAW_OF=()
build_options() {
    local parent=$1
    shift
    local v label
    BUILT_OPTIONS=()
    for v in "$@"; do
        if [ -d "$parent" ] && existing_dir "$parent" "$v"; then
            label="$v ✓"
        else
            label="$v"
        fi
        RAW_OF["$label"]=$v
        BUILT_OPTIONS+=("$label")
    done
}

build_options "$proj_dir" prod dev
env_label=$(choose "환경(environment)을 선택하세요 (✓ = 이미 존재):" "${BUILT_OPTIONS[@]}")
env=${RAW_OF["$env_label"]}

build_options "$proj_dir/$env" stable beta dev
chan_label=$(choose "채널(channel)을 선택하세요 (✓ = 이미 존재):" "${BUILT_OPTIONS[@]}")
chan=${RAW_OF["$chan_label"]}

chan_dir="$proj_dir/$env/$chan"

# --- 6. Version --------------------------------------------------------------
existing_versions=()
if [ -d "$chan_dir" ]; then
    while IFS= read -r d; do
        [ -n "$d" ] && existing_versions+=("$(basename "$d")")
    done < <(find "$chan_dir" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | sort)
fi
if [ "${#existing_versions[@]}" -gt 0 ]; then
    {
        echo "기존 버전 ($proj/$env/$chan):"
        for v in "${existing_versions[@]}"; do
            printf '  - %s\n' "$v"
        done
    } >&2
else
    echo "기존 버전 없음 ($proj/$env/$chan)" >&2
fi
ver=$(ask_required "새 버전(version)")

ver_dir="$chan_dir/$ver"

# Platform (mark existing under this version).
build_options "$ver_dir" windows-x64 linux-x64
plat_label=$(choose "플랫폼(platform)을 선택하세요 (✓ = 이미 존재):" "${BUILT_OPTIONS[@]}")
plat=${RAW_OF["$plat_label"]}

DEST="$SERVER_ROOT/projects/$proj/$env/$chan/$ver/$plat"

if [ -d "$DEST/files" ]; then
    if ! confirm "이미 존재합니다: $DEST/files — 덮어쓸까요?" "n"; then
        echo "중단합니다." >&2
        exit 1
    fi
fi

# --- 7. Unzip + entry-point --------------------------------------------------
mkdir -p "$DEST/files"
echo "압축 해제: $ZIP -> $DEST/files" >&2
unzip -o "$ZIP" -d "$DEST/files" >&2

ep_candidates=()
while IFS= read -r f; do
    # Present paths relative to files/ (that's what --entry-point expects).
    rel=${f#"$DEST/files/"}
    [ -n "$rel" ] && ep_candidates+=("$rel")
done < <(find "$DEST/files" -type f \( -iname '*.exe' -o -iname '*.sh' \) 2>/dev/null | sort)

MANUAL_EP_LABEL='[직접 입력]'
ep=""
if [ "${#ep_candidates[@]}" -gt 0 ]; then
    ep_choice=$(choose "실행 진입점(entry-point)을 선택하세요 (files/ 기준 상대경로):" \
        "${ep_candidates[@]}" "$MANUAL_EP_LABEL")
    if [ "$ep_choice" = "$MANUAL_EP_LABEL" ]; then
        ep=$(ask_entry_point)
    else
        ep=$ep_choice
    fi
else
    echo "자동 감지된 .exe/.sh 가 없습니다." >&2
    ep=$(ask_entry_point)
fi

# --- 8. Catalog profile / allowed-profiles / set-latest / notes --------------
catprofile=$(choose "카탈로그 프로필(catalog-profile)을 선택하세요:" general developer)
profiles=$(ask "받을 수 있는 클라이언트 프로필(allowed-profiles, 쉼표 구분)" "$catprofile")
[ -z "$profiles" ] && profiles=$catprofile

set_latest=0
if confirm "이 버전을 같은 트랙의 '최신(latest)'으로 표시할까요?" "y"; then
    set_latest=1
fi

notes=$(ask "릴리스 노트(notes, 비워도 됨)")

# --- 9. Summary + confirm ----------------------------------------------------
{
    echo
    echo "================ 요약 ================"
    printf '  ZIP             : %s\n' "$ZIP"
    printf '  projectId       : %s\n' "$proj"
    printf '  displayName     : %s\n' "$disp"
    printf '  environment     : %s\n' "$env"
    printf '  channel         : %s\n' "$chan"
    printf '  version         : %s\n' "$ver"
    printf '  platform        : %s\n' "$plat"
    printf '  entry-point     : %s\n' "$ep"
    printf '  catalog-profile : %s\n' "$catprofile"
    printf '  allowed-profiles: %s\n' "$profiles"
    printf '  set-latest      : %s\n' "$([ "$set_latest" -eq 1 ] && echo yes || echo no)"
    printf '  notes           : %s\n' "${notes:-(없음)}"
    printf '  server-root     : %s\n' "$SERVER_ROOT"
    printf '  base-url-root   : %s\n' "$BASE_URL_ROOT"
    printf '  dest            : %s\n' "$DEST/files"
    echo "======================================"
} >&2

if ! confirm "위 내용으로 퍼블리시할까요?" "n"; then
    echo "중단합니다. (파일은 $DEST/files 에 이미 풀려 있습니다.)" >&2
    exit 1
fi

# Delegate manifest-gen + catalog-update to the existing tool (files already in
# place, so --no-copy).
publish_args=(
    --no-copy
    --package-dir "$DEST/files"
    --server-root "$SERVER_ROOT"
    --base-url-root "$BASE_URL_ROOT"
    --project-id "$proj"
    --display-name "$disp"
    --version "$ver"
    --environment "$env"
    --channel "$chan"
    --platform "$plat"
    --entry-point "$ep"
    --catalog-profile "$catprofile"
    --allowed-profiles "$profiles"
)
[ -n "$notes" ] && publish_args+=(--notes "$notes")
[ "$set_latest" -eq 1 ] && publish_args+=(--set-latest)

# If the delegate fails after files are unzipped, the release is half-published
# (files present, catalog possibly not updated) — say so clearly instead of dying silently.
if ! "$script_dir/publish-release.sh" "${publish_args[@]}"; then
    echo >&2
    echo "오류: 퍼블리시(manifest/catalog) 단계가 실패했습니다." >&2
    echo "  파일은 이미 풀려 있습니다: $DEST/files" >&2
    echo "  catalog가 갱신되지 않았을 수 있으니, 위 오류를 확인 후 같은 값으로 다시 실행하세요." >&2
    echo "  현재 등록 상태 확인: $launcher list-releases --catalog \"$SERVER_ROOT/catalogs/$catprofile/catalog.json\" --project \"$proj\"" >&2
    exit 1
fi

# SELinux context re-apply (best-effort). Use 'sudo -n' so a host without cached/NOPASSWD
# sudo fails fast with a warning instead of silently blocking on a hidden password prompt.
if command -v restorecon >/dev/null 2>&1; then
    if ! sudo -n restorecon -Rv "$SERVER_ROOT" >/dev/null 2>&1; then
        echo "경고: restorecon 자동 실행을 건너뜁니다(sudo 비밀번호 필요 등). 직접 실행하세요:" >&2
        echo "  sudo restorecon -Rv \"$SERVER_ROOT\"" >&2
    fi
else
    echo "참고: restorecon 명령이 없어 SELinux 컨텍스트 재적용을 건너뜁니다." >&2
fi

echo
echo "등록된 릴리스 ($proj):"
"$launcher" list-releases \
    --catalog "$SERVER_ROOT/catalogs/$catprofile/catalog.json" \
    --project "$proj"

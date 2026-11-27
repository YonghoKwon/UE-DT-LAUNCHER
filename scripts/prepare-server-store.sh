#!/usr/bin/env bash
# Provision the empty authority store before starting the distribution service.
set -euo pipefail

user_mode=0
if [[ ${1:-} == --user ]]; then
    user_mode=1
    shift
fi
if [[ $# -ne 1 || $1 != /* || $1 == / ]]; then
    echo "Usage: $0 [--user] /absolute/path/to/store" >&2
    exit 2
fi

store=$1
parent=$(dirname -- "$store")
if [[ ! -d "$parent" || $(realpath -m -- "$store") != "$store" || -L "$store" ]]; then
    echo 'Choose a canonical store path under an existing parent.' >&2
    exit 1
fi

if [[ -e "$store" ]]; then
    [[ -d "$store" ]] || { echo 'The store must be a directory.' >&2; exit 1; }
    first_entry=$(find "$store" -mindepth 1 -maxdepth 1 -print -quit)
    [[ -z "$first_entry" ]] || { echo 'The store must be empty.' >&2; exit 1; }
fi

if (( user_mode )); then
    [[ $EUID != 0 ]] || { echo 'Run --user as the service account.' >&2; exit 1; }
    install -d -m 0700 -- "$store"
else
    install -d -o uedt-distribution -g uedt-distribution -m 0711 -- "$store"
fi

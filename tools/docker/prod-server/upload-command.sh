#!/bin/bash
set -euo pipefail
command=${SSH_ORIGINAL_COMMAND:-}
incoming=/dt/store/incoming
status=/dt/store/upload-status
segment='[A-Za-z0-9][A-Za-z0-9._-]{0,127}'
umask 0007

case "$command" in
    true) exit 0 ;;
    "test -d $incoming") test -d "$incoming"; exit ;;
    "test -w $incoming") test -w "$incoming"; exit ;;
    /usr/libexec/openssh/sftp-server)
        exec /usr/libexec/openssh/sftp-server -e -d "$incoming" -u 0007 \
            -p realpath,stat,lstat,fstat,open,close,read,write,setstat,fsetstat,fsync,limits
        ;;
esac

if [[ $command =~ $incoming/($segment) ]]; then
    upload=$incoming/${BASH_REMATCH[1]}
    if [[ $command == "mkdir -- $upload" ]]; then
        exec /usr/bin/mkdir -- "$upload"
    fi
    for file in payload.zip release.json; do
        source=$upload/$file.uploading
        target=$upload/$file
        if [[ $command == "mv -n -- $source $target && test ! -e $source && test -f $target" ]]; then
            [[ -d "$upload" && ! -L "$upload" && -f "$source" && ! -L "$source" && ! -L "$target" ]]
            /usr/bin/mv -n -- "$source" "$target"
            [[ ! -e "$source" && -f "$target" && ! -L "$target" ]]
            exit
        fi
    done
    if [[ $command == "test -f $upload/payload.zip && test -f $upload/release.json && test ! -e $upload/payload.zip.uploading && test ! -e $upload/release.json.uploading" ]]; then
        [[ -d "$upload" && ! -L "$upload" &&
           -f "$upload/payload.zip" && ! -L "$upload/payload.zip" &&
           -f "$upload/release.json" && ! -L "$upload/release.json" &&
           ! -e "$upload/payload.zip.uploading" && ! -L "$upload/payload.zip.uploading" &&
           ! -e "$upload/release.json.uploading" && ! -L "$upload/release.json.uploading" ]]
        exit
    fi
fi
if [[ $command =~ $status/($segment)\.json$ ]]; then
    file=$status/${BASH_REMATCH[1]}.json
    if [[ $command == "if test -r $status && test -x $status && ! test -e $file && ! test -L $file; then exit 44; fi; exec cat -- $file" ]]; then
        if [[ -r "$status" && -x "$status" && ! -e "$file" && ! -L "$file" ]]; then exit 44; fi
        [[ -f "$file" && ! -L "$file" ]]
        exec /usr/bin/cat -- "$file"
    fi
fi
echo 'SSH command not permitted by the upload fixture.' >&2
exit 126

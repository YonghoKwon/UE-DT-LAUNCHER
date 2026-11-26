ARG ROCKY_IMAGE=docker.io/rockylinux/rockylinux:8.4@sha256:f0d7460b97156f6c8ea2ae73152bc11fe410d272387d60ddff36dfcea22ef689
FROM ${ROCKY_IMAGE}
ARG RUN_ID
ARG OWNER_TOKEN
LABEL uedt.run=$RUN_ID uedt.owner=$OWNER_TOKEN

COPY rocky-8.4-vault.repo /etc/yum.repos.d/uedt-vault.repo
RUN dnf -y --disablerepo='*' --enablerepo='uedt-8.4-*' --setopt=install_weak_deps=False \
      install nginx openssh-server libicu openssl curl shadow-utils util-linux findutils binutils diffutils \
    && dnf clean all \
    && groupadd -g 20001 uedt-distribution \
    && groupadd -g 20002 uedt-upload \
    && useradd -u 20001 -g uedt-distribution -G uedt-upload -r -M -s /sbin/nologin uedt-distribution \
    && useradd -u 20002 -g uedt-upload -M -p '*' -s /bin/bash uploader \
    && install -d -m 0711 /dt/store \
    && install -d -m 0700 -o uedt-distribution -g uedt-distribution /dt/tools/server-home \
    && ln -sf /dev/stdout /var/log/nginx/ue-dt-access.log

# Keep the SSH session in a minimal root with just the upload protocol tools.
RUN set -eu; jail=/dt/upload-jail; \
    install -d -m 0755 "$jail" "$jail/etc" "$jail/home/uploader" "$jail/dt/store"; \
    programs='/bin/bash /usr/bin/mkdir /usr/bin/mv /usr/bin/cat /usr/libexec/openssh/sftp-server'; \
    if [ -x /usr/bin/coreutils ]; then programs="$programs /usr/bin/coreutils"; fi; \
    for program in $programs; do \
        cp -L --parents "$program" "$jail"; \
        if ! ldd "$program" > /tmp/uedt-jail-libraries; then \
            if head -n 1 "$program" | grep -q '^#!/usr/bin/coreutils --coreutils-prog-shebang='; then continue; fi; \
            echo "Cannot audit jail program: $program"; cat /tmp/uedt-jail-libraries; exit 1; \
        fi; \
        if grep -q 'not found' /tmp/uedt-jail-libraries; then cat /tmp/uedt-jail-libraries; exit 1; fi; \
        for library in $(awk '$2=="=>" && $3~/^\// {print $3} $1~/^\// {print $1}' /tmp/uedt-jail-libraries); do \
            cp -L --parents "$library" "$jail"; \
        done; \
    done; \
    rm /tmp/uedt-jail-libraries; \
    cp -L --parents /lib64/libnss_files.so.2 "$jail"; \
    getent passwd root uploader > "$jail/etc/passwd"; \
    getent group root uedt-upload > "$jail/etc/group"

COPY server/ /dt/publish/server-linux-x64/
COPY launcher/ /dt/publish/linux-x64/
COPY server.json access-policy.json uploader.pub /dt/tools/
COPY nginx.conf /etc/nginx/nginx.conf
COPY sshd_config /etc/ssh/uedt-sshd_config
COPY upload-command.sh /dt/upload-jail/upload-command.sh
COPY entrypoint.sh /dt/tools/entrypoint.sh
RUN chmod 0644 /dt/tools/uploader.pub \
    && chmod 0755 /dt/upload-jail/upload-command.sh \
    && chmod 0755 /dt/tools/entrypoint.sh /dt/publish/server-linux-x64/UeDtLauncher.DistributionServer /dt/publish/linux-x64/UeDtLauncher
ENTRYPOINT ["/dt/tools/entrypoint.sh"]

ARG ROCKY_IMAGE=docker.io/rockylinux/rockylinux:8.4@sha256:f0d7460b97156f6c8ea2ae73152bc11fe410d272387d60ddff36dfcea22ef689
FROM ${ROCKY_IMAGE}
ARG RUN_ID
LABEL uedt.run=$RUN_ID

COPY rocky-8.4-vault.repo /etc/yum.repos.d/uedt-vault.repo
RUN dnf -y --disablerepo='*' --enablerepo='uedt-8.4-*' --setopt=install_weak_deps=False \
      install nginx openssh-server libicu openssl curl shadow-utils util-linux findutils binutils \
    && dnf clean all \
    && groupadd -g 20001 uedt-distribution \
    && groupadd -g 20002 uedt-upload \
    && useradd -u 20001 -g uedt-distribution -G uedt-upload -r -M -s /sbin/nologin uedt-distribution \
    && useradd -u 20002 -g uedt-upload -M -p '*' -s /bin/bash uploader \
    && install -d -m 0711 /dt/store \
    && install -d -m 0700 -o uedt-distribution -g uedt-distribution /dt/tools/server-home \
    && ln -sf /dev/stdout /var/log/nginx/ue-dt-access.log

COPY server/ /dt/publish/server-linux-x64/
COPY launcher/ /dt/publish/linux-x64/
COPY server.json access-policy.json uploader.pub /dt/tools/
COPY nginx.conf /etc/nginx/nginx.conf
COPY sshd_config /etc/ssh/uedt-sshd_config
COPY entrypoint.sh /dt/tools/entrypoint.sh
RUN chmod 0644 /dt/tools/uploader.pub \
    && chmod 0755 /dt/tools/entrypoint.sh /dt/publish/server-linux-x64/UeDtLauncher.DistributionServer /dt/publish/linux-x64/UeDtLauncher
ENTRYPOINT ["/dt/tools/entrypoint.sh"]

ARG ROCKY_IMAGE=docker.io/rockylinux/rockylinux:8.4@sha256:f0d7460b97156f6c8ea2ae73152bc11fe410d272387d60ddff36dfcea22ef689
FROM ${ROCKY_IMAGE}
ARG RUN_ID
LABEL uedt.run=$RUN_ID
COPY tools/docker/prod-server/rocky-8.4-vault.repo /etc/yum.repos.d/uedt-vault.repo
RUN dnf -y --disablerepo='*' --enablerepo='uedt-8.4-*' --setopt=install_weak_deps=False \
      install nginx libicu openssl curl shadow-utils util-linux findutils binutils diffutils \
              python3 procps-ng iproute cronie xfsprogs httpd-tools which \
    && dnf clean all \
    && groupadd -g 300 dt2prod \
    && useradd -u 301 -g dt2prod -m -s /bin/bash dt2prod \
    && mkdir /dt

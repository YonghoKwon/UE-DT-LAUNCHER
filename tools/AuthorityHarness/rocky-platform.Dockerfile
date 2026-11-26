FROM docker.io/rockylinux/rockylinux:8.4@sha256:f0d7460b97156f6c8ea2ae73152bc11fe410d272387d60ddff36dfcea22ef689
ARG RUN_ID
LABEL uedt.run=$RUN_ID

RUN printf '%s\n' '[uedt-8.4-baseos]' 'name=Rocky 8.4 Vault BaseOS' \
    'baseurl=https://dl.rockylinux.org/vault/rocky/8.4/BaseOS/x86_64/os/' \
    'gpgcheck=1' 'gpgkey=file:///etc/pki/rpm-gpg/RPM-GPG-KEY-rockyofficial' \
    > /etc/yum.repos.d/uedt-vault.repo \
    && dnf -y --disablerepo='*' --enablerepo='uedt-8.4-baseos' --setopt=install_weak_deps=False \
       install libicu openssl-libs util-linux xfsprogs \
    && dnf clean all
COPY --chmod=0755 rocky-platform.sh /opt/checks/rocky-platform.sh
COPY --chmod=0755 harness/ /opt/checks/harness/

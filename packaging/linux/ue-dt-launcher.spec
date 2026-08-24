Name:           ue-dt-launcher
%global __os_install_post %{nil}
Version:        %{launcher_version}
Release:        1%{?dist}
Summary:        UE-DT managed application launcher and update agent
License:        Proprietary
BuildArch:      x86_64
Source0:        ue-dt-launcher-%{version}.tar.gz
Requires(pre):  shadow-utils

%description
Machine-wide UE-DT launcher CLI and managed update/recovery agent.

%prep
%setup -q

%install
mkdir -p %{buildroot}/opt/ue-dt-launcher %{buildroot}/etc/ue-dt-launcher %{buildroot}/var/lib/ue-dt-launcher/state %{buildroot}/var/lib/ue-dt-launcher/apps %{buildroot}/var/log/ue-dt-launcher %{buildroot}/usr/lib/systemd/system
install -m 0755 UeDtLauncher %{buildroot}/opt/ue-dt-launcher/UeDtLauncher
install -m 0755 UeDtLauncher.Agent %{buildroot}/opt/ue-dt-launcher/UeDtLauncher.Agent
install -m 0644 ue-dt-launcher-agent.service %{buildroot}/usr/lib/systemd/system/ue-dt-launcher-agent.service
install -m 0640 launcher.config.json %{buildroot}/etc/ue-dt-launcher/launcher.config.json

%pre
getent group uedt >/dev/null || groupadd -r uedt
getent passwd uedt >/dev/null || useradd -r -g uedt -d /var/lib/ue-dt-launcher -s /sbin/nologin uedt

%post
systemctl daemon-reload >/dev/null 2>&1 || :

%preun
if [ $1 -eq 0 ]; then systemctl --no-reload disable --now ue-dt-launcher-agent.service >/dev/null 2>&1 || :; fi

%postun
systemctl daemon-reload >/dev/null 2>&1 || :

%files
/opt/ue-dt-launcher/UeDtLauncher
/opt/ue-dt-launcher/UeDtLauncher.Agent
/usr/lib/systemd/system/ue-dt-launcher-agent.service
%config(noreplace) /etc/ue-dt-launcher/launcher.config.json
%dir %attr(0750,uedt,uedt) /var/lib/ue-dt-launcher
%dir %attr(0750,uedt,uedt) /var/lib/ue-dt-launcher/state
%dir %attr(0755,uedt,uedt) /var/lib/ue-dt-launcher/apps
%dir %attr(0750,uedt,uedt) /var/log/ue-dt-launcher

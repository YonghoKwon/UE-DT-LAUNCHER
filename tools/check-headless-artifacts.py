"""Read-only package/template contracts. Never registers tasks or installs packages."""
from pathlib import Path
import xml.etree.ElementTree as ET

root=Path(__file__).resolve().parents[1]
ns={'t':'http://schemas.microsoft.com/windows/2004/02/mit/task'}
task=ET.parse(root/'installer/windows/CheckTask.xml.template').getroot()
assert task.find('t:Settings/t:Enabled',ns).text=='false'
assert task.find('t:Settings/t:StartWhenAvailable',ns).text=='false'
assert task.find('t:Settings/t:MultipleInstancesPolicy',ns).text=='IgnoreNew'
assert 'scheduled-check' in task.find('t:Actions/t:Exec/t:Arguments',ns).text
timer=(root/'packaging/linux/ue-dt-check.timer.template').read_text()
assert '@ADMIN_INTERVAL_SECONDS@' in timer and 'Persistent=false' in timer and not any(line.strip()=='[Install]' for line in timer.splitlines())
service=(root/'packaging/linux/ue-dt-check.service').read_text()
assert ' scheduled-check ' in service and 'service-run' not in service
spec=(root/'packaging/linux/ue-dt-launcher.spec').read_text()
assert '%config(noreplace)' in spec and '0750,root,uedt' in spec
print('PASS: disabled scheduler, no catch-up/install command, RPM config/credential ownership contracts')

"""Check the maintained documentation boundary and local Markdown links (no app execution)."""
from pathlib import Path
from urllib.parse import unquote
import re

root = Path(__file__).resolve().parents[1]
managed = {"README.md", "AGENTS.md", "IMPROVEMENTS.md", "PROJECT_GOALS.md"}
assert {p.name for p in root.glob("*.md")} == managed
reference = root / "docs/reference"
expected = {"README.md", "feature-workflow.md", "distribution-workflow.md", "intranet-auth.md",
            "guide-03-launcher-usage.md", "launcher-user-guide.md", "launcher-ui-customization.md",
            "runtime-safety.md", "service-mode.md", "commercial-deployment.md"}
assert {p.name for p in reference.iterdir() if p.is_file()} == expected
assert {p.name for p in (root / "docs").iterdir()} == {"reference"}
assert (reference / "archive/README.md").is_file()
required_evidence = {"deployment-safety-validation.md", "distribution-validation.md", "intranet-auth-validation.md",
                     "managed-gui-safety-validation.md", "performance-validation.md", "runtime-safety-completion-validation.md"}
assert required_evidence <= {p.name for p in (reference / "archive/validation").glob("*.md")}
assert {"guide-01-linux-server-setup.md", "guide-02-publish-package.md"} <= {p.name for p in (reference / "archive/guides").glob("*.md")}
assert (reference / "archive/validation/intranet-auth-load-results.json").is_file()
files = sorted(root.glob("*.md")) + sorted((root / "docs").rglob("*.md"))
errors = []
links = 0
for path in files:
    body = path.read_text(encoding="utf-8-sig")
    for match in re.finditer(r"\]\(([^)\s]+)\)", body):
        target = match.group(1).split("#", 1)[0]
        if not target or re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*:", target):
            continue
        resolved = (path.parent / unquote(target)).resolve()
        if not resolved.is_relative_to(root) or not resolved.exists():
            errors.append(f"{path.relative_to(root)} -> {target}")
        links += 1
assert not errors, "\n".join(errors)
print(f"PASS: 4 root documents, 9 current guides + index, {len(files)} Markdown files, {links} local links")

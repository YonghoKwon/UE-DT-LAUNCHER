"""Discover the workflow Python call/import closure without executing fixture or packaging code."""
import ast
from pathlib import Path
import re


def workflow_python(repo):
    repo = Path(repo)
    directory = repo / 'tools'
    pending = ['.github/workflows/build.yml', '.github/workflows/release.yml']
    shells, roots = set(), set()
    while pending:
        name = pending.pop()
        if name in shells:
            continue
        shells.add(name)
        text = (repo / name).read_text(encoding='utf-8-sig')
        for match in re.finditer(r'(?:tools|scripts)/[A-Za-z0-9_./-]+\.(?:py|ps1|sh)', text):
            child = match.group()
            if not (repo / child).is_file():
                continue
            if child.endswith('.py'):
                roots.add(Path(child).name)
            else:
                pending.append(child)
        if 'test*real_ue_fixture.py' in text:
            roots.update(p.name for p in directory.glob('test*real_ue_fixture.py'))
    closure, edges, pending = set(), {}, list(roots)
    while pending:
        name = pending.pop()
        if name in closure:
            continue
        closure.add(name)
        tree = ast.parse((directory / name).read_text(encoding='utf-8-sig'))
        children = set()
        for node in ast.walk(tree):
            modules = []
            if isinstance(node, ast.Import):
                modules = [alias.name.split('.')[0] for alias in node.names]
            elif isinstance(node, ast.ImportFrom) and node.module:
                modules = [node.module.split('.')[0]]
            elif isinstance(node, ast.Call) and node.args and isinstance(node.args[0], ast.Constant) and isinstance(node.args[0].value, str):
                function = node.func.attr if isinstance(node.func, ast.Attribute) else node.func.id if isinstance(node.func, ast.Name) else ''
                if function in ('__import__', 'import_module'):
                    modules = [node.args[0].value.split('.')[0]]
            for module in modules:
                if (directory / (module + '.py')).is_file():
                    children.add(module + '.py')
            if isinstance(node, ast.Constant) and isinstance(node.value, str) and node.value.endswith('.py'):
                child = Path(node.value).name
                if (directory / child).is_file():
                    children.add(child)
        edges[name] = sorted(children)
        pending.extend(children - closure)
    return {'roots': sorted(roots), 'closure': sorted(closure), 'shells': sorted(shells), 'edges': edges}


def implicit_text_calls(tree):
    """Reject locale-dependent file/process text, but preserve byte streams such as ZIP members and IPC."""
    failures = []
    binary_members = set()
    for block in ast.walk(tree):
        if not isinstance(block, ast.With):
            continue
        aliases = {item.optional_vars.id for item in block.items if isinstance(item.optional_vars, ast.Name)
                   and isinstance(item.context_expr, ast.Call) and isinstance(item.context_expr.func, ast.Attribute)
                   and isinstance(item.context_expr.func.value, ast.Name) and item.context_expr.func.value.id == 'zipfile'
                   and item.context_expr.func.attr == 'ZipFile'}
        for statement in block.body:
            for call in ast.walk(statement):
                if isinstance(call, ast.Call) and isinstance(call.func, ast.Attribute) and call.func.attr == 'open' and isinstance(call.func.value, ast.Name) and call.func.value.id in aliases:
                    binary_members.add(id(call))
    for node in ast.walk(tree):
        if not isinstance(node, ast.Call):
            continue
        name = node.func.attr if isinstance(node.func, ast.Attribute) else node.func.id if isinstance(node.func, ast.Name) else ''
        keywords = {keyword.arg: keyword.value for keyword in node.keywords}
        implicit_encoding = 'encoding' not in keywords or isinstance(keywords['encoding'], ast.Constant) and keywords['encoding'].value in (None, 'locale')
        if name in ('read_text', 'write_text') and implicit_encoding:
            failures.append((node.lineno, name))
        elif name == 'open' and implicit_encoding:
            # The closure contains one ZipFile.open(name) member stream; it has no encoding parameter.
            if id(node) in binary_members:
                continue
            index = 1 if isinstance(node.func, ast.Name) else 0
            mode = keywords.get('mode', node.args[index] if len(node.args) > index else None)
            if not (isinstance(mode, ast.Constant) and isinstance(mode.value, str) and 'b' in mode.value):
                failures.append((node.lineno, name))
        elif name in ('run', 'Popen', 'check_output') and implicit_encoding:
            if any(key in keywords and not (isinstance(keywords[key], ast.Constant) and keywords[key].value is False) for key in ('text', 'universal_newlines')):
                failures.append((node.lineno, 'subprocess-text'))
    return failures

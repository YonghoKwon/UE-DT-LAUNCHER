"""Keep CI logs, verification input and console output independent of Windows ACP."""
from ci_text import configure_console
configure_console()
import ast
import importlib.util
import io
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
from ci_python_contract import implicit_text_calls, workflow_python

TOOLS = Path(__file__).resolve().parent
REPO = TOOLS.parent
spec = importlib.util.spec_from_file_location('promotion_proof', TOOLS / 'test-release-promotion.py')
promotion = importlib.util.module_from_spec(spec)
spec.loader.exec_module(promotion)


class CiTextTests(unittest.TestCase):
    def test_promotion_logs_remain_lossless_when_default_file_encoding_is_cp1252(self):
        original = io.open
        def default_cp1252(file, mode='r', buffering=-1, encoding=None, errors=None, newline=None, closefd=True, opener=None):
            actual = encoding if 'b' in mode or encoding not in (None, 'locale') else 'cp1252'
            return original(file, mode, buffering, actual, errors, newline, closefd, opener)
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'command.log'
            value = SimpleNamespace(stdout='업로드 완료\n', stderr='권한 거부: 다음 조치 확인\n')
            with patch('io.text_encoding', lambda encoding, *args: encoding or 'locale'), patch('io.open', default_cp1252):
                # Confirm this harness really rejects the old implicit writer.
                with self.assertRaises(UnicodeEncodeError):
                    path.write_text(value.stdout + value.stderr)  # Deliberate negative control, not a tool I/O path.
                promotion.write_command_log(path, value)
            self.assertEqual(value.stdout + value.stderr, path.read_text(encoding='utf-8'))

    def test_provisioning_output_is_consumed_without_persisting_secrets(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'secret-command.log'
            promotion.write_command_log(path, SimpleNamespace(stdout='PRIVATE-TOKEN-MARKER', stderr='PRIVATE-ERROR-MARKER'), secret=True)
            self.assertEqual('secret output omitted\n', path.read_text(encoding='utf-8'))

    def test_cp1252_console_and_utf8_product_output_do_not_fail_or_lose_korean(self):
        environment = os.environ.copy()
        environment.pop('PYTHONUTF8', None)
        environment['PYTHONIOENCODING'] = 'cp1252:strict'
        environment['PYTHONPATH'] = str(TOOLS)
        source = '''import sys,subprocess
assert sys.flags.utf8_mode == 0
assert sys.stdout.encoding.lower() == 'cp1252'
from ci_text import configure_console
configure_console()
assert sys.stdout.encoding.lower().replace('-', '') == 'utf8'
message = "\\uad8c\\ud55c \\uac70\\ubd80"
child = subprocess.run([sys.executable, '-c', "import sys;sys.stdout.buffer.write('\\uad8c\\ud55c \\uac70\\ubd80'.encode('utf-8'))"], capture_output=True, text=True, encoding='utf-8')
assert child.returncode == 0 and child.stdout == message
print(child.stdout)
print(message, file=sys.stderr)
'''
        result = subprocess.run([sys.executable, '-X', 'utf8=0', '-c', source], capture_output=True, env=environment)
        self.assertEqual(0, result.returncode, result.stderr.decode('utf-8', errors='replace'))
        self.assertEqual('권한 거부\n', result.stdout.decode('utf-8').replace('\r\n', '\n'))
        self.assertEqual('권한 거부\n', result.stderr.decode('utf-8').replace('\r\n', '\n'))

    def test_workflow_closure_has_explicit_text_encodings(self):
        graph = workflow_python(REPO)
        self.assertIn('test-release-promotion.py', graph['roots'])
        self.assertIn('verify-rpm-payload.py', graph['closure'])
        for name in graph['closure']:
            source = (TOOLS / name).read_text(encoding='utf-8-sig')
            tree = ast.parse(source)
            failures = implicit_text_calls(tree)
            # Only the intentional old-behavior negative control above is exempt.
            if name == 'test-ci-text.py':
                controls = [node.lineno for node in ast.walk(tree) if isinstance(node, ast.Call) and ast.get_source_segment(source, node) == 'path.write_text(value.stdout + value.stderr)']
                self.assertEqual(1, len(controls))
                failures = [failure for failure in failures if failure[0] not in controls]
            self.assertEqual([], failures, name)

    def test_workflow_printing_entrypoints_configure_utf8_streams(self):
        graph = workflow_python(REPO)
        for name in graph['closure']:
            tree = ast.parse((TOOLS / name).read_text(encoding='utf-8-sig'))
            has_print = any(isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id == 'print' for node in ast.walk(tree))
            if name in graph['roots'] or has_print:
                self.assertTrue(any(isinstance(node, ast.Expr) and isinstance(node.value, ast.Call) and isinstance(node.value.func, ast.Name) and node.value.func.id == 'configure_console' for node in tree.body), name)

    def test_inline_workflow_and_shell_python_declares_file_encodings(self):
        for name in workflow_python(REPO)['shells']:
            source = (REPO / name).read_text(encoding='utf-8-sig')
            for snippet in re.findall(r"python[0-9]*\s+-c\s+'([^']+)'", source):
                self.assertEqual([], implicit_text_calls(ast.parse(snippet)), name)


if __name__ == '__main__':
    unittest.main()

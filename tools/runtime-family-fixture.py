"""Harmless ordinary-descendant fixture. Writes only inside a caller-provided test directory."""
import argparse
import os
from pathlib import Path
import subprocess
import sys
import time

parser = argparse.ArgumentParser()
parser.add_argument("mode", choices=["direct", "root-exits", "double-fork", "child"])
parser.add_argument("root")
args = parser.parse_args()
root = Path(args.root)
if args.mode == "root-exits":
    subprocess.Popen([sys.executable, __file__, "child", str(root)], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                     creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    sys.exit(0)
if args.mode == "double-fork":
    if os.name == "nt":
        raise RuntimeError("double-fork requires Linux")
    if os.fork():
        os._exit(0)
    os.setsid()
    if os.fork():
        os._exit(0)
(root / "child-started").write_text(str(os.getpid()))
time.sleep(2)
(root / "child-completed").write_text("completed")

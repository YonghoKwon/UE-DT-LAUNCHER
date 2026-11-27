#!/usr/bin/env python3
"""User crontab entry point and supervisor for the owned server child."""

import fcntl
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import time

CONTROL = Path("/dt/.dt-setup")
SOCKET = str(CONTROL / "cron.sock")
LOCK = CONTROL / "cron.lock"
COMMAND = [sys.executable, "-B", "/dt/tools/dt_setup_guard.py"]


def request(message, timeout):
    with socket.socket(socket.AF_UNIX) as client:
        client.settimeout(timeout)
        try:
            client.connect(SOCKET)
        except (FileNotFoundError, ConnectionRefusedError):
            return None
        client.sendall(message)
        return client.recv(16)


def interrupted(signum, frame):
    raise SystemExit(128 + signum)


def supervise():
    signal.signal(signal.SIGTERM, interrupted)
    with LOCK.open("a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return 0
        # A stale socket can only be removed after claiming the supervisor lock.
        path = Path(SOCKET)
        if path.is_socket():
            path.unlink()
        with socket.socket(socket.AF_UNIX) as server:
            server.bind(SOCKET)
            child = None
            try:
                server.listen(4)
                server.settimeout(0.5)
                child = subprocess.Popen(COMMAND)
                while child.poll() is None:
                    try:
                        client, _ = server.accept()
                    except socket.timeout:
                        continue
                    with client:
                        client.settimeout(1)
                        try:
                            if client.recv(16) == b"stop":
                                stop_child(child)
                                client.sendall(b"stopped")
                                return 0
                            client.sendall(b"running" if child.poll() is None else b"exited")
                        except (OSError, socket.timeout):
                            pass
                return child.returncode
            finally:
                if child is not None:
                    stop_child(child)
                path.unlink()


def stop_child(child):
    if child.poll() is None:
        # This Popen object exclusively owns and reaps the child.
        child.terminate()
        try:
            child.wait(timeout=40)
        except subprocess.TimeoutExpired:
            # Escalation applies only to the owned and still unreaped child.
            child.kill()
            child.wait()


def main():
    os.umask(0o077)
    if len(sys.argv) != 2:
        return 2
    if sys.argv[1] == "supervise":
        return supervise()
    operation = sys.argv[1]
    if operation == "boot":
        # Serialize boot against the same directory lock as install and update.
        fd = os.open("/dt", os.O_RDONLY | os.O_DIRECTORY)
        try:
            fcntl.flock(fd, fcntl.LOCK_EX)
            if (CONTROL / "pending.json").exists():
                print("Run dt-setup.sh install to recover the pending transaction.", file=sys.stderr)
                return 1
            return start()
        finally:
            os.close(fd)
    if operation == "status":
        return 0 if request(b"status", 2) == b"running" else 1
    if operation == "stop":
        result = request(b"stop", 45)
        if result is None:
            # A launching supervisor may already hold the lock before bind/listen.
            if LOCK.exists():
                with LOCK.open("r") as lock:
                    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        elif result == b"stopped":
            with LOCK.open("r") as lock:
                fcntl.flock(lock, fcntl.LOCK_EX)
        return 0 if result in (None, b"stopped") else 1
    if operation == "start":
        return start()
    return 2


def start():
    if request(b"status", 2) == b"running":
        return 0
    log = CONTROL / "cron.log"
    with log.open("ab") as stream:
        child = subprocess.Popen([sys.executable, "-B", __file__, "supervise"],
                                 stdin=subprocess.DEVNULL, stdout=stream,
                                 stderr=stream, start_new_session=True)
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        if request(b"status", 2) == b"running":
            return 0
        if child.poll() is not None and child.returncode != 0:
            break
        time.sleep(0.1)
    return 1


if __name__ == "__main__":
    sys.exit(main())

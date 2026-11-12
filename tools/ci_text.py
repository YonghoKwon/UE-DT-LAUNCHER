"""Locale-independent console output for CI tools; files/processes declare their encodings at each call."""
import sys


def configure_console():
    """Configure Python streams only; never change the host console code page or verification strings."""
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, 'reconfigure', None)
        if reconfigure is not None:
            reconfigure(encoding='utf-8', errors='backslashreplace')

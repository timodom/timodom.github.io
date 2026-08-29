"""The safety guarantee, asserted rather than promised.

The audit's claim is that this package cannot place an order because the
capability is absent -- not because a flag is set. A flag is one bad merge from
being wrong; an import that does not exist cannot be. These tests fail loudly if
someone later adds one.
"""
import ast
import pathlib

PKG = pathlib.Path(__file__).resolve().parent.parent / "signalrouter"

# Anything that could reach a broker, a pipe, or the network.
FORBIDDEN_MODULES = {
    "socket", "ssl", "http", "http.client", "urllib", "urllib.request",
    "requests", "httpx", "aiohttp", "websockets", "subprocess", "ctypes",
    "win32file", "win32pipe", "win32api", "pywintypes", "order_router",
    "clr", "pythonnet",
}

FORBIDDEN_CALLS = {"place_bracket_entry", "close_all", "move_tp", "cancel_orders",
                   "send_command", "OpenOrder", "SubmitOrder"}


def _sources():
    return sorted(PKG.rglob("*.py"))


def test_package_has_source_files():
    assert _sources(), "no sources found -- the scan below would vacuously pass"


def test_no_network_pipe_or_broker_imports():
    offenders = []
    for path in _sources():
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
        for node in ast.walk(tree):
            if isinstance(node, ast.Import):
                for a in node.names:
                    if a.name.split(".")[0] in FORBIDDEN_MODULES:
                        offenders.append(f"{path.name}: import {a.name}")
            elif isinstance(node, ast.ImportFrom):
                if node.module and node.module.split(".")[0] in FORBIDDEN_MODULES:
                    offenders.append(f"{path.name}: from {node.module}")
    assert not offenders, "signal router gained a way to reach out: " + "; ".join(offenders)


def test_no_order_placement_calls():
    offenders = []
    for path in _sources():
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
        for node in ast.walk(tree):
            if isinstance(node, ast.Call):
                fn = node.func
                name = getattr(fn, "attr", None) or getattr(fn, "id", None)
                if name in FORBIDDEN_CALLS:
                    offenders.append(f"{path.name}: {name}()")
    assert not offenders, "order-placement call in the signal router: " + "; ".join(offenders)


def test_no_file_writes_anywhere_in_the_package():
    """Phase 1 is parse-only. Nothing here opens a file at all, let alone for
    writing -- so it cannot truncate a file a live executor depends on."""
    offenders = []
    for path in _sources():
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
        for node in ast.walk(tree):
            if isinstance(node, ast.Call):
                name = getattr(node.func, "attr", None) or getattr(node.func, "id", None)
                if name in {"open", "write_text", "write_bytes"}:
                    offenders.append(f"{path.name}: {name}()")
    assert not offenders, "signal router touched the filesystem: " + "; ".join(offenders)

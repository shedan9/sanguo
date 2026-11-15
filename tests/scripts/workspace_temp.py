"""Writable test fixtures on Windows hosts that restrict 0o700 temp directories."""
from contextlib import contextmanager
from pathlib import Path
import shutil
import uuid


@contextmanager
def workspace_directory():
    root = Path(__file__).resolve().parents[2] / "build" / "test-temp"
    root.mkdir(parents=True, exist_ok=True)
    directory = root / ("fixture-" + uuid.uuid4().hex)
    directory.mkdir(mode=0o777)
    try:
        yield directory
    finally:
        if directory.resolve().parent != root.resolve():
            raise RuntimeError("Test fixture escaped the workspace temp directory")
        shutil.rmtree(directory)

"""Build the standalone Windows executable.

    python build.py

Produces dist/Jade Audio Control.exe - a single file with Python, Tk and
hidapi inside, so it runs on a machine with no Python installed.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
NAME = "Jade Audio Control"


def main() -> int:
    try:
        import PyInstaller  # noqa: F401
    except ImportError:
        print("PyInstaller is missing. Install it with:")
        print(f"    {sys.executable} -m pip install pyinstaller")
        return 1

    for stale in (ROOT / "build", ROOT / f"{NAME}.spec"):
        if stale.is_dir():
            shutil.rmtree(stale)
        elif stale.exists():
            stale.unlink()

    cmd = [
        sys.executable,
        "-m",
        "PyInstaller",
        "--noconfirm",
        "--clean",
        "--onefile",
        "--windowed",
        "--name",
        NAME,
        "--icon",
        "app.ico",
        # app.ico is read at runtime for the window icon, so ship it inside.
        "--add-data",
        "app.ico;.",
        "--hidden-import",
        "hid",
        "--hidden-import",
        "requests",
        # Pillow decodes the login CAPTCHA, which Tk cannot read on its own.
        "--hidden-import",
        "PIL.ImageTk",
        # Keep the binary small: none of these are used.
        *sum(
            (["--exclude-module", m] for m in ("numpy", "lxml", "pytest", "pandas", "matplotlib", "scipy")),
            [],
        ),
        "run.py",
    ]
    result = subprocess.run(cmd, cwd=ROOT)
    if result.returncode != 0:
        return result.returncode

    exe = ROOT / "dist" / f"{NAME}.exe"
    print(f"\nBuilt {exe}  ({exe.stat().st_size / 1_048_576:.1f} MB)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

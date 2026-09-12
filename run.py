"""Entry point: python run.py

Missing dependencies are reported in a dialog rather than a traceback, because
this is normally started from the .bat launcher with no console attached.
"""

from __future__ import annotations

import sys


def _fatal(title: str, message: str) -> int:
    print(f"{title}: {message}", file=sys.stderr)
    try:
        import tkinter as tk
        from tkinter import messagebox

        root = tk.Tk()
        root.withdraw()
        messagebox.showerror(title, message)
        root.destroy()
    except Exception:
        pass
    return 1


def main() -> int:
    try:
        import tkinter  # noqa: F401
    except ImportError:
        return _fatal(
            "Jade Audio Control",
            "This Python has no Tk support.\n\n"
            "Install Python from python.org with the 'tcl/tk' option enabled.",
        )

    try:
        import hid  # noqa: F401
    except ImportError:
        return _fatal(
            "Jade Audio Control",
            "The 'hidapi' package is missing.\n\n"
            f"Install it with:\n    {sys.executable} -m pip install hidapi",
        )

    from jadeaudio.app import main as app_main

    return app_main()


if __name__ == "__main__":
    raise SystemExit(main())

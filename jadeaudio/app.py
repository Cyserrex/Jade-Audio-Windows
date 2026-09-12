"""Jade Audio Control - a Windows desktop app for FiiO / JadeAudio USB dongles.

All HID traffic happens on one worker thread; the Tk thread only ever touches
widgets. Results come back through `root.after`.
"""

from __future__ import annotations

import json
import math
import queue
import sys
import threading
import time
import tkinter as tk
import traceback
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

from . import capabilities as caps_mod
from . import cloud as cloud_mod
from .cloud_dialog import CloudDialog
from . import eq as eqmath
from . import protocol as p
from .device import Device, NotFound

APP_NAME = "Jade Audio Control"

BG = "#14161a"
PANEL = "#1c1f26"
FG = "#e6e8eb"
MUTED = "#8b929e"
ACCENT = "#3ea6a0"
GRID = "#2a2e37"
BAND_COLORS = ("#e8705a", "#e8b05a", "#7bc86c", "#5aa8e8", "#b07be8")

FREQ_MIN, FREQ_MAX = 20.0, 20000.0
DB_SPAN = 15.0

#: The firmware needs a moment after a preset switch before its band registers
#: read back the new preset, and a shorter one between consecutive band writes.
PRESET_SETTLE = 0.8
BAND_SETTLE = 0.25


class Worker:
    """Serialises device calls onto a background thread."""

    def __init__(self, root: tk.Misc):
        self.root = root
        self._q: queue.Queue = queue.Queue()
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()

    def submit(self, fn, on_done=None, on_error=None) -> None:
        self._q.put((fn, on_done, on_error))

    def _run(self) -> None:
        while True:
            fn, on_done, on_error = self._q.get()
            try:
                result = fn()
            except Exception as exc:
                if on_error is not None:
                    self.root.after(0, on_error, exc)
                continue
            if on_done is not None:
                self.root.after(0, on_done, result)


class CurveCanvas(tk.Canvas):
    """Log-frequency EQ plot with draggable band handles."""

    def __init__(self, master, on_drag, **kw):
        super().__init__(master, bg=PANEL, highlightthickness=0, **kw)
        self.on_drag = on_drag
        self.bands: list[dict] = []
        self.gain_range = (-12.0, 12.0)
        self._dragging: int | None = None
        self._freqs = eqmath.log_freqs(FREQ_MIN, FREQ_MAX, 260)
        self.bind("<Configure>", lambda _e: self.redraw())
        self.bind("<Button-1>", self._press)
        self.bind("<B1-Motion>", self._motion)
        self.bind("<ButtonRelease-1>", lambda _e: setattr(self, "_dragging", None))

    # -- coordinate helpers --------------------------------------------------

    def _x(self, freq: float) -> float:
        lo, hi = math.log10(FREQ_MIN), math.log10(FREQ_MAX)
        f = min(max(freq, FREQ_MIN), FREQ_MAX)
        return (math.log10(f) - lo) / (hi - lo) * self.winfo_width()

    def _freq(self, x: float) -> float:
        lo, hi = math.log10(FREQ_MIN), math.log10(FREQ_MAX)
        t = min(max(x / max(self.winfo_width(), 1), 0.0), 1.0)
        return math.pow(10.0, lo + t * (hi - lo))

    def _y(self, db: float) -> float:
        h = self.winfo_height()
        return h / 2 - (db / DB_SPAN) * (h / 2 - 10)

    def _db(self, y: float) -> float:
        h = self.winfo_height()
        return (h / 2 - y) / max(h / 2 - 10, 1) * DB_SPAN

    # -- interaction ---------------------------------------------------------

    def _nearest(self, x: float, y: float) -> int | None:
        best, best_d = None, 1e9
        for i, band in enumerate(self.bands):
            d = math.hypot(self._x(band["frequency"]) - x, self._y(band["gain"]) - y)
            if d < best_d:
                best, best_d = i, d
        return best if best_d <= 28 else None

    def _press(self, event) -> None:
        self._dragging = self._nearest(event.x, event.y)

    def _motion(self, event) -> None:
        if self._dragging is None:
            return
        lo, hi = self.gain_range
        freq = int(round(min(max(self._freq(event.x), FREQ_MIN), FREQ_MAX)))
        gain = round(min(max(self._db(event.y), lo), hi), 1)
        self.on_drag(self._dragging, freq, gain)

    # -- drawing -------------------------------------------------------------

    def redraw(self) -> None:
        self.delete("all")
        w, h = self.winfo_width(), self.winfo_height()
        if w < 10 or h < 10:
            return

        for db in (-12, -6, 0, 6, 12):
            y = self._y(db)
            self.create_line(0, y, w, y, fill=ACCENT if db == 0 else GRID)
            self.create_text(6, y - 8, text=f"{db:+d}", fill=MUTED, anchor="w", font=("Segoe UI", 7))
        for f in (20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000):
            x = self._x(f)
            self.create_line(x, 0, x, h, fill=GRID)
            label = f"{f // 1000}k" if f >= 1000 else str(f)
            self.create_text(x + 3, h - 10, text=label, fill=MUTED, anchor="w", font=("Segoe UI", 7))

        if not self.bands:
            return

        for i, band in enumerate(self.bands):
            pts = []
            for f, db in zip(self._freqs, eqmath.band_response_db(band, self._freqs)):
                pts += [self._x(f), self._y(db)]
            self.create_line(*pts, fill=BAND_COLORS[i % len(BAND_COLORS)], width=1, smooth=True)

        pts = []
        for f, db in zip(self._freqs, eqmath.total_response_db(self.bands, self._freqs)):
            pts += [self._x(f), self._y(db)]
        self.create_line(*pts, fill=FG, width=2, smooth=True)

        for i, band in enumerate(self.bands):
            x, y = self._x(band["frequency"]), self._y(band["gain"])
            color = BAND_COLORS[i % len(BAND_COLORS)]
            self.create_oval(x - 9, y - 9, x + 9, y + 9, fill=color, outline=PANEL, width=2)
            self.create_text(x, y, text=str(i + 1), fill="#14161a", font=("Segoe UI", 8, "bold"))


class App(ttk.Frame):
    def __init__(self, root: tk.Tk):
        super().__init__(root, padding=12)
        self.root = root
        self.pack(fill="both", expand=True)

        self.dev: Device | None = None
        self.caps = caps_mod.Capabilities()
        self.bands: list[dict] = []
        self.worker = Worker(root)
        self._suspend = False
        self._pending_writes: dict[str, str] = {}

        self._build()
        self.after(200, self.connect)

    # -- layout --------------------------------------------------------------

    def _build(self) -> None:
        self.status_var = tk.StringVar(value="")
        ttk.Label(self, textvariable=self.status_var, style="Status.TLabel", anchor="w").pack(
            side="bottom", fill="x", pady=(8, 0)
        )

        header = ttk.Frame(self)
        header.pack(fill="x", pady=(0, 10))
        self.title_var = tk.StringVar(value="Searching for device...")
        self.sub_var = tk.StringVar(value="")
        ttk.Label(header, textvariable=self.title_var, style="Title.TLabel").pack(anchor="w")
        ttk.Label(header, textvariable=self.sub_var, style="Muted.TLabel").pack(anchor="w")
        ttk.Button(header, text="Reconnect", command=self.connect).pack(side="right")

        # Pack the fixed rows from the bottom up so that the curve is the only
        # thing that gives way when the window is short.
        self.extras = ttk.Frame(self)
        self.extras.pack(side="bottom", fill="x", pady=(0, 8))

        self.bands_frame = ttk.Frame(self)
        self.bands_frame.pack(side="bottom", fill="x", pady=(0, 8))

        preset_row = ttk.Frame(self)
        preset_row.pack(side="bottom", fill="x", pady=(0, 8))
        ttk.Label(preset_row, text="Preset").pack(side="left", padx=(0, 6))
        self.preset_var = tk.StringVar()
        self.preset_box = ttk.Combobox(preset_row, textvariable=self.preset_var, state="readonly", width=14)
        self.preset_box.pack(side="left")
        self.preset_box.bind("<<ComboboxSelected>>", self._on_preset)
        for text, cmd in (
            ("Get from cloud...", self.open_cloud),
            ("Export preset...", self.export_preset),
            ("Import preset...", self.import_preset),
            ("Backup all...", self.backup_all),
            ("Restore backup...", self.restore_backup),
        ):
            ttk.Button(preset_row, text=text, command=cmd).pack(side="left", padx=(6, 0))
        ttk.Label(preset_row, text="Edits apply and persist immediately.", style="Muted.TLabel").pack(
            side="left", padx=(12, 0)
        )

        self.curve = CurveCanvas(self, self._on_curve_drag, height=220)
        self.curve.pack(fill="both", expand=True, pady=(0, 10))

    def _build_band_rows(self) -> None:
        for child in self.bands_frame.winfo_children():
            child.destroy()
        self.band_widgets = []
        if not self.bands:
            return

        headers = ("", "Frequency (Hz)", "Gain (dB)", "Q", "Type")
        for col, text in enumerate(headers):
            ttk.Label(self.bands_frame, text=text, style="Muted.TLabel").grid(
                row=0, column=col, sticky="w", padx=4, pady=(0, 2)
            )
        self.bands_frame.columnconfigure(1, weight=1)
        self.bands_frame.columnconfigure(2, weight=2)
        self.bands_frame.columnconfigure(3, weight=1)

        type_names = {int(t): t.name.replace("_", " ").title() for t in p.FilterType}
        allowed = [type_names[int(t)] for t in self.caps.filter_types]

        for i, band in enumerate(self.bands):
            row = i + 1
            tk.Label(
                self.bands_frame,
                text=f" {i + 1} ",
                bg=BAND_COLORS[i % len(BAND_COLORS)],
                fg="#14161a",
                font=("Segoe UI", 8, "bold"),
            ).grid(row=row, column=0, padx=4, pady=2)

            freq = tk.StringVar(value=str(band["frequency"]))
            gain = tk.DoubleVar(value=band["gain"])
            qval = tk.StringVar(value=f"{band['q']:.2f}")
            ftype = tk.StringVar(value=type_names.get(band["filter_type"], "Peak"))

            ttk.Entry(self.bands_frame, textvariable=freq, width=8).grid(row=row, column=1, sticky="w", padx=4)
            gain_box = ttk.Frame(self.bands_frame)
            gain_box.grid(row=row, column=2, sticky="ew", padx=4)
            lo, hi = self.caps.gain_range
            scale = ttk.Scale(gain_box, from_=lo, to=hi, variable=gain, orient="horizontal")
            scale.pack(side="left", fill="x", expand=True)
            gain_label = ttk.Label(gain_box, width=6, style="Muted.TLabel")
            gain_label.pack(side="left", padx=(6, 0))
            ttk.Entry(self.bands_frame, textvariable=qval, width=6).grid(row=row, column=3, sticky="w", padx=4)
            combo = ttk.Combobox(
                self.bands_frame, textvariable=ftype, state="readonly", values=allowed, width=11
            )
            combo.grid(row=row, column=4, sticky="w", padx=4)

            widgets = {"freq": freq, "gain": gain, "q": qval, "type": ftype, "label": gain_label}
            self.band_widgets.append(widgets)
            gain_label.configure(text=f"{band['gain']:+.1f}")

            freq.trace_add("write", lambda *_a, i=i: self._on_field(i))
            qval.trace_add("write", lambda *_a, i=i: self._on_field(i))
            gain.trace_add("write", lambda *_a, i=i: self._on_field(i))
            combo.bind("<<ComboboxSelected>>", lambda _e, i=i: self._on_field(i))

    def _build_extras(self) -> None:
        for child in self.extras.winfo_children():
            child.destroy()
        col = 0

        def slider(label, lo, hi, value, setter, fmt="{:.0f}"):
            nonlocal col
            box = ttk.Frame(self.extras)
            box.grid(row=0, column=col, sticky="ew", padx=(0, 16))
            self.extras.columnconfigure(col, weight=1)
            col += 1
            ttk.Label(box, text=label, style="Muted.TLabel").pack(anchor="w")
            row = ttk.Frame(box)
            row.pack(fill="x")
            var = tk.DoubleVar(value=value)
            ttk.Scale(row, from_=lo, to=hi, variable=var, orient="horizontal").pack(
                side="left", fill="x", expand=True
            )
            readout = ttk.Label(row, width=7, style="Muted.TLabel", text=fmt.format(value))
            readout.pack(side="left", padx=(6, 0))

            def changed(*_a):
                readout.configure(text=fmt.format(var.get()))
                if not self._suspend:
                    self._debounced(label, lambda v=var.get(): setter(v))

            var.trace_add("write", changed)
            return var

        if self.caps.has(p.Reg.GLOBAL_GAIN):
            lo, hi = self.caps.gain_range
            self.preamp_var = slider(
                "EQ global gain (dB)", lo, hi, self.state_preamp, self._set_preamp, "{:+.1f}"
            )
        if self.caps.has(p.Reg.VOL_OUTPUT):
            self.volume_var = slider(
                "Output volume", 0, self.caps.volume_max, self.state_volume, self._set_volume
            )
        if self.caps.has(p.Reg.FILTER_MODE):
            box = ttk.Frame(self.extras)
            box.grid(row=0, column=col, sticky="ew", padx=(0, 16))
            col += 1
            ttk.Label(box, text="DAC filter", style="Muted.TLabel").pack(anchor="w")
            names = [m.name.replace("_", " ").title() for m in p.FilterMode]
            var = tk.StringVar(value=names[self.state_filter_mode])
            combo = ttk.Combobox(box, textvariable=var, state="readonly", values=names, width=16)
            combo.pack(fill="x")
            combo.bind(
                "<<ComboboxSelected>>",
                lambda _e: self._run(lambda: self.dev.set_filter_mode(names.index(var.get()))),
            )

    # -- device lifecycle ----------------------------------------------------

    def connect(self) -> None:
        self.status_var.set("Connecting...")
        if self.dev is not None:
            self.dev.close()
            self.dev = None

        def work():
            dev = Device()
            caps = caps_mod.probe(dev)
            state = {
                "firmware": dev.firmware_version if caps.has(p.Reg.FIRMWARE_VERSION) else "?",
                "preset": dev.get_eq_preset() if caps.has(p.Reg.PEQ_PRE) else 0,
                "preamp": dev.get_eq_global_gain() if caps.has(p.Reg.GLOBAL_GAIN) else 0.0,
                "volume": dev.request(p.get_volume_output()).payload[0]
                if caps.has(p.Reg.VOL_OUTPUT)
                else 0,
                "filter_mode": dev.get_filter_mode() if caps.has(p.Reg.FILTER_MODE) else 0,
                "bands": [dev.get_eq_band(i) for i in range(caps.eq_count)],
            }
            return dev, caps, state

        self.worker.submit(work, self._connected, self._connect_failed)

    def _connected(self, result) -> None:
        self.dev, self.caps, state = result
        self.state_preamp = state["preamp"]
        self.state_volume = state["volume"]
        self.state_filter_mode = state["filter_mode"]
        self.bands = state["bands"]

        self.title_var.set(self.dev.product_name or "Unknown device")
        info = self.dev.info
        self.sub_var.set(
            f"Firmware {state['firmware']}   ·   "
            f"VID {info['vendor_id']:#06x} PID {info['product_id']:#06x}   ·   "
            f"report id {self.dev.report_id}"
        )

        self._suspend = True
        names = [name for name, _ in self.caps.eq_presets]
        self.preset_box.configure(values=names)
        current = next((n for n, v in self.caps.eq_presets if v == state["preset"]), names[0])
        self.preset_var.set(current)
        self._build_band_rows()
        self._build_extras()
        self._suspend = False

        self.curve.gain_range = self.caps.gain_range
        self._refresh_curve()
        supported = ", ".join(sorted(p.Reg(r).name for r in self.caps.supported))
        self.status_var.set(f"Connected. Supported registers: {supported}")

    def _connect_failed(self, exc: Exception) -> None:
        self.dev = None
        if isinstance(exc, NotFound):
            self.title_var.set("No device found")
            self.sub_var.set("Plug in a FiiO / JadeAudio dongle and press Reconnect.")
        else:
            self.title_var.set("Connection failed")
            self.sub_var.set(str(exc))
        self.status_var.set("")

    def _run(self, fn, on_done=None) -> None:
        if self.dev is None:
            return
        self.worker.submit(fn, on_done, self._device_error)

    def _device_error(self, exc: Exception) -> None:
        self.status_var.set(f"{type(exc).__name__}: {exc}")
        if isinstance(exc, OSError):
            # The dongle re-enumerates now and then; pick it back up.
            self.status_var.set("Device dropped off the bus, reconnecting...")
            self.after(1500, self.connect)

    def _debounced(self, key: str, fn, delay: int = 120) -> None:
        """Coalesce rapid slider movement into one write."""
        existing = self._pending_writes.pop(key, None)
        if existing is not None:
            self.after_cancel(existing)
        self._pending_writes[key] = self.after(delay, lambda: self._run(fn))

    # -- edits ---------------------------------------------------------------

    def _refresh_curve(self) -> None:
        self.curve.bands = self.bands
        self.curve.redraw()

    def _on_curve_drag(self, index: int, freq: int, gain: float) -> None:
        self.bands[index]["frequency"] = freq
        self.bands[index]["gain"] = gain
        self._suspend = True
        self.band_widgets[index]["freq"].set(str(freq))
        self.band_widgets[index]["gain"].set(gain)
        self.band_widgets[index]["label"].configure(text=f"{gain:+.1f}")
        self._suspend = False
        self._refresh_curve()
        self._push_band(index)

    def _on_field(self, index: int) -> None:
        if self._suspend:
            return
        w = self.band_widgets[index]
        try:
            freq = int(float(w["freq"].get()))
            q = float(w["q"].get())
        except ValueError:
            return
        gain = round(w["gain"].get(), 1)
        name = w["type"].get().upper().replace(" ", "_")
        try:
            ftype = int(p.FilterType[name])
        except KeyError:
            ftype = 0

        lo, hi = self.caps.gain_range
        qlo, qhi = self.caps.q_range
        band = self.bands[index]
        band.update(
            frequency=min(max(freq, int(FREQ_MIN)), int(FREQ_MAX)),
            gain=min(max(gain, lo), hi),
            q=min(max(q, qlo), qhi),
            filter_type=ftype,
        )
        w["label"].configure(text=f"{band['gain']:+.1f}")
        self._refresh_curve()
        self._push_band(index)

    def _push_band(self, index: int) -> None:
        band = self.bands[index]
        self._debounced(
            f"band{index}",
            lambda b=dict(band), i=index: self.dev.set_eq_band(
                i, b["frequency"], b["gain"], b["q"], b["filter_type"]
            ),
        )

    def _set_preamp(self, db: float) -> None:
        self.dev.set_eq_global_gain(round(db, 1))

    def _set_volume(self, value: float) -> None:
        self.dev.send(p.set_volume_output(int(round(value))))

    def _on_preset(self, _event=None) -> None:
        if self._suspend or self.dev is None:
            return
        value = dict(self.caps.eq_presets).get(self.preset_var.get())
        if value is None:
            return

        def work():
            self.dev.set_eq_preset(value)
            time.sleep(PRESET_SETTLE)
            return [self.dev.get_eq_band(i) for i in range(self.caps.eq_count)]

        def done(bands):
            self.bands = bands
            self._suspend = True
            self._build_band_rows()
            self._suspend = False
            self._refresh_curve()
            self.status_var.set(f"Preset '{self.preset_var.get()}' loaded from device.")

        self._run(work, done)

    # -- actions -------------------------------------------------------------

    def open_cloud(self) -> None:
        if self.dev is None:
            return
        dialog = CloudDialog(
            self, {"BG": BG, "PANEL": PANEL, "FG": FG, "MUTED": MUTED, "ACCENT": ACCENT}
        )
        set_icon_for(dialog)

    def apply_cloud_preset(self, preset) -> None:
        """Write a downloaded preset into the preset currently selected."""
        count = self.caps.eq_count
        bands = [dict(b) for b in preset.bands[:count]]
        if not bands:
            messagebox.showerror(APP_NAME, "That preset has no bands in it.")
            return
        while len(bands) < count:  # leave any spare band flat
            bands.append(
                {"index": len(bands), "frequency": 1000, "gain": 0.0, "q": 0.7, "filter_type": 0}
            )
        for i, band in enumerate(bands):
            band["index"] = i

        note = ""
        if len(preset.bands) > count:
            note = (
                f"\n\nIt has {len(preset.bands)} bands and this device has {count}; "
                "the extra ones are dropped."
            )
        if not messagebox.askyesno(
            APP_NAME,
            f"Write '{preset.name}' into the '{self.preset_var.get()}' preset?\n\n"
            f"That overwrites the bands currently in it.{note}",
        ):
            return

        gain = preset.global_gain
        lo, hi = self.caps.gain_range
        gain = min(max(gain, lo), hi)

        def work():
            for i, band in enumerate(bands):
                self.dev.set_eq_band(
                    i, band["frequency"], band["gain"], band["q"], band["filter_type"]
                )
                time.sleep(BAND_SETTLE)
            if self.caps.has(p.Reg.GLOBAL_GAIN):
                self.dev.set_eq_global_gain(round(gain, 1))
            time.sleep(0.3)
            return [self.dev.get_eq_band(i) for i in range(self.caps.eq_count)]

        def done(read_back):
            self.bands = read_back
            self._suspend = True
            self._build_band_rows()
            self._suspend = False
            self._refresh_curve()
            if self.caps.has(p.Reg.GLOBAL_GAIN) and hasattr(self, "preamp_var"):
                self._suspend = True
                self.preamp_var.set(gain)
                self._suspend = False
            self.status_var.set(f"Applied '{preset.name}' to {self.preset_var.get()}.")

        self._run(work, done)

    def _read_all_presets(self) -> dict:
        """Snapshot every preset by selecting each one in turn."""
        original = self.dev.get_eq_preset()
        snapshot = {}
        for name, value in self.caps.eq_presets:
            self.dev.set_eq_preset(value)
            time.sleep(PRESET_SETTLE)
            snapshot[name] = [self.dev.get_eq_band(i) for i in range(self.caps.eq_count)]
        self.dev.set_eq_preset(original)
        time.sleep(PRESET_SETTLE)
        return snapshot

    def backup_all(self) -> None:
        path = filedialog.asksaveasfilename(
            defaultextension=".json",
            filetypes=[("Backup", "*.json")],
            initialfile="ja11-presets-backup.json",
        )
        if not path:
            return
        self.status_var.set("Reading every preset from the device...")

        def work():
            snapshot = self._read_all_presets()
            Path(path).write_text(
                json.dumps(
                    {"device": self.dev.product_name, "presets": snapshot}, indent=2
                ),
                encoding="utf-8",
            )
            return path

        self._run(work, lambda r: self.status_var.set(f"Backed up every preset to {r}"))

    def restore_backup(self) -> None:
        path = filedialog.askopenfilename(filetypes=[("Backup", "*.json")])
        if not path:
            return
        data = json.loads(Path(path).read_text(encoding="utf-8"))
        presets = data.get("presets") or {}
        if not presets:
            messagebox.showerror(APP_NAME, "That file has no presets in it.")
            return
        if not messagebox.askyesno(
            APP_NAME, f"Overwrite {len(presets)} presets on the device with this backup?"
        ):
            return
        self.status_var.set("Writing presets back to the device...")

        def work():
            by_name = dict(self.caps.eq_presets)
            for name, bands in presets.items():
                if name not in by_name:
                    continue
                self.dev.set_eq_preset(by_name[name])
                time.sleep(PRESET_SETTLE)
                for i, band in enumerate(bands[: self.caps.eq_count]):
                    self.dev.set_eq_band(
                        i, band["frequency"], band["gain"], band["q"], band["filter_type"]
                    )
                    time.sleep(BAND_SETTLE)
            value = dict(self.caps.eq_presets)[self.preset_var.get()]
            self.dev.set_eq_preset(value)
            time.sleep(PRESET_SETTLE)
            return [self.dev.get_eq_band(i) for i in range(self.caps.eq_count)]

        def done(bands):
            self.bands = bands
            self._build_band_rows()
            self._refresh_curve()
            self.status_var.set("Backup restored.")

        self._run(work, done)

    def export_preset(self) -> None:
        if not self.bands:
            return
        path = filedialog.asksaveasfilename(
            defaultextension=".json",
            filetypes=[("Preset", "*.json")],
            initialfile=f"{self.preset_var.get().lower()}.json",
        )
        if not path:
            return
        payload = {
            "device": self.dev.product_name if self.dev else "",
            "preset": self.preset_var.get(),
            "preamp": getattr(self, "preamp_var", None).get() if hasattr(self, "preamp_var") else None,
            "bands": self.bands,
        }
        Path(path).write_text(json.dumps(payload, indent=2), encoding="utf-8")
        self.status_var.set(f"Exported to {path}")

    def import_preset(self) -> None:
        path = filedialog.askopenfilename(filetypes=[("Preset", "*.json")])
        if not path:
            return
        data = json.loads(Path(path).read_text(encoding="utf-8"))
        bands = data.get("bands") or []
        if len(bands) != len(self.bands):
            messagebox.showerror(
                APP_NAME,
                f"This preset has {len(bands)} bands, the device has {len(self.bands)}.",
            )
            return
        self.bands = bands
        self._build_band_rows()
        self._refresh_curve()
        def work():
            for i, band in enumerate(self.bands):
                self.dev.set_eq_band(
                    i, band["frequency"], band["gain"], band["q"], band["filter_type"]
                )
                time.sleep(BAND_SETTLE)

        self._run(work, lambda _r: self.status_var.set(f"Imported {Path(path).name}."))


def _resource(name: str) -> Path | None:
    """Locate a bundled data file, both from source and from a PyInstaller exe."""
    roots = [Path(__file__).resolve().parent.parent]
    bundle = getattr(sys, "_MEIPASS", None)
    if bundle:
        roots.insert(0, Path(bundle))
    for root in roots:
        candidate = root / name
        if candidate.exists():
            return candidate
    return None


def set_icon(root: tk.Tk) -> None:
    icon = _resource("app.ico")
    if icon is None:
        return
    try:
        root.iconbitmap(default=str(icon))
    except tk.TclError:
        pass


def set_icon_for(window: tk.Misc) -> None:
    """Give a secondary window the same icon as the main one."""
    icon = _resource("app.ico")
    if icon is None:
        return
    try:
        window.iconbitmap(str(icon))
    except tk.TclError:
        pass


def style(root: tk.Tk) -> None:
    root.configure(bg=BG)
    s = ttk.Style(root)
    s.theme_use("clam")
    s.configure(".", background=BG, foreground=FG, fieldbackground=PANEL, bordercolor=GRID)
    s.configure("TFrame", background=BG)
    s.configure("TLabel", background=BG, foreground=FG, font=("Segoe UI", 9))
    s.configure("Title.TLabel", font=("Segoe UI Semibold", 15))
    s.configure("Muted.TLabel", foreground=MUTED, font=("Segoe UI", 8))
    s.configure("Status.TLabel", foreground=MUTED, background=PANEL, font=("Segoe UI", 8), padding=(8, 4))
    s.configure("TButton", background=PANEL, foreground=FG, borderwidth=0, padding=(10, 5))
    s.map("TButton", background=[("active", GRID)])
    s.configure(
        "TEntry",
        fieldbackground=PANEL,
        foreground=FG,
        insertcolor=FG,
        borderwidth=1,
        padding=3,
        lightcolor=GRID,
        darkcolor=GRID,
    )
    s.configure(
        "TCombobox",
        fieldbackground=PANEL,
        background=PANEL,
        foreground=FG,
        arrowcolor=FG,
        borderwidth=1,
        padding=3,
        lightcolor=GRID,
        darkcolor=GRID,
        selectbackground=PANEL,
        selectforeground=FG,
    )
    s.map(
        "TCombobox",
        fieldbackground=[("readonly", PANEL), ("focus", PANEL)],
        foreground=[("readonly", FG)],
        selectbackground=[("readonly", PANEL)],
        selectforeground=[("readonly", FG)],
        background=[("active", GRID)],
        arrowcolor=[("active", ACCENT)],
    )
    s.configure("TScale", background=BG, troughcolor=PANEL)
    s.configure("TCheckbutton", background=BG, foreground=FG, font=("Segoe UI", 9))
    s.map("TCheckbutton", background=[("active", BG)])
    s.configure("TNotebook", background=BG, borderwidth=0)
    s.configure("TNotebook.Tab", background=BG, foreground=MUTED, padding=(14, 6), borderwidth=0)
    s.map(
        "TNotebook.Tab",
        background=[("selected", PANEL)],
        foreground=[("selected", FG), ("active", FG)],
    )
    s.configure(
        "Treeview",
        background=PANEL,
        fieldbackground=PANEL,
        foreground=FG,
        rowheight=22,
        borderwidth=0,
    )
    s.configure("Treeview.Heading", background=BG, foreground=MUTED, borderwidth=0)
    s.configure(
        "Vertical.TScrollbar", background=PANEL, troughcolor=BG, bordercolor=BG, arrowcolor=MUTED
    )
    s.map("Treeview.Heading", background=[("active", GRID)])
    s.map("Treeview", background=[("selected", ACCENT)], foreground=[("selected", "#14161a")])
    root.option_add("*TCombobox*Listbox.background", PANEL)
    root.option_add("*TCombobox*Listbox.foreground", FG)
    root.option_add("*TCombobox*Listbox.selectBackground", ACCENT)


def main() -> int:
    root = tk.Tk()
    root.title(APP_NAME)
    set_icon(root)
    # Fit the work area rather than assuming a tall screen: the taskbar would
    # otherwise cover the status bar on a 768-high display.
    width = min(1020, root.winfo_screenwidth() - 80)
    height = min(740, root.winfo_screenheight() - 120)
    root.geometry(f"{width}x{height}+40+20")
    root.minsize(880, 520)
    style(root)
    try:
        App(root)
        root.mainloop()
    except Exception:
        traceback.print_exc()
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

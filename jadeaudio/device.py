"""USB-HID transport for FiiO / JadeAudio dongles."""

from __future__ import annotations

import threading
import time

import hid

from . import protocol as p

FIIO_VENDOR_ID = 0x2972

#: Output/input report id per product. Anything not listed uses 7, which is
#: what the official app falls back to.
REPORT_IDS = {
    "FIIO KA17": 1,
    "JadeAudio JA11": 2,
    "USB20": 0,
}
DEFAULT_REPORT_ID = 7

#: Fallback report payload size, used when the report descriptor cannot be read.
#: Windows' HID stack requires every write to match the length declared in the
#: descriptor, so the real size is looked up per report id when possible.
DEFAULT_REPORT_SIZE = 20


def parse_report_sizes(descriptor: bytes) -> tuple[dict[int, int], dict[int, int]]:
    """Walk a HID report descriptor, returning (input_sizes, output_sizes) in bytes.

    Only the items that matter here are tracked: report id, report size and
    report count, accumulated per (report id, direction).
    """
    inputs: dict[int, int] = {}
    outputs: dict[int, int] = {}
    report_id = 0
    size = count = 0
    i = 0
    while i < len(descriptor):
        prefix = descriptor[i]
        length = prefix & 0x03
        length = 4 if length == 3 else length
        tag = prefix & 0xFC
        value = int.from_bytes(descriptor[i + 1 : i + 1 + length], "little")
        if tag == 0x84:  # Report ID
            report_id = value
        elif tag == 0x74:  # Report Size
            size = value
        elif tag == 0x94:  # Report Count
            count = value
        elif tag == 0x80:  # Input
            inputs[report_id] = inputs.get(report_id, 0) + size * count
        elif tag == 0x90:  # Output
            outputs[report_id] = outputs.get(report_id, 0) + size * count
        i += 1 + length
    to_bytes = lambda d: {k: (v + 7) // 8 for k, v in d.items()}
    return to_bytes(inputs), to_bytes(outputs)


class DeviceError(Exception):
    pass


class NotFound(DeviceError):
    pass


class Timeout(DeviceError):
    pass


def list_devices() -> list[dict]:
    """Every FiiO/JadeAudio HID interface currently attached."""
    seen = []
    for info in hid.enumerate(FIIO_VENDOR_ID, 0):
        seen.append(info)
    return seen


def find(product_name: str | None = None) -> dict:
    """Pick the control interface, optionally restricted to one product name."""
    candidates = list_devices()
    if product_name:
        candidates = [d for d in candidates if (d.get("product_string") or "") == product_name]
    if not candidates:
        raise NotFound("No FiiO/JadeAudio HID device found")
    return candidates[0]


class Device:
    """A connected dongle.

    `request` writes a frame and waits for the matching reply; the read loop
    runs on the calling thread, so keep GUI work off it.
    """

    def __init__(self, info: dict | None = None, timeout: float = 1.5):
        self.info = info or find()
        self.product_name = self.info.get("product_string") or ""
        self.report_id = REPORT_IDS.get(self.product_name, DEFAULT_REPORT_ID)
        self.timeout = timeout
        self._lock = threading.RLock()
        if hasattr(hid, "Device"):  # the `hid` package
            self._h = hid.Device(path=self.info["path"])
            self._h.nonblocking = False
        else:  # cython-hidapi's legacy API
            self._h = hid.device()
            self._h.open_path(self.info["path"])
            self._h.set_nonblocking(False)
        self.in_size = DEFAULT_REPORT_SIZE
        self.out_size = DEFAULT_REPORT_SIZE
        try:
            descriptor = bytes(self._h.get_report_descriptor())
        except Exception:
            descriptor = b""
        if descriptor:
            inputs, outputs = parse_report_sizes(descriptor)
            self.in_size = inputs.get(self.report_id, DEFAULT_REPORT_SIZE)
            self.out_size = outputs.get(self.report_id, DEFAULT_REPORT_SIZE)

    def close(self) -> None:
        with self._lock:
            try:
                self._h.close()
            except Exception:
                pass

    def __enter__(self) -> "Device":
        return self

    def __exit__(self, *exc) -> None:
        self.close()

    # -- raw io --------------------------------------------------------------

    def send(self, frame: bytes) -> None:
        buf = bytearray(self.out_size + 1)
        buf[0] = self.report_id
        buf[1 : 1 + len(frame)] = frame
        with self._lock:
            self._h.write(bytes(buf))

    def request(self, frame: bytes, reg: int | None = None, timeout: float | None = None) -> p.Frame:
        """Send *frame* and return the first reply carrying the same register."""
        want = frame[4] if reg is None else reg
        deadline = time.monotonic() + (self.timeout if timeout is None else timeout)
        with self._lock:
            self._drain()
            self.send(frame)
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise Timeout(f"No reply for register {want}")
                data = self._read(int(remaining * 1000))
                if not data:
                    continue
                reply = p.parse(bytes(data))
                if reply is not None and reply.reg == want:
                    return reply

    def _read(self, timeout_ms: int) -> bytes:
        try:
            return bytes(self._h.read(self.in_size + 1, max(1, timeout_ms)))
        except TypeError:  # the `hid` package spells the timeout as a keyword
            return bytes(self._h.read(self.in_size + 1, timeout=max(1, timeout_ms)))

    def _drain(self) -> None:
        while True:
            data = self._read(1)
            if not data:
                return

    # -- typed accessors -----------------------------------------------------

    @property
    def firmware_version(self) -> str:
        payload = self.request(p.get_firmware_version()).payload
        if len(payload) >= 2:
            return f"{payload[0]}.{payload[1]}" + (f".{payload[2]}" if len(payload) > 2 else "")
        return payload.hex()

    def get_eq_preset(self) -> int:
        return self.request(p.get_eq_preset()).payload[0]

    def set_eq_preset(self, preset: int) -> None:
        self.send(p.set_eq_preset(preset))

    def get_eq_switch(self) -> int:
        return self.request(p.get_eq_switch()).payload[0]

    def set_eq_switch(self, on: bool) -> None:
        self.send(p.set_eq_switch(1 if on else 0))

    def get_eq_count(self) -> int:
        return self.request(p.get_eq_count()).payload[0]

    def get_eq_band(self, index: int) -> dict:
        return p.decode_eq_band(self.request(p.get_eq_band(index)).payload)

    def set_eq_band(self, index: int, freq: int, gain: float, q: float, filter_type: int) -> None:
        self.send(p.set_eq_band(index, freq, gain, q, filter_type))

    def get_eq_global_gain(self) -> float:
        return p.dec_gain(self.request(p.get_eq_global_gain()).payload)

    def set_eq_global_gain(self, db: float) -> None:
        self.send(p.set_eq_global_gain(db))

    def save_eq(self) -> None:
        self.send(p.save_eq())

    def get_filter_mode(self) -> int:
        return self.request(p.get_filter_mode()).payload[0]

    def set_filter_mode(self, mode: int) -> None:
        self.send(p.set_filter_mode(mode))

    def get_volume_max(self) -> int:
        return self.request(p.get_volume_max()).payload[0]

    def set_volume_max(self, value: int) -> None:
        self.send(p.set_volume_max(value))

    def get_channel_balance(self) -> int:
        payload = self.request(p.get_channel_balance()).payload
        left, right = payload[0], payload[1]
        return right if right else -left

    def set_channel_balance(self, value: int) -> None:
        self.send(p.set_channel_balance(value))

    def get_led(self) -> int:
        return self.request(p.get_led()).payload[0]

    def set_led(self, value: int) -> None:
        self.send(p.set_led(value))

    def reset_to_factory(self) -> None:
        self.send(p.reset_to_factory())

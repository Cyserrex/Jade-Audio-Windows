"""Frame encoding/decoding for the FiiO / JadeAudio USB-HID control protocol.

Wire format (both directions)::

    [0] head    0xAA for a write, 0xBB for a read
    [1] start   0x0A for a write, 0x0B for a read
    [2] seq_hi  16-bit rolling sequence number, big endian
    [3] seq_lo
    [4] reg     register id (see `Reg`)
    [5] len     payload length
    [6..] payload
    [-2] crc8   over every byte before it
    [-1] stop   0xEE

The device answers a request with the same register id; the payload of the
reply is what the getters below decode.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass
from enum import IntEnum

from ._crc import crc8

SET_HEAD = 0xAA
SET_START = 0x0A
GET_HEAD = 0xBB
GET_START = 0x0B
STOP = 0xEE


class Reg(IntEnum):
    """Register ids understood by the device."""

    VOL_MAX = 1
    VOL_OUTPUT = 2
    FILTER_MODE = 4
    LED_LIGHT = 6
    VOL_BALANCE = 7
    VOL_OUTPUT_SWITCH = 8
    FIRMWARE_VERSION = 11
    SCREEN_ORIENTATION = 16
    LANGUAGE = 17
    MIC_SWITCH = 18
    PEQ_PARAMS = 21
    PEQ_PRE = 22
    GLOBAL_GAIN = 23
    PEQ_COUNT = 24
    PEQ_SAVE = 25
    PEQ_SWITCH = 26
    RESET_PRE = 27
    RESET_ALL_PRE = 28
    MIC_MONITOR_SWITCH = 29
    MIC_MONITOR_VOL = 30
    RESET_TO_FACTORY = 33
    MIC_VOL = 34
    PEQ_NAME = 48
    LED_CTRL = 51
    MIC_NR_LEVEL = 52
    GAIN_3D = 53
    REVERB_LEVEL = 54


class FilterMode(IntEnum):
    SD_SHARP = 0
    SHARP = 1
    SD_SLOW = 2
    SLOW = 3
    NO_OVERSAMPLING = 4


class FilterType(IntEnum):
    PEAK = 0
    LOW_SHELF = 1
    HIGH_SHELF = 2
    BAND_PASS = 3
    LOW_PASS = 4
    HIGH_PASS = 5
    ALL_PASS = 6


#: EQ presets exposed by the JA11, in the order the official app lists them.
JA11_EQ_PRESETS = (
    ("Vocal", 0),
    ("Classic", 1),
    ("Bass", 2),
    ("USER1", 3),
    ("EQ Off", 4),
)

#: The JA11 restricts the band editor to these filter shapes.
JA11_FILTER_TYPES = (FilterType.PEAK, FilterType.LOW_SHELF, FilterType.HIGH_SHELF)

#: Per-band gain limits, in dB.
JA11_GAIN_RANGE = (-12.0, 12.0)
#: Q limits.
JA11_Q_RANGE = (0.25, 8.0)

_seq = 0


def _next_seq() -> int:
    global _seq
    value = _seq
    _seq = 0 if value == 0xFFFF else value + 1
    return value


def build(head: int, start: int, reg: int, payload: bytes = b"") -> bytes:
    """Assemble one protocol frame."""
    seq = _next_seq()
    body = bytes([head, start, (seq >> 8) & 0xFF, seq & 0xFF, reg, len(payload)]) + bytes(payload)
    return body + bytes([crc8(body), STOP])


def write(reg: int, payload: bytes = b"") -> bytes:
    """A frame that sets *reg*."""
    return build(SET_HEAD, SET_START, reg, payload)


def read(reg: int, payload: bytes = b"") -> bytes:
    """A frame that queries *reg*."""
    return build(GET_HEAD, GET_START, reg, payload)


@dataclass(frozen=True)
class Frame:
    head: int
    start: int
    seq: int
    reg: int
    payload: bytes

    @property
    def is_write(self) -> bool:
        return self.head == SET_HEAD


def parse(data: bytes) -> Frame | None:
    """Decode a frame out of a raw input report, or return None if there is none.

    Input reports are zero padded, and on some firmwares carry a leading report
    id, so the header is searched for rather than assumed to be at offset 0.
    """
    for i in range(0, max(1, len(data) - 7)):
        head = data[i]
        if head not in (SET_HEAD, GET_HEAD):
            continue
        if i + 6 > len(data):
            continue
        start = data[i + 1]
        if (head, start) not in ((SET_HEAD, SET_START), (GET_HEAD, GET_START)):
            continue
        length = data[i + 5]
        end = i + 6 + length
        if end + 2 > len(data):
            continue
        if data[end + 1] != STOP:
            continue
        body = data[i:end]
        # Outgoing frames checksum the whole body. Replies from at least the
        # JA11 checksum only from the sequence number on, so accept either.
        if data[end] not in (crc8(body), crc8(body[2:])):
            continue
        return Frame(head, start, (data[i + 2] << 8) | data[i + 3], data[i + 4], bytes(body[6:]))
    return None


# --- value codecs -----------------------------------------------------------


def _u16(value: int) -> bytes:
    return struct.pack(">H", value & 0xFFFF)


def enc_gain(db: float) -> bytes:
    """dB gain -> two bytes of tenths, signed."""
    return struct.pack(">h", int(round(db * 10)))


def dec_gain(data: bytes) -> float:
    return struct.unpack(">h", data[:2])[0] / 10.0


def enc_q(q: float) -> bytes:
    return _u16(int(round(q * 100)))


def dec_q(data: bytes) -> float:
    return struct.unpack(">H", data[:2])[0] / 100.0


def enc_freq(hz: int) -> bytes:
    return _u16(int(hz))


def dec_freq(data: bytes) -> int:
    return struct.unpack(">H", data[:2])[0]


# --- command builders -------------------------------------------------------


def get_firmware_version() -> bytes:
    return read(Reg.FIRMWARE_VERSION)


def get_eq_preset() -> bytes:
    return read(Reg.PEQ_PRE)


def set_eq_preset(preset: int) -> bytes:
    return write(Reg.PEQ_PRE, bytes([preset]))


def get_eq_switch() -> bytes:
    return read(Reg.PEQ_SWITCH)


def set_eq_switch(on: int) -> bytes:
    return write(Reg.PEQ_SWITCH, bytes([on]))


def get_eq_count() -> bytes:
    return read(Reg.PEQ_COUNT)


def get_eq_band(index: int) -> bytes:
    return read(Reg.PEQ_PARAMS, bytes([index]))


def set_eq_band(index: int, freq: int, gain: float, q: float, filter_type: int) -> bytes:
    return write(
        Reg.PEQ_PARAMS,
        bytes([index]) + enc_gain(gain) + enc_freq(freq) + enc_q(q) + bytes([filter_type]),
    )


def get_eq_global_gain() -> bytes:
    return read(Reg.GLOBAL_GAIN)


def set_eq_global_gain(db: float) -> bytes:
    return write(Reg.GLOBAL_GAIN, enc_gain(db))


def save_eq() -> bytes:
    """Persist the working EQ into the currently selected preset.

    Not used by the GUI: on JA11 firmware 2.20 this makes the dongle drop off
    the USB bus for a moment and can leave preset slots holding the wrong
    bands. Band writes already persist on their own, so this is only here for
    completeness.
    """
    return write(Reg.PEQ_SAVE, bytes([0]))


def reset_preset() -> bytes:
    return write(Reg.RESET_PRE)


def get_filter_mode() -> bytes:
    return read(Reg.FILTER_MODE)


def set_filter_mode(mode: int) -> bytes:
    return write(Reg.FILTER_MODE, bytes([mode]))


def get_volume_max() -> bytes:
    return read(Reg.VOL_MAX)


def set_volume_max(value: int) -> bytes:
    return write(Reg.VOL_MAX, bytes([value]))


def get_volume_output() -> bytes:
    return read(Reg.VOL_OUTPUT)


def set_volume_output(value: int) -> bytes:
    return write(Reg.VOL_OUTPUT, bytes([value]))


def get_channel_balance() -> bytes:
    return read(Reg.VOL_BALANCE)


def set_channel_balance(value: int) -> bytes:
    """Negative leans left, positive leans right, 0 is centred."""
    if value > 0:
        payload = bytes([0, value & 0xFF])
    elif value < 0:
        payload = bytes([value & 0xFF, 0])
    else:
        payload = bytes([0, 0])
    return write(Reg.VOL_BALANCE, payload)


def get_led() -> bytes:
    return read(Reg.LED_LIGHT)


def set_led(value: int) -> bytes:
    return write(Reg.LED_LIGHT, bytes([value]))


def reset_to_factory() -> bytes:
    return write(Reg.RESET_TO_FACTORY, bytes([0]))


def decode_eq_band(payload: bytes) -> dict:
    """Decode a PEQ_PARAMS reply payload."""
    return {
        "index": payload[0],
        "gain": dec_gain(payload[1:3]),
        "frequency": dec_freq(payload[3:5]),
        "q": dec_q(payload[5:7]),
        "filter_type": payload[7],
    }

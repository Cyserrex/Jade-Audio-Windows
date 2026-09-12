"""What each dongle actually answers on the wire.

The official app decides this from a server-side device table; here it is
probed once at connect time, and the table below only supplies labels and
ranges for the registers that turn out to be live.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from . import protocol as p
from .device import Device, Timeout


@dataclass
class Capabilities:
    """Registers the connected device responded to."""

    supported: set[int] = field(default_factory=set)
    eq_count: int = 0
    eq_presets: tuple = ()
    filter_types: tuple = ()
    gain_range: tuple = (-12.0, 12.0)
    q_range: tuple = (0.25, 8.0)
    volume_max: int = 60

    def has(self, reg: int) -> bool:
        return reg in self.supported


#: Registers worth probing. Ones that only act (resets, saves) are excluded
#: because they have no readable counterpart.
PROBE_REGS = (
    p.Reg.VOL_MAX,
    p.Reg.VOL_OUTPUT,
    p.Reg.VOL_OUTPUT_SWITCH,
    p.Reg.VOL_BALANCE,
    p.Reg.FILTER_MODE,
    p.Reg.LED_LIGHT,
    p.Reg.FIRMWARE_VERSION,
    p.Reg.PEQ_PRE,
    p.Reg.PEQ_COUNT,
    p.Reg.PEQ_SWITCH,
    p.Reg.GLOBAL_GAIN,
    p.Reg.MIC_SWITCH,
    p.Reg.MIC_VOL,
    p.Reg.GAIN_3D,
    p.Reg.REVERB_LEVEL,
)


def probe(dev: Device, timeout: float = 0.2) -> Capabilities:
    """Ask the device which registers it answers, and read its EQ geometry."""
    caps = Capabilities()
    for reg in PROBE_REGS:
        try:
            dev.request(p.read(reg), timeout=timeout)
        except Timeout:
            continue
        except Exception:
            continue
        caps.supported.add(int(reg))

    # PEQ_PARAMS needs a band index in the request, so probe it separately.
    try:
        dev.request(p.get_eq_band(0), timeout=timeout)
        caps.supported.add(int(p.Reg.PEQ_PARAMS))
    except Exception:
        pass

    if caps.has(p.Reg.PEQ_COUNT):
        try:
            caps.eq_count = dev.get_eq_count()
        except Exception:
            caps.eq_count = 0

    caps.eq_presets = p.JA11_EQ_PRESETS
    caps.filter_types = p.JA11_FILTER_TYPES
    caps.gain_range = p.JA11_GAIN_RANGE
    caps.q_range = p.JA11_Q_RANGE
    return caps

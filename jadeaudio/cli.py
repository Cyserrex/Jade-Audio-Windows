"""Command line access to the same device layer the GUI uses.

    python -m jadeaudio.cli info
    python -m jadeaudio.cli preset bass
    python -m jadeaudio.cli band 0 --freq 60 --gain -3 --q 1.0 --type peak
    python -m jadeaudio.cli volume 45
    python -m jadeaudio.cli cloud community --page 1
    python -m jadeaudio.cli cloud search bass
    python -m jadeaudio.cli cloud official
    python -m jadeaudio.cli cloud get FiiOSC-<code>
    python -m jadeaudio.cli cloud apply FiiOSC-<code>
    python -m jadeaudio.cli backup presets.json
    python -m jadeaudio.cli restore presets.json
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

from . import capabilities as caps_mod
from . import protocol as p
from .device import Device, NotFound

PRESET_SETTLE = 0.8
BAND_SETTLE = 0.25


def _presets(caps) -> dict[str, int]:
    return {name.lower().replace(" ", "-"): value for name, value in caps.eq_presets}


def cmd_info(dev: Device, caps, _args) -> int:
    print(f"{dev.product_name}  firmware {dev.firmware_version}")
    info = dev.info
    print(f"  VID {info['vendor_id']:#06x}  PID {info['product_id']:#06x}  report id {dev.report_id}")
    print(f"  registers: {', '.join(sorted(p.Reg(r).name for r in caps.supported))}")
    if caps.has(p.Reg.VOL_OUTPUT):
        print(f"  volume: {dev.request(p.get_volume_output()).payload[0]}")
    if caps.has(p.Reg.GLOBAL_GAIN):
        print(f"  global gain: {dev.get_eq_global_gain():+.1f} dB")
    if caps.eq_count:
        current = dev.get_eq_preset()
        name = next((n for n, v in caps.eq_presets if v == current), str(current))
        print(f"  preset: {name}")
        for i in range(caps.eq_count):
            b = dev.get_eq_band(i)
            print(
                f"    band {i + 1}: {b['frequency']:>6} Hz  {b['gain']:+6.1f} dB  "
                f"Q {b['q']:.2f}  {p.FilterType(b['filter_type']).name.lower()}"
            )
    return 0


def cmd_preset(dev: Device, caps, args) -> int:
    table = _presets(caps)
    key = args.name.lower().replace(" ", "-")
    if key not in table:
        print(f"Unknown preset. Choose from: {', '.join(table)}", file=sys.stderr)
        return 2
    dev.set_eq_preset(table[key])
    time.sleep(PRESET_SETTLE)
    print(f"preset -> {args.name}")
    return 0


def cmd_band(dev: Device, caps, args) -> int:
    if not 0 <= args.index < caps.eq_count:
        print(f"Band index must be 0..{caps.eq_count - 1}", file=sys.stderr)
        return 2
    band = dev.get_eq_band(args.index)
    freq = args.freq if args.freq is not None else band["frequency"]
    gain = args.gain if args.gain is not None else band["gain"]
    q = args.q if args.q is not None else band["q"]
    ftype = p.FilterType[args.type.upper().replace("-", "_")] if args.type else band["filter_type"]
    dev.set_eq_band(args.index, int(freq), float(gain), float(q), int(ftype))
    time.sleep(BAND_SETTLE)
    print(f"band {args.index}: {dev.get_eq_band(args.index)}")
    return 0


def cmd_volume(dev: Device, caps, args) -> int:
    if not caps.has(p.Reg.VOL_OUTPUT):
        print("This device has no output volume register.", file=sys.stderr)
        return 2
    dev.send(p.set_volume_output(args.value))
    time.sleep(0.2)
    print(f"volume -> {dev.request(p.get_volume_output()).payload[0]}")
    return 0


def cmd_gain(dev: Device, caps, args) -> int:
    if not caps.has(p.Reg.GLOBAL_GAIN):
        print("This device has no global gain register.", file=sys.stderr)
        return 2
    dev.set_eq_global_gain(args.value)
    time.sleep(0.2)
    print(f"global gain -> {dev.get_eq_global_gain():+.1f} dB")
    return 0


def cmd_cloud(dev: Device, caps, args) -> int:
    from .cloud import Cloud, CloudError, device_type_for

    cloud = Cloud()
    device_type = device_type_for(dev.product_name)
    try:
        if args.action == "community":
            presets, total = cloud.community_presets(device_type, page=args.page, page_size=20)
            print(f"{total} community preset(s) for {dev.product_name}, page {args.page}")
            for item in presets:
                print(f"  {item.downloads:>4} dl  {item.name}  by {item.author}")
                print(f"           {item.share_code}")
            return 0

        if args.action == "search":
            hits = cloud.search(args.code, device_type)
            print(f"{len(hits)} hit(s) for {args.code!r}")
            for item in hits:
                print(f"  {item.downloads:>4} dl  {item.name}  by {item.author}")
                print(f"           {item.share_code}")
            return 0

        if args.action == "official":
            presets, total = cloud.official_presets(device_type, page_size=50)
            print(f"{total} official preset(s) for {dev.product_name} (type {device_type})")
            for item in presets:
                print(f"  {item.name}  ({len(item.bands)} bands)  {item.share_code}")
            if not total:
                print("  FiiO publishes none for this model; fetch a community one by share code.")
            return 0

        preset = cloud.by_share_code(args.code)
    except CloudError as exc:
        print(exc, file=sys.stderr)
        return 1

    print(f"{preset.name}  by {preset.author}  ({preset.downloads} downloads)")
    if preset.description:
        print(f"  {preset.description}")
    print(f"  device type {preset.device_type}, global gain {preset.global_gain:+.1f} dB")
    for i, b in enumerate(preset.bands):
        print(
            f"    band {i + 1}: {b['frequency']:>6} Hz  {b['gain']:+6.1f} dB  "
            f"Q {b['q']:.2f}  {p.FilterType(b['filter_type']).name.lower()}"
        )
    if args.action != "apply":
        return 0

    if preset.device_type != device_type and not args.force:
        print(
            f"\nRefusing to apply: made for device type {preset.device_type}, "
            f"you have {device_type}. Pass --force to do it anyway.",
            file=sys.stderr,
        )
        return 1

    count = caps.eq_count
    bands = preset.bands[:count]
    print(f"\nWriting {len(bands)} band(s) into the preset currently selected...")
    for i, band in enumerate(bands):
        dev.set_eq_band(i, band["frequency"], band["gain"], band["q"], band["filter_type"])
        time.sleep(BAND_SETTLE)
    if caps.has(p.Reg.GLOBAL_GAIN):
        lo, hi = caps.gain_range
        dev.set_eq_global_gain(round(min(max(preset.global_gain, lo), hi), 1))
    print("done")
    return 0


def cmd_backup(dev: Device, caps, args) -> int:
    original = dev.get_eq_preset()
    snapshot = {}
    for name, value in caps.eq_presets:
        dev.set_eq_preset(value)
        time.sleep(PRESET_SETTLE)
        snapshot[name] = [dev.get_eq_band(i) for i in range(caps.eq_count)]
        print(f"  read {name}")
    dev.set_eq_preset(original)
    time.sleep(PRESET_SETTLE)
    Path(args.path).write_text(
        json.dumps({"device": dev.product_name, "presets": snapshot}, indent=2), encoding="utf-8"
    )
    print(f"saved {args.path}")
    return 0


def cmd_restore(dev: Device, caps, args) -> int:
    data = json.loads(Path(args.path).read_text(encoding="utf-8"))
    by_name = dict(caps.eq_presets)
    for name, bands in (data.get("presets") or {}).items():
        if name not in by_name:
            continue
        dev.set_eq_preset(by_name[name])
        time.sleep(PRESET_SETTLE)
        for i, band in enumerate(bands[: caps.eq_count]):
            dev.set_eq_band(i, band["frequency"], band["gain"], band["q"], band["filter_type"])
            time.sleep(BAND_SETTLE)
        print(f"  wrote {name}")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="jadeaudio", description=__doc__)
    sub = parser.add_subparsers(dest="cmd", required=True)

    sub.add_parser("info", help="show device state").set_defaults(fn=cmd_info)

    q = sub.add_parser("preset", help="select an EQ preset")
    q.add_argument("name")
    q.set_defaults(fn=cmd_preset)

    q = sub.add_parser("band", help="edit one PEQ band")
    q.add_argument("index", type=int)
    q.add_argument("--freq", type=int)
    q.add_argument("--gain", type=float)
    q.add_argument("--q", type=float)
    q.add_argument("--type", choices=[t.name.lower() for t in p.FilterType])
    q.set_defaults(fn=cmd_band)

    q = sub.add_parser("volume", help="set output volume")
    q.add_argument("value", type=int)
    q.set_defaults(fn=cmd_volume)

    q = sub.add_parser("gain", help="set EQ global gain in dB")
    q.add_argument("value", type=float)
    q.set_defaults(fn=cmd_gain)

    q = sub.add_parser("cloud", help="browse or fetch presets from FiiO's online library")
    q.add_argument("action", choices=["community", "search", "official", "get", "apply"])
    q.add_argument("code", nargs="?", default="", help="share code for get/apply, keyword for search")
    q.add_argument("--page", type=int, default=1, help="page number, for community")
    q.add_argument("--force", action="store_true", help="apply even if made for another device")
    q.set_defaults(fn=cmd_cloud)

    q = sub.add_parser("backup", help="write every preset to a JSON file")
    q.add_argument("path")
    q.set_defaults(fn=cmd_backup)

    q = sub.add_parser("restore", help="write a JSON backup back to the device")
    q.add_argument("path")
    q.set_defaults(fn=cmd_restore)

    args = parser.parse_args(argv)
    try:
        dev = Device()
    except NotFound as exc:
        print(exc, file=sys.stderr)
        return 1
    try:
        return args.fn(dev, caps_mod.probe(dev), args)
    finally:
        dev.close()


if __name__ == "__main__":
    raise SystemExit(main())

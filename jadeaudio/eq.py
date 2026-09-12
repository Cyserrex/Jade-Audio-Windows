"""Biquad maths for previewing the device's EQ curve.

Coefficients follow the Audio EQ Cookbook, matching what the official app
computes at a 48 kHz sample rate.
"""

from __future__ import annotations

import cmath
import math

from .protocol import FilterType

FS = 48000.0


def biquad(gain_db: float, freq: float, q: float, filter_type: int, fs: float = FS) -> tuple:
    """Return (b0, b1, b2, a0, a1, a2) for one band."""
    w = 2.0 * freq / fs
    a = math.pow(10.0, gain_db / 40.0)
    sqrt_a = math.sqrt(a)
    sn = math.sin(math.pi * w)
    cs = math.cos(math.pi * w)
    q = q if q and q > 0 else 1.0
    alpha = sn / (2.0 * q)

    if filter_type == FilterType.PEAK:
        return (1 + alpha * a, -2 * cs, 1 - alpha * a, 1 + alpha / a, -2 * cs, 1 - alpha / a)
    if filter_type == FilterType.LOW_SHELF:
        return (
            a * (a + 1 - (a - 1) * cs + 2 * sqrt_a * alpha),
            2 * a * (a - 1 - (a + 1) * cs),
            a * (a + 1 - (a - 1) * cs - 2 * sqrt_a * alpha),
            a + 1 + (a - 1) * cs + 2 * sqrt_a * alpha,
            -2 * (a - 1 + (a + 1) * cs),
            a + 1 + (a - 1) * cs - 2 * sqrt_a * alpha,
        )
    if filter_type == FilterType.HIGH_SHELF:
        return (
            a * (a + 1 + (a - 1) * cs + 2 * sqrt_a * alpha),
            -2 * a * (a - 1 + (a + 1) * cs),
            a * (a + 1 + (a - 1) * cs - 2 * sqrt_a * alpha),
            a + 1 - (a - 1) * cs + 2 * sqrt_a * alpha,
            2 * (a - 1 - (a + 1) * cs),
            a + 1 - (a - 1) * cs - 2 * sqrt_a * alpha,
        )
    if filter_type == FilterType.LOW_PASS:
        return ((1 - cs) / 2, 1 - cs, (1 - cs) / 2, 1 + alpha, -2 * cs, 1 - alpha)
    if filter_type == FilterType.HIGH_PASS:
        return ((1 + cs) / 2, -(1 + cs), (1 + cs) / 2, 1 + alpha, -2 * cs, 1 - alpha)
    if filter_type == FilterType.BAND_PASS:
        return (alpha, 0.0, -alpha, 1 + alpha, -2 * cs, 1 - alpha)
    if filter_type == FilterType.ALL_PASS:
        return (1 - alpha, -2 * cs, 1 + alpha, 1 + alpha, -2 * cs, 1 - alpha)
    return (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)


def band_response_db(band: dict, freqs: list[float], fs: float = FS) -> list[float]:
    """Magnitude response of a single band, in dB, at each frequency."""
    b0, b1, b2, a0, a1, a2 = biquad(band["gain"], band["frequency"], band["q"], band["filter_type"], fs)
    b0, b1, b2, a1, a2 = b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0
    out = []
    for f in freqs:
        z = cmath.exp(-2j * math.pi * f / fs)
        h = (b0 + b1 * z + b2 * z * z) / (1.0 + a1 * z + a2 * z * z)
        mag = abs(h)
        out.append(20.0 * math.log10(mag) if mag > 1e-12 else -240.0)
    return out


def total_response_db(bands: list[dict], freqs: list[float], fs: float = FS) -> list[float]:
    """Summed magnitude response of every band."""
    total = [0.0] * len(freqs)
    for band in bands:
        for i, db in enumerate(band_response_db(band, freqs, fs)):
            total[i] += db
    return total


def log_freqs(low: float = 20.0, high: float = 20000.0, count: int = 240) -> list[float]:
    step = (math.log10(high) - math.log10(low)) / (count - 1)
    return [math.pow(10.0, math.log10(low) + step * i) for i in range(count)]

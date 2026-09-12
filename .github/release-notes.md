A native Windows app for FiiO / JadeAudio USB dongles — the controls the
official [FiiO Control](https://fiiocontrol.fiio.com) web app offers, without a
browser or a WebHID permission prompt.

Developed and verified against a **JadeAudio JA11** (VID `0x2972`, PID `0x0102`,
firmware 2.20).

## Download

**`Jade.Audio.Control.exe`** — a single 187 KB file. Copy it anywhere and
double-click. Nothing sits beside it, not even a `.config`.

It targets **.NET Framework 4.8**, which ships with Windows 10 1903 and later
and with Windows 11, so on any current machine it simply runs. On Windows 7 SP1
or 8.1, install 4.8 once from
[Microsoft](https://dotnet.microsoft.com/download/dotnet-framework/net48).

No driver, no admin rights: the dongle is a plain HID device. Close the FiiO
Control browser tab first — two clients on the same HID interface confuse each
other.

## What it does

- **Equalizer** — a response plot with a draggable handle per band, one
  translucent trace each and the summed curve on top. Frequency, gain, Q and
  filter shape per band, plus global gain and output volume.
- **Preset library** — Handpick (671 community presets for the JA11 at the time
  of writing), Official, My presets, and lookup by share code, with search and
  paging. Browsing and applying need no account.
- **Device** — what the dongle reports about itself, and backup/restore of every
  preset to a JSON file.
- Only the controls the attached device actually answers are shown, so other
  FiiO dongles get a sensible subset instead of dead knobs.

## Signing in

Optional, and only needed for the presets saved on your own FiiO account. You
type your username, password and the CAPTCHA yourself; the password is forwarded
to FiiO's token endpoint and kept nowhere else — not on disk, not in a log.
Tokens live in memory, so a restart means signing in again.

## Also in the repository

A Python/Tk edition of the same thing, which carries the command line:

```
python -m jadeaudio.cli info
python -m jadeaudio.cli cloud community
python -m jadeaudio.cli backup presets.json
```

## Protocol notes

The HID protocol was decoded from the official web bundle and verified against
real hardware. Two findings worth repeating:

- Replies from the JA11 checksum only from the sequence number onward, while
  outgoing frames checksum the whole body.
- `PEQ_SAVE` (register 25) is unsafe on firmware 2.20 — it drops the dongle off
  the USB bus and can leave preset slots holding another preset's bands. This
  app never sends it; band writes persist on their own.

# Jade Audio Control

Two Windows apps for FiiO / JadeAudio USB dongles, sharing one reverse-engineered
protocol:

| | |
| --- | --- |
| [`csharp/`](csharp/README.md) | **C# / WPF on .NET Framework 4.8** — the one to use. Custom-drawn interface, a single 187 KB exe that runs on any Windows with 4.8 (built into Windows 10 1903+ and 11). |
| this folder | **Python / Tk** — the original, and where the command line lives. |

Both talk to the device identically; pick whichever suits. The sections below
describe the Python edition and the protocol itself, which both are built on.

---

A native Windows app for FiiO / JadeAudio USB dongles — the same controls the
official [FiiO Control](https://fiiocontrol.fiio.com) web app offers, without a
browser, a WebHID permission prompt, or an internet connection.

Developed and verified against a **JadeAudio JA11** (VID `0x2972`, PID `0x0102`,
firmware 2.20).

![screenshot](csharp/docs/equalizer.png)

*The C# edition. The Python one looks like `screenshot.png`.*

## What it does

- Live parametric EQ curve — drag the numbered handles to move a band
- 5-band PEQ editor: frequency, gain, Q, filter shape
- Preset switching (Vocal / Classic / Bass / USER1 / EQ Off on the JA11)
- EQ global gain and output volume
- Browse, search and apply presets from FiiO's online library
- Sign in to your FiiO account to reach the presets saved there
- Export / import a single preset, and back up or restore **every** preset
- Capability probing: only the controls the attached device actually answers
  are shown, so other FiiO dongles get a sensible subset rather than dead knobs

## Running

### The executable

`dist\Jade Audio Control.exe` is a single self-contained file — Python, Tk and
hidapi are inside it, so it runs on a Windows machine with no Python installed.
Copy it anywhere and double-click it. Nothing to install, no admin rights: the
dongle is a plain HID device.

Rebuild it after changing the source with:

```bash
pip install pyinstaller
python build.py
```

### From source

Needs Python 3.10+ with Tk (the python.org installer includes it).

Double-click **`Jade Audio Control.bat`**. It picks a Python that has both Tk
and `hidapi` — several interpreters are usually on PATH and only some of them
qualify — installs `hidapi` on first run if nothing has it, and then starts the
app through `pythonw` so no console window lingers. If something is missing it
says so instead of failing silently.

Or by hand:

```bash
pip install hidapi
python run.py
```

Either way, close the FiiO Control browser tab first — two clients talking to
the same HID interface will confuse each other.

## Command line

The same device layer is available headless, which is handy for scripting or
for putting an EQ change on a hotkey:

```bash
python -m jadeaudio.cli info
python -m jadeaudio.cli preset bass
python -m jadeaudio.cli band 0 --freq 60 --gain -3 --q 1.0 --type peak
python -m jadeaudio.cli volume 45
python -m jadeaudio.cli gain -2.5
python -m jadeaudio.cli cloud official
python -m jadeaudio.cli cloud get FiiOSC-65ed803041e243bfb3b9395a1cfb704d
python -m jadeaudio.cli cloud apply FiiOSC-65ed803041e243bfb3b9395a1cfb704d
python -m jadeaudio.cli backup presets.json
python -m jadeaudio.cli restore presets.json
```

## The protocol

Decoded from the official web app's JavaScript bundle and then verified against
real hardware. Every frame, in both directions, looks like this:

| offset | field  | notes                                        |
| ------ | ------ | -------------------------------------------- |
| 0      | head   | `0xAA` write, `0xBB` read                    |
| 1      | start  | `0x0A` write, `0x0B` read                    |
| 2–3    | seq    | 16-bit rolling counter, big endian           |
| 4      | reg    | register id, see `jadeaudio/protocol.py`     |
| 5      | len    | payload length                               |
| 6…     | payload |                                             |
| −2     | crc8   | table-driven CRC-8                           |
| −1     | stop   | `0xEE`                                       |

Frames ride on HID report id 2 for the JA11 (1 for the KA17, 7 for everything
else), in 20-byte input and output reports on the `0x0C`/`0x01` collection.

Values are encoded as: gain in tenths of a dB (signed, 2 bytes), frequency in Hz
(2 bytes), Q ×100 (2 bytes), filter type as one byte.

### Notes from testing the JA11

Two behaviours worth knowing, both found by probing the real device:

- **Replies checksum differently from requests.** Outgoing frames CRC the whole
  body; the JA11's replies CRC only from the sequence number onward. The parser
  accepts either.
- **`PEQ_SAVE` (register 25) is unsafe on firmware 2.20.** It makes the dongle
  drop off the USB bus for a moment, and it left preset slots holding another
  preset's bands. The app never sends it — band writes already persist on their
  own. Allow ~0.8 s after a preset switch before reading bands back, or you get
  a mix of the old and new preset.

### Registers the JA11 answers

`FIRMWARE_VERSION`, `PEQ_PRE`, `PEQ_PARAMS`, `PEQ_COUNT`, `GLOBAL_GAIN`,
`VOL_OUTPUT`, `MIC_SWITCH`. Everything else in `protocol.Reg` belongs to other
models in the range and times out here — which is exactly what the capability
probe is for.

## The online preset library

FiiO hosts an EQ library, and `jadeaudio/cloud.py` speaks to it. Every request
to `/ucenter-api` is wrapped in a hybrid envelope — a random AES-256 key
encrypts the JSON body, and that key travels RSA-encrypted next to it:

```json
{"cipherSign": "base64(RSA_PKCS1v15(aes_key_hex))",
 "cipherText": "base64(AES_256_ECB(body))"}
```

The AES key is a 32-character hex string used verbatim as 32 key bytes, and the
RSA public key is the one shipped in the web app. Responses come back encrypted
under the same key, tied to the request by `X-Request-ID`.

That service carries **no Authorization header at all** — the caller is
identified by a `userId` inside the encrypted body. So browsing and searching
work without an account:

| endpoint                  | what it gives                                  | account |
| ------------------------- | ---------------------------------------------- | ------- |
| `/get-share-peq`          | community presets, paginated, or one by code   | no      |
| `/search-peq`             | search shared presets by name or description   | no      |
| `/get-peq-official-list`  | FiiO's own presets for a device                | no      |
| `/get-peq`                | the presets saved on your account              | yes     |

### Signing in

`/usersystem-api` is the account system, and `cloud.Auth` implements it:

1. `POST /oauth/token` with `grant_type=client_credentials` and the web app's
   public client id → an application token.
2. `POST /api/portal/captcha/pic` with that token → a JPEG CAPTCHA and its token.
3. `POST /oauth/token` with `grant_type=password`, the username, password and
   the CAPTCHA the person read → the user's token.
4. `GET /api/portal/user/user_info` — AES key wrapped with a *second* RSA key,
   passed as the `cipherSign` query parameter — → the profile, and the `userId`
   the preset service wants.

**Credentials are the user's to enter.** The app shows the CAPTCHA image and
collects the username and password in its own dialog; the password is forwarded
to FiiO's token endpoint and then dropped. Tokens live in memory for the life of
the process — nothing is written to disk, so signing in again is needed after a
restart.

### What is actually there for the JA11

The official library holds 58 presets and **none of them are for the JA11** —
they are mostly for the KA15, SNOWSKY Melody and BTR17. The community library
is the opposite: **671 presets for the JA11**, all 5-band with a `masterGain`,
which maps exactly onto this device. That is the tab worth using.

## Layout

| file                        | role                                             |
| --------------------------- | ------------------------------------------------ |
| `jadeaudio/protocol.py`     | frame encode/decode, registers, value codecs      |
| `jadeaudio/_crc.py`         | CRC-8 table lifted from the official app          |
| `jadeaudio/device.py`       | HID transport, report-descriptor sizing, accessors|
| `jadeaudio/capabilities.py` | runtime probe of which registers respond          |
| `jadeaudio/eq.py`           | biquad maths for the response curve               |
| `jadeaudio/app.py`          | the Tk GUI                                        |
| `jadeaudio/cloud.py`        | encrypted API client, and the account session     |
| `jadeaudio/cloud_dialog.py` | the "Online presets" window and its login dialog  |
| `jadeaudio/cli.py`          | headless equivalent                               |
| `build.py`                  | builds the standalone exe with PyInstaller        |

## Safety

Before changing anything, take a snapshot of the presets as they came:

```bash
python -m jadeaudio.cli backup presets.json
```

`python -m jadeaudio.cli restore presets.json` puts them back exactly.

# Fn hotkey and OSD research — AORUS 17G KD

Recovered from the installed Gigabyte software: `FusionShortcut.exe` (`Form1`),
`OSDwindow.exe` (`OSDMain`, `EventWatcherAsync`) and `KeyboardModel2023.dll`
(`IoneKeyBoard.RegisterKyboardEvent`). Nothing here is confirmed on hardware yet.

## Two independent event channels

The laptop reports Fn activity twice, through unrelated mechanisms, and Gigabyte's
software listens to both.

### 1. Raw Input on the keyboard's vendor collections

`FusionShortcut` and `OSDwindow` both call `RegisterRawInputDevices` with
`RIDEV_INPUTSINK` for five usages:

| UsagePage | Usage | What it carries |
|---|---|---|
| `0x0001` | `0x0006` | standard keyboard |
| `0x0001` | `0x0002` | mouse |
| `0xFF01` | `0x2209` | vendor collection |
| `0xFF02` | `0x0001` | **the Fn hotkey collection** |
| `0xFF00` | `0xFF00` | vendor collection |

On this machine those match the enumerated collections: `mi_02&col03` is
`0xFF02/0x0001` with a 4-byte input report, and `mi_02&col07` is `0xFF00/0xFF00`
with 9-byte input and output reports.

`WM_INPUT` messages are then dispatched by report size:

**4-byte reports** (`dwSizHid == 4`), bytes named `bRawData1..4`:

| Pattern | Meaning |
|---|---|
| `4, 0, 0, 137` | launch recovery (`RecoveryF9.exe`) |
| `4, 0, 0, 138` | launch update-all |
| `4, 0, 0, 139` | update-all with `/default` |
| `4, 1, 0` / `4, 1, 25` / `4, 1, 50` | keyboard backlight level changed to 0 / 50 / 100 % |
| `_, _, _, 37` | fan mode → stealth |
| `_, _, _, 38` | fan mode → auto low |
| `_, _, _, 39` | fan mode → auto high |

**9-byte reports** (`dwSizHid == 9`):

| Pattern | Meaning |
|---|---|
| `9, _, _, 23` | firmware version reply; bytes 7 and 8 are BCD nibbles `a.b.c.d` |
| `9, _, 1, 3` | display brightness changed; byte 6 carries the level |

### 2. WMI events

`OSDwindow` subscribes to `SELECT * FROM GB_WMIACPI_Event` (and, on newer platforms,
`GMC_WMIEvent`) and switches on the event's `Data` value:

| Data | Meaning |
|---|---|
| `202` | touchpad disabled |
| `458` | touchpad enabled |
| `450` | Wi-Fi enabled |
| `194` | Wi-Fi disabled |

The same class also delivers a brightness event carrying a `Brightness` property.

## Why the current OSD is annoying

Volume keys on this keyboard are a **standard HID consumer-control collection**
(`mi_02&col04`, UsagePage `0x000C`), so Windows already draws its own volume overlay.
`OSDwindow.exe` listens to the same input and draws a second, layered window on top.
The result is two overlays for one keypress. Display brightness has the same problem.

The fix is not to draw a prettier overlay. It is to draw nothing for anything Windows
already handles, and to draw a small overlay only for the things Windows knows nothing
about: fan mode, keyboard backlight level, and the touchpad and Wi-Fi toggles.

## Design implications for v0.3

- A hidden message-only window registering the five raw-input usages, plus a
  `ManagementEventWatcher` on `GB_WMIACPI_Event`. Both are cheap and need no polling.
- Raw-input registration needs no elevation. The WMI event subscription does.
- Map hotkeys to actions the app already owns: fan mode cycling calls `FanController`,
  backlight level calls the lighting controller from v0.2.
- An OSD that is off by default, with a per-signal opt-in, so the owner can enable it
  for fan mode only and never see a duplicate volume overlay again.
- Debounce: the two channels can report the same event, so a signal seen on both within
  a short window must fire once.

## Open questions

- Whether the 37/38/39 fan codes are emitted by this chassis or only by older ones.
- What the `0xFF00/0xFF00` 9-byte output report is for; Gigabyte writes to it but the
  decompiled code path is unclear.
- Whether the Fn key itself can be observed, or only the resulting combinations.

## Observed on hardware — AORUS 17G KD, 2026-09-07

Everything above this heading was recovered from Gigabyte's binaries. Everything below was
**observed on a real machine** through the diagnostics trace, and where the two disagree,
this section wins for this chassis.

### The channel works

`reports=108 unreadable-packets=0 events=0 unreadable-events=0 faults=0`

Raw input registers, the message window receives, and `RawInputBuffer` read every packet.
So the 24-byte x64 `RAWINPUTHEADER`, the `RAWHID` prologue offsets and the report walk are
all correct on this hardware.

### The byte indexing is correct

Every report is four bytes beginning `04`, and the first byte equals the report length.
That is the internal consistency check the two recovered tables offered, and the hardware
agrees with it. `bRawData1 == report[0]` is confirmed, not merely assumed.

### The key codes are NOT the ones in the tables above

The recovered tables give 137/138/139 for the launcher keys and 37/38/39 for fan modes.
This chassis sends none of those. Observed instead, all on the 4-byte shape:

| Bytes | Decimal 4th | Key | How it was established |
|---|---|---|---|
| `04 00 00 7D` then `04 00 00 00` | 125 | **Brightness down** | repeated presses, then by elimination |
| `04 00 00 7E` then `04 00 00 00` | 126 | **Brightness up** | four isolated presses, nothing else pressed |
| `04 00 01 86` then `04 00 00 86` | 134 | unidentified | third byte toggles 1 then 0 |
| `04 00 01 87` then `04 00 00 87` | 135 | unidentified | third byte toggles 1 then 0 |

Two release conventions exist. The brightness keys clear the code (`04 00 00 00`); the
`86`/`87` keys keep the code and toggle **byte 2** from 1 to 0. So byte 2 is a press/release
flag for that class of key, and a decoder must not treat a non-zero byte 2 as a mismatch.

### The firmware reports brightness but does not apply it

This is the finding that changes a design decision. The panel brightness does **not** change
when these keys are pressed with Gigabyte's software stopped. The firmware only reports the
intent; Control Center was performing the change in software. `GetBrightness` on the WMI
interface answers `Invalid object` on this model, so the embedded controller does not expose
it either.

Consequence: an app that wants the brightness keys to work must set the panel brightness
itself, and because that change does not travel through Windows' own hotkey path, Windows
draws no overlay for it. The rule "never draw for brightness because Windows already does"
was written from the decompiled behaviour and is **false on this chassis**.

### What was built from this

Acted on rather than merely recorded, in `feat(hotkeys)` after this file was written:

- `HotkeySignal.PanelBrightnessUp` / `PanelBrightnessDown` decode from `04 00 00 7E` and
  `04 00 00 7D`. The **press only** - the release `04 00 00 00` decodes to nothing, which
  is what makes one tap one step
- `OpenAorus.Hardware.Display` sets the brightness through
  `WmiMonitorBrightnessMethods.WmiSetBrightness` in `root\WMI`, reading the current level
  and the supported levels from `WmiMonitorBrightness`. Windows' classes, not Gigabyte's,
  because `GetBrightness` answers `Invalid object` here. It needs no elevation
- One press moves a tenth of the reported ladder, never less than one of its levels, so a
  panel reporting all of 0-100 moves ten points and a panel reporting six stops moves one.
  Nothing is ever written that the panel did not list
- A machine with no `WmiMonitorBrightness` instance - a desktop, an external monitor - does
  nothing and says nothing
- The overlay draws for these two keys, and it is the one overlay that defaults **on**. The
  rule it appears to break was "do not draw a second card over one Windows already draws",
  and here there is no first card: see `HotkeySettings.OverlayForPanelBrightness`. The
  9-byte `DisplayBrightness` report above is still refused, because it means the opposite -
  that something else already made the change

Still open: **134 and 135**. Nothing decodes them and nothing acts on them, deliberately.
`HotkeyTrace` writes every report down by its bytes before anything decodes it, so both
halves of each - `04 00 01 86` and `04 00 00 86`, `04 00 01 87` and `04 00 00 87` - appear
in the diagnostics dump. Whoever identifies them next should start there.

### The WMI event channel produced nothing

`events=0` across 108 reports. Either this chassis does not use `GB_WMIACPI_Event` for
hotkeys, or the keys that would raise it (touchpad, Wi-Fi) were not among those pressed.
Not yet settled.

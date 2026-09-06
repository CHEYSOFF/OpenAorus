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

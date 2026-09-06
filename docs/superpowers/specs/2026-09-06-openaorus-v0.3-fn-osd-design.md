# OpenAorus v0.3 — Fn hotkeys and the OSD problem — Design (draft)

Date: 2026-09-06
Status: approved; scheduled after the fan-safety work.
Research: `docs/research/fn-hotkey-signals.md`

## 1. Goal

Make the Fn row work without Gigabyte's software, and stop the duplicate on-screen
display. The owner's original complaint was specific: Gigabyte draws its own volume
overlay on top of the one Windows already draws.

In scope:

- Listen to both hotkey channels: raw input on the keyboard's vendor collections and
  the `GB_WMIACPI_Event` WMI event stream
- Act on the hotkeys the app can actually service: fan mode, keyboard backlight level,
  touchpad and Wi-Fi state changes
- An optional, minimal overlay that is **off by default** and can be enabled per signal
- Never draw an overlay for volume or display brightness, because Windows already does

Out of scope:

- Remapping keys or macro recording
- Replacing Windows' own overlays
- Anything that requires a keyboard filter driver

## 2. The two channels

**Raw input.** A message-only window registers five usages with `RIDEV_INPUTSINK`, the
same set Gigabyte's own shortcut process registers: `0001/0006`, `0001/0002`,
`FF01/2209`, `FF02/0001` and `FF00/FF00`. Fn events arrive as `WM_INPUT` with a 4-byte
or 9-byte HID report. The decoded meanings are in the research note; the ones that
matter here are the fan-mode codes, the backlight-level codes and the brightness
report. Raw input needs no elevation.

**WMI events.** `SELECT * FROM GB_WMIACPI_Event` delivers a `Data` value for touchpad
and Wi-Fi transitions. This subscription needs elevation, which the app already has.

Both channels can report the same physical keypress, so every signal passes through a
debounce keyed on signal identity with a short window. One keypress, one action, one
overlay at most.

## 3. Architecture

```
src/OpenAorus.Hardware/Hotkeys/HotkeySignal.cs      enum of the signals we understand
src/OpenAorus.Hardware/Hotkeys/RawInputDecoder.cs   pure: HID report bytes -> HotkeySignal?
src/OpenAorus.Hardware/Hotkeys/WmiEventDecoder.cs   pure: event Data value -> HotkeySignal?
src/OpenAorus.Hardware/Hotkeys/SignalDebouncer.cs   pure: signal + timestamp -> fire or drop
src/OpenAorus.App/Hotkeys/RawInputWindow.cs         message-only HWND, registration, WM_INPUT
src/OpenAorus.App/Hotkeys/HotkeyService.cs          wires both channels to actions
src/OpenAorus.App/Views/OverlayWindow.xaml(.cs)     the minimal overlay
```

The three decoders are pure functions over bytes, so the whole hotkey protocol is
unit-testable without a keyboard — the same discipline that made the fan sequences and
the lighting packets testable.

## 4. Behaviour

- **Fan mode hotkey:** cycles Quiet → Normal → Gaming → Turbo and applies it through the
  existing `FanController`, so the app's own state and the tray tooltip stay correct.
- **Backlight hotkey:** the keyboard changes its own brightness in firmware; the app
  reads the reported level and updates the lighting view model so the slider agrees.
- **Touchpad and Wi-Fi:** display-only. The firmware already performed the toggle; the
  app just reports it if the overlay is enabled.
- **Volume and display brightness:** decoded, then deliberately ignored. No overlay,
  no action. This is the fix for the original complaint.
- **Overlay:** a small, borderless, click-through window near the bottom centre, fading
  after about a second. Off by default. Settings gains one checkbox per signal.

## 5. Testing

- `RawInputDecoder` tests: every documented byte pattern maps to the right signal, and
  unknown patterns map to null rather than throwing.
- `WmiEventDecoder` tests: the four known `Data` values, plus an unknown value.
- `SignalDebouncer` tests: the same signal twice inside the window fires once; outside
  the window fires twice; different signals never suppress each other.
- Owner verification: each Fn combination produces exactly one action and at most one
  overlay, and the volume keys show only the Windows overlay.

## 6. Risks

- The fan-mode hotkey codes may not be emitted by this chassis; the research note flags
  them as unconfirmed. If they are absent, the feature degrades to the other signals and
  the app keeps working.
- `RIDEV_INPUTSINK` receives input regardless of focus, which is what makes a global
  hotkey work but also means a decoding bug could act on unrelated input. The decoders
  return null for anything they do not recognise exactly, and the debouncer bounds the
  blast radius.

# OpenAorus v0.2 — Keyboard RGB — Design

Date: 2026-09-06
Status: drafted while v0.1 was being built; owner approved the scope (spec, plan and
implementation of effects and per-key) before starting
Target machine: GIGABYTE AORUS 17G KD, keyboard "Fusion RGB KB" `1044:7A3D`

## 1. Goal

Replace Gigabyte Control Center's keyboard lighting for the Ione-family per-key
keyboards, inside the existing OpenAorus tray app. When v0.2 ships, the owner should
have no reason to open GCC for lighting either.

In scope:

- Detect the lighting HID interface and expose it as a first-class device
- All 18 built-in effects with their real parameters (colour, brightness, speed,
  direction, random) rather than a subset
- Per-key custom mode with a keyboard-shaped editor and paint/pick interaction
- Named lighting presets saved alongside the fan settings, re-applied at startup
  and after resume
- Brightness control that works without changing the current effect
- Graceful absence: a machine with no supported keyboard simply does not show the tab

Out of scope for v0.2:

- The chassis light bar and logo LED (`SetLightBar` on the WMI side)
- ITE-family keyboards (`7A38`/`7A39`/`7A3B`), 9-byte reports, different protocol
- Reacting to audio, screen colour or game events
- Sharing profiles between machines

## 2. Hardware interface

No elevation required: the lighting path is plain HID, unlike the fan WMI.

- Accept `VID 0x1044` or `0x0414`; `PID` in `{0x7A3C, 0x7A3D, 0x7A3F}`.
- Select the collection with `UsagePage 0xFF01`, `Usage 0x0001` and
  `FeatureReportByteLength == 264`. Never guess by path string: `HidP_GetCaps` is the
  reliable discriminator, and the same PID exposes ten collections.
- Every exchange is a 264-byte `HidD_SetFeature` (and `HidD_GetFeature` for reads),
  report id `0x07` in byte 0, command in byte 1.
- Pace writes at 65 ms, matching GCC. The keyboard drops reports sent faster.

Full protocol, including the mode-offset table, per-mode configuration shapes and the
two-report per-key plane format, is in `docs/research/ione-keyboard-protocol.md`.

### 2.1 Commands

| Cmd | Direction | Purpose |
|---|---|---|
| `0x02` | write | set effect + its configuration + brightness |
| `0x82` | write then read | read current status |
| `0x06` | write ×2 | write 128 per-key colours (`[3]=1` reds+greens, `[3]=2` blues) |
| `0x86` | write+read ×2 | read 128 per-key colours |
| `0x8A` | write | clear stored configuration |

### 2.2 Effects

18 effects, ids `0x00`–`0x12`. Each owns a slice of the report at
`13 + offset[id]`, so changing one effect's settings leaves the others intact.
Parameter shapes fall into six families (single colour, colour+speed,
colour+speed+random, speed+direction, speed only, two colours), plus Custom which
carries no inline configuration.

Speed is inverted on the wire: `wire = 10 - round(ui / 10)` for a 0–100 UI value.

### 2.3 Per-key custom

128 slots, RGB, transmitted plane-major (128 reds, 128 greens, 128 blues) across two
reports. Slot order depends on the physical layout; GCC flags `7A3D` as the UK order
and `7A3C` as US. v0.2 ships both maps and a layout override in settings, because the
mapping cannot be verified without the hardware.

## 3. Architecture

New code, all inside the existing two projects:

```
src/OpenAorus.Hardware/Lighting/
  IKeyboardHid.cs          seam: SetFeature(byte[264]) / GetFeature() -> byte[264]
  KeyboardHid.cs           SetupAPI + HidD_* discovery and IO (real impl)
  LightingDevice.cs        device identity, presence, layout
  LightEffect.cs           enum of the 18 effects
  EffectParameters.cs      colour(s), speed, direction, random, brightness
  EffectPacket.cs          pure: parameters -> 264-byte report (the whole protocol)
  KeyLayout.cs             128-slot maps for ENG-US and ENG-UK, name <-> index
  PerKeyPacket.cs          pure: 128 colours -> the two 264-byte reports, and back
  LightingController.cs    apply effect / apply per-key / read back / brightness
src/OpenAorus.App/ViewModels/LightingViewModel.cs
src/OpenAorus.App/ViewModels/PerKeyEditorViewModel.cs
src/OpenAorus.App/Views/LightingPanel.xaml
src/OpenAorus.App/Views/KeyboardCanvas.xaml     keyboard-shaped per-key editor
```

`EffectPacket` and `PerKeyPacket` are pure byte-array builders with no IO, so the
entire protocol is unit-testable against the documented layout — the same approach
that made the fan sequences testable in v0.1. `KeyboardHid` is the only file allowed
to call `hid.dll` or `setupapi.dll`.

The main window grows a second tab or a segmented control: Cooling | Lighting. The
window stays the same width; only its height grows when Lighting is selected.

## 4. Behaviour

- **Presence:** at startup `LightingDevice.Detect()` looks for a matching collection.
  Absent → the Lighting section is hidden entirely and nothing else changes.
- **Applying an effect:** build the report, write it, then read back with `0x82` and
  reconcile the UI. A failed write shows the same red banner v0.1 uses.
- **Brightness:** re-sends the current effect with the new brightness, because the
  protocol has no standalone brightness command.
- **Per-key:** the editor paints colours onto a keyboard-shaped grid. Apply writes the
  two reports, then selects effect `0x12`. A "Read from keyboard" button pulls the
  current 128 colours back so the editor starts from what the hardware holds.
- **Layout:** defaults to the map GCC implies for the detected PID, with a manual
  override (US / UK) in settings for owners whose keyboard disagrees.
- **Persistence:** the active effect, its parameters and the per-key colours are saved
  in the existing `settings.json` and re-applied at startup and after resume, next to
  the fan mode.
- **Presets:** named snapshots of effect + parameters + per-key colours, listed in the
  Lighting panel. v0.2 ships three: Off, Warm White, and the owner's last custom.

## 5. Testing

- `EffectPacket` tests: one per effect family, asserting the exact 264 bytes for known
  inputs, including the offset table, the speed inversion and the brightness byte.
- `PerKeyPacket` tests: round-trip 128 colours through build and parse, and pin the
  plane-major order and the two headers.
- `KeyLayout` tests: both maps have 128 entries, unique non-`N/A` names, and the two
  layouts differ only where expected.
- `LightingController` tests against a fake `IKeyboardHid` that records reports.
- Manual, owner only: every effect visibly matches its name; per-key colours land on
  the right keys (this is the test that settles the US/UK question).

## 6. Risks

- The per-key slot map is unverified on this exact model. Mitigation: ship both maps,
  an override, and a "paint one key at a time" mode that makes a wrong map obvious in
  seconds.
- Direction encodings per effect are only partly known; wrong values are cosmetic and
  fixable once the owner sees them.
- Writing effects too fast can wedge the keyboard's controller until replug. Mitigation:
  a serialized 65 ms-paced queue, same shape as the fan write queue.
- GCC's own lighting service may fight over the device while it runs; v0.1's takeover
  already stops those processes.

## 6a. Amendment: effect writes are read-modify-write

Added after the Task 2 review, which found that the original design silently assumed
something it never stated.

Every effect stores its configuration in its own slice of one shared block, and command
`0x82` reads that whole 264-byte block back. The keyboard therefore holds the block. It
follows that sending a freshly zeroed buffer with only the active effect's slice filled
would erase every other effect's saved configuration on each switch: set up Wave, switch
to Ripple, come back, and Wave is at defaults.

`LightingController` must instead read with `0x82`, patch only the active slice, and write
back with `0x02`. If the read fails it falls back to a zeroed buffer, which is the current
behaviour and no worse than it.

This is correct whether the firmware persists the whole block as received or reads only
the slice belonging to the selected mode, so it does not wait on hardware to justify. It
costs one extra feature report per change, well inside the 65 ms pacing budget.

`EffectPacket` accordingly needs an overload that patches a caller-supplied buffer rather
than only allocating a new one.

Per-key colours are unaffected: they live in separate storage and survive a `0x02` write.

## 7. Attribution

The protocol was reconstructed from Gigabyte's own binaries and cross-checked against
`rcassani/keyboard-fusion-rgb` (GPL-3.0). OpenAorus is GPL-3.0, so the licences are
compatible. Credit belongs in the README and at the top of `EffectPacket.cs`.

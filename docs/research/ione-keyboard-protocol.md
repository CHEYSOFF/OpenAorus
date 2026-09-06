# Keyboard RGB research — AORUS 17G KD ("Fusion RGB KB", Ione family)

Sources: decompiled `KeyboardModel2023.dll` (`IoneKeyBoard`, `GenericKeyBoard`) and
`FusionKeyboard.dll` from the installed Gigabyte software, cross-checked against
[rcassani/keyboard-fusion-rgb](https://github.com/rcassani/keyboard-fusion-rgb)
(GPL-3.0, PID 7A3C, same Ione family). The two agree on every field checked.

## Device

- USB `VID 0x1044` (Chu Yuen) — some units enumerate as `0x0414`; both must be accepted.
- `PID 0x7A3D` on this machine. Sibling Ione PIDs: `7A3C`, `7A3F`.
- Product string: `Fusion RGB KB`.
- Lighting lives on HID collection **`mi_02&col06`**: `UsagePage 0xFF01`, `Usage 0x0001`,
  **feature report length 264**, no input/output reports. Verified on the machine:
  `\\?\hid#vid_1044&pid_7a3d&mi_02&col06#...`
- GCC's `GenericKeyBoard` sets `isExistIone = true` and **`isUK = true`** for `7A3D`
  (7A3C gets `isUK = false`), so the 17G KD very likely uses the ENG-UK key order.
  This must be confirmed on hardware before per-key colours are trusted.
- Access needs no elevation (plain `CreateFile` + `HidD_SetFeature`), unlike the fan WMI.

## Packet format (all writes are 264-byte HID **feature** reports)

Byte 0 is the report id `0x07`. Byte 1 is the command:

| Cmd | Meaning | Payload |
|---|---|---|
| `0x02` | set mode + its configuration | see below |
| `0x82` | read current status | write then `HidD_GetFeature` 264 |
| `0x86` | read per-key colours | `[3] = 1` then `[3] = 2`, two reads |
| `0x06` | write per-key colours | `[3] = 1` then `[3] = 2`, two writes |
| `0x8A` | clear stored configuration | rest zero |
| `0x17` | firmware version | GCC only; response carries a BCD version |

### Set mode (`0x02`)

```
[0]  0x07            report id
[1]  0x02            command
[2..9]   0x00
[10] mode            0x00..0x12
[11] 0x00            (GCC writes 0xFF here for Static and StarShining; harmless)
[12] brightness      0..100
[13 + offset[mode] .. ]  mode configuration bytes
rest zero, total 264
```

`offset[mode]` (each mode owns its own slice, so switching modes keeps the others' settings):

| mode | 00 | 01 | 02 | 03 | 04 | 05 | 06 | 07 | 08 | 09 | 0A | 0B | 0C | 0D | 0E | 0F | 10 | 11 | 12 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| offset | 0 | 4 | 9 | 11 | 16 | 21 | 26 | 27 | 32 | 37 | 43 | 48 | 54 | 59 | 68 | 76 | 78 | 86 | 0 |

Modes: `00` Static, `01` Breathing, `02` Flow, `03` Firework, `04` Ripple, `05` Rain,
`06` Cycling, `07` Trigger, `08` Pulse, `09` Radar, `0A` Star Shining, `0B` Wave,
`0C` Cross, `0D` Dragonstrike, `0E` Bloom, `0F` Spiral, `10` Merge, `11` Crash,
`12` Custom (per-key).

Configuration buffers, by shape:

- Static: `[0x00, R, G, B]`
- Colour + speed (Breathing, Ripple): `[speed, mode?, R, G, B]`
- Colour + speed + random (Firework, Rain, Trigger, Pulse, Star, Cross): `[speed, random, R, G, B]`
- Speed + direction only (Flow, Spiral): `[speed, direction]`
- Speed only (Cycling): `[speed]`
- Two colours (Dragonstrike, Bloom, Merge, Crash): `[speed, random, (direction), R1, G1, B1, R2, G2, B2]`
- Wave: `[speed, random, direction, R, G, B]`
- Custom: no configuration bytes; the colours are written separately (below)

Speed is inverted before sending: `wire = 10 - round(ui_speed / 10)` for a 0..100 UI value.
Direction encodings differ per mode; GCC swaps 2 and 3 for Flow. Each mode's direction
set must be checked against hardware.

GCC sleeps 65 ms after each feature report; the reference client uses 10 ms.

### Per-key custom (`0x12` + commands `0x06` / `0x86`)

128 key slots, each RGB. Colours are sent **plane by plane** (all Reds, then all
Greens, then all Blues — column-major over a 128×3 array), split across two reports:

```
write 1: [0x07, 0x06, 0x00, 0x01] + 4 zero bytes + 256 bytes (128 R then 128 G)
write 2: [0x07, 0x06, 0x00, 0x02] + 4 zero bytes + 128 bytes (128 B) + 128 zero bytes
```

Reading back uses `0x86` with the same `[3] = 1 / 2` split; each response's first 8
bytes are a header, and the two payloads concatenate into the same 384-byte plane order.

Selecting Custom mode is a normal `0x02` write with mode `0x12` and a brightness; the
colours themselves persist in the keyboard.

Key-slot order for ENG-US and ENG-UK is in `ione-keymap-eng-us.txt` /
`ione-keymap-eng-uk.txt` (128 entries each, `N/A` for unpopulated slots; about 101 real
keys). The two layouts differ only in a few slots.

## What is still unknown

- Whether the 17G KD really uses the ENG-UK slot order (GCC's `isUK` flag says yes).
- Exact direction encodings per mode.
- Whether the light bar / logo LED on this chassis is a separate zone (the WMI side has
  `SetLightBar`, unused in v0.1).

## Licensing note

`keyboard-fusion-rgb` is GPL-3.0 and OpenAorus is GPL-3.0, so the licences are
compatible. Only protocol facts were taken, not code; the project must be credited in
the README and in the source file that implements this protocol.

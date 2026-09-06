# OpenAorus

Open-source, single-executable replacement for the fan, sensor and battery
features of Gigabyte Control Center on Gigabyte AORUS / AERO laptops.
Think [G-Helper](https://github.com/seerge/g-helper), but for Gigabyte.

**Status: pre-alpha, v0.1. Unverified on real hardware — see [Verification status](#verification-status) below.**

## Why

Gigabyte Control Center is slow to open, breaks between versions, fights you
over fan modes ("AI" mode re-applies itself), and paints its own volume OSD on
top of the Windows one. Everything it does to the fans and battery goes through
one ACPI-WMI interface that any elevated program can call. So this does.

## Features (v0.1)

- Fan modes: Quiet, Normal, Gaming, Turbo, Fixed duty, and a custom 15-point
  curve (the curve runs in the embedded controller, so it survives the app
  closing)
- Live CPU/GPU temperatures, fan RPM and duty in the window and tray tooltip
- Battery charge limit (60-100 %)
- Tray app with a `--dump` diagnostics command
- "Start with Windows" — a scheduled task that launches silently, elevated, in
  the tray (no UAC prompt after first setup)
- One-click "take over from Gigabyte Control Center" (reversible), which stops
  GCC's own processes, service and scheduled task so it stops overriding
  whatever fan mode you picked
- Re-applies your saved mode and charge limit automatically after sleep/resume

Not in v0.1: keyboard RGB, Fn hotkeys, per-app profiles, power limits.

## Verification status

v0.1 was built and unit-tested (82 tests) on a machine that cannot elevate and
has no Gigabyte hardware attached, so **nothing here has been confirmed against
a real embedded controller yet** — not the fan modes, not the custom curve,
not the battery charge limit, not autostart, not sleep/resume behaviour.
Everything is implemented against the protocol reverse-engineered from
Gigabyte Control Center and covered by unit tests against a fake WMI layer,
which is not the same thing as a fan spinning up on a real laptop.

[`VERIFY.md`](VERIFY.md) lists every check that still needs to happen on
actual hardware before this should be considered trustworthy. If you run any
of those checks, please report back — a passing or failing checklist is
useful either way.

## Install

Download `OpenAorus-vX.Y.Z-win-x64.exe` from Releases (or the smaller
`-framework-dependent` build if you have the .NET 8 Desktop Runtime). Run it;
accept the UAC prompt. In ⚙ Settings turn on **Start with Windows** to get a
silent elevated tray start, and **Take over from Gigabyte Control Center** so
GCC stops fighting your fan mode. Keep GCC installed: it provides the WMI schema
(`acpimof.dll`) that OpenAorus talks to. Removing that dependency is a future
item, not a v0.1 feature.

Command-line flags: `--tray` starts hidden in the tray (used by the autostart
task), `--show` forces the window visible even together with `--tray`,
`--dump` writes a diagnostics report and exits, `--apply` re-applies the saved
mode and exits. No flags at all also starts visible.

## Which laptops

This talks to the `GB_WMIACPI` WMI interface, which is not universal across
Gigabyte's laptop lineup. Per the device list in
[tangalbert919/gigabyte-laptop-wmi](https://github.com/tangalbert919/gigabyte-laptop-wmi):

- **Supported**: AERO, AORUS, and GIGABYTE Gaming from 2025 onwards (e.g. the
  A16), plus some P-series machines.
- **Not supported at all, and never will be by this app**: Sabre, GIGABYTE
  Gaming 2024 and earlier (G5, G6, G7), and the U series. These are rebadged
  Clevo laptops with a completely different embedded-controller interface.
  If you own one, try [NoteBookFanControl](https://github.com/hirschmann/nbfc)
  instead — it is a community-configured embedded-controller fan tool rather
  than a Gigabyte or Clevo specific one, and it ships configurations covering
  many Clevo-built machines.

Even within the supported families, the fan-duty scale (how many discrete
steps the controller accepts) varies by model. Only the **AORUS 17G KD** is a
tested profile; every other model gets a yellow warning banner in the app,
runs read/write against a best-guess duty scale, and should be treated as
experimental. If you have a different supported model, please run
`OpenAorus.exe --dump > dump.txt` from an elevated terminal and open an issue
with the file and your exact model name — that's how more profiles get added.

## Safety

OpenAorus only calls the same `GB_WMIACPI` methods Gigabyte Control Center
calls, never writes fan duty above the model's maximum, and the custom curve
runs inside the embedded controller, so closing the app does not stop the fans.

## Docs

- [v0.1 design](docs/superpowers/specs/2026-09-06-openaorus-v0.1-design.md)
- [Research notes](docs/research/2026-09-05-research-notes.md) and the full
  [WMI method table](docs/research/gb-wmiacpi-methods-aorus-17g-kd.txt)
- [`VERIFY.md`](VERIFY.md) — the hardware verification checklist

## Credits

Protocol knowledge builds on
[tangalbert919/gigabyte-laptop-wmi](https://github.com/tangalbert919/gigabyte-laptop-wmi),
[s-h-a-d-o-w/alfc](https://github.com/s-h-a-d-o-w/alfc),
[wtwrp/aeroctl](https://gitlab.com/wtwrp/aeroctl) and
[rcassani/keyboard-fusion-rgb](https://github.com/rcassani/keyboard-fusion-rgb).

## License

GPL-3.0. Not affiliated with GIGABYTE.

# OpenAorus

Open-source, single-executable replacement for the fan, sensor and battery
features of Gigabyte Control Center on Gigabyte AORUS / AERO laptops.
Think [G-Helper](https://github.com/seerge/g-helper), but for Gigabyte.

**Status: pre-alpha, v0.1 in development.** Tested only on an AORUS 17G KD so far.

## Why

Gigabyte Control Center is slow to open, breaks between versions, fights you
over fan modes ("AI" mode re-applies itself), and paints its own volume OSD on
top of the Windows one. Everything it does to the fans and battery goes through
one ACPI-WMI interface that any elevated program can call. So this does.

## Planned for v0.1

- Fan modes: Quiet, Normal, Gaming, Turbo, Fixed duty, Custom 15-point curve
  (the curve runs in the embedded controller, so it survives the app closing)
- Live CPU/GPU temps, fan RPM and duty in the window and tray tooltip
- Battery charge limit (60-100 %)
- Tray app, starts with Windows without a UAC prompt after first setup
- One-click "take over from Gigabyte Control Center" (reversible)

Not in v0.1: keyboard RGB, Fn hotkeys, per-app profiles, power limits.

## Which laptops

Any Gigabyte laptop exposing the `GB_WMIACPI` WMI classes: AORUS, AERO and the
2025+ GIGABYTE Gaming line. Per-model fan-duty scales differ, so untested
models get a warning banner. If you have one, run `OpenAorus.exe --dump` and
open an issue with the output.

## Docs

- [v0.1 design](docs/superpowers/specs/2026-09-06-openaorus-v0.1-design.md)
- [Research notes](docs/research/2026-09-05-research-notes.md) and the full
  [WMI method table](docs/research/gb-wmiacpi-methods-aorus-17g-kd.txt)

## Credits

Protocol knowledge builds on
[tangalbert919/gigabyte-laptop-wmi](https://github.com/tangalbert919/gigabyte-laptop-wmi),
[s-h-a-d-o-w/alfc](https://github.com/s-h-a-d-o-w/alfc),
[wtwrp/aeroctl](https://gitlab.com/wtwrp/aeroctl) and
[rcassani/keyboard-fusion-rgb](https://github.com/rcassani/keyboard-fusion-rgb).

## License

GPL-3.0. Not affiliated with GIGABYTE.

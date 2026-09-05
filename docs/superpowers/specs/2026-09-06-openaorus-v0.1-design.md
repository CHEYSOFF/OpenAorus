# OpenAorus v0.1 — Design

Date: 2026-09-06
Status: approved by owner, ready for implementation planning
Target machine: GIGABYTE AORUS 17G KD (i7-11800H, RTX 3060 Laptop, BIOS FB07, Windows 11)

## 1. Goal

An open-source, single-executable replacement for the fan, sensor and battery
features of Gigabyte Control Center (GCC) on Gigabyte laptops. v0.1 must be good
enough for the owner to stop opening GCC day to day.

In scope for v0.1:

- Fan modes: Quiet, Normal, Gaming, Turbo, Fixed duty, Custom (EC-native curve)
- Live sensors: CPU temp, GPU temp, fan 1/2 RPM, fan duty
- Battery charge policy and 60–100 % charge stop
- Tray app with one compact window, start-with-Windows, re-apply on boot/resume
- "Take over from GCC" toggle that disables GCC's background components
- Unknown-model read-only mode with a diagnostic dump

Explicitly out of scope for v0.1 (later versions):

- Keyboard RGB (Fusion RGB KB, 264-byte HID feature reports)
- Fn hotkey handling and OSD
- Per-app profiles (GCC's "AI" mode)
- CPU/GPU power limits, GPU boost, light bar
- Driver updates
- Shipping our own WMI MOF registration (v0.1 assumes GCC remains installed)

## 2. Hardware interface

All EC access goes through the ACPI-WMI classes GCC uses:

| Class | GUID | Role |
|---|---|---|
| `root\WMI:GB_WMIACPI_Get` | `ABBC0F6F-8EA1-11d1-00A0-C90629100000` | reads |
| `root\WMI:GB_WMIACPI_Set` | `ABBC0F75-8EA1-11d1-00A0-C90629100000` | writes |
| `root\WMI:GB_WMIACPI_Event` | `ABBC0F72-8EA1-11d1-00A0-C90629100000` | hotkey events (unused in v0.1) |

Instance name: `ACPI\PNP0C14\DCK_0`. Full method table for the 17G KD is in
`docs/research/gb-wmiacpi-methods-aorus-17g-kd.txt`.

Access requires an elevated process. There is no kernel driver and no direct
EC I/O in this project.

### 2.1 Fan primitives (GB_WMIACPI_Set unless noted)

| Method | Arg | Meaning |
|---|---|---|
| `SetCurrentFanStep` | byte | always 0 before a mode change |
| `SetFixedFanStatus` | 0/1 | fixed-duty mode on/off |
| `SetStepFanStatus` | 0/1 | "10 steps"/custom table mode on/off |
| `SetAutoFanStatus` | 0/1 | auto-max ("Gaming") on/off |
| `SetNvThermalTarget` | 0/1 | GCC's "quiet mode" flag |
| `SetFixedFanSpeed` | duty | fixed duty for fan 1 |
| `SetGPUFanDuty` | duty | fixed duty for fan 2 |
| `SetFanAdjustStatus` | duty | custom-mode floor duty |
| `SetFanIndexValue` | index, temp, duty | one curve point (index 0..14) |
| `GetFanIndexValue` (Get) | index | read one curve point |
| `GetCPUFanDuty`, `GetGPUFanDuty` (Get) | | current duty |
| `getRpm1`, `getRpm2` (Get) | | RPM, u16, byte-swapped on Gigabyte models |
| `getCpuTemp`, `getGpuTemp1` (Get) | | temps in °C |
| `GetThermalData` (Get) | | three raw sensors, GPU fallback |

Duty scale is 0..`DutyMax`; `DutyMax` = 229 on the 17G KD (Gen_F/G), 100 on some
Gen_H AERO models. The UI always shows percent.

### 2.2 Mode sequences (mirrors GCC `ucNotebook.Helper.FanControl`)

Every sequence starts with `SetCurrentFanStep(0)`. 500 ms between writes, as
GCC does.

| Mode | Fixed | Step | Auto | NvThermalTarget | extra |
|---|---|---|---|---|---|
| Quiet | 0 | 0 | 0 | 1 | |
| Normal | 0 | 0 | 0 | 0 | |
| Gaming | 0 | 0 | 1 | 0 | |
| Turbo | 1 | 1 | 0 | 0 | `SetFixedFanSpeed(DutyMax)`, `SetGPUFanDuty(DutyMax)` before enabling |
| Fixed | 1 | 1 | 0 | 0 | `SetFixedFanSpeed(d)`, `SetGPUFanDuty(d)` |
| Custom | 0 | 1 | 0 | 0 | `SetFanIndexValue(i, temp_i, duty_i)` for each point, then a `(0,0)` terminator if fewer than 15 |

Order for Turbo/Fixed: step0 → Auto=0 → NvThermalTarget=0 → duties → Step=1 → Fixed=1.
Order for Custom: step0 → Fixed=0 → Auto=0 → NvThermalTarget=0 → points → Step=1.

### 2.3 Battery

| Method | Meaning |
|---|---|
| `SetChargePolicy(byte)` / `GetChargePolicy` | 0 = standard, non-zero = custom stop enabled |
| `SetChargeStop(byte)` / `GetChargeStop` | stop percentage 60..100 |
| `GetBatteryCount`, `GetBatteryHealth` | cycles, health byte (display only) |

### 2.4 Model profiles

`ModelProfile` is selected by the SMBIOS product name (`Win32_ComputerSystemProduct.Name`).
Fields: `DutyMax`, `FanCount`, `HasGpuTemp1`, `RpmByteSwapped`, `Tested`.
v0.1 ships one tested profile (`AORUS 17G KD`) and a generic profile for any
name starting with `AORUS`, `AERO`, or `GIGABYTE` (`Tested = false`, same
values as 17G KD). Anything else → read-only mode.

## 3. Architecture

```
OpenAorus.sln
├─ src/OpenAorus.Hardware/        net8.0 class library, no UI references
│   ├─ IGigabyteWmi.cs            Invoke(Get|Set, method, args) → dictionary of out params
│   ├─ GigabyteWmi.cs             System.Management implementation
│   ├─ ModelProfile.cs            capability table + detection
│   ├─ FanController.cs           modes, curve, validation, sequences
│   ├─ SensorReader.cs            temps, RPM, duty
│   ├─ BatteryController.cs       policy + stop
│   └─ Diagnostics.cs             dump every Get* to text
├─ src/OpenAorus.App/             net8.0-windows WPF
│   ├─ Elevation.cs               relaunch with runas if not admin
│   ├─ StartupTask.cs             schtasks logon task, highest privileges
│   ├─ GccTakeover.cs             disable/restore GCC task, Run key, SMV4_Service
│   ├─ Settings.cs                JSON in %LOCALAPPDATA%\OpenAorus\settings.json
│   ├─ TrayIcon + MainWindow      single compact window (see §5)
│   └─ ViewModels/
└─ tests/OpenAorus.Hardware.Tests/ xUnit, FakeGigabyteWmi records calls
```

Rules:

- `OpenAorus.App` never calls `System.Management` directly; everything goes
  through `IGigabyteWmi` so the UI can run against the fake for screenshots.
- All writes go through one `SemaphoreSlim`-guarded queue in `FanController`;
  a second click waits for the first sequence to finish.
- Sensor polling: 1 s while the window is visible, 5 s while hidden.

## 4. Behaviour

- **Startup:** if not elevated → relaunch with `runas`; if the user cancels, show
  a message and exit. Detect model → profile. Load settings. Re-apply the saved
  mode (and curve / fixed duty / charge stop). Start polling. Minimise to tray
  unless started with `--show`.
- **Resume from sleep:** re-apply the saved mode after `PowerModes.Resume`.
- **Mode change:** run the sequence on a background thread, disable the mode
  buttons while it runs, show the result in a status line.
- **Curve editor:** 2–15 points; temperatures strictly increasing 0–100 °C;
  duty non-decreasing 0–100 %; Apply writes and saves; Reset restores defaults.
- **Fixed slider:** 0–100 %, applied on release.
- **Battery:** policy toggle + stop slider (60–100, step 5), applied on release.
- **Take over from GCC:** elevated actions: `schtasks /Change /TN GCC /Disable`,
  delete `HKLM\...\Run\AorusFusion` (value saved to settings for restore),
  `sc stop SMV4_Service` + `sc config start=disabled`, and kill running
  GCC/ControlCenter/FusionStation/FusionShortcut/OSDwindow processes. Undo
  restores each item it recorded.
- **Start with Windows:** creates/deletes logon task `OpenAorus` with
  `/RL HIGHEST` pointing at the current exe.
- **Untested model (name matches AORUS/AERO/GIGABYTE, no tested profile):**
  all controls enabled, persistent yellow banner "Untested model - compare with
  GCC before trusting fan duties" plus an "Export diagnostics" button.
- **Read-only mode (unknown model name):** sensors visible, all write
  controls disabled, banner with "Export diagnostics" writing
  `%LOCALAPPDATA%\OpenAorus\diagnostics-<model>.txt`.
- **CLI:** `--dump` prints the diagnostics to stdout and exits (still needs
  elevation); `--apply` re-applies saved settings and exits (used by the task
  if the user prefers no tray).

## 5. UI

One window, ~420 × 560, dark, no chrome beyond a title bar and close-to-tray.
Top to bottom:

1. Mode row: six toggle buttons (Quiet · Normal · Gaming · Turbo · Fixed · Custom).
2. Sensor row: CPU °C, GPU °C, Fan1 RPM, Fan2 RPM, duty %.
3. Context panel: Fixed → slider; Custom → curve editor (drag points, numeric
   table underneath); other modes → short description of what the EC does.
4. Battery card: policy toggle, stop slider, cycle count.
5. Footer: model name + profile status, Settings gear (start with Windows,
   take over from GCC, poll interval), version.

Tray: left-click toggles the window; right-click menu has the six modes,
"Open", "Quit". Tooltip: `CPU 61° · GPU 55° · 2400/2300 rpm`.

## 6. Error handling

- WMI exceptions are caught at the `IGigabyteWmi` boundary and returned as a
  failed result; the caller decides. UI shows a red banner and red tray icon
  until the next successful call.
- A failed mode sequence leaves the EC in whatever state it reached; the UI
  reports which step failed and offers Retry. No automatic rollback (the EC's
  own defaults are safe).
- Settings file corrupt → rename to `.bad`, start with defaults.
- Model detection failure → read-only mode.

## 7. Testing

- `FakeGigabyteWmi` records `(class, method, args)` and returns scripted values.
- Tests: each mode's exact sequence and order; duty scaling 0–100 → 0–DutyMax
  for both profiles; curve validation cases; RPM byte swap; settings
  round-trip; takeover restore bookkeeping (pure logic part).
- Manual: owner runs each build elevated on the 17G KD; `--dump` output is
  attached to bug reports. The assistant's shell cannot elevate, so every
  hardware claim is verified by the owner, not by the assistant.

## 8. Packaging and release

- `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`
  → `OpenAorus.exe` (expect ~70 MB self-contained; a framework-dependent
  ~2 MB build is also published for users with .NET 8 installed).
- GitHub Actions: build + test on push; publish both artifacts on `v*` tags.
- README: what it is, supported models table, "help me test yours" ask,
  relationship to GCC, safety notes, credits to gigabyte-laptop-wmi, alfc,
  aeroctl, keyboard-fusion-rgb.

## 9. Risks

- GCC's `acpimof.dll` provides the WMI schema; uninstalling GCC removes the
  classes. v0.1 documents "keep GCC installed or install acpimof manually".
- A GCC update may re-enable its task/service; takeover shows a warning if it
  detects GCC running again.
- Untested models may use `DutyMax = 100` or lack `getGpuTemp1`. A wrong
  `DutyMax` on an untested model could under-drive fans (writing 229 where the
  EC expects 100 is clamped by firmware; writing 100 where it expects 229 gives
  ~44 %). Mitigation: the untested banner, the README asking testers to compare
  duty read-back with GCC first, and `--dump` output to build a real profile.

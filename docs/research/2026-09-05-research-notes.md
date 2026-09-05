# Aorus 17G KD — Control Center replacement: research notes (2026-09-05)

## Machine
- GIGABYTE AORUS 17G KD, BIOS FB07 (2021-10), i7-11800H, RTX 3060 Laptop, Win11 26200
- User account is admin-with-UAC (GCC task runs as slobb RunLevel=Highest); Claude shell is unelevated
- Installed: GCC 25.12.10.01 (C:\Program Files\GIGABYTE\Control Center, .NET Fx 4.7.2, WPF+WebView2)
  + legacy "Control Center 25.11.12.01" (C:\Program Files\ControlCenter) with SMV4_Service (LocalSystem), FusionStation/FusionShortcut/OSDwindow
- Intel XTU 7.14 installed

## Hardware interface = ACPI-WMI (no kernel driver needed)
- root\WMI classes: GB_WMIACPI_Get {ABBC0F6F-...} (WMBC), GB_WMIACPI_Set {ABBC0F75-...} (WMBD), GB_WMIACPI_Event {ABBC0F72-...}, GB_WMIACPI_Data
- Instance name: ACPI\PNP0C14\DCK_0. MOF via acpimof.dll registered by GCC (HKLM\SYSTEM\CCS\Services\WmiAcpi MofImagePath) — app must ship/register acpimof.dll or read BMOF if GCC uninstalled (Ixmoon README has registry steps)
- Requires elevation (non-admin → Access denied even for Get). Strategy: UAC once + scheduled task RunLevel Highest for autostart (G-Helper pattern)
- Full method table dumped: tool-results/bx0es4das.txt (Get: fan duty, temps, rpm, charge, thermal; Set: fan modes, curve, charge, RGB, touchpad, etc.)

## Fan modes — exact GCC sequences (from ucNotebook.Helper.FanControl / DeepFan.Helpers)
Primitives (GB_WMIACPI_Set): SetCurrentFanStep(0), SetFixedFanStatus(0/1), SetStepFanStatus(0/1), SetAutoFanStatus(0/1),
  SetNvThermalTarget(0/1) [="QuietMode"], SetFixedFanSpeed(duty), SetGPUFanDuty(duty), SetFanAdjustStatus(duty) [="custom status"],
  SetFanIndexValue(index,temp,speed) [curve points, 15 entries], SetTppStatus (only bThermalTpp models), SetFanModeNotify (AiNexus only), SetSuperQuiet, SetWhisperMode(+NvFunc.dll NVAPI whisper)
- Normal : step0; Fixed=0; Step=0; Auto=0; NvThermalTarget=0
- Quiet/Eco: step0; Fixed=0; Step=0; Auto=0; NvThermalTarget=1
- Gaming/Power: step0; Fixed=0; Step=0; Auto=1; NvThermalTarget=0 (+TPP=1)
- Turbo/Max: step0; Auto=0; Quiet=0; FixedSpeed=229 (Gen_H AERO:100); GPUFanDuty=229; Step=1; Fixed=1
- Custom-Auto (curve floor): Fixed=0; Step=1; Auto=0; SetFanAdjustStatus(duty)   duty = 229*(30+v*5)/100
- Custom-Fixed: Fixed=1; Step=1; Auto=0; SetFixedFanSpeed(duty); SetGPUFanDuty(duty)
- Deep/curve: Fixed=0; Step=1; Auto=0; then SetFanIndexValue(i,temp,speed) for each point until (0,0)
- Duty scale: 0..229 (=100%). Reads: GetCPUFanDuty/GetGPUFanDuty, getRpm1/2 (byte-swapped u16), getCpuTemp, GetThermalData(3 sensors), GetDeepFan(5pt), GetFanIndexValue(i)
- "AI"/auto-profile = per-app hitlist (ControlCenter\AORUS 17G KD\{game,performance,creator,meeting}.xml) + Win32_ProcessStartTrace watcher → applies fan/KB/audio profile. That's the thing that "won't turn off".
- Legacy 15-step custom table stored in HKLM (CustomFanTable, 30 bytes = 15x(temp,pwm))

## Battery
- SetChargePolicy(Data) + SetChargeStop(Data 60..100) via CWMI.CallMethod; Get: GetChargePolicy/GetChargeStop/GetBatteryCount/GetBatteryHealth

## Keyboard (per-key RGB) = "Fusion RGB KB", USB VID 0x1044(=1044 dec 0x0414? no: 0x1044 Chu Yuen) PID 0x7A3D  ("Ione" family: 7A3C/7A3D/7A3F)
- HID collection mi_02&col06: UsagePage 0xFF01, Feature report 264 bytes → HidD_SetFeature/GetFeature
- Protocol (KeyboardModel2023.IoneKeyBoard): buf[0]=0x07, buf[1]=cmd: 0x17 get version, 0x82 get profile (SetFeature then GetFeature), 0x02 write profile
  profile: [10]=effect id 0..18, [11]=0xFF for static/type10, [12]=brightness, effect-specific offsets for speed/mode/dir/rgb (see decomp IoneKeyBoard.cs)
  per-key custom = effect 18 + separate key map packets (see rcassani/keyboard-fusion-rgb, PID 7A3C, 264-byte data, 0x12 custom)
- Fn/hotkey events: RawInput on UsagePage 0xFF02 Usage 1 (col03, 4-byte input reports) → byte4 codes: 37/38/39 fan modes, 137/138/139 recovery, brightness sync; plus WMI GB_WMIACPI_Event Data codes (202/458 touchpad, 450/194 wifi ...)
- Volume keys are standard HID consumer control (col04, UsagePage 0x0C) → Windows shows native OSD; ACC's OSDwindow.exe draws a 2nd one. Solution: just don't.
- Older ITE keyboards (7A38/39/3B/3F): 9-byte feature reports + 65-byte WriteFile

## Prior art
- gitlab wtwrp/aeroctl (C#/WPF, .NET5, GB_WMIACPI, Aero-focused, stale) + forks cabal95/aeroctl, schneidermayer/aeroctl-light (no fan ctl)
- s-h-a-d-o-w/alfc 37★ TS/Node web UI, Aorus 15G, archived 2026-05
- Ixmoon/Gigabyte-Fan-Battery-Center 8★ Python, 15P XD, unmaintained
- tangalbert919/gigabyte-laptop-wmi 77★ Linux kernel driver, active (2026-08); supports AERO/AORUS/GIGABYTE GAMING(2025+)/P-series — best method-ID doc
- rcassani/keyboard-fusion-rgb 9★ Python per-key protocol; rcassani/p37-ec-aorus15g (EC direct)
- Benchmarks: seerge/g-helper 15k★ C# WinForms single exe; LenovoLegionToolkit 7.5k★ C# WPF (archived 2025-07)
- Nobody has a maintained, polished, G-Helper-class Windows app for Gigabyte laptops → real gap

## Applicability
- Any Gigabyte laptop exposing GB_WMIACPI: AERO (14/15/16/17), AORUS (5/7/15/15G/15P/17/17G/17X, 16X/17X 2023+), GIGABYTE GAMING G5/G6/G7/A5/A7 (2025+ per Linux driver; older G-series may lack), some P-series. GCC ucNotebook has model table Gen_E/F/G/H with per-model flags (ThermalTpp, Gpilot/AiNexus, keyboard type PerKey/3zone/1zone/white)
- Method IDs mostly stable across generations; per-model quirks: duty max 229 vs 100, TPP, fan count 2-4, keyboard type (Ione per-key, ITE, Compal 1-zone)

## Reddit
- reddit blocked from WebFetch/Playwright/jina; Chrome extension not connected. Need user to open r/gigabyte, r/GigabyteGaming, r/AORUS, r/GamingLaptops rules pages, or connect Claude-in-Chrome.
- Indirect signals: notebookcheck calls GCC "pixelated and unattractive"; alternativeto lists only OpenRGB; Steam/Tom's threads: "jet engine fans", "doesn't remember settings", "bloatware"


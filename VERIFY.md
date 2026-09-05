# Hardware verification checklist

Everything in OpenAorus that touches the embedded controller was written against the
protocol decompiled from Gigabyte Control Center and is covered by unit tests, but
**no fan, sensor or battery behaviour has been confirmed on real hardware yet**. The
build environment cannot elevate, and every one of these steps needs administrator
rights on the laptop itself.

Work through this list on an AORUS 17G KD (or any Gigabyte laptop) and record what you
see. Anything that disagrees with the expected result is a bug, not a surprise.

Open an elevated terminal at the repo root first, and keep using it for every step.
`dotnet run --project src/OpenAorus.App -- <args>` works there, as does a published
`OpenAorus.exe`.

Starting from a normal terminal is worth avoiding: the app asks Windows to relaunch
itself with administrator rights, and under `dotnet run` the process it relaunches is
the .NET host rather than the app, so nothing useful happens. The published executable
does not have that problem.

## 1. First hardware contact

```
dotnet run --project src/OpenAorus.App -- --dump
```

- [ ] The output starts with `OpenAorus 0.1.0 diagnostics` and a model line reading
      `Model: AORUS 17G KD (Tested, DutyMax=229, Fans=2)`
- [ ] `getCpuTemp` reports a plausible temperature in °C
- [ ] `GetCPUFanDuty` and `GetGPUFanDuty` are between 0 and 229
- [ ] `getRpm1` / `getRpm2` return non-zero raw values while the fans spin
- [ ] The fan table at the end lists 15 slots without errors
- [ ] Compare the reported GPU temperature against Task Manager. If `getGpuTemp1` is 0,
      note which of `Thermal1` / `Thermal2` / `Thermal3` matches, because the fallback
      currently assumes `Thermal2`
- [ ] Save the whole output to `docs/research/dump-aorus-17g-kd.txt` and commit it

## 2. Fan modes

Launch the app: `dotnet run --project src/OpenAorus.App -- --show`

- [ ] The tray icon appears and the window opens at the bottom right
- [ ] Temperatures and RPM update about once a second
- [ ] The status line reads `Applied Normal at startup`
- [ ] **Gaming** makes the fans audibly ramp within a couple of seconds and the duty
      reading rises
- [ ] **Quiet** brings them back down
- [ ] **Turbo** pins both fans at 100 %
- [ ] The tray right-click menu switches modes as well
- [ ] Closing the window hides it to the tray; Quit exits

## 3. Fixed duty and the custom curve

- [ ] Select **Fixed**, drag to 30 %, release. The fans settle and the duty reads about 30 %
- [ ] Select **Custom**, drag the 80 °C point upwards, click Apply. The status line reads
      `Custom applied`
- [ ] Run `--dump` again: the fan table slots match the editor, with each duty equal to
      `round(percent × 229 / 100)`
- [ ] Close the app entirely and confirm the curve still governs the fans, which is the
      whole point of writing it into the controller rather than polling from software

## 4. Battery, autostart and taking over from Gigabyte Control Center

- [ ] Tick **Limit charge**, set 80 %. `--dump` shows `GetChargePolicy: Data=4` and
      `GetChargeStop: Data=80`
- [ ] Untick it: `Data=0` and `Data=100`
- [ ] Leave it at 80 % on AC overnight and confirm the battery stops near 80 %
- [ ] Settings → **Start with Windows** → Toggle. Check the task exists:
      `schtasks /Query /TN OpenAorus /V /FO LIST` shows the highest run level and a logon trigger
- [ ] Log off and back on: the tray icon returns with no UAC prompt, and the saved mode
      is re-applied
- [ ] Settings → **Take over from Gigabyte Control Center** → Toggle, then check:
      `Get-Process GCC,FusionStation -ErrorAction SilentlyContinue` returns nothing,
      `schtasks /Query /TN GCC /FO LIST /V` shows Disabled,
      `Get-Service SMV4_Service` shows Stopped and Disabled
- [ ] With takeover on, pick a fan mode and leave the machine for a few minutes. It must
      still be in that mode; Gigabyte's watcher used to override it
- [ ] Toggle takeover off and confirm all three items are restored

## 5. Sleep and resume

- [ ] Sleep the laptop, wake it, and confirm the status line reads
      `Re-applied <mode> after resume`

## Assumptions this checklist is really testing

Three things were reconstructed from Gigabyte's software and could not be checked
without the laptop. If any of them is wrong, the symptom shows up above.

- **The curve terminator.** A custom curve with fewer than 15 points is followed by a
  `(0, 0)` point, on the assumption that the controller reads its table until it meets
  a zero entry. If step 3's read-back shows stale points after the ones you set, that
  assumption is wrong and the fix is to write all 15 slots explicitly.
- **One duty write per fan pair.** Fixed and Turbo write `SetFixedFanSpeed` and
  `SetGPUFanDuty`. Models with three or four fans also expose `SetFixedFan3Duty` and
  `SetFixedFan4Duty`, which this build never calls. On the 17G KD there are only two
  fans, so this should not matter; on another model, a fan that ignores Fixed mode is
  the tell.
- **The GPU temperature source.** `getGpuTemp1` is read first and `GetThermalData`'s
  second sensor is the fallback. Step 1 asks you to confirm which one actually tracks
  the GPU.

## If something fails

Run `--dump` and attach the output to a note describing which step failed and what
happened instead. The failing step almost always points at one WMI method, and the
method names in the dump line up with the ones in
`docs/research/gb-wmiacpi-methods-aorus-17g-kd.txt`.

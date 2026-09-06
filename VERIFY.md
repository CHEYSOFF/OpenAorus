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

Publish the app first and run the published executable for this section, rather than
`dotnet run`: `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
src/OpenAorus.App`, then run the `OpenAorus.exe` it produces. **Start with Windows** registers a
logon task pointed at the current process's executable path; under `dotnet run` that path is
`dotnet.exe`, not the app, so the task registers and reports itself as enabled (the first
assertion below would pass) but the tray icon never comes back after logon, because dotnet.exe
with no arguments does nothing (the second assertion would then fail, for a reason that has
nothing to do with the app itself). OpenAorus refuses to create the task at all when the exe
path does not end in `OpenAorus.exe`, precisely to stop this from happening quietly.

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

## 5. Keyboard lighting

The switch above the content reads **Cooling | Lighting**. If there is no Lighting button,
the app did not find a supported keyboard collection and nothing below applies.

- [ ] The line under the switch reads `Keyboard connected · ENG-US slot order` or
      `ENG-UK`. Note which; step 5.3 is what decides whether it is right
- [ ] Walk the effect list. Each one visibly matches its name, and the controls that
      appear change with it: Wave offers a direction, Merge offers a second colour,
      Flow offers neither colour box
- [ ] Move **Brightness** with an effect other than Static selected. The keyboard dims
      and brightens without the effect restarting or changing

### 5.1 Colours and presets

- [ ] Pick a swatch, then type a hex value. Both change the keyboard, and a half-typed
      value like `#AB` changes nothing until it is complete
- [ ] Apply **Off**, **Warm White** and **Aorus Orange** in turn; each looks like its name
- [ ] Set up something you like, press **Save current as preset**, switch to another
      effect, then press the new preset. It comes back exactly
- [ ] Press **×** on your own preset: it goes. Press **×** on **Off**: the status line
      says it is built in and it stays

### 5.2 Per-key colours

- [ ] Select the **Custom** effect. The keyboard picture appears
- [ ] **Fill** paints every key, **Apply** sends it. The whole keyboard lights, including
      the keys the picture does not draw - nothing should be left dark that is not
- [ ] **Clear** then **Apply**: the keyboard goes dark
- [ ] Paint a handful of keys different colours, press **Apply**, then **Read**. The
      picture comes back showing the colours the keyboard is holding
- [ ] Drag across a row with the mouse held down: every key crossed takes the brush colour

### 5.3 The slot order - the one that decides US versus UK

This is the check the whole per-key feature rests on. The 128-slot order was recovered
from Gigabyte's software and has never been measured against hardware.

- [ ] **Clear**, paint only **A**, press **Apply**. The status line says
      `Painted A · slot 10`. Exactly one key lights, and it is **A**
- [ ] Repeat for **Enter**, **Space**, **Num-5**, **`[`** and **`]`**. Painting `[` must
      light `[`, not `]` - the two are 16 slots apart in the report and are the pair most
      likely to expose a wrong map
- [ ] If a key lights that is not the one you painted, note which key you painted and
      which one lit, then set `"LayoutOverride": "EngUs"` (or `"EngUk"`) in
      `%LocalAppData%\OpenAorus\settings.json`, restart, and try the same key again
- [ ] If neither order gets it right, the recovered map is wrong for this model. The two
      key names and the slot number from the status line are exactly what is needed to
      fix it

### 5.4 It comes back

- [ ] With a per-key painting applied, reboot. The same colours return
- [ ] Select a plain effect, sleep the laptop, wake it. The same effect returns

## 6. Sleep and resume

- [ ] Sleep the laptop, wake it, and confirm the status line reads
      `Re-applied <mode> after resume`

## Assumptions this checklist is really testing

Four things were reconstructed from Gigabyte's software and could not be checked
without the laptop. If any of them is wrong, the symptom shows up above.

- **The 128-slot key order.** Both the ENG-US and ENG-UK maps were recovered from
  Gigabyte's binaries and neither has been measured. The slots are an electrical scan
  matrix, so a wrong map looks entirely plausible until a key lights that is not the one
  you painted. Step 5.3 is the only thing that settles it. The picture of the keyboard is
  a separate table joined to the slots by key name, so a wrong map moves colours, never
  the picture.

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

# Hardware verification checklist

Everything in OpenAorus that touches the embedded controller or the keyboard was written
against protocols decompiled from Gigabyte Control Center and is covered by unit tests,
but **no fan, sensor, battery or lighting behaviour has been confirmed on real hardware
yet**. The build environment cannot elevate and has no keyboard of the supported family
attached.

Every step below needs administrator rights on the laptop itself, except section 5: the
keyboard is a plain HID device and its protocol needs none. The app relaunches itself
elevated at startup regardless, for the fan side, so the UAC prompt appears either way.

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

- [ ] The output starts with `OpenAorus 0.2.0 diagnostics` and a model line reading
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

### 3.1 The safety guards

Both settings in section 3 outlive the app: Fixed latches `SetFixedFanStatus` and a custom
curve is written into the controller's own table, so either one governs the fans after the
window is closed and after a reboot, with nothing left running to reconsider. Nothing in
the recovered protocol documents a controller-side minimum or an emergency override, so
the floors in `FanSafety` are the only ones there are, and this section is what confirms
they are actually reaching the hardware rather than only the UI.

Do not skip the load test at the end. Everything above it checks that the app refuses
something; only that one checks that what it *did* accept still cools the machine.

- [ ] The Fixed slider will not go below 20 %, by drag or by arrow key. The number beside
      it never reads less
- [ ] In the curve editor, drag the 80 °C point down to about 20 %. The line under the
      table says the curve needs at least 60 % at 80 °C, naming both numbers. Click Apply:
      the banner says the same thing and `--dump` shows the fan table unchanged
- [ ] Drag the last point down to 80 °C. The editor says the last point must be at 85 °C
      or above. This is the rule that is easiest to think is pedantic: above its last point
      the controller holds that point's duty indefinitely, so a table ending at 80 °C hands
      the whole danger zone back to whatever duty was last written
- [ ] Put the first points at 0 % while leaving the hot end alone. This must still apply -
      a silent idle is the point of a custom curve, and the guard is about the danger zone
      only
- [ ] Close the app. Edit `%LocalAppData%\OpenAorus\settings.json` by hand to
      `"FixedPercent": 0` and a flat curve (two points, both 0 %), then start the app.
      A banner says the saved fan settings could have left the fans too slow, the Fixed
      slider reads 20 %, and the curve editor shows the default curve
- [ ] With that same edited file, run `--apply` instead of opening the window and then
      `--dump`. The fan table must be the default curve, not the flat one: `--apply` never
      touches the UI, so this is the check that the guard lives in the domain and not in
      the window
- [ ] Load the machine until the CPU passes 90 °C in a mode whose duty is under 80 % -
      Quiet under a stress test is the usual way there. Within a second or two the fans
      go to full, the mode selection moves to Turbo, and the banner reads
      `Fans forced to full: CPU reached <n> °C`. It must say that **once**: watch for a
      further half minute and confirm the fans are not being re-driven every second, and
      that the banner does not clear itself while the machine is still hot
- [ ] **The one that matters.** Set a custom curve you would actually use, click Apply,
      quit the app from the tray, and then load the machine with nothing of OpenAorus
      running. The fans must ramp as the temperature climbs. If they sit at the low end
      instead, the controller is not running the table and every guard above is guarding
      something that was never in charge

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
      which one lit, then set `"LayoutOverride"` to `"EngUs"` (or `"EngUk"`) inside the
      `"Lighting"` section of
      `%LocalAppData%\OpenAorus\settings.json`, restart, and try the same key again
- [ ] If neither order gets it right, the recovered map is wrong for this model. The two
      key names and the slot number from the status line are exactly what is needed to
      fix it
- [ ] Record which product id this keyboard is, from Device Manager: the `Fusion RGB KB`
      device, Details, Hardware Ids. `7A3D` is treated as the ENG-UK order and `7A3C` as
      ENG-US because Gigabyte's own software says so. `7A3F` also gets ENG-US, but purely
      as a fallback: nothing is known about that model's slot order, and ENG-US was picked
      so lighting works at all. On a `7A3F` keyboard this step is not confirming a reading,
      it is the first evidence anyone has, so report the result whichever way it comes out

### 5.4 It comes back

- [ ] With a per-key painting applied, reboot. The same colours return
- [ ] Select a plain effect, sleep the laptop, wake it. The same effect returns

### 5.5 One effect's settings survive configuring another

All 18 configurable effects keep their settings in slices of a single 264-byte block, and
the slices are adjacent. An effect that writes one byte too many silently overwrites the
*first* byte of the next effect's stored settings, and nothing shows until you switch to
that neighbour. Two such overflows were found by arithmetic against the offset table:
Bloom and Merge were sending the nine-byte two-colour shape into an eight-byte slice.
Separately, selecting an effect now reads the whole block back first and patches only that
effect's slice, instead of sending a zeroed report that would reset every effect the owner
is not currently looking at.

Neither correction can be tested anywhere but here. The unit tests pin the bytes the app
emits; only the keyboard knows what it stored. **This section is the only hardware
evidence either of those two fixes will ever get.**

- [ ] Set **Bloom** to a distinctive pair of colours. Switch to **Spiral** and set its
      speed and direction. Switch back to Bloom: its colours, speed and random flag must be
      exactly as you left them. Then switch to Spiral once more: it must be as you left it
      too. Bloom coming back at defaults means the read-back is failing; Spiral's speed or
      direction changing on its own means Bloom is still writing a byte past its slice
- [ ] The same for **Merge** and **Crash**, in both directions. Merge's slice runs into
      Crash's, so a Merge overflow shows up as Crash's speed changing by itself
- [ ] Configure **Wave**, then **Cross**, then **Dragonstrike**, then return to Wave.
      Wave's speed, direction and colour must all be unchanged, and so must Cross's when
      you pass back through it
- [ ] If *every* other effect comes back at defaults after a single switch, this is the
      read-modify-write failing wholesale rather than one slice overflowing: the block read
      is being refused and the app is falling back to a zeroed buffer
- [ ] Anything else that comes back altered names its own culprit. Note which effect you
      configured and which one changed afterwards; that pair identifies the payload length
      to shorten in `EffectPacket`

### 5.6 The bytes nobody could identify

- [ ] **Radar.** The recovered protocol notes give the configuration shape of every effect
      except this one. It is sent as speed, random, direction, then colour, inferred from
      the six bytes its slice has room for and from the capability table saying it takes a
      direction. Check that Radar animates, that its colour and speed take effect and that
      its direction control reverses something. Then switch to **Star Shining**, which owns
      the slice immediately after Radar's, and confirm its settings survived. If Radar
      misbehaves while everything around it is fine, its payload shape is the thing to
      change, and nothing short of this will say so
- [ ] **Breathing and Ripple.** The second byte of their slice is an unidentified mode
      selector, not the random flag that every neighbouring shape carries in that position.
      The app therefore never writes it: on a read-modify-write it carries through whatever
      the firmware or Gigabyte's software last stored there. Confirm both effects animate
      normally and that their colour and speed take effect. One of them stuck in a
      behaviour the UI cannot change is that byte holding a value from before OpenAorus
      ever touched the keyboard

## 6. Sleep and resume

- [ ] Sleep the laptop, wake it, and confirm the status line reads
      `Re-applied <mode> after resume`

## Assumptions this checklist is really testing

Everything below was reconstructed from Gigabyte's software, or inferred from the shape
of the data, and could not be checked without the laptop. If any of it is wrong, the
symptom shows up above.

- **The 128-slot key order.** Both the ENG-US and ENG-UK maps were recovered from
  Gigabyte's binaries and neither has been measured. The slots are an electrical scan
  matrix, so a wrong map looks entirely plausible until a key lights that is not the one
  you painted. Step 5.3 is the only thing that settles it. The picture of the keyboard is
  a separate table joined to the slots by key name, so a wrong map moves colours, never
  the picture.

- **Which slot order a `7A3F` keyboard uses.** `7A3D` is mapped to ENG-UK and `7A3C` to
  ENG-US on the strength of a flag in Gigabyte's software. `7A3F` is supported but never
  appears in anything that was recovered, so it falls back to ENG-US on no evidence
  whatsoever. Step 5.3 on such a keyboard produces the first fact anyone has about it.

- **Where each effect's slice ends.** The offset table gives where each effect's
  configuration starts; the length of each is taken to be the gap to the next one, which
  is what makes the shape of a payload checkable at all. Bloom and Merge were writing one
  byte past that gap and were shortened on that reasoning alone. Section 5.5 is the only
  thing that can confirm the arithmetic matched the firmware.

- **Radar's payload shape.** Every other effect's configuration shape is named in the
  recovered notes. Radar's is not, so it is sent as speed, random, direction and colour,
  which is the shape that fits its six-byte slice given that it accepts a direction. If
  Radar is the one effect that misbehaves, this is why, and section 5.6 says what to
  record.

- **The second configuration byte of Breathing and Ripple.** The notes call it a mode
  selector and do not say what its values mean, so the app leaves it exactly as the
  keyboard had it rather than writing a zero over something the firmware may be using.
  That is the safer half of the bet, not a known-correct choice; section 5.6 is where an
  effect stuck in an unexplained mode would show up.

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
- **That the controller has no floor of its own.** The fan floors exist because nothing
  recovered from Gigabyte's software mentions a minimum duty or a thermal override below
  the values the app writes, so the app assumes there is none and refuses to write a
  setting that would stop the fans. If a duty of 0 turns out to be quietly ignored by the
  firmware, the guards are merely redundant, which is the harmless direction to be wrong
  in. Section 3.1's load test is what would show the opposite - a curve accepted by the
  app and then not run by the controller.
- **That 90 °C is the right place for the watchdog to intervene.** It is chosen to sit
  above any normal load and below anything that throttles hard, not measured. If the
  17G KD's own thermal management already has the fans up well before that, the watchdog
  will simply never fire, which section 3.1 asks you to confirm by making it fire on
  purpose.

## If something fails

Run `--dump` and attach the output to a note describing which step failed and what
happened instead. The failing step almost always points at one WMI method, and the
method names in the dump line up with the ones in
`docs/research/gb-wmiacpi-methods-aorus-17g-kd.txt`.

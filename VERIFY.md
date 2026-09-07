# Hardware verification checklist

Everything in OpenAorus that touches the embedded controller or the keyboard was written
against protocols decompiled from Gigabyte Control Center and is covered by unit tests,
but **no fan, sensor, battery or lighting behaviour has been confirmed on real hardware
yet**. The build environment cannot elevate and has no keyboard of the supported family
attached.

Every step below needs administrator rights on the laptop itself, except section 5 and
the raw-input half of section 7: the keyboard is a plain HID device and its protocol
needs none, and raw input needs none either. The app relaunches itself elevated at
startup regardless, for the fan side, so the UAC prompt appears either way.

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

- [ ] The output starts with `OpenAorus 0.3.0 diagnostics` and a model line reading
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
      `Fans forced to full: CPU reached <n> °C`. As long as nothing else touches the fans
      it must say that **once**: watch for a further half minute and confirm the fans are
      not being re-driven every second, and that the banner does not clear itself while
      the machine is still hot
- [ ] Keep the load on, and while the fans are still forced to full click **Quiet**. The
      fans drop, and then within a few seconds - as soon as the lagging duty read-back
      has come down with them - the watchdog forces them back to full and says so again.
      The mode you picked replaced the Turbo it had applied, which is the whole reason it
      was holding off. This stands in for the case that is awkward to stage by hand: on a
      resume the saved mode is re-applied the same way, and a machine that stayed hot
      across the sleep would otherwise come back on a slow mode with nothing watching it
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

## 7. Fn hotkeys and the overlay

Nothing in this section has ever been observed on hardware. Both hotkey channels were
recovered from Gigabyte's software rather than seen on this chassis, so it is entirely
possible that neither one opens and that v0.3 does nothing at all here. That is a
finding, not a failure. Record it.

Work through 7.1 before anything else, and do not skip it because pressing a key is
quicker. Three unrelated failures look identical from the keyboard: a chassis that emits
nothing on these collections, a report that arrives and is misread by one byte, and a
packet that arrives and cannot be walked at all. In all three you press an Fn key and
nothing happens. The app now writes down what arrived before anything decodes it, so one
line of the diagnostics dump separates the three, and every step after this one is
interpretable only once you know which of them you are looking at.

### 7.1 Read the dump first: which channels opened at all

The channels are opened by the running window, not by the command line. `--dump` and
`--apply` exit before a window exists, so a `--dump` run always reports that nothing has
arrived, whatever the keyboard did. Use the running app:

- [ ] Start OpenAorus with the window open and check Settings first: **Respond to the Fn
      row** must be ticked. It is on by default, and with it off no channel is opened at
      all, which reads in the dump exactly like a chassis that emits nothing
- [ ] Press, in this order: the fan-mode key three or four times, the backlight key
      through all its steps, the touchpad key, the Wi-Fi key, volume up and down, and
      display brightness up and down
- [ ] Click **Diagnostics** in the footer of the window. The status line names the file
      it wrote, in the same folder as `settings.json`
- [ ] Near the top of that file, under the model and OS lines, is a block beginning
      `Hotkey channels:` with five counts, followed by up to 32 recorded lines. Read it
      before reading anything else

What the counts mean, and they are the whole point of this step:

- [ ] **All five counts zero and the line `nothing has arrived`.** Nothing reached the
      app. With the Fn row confirmed on above, the finding is that this chassis emits
      nothing on the three vendor collections and raises no `GB_WMIACPI_Event`. Stop
      here and record it. Nothing else in this section is worth attempting, and the
      conclusion is that this model needs a different approach rather than a different
      constant
- [ ] **`reports=` non-zero, with lines like `#3 report 4 bytes: 04 01 19 00`, and yet
      no key did anything.** The raw-input channel is open and the decoding is wrong.
      This is the case the whole trace exists to expose, and 7.2 is where it is settled:
      compare the hex against the tables in `docs/research/fn-hotkey-signals.md`
- [ ] **`unreadable-packets=` non-zero.** A `WM_INPUT` message arrived and no report
      could be read out of it. The line names which of four places it was given up on,
      and they have unrelated fixes, so read the cause and not the length:
      `size query failed` (the OS would not say how big the packet is),
      `over cap` (larger than the 4096 bytes this window will allocate for),
      `copy short` (the OS agreed a size and then did not fill it, which is a P/Invoke
      shaped problem and has nothing to do with the report tables), and
      `walk rejected` (the packet arrived whole and the walk found nothing in it, which
      is what a wrong x64 header offset looks like). One exception worth knowing:
      `walk rejected` on a packet of exactly 36 bytes is evidence the header offset is
      *right*, because 36 is a 24-byte header plus the two length fields plus one 4-byte
      report, so look at the copy path instead
- [ ] **`WMI event: Data=202` and friends.** The WMI subscription is open and its `Data`
      property is named and typed what the research says. That is one of the two
      assumptions this release rests on, confirmed in one line
- [ ] **`WMI event without a readable Data: ...`.** The subscription is open, an event
      arrived, and the value was not where it was expected. The line lists the property
      names that did arrive. Copy them down verbatim: they are the entire answer to a
      subscription that runs forever and reports nothing, and nothing else in the app
      will ever produce them
- [ ] **`events=0` with no fault.** The subscription is running and the provider has
      raised nothing. Press the touchpad and Wi-Fi keys again before concluding it
- [ ] **`faults=` non-zero.** A channel failed. `fault in raw-input registration:
      Win32 error <n>` means the collections were refused, `fault in raw-input window
      creation` means the message-only window was never made, and `fault in WMI event
      subscription` means the provider refused the subscription, usually for want of
      elevation. If **both** channels failed the window says so once, in a banner. If only
      one failed, or a fault happened after a channel was already open, this line is the
      only place it is ever said

Two things about how the block is written, so it is not misread. Repeats of one shape
are recorded once and only counted after that: one line per fault site, one per packet
length and cause, one per set of event property names. So a count climbing with no new
line is the channel still misbehaving in the same way, not the trace losing entries.
Reports are the exception and every one is written, into a ring of 32; if the block says
`(n earlier entries dropped)` the keyboard is talking faster than the ring holds, which
is the channel working.

### 7.2 Press every Fn combination and write down which do nothing

- [ ] Go along the whole Fn row and note, for each key, whether OpenAorus reacted. Then
      export the dump again and read the recorded reports against what you pressed
- [ ] **Every key dead, with reports listed in the dump**, means one wrong assumption,
      and it is almost certainly the byte indexing: the decoder reads the research's
      `bRawData1` as `report[0]`, and the other reading shifts every pattern by one byte.
      Under that reading every documented pattern fails and every Fn key does nothing
- [ ] The dump prints reports in hex and the research tables are written in decimal, so
      compare them through this: `4` is `04`, `9` is `09`, `0` is `00`, `1` is `01`,
      `3` is `03`, `23` is `17`, `25` is `19`, `37` is `25`, `38` is `26`, `39` is `27`,
      `50` is `32`, `137` is `89`, `138` is `8A`, `139` is `8B`
- [ ] So, under the reading the app uses: a backlight press reads `04 01 00 ..`,
      `04 01 19 ..` or `04 01 32 ..`; a fan press is a 4-byte report whose **last** byte
      is `25`, `26` or `27`; Gigabyte's three launcher codes read `04 00 00 89`, `8A` and
      `8B`; and the 9-byte display-brightness report begins `09` and carries `01 03` in
      its third and fourth bytes. A dump full of reports that match none of these shapes,
      but would match them shifted along by one byte, is the answer to this whole section
- [ ] **Most keys working and two dead** is a research gap, not an indexing error.
      Record which two
- [ ] Note in particular whether the Fn key alone is visible, or only the combinations.
      The research could not say
- [ ] The app registers three vendor collections and never the standard keyboard page. If
      a key produces no report at all here while the rest of the row does, the signal may
      be one that only arrives on the keyboard page, and this step is the only thing that
      would show it

### 7.3 The bug this release exists to fix

- [ ] With **every** overlay switched on in Settings, press volume up, volume down and
      mute. Exactly one overlay must appear, Windows' own, and the volume must actually
      change
- [ ] Repeat with Gigabyte Control Center taken over
- [ ] Do the same with the display-brightness keys. Again: exactly one overlay, Windows'
      own
- [ ] **This is the acceptance test for the whole release.** Everything else in this
      section is a feature; a second card on either of those is the bug v0.3 exists to
      remove, reintroduced
- [ ] The two suppressions are not the same kind of thing, so a failure means different
      things. Volume is structural: its keys live on the consumer-control collection,
      which this app never registers, so a second volume card would mean something has
      started registering that page. Display brightness does arrive, on
      `0xFF00/0xFF00`, and is refused in `HotkeyPolicy`, so a second brightness card
      would mean that refusal has gone. Say which one you saw

### 7.4 One keypress, one action

The de-duplication window is 250 ms. It was chosen, not measured, and this step is the
measurement.

- [ ] Press the fan-mode key five times, waiting each time for the mode to change before
      pressing again: five mode changes, not ten and not two. Applying one automatic mode
      is five WMI writes and Turbo is seven, paced 500 ms apart, so a single change takes
      about two seconds and Turbo about three, and five presses walked round the ring
      take something on the order of ten to fifteen seconds to settle. What is being
      counted is the changes, not the speed. A slow result is the pacing, not a bug
- [ ] Now press it five times as fast as you can. **Fewer than five changes is expected
      here and is not the de-duplication.** A press that lands while an apply is still
      running is dropped rather than queued, deliberately: queued, a burst would leave
      the machine working through presses nobody is still making. What must not happen is
      *more* changes than presses
- [ ] Hold the fan-mode key down. There is no key-up on these collections, so the window
      throttles rather than latches: a hold fires at most once per window and each of
      those that lands mid-apply is dropped. The mode should walk slowly and stop when
      you let go, not carry on changing afterwards
- [ ] More changes than presses means the window is too short. A deliberate double tap,
      slow enough to be two presses, that produces one change means it is too long

### 7.5 Whether the firmware runs its own fan rotation underneath

- [ ] Note the mode OpenAorus shows, press the fan key once, and note it again. Then
      press it three more times and check the app has walked Quiet, Normal, Gaming, Turbo
      in order. From Fixed or Custom the first press lands on Normal by design
- [ ] Watch the machine rather than the app for one of these presses. If the fans audibly
      do something the app did not ask for, or the app's mode and the machine's behaviour
      diverge, the firmware is running its own rotation underneath and the app should
      honour the mode each wire code names instead of cycling
- [ ] The dump makes the same question answerable directly. If every press records the
      same code, the firmware is naming one fixed thing and cycling is right. If the code
      walks `25`, `26`, `27` from press to press, the firmware is rotating its own three
      modes and honouring the named mode would track it instead of drifting out of step
- [ ] `HotkeyPolicy.NamedMode` already holds that table, tested and unused, and switching
      to it is one line in `HotkeyPolicy.Service`
- [ ] This is the one observation in this section that could change a design decision
      rather than a constant

### 7.6 The keyboard backlight

- [ ] Cycle the backlight key through all its steps and check the lighting panel's
      brightness slider follows: 0 %, 50 %, 100 %. The app writes nothing back to the
      keyboard here; the firmware has already changed the lighting, and the app only
      moves the slider to agree and saves it
- [ ] A step the slider does not follow means this keyboard has more than the three
      levels the research documents. Record the report the dump shows for that step: its
      third byte is the level on the wire, and only `00`, `19` and `32` are understood.
      The app deliberately refuses to guess a scale rather than inventing one, so a
      fourth step is reported as not understood instead of as a plausible wrong number
- [ ] One odd consequence to expect if that happens, because it does not look like a
      backlight problem: a 4-byte report beginning `04 01` is treated as a backlight
      report whatever its last byte, so on a keyboard with an undocumented level the
      **fan** key can look like it works only sometimes

### 7.7 The overlay behaves like a notification, not a window

The three extended window styles that buy this are applied to the real window handle when
the card is first created. Nothing in the test suite can observe whether they work: the
tests pin only that the markup declarations behind them are still present, so everything
below is the first evidence there is.

- [ ] Press a serviced key while typing in another program: the overlay appears, the
      caret stays put, and nothing typed is lost
- [ ] Click where the overlay is drawn while it is up: the click reaches what is behind it
- [ ] Alt-Tab while a card is on screen: the card is not in the list
- [ ] Press a serviced key twice in quick succession with its overlay on: the words
      change inside one card. A second card must never stack on the first
- [ ] Press a serviced key while a full-screen game is running: the overlay appears over
      it or not at all, depending on how the game presents, and both are acceptable. What
      must not happen is the game losing focus or minimising. This case is untested by
      anything

### 7.8 Placement

- [ ] On a single monitor, the card centres at the bottom of the screen
- [ ] With a second monitor attached, it centres at the bottom of the screen the **mouse**
      is on. Not the focused window and not the primary monitor: a hotkey can fire with
      nothing focused at all, and the pointer is the only thing that always names a screen
- [ ] With the two monitors at **different** scaling factors, check it again on each. This
      is the one placement case that could not be reasoned out without hardware, and it is
      approximate by construction: the screen's bounds are taken from the monitor the
      pointer is on, but the scale used to convert them comes from the window's own
      source, which is whichever screen the card was last shown on. Expect the first
      appearance on the other monitor to be the one that can land off centre, and a second
      press on that same monitor to be right. Record whether that is what happens

### 7.9 It must never react to anything that is not an Fn key

- [ ] Type normally in another program for a minute with every overlay switched on. If
      OpenAorus reacts to anything at all, **stop and report it**
- [ ] It should be impossible: the app registers three vendor collections and never the
      standard keyboard, mouse or consumer-control pages, so ordinary typing is never
      delivered to it in the first place. A reaction would mean either a decoder matching
      far too loosely or a registration that is not the one in `RawInputWindow.Usages`,
      and it is the one failure in this section worth stopping the session for
- [ ] The same dump reads on this: a stream of recorded reports while you are only typing
      is the tell, whether or not anything visible happened

### 7.10 The questions the research could not answer

- [ ] Is the Fn key itself observable, or only the resulting combinations? (7.2)
- [ ] Does this chassis emit the fan codes 37, 38 and 39 at all? (7.1)
- [ ] What is the 9-byte **output** report on collection `0xFF00/0xFF00` for? Gigabyte
      writes to it and the decompiled path does not explain it. OpenAorus never writes to
      it. If something about the keyboard behaves differently under GCC than under
      OpenAorus, this is the first place to look

### Assumptions this section is really testing

- **That either hotkey channel exists on this chassis.** Both were recovered from
  Gigabyte's software and neither has been observed here. v0.3 could ship and do nothing
  at all without a single test noticing; 7.1 is the only thing that would say so.

- **That `bRawData1..4` means indices 0..3.** The 4-byte and 9-byte report tables only
  agree with each other under that reading, but it is a reading of decompiled field
  names. If it is wrong, every pattern shifts by one byte and every Fn key does nothing,
  which is exactly what 7.2 asks you to look for, and why the reports are written down by
  their bytes before anything decodes them.

- **That the x64 raw-input header is 24 bytes.** The walk that finds the reports inside a
  `WM_INPUT` packet starts there, and if it is wrong the two length fields read as
  rubbish, every packet is rejected and no Fn key ever does anything. It fails silently
  and it fails identically to the assumption above, which is why the dump names the place
  a packet was given up on rather than only its length (7.1).

- **That the fan-mode key should cycle.** The wire codes 37, 38 and 39 name Gigabyte's
  own firmware modes, and three distinct codes describe a state rather than a "next"
  edge. The app cycles anyway, because it has taken the mode machine over and there is no
  code for Turbo at all. Only hardware can say whether the firmware runs its own rotation
  underneath, in which case honouring the named mode is better and `HotkeyPolicy.NamedMode`
  is the table to switch to. Section 7.5.

- **That the backlight has exactly three levels**, 0, 25 and 50 on the wire, meaning 0 %,
  50 % and 100 %. Doubling the byte would invent a scale nobody measured, so an
  undocumented value is reported as not understood rather than guessed at. A four-step
  keyboard shows up in 7.6 as a level the slider does not follow, and as a fan key that
  seems to work only sometimes.

- **That 250 ms is the right de-duplication window.** Chosen to sit above the gap between
  two channels describing one press and below a deliberate double tap. Not measured. On
  the documented data the two channels' vocabularies do not even overlap, so the window is
  really insurance against key auto-repeat rather than against the cross-channel echo the
  design names. Section 7.4.

- **That the three extended window styles are enough** to make the overlay click-through
  and focus-proof: `WS_EX_TRANSPARENT`, `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW`. None of
  the three is exercisable without a desktop, and no test reaches past the markup that
  declares their WPF-level counterparts. Section 7.7, where full-screen games are the part
  nothing at all has looked at.

- **That the DPI transform is the right one.** Correct on a single-DPI setup by
  construction, and taken from the wrong monitor on a mixed-DPI one, because the scale
  comes from the screen the card was last shown on. Section 7.8.

- **That the WMI event's payload is a `Data` property that converts to an integer.** The
  research names it and nothing has checked it. A wrong name, or a value the provider
  hands over as text or as a real, is a subscription that runs and reports nothing,
  silently, which is why 7.1 asks for the property names that did arrive.

- **That cutting Gigabyte's five raw-input usages to three loses nothing.** The two
  dropped are the standard keyboard and mouse pages, which under `RIDEV_INPUTSINK` would
  deliver every keystroke typed anywhere on the machine into this elevated process for no
  purpose. Every documented signal arrives on a vendor collection. If 7.2 finds a key that
  does nothing and 7.9 finds nothing spurious, the missing signal may be one that only
  arrives on the keyboard page. That is the case for adding `0x0001/0x0006` back, and the
  only one.

### The four checks worth doing first

In this order. The first session does not need to reach the end of the section to be
worth running.

1. **7.1**, the dump. One line partitions every other unknown below it, and if it says
   nothing has arrived, the rest of the section has nothing to measure.
2. **7.3**, the volume and brightness keys with every overlay switched on. It is the
   acceptance test for the release and it takes a few seconds.
3. **7.2**, the walk along the Fn row with the dump open beside it. This is what settles
   the byte-indexing assumption the whole release rests on.
4. **7.5**, four presses of the fan key while watching which code the dump records. The
   one observation that could change a design decision rather than a constant.

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

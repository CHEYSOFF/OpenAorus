# OpenAorus

Open-source, single-executable replacement for the fan, sensor, battery and
keyboard-lighting features of Gigabyte Control Center on Gigabyte AORUS / AERO
laptops. Think [G-Helper](https://github.com/seerge/g-helper), but for Gigabyte.

**Status: pre-alpha, v0.3. Nothing here is verified on real hardware: not the fan
control from v0.1, not the lighting added in v0.2, and not the Fn hotkeys added in
v0.3. See [Verification status](#verification-status) below.**

## Why

Gigabyte Control Center is slow to open, breaks between versions, fights you
over fan modes ("AI" mode re-applies itself), and paints its own volume OSD on
top of the Windows one. Everything it does to the fans and battery goes through
one ACPI-WMI interface that any elevated program can call. So this does. The
keyboard lighting is a separate story: an ordinary USB HID device that answers
to any program at all, elevated or not.

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

## Keyboard lighting (v0.2)

- 18 whole-keyboard effects: Static, Breathing, Flow, Firework, Ripple, Rain,
  Cycling, Trigger, Pulse, Radar, Star Shining, Wave, Cross, Dragonstrike,
  Bloom, Spiral, Merge and Crash. Each shows only the controls it actually
  takes, so the colour, second colour, speed, direction and "random colours"
  boxes appear and disappear as you move through the list
- Brightness, applied without having to switch away from the current effect
- Per-key colours painted onto a keyboard-shaped editor, with fill, clear,
  drag-to-paint, and a Read button that pulls back the 128 colours the keyboard
  is currently holding
- Presets: three built in (Off, Warm White, Aorus Orange) plus as many of your
  own as you save
- The effect, its parameters, the per-key colours and your presets live in the
  same `settings.json` as the fan settings, and are re-applied at startup and
  after sleep/resume

Lighting talks to the keyboard as a plain HID device, so **it needs no
administrator rights and does not go through WMI at all**. That also means it
does not need Gigabyte Control Center installed; the fan side still does, for
the WMI schema (see [Install](#install)). The app as a whole still asks for
elevation when it launches, because the fan and battery side cannot work
without it. If GCC is running, its own lighting service can fight over the
keyboard, which is what the v0.1 takeover switch is for.

If no supported keyboard is found, the Lighting section is not shown and
nothing else about the app changes.

## Fn hotkeys and the overlay (v0.3)

The Fn row keeps working without Gigabyte's shortcut and OSD processes running,
and stops producing the second on-screen display that started this project:

- The fan-mode key cycles Quiet, Normal, Gaming, Turbo through the same
  controller the buttons use, so the window, the tray tooltip and the saved
  settings all stay in step with it
- The keyboard backlight key is handled by the keyboard's own firmware;
  OpenAorus reads the level it reports and moves the lighting panel's
  brightness slider to match, so the app agrees with the keyboard instead of
  arguing with it. Nothing is written back to the keyboard
- The touchpad and Wi-Fi keys are reported. The firmware has already done the
  toggle by the time the app hears about it, so there is nothing to do but say so
- The screen-brightness keys **are made to work by OpenAorus**, because on this
  chassis nothing else does. The firmware reports the key and then leaves the
  panel exactly where it was; Gigabyte's software was performing the change
  itself, and the embedded controller does not expose brightness at all. So
  OpenAorus sets it through Windows' own `WmiSetBrightness`, stepping through
  the levels the panel says it supports rather than adding a fixed percentage.
  A machine with no controllable panel — a desktop, or a lid closed onto an
  external monitor — does nothing, quietly
- Volume is never even received. Its keys live on the consumer-control
  collection OpenAorus deliberately does not register, so **there is no volume
  overlay to switch on and no setting for one**, and adding either would be a
  mistake rather than a feature. The same goes for the separate report that says
  the brightness has *already* changed: something else made that change and drew
  its own card, and a second card on top of the first is the whole reason this
  release exists

The overlay is **off by default for everything except the screen-brightness
keys**, and opt-in per signal: fan mode, keyboard backlight, touchpad, Wi-Fi and
screen brightness each have their own switch in Settings, plus how long the card
stays up. Brightness is the one exception because it is the one key with no
other feedback at all — the firmware draws nothing, and Windows draws nothing
either, because the change OpenAorus makes never travels through its hotkey
path. Untick it and the keys still work, silently. The whole Fn row can also be
switched off in one place, and that stops the app listening at all.

Two channels carry this, and they need different things. Raw input on three of
the keyboard's vendor collections **needs no administrator rights**; the
`GB_WMIACPI_Event` subscription that carries the touchpad and Wi-Fi notices does,
which the app already has for the fans. The three collections are all vendor
ones: OpenAorus never registers the standard keyboard, mouse or consumer-control
pages, so it never sees ordinary typing, and it could not draw over the Windows
volume overlay even if someone asked it to, because the volume keys are never
delivered to it in the first place.

The channel itself has now been watched on an AORUS 17G KD: reports arrive, and
the byte indexing the decoder assumed is confirmed. What has **not** held up is
the key codes. Everything recovered from Gigabyte's binaries — the launcher
codes, the fan-mode codes, the backlight levels — remains unconfirmed, and this
chassis has not been seen to send any of them. What it does send is the two
brightness keys above, plus two further codes nobody has identified yet, which
are written down and acted on by nothing. Because "nothing happened" is also what
a misread report looks like, the app writes down every report it receives, by its
bytes, before anything decodes it, along with anything it could not read and why.
That
record is in the diagnostics file the **Diagnostics** button in the window footer
writes. If the Fn row does nothing on your machine, that file is what says
whether anything arrived; section 7 of [`VERIFY.md`](VERIFY.md) walks through
reading it, and reports from any model are welcome.

Still missing: per-app profiles, power limits, and the chassis light bar and
logo LED.

## Verification status

v0.1 was built on a machine that cannot elevate and has no Gigabyte hardware
attached, and v0.2 and v0.3 were built on the same machine, with no keyboard of
the supported family present either. So **nothing here has been confirmed against
a real embedded controller or a real keyboard**: not the fan modes, not the
custom curve, not the battery charge limit, not autostart, not sleep/resume
behaviour, not one pixel of the lighting, and not a single Fn keypress.
Everything is implemented against protocols reverse-engineered from Gigabyte
Control Center and covered by unit tests against a fake WMI layer and a fake HID
device, which is not the same thing as a fan spinning up or a key lighting on a
real laptop.

The hotkeys are the least confirmed part of it. Neither channel has ever been
opened on a Gigabyte machine, so unlike the fan and lighting work, where the
question is whether the recovered protocol is right, here the prior question is
whether this chassis says anything on those channels at all. It could ship and
do nothing without a single test noticing, which is why section 7 of
[`VERIFY.md`](VERIFY.md) starts by reading the diagnostics file rather than by
pressing a key.

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
(`acpimof.dll`) that OpenAorus talks to for fans, sensors and the battery.
Removing that dependency is a future item. The lighting does not depend on it;
that side is plain HID and would work on a machine with GCC uninstalled, which
the fan side would not.

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

The lighting is keyed on the keyboard, not on the model list above, so the two
do not have to agree: a laptop whose fan profile is a best guess can still get
its lighting restored, and a laptop on the supported list with a different
keyboard gets no Lighting section at all.

### Which keyboards

OpenAorus looks for the Ione "Fusion RGB KB" family: USB vendor `0x1044` (some
units enumerate as `0x0414`) with product `0x7A3C`, `0x7A3D` or `0x7A3F`. It
picks the lighting endpoint out of that device's collections by their HID
capabilities rather than by parsing device paths. Other Gigabyte keyboards,
including the ITE-family ones, speak a different protocol and are not supported.

Per-key colours are addressed by 128 firmware slots, and the slot order differs
between the ENG-US and ENG-UK variants of the keyboard. Both maps were
transcribed out of Gigabyte's own software and **neither has been measured
against a real keyboard**. OpenAorus follows Gigabyte's software in treating
`0x7A3D` as the ENG-UK order; everything else, `0x7A3C` and `0x7A3F` alike, gets
ENG-US. For `0x7A3C` that is what Gigabyte's software says. For `0x7A3F` it is a
fallback with nothing behind it: that model's slot order is genuinely unknown,
and ENG-US was chosen so lighting works at all rather than because there is any
reason to think it is right.

The symptom of a wrong guess is that you paint one key and a different key
lights. If that happens, add a `LayoutOverride` to the `Lighting` section of
`%LocalAppData%\OpenAorus\settings.json`:

```json
{
  "Lighting": {
    "LayoutOverride": "EngUs"
  }
}
```

The rest of the file stays as it is; `"EngUk"` is the other value, and removing
the line goes back to the guess. Restart the app after editing. Section 5 of
[`VERIFY.md`](VERIFY.md) walks through the check that settles it. Reports from
any model are welcome, including the ones that turn out to be fine.

## Safety

OpenAorus only calls the same `GB_WMIACPI` methods Gigabyte Control Center
calls, and clamps every duty it writes to the model's maximum before sending
it. On the tested AORUS 17G KD that maximum is known to be correct; on an
untested model it is a guess, and the yellow "untested model" banner exists
because of that guess, not in spite of it - a wrong maximum could under- or
over-drive the fans (see [`VERIFY.md`](VERIFY.md)).

Fixed and Custom duty are written into the embedded controller and stay in
force there after OpenAorus exits - closing the app does not stop the fans in
either mode, which cuts both ways: nothing crashes if the tray icon dies, but
a low Fixed duty you set for a quiet moment keeps governing the fans under a
later heavy load with no running app to show it. Select **Normal** to hand
control back to the controller's own thermal management before closing the
app if that matters to you. Whether the controller genuinely keeps running a
Custom curve with the app closed is itself one of the unverified assumptions
in [`VERIFY.md`](VERIFY.md), not yet a confirmed guarantee.

Because those settings outlive the app, there are floors under them. Fixed
will not go below 20 %, and a custom curve has to reach 60 % by 80 °C and 90 %
by 90 °C with its last point at 85 °C or above, so that the table still governs
the temperatures that matter rather than leaving the controller holding a low
duty above the last point it was given. A curve that sits at 0 % while the
machine is cool is fine and stays fine - that is how a silent idle works, and
the controller ramps it as things heat up. The floors are checked wherever a
setting reaches the hardware, including `--apply`, and an old or hand-edited
`settings.json` that breaks them is repaired on load with a notice saying so.
While the app is running it also watches the CPU: at 90 °C with the fans under
80 % it takes them off you, in two stages. First it puts them on Gaming, the
controller's own aggressive automatic curve, which lifts the fans and then eases
them back down as the machine cools. If a later poll still reads 90 °C with the
duty still under 80 %, it escalates once more to Turbo and leaves the machine
there rather than quietly putting it back - Turbo is a fixed duty and stops
reading the temperature at all, which is why it is the last resort and not the
first answer. Each stage tells you which one it was.

## Docs

- [v0.1 design](docs/superpowers/specs/2026-09-06-openaorus-v0.1-design.md),
  [v0.2 keyboard RGB design](docs/superpowers/specs/2026-09-06-openaorus-v0.2-rgb-design.md)
  and [v0.3 Fn hotkeys and the OSD problem](docs/superpowers/specs/2026-09-06-openaorus-v0.3-fn-osd-design.md)
- [Research notes](docs/research/2026-09-05-research-notes.md) and the full
  [WMI method table](docs/research/gb-wmiacpi-methods-aorus-17g-kd.txt)
- [Fn hotkey and OSD research](docs/research/fn-hotkey-signals.md), with the two
  event channels and the three questions it could not answer
- [Keyboard protocol notes](docs/research/ione-keyboard-protocol.md), with the
  two recovered slot maps
  ([ENG-US](docs/research/ione-keymap-eng-us.txt),
  [ENG-UK](docs/research/ione-keymap-eng-uk.txt))
- [`VERIFY.md`](VERIFY.md) — the hardware verification checklist

## Credits

Protocol knowledge builds on
[tangalbert919/gigabyte-laptop-wmi](https://github.com/tangalbert919/gigabyte-laptop-wmi),
[s-h-a-d-o-w/alfc](https://github.com/s-h-a-d-o-w/alfc) and
[wtwrp/aeroctl](https://gitlab.com/wtwrp/aeroctl) for the fan, sensor and
battery WMI interface, and on
[rcassani/keyboard-fusion-rgb](https://github.com/rcassani/keyboard-fusion-rgb)
for the keyboard lighting: the protocol here was reconstructed from Gigabyte's
own binaries and then cross-checked field by field against that project, which
covers the same Ione keyboard family. It is GPL-3.0, as is this, and only
protocol facts were taken rather than code.

## License

GPL-3.0. Not affiliated with GIGABYTE.

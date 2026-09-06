# OpenAorus v0.3 Fn Hotkeys and OSD Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Fn row work without Gigabyte's software, and stop the duplicate on-screen
display. The owner's complaint was precise: Gigabyte draws its own volume overlay on top of the
one Windows already draws. Two input channels - raw input on the keyboard's vendor collections
and the `GB_WMIACPI_Event` stream - act on the hotkeys the app can service. An optional minimal
overlay, off by default, which must never draw for volume or display brightness.

**Architecture:** The whole hotkey protocol is pure functions over bytes. `RawInputDecoder`,
`WmiEventDecoder`, `SignalDebouncer`, `HotkeyPolicy` and `RawInputBuffer` have no IO and no WPF,
so the path from a four-byte HID report to an applied fan mode is unit-testable end to end.
`IHotkeySource` and `IWmiEventSource` are the two seams, and they carry **undecoded** bytes -
mirroring `IKeyboardHid`, which carries undecoded 264-byte reports while `EffectPacket` stays
pure. `HotkeyPolicy` is the small pure policy object in the shape of `FanSafety`/`FanWatchdog`:
it decides, `MainViewModel` acts. Only `RawInputWindow` and `WmiEventListener` touch the OS.

**Tech Stack:** .NET 8 (`net8.0-windows`, `win-x64`), C# 12, WPF + WinForms, `CommunityToolkit.Mvvm`,
P/Invoke to `user32.dll`, `System.Management`, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-06-openaorus-v0.3-fn-osd-design.md`
**Signal reference:** `docs/research/fn-hotkey-signals.md`

## Global Constraints

- Target framework `net8.0-windows`, `RuntimeIdentifier win-x64`; `Nullable` and `ImplicitUsings`
  enabled. **Implicit usings do not include `System.IO`** - add `using System.IO;` in any file
  that touches `File`, `Directory` or `Path`, and prefer `System.IO.Path` fully qualified where a
  local `Path` member exists (see `SettingsStore`).
- `src/OpenAorus.App` enables WPF **and** WinForms together. `Application`, `MessageBox`,
  `Clipboard`, `Cursors`, `Brushes`, `Brush`, `Color`, `Point`, `Size`, `Button`,
  `MouseEventArgs`, `Binding.DoNothing` and `UserControl` are all ambiguous there. Fully qualify
  (`System.Windows.Application`, `System.Windows.Media.Color`, ...) or alias, and keep `Point`
  and `Size` out of new public signatures entirely - pass doubles.
- Never give a XAML element an `x:Name` that matches a `UserControl` type name; the generated
  field collides with the type.
- A `DataTrigger` on an object-typed property with `Value="True"` never fires. Converters return
  `"Selected"` / `"Unselected"` (see `EnumEqualsConverter`).
- Every `{StaticResource}` key must resolve. `XamlResourceTests` parses the markup as plain XML
  and checks; the test project's `Views/*.xaml` glob picks up new views automatically, so
  `OverlayWindow.xaml` is covered the moment it exists.
- Only `src/OpenAorus.App/Hotkeys/RawInputWindow.cs` may call `user32.dll` raw input, and only
  `src/OpenAorus.App/Hotkeys/WmiEventListener.cs` may construct a `ManagementEventWatcher`.
  Everything else goes through `IHotkeySource` / `IWmiEventSource`.
- **Three raw-input usages, never five.** `0xFF01/0x2209`, `0xFF02/0x0001`, `0xFF00/0xFF00`.
  Not `0x0001/0x0006` (standard keyboard), not `0x0001/0x0002` (mouse), not `0x000C` (consumer
  control). See Task 7 for why this deviates from the binding spec.
- Raw input needs no elevation. The WMI subscription does; the app already has it.
- The build is warning-free at Debug **and** Release and must stay so. The suite stands at
  **501 tests**; every task adds to it and none may go red.
- Commit messages describe the change and nothing else: no attribution trailers, no tooling named.
  Follow the existing prefixes (`feat(hotkeys):`, `test(hotkeys):`, `fix(...)`, `docs:`).
- **Stage explicitly by path.** Never `git add -A` and never `git add .`: agent worktrees live
  under `.claude/` inside the repository and would be swept into the commit.
- Run tests with `dotnet test OpenAorus.sln`; build with `dotnet build OpenAorus.sln -c Release`.
- Steps marked `OWNER VERIFY` need the physical laptop. Never claim hotkey behaviour is confirmed
  without it; every one of them has a home in `VERIFY.md` section 8 (Task 10).

## File Structure

```
src/OpenAorus.Hardware/Hotkeys/HotkeySignal.cs        enum of the signals we understand + HotkeyEvent
src/OpenAorus.Hardware/Hotkeys/RawInputDecoder.cs     pure: HID report bytes -> HotkeyEvent?
src/OpenAorus.Hardware/Hotkeys/WmiEventDecoder.cs     pure: event Data value -> HotkeyEvent?
src/OpenAorus.Hardware/Hotkeys/SignalDebouncer.cs     pure: signal + level + timestamp -> fire or drop
src/OpenAorus.Hardware/Hotkeys/HotkeyPolicy.cs        pure: event + current mode + settings -> HotkeyAction
src/OpenAorus.Hardware/Hotkeys/IHotkeySource.cs       seams: undecoded report bytes / event Data
src/OpenAorus.Hardware/Hotkeys/RawInputBuffer.cs      pure: RAWINPUT buffer -> the reports inside it
src/OpenAorus.Hardware/Config/HotkeySettings.cs       persisted toggles + Repair()
src/OpenAorus.Hardware/Config/AppSettings.cs          (modified) Hotkeys property
src/OpenAorus.Hardware/Config/SettingsStore.cs        (modified) LastLoadHotkeysRepaired
src/OpenAorus.App/Hotkeys/RawInputWindow.cs           message-only HWND, registration, WM_INPUT
src/OpenAorus.App/Hotkeys/WmiEventListener.cs         ManagementEventWatcher on GB_WMIACPI_Event
src/OpenAorus.App/Hotkeys/HotkeyService.cs            wires both channels through the pure parts
src/OpenAorus.App/Views/OverlayPlacement.cs           pure: work area + card size -> left/top
src/OpenAorus.App/Views/OverlayWindow.xaml(.cs)       the minimal overlay
src/OpenAorus.App/ViewModels/HotkeysViewModel.cs      the Settings card
src/OpenAorus.App/ViewModels/MainViewModel.cs         (modified) acts on HotkeyAction
src/OpenAorus.App/ViewModels/LightingViewModel.cs     (modified) NoteFirmwareBacklight
src/OpenAorus.App/Views/SettingsWindow.xaml           (modified) hotkey card, outside the CanWrite gate
tests/OpenAorus.Hardware.Tests/FakeHotkeySource.cs
tests/OpenAorus.Hardware.Tests/RawInputDecoderTests.cs
tests/OpenAorus.Hardware.Tests/WmiEventDecoderTests.cs
tests/OpenAorus.Hardware.Tests/SignalDebouncerTests.cs
tests/OpenAorus.Hardware.Tests/HotkeyPolicyTests.cs
tests/OpenAorus.Hardware.Tests/RawInputBufferTests.cs
tests/OpenAorus.Hardware.Tests/HotkeySettingsTests.cs
tests/OpenAorus.Hardware.Tests/HotkeyServiceTests.cs
tests/OpenAorus.Hardware.Tests/RawInputWindowTests.cs
tests/OpenAorus.Hardware.Tests/OverlayPlacementTests.cs
tests/OpenAorus.Hardware.Tests/HotkeySettingsUiTests.cs
```

---

### Task 1: The signal vocabulary and the raw-input decoder

**Files:**
- Create: `src/OpenAorus.Hardware/Hotkeys/HotkeySignal.cs`, `src/OpenAorus.Hardware/Hotkeys/RawInputDecoder.cs`
- Test: `tests/OpenAorus.Hardware.Tests/RawInputDecoderTests.cs`
- Read: `docs/research/fn-hotkey-signals.md` (the two report tables)

**Interfaces:**
- Produces:
  - `enum HotkeySignal { FanModeStealth, FanModeAutoLow, FanModeAutoHigh, KeyboardBacklightLevel, DisplayBrightness, TouchpadEnabled, TouchpadDisabled, WifiEnabled, WifiDisabled, LaunchRecovery, LaunchUpdateAll, LaunchUpdateAllDefault, FirmwareVersionReply }`
  - `sealed record HotkeyEvent(HotkeySignal Signal, int Level = 0)`
  - `static class RawInputDecoder` with `static HotkeyEvent? Decode(byte[] report)`,
    `const int ShortReportLength = 4`, `const int LongReportLength = 9`,
    `static IReadOnlyList<int> BacklightWireLevels` = `{0, 25, 50}`
- Consumes: nothing.

**The byte indexing this whole release rests on.** The research names report bytes
`bRawData1..4`. This decoder reads that as **1-based field names over 0-based indices**: byte
`bRawData1` is `report[0]`. That is the only reading under which the 4-byte and 9-byte tables
agree with each other, but it is a reading of decompiled field names, not an observation. If it
is wrong every pattern shifts by one byte and every Fn key does nothing - which is exactly what
`VERIFY.md` step 8.2 is built to detect. Under the same reading, "byte 6" of the brightness
report is `report[5]` and "bytes 7 and 8" of the firmware reply are `report[6]` and `report[7]`.

**Precedence.** The fan rows are wildcards on the first three bytes (`_, _, _, 37`), so they must
be tested **last** among 4-byte patterns or they would swallow a launch code or a backlight report
whose fourth byte happened to land on 37-39. Exact patterns first, wildcards after.

**No extrapolation on backlight.** Only wire values 0, 25 and 50 are documented, meaning 0 %,
50 % and 100 %. Doubling the byte would invent a scale nobody measured, so an undocumented value
returns `null` - reported as not understood rather than guessed at. A four-step keyboard shows up
in `VERIFY.md` as a level the slider does not follow.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/RawInputDecoderTests.cs`:

```csharp
using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The 4-byte and 9-byte HID reports the keyboard's vendor collections deliver, decoded.
/// </summary>
/// <remarks>
/// Everything here is a reading of Gigabyte's decompiled field names, not an observation, and
/// the reading that matters most is that <c>bRawData1</c> is <c>report[0]</c>. The tests pin
/// the reading rather than proving it; hardware settles it.
/// </remarks>
public class RawInputDecoderTests
{
    [Theory]
    [InlineData(137, HotkeySignal.LaunchRecovery)]
    [InlineData(138, HotkeySignal.LaunchUpdateAll)]
    [InlineData(139, HotkeySignal.LaunchUpdateAllDefault)]
    public void The_launch_codes_decode_from_a_four_byte_report(int code, HotkeySignal expected)
    {
        var e = RawInputDecoder.Decode(new byte[] { 4, 0, 0, (byte)code });
        Assert.Equal(new HotkeyEvent(expected), e);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(25, 50)]
    [InlineData(50, 100)]
    public void The_backlight_report_carries_the_level_the_firmware_moved_to(int wire, int percent)
    {
        var e = RawInputDecoder.Decode(new byte[] { 4, 1, (byte)wire, 0 });
        Assert.Equal(new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, percent), e);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(75)]
    [InlineData(255)]
    public void A_backlight_level_nobody_documented_is_not_guessed_at(int wire)
    {
        // Doubling the byte would invent a scale that was never measured. A keyboard with four
        // steps has to show up as "not understood", not as a plausible wrong number.
        Assert.Null(RawInputDecoder.Decode(new byte[] { 4, 1, (byte)wire, 0 }));
    }

    [Theory]
    [InlineData(37, HotkeySignal.FanModeStealth)]
    [InlineData(38, HotkeySignal.FanModeAutoLow)]
    [InlineData(39, HotkeySignal.FanModeAutoHigh)]
    public void The_three_fan_codes_stay_distinct_signals(int code, HotkeySignal expected)
    {
        // Kept apart rather than collapsed into one "fan key", so the recovered information
        // survives: honouring the named mode instead of cycling is then a switch change.
        Assert.Equal(new HotkeyEvent(expected), RawInputDecoder.Decode(new byte[] { 4, 0, 0, (byte)code }));
        Assert.Equal(new HotkeyEvent(expected), RawInputDecoder.Decode(new byte[] { 9, 7, 3, (byte)code }));
    }

    [Fact]
    public void An_exact_pattern_wins_over_the_fan_wildcard()
    {
        // The fan rows match on the fourth byte alone. A backlight report whose fourth byte
        // happens to be 37 is still a backlight report.
        var e = RawInputDecoder.Decode(new byte[] { 4, 1, 25, 37 });
        Assert.Equal(new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, 50), e);
    }

    [Fact]
    public void The_brightness_report_carries_its_level_in_byte_six()
    {
        var report = new byte[] { 9, 0, 1, 3, 0, 42, 0, 0, 0 };
        Assert.Equal(new HotkeyEvent(HotkeySignal.DisplayBrightness, 42), RawInputDecoder.Decode(report));
    }

    [Fact]
    public void The_firmware_reply_is_recognised_so_it_is_never_mistaken_for_a_keypress()
    {
        var report = new byte[] { 9, 0, 0, 23, 0, 0, 0x12, 0x34, 0 };
        Assert.Equal(new HotkeyEvent(HotkeySignal.FirmwareVersionReply), RawInputDecoder.Decode(report));
    }

    [Theory]
    [InlineData(new byte[] { 4, 0, 0, 200 })]
    [InlineData(new byte[] { 4, 2, 0, 0 })]
    [InlineData(new byte[] { 9, 0, 2, 3, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 9, 0, 1, 4, 0, 0, 0, 0, 0 })]
    public void Anything_it_does_not_recognise_exactly_decodes_to_nothing(byte[] report)
    {
        Assert.Null(RawInputDecoder.Decode(report));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(264)]
    public void A_report_of_any_other_length_decodes_to_nothing(int length)
    {
        Assert.Null(RawInputDecoder.Decode(new byte[length]));
    }

    [Fact]
    public void A_null_report_is_a_programming_error_not_an_unknown_key()
    {
        Assert.Throws<ArgumentNullException>(() => RawInputDecoder.Decode(null!));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~RawInputDecoderTests`
Expected: compile error, `HotkeySignal` not found.

- [ ] **Step 3: Implement the vocabulary**

`src/OpenAorus.Hardware/Hotkeys/HotkeySignal.cs`:

```csharp
namespace OpenAorus.Hardware.Hotkeys;

/// <summary>Everything the two channels can say that this app understands.</summary>
/// <remarks>
/// Wider than the set the app acts on, on purpose. A signal that is decoded and then ignored -
/// display brightness, the three launcher codes, the firmware version reply - is one that cannot
/// be mistaken for something else by a looser pattern later. Volume is absent because it never
/// arrives: it lives on the consumer-control collection, which this app deliberately never
/// registers. See <c>RawInputWindow.Usages</c>.
/// </remarks>
public enum HotkeySignal
{
    /// <summary>Wire code 37. Gigabyte's firmware calls this mode "stealth".</summary>
    FanModeStealth,

    /// <summary>Wire code 38. Gigabyte's firmware calls this mode "auto low".</summary>
    FanModeAutoLow,

    /// <summary>Wire code 39. Gigabyte's firmware calls this mode "auto high".</summary>
    FanModeAutoHigh,

    /// <summary>The keyboard changed its own backlight; <see cref="HotkeyEvent.Level"/> is the
    /// new level in percent.</summary>
    KeyboardBacklightLevel,

    /// <summary>The panel brightness changed. Decoded, then deliberately ignored: Windows draws
    /// this overlay already, and drawing a second one is the bug v0.3 exists to fix.</summary>
    DisplayBrightness,

    TouchpadEnabled,
    TouchpadDisabled,
    WifiEnabled,
    WifiDisabled,

    /// <summary>Gigabyte's own launcher codes. Recognised so they cannot be misread; never acted on.</summary>
    LaunchRecovery,
    LaunchUpdateAll,
    LaunchUpdateAllDefault,

    /// <summary>A reply to a firmware-version request, not a keypress.</summary>
    FirmwareVersionReply,
}

/// <summary>One decoded signal, with the value that came with it where there was one.</summary>
/// <param name="Signal">What the keyboard or the firmware said.</param>
/// <param name="Level">Backlight level in percent, or the panel brightness byte. 0 otherwise.</param>
public sealed record HotkeyEvent(HotkeySignal Signal, int Level = 0);
```

- [ ] **Step 4: Implement the decoder**

`src/OpenAorus.Hardware/Hotkeys/RawInputDecoder.cs`:

```csharp
namespace OpenAorus.Hardware.Hotkeys;

/// <summary>
/// Turns one HID input report from the keyboard's vendor collections into a
/// <see cref="HotkeyEvent"/>, or into nothing.
/// </summary>
/// <remarks>
/// <para>
/// Pure and total: no IO, no state, and every input that is not an exact documented match returns
/// null rather than throwing or guessing. That matters more here than anywhere else in the app,
/// because <c>RIDEV_INPUTSINK</c> delivers these reports regardless of focus - a pattern that
/// matched loosely would act on input meant for another program.
/// </para>
/// <para>
/// Byte indexing: the research names bytes <c>bRawData1..4</c> and this reads them as 1-based
/// names over 0-based indices, so <c>bRawData1</c> is <c>report[0]</c>. It is the only reading
/// under which the 4-byte and 9-byte tables agree, and it is still a reading rather than an
/// observation - <c>VERIFY.md</c> section 8 is what settles it.
/// </para>
/// </remarks>
public static class RawInputDecoder
{
    /// <summary>The vendor collection on <c>mi_02&amp;col03</c> delivers reports this long.</summary>
    public const int ShortReportLength = 4;

    /// <summary>The vendor collection on <c>mi_02&amp;col07</c> delivers reports this long.</summary>
    public const int LongReportLength = 9;

    /// <summary>The three backlight levels the firmware is documented to report, on the wire.</summary>
    /// <remarks>0, 25 and 50 mean 0 %, 50 % and 100 %. Nothing else is a level this app claims to
    /// understand: the mapping is a lookup and not arithmetic precisely so a fourth step on some
    /// other chassis surfaces as unknown instead of as a plausible wrong number.</remarks>
    public static IReadOnlyList<int> BacklightWireLevels { get; } = new[] { 0, 25, 50 };

    /// <summary>Decodes one report.</summary>
    /// <param name="report">The report bytes, exactly as they came off the wire.</param>
    /// <returns>The signal, or null if this is not a report this app understands.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is null.</exception>
    public static HotkeyEvent? Decode(byte[] report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Length switch
        {
            ShortReportLength => DecodeShort(report),
            LongReportLength => DecodeLong(report),
            _ => null,
        };
    }

    private static HotkeyEvent? DecodeShort(byte[] r)
    {
        // Exact patterns first; the fan wildcard below matches on r[3] alone and would otherwise
        // swallow a launch code or a backlight report whose fourth byte landed on 37-39.
        if (r[0] == 4 && r[1] == 0 && r[2] == 0)
        {
            switch (r[3])
            {
                case 137: return new HotkeyEvent(HotkeySignal.LaunchRecovery);
                case 138: return new HotkeyEvent(HotkeySignal.LaunchUpdateAll);
                case 139: return new HotkeyEvent(HotkeySignal.LaunchUpdateAllDefault);
            }
        }

        if (r[0] == 4 && r[1] == 1) return Backlight(r[2]);

        return FanMode(r[3]);
    }

    private static HotkeyEvent? DecodeLong(byte[] r)
    {
        if (r[3] == 23) return new HotkeyEvent(HotkeySignal.FirmwareVersionReply);
        if (r[2] == 1 && r[3] == 3) return new HotkeyEvent(HotkeySignal.DisplayBrightness, r[5]);
        return FanMode(r[3]);
    }

    private static HotkeyEvent? Backlight(byte wire)
    {
        var index = BacklightWireLevels.ToList().IndexOf(wire);
        if (index < 0) return null;
        // 0 -> 0 %, 25 -> 50 %, 50 -> 100 %: the position in the documented table, not a scale.
        return new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, index * 50);
    }

    private static HotkeyEvent? FanMode(byte code) => code switch
    {
        37 => new HotkeyEvent(HotkeySignal.FanModeStealth),
        38 => new HotkeyEvent(HotkeySignal.FanModeAutoLow),
        39 => new HotkeyEvent(HotkeySignal.FanModeAutoHigh),
        _ => null,
    };
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass, including the 501 already there.

- [ ] **Step 6: Commit**

```bash
git add src/OpenAorus.Hardware/Hotkeys/HotkeySignal.cs \
        src/OpenAorus.Hardware/Hotkeys/RawInputDecoder.cs \
        tests/OpenAorus.Hardware.Tests/RawInputDecoderTests.cs
git commit -m "feat(hotkeys): decode the keyboard's vendor-collection reports"
```

---

### Task 2: The WMI event decoder

**Files:**
- Create: `src/OpenAorus.Hardware/Hotkeys/WmiEventDecoder.cs`
- Test: `tests/OpenAorus.Hardware.Tests/WmiEventDecoderTests.cs`
- Read: `src/OpenAorus.Hardware/Hotkeys/HotkeySignal.cs`, `docs/research/fn-hotkey-signals.md`

**Interfaces:**
- Produces:
  - `static class WmiEventDecoder` with `const string EventClass = "GB_WMIACPI_Event"`,
    `const string Query = "SELECT * FROM GB_WMIACPI_Event"`,
    `const string DataProperty = "Data"`,
    `static HotkeyEvent? Decode(int data)`
- Consumes: `HotkeySignal`, `HotkeyEvent` (Task 1).

The query string and the property name live here rather than in the listener so the one thing a
test can check about the subscription - that it names the class the research names - is checkable
without a WMI provider. **The property name is an assumption:** the research says the class
"delivers a `Data` value", and nothing has confirmed either the name or its CIM type on this
chassis. A wrong name means the listener runs and delivers nothing, silently. That is an
`OWNER VERIFY` item (Task 10, check 8.1).

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/WmiEventDecoderTests.cs`:

```csharp
using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

public class WmiEventDecoderTests
{
    [Theory]
    [InlineData(202, HotkeySignal.TouchpadDisabled)]
    [InlineData(458, HotkeySignal.TouchpadEnabled)]
    [InlineData(450, HotkeySignal.WifiEnabled)]
    [InlineData(194, HotkeySignal.WifiDisabled)]
    public void The_four_documented_data_values_decode(int data, HotkeySignal expected)
    {
        Assert.Equal(new HotkeyEvent(expected), WmiEventDecoder.Decode(data));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(203)]
    [InlineData(451)]
    [InlineData(int.MaxValue)]
    public void Anything_else_decodes_to_nothing(int data)
    {
        Assert.Null(WmiEventDecoder.Decode(data));
    }

    [Fact]
    public void The_subscription_names_the_class_the_research_names()
    {
        // The listener builds its watcher from these, so a typo here is a subscription that
        // never fires - and there is no way to see that without the laptop.
        Assert.Equal("GB_WMIACPI_Event", WmiEventDecoder.EventClass);
        Assert.Contains(WmiEventDecoder.EventClass, WmiEventDecoder.Query, StringComparison.Ordinal);
        Assert.StartsWith("SELECT * FROM ", WmiEventDecoder.Query, StringComparison.Ordinal);
        Assert.Equal("Data", WmiEventDecoder.DataProperty);
    }

    [Fact]
    public void Touchpad_and_wifi_are_the_only_things_this_channel_says()
    {
        // Brightness also arrives on this class, carrying a Brightness property rather than a
        // Data value. It is not decoded here: Windows already draws that overlay, and the whole
        // point of v0.3 is not to draw a second one.
        var decoded = Enumerable.Range(0, 1024)
            .Select(WmiEventDecoder.Decode)
            .Where(e => e is not null)
            .Select(e => e!.Signal)
            .ToList();

        Assert.Equal(4, decoded.Count);
        Assert.DoesNotContain(HotkeySignal.DisplayBrightness, decoded);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~WmiEventDecoderTests`
Expected: compile error, `WmiEventDecoder` not found.

- [ ] **Step 3: Implement**

`src/OpenAorus.Hardware/Hotkeys/WmiEventDecoder.cs`:

```csharp
namespace OpenAorus.Hardware.Hotkeys;

/// <summary>
/// Turns one <c>GB_WMIACPI_Event</c> <c>Data</c> value into a <see cref="HotkeyEvent"/>.
/// </summary>
/// <remarks>
/// <para>
/// The firmware has already performed these toggles by the time the event arrives - the touchpad
/// is off, the radio is on - so this channel is only ever telling the app what happened. Nothing
/// downstream writes anything back.
/// </para>
/// <para>
/// The same class also delivers a brightness event carrying a <c>Brightness</c> property rather
/// than a <c>Data</c> value. It is deliberately not decoded: Windows draws that overlay itself,
/// and a second one is the complaint this release exists to answer.
/// </para>
/// </remarks>
public static class WmiEventDecoder
{
    /// <summary>The WMI class the events arrive on, in <c>root\WMI</c>.</summary>
    public const string EventClass = "GB_WMIACPI_Event";

    /// <summary>The subscription <c>WmiEventListener</c> opens.</summary>
    public const string Query = "SELECT * FROM " + EventClass;

    /// <summary>The property carrying the value this decodes.</summary>
    /// <remarks>Unconfirmed on hardware: the research names it and nothing has checked it. A
    /// wrong name here is a subscription that runs and reports nothing - see VERIFY 8.1.</remarks>
    public const string DataProperty = "Data";

    /// <summary>Decodes one event's <c>Data</c> value.</summary>
    /// <param name="data">The value the event carried.</param>
    /// <returns>The signal, or null for a value this app does not understand.</returns>
    public static HotkeyEvent? Decode(int data) => data switch
    {
        202 => new HotkeyEvent(HotkeySignal.TouchpadDisabled),
        458 => new HotkeyEvent(HotkeySignal.TouchpadEnabled),
        450 => new HotkeyEvent(HotkeySignal.WifiEnabled),
        194 => new HotkeyEvent(HotkeySignal.WifiDisabled),
        _ => null,
    };
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/OpenAorus.Hardware/Hotkeys/WmiEventDecoder.cs \
        tests/OpenAorus.Hardware.Tests/WmiEventDecoderTests.cs
git commit -m "feat(hotkeys): decode the GB_WMIACPI_Event touchpad and Wi-Fi values"
```

---

### Task 3: The debouncer

**Files:**
- Create: `src/OpenAorus.Hardware/Hotkeys/SignalDebouncer.cs`
- Test: `tests/OpenAorus.Hardware.Tests/SignalDebouncerTests.cs`
- Read: `src/OpenAorus.Hardware/Hotkeys/HotkeySignal.cs`

**Interfaces:**
- Produces:
  - `sealed class SignalDebouncer` with `const int WindowMs = 250`,
    `SignalDebouncer(int windowMs = WindowMs)`,
    `bool ShouldFire(HotkeyEvent signal, long nowMs)`
- Consumes: `HotkeyEvent` (Task 1).

**Keyed on signal AND level.** A level-blind key would drop the second of two quick backlight
taps - 0 % then 50 % inside 250 ms is two real events, not one echoed twice.

**What the window really guards.** The design spec justifies it as cross-channel echo, and on the
documented data the two channels' vocabularies do not overlap at all, so that duplication has
never been seen. The likelier duplicate is **key auto-repeat**: a held fan-mode key repeating at
the keyboard's repeat rate would otherwise queue one seven-step fan write per repeat. 250 ms is
chosen to sit above the gap between two channels describing one press and below a deliberate
double tap. It is not measured, which is why `VERIFY.md` 8.4 asks for five rapid presses.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/SignalDebouncerTests.cs`:

```csharp
using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>One keypress, one action, one overlay at most.</summary>
public class SignalDebouncerTests
{
    [Fact]
    public void The_same_signal_twice_inside_the_window_fires_once()
    {
        var d = new SignalDebouncer();
        var e = new HotkeyEvent(HotkeySignal.FanModeStealth);

        Assert.True(d.ShouldFire(e, 1000));
        Assert.False(d.ShouldFire(e, 1000 + SignalDebouncer.WindowMs - 1));
    }

    [Fact]
    public void The_same_signal_outside_the_window_fires_twice()
    {
        var d = new SignalDebouncer();
        var e = new HotkeyEvent(HotkeySignal.FanModeStealth);

        Assert.True(d.ShouldFire(e, 1000));
        Assert.True(d.ShouldFire(e, 1000 + SignalDebouncer.WindowMs));
    }

    [Fact]
    public void Different_signals_never_suppress_each_other()
    {
        var d = new SignalDebouncer();

        Assert.True(d.ShouldFire(new HotkeyEvent(HotkeySignal.FanModeStealth), 1000));
        Assert.True(d.ShouldFire(new HotkeyEvent(HotkeySignal.WifiEnabled), 1000));
        Assert.True(d.ShouldFire(new HotkeyEvent(HotkeySignal.TouchpadDisabled), 1000));
    }

    [Fact]
    public void Two_quick_backlight_taps_are_two_events_because_the_level_is_part_of_the_key()
    {
        // The reason the key is not the signal alone: 0 % then 50 % inside the window is the
        // owner tapping the key twice, and dropping the second would leave the slider behind.
        var d = new SignalDebouncer();

        Assert.True(d.ShouldFire(new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, 0), 1000));
        Assert.True(d.ShouldFire(new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, 50), 1050));
        Assert.False(d.ShouldFire(new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, 50), 1100));
    }

    [Fact]
    public void A_repeat_of_the_same_level_much_later_fires_again()
    {
        var d = new SignalDebouncer();
        var e = new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, 50);

        Assert.True(d.ShouldFire(e, 1000));
        Assert.False(d.ShouldFire(e, 1100));
        Assert.True(d.ShouldFire(e, 5000));
    }

    [Fact]
    public void A_held_key_repeating_is_suppressed_for_as_long_as_it_repeats()
    {
        // Auto-repeat is the duplication most likely to be real. One seven-step fan write per
        // repeat would be seconds of controller traffic for one finger.
        var d = new SignalDebouncer();
        var e = new HotkeyEvent(HotkeySignal.FanModeAutoHigh);

        Assert.True(d.ShouldFire(e, 0));
        for (var t = 33; t < SignalDebouncer.WindowMs; t += 33)
            Assert.False(d.ShouldFire(e, t));
    }

    [Fact]
    public void A_shorter_window_can_be_asked_for()
    {
        var d = new SignalDebouncer(windowMs: 10);
        var e = new HotkeyEvent(HotkeySignal.WifiDisabled);

        Assert.True(d.ShouldFire(e, 0));
        Assert.False(d.ShouldFire(e, 5));
        Assert.True(d.ShouldFire(e, 10));
    }

    [Fact]
    public void A_timestamp_that_goes_backwards_does_not_wedge_it_shut()
    {
        // Nothing should hand it one, but a clock that jumped must not leave a signal suppressed
        // for as long as the process runs.
        var d = new SignalDebouncer();
        var e = new HotkeyEvent(HotkeySignal.FanModeStealth);

        Assert.True(d.ShouldFire(e, 10_000));
        Assert.True(d.ShouldFire(e, 5_000));
    }

    [Fact]
    public void A_null_signal_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => new SignalDebouncer().ShouldFire(null!, 0));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~SignalDebouncerTests`
Expected: compile error, `SignalDebouncer` not found.

- [ ] **Step 3: Implement**

`src/OpenAorus.Hardware/Hotkeys/SignalDebouncer.cs`:

```csharp
namespace OpenAorus.Hardware.Hotkeys;

/// <summary>
/// Decides, one signal at a time, whether it is a fresh keypress or an echo of the last one.
/// </summary>
/// <remarks>
/// <para>
/// The spec's reason is that the two channels can describe one physical press. On the documented
/// data their vocabularies do not overlap at all, so that duplication has never been observed and
/// this is insurance against it. The duplication that is likely to be real is key auto-repeat: a
/// held fan-mode key would otherwise queue one seven-step controller write per repeat.
/// </para>
/// <para>
/// The key is the signal <em>and</em> its level. A level-blind key would treat two quick backlight
/// taps as one press and leave the slider showing the level the keyboard had moved past.
/// </para>
/// <para>
/// The table is bounded by the signals that carry a level: three backlight steps and at most 256
/// brightness bytes. Nothing grows without limit, so there is nothing to evict.
/// </para>
/// </remarks>
public sealed class SignalDebouncer
{
    /// <summary>How long one signal-and-level suppresses a repeat of itself, in milliseconds.</summary>
    /// <remarks>Chosen to sit above the gap between two channels describing one press and below a
    /// deliberate double tap. Not measured - VERIFY 8.4 is what measures it.</remarks>
    public const int WindowMs = 250;

    private readonly Dictionary<(HotkeySignal Signal, int Level), long> _lastFired = new();
    private readonly int _windowMs;

    /// <param name="windowMs">The suppression window. Defaults to <see cref="WindowMs"/>.</param>
    public SignalDebouncer(int windowMs = WindowMs) => _windowMs = windowMs;

    /// <summary>Feeds one decoded signal in and answers whether it should be acted on.</summary>
    /// <param name="signal">The decoded signal.</param>
    /// <param name="nowMs">A monotonic millisecond timestamp; the app passes
    /// <see cref="Environment.TickCount64"/>.</param>
    /// <returns>True for a fresh press, false for an echo or a repeat.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="signal"/> is null.</exception>
    public bool ShouldFire(HotkeyEvent signal, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var key = (signal.Signal, signal.Level);
        if (_lastFired.TryGetValue(key, out var last) && nowMs >= last && nowMs - last < _windowMs)
            return false;

        _lastFired[key] = nowMs;
        return true;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/OpenAorus.Hardware/Hotkeys/SignalDebouncer.cs \
        tests/OpenAorus.Hardware.Tests/SignalDebouncerTests.cs
git commit -m "feat(hotkeys): collapse a repeated signal into one action"
```

---

### Task 4: HotkeyPolicy - the pure decision

**Files:**
- Create: `src/OpenAorus.Hardware/Hotkeys/HotkeyPolicy.cs`
- Test: `tests/OpenAorus.Hardware.Tests/HotkeyPolicyTests.cs`
- Read: `src/OpenAorus.App/ViewModels/FanWatchdog.cs` (the shape to copy),
  `src/OpenAorus.Hardware/Fans/FanSafety.cs`, `src/OpenAorus.Hardware/Fans/FanMode.cs`

**Interfaces:**
- Produces:
  - `enum HotkeyOutcome { Ignore, CycleFanMode, SetBacklightLevel, Notify }`
  - `sealed record HotkeyAction(HotkeyOutcome Outcome, FanMode? Mode = null, int Level = 0, string Text = "", bool ShowOverlay = false)` with `static readonly HotkeyAction None`
  - `static class HotkeyPolicy` with
    `static HotkeyAction Decide(HotkeyEvent signal, FanMode current, HotkeySettings settings)`,
    `static FanMode NextMode(FanMode current)`,
    `static FanMode? NamedMode(HotkeySignal signal)`
- Consumes: `HotkeyEvent`, `HotkeySignal` (Task 1), `FanMode`, `HotkeySettings` (Task 5).

> **Order note.** This task names `HotkeySettings`, which Task 5 creates. Implement Task 5 first
> if you are working strictly in order, or create the settings class here and let Task 5 add its
> `Repair()` and its wiring. Either way the type is
> `OpenAorus.Hardware.Config.HotkeySettings` with `Enabled`, `OverlayForFanMode`,
> `OverlayForBacklight`, `OverlayForTouchpad`, `OverlayForWifi` and `OverlaySeconds`.

This is the `FanWatchdog` of v0.3: a small pure object that decides, with the acting left to
`MainViewModel`. It has no clock, no IO and no WPF, so every rule below is exercised directly.

**The fan key cycles, and the alternative is kept next to it.** Wire codes 37, 38 and 39 name
Gigabyte's own firmware modes (stealth, auto low, auto high). The binding spec asks for a cycle
Quiet → Normal → Gaming → Turbo, and that is what `Decide` does: the app has taken the mode
machine over, there is no code for Turbo, and Fixed and Custom are not on the cycle at all.
`NamedMode` is the other reading - honour the mode the code names - implemented, tested and
unused, so reversing the decision is one line in `Decide`. This is the design decision most
likely to be wrong, and only hardware can say: if the firmware runs its own rotation underneath,
a cycling app diverges from it on the first press. `VERIFY.md` 8.5 is the observation that decides.

**Fixed and Custom land on Normal, not Quiet.** The spec is silent and the earlier planning pass
chose Quiet. This plan chooses Normal deliberately: an owner sitting on Fixed at 80 % under load
who taps the fan key should not be dropped to the quietest mode the machine has. Landing on
Normal is the same one-line change and cannot make a hot machine hotter. If you prefer Quiet, the
test names the choice and changing both together is honest.

**Volume and brightness never draw.** Volume never reaches this code at all - the consumer-control
collection is never registered, so the guarantee is structural. Display brightness is different:
it arrives on `0xFF00/0xFF00`, which *is* registered, so suppressing it is a policy decision and
needs a test of its own. The `default:` arm covers it, and a test walks every enum member with
every overlay toggle on to prove nothing outside the serviced set can ever ask for an overlay.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/HotkeyPolicyTests.cs`:

```csharp
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// What one decoded signal means, given what the fans are doing and what the owner turned on.
/// </summary>
/// <remarks>
/// The same shape as <see cref="OpenAorus.App.ViewModels.FanWatchdog"/>: a pure object that
/// decides, with the acting left to the view model. Everything the app does in response to an
/// Fn key is decided here, so everything is checkable here.
/// </remarks>
public class HotkeyPolicyTests
{
    private static HotkeySettings AllOverlaysOn() => new()
    {
        Enabled = true,
        OverlayForFanMode = true,
        OverlayForBacklight = true,
        OverlayForTouchpad = true,
        OverlayForWifi = true,
    };

    [Theory]
    [InlineData(FanMode.Quiet, FanMode.Normal)]
    [InlineData(FanMode.Normal, FanMode.Gaming)]
    [InlineData(FanMode.Gaming, FanMode.Turbo)]
    [InlineData(FanMode.Turbo, FanMode.Quiet)]
    public void The_fan_key_walks_the_four_automatic_modes_in_a_ring(FanMode current, FanMode expected)
    {
        Assert.Equal(expected, HotkeyPolicy.NextMode(current));
    }

    [Theory]
    [InlineData(FanMode.Fixed)]
    [InlineData(FanMode.Custom)]
    public void Off_the_ring_the_key_lands_on_Normal(FanMode current)
    {
        // Not Quiet. An owner on Fixed at 80 % under load who taps the key must not be dropped
        // to the quietest mode the machine has; Normal is the same one-line choice and is safe.
        Assert.Equal(FanMode.Normal, HotkeyPolicy.NextMode(current));
    }

    [Fact]
    public void Every_fan_mode_has_a_next_one()
    {
        foreach (var mode in Enum.GetValues<FanMode>())
            Assert.Contains(HotkeyPolicy.NextMode(mode), Enum.GetValues<FanMode>());
    }

    [Theory]
    [InlineData(HotkeySignal.FanModeStealth)]
    [InlineData(HotkeySignal.FanModeAutoLow)]
    [InlineData(HotkeySignal.FanModeAutoHigh)]
    public void All_three_fan_codes_cycle_rather_than_selecting_the_mode_they_name(HotkeySignal signal)
    {
        var a = HotkeyPolicy.Decide(new HotkeyEvent(signal), FanMode.Normal, AllOverlaysOn());

        Assert.Equal(HotkeyOutcome.CycleFanMode, a.Outcome);
        Assert.Equal(FanMode.Gaming, a.Mode);
        Assert.True(a.ShowOverlay);
        Assert.Contains("Gaming", a.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HotkeySignal.FanModeStealth, FanMode.Quiet)]
    [InlineData(HotkeySignal.FanModeAutoLow, FanMode.Normal)]
    [InlineData(HotkeySignal.FanModeAutoHigh, FanMode.Gaming)]
    public void The_mode_each_code_names_is_kept_even_though_nothing_uses_it(HotkeySignal signal, FanMode named)
    {
        // The recovered information, preserved. If hardware shows the firmware runs its own
        // rotation underneath, Decide switches to this table and nothing else moves.
        Assert.Equal(named, HotkeyPolicy.NamedMode(signal));
    }

    [Fact]
    public void Only_the_fan_codes_name_a_mode()
    {
        foreach (var signal in Enum.GetValues<HotkeySignal>())
        {
            var isFanCode = signal is HotkeySignal.FanModeStealth
                or HotkeySignal.FanModeAutoLow or HotkeySignal.FanModeAutoHigh;
            Assert.Equal(isFanCode, HotkeyPolicy.NamedMode(signal) is not null);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void The_backlight_signal_carries_its_level_through(int level)
    {
        var a = HotkeyPolicy.Decide(
            new HotkeyEvent(HotkeySignal.KeyboardBacklightLevel, level), FanMode.Normal, AllOverlaysOn());

        Assert.Equal(HotkeyOutcome.SetBacklightLevel, a.Outcome);
        Assert.Equal(level, a.Level);
        Assert.Null(a.Mode);
    }

    [Theory]
    [InlineData(HotkeySignal.TouchpadEnabled)]
    [InlineData(HotkeySignal.TouchpadDisabled)]
    [InlineData(HotkeySignal.WifiEnabled)]
    [InlineData(HotkeySignal.WifiDisabled)]
    public void The_touchpad_and_radio_signals_are_display_only(HotkeySignal signal)
    {
        var a = HotkeyPolicy.Decide(new HotkeyEvent(signal), FanMode.Normal, AllOverlaysOn());

        // The firmware already did the toggle. There is nothing to write back.
        Assert.Equal(HotkeyOutcome.Notify, a.Outcome);
        Assert.NotEqual("", a.Text);
    }

    [Theory]
    [InlineData(HotkeySignal.DisplayBrightness)]
    [InlineData(HotkeySignal.LaunchRecovery)]
    [InlineData(HotkeySignal.LaunchUpdateAll)]
    [InlineData(HotkeySignal.LaunchUpdateAllDefault)]
    [InlineData(HotkeySignal.FirmwareVersionReply)]
    public void The_signals_the_app_cannot_service_do_nothing_at_all(HotkeySignal signal)
    {
        var a = HotkeyPolicy.Decide(new HotkeyEvent(signal, 42), FanMode.Normal, AllOverlaysOn());

        Assert.Equal(HotkeyOutcome.Ignore, a.Outcome);
        Assert.False(a.ShowOverlay);
    }

    [Fact]
    public void Display_brightness_never_draws_an_overlay_whatever_is_switched_on()
    {
        // The one signal where "we never draw over Windows' own overlay" is a decision rather
        // than a structural fact: volume never reaches this app, but brightness does, on the
        // 0xFF00/0xFF00 collection. This is the test standing in for the missing registration.
        var a = HotkeyPolicy.Decide(
            new HotkeyEvent(HotkeySignal.DisplayBrightness, 80), FanMode.Turbo, AllOverlaysOn());

        Assert.False(a.ShowOverlay);
        Assert.Equal(HotkeyAction.None, a);
    }

    [Fact]
    public void Nothing_outside_the_serviced_set_can_ever_ask_for_an_overlay()
    {
        var serviced = new[]
        {
            HotkeySignal.FanModeStealth, HotkeySignal.FanModeAutoLow, HotkeySignal.FanModeAutoHigh,
            HotkeySignal.KeyboardBacklightLevel,
            HotkeySignal.TouchpadEnabled, HotkeySignal.TouchpadDisabled,
            HotkeySignal.WifiEnabled, HotkeySignal.WifiDisabled,
        };

        foreach (var signal in Enum.GetValues<HotkeySignal>())
        {
            var a = HotkeyPolicy.Decide(new HotkeyEvent(signal), FanMode.Normal, AllOverlaysOn());
            if (serviced.Contains(signal)) continue;
            Assert.False(a.ShowOverlay, $"{signal} asked for an overlay");
            Assert.Equal(HotkeyOutcome.Ignore, a.Outcome);
        }
    }

    [Fact]
    public void The_overlay_is_off_unless_the_owner_switched_that_signal_on()
    {
        var settings = new HotkeySettings { Enabled = true, OverlayForFanMode = true };

        var fan = HotkeyPolicy.Decide(new HotkeyEvent(HotkeySignal.FanModeStealth), FanMode.Quiet, settings);
        var wifi = HotkeyPolicy.Decide(new HotkeyEvent(HotkeySignal.WifiEnabled), FanMode.Quiet, settings);

        Assert.True(fan.ShowOverlay);
        Assert.False(wifi.ShowOverlay);
        // Still an action - the overlay toggle governs the drawing, never the doing.
        Assert.Equal(HotkeyOutcome.Notify, wifi.Outcome);
    }

    [Fact]
    public void A_defaulted_settings_object_draws_nothing_at_all()
    {
        var settings = new HotkeySettings();

        foreach (var signal in Enum.GetValues<HotkeySignal>())
            Assert.False(HotkeyPolicy.Decide(new HotkeyEvent(signal), FanMode.Normal, settings).ShowOverlay);
    }

    [Fact]
    public void Switching_hotkeys_off_stops_every_signal_dead()
    {
        var settings = AllOverlaysOn();
        settings.Enabled = false;

        foreach (var signal in Enum.GetValues<HotkeySignal>())
            Assert.Equal(HotkeyAction.None, HotkeyPolicy.Decide(new HotkeyEvent(signal), FanMode.Normal, settings));
    }

    [Fact]
    public void Nulls_are_programming_errors()
    {
        Assert.Throws<ArgumentNullException>(() => HotkeyPolicy.Decide(null!, FanMode.Normal, new HotkeySettings()));
        Assert.Throws<ArgumentNullException>(
            () => HotkeyPolicy.Decide(new HotkeyEvent(HotkeySignal.WifiEnabled), FanMode.Normal, null!));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~HotkeyPolicyTests`
Expected: compile error, `HotkeyPolicy` not found.

- [ ] **Step 3: Implement**

`src/OpenAorus.Hardware/Hotkeys/HotkeyPolicy.cs`:

```csharp
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;

namespace OpenAorus.Hardware.Hotkeys;

/// <summary>What the app should do about one signal.</summary>
public enum HotkeyOutcome
{
    /// <summary>Nothing. Either the app cannot service the key, or Windows already handles it.</summary>
    Ignore,

    /// <summary>Apply <see cref="HotkeyAction.Mode"/> through the existing fan controller.</summary>
    CycleFanMode,

    /// <summary>Move the lighting panel's brightness to <see cref="HotkeyAction.Level"/>. The
    /// firmware has already changed it; nothing is written back to the keyboard.</summary>
    SetBacklightLevel,

    /// <summary>Say so and no more: the firmware already performed the toggle.</summary>
    Notify,
}

/// <summary>One decision, in the terms the view model acts in.</summary>
/// <param name="Outcome">What to do.</param>
/// <param name="Mode">The fan mode to apply, for <see cref="HotkeyOutcome.CycleFanMode"/>.</param>
/// <param name="Level">The backlight level in percent, for <see cref="HotkeyOutcome.SetBacklightLevel"/>.</param>
/// <param name="Text">What the overlay would say.</param>
/// <param name="ShowOverlay">Whether the owner asked to see this one.</param>
public sealed record HotkeyAction(
    HotkeyOutcome Outcome,
    FanMode? Mode = null,
    int Level = 0,
    string Text = "",
    bool ShowOverlay = false)
{
    /// <summary>Do nothing, say nothing, draw nothing.</summary>
    public static readonly HotkeyAction None = new(HotkeyOutcome.Ignore);
}

/// <summary>
/// Turns one decoded signal into one decision, given what the fans are doing and what the owner
/// switched on.
/// </summary>
/// <remarks>
/// <para>
/// The same division of labour as <see cref="FanSafety"/> and the fan watchdog: everything the
/// app does in response to an Fn key is decided in this one pure place, and
/// <c>MainViewModel</c> does the acting. Nothing here has a clock, touches the OS or knows what
/// an overlay looks like.
/// </para>
/// <para>
/// The <c>default</c> arm is deliberately the ignore arm. A signal added to
/// <see cref="HotkeySignal"/> later is inert until someone writes a case for it, which is the
/// safe direction for a decoder whose reports arrive regardless of focus.
/// </para>
/// </remarks>
public static class HotkeyPolicy
{
    /// <summary>Decides what one signal means right now.</summary>
    /// <param name="signal">The decoded signal.</param>
    /// <param name="current">The fan mode the app believes is in force.</param>
    /// <param name="settings">The owner's hotkey and overlay toggles.</param>
    /// <returns>The action to take; <see cref="HotkeyAction.None"/> for anything not serviced.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="signal"/> or
    /// <paramref name="settings"/> is null.</exception>
    public static HotkeyAction Decide(HotkeyEvent signal, FanMode current, HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled) return HotkeyAction.None;

        switch (signal.Signal)
        {
            case HotkeySignal.FanModeStealth:
            case HotkeySignal.FanModeAutoLow:
            case HotkeySignal.FanModeAutoHigh:
            {
                // The spec asks for a cycle. NamedMode(signal.Signal) is the other reading,
                // kept beside this and one line away - see the class remarks and VERIFY 8.5.
                var next = NextMode(current);
                return new HotkeyAction(HotkeyOutcome.CycleFanMode, next, 0,
                    $"Fan mode: {next}", settings.OverlayForFanMode);
            }

            case HotkeySignal.KeyboardBacklightLevel:
                return new HotkeyAction(HotkeyOutcome.SetBacklightLevel, null, signal.Level,
                    $"Keyboard backlight {signal.Level} %", settings.OverlayForBacklight);

            case HotkeySignal.TouchpadEnabled:
                return new HotkeyAction(HotkeyOutcome.Notify, null, 0, "Touchpad on", settings.OverlayForTouchpad);
            case HotkeySignal.TouchpadDisabled:
                return new HotkeyAction(HotkeyOutcome.Notify, null, 0, "Touchpad off", settings.OverlayForTouchpad);
            case HotkeySignal.WifiEnabled:
                return new HotkeyAction(HotkeyOutcome.Notify, null, 0, "Wi-Fi on", settings.OverlayForWifi);
            case HotkeySignal.WifiDisabled:
                return new HotkeyAction(HotkeyOutcome.Notify, null, 0, "Wi-Fi off", settings.OverlayForWifi);

            default:
                // Display brightness, the launcher codes and the firmware reply. Brightness is
                // the one that matters: Windows draws that overlay already, and drawing a second
                // one is the complaint this release answers.
                return HotkeyAction.None;
        }
    }

    /// <summary>The mode the fan key moves to from <paramref name="current"/>.</summary>
    /// <remarks>Fixed and Custom are not on the ring - the ring is the four automatic modes - and
    /// they land on Normal rather than Quiet, because an owner running Fixed at a high duty under
    /// load must not be dropped to the quietest mode the machine has by one keypress.</remarks>
    public static FanMode NextMode(FanMode current) => current switch
    {
        FanMode.Quiet => FanMode.Normal,
        FanMode.Normal => FanMode.Gaming,
        FanMode.Gaming => FanMode.Turbo,
        FanMode.Turbo => FanMode.Quiet,
        _ => FanMode.Normal,
    };

    /// <summary>
    /// The mode each fan wire code names in Gigabyte's own firmware, or null for anything else.
    /// </summary>
    /// <remarks>
    /// Deliberately unused. The codes name stealth, auto low and auto high, and if the firmware
    /// turns out to run its own rotation underneath the app's, honouring the named mode is the
    /// better behaviour and this is the table <see cref="Decide"/> would switch to. Keeping it
    /// here and tested is what stops the recovered information being thrown away.
    /// </remarks>
    public static FanMode? NamedMode(HotkeySignal signal) => signal switch
    {
        HotkeySignal.FanModeStealth => FanMode.Quiet,
        HotkeySignal.FanModeAutoLow => FanMode.Normal,
        HotkeySignal.FanModeAutoHigh => FanMode.Gaming,
        _ => null,
    };
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/OpenAorus.Hardware/Hotkeys/HotkeyPolicy.cs \
        tests/OpenAorus.Hardware.Tests/HotkeyPolicyTests.cs
git commit -m "feat(hotkeys): decide what each signal means in one pure place"
```

---

### Task 5: Persisted hotkey settings

**Files:**
- Create: `src/OpenAorus.Hardware/Config/HotkeySettings.cs`
- Modify: `src/OpenAorus.Hardware/Config/AppSettings.cs`, `src/OpenAorus.Hardware/Config/SettingsStore.cs`, `src/OpenAorus.App/ViewModels/MainViewModel.cs`
- Test: `tests/OpenAorus.Hardware.Tests/HotkeySettingsTests.cs`
- Read: `src/OpenAorus.Hardware/Config/LightingSettings.cs` (the `Repair()` precedent),
  `tests/OpenAorus.Hardware.Tests/LightingSettingsTests.cs`,
  `tests/OpenAorus.Hardware.Tests/FanSettingsRepairTests.cs`

**Interfaces:**
- Produces:
  - `sealed class HotkeySettings` with `bool Enabled = true`, `bool OverlayForFanMode`,
    `bool OverlayForBacklight`, `bool OverlayForTouchpad`, `bool OverlayForWifi`,
    `int OverlaySeconds = 1`, `bool Repair()`,
    `const int MinOverlaySeconds = 1`, `const int MaxOverlaySeconds = 10`
  - `AppSettings.Hotkeys` (never-null property, same shape as `AppSettings.Lighting`)
  - `SettingsStore.LastLoadHotkeysRepaired`, folded into `LastLoadRepaired`
- Consumes: nothing.

**The overlay is off by default; the channels are on.** `Enabled` defaults to true because
listening *is* the release. Every `OverlayFor*` defaults to false because the design says the
overlay is opt-in per signal, and because an overlay the owner did not ask for is the thing they
complained about.

**Repair follows the existing precedent exactly.** `SettingsStore.Load` calls
`settings.Lighting.Repair()` and `settings.RepairFans()`; it now calls
`settings.Hotkeys.Repair()` alongside them, and the banner in `MainViewModel` gains a third
clause naming which half changed. A malformed `settings.json` still degrades to defaults with a
banner rather than to a crash.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/HotkeySettingsTests.cs`:

```csharp
using System.IO;
using OpenAorus.Hardware.Config;

namespace OpenAorus.Hardware.Tests;

public class HotkeySettingsTests
{
    [Fact]
    public void The_channels_are_on_and_every_overlay_is_off_out_of_the_box()
    {
        var s = new HotkeySettings();

        // Listening is the feature. Drawing is what the owner complained about.
        Assert.True(s.Enabled);
        Assert.False(s.OverlayForFanMode);
        Assert.False(s.OverlayForBacklight);
        Assert.False(s.OverlayForTouchpad);
        Assert.False(s.OverlayForWifi);
    }

    [Fact]
    public void A_fresh_instance_needs_no_repair()
    {
        Assert.False(new HotkeySettings().Repair());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(999)]
    public void An_overlay_duration_from_outside_the_range_is_brought_back_in(int seconds)
    {
        var s = new HotkeySettings { OverlaySeconds = seconds };

        Assert.True(s.Repair());
        Assert.InRange(s.OverlaySeconds, HotkeySettings.MinOverlaySeconds, HotkeySettings.MaxOverlaySeconds);
    }

    [Theory]
    [InlineData(HotkeySettings.MinOverlaySeconds)]
    [InlineData(3)]
    [InlineData(HotkeySettings.MaxOverlaySeconds)]
    public void A_duration_already_in_range_is_left_alone(int seconds)
    {
        var s = new HotkeySettings { OverlaySeconds = seconds };

        Assert.False(s.Repair());
        Assert.Equal(seconds, s.OverlaySeconds);
    }

    [Fact]
    public void Settings_written_without_a_hotkey_section_still_load()
    {
        // Every v0.1 and v0.2 settings.json is this file.
        var settings = new AppSettings { Hotkeys = null! };

        Assert.NotNull(settings.Hotkeys);
        Assert.True(settings.Hotkeys.Enabled);
    }

    [Fact]
    public void A_broken_hotkey_section_is_repaired_on_load_and_reported()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openaorus-hotkeys-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"Hotkeys":{"Enabled":true,"OverlaySeconds":0}}""");
            var store = new SettingsStore(path);

            var loaded = store.Load();

            Assert.False(store.LastLoadWasReset);
            Assert.True(store.LastLoadHotkeysRepaired);
            Assert.True(store.LastLoadRepaired);
            Assert.Equal(HotkeySettings.MinOverlaySeconds, loaded.Hotkeys.OverlaySeconds);
            // The rest of the file survives: this is the milder notice, not a reset.
            Assert.True(loaded.Hotkeys.Enabled);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void A_sound_hotkey_section_reports_no_repair()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openaorus-hotkeys-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"Hotkeys":{"Enabled":false,"OverlayForFanMode":true,"OverlaySeconds":2}}""");
            var store = new SettingsStore(path);

            var loaded = store.Load();

            Assert.False(store.LastLoadHotkeysRepaired);
            Assert.False(loaded.Hotkeys.Enabled);
            Assert.True(loaded.Hotkeys.OverlayForFanMode);
            Assert.Equal(2, loaded.Hotkeys.OverlaySeconds);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void The_toggles_survive_a_save_and_a_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openaorus-hotkeys-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var settings = new AppSettings();
            settings.Hotkeys.OverlayForFanMode = true;
            settings.Hotkeys.OverlayForWifi = true;
            settings.Hotkeys.OverlaySeconds = 4;
            store.Save(settings);

            var loaded = new SettingsStore(path).Load();

            Assert.True(loaded.Hotkeys.OverlayForFanMode);
            Assert.True(loaded.Hotkeys.OverlayForWifi);
            Assert.False(loaded.Hotkeys.OverlayForBacklight);
            Assert.Equal(4, loaded.Hotkeys.OverlaySeconds);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~HotkeySettingsTests`
Expected: compile error, `HotkeySettings` not found.

- [ ] **Step 3: Implement the settings class**

`src/OpenAorus.Hardware/Config/HotkeySettings.cs`:

```csharp
namespace OpenAorus.Hardware.Config;

/// <summary>
/// The hotkey half of <see cref="AppSettings"/>: whether to listen at all, and which signals the
/// owner wants to see an overlay for.
/// </summary>
/// <remarks>
/// <para>
/// Listening defaults on and every overlay defaults off. That split is the whole design: making
/// the Fn row work is the feature, and an on-screen display nobody asked for is the bug being
/// fixed. There is deliberately no toggle for volume or display brightness - the app never draws
/// for those, and offering a switch would imply it could.
/// </para>
/// <para>
/// Like the lighting settings, everything here arrives from a file anyone can edit, so
/// <see cref="Repair"/> exists to bring a loaded instance back inside the ranges the app accepts.
/// <see cref="SettingsStore.Load"/> calls it; nothing else needs to.
/// </para>
/// </remarks>
public sealed class HotkeySettings
{
    /// <summary>The shortest an overlay may stay up, in seconds.</summary>
    public const int MinOverlaySeconds = 1;

    /// <summary>The longest an overlay may stay up, in seconds.</summary>
    /// <remarks>A card that outlasts this stops reading as a notification and starts reading as
    /// a window that will not go away - which is the failure mode being replaced.</remarks>
    public const int MaxOverlaySeconds = 10;

    /// <summary>Whether the app listens to the two hotkey channels at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Show the overlay when the fan-mode key cycles the mode.</summary>
    public bool OverlayForFanMode { get; set; }

    /// <summary>Show the overlay when the keyboard changes its own backlight level.</summary>
    public bool OverlayForBacklight { get; set; }

    /// <summary>Show the overlay when the firmware toggles the touchpad.</summary>
    public bool OverlayForTouchpad { get; set; }

    /// <summary>Show the overlay when the firmware toggles the radio.</summary>
    public bool OverlayForWifi { get; set; }

    /// <summary>How long the overlay stays up, in seconds.</summary>
    public int OverlaySeconds { get; set; } = MinOverlaySeconds;

    /// <summary>
    /// Replaces any value that could not have come from this app with its nearest legal one, and
    /// reports whether it had to.
    /// </summary>
    /// <remarks>The same job and the same call site as
    /// <see cref="LightingSettings.Repair"/> and <see cref="AppSettings.RepairFans"/>. The return
    /// value drives the notice that tells the owner, so this stays honest about having changed
    /// nothing.</remarks>
    /// <returns>True if any value was replaced.</returns>
    public bool Repair()
    {
        var clamped = Math.Clamp(OverlaySeconds, MinOverlaySeconds, MaxOverlaySeconds);
        if (clamped == OverlaySeconds) return false;
        OverlaySeconds = clamped;
        return true;
    }
}
```

- [ ] **Step 4: Hang it off AppSettings**

In `src/OpenAorus.Hardware/Config/AppSettings.cs`, beside the `Lighting` property and following
its shape exactly:

```csharp
    private HotkeySettings _hotkeys = new();

    /// <summary>Fn hotkeys and the overlay. Never null: a file written with <c>"Hotkeys": null</c>,
    /// or one from v0.1 or v0.2 that has no hotkey section at all, still loads with usable
    /// defaults - the channels on and every overlay off.</summary>
    public HotkeySettings Hotkeys
    {
        get => _hotkeys;
        set => _hotkeys = value ?? new HotkeySettings();
    }
```

- [ ] **Step 5: Report the repair from the store**

In `src/OpenAorus.Hardware/Config/SettingsStore.cs`:

- add `public bool LastLoadHotkeysRepaired { get; private set; }` with a summary in the shape of
  the lighting and fan ones;
- widen `LastLoadRepaired` to `LastLoadLightingRepaired || LastLoadFansRepaired || LastLoadHotkeysRepaired`;
- reset it alongside the others at the top of `Load()`;
- set it after the two existing repairs: `LastLoadHotkeysRepaired = settings.Hotkeys.Repair();`.

- [ ] **Step 6: Name it in the banner**

In `MainViewModel`'s `LastLoadRepaired` branch, add a third clause beside the lighting and fan
ones, before the closing `parts.Add("Everything else in your settings was kept.")`:

```csharp
            if (_s.Store.LastLoadHotkeysRepaired)
                parts.Add("A saved hotkey overlay duration was outside the range the app accepts " +
                          $"and has been brought back inside {HotkeySettings.MinOverlaySeconds}-" +
                          $"{HotkeySettings.MaxOverlaySeconds} seconds.");
```

Add `using OpenAorus.Hardware.Config;` if it is not already there.

- [ ] **Step 7: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass, including `SettingsStoreTests`, `LightingSettingsTests`,
`FanSettingsRepairTests` and `MainViewModelRepairNoticeTests`.

- [ ] **Step 8: Commit**

```bash
git add src/OpenAorus.Hardware/Config/HotkeySettings.cs \
        src/OpenAorus.Hardware/Config/AppSettings.cs \
        src/OpenAorus.Hardware/Config/SettingsStore.cs \
        src/OpenAorus.App/ViewModels/MainViewModel.cs \
        tests/OpenAorus.Hardware.Tests/HotkeySettingsTests.cs
git commit -m "feat(hotkeys): persist the hotkey and overlay toggles, repairing a bad file"
```

---

### Task 6: The seams, the RAWINPUT buffer walk, and the fakes

**Files:**
- Create: `src/OpenAorus.Hardware/Hotkeys/IHotkeySource.cs`, `src/OpenAorus.Hardware/Hotkeys/RawInputBuffer.cs`
- Test: `tests/OpenAorus.Hardware.Tests/FakeHotkeySource.cs`, `tests/OpenAorus.Hardware.Tests/RawInputBufferTests.cs`
- Read: `src/OpenAorus.Hardware/Lighting/IKeyboardHid.cs`, `tests/OpenAorus.Hardware.Tests/FakeKeyboardHid.cs`, `src/OpenAorus.Hardware/Wmi/IGigabyteWmi.cs`

**Interfaces:**
- Produces:
  - `interface IHotkeySource : IDisposable { event Action<byte[]>? ReportReceived; void Start(); }`
  - `interface IWmiEventSource : IDisposable { event Action<int>? EventReceived; void Start(); }`
  - `static class RawInputBuffer` with `static IReadOnlyList<byte[]> Reports(byte[] buffer)`,
    `const int TypeHid = 2`, `const int HeaderSize = 24`, `const int MaxReportBytes = 64`,
    `const int MaxReports = 16`
  - test doubles `FakeHotkeySource` (`Emit(byte[])`, `Started`) and `FakeWmiEventSource`
    (`Emit(int)`, `Started`)
- Consumes: nothing.

**The seams carry undecoded bytes.** `IHotkeySource` hands over the HID report exactly as it came
off the wire, and `IWmiEventSource` hands over the raw `Data` value. That mirrors `IKeyboardHid`,
which carries undecoded 264-byte reports while `EffectPacket` stays pure, and it is what lets one
fake source cover the whole path from a four-byte report to an applied fan mode in Task 8. The
alternative - a seam that hands over a `HotkeyEvent` - would put the decoder on the untestable
side of the line for no gain.

**`RawInputBuffer` is the part of `WM_INPUT` handling that can be tested.** Walking the `RAWINPUT`
structure is arithmetic over a byte buffer, so it is pulled out here and handed hand-built
buffers. What is left in `RawInputWindow` after this is `RegisterRawInputDevices`, the
`HwndSource` and the window styles - four `OWNER VERIFY` items and nothing else.

The layout assumed is **x64 only**, which is what `RuntimeIdentifier win-x64` builds:
`RAWINPUTHEADER` is `dwType`(4) `dwSize`(4) `hDevice`(8) `wParam`(8) = 24 bytes, then `RAWHID` is
`dwSizeHid`(4) `dwCount`(4) and the reports.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/FakeHotkeySource.cs`:

```csharp
using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>A keyboard that types whatever a test tells it to.</summary>
public sealed class FakeHotkeySource : IHotkeySource
{
    public event Action<byte[]>? ReportReceived;

    /// <summary>Whether anyone opened the channel.</summary>
    public bool Started { get; private set; }

    /// <summary>Whether the channel was closed again.</summary>
    public bool Disposed { get; private set; }

    public void Start() => Started = true;

    /// <summary>Delivers one report, exactly as the vendor collection would.</summary>
    public void Emit(params byte[] report) => ReportReceived?.Invoke(report);

    public void Dispose() => Disposed = true;
}

/// <summary>A WMI subscription that fires whatever a test tells it to.</summary>
public sealed class FakeWmiEventSource : IWmiEventSource
{
    public event Action<int>? EventReceived;

    public bool Started { get; private set; }
    public bool Disposed { get; private set; }

    public void Start() => Started = true;

    /// <summary>Delivers one event's <c>Data</c> value.</summary>
    public void Emit(int data) => EventReceived?.Invoke(data);

    public void Dispose() => Disposed = true;
}
```

`tests/OpenAorus.Hardware.Tests/RawInputBufferTests.cs`:

```csharp
using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The walk over a <c>RAWINPUT</c> buffer, driven by hand-built buffers rather than by a keyboard.
/// </summary>
/// <remarks>
/// This is the whole of <c>WM_INPUT</c> handling that does not need a desktop. What is left after
/// it - the registration call, the message-only window and the extended styles - cannot be
/// exercised here and is in VERIFY instead.
/// </remarks>
public class RawInputBufferTests
{
    /// <summary>A buffer in the x64 layout: 24-byte header, then dwSizeHid, dwCount, the reports.</summary>
    private static byte[] Build(uint type, uint sizeHid, uint count, params byte[] payload)
    {
        var buffer = new byte[RawInputBuffer.HeaderSize + 8 + payload.Length];
        BitConverter.GetBytes(type).CopyTo(buffer, 0);
        BitConverter.GetBytes(sizeHid).CopyTo(buffer, RawInputBuffer.HeaderSize);
        BitConverter.GetBytes(count).CopyTo(buffer, RawInputBuffer.HeaderSize + 4);
        payload.CopyTo(buffer, RawInputBuffer.HeaderSize + 8);
        return buffer;
    }

    [Fact]
    public void One_four_byte_report_comes_back_whole()
    {
        var reports = RawInputBuffer.Reports(Build(RawInputBuffer.TypeHid, 4, 1, 4, 0, 0, 39));

        Assert.Equal(new byte[] { 4, 0, 0, 39 }, Assert.Single(reports));
    }

    [Fact]
    public void One_nine_byte_report_comes_back_whole()
    {
        var payload = new byte[] { 9, 0, 1, 3, 0, 60, 0, 0, 0 };

        var reports = RawInputBuffer.Reports(Build(RawInputBuffer.TypeHid, 9, 1, payload));

        Assert.Equal(payload, Assert.Single(reports));
    }

    [Fact]
    public void A_batched_buffer_is_split_into_its_reports()
    {
        // dwCount above 1 is legal and does happen; splitting on dwSizeHid is the only way to
        // read the second report at all.
        var reports = RawInputBuffer.Reports(
            Build(RawInputBuffer.TypeHid, 4, 3, 4, 0, 0, 37, 4, 1, 25, 0, 4, 0, 0, 137));

        Assert.Equal(3, reports.Count);
        Assert.Equal(new byte[] { 4, 0, 0, 37 }, reports[0]);
        Assert.Equal(new byte[] { 4, 1, 25, 0 }, reports[1]);
        Assert.Equal(new byte[] { 4, 0, 0, 137 }, reports[2]);
    }

    [Theory]
    [InlineData(0u)] // RIM_TYPEMOUSE
    [InlineData(1u)] // RIM_TYPEKEYBOARD
    [InlineData(7u)]
    public void Anything_that_is_not_a_hid_report_yields_nothing(uint type)
    {
        Assert.Empty(RawInputBuffer.Reports(Build(type, 4, 1, 4, 0, 0, 39)));
    }

    [Fact]
    public void A_buffer_shorter_than_its_own_header_yields_nothing()
    {
        Assert.Empty(RawInputBuffer.Reports(new byte[RawInputBuffer.HeaderSize]));
        Assert.Empty(RawInputBuffer.Reports(new byte[4]));
        Assert.Empty(RawInputBuffer.Reports(Array.Empty<byte>()));
    }

    [Fact]
    public void A_buffer_that_promises_more_than_it_holds_yields_nothing()
    {
        // Truncation must not be read as a short report, and the arithmetic must not overflow
        // into a negative length: this runs on data the app did not write.
        var truncated = Build(RawInputBuffer.TypeHid, 9, 4, 9, 0, 1, 3);

        Assert.Empty(RawInputBuffer.Reports(truncated));
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(1u, 0u)]
    [InlineData(uint.MaxValue, 1u)]
    [InlineData(4u, uint.MaxValue)]
    public void A_nonsense_size_or_count_yields_nothing(uint sizeHid, uint count)
    {
        Assert.Empty(RawInputBuffer.Reports(Build(RawInputBuffer.TypeHid, sizeHid, count, 4, 0, 0, 39)));
    }

    [Fact]
    public void A_report_longer_than_anything_this_keyboard_sends_yields_nothing()
    {
        var oversized = Build(RawInputBuffer.TypeHid, RawInputBuffer.MaxReportBytes + 1, 1,
            new byte[RawInputBuffer.MaxReportBytes + 1]);

        Assert.Empty(RawInputBuffer.Reports(oversized));
    }

    [Fact]
    public void A_null_buffer_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => RawInputBuffer.Reports(null!));
    }

    [Fact]
    public void The_reports_it_returns_survive_the_buffer_being_reused()
    {
        // The real caller reuses one stack buffer across messages. A returned report that
        // aliased it would change under the decoder's feet.
        var buffer = Build(RawInputBuffer.TypeHid, 4, 1, 4, 0, 0, 39);

        var report = Assert.Single(RawInputBuffer.Reports(buffer));
        Array.Clear(buffer);

        Assert.Equal(new byte[] { 4, 0, 0, 39 }, report);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~RawInputBufferTests`
Expected: compile error, `IHotkeySource` and `RawInputBuffer` not found.

- [ ] **Step 3: Implement the seams**

`src/OpenAorus.Hardware/Hotkeys/IHotkeySource.cs`:

```csharp
namespace OpenAorus.Hardware.Hotkeys;

/// <summary>
/// The only door to raw input. Real implementation: <c>OpenAorus.App.Hotkeys.RawInputWindow</c>.
/// </summary>
/// <remarks>
/// It carries the HID report undecoded, exactly as <see cref="Lighting.IKeyboardHid"/> carries
/// undecoded 264-byte reports. Decoding on this side of the seam would put
/// <see cref="RawInputDecoder"/> where no test can reach it; leaving the bytes alone means one
/// fake covers the whole path from a report to an applied fan mode.
/// </remarks>
public interface IHotkeySource : IDisposable
{
    /// <summary>Raised for each HID input report that arrives, on the thread that received it.</summary>
    event Action<byte[]>? ReportReceived;

    /// <summary>Opens the channel. Safe to call once; a second call does nothing.</summary>
    void Start();
}

/// <summary>
/// The only door to the WMI event stream. Real implementation:
/// <c>OpenAorus.App.Hotkeys.WmiEventListener</c>.
/// </summary>
/// <remarks>Carries the raw <c>Data</c> value for the same reason, and needs elevation where the
/// raw-input channel does not.</remarks>
public interface IWmiEventSource : IDisposable
{
    /// <summary>Raised with one event's <c>Data</c> value.</summary>
    event Action<int>? EventReceived;

    /// <summary>Opens the subscription.</summary>
    void Start();
}
```

- [ ] **Step 4: Implement the buffer walk**

`src/OpenAorus.Hardware/Hotkeys/RawInputBuffer.cs`:

```csharp
namespace OpenAorus.Hardware.Hotkeys;

/// <summary>
/// Reads the HID input reports out of the byte buffer <c>GetRawInputData</c> fills in.
/// </summary>
/// <remarks>
/// <para>
/// Pulled out of the window that receives <c>WM_INPUT</c> so it can be handed a hand-built buffer
/// by a test. The layout is the x64 one, which is the only one this app is built for
/// (<c>RuntimeIdentifier win-x64</c>): a 24-byte <c>RAWINPUTHEADER</c> - <c>dwType</c>,
/// <c>dwSize</c>, <c>hDevice</c>, <c>wParam</c> - then <c>RAWHID</c>'s <c>dwSizeHid</c> and
/// <c>dwCount</c>, then <c>dwCount</c> reports of <c>dwSizeHid</c> bytes each.
/// </para>
/// <para>
/// Everything it cannot make sense of yields no reports rather than throwing. This runs on data
/// the app did not write, on every raw-input message the system delivers, so a throw here would
/// be a fault raised from inside a window procedure.
/// </para>
/// </remarks>
public static class RawInputBuffer
{
    /// <summary><c>RIM_TYPEHID</c>.</summary>
    public const int TypeHid = 2;

    /// <summary>Size of <c>RAWINPUTHEADER</c> on x64.</summary>
    public const int HeaderSize = 24;

    /// <summary>The longest report this app will read out of a buffer.</summary>
    /// <remarks>The documented collections send 4 and 9 bytes; anything an order of magnitude
    /// past that is not something this app understands, and refusing it bounds the work done per
    /// message.</remarks>
    public const int MaxReportBytes = 64;

    /// <summary>The most reports this app will read out of one buffer.</summary>
    public const int MaxReports = 16;

    /// <summary>The reports inside one <c>RAWINPUT</c> buffer.</summary>
    /// <param name="buffer">The bytes <c>GetRawInputData</c> filled in.</param>
    /// <returns>One byte array per report, copied out of the buffer; empty for anything that is
    /// not a well-formed HID message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is null.</exception>
    public static IReadOnlyList<byte[]> Reports(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (buffer.Length <= HeaderSize + 8) return Array.Empty<byte[]>();
        if (BitConverter.ToUInt32(buffer, 0) != TypeHid) return Array.Empty<byte[]>();

        var sizeHid = BitConverter.ToUInt32(buffer, HeaderSize);
        var count = BitConverter.ToUInt32(buffer, HeaderSize + 4);
        if (sizeHid is 0 or > MaxReportBytes || count is 0 or > MaxReports) return Array.Empty<byte[]>();

        var start = HeaderSize + 8;
        // Widened before multiplying: the values come off the wire and 32-bit arithmetic here
        // would wrap into a length that looks like it fits.
        if (start + (long)sizeHid * count > buffer.Length) return Array.Empty<byte[]>();

        var reports = new List<byte[]>((int)count);
        for (var i = 0; i < count; i++)
        {
            // Copied, not sliced: the caller reuses one buffer across messages, and a report
            // that aliased it would change under the decoder.
            var report = new byte[sizeHid];
            Array.Copy(buffer, start + i * (int)sizeHid, report, 0, (int)sizeHid);
            reports.Add(report);
        }
        return reports;
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/OpenAorus.Hardware/Hotkeys/IHotkeySource.cs \
        src/OpenAorus.Hardware/Hotkeys/RawInputBuffer.cs \
        tests/OpenAorus.Hardware.Tests/FakeHotkeySource.cs \
        tests/OpenAorus.Hardware.Tests/RawInputBufferTests.cs
git commit -m "feat(hotkeys): add the input seams and the RAWINPUT buffer walk"
```

---

### Task 7: RawInputWindow and WmiEventListener - the OS-facing halves

**Files:**
- Create: `src/OpenAorus.App/Hotkeys/RawInputWindow.cs`, `src/OpenAorus.App/Hotkeys/WmiEventListener.cs`
- Test: `tests/OpenAorus.Hardware.Tests/RawInputWindowTests.cs`
- Read: `src/OpenAorus.Hardware/Hotkeys/IHotkeySource.cs`, `src/OpenAorus.Hardware/Hotkeys/RawInputBuffer.cs`, `src/OpenAorus.Hardware/Wmi/GigabyteWmi.cs`, `docs/research/fn-hotkey-signals.md`

**Interfaces:**
- Produces:
  - `sealed class RawInputWindow : IHotkeySource` with
    `static IReadOnlyList<(ushort Page, ushort Usage)> Usages`,
    `const int WmInput = 0x00FF`, `const int RidevInputSink = 0x00000100`
  - `sealed class WmiEventListener : IWmiEventSource`
- Consumes: `IHotkeySource`, `IWmiEventSource`, `RawInputBuffer` (Task 6), `WmiEventDecoder` (Task 2).

**Three usages, not five: a deliberate deviation from the binding spec.** The design spec says to
register "the same set Gigabyte's own shortcut process registers", which includes the standard
keyboard page `0x0001/0x0006` and the mouse `0x0001/0x0002`. Under `RIDEV_INPUTSINK` that
delivers **every keystroke typed anywhere on the machine** into this process, in an app that
already runs elevated, for no purpose - every documented signal arrives on a vendor collection.
So the registration is cut to `0xFF01/0x2209`, `0xFF02/0x0001` and `0xFF00/0xFF00`, and the
consumer-control page `0x000C` is excluded too. The side benefit is that "never draw a volume
overlay" stops being a policy decision and becomes structural: those keys never reach this
process at all.

The cost is named honestly in `VERIFY.md`: research open question 1 asks whether the Fn key
itself is observable or only the resulting combinations, and if a needed signal turns out to
arrive only on the keyboard page, this cut hides it. `VERIFY.md` 8.2 - press every Fn combination
and write down which do nothing - is what would show that, and the fallback is to add
`0x0001/0x0006` back and revisit. Do not add it speculatively.

Both classes here are the untestable end of the app. What a test can reach is the usage table,
and it is worth reaching: it is the only mechanical guarantee that the volume keys stay out.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/RawInputWindowTests.cs`:

```csharp
using OpenAorus.App.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The one part of the raw-input registration that can be checked without a desktop, and the
/// part that matters most.
/// </summary>
/// <remarks>
/// Gigabyte's own shortcut process registers five usages, two of which are the standard keyboard
/// and mouse pages. Under RIDEV_INPUTSINK those deliver every keystroke typed anywhere on the
/// machine into this elevated process for no purpose. Cutting them is what makes "OpenAorus never
/// draws a volume overlay" a fact about the registration rather than a promise about the code.
/// </remarks>
public class RawInputWindowTests
{
    [Fact]
    public void Exactly_three_vendor_collections_are_registered()
    {
        Assert.Equal(3, RawInputWindow.Usages.Count);
        Assert.Contains((0xFF01, 0x2209), RawInputWindow.Usages);
        Assert.Contains((0xFF02, 0x0001), RawInputWindow.Usages);
        Assert.Contains((0xFF00, 0xFF00), RawInputWindow.Usages);
    }

    [Fact]
    public void The_standard_keyboard_and_mouse_pages_are_never_registered()
    {
        // A regression here is a keylogger's data flow, not a cosmetic problem.
        Assert.DoesNotContain((0x0001, 0x0006), RawInputWindow.Usages);
        Assert.DoesNotContain((0x0001, 0x0002), RawInputWindow.Usages);
    }

    [Fact]
    public void The_consumer_control_page_is_never_registered()
    {
        // Volume lives here, and Windows already draws its overlay for it. Not registering the
        // page is why this app cannot draw a second one even by mistake.
        Assert.DoesNotContain(RawInputWindow.Usages, u => u.Page == 0x000C);
    }

    [Fact]
    public void Every_registered_page_is_a_vendor_defined_one()
    {
        // Vendor-defined pages start at 0xFF00. Anything below is a standard page and does not
        // belong in this list.
        Assert.All(RawInputWindow.Usages, u => Assert.True(u.Page >= 0xFF00, $"page {u.Page:X4}"));
    }

    [Fact]
    public void The_message_and_flag_constants_are_the_documented_ones()
    {
        Assert.Equal(0x00FF, RawInputWindow.WmInput);
        Assert.Equal(0x00000100, RawInputWindow.RidevInputSink);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~RawInputWindowTests`
Expected: compile error, `RawInputWindow` not found.

- [ ] **Step 3: Implement the raw-input window**

`src/OpenAorus.App/Hotkeys/RawInputWindow.cs`. Note `using System.Windows.Interop;` for
`HwndSource`, and fully qualify anything from the ambiguous list.

```csharp
using System.Runtime.InteropServices;
using System.Windows.Interop;
using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.App.Hotkeys;

/// <summary>
/// A message-only window that receives <c>WM_INPUT</c> for the keyboard's vendor collections and
/// hands the reports on undecoded.
/// </summary>
/// <remarks>
/// <para>
/// Three usages, not the five Gigabyte's own shortcut process registers. The two it drops are the
/// standard keyboard and mouse pages, which under <c>RIDEV_INPUTSINK</c> would deliver every
/// keystroke typed anywhere on the machine into this elevated process - for nothing, since every
/// documented signal arrives on a vendor collection. The consumer-control page carrying the
/// volume keys is excluded for the same reason and one more: not receiving them is what makes it
/// impossible for this app to draw a second volume overlay.
/// </para>
/// <para>
/// Nothing here is exercised by a test beyond <see cref="Usages"/>. The registration call, the
/// window and the message loop need a desktop; VERIFY 8.1 and 8.2 are what confirm them.
/// </para>
/// </remarks>
public sealed class RawInputWindow : IHotkeySource
{
    /// <summary>The vendor collections this app listens to, and nothing else.</summary>
    public static IReadOnlyList<(ushort Page, ushort Usage)> Usages { get; } = new[]
    {
        ((ushort)0xFF01, (ushort)0x2209),
        ((ushort)0xFF02, (ushort)0x0001),   // the Fn hotkey collection
        ((ushort)0xFF00, (ushort)0xFF00),
    };

    /// <summary><c>WM_INPUT</c>.</summary>
    public const int WmInput = 0x00FF;

    /// <summary><c>RIDEV_INPUTSINK</c>: deliver input regardless of which window has focus.</summary>
    public const int RidevInputSink = 0x00000100;

    private const int RidDeviceInfo = 0x10000003;   // RID_INPUT
    private static readonly IntPtr HwndMessage = new(-3);

    private HwndSource? _source;
    private bool _started;

    /// <inheritdoc />
    public event Action<byte[]>? ReportReceived;

    /// <inheritdoc />
    public void Start()
    {
        if (_started) return;
        _started = true;

        // Message-only: no desktop presence, no taskbar entry, and it cannot be activated.
        var parameters = new HwndSourceParameters("OpenAorus.RawInput")
        {
            ParentWindow = HwndMessage,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(OnMessage);

        var devices = Usages
            .Select(u => new RawInputDevice
            {
                UsagePage = u.Page,
                Usage = u.Usage,
                Flags = RidevInputSink,
                Target = _source.Handle,
            })
            .ToArray();

        // A failure is not fatal: the WMI channel may still work, and the app's own UI is
        // unaffected. VERIFY 8.1 is what says whether this succeeded on real hardware.
        RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmInput) return IntPtr.Zero;

        // Not marked handled: this is an input sink, and swallowing the message would take the
        // key away from whatever program the owner is actually typing into.
        var size = 0u;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(lParam, RidDeviceInfo, IntPtr.Zero, ref size, headerSize) != 0 || size == 0)
            return IntPtr.Zero;

        var buffer = new byte[size];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            if (GetRawInputData(lParam, RidDeviceInfo, handle.AddrOfPinnedObject(), ref size, headerSize) != size)
                return IntPtr.Zero;
        }
        finally { handle.Free(); }

        foreach (var report in RawInputBuffer.Reports(buffer))
            ReportReceived?.Invoke(report);

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source?.RemoveHook(OnMessage);
        _source?.Dispose();
        _source = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public int Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
}
```

- [ ] **Step 4: Implement the WMI listener**

`src/OpenAorus.App/Hotkeys/WmiEventListener.cs`:

```csharp
using System.Management;
using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.App.Hotkeys;

/// <summary>
/// Subscribes to <c>GB_WMIACPI_Event</c> in <c>root\WMI</c> and hands each event's <c>Data</c>
/// value on undecoded.
/// </summary>
/// <remarks>
/// <para>
/// Needs elevation, which the app already has. The scope and options mirror
/// <see cref="OpenAorus.Hardware.Wmi.GigabyteWmi"/>; the query and the property name come from
/// <see cref="WmiEventDecoder"/> so the one checkable thing about this subscription is checkable
/// without a WMI provider.
/// </para>
/// <para>
/// The property name is unconfirmed on hardware. A wrong name is a subscription that runs and
/// reports nothing, silently, which is why VERIFY 8.1 asks for it directly rather than inferring
/// it from a key not working.
/// </para>
/// </remarks>
public sealed class WmiEventListener : IWmiEventSource
{
    private const string ScopePath = @"root\WMI";

    private ManagementEventWatcher? _watcher;
    private bool _started;

    /// <inheritdoc />
    public event Action<int>? EventReceived;

    /// <inheritdoc />
    public void Start()
    {
        if (_started) return;
        _started = true;

        try
        {
            var options = new ConnectionOptions
            {
                EnablePrivileges = true,
                Impersonation = ImpersonationLevel.Impersonate,
            };
            var scope = new ManagementScope(ScopePath, options);
            scope.Connect();

            _watcher = new ManagementEventWatcher(scope, new EventQuery(WmiEventDecoder.Query));
            _watcher.EventArrived += OnEventArrived;
            _watcher.Start();
        }
        catch (Exception)
        {
            // Broad on purpose, and swallowed on purpose. This channel carries the touchpad and
            // radio notices only; a provider that refuses the subscription must not stop the
            // raw-input channel or the rest of the app from starting.
            _watcher = null;
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var value = e.NewEvent?[WmiEventDecoder.DataProperty];
            if (value is null) return;
            EventReceived?.Invoke(Convert.ToInt32(value));
        }
        catch (Exception)
        {
            // The property may be absent (a brightness event carries Brightness instead) or of a
            // type that will not convert. Either is "nothing this app understands", which is the
            // same answer the decoder would give.
        }
    }

    public void Dispose()
    {
        if (_watcher is null) return;
        _watcher.EventArrived -= OnEventArrived;
        try { _watcher.Stop(); } catch (Exception) { }
        _watcher.Dispose();
        _watcher = null;
    }
}
```

- [ ] **Step 5: Run tests and build Release**

Run: `dotnet test OpenAorus.sln`
Run: `dotnet build OpenAorus.sln -c Release`
Expected: all pass; **no warnings** at Debug or Release.

- [ ] **Step 6: `OWNER VERIFY` - record what could not be checked here**

Four things in this task have no test and must reach `VERIFY.md` section 8 in Task 10:
`RegisterRawInputDevices` actually succeeding, the `HwndSource` message-only window receiving
`WM_INPUT` at all, the `ManagementEventWatcher` delivering, and the `Data` property name.

- [ ] **Step 7: Commit**

```bash
git add src/OpenAorus.App/Hotkeys/RawInputWindow.cs \
        src/OpenAorus.App/Hotkeys/WmiEventListener.cs \
        tests/OpenAorus.Hardware.Tests/RawInputWindowTests.cs
git commit -m "feat(hotkeys): listen on three vendor collections and the WMI event stream"
```

---

### Task 8: HotkeyService, and acting on what it decides

**Files:**
- Create: `src/OpenAorus.App/Hotkeys/HotkeyService.cs`
- Modify: `src/OpenAorus.App/ViewModels/MainViewModel.cs`, `src/OpenAorus.App/ViewModels/LightingViewModel.cs`, `src/OpenAorus.App/AppServices.cs`
- Test: `tests/OpenAorus.Hardware.Tests/HotkeyServiceTests.cs`
- Read: `tests/OpenAorus.Hardware.Tests/FakeHotkeySource.cs` (Task 6), `src/OpenAorus.App/ViewModels/MainViewModel.cs` (`RunWatchdogAsync`, `SelectModeAsync`), `src/OpenAorus.Hardware/Fans/FanController.cs`

**Interfaces:**
- Produces:
  - `sealed class HotkeyService : IDisposable` with
    `HotkeyService(IHotkeySource raw, IWmiEventSource wmi, HotkeySettings settings, Func<FanMode> currentMode, Func<long>? clock = null, SignalDebouncer? debouncer = null)`,
    `event Action<HotkeyAction>? ActionRequested`, `void Start()`
  - `LightingViewModel.NoteFirmwareBacklight(int percent)`
  - `MainViewModel.OnHotkeyAsync(HotkeyAction action)`, `event Action<string>? OverlayRequested`
- Consumes: everything from Tasks 1-7.

`HotkeyService` is the wiring and nothing else: source → decoder → debouncer → policy → event.
`MainViewModel` does the acting, exactly as it does for `FanWatchdog`.

**The backlight is never written back.** The firmware has already changed the keyboard's
brightness by the time the report arrives. Writing the level back would be a redundant 264-byte
report racing the firmware, so `NoteFirmwareBacklight` moves the slider with the live write
suppressed and records the value.

**The fan cycle is not gated on `IsBusy`.** `FanController` serializes its own sequences behind a
semaphore. A press that lands during another write must queue, not be dropped - and note that
each apply is a seven-step sequence paced at 500 ms, so five rapid presses take about fifteen
seconds to settle. That is expected, and `VERIFY.md` 8.4 says so.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/HotkeyServiceTests.cs`:

```csharp
using OpenAorus.App.Hotkeys;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The whole path from a four-byte report to a requested action, over fakes.
/// </summary>
/// <remarks>
/// This is what the undecoded seams bought: one fake source and one fake event stream cover the
/// decoder, the debouncer and the policy together, so the only untested thing left between a
/// keypress and a fan write is the registration call itself.
/// </remarks>
public class HotkeyServiceTests
{
    private sealed class Rig : IDisposable
    {
        public FakeHotkeySource Raw { get; } = new();
        public FakeWmiEventSource Wmi { get; } = new();
        public HotkeySettings Settings { get; } = new()
        {
            Enabled = true,
            OverlayForFanMode = true,
            OverlayForBacklight = true,
            OverlayForTouchpad = true,
            OverlayForWifi = true,
        };
        public List<HotkeyAction> Actions { get; } = new();
        public FanMode Mode { get; set; } = FanMode.Quiet;
        public long Now { get; set; }
        public HotkeyService Service { get; }

        public Rig()
        {
            Service = new HotkeyService(Raw, Wmi, Settings, () => Mode, () => Now);
            Service.ActionRequested += Actions.Add;
            Service.Start();
        }

        public void Dispose() => Service.Dispose();
    }

    [Fact]
    public void Starting_opens_both_channels()
    {
        using var rig = new Rig();

        Assert.True(rig.Raw.Started);
        Assert.True(rig.Wmi.Started);
    }

    [Fact]
    public void A_fan_report_becomes_a_cycle_to_the_next_mode()
    {
        using var rig = new Rig();

        rig.Raw.Emit(4, 0, 0, 39);

        var action = Assert.Single(rig.Actions);
        Assert.Equal(HotkeyOutcome.CycleFanMode, action.Outcome);
        Assert.Equal(FanMode.Normal, action.Mode);
        Assert.True(action.ShowOverlay);
    }

    [Fact]
    public void The_cycle_is_read_from_the_mode_the_app_is_actually_in()
    {
        using var rig = new Rig { Mode = FanMode.Gaming };

        rig.Raw.Emit(4, 0, 0, 37);

        Assert.Equal(FanMode.Turbo, Assert.Single(rig.Actions).Mode);
    }

    [Fact]
    public void The_same_report_twice_inside_the_window_acts_once()
    {
        using var rig = new Rig();

        rig.Raw.Emit(4, 0, 0, 39);
        rig.Now += SignalDebouncer.WindowMs - 1;
        rig.Raw.Emit(4, 0, 0, 39);

        Assert.Single(rig.Actions);
    }

    [Fact]
    public void The_same_report_outside_the_window_acts_twice()
    {
        using var rig = new Rig();

        rig.Raw.Emit(4, 0, 0, 39);
        rig.Now += SignalDebouncer.WindowMs;
        rig.Raw.Emit(4, 0, 0, 39);

        Assert.Equal(2, rig.Actions.Count);
    }

    [Fact]
    public void A_backlight_report_carries_the_level_the_firmware_moved_to()
    {
        using var rig = new Rig();

        rig.Raw.Emit(4, 1, 50, 0);

        var action = Assert.Single(rig.Actions);
        Assert.Equal(HotkeyOutcome.SetBacklightLevel, action.Outcome);
        Assert.Equal(100, action.Level);
    }

    [Fact]
    public void A_wmi_event_becomes_a_notice()
    {
        using var rig = new Rig();

        rig.Wmi.Emit(202);

        var action = Assert.Single(rig.Actions);
        Assert.Equal(HotkeyOutcome.Notify, action.Outcome);
        Assert.Contains("Touchpad", action.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_brightness_report_produces_no_action_at_all()
    {
        using var rig = new Rig();

        rig.Raw.Emit(9, 0, 1, 3, 0, 60, 0, 0, 0);

        // The bug this release fixes: Windows draws that overlay already.
        Assert.Empty(rig.Actions);
    }

    [Theory]
    [InlineData(new byte[] { 4, 0, 0, 200 })]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 4, 1, 33, 0 })]
    public void A_report_it_cannot_decode_produces_nothing(byte[] report)
    {
        using var rig = new Rig();

        rig.Raw.Emit(report);

        Assert.Empty(rig.Actions);
    }

    [Fact]
    public void An_unknown_wmi_value_produces_nothing()
    {
        using var rig = new Rig();

        rig.Wmi.Emit(1);

        Assert.Empty(rig.Actions);
    }

    [Fact]
    public void Switching_hotkeys_off_at_run_time_stops_the_actions_without_restarting()
    {
        using var rig = new Rig();
        rig.Settings.Enabled = false;

        rig.Raw.Emit(4, 0, 0, 39);
        rig.Wmi.Emit(450);

        // The settings object is the live one the Settings window edits, so a toggle takes
        // effect on the next keypress rather than on the next launch.
        Assert.Empty(rig.Actions);
    }

    [Fact]
    public void Disposing_closes_both_channels()
    {
        var rig = new Rig();
        rig.Dispose();

        Assert.True(rig.Raw.Disposed);
        Assert.True(rig.Wmi.Disposed);
    }

    [Fact]
    public void A_report_arriving_after_disposal_does_nothing()
    {
        var rig = new Rig();
        rig.Dispose();

        rig.Raw.Emit(4, 0, 0, 39);

        Assert.Empty(rig.Actions);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~HotkeyServiceTests`
Expected: compile error, `HotkeyService` not found.

- [ ] **Step 3: Implement the service**

`src/OpenAorus.App/Hotkeys/HotkeyService.cs`:

```csharp
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.App.Hotkeys;

/// <summary>
/// Wires the two channels to the pure parts and raises one action per real keypress.
/// </summary>
/// <remarks>
/// <para>
/// Wiring and nothing else: source, decoder, debouncer, policy, event. Every decision lives in
/// <see cref="HotkeyPolicy"/> and every byte pattern in <see cref="RawInputDecoder"/>, so this
/// class has nothing to get wrong that a test could not see.
/// </para>
/// <para>
/// It raises rather than acts, for the reason <see cref="ViewModels.FanWatchdog"/> does: the
/// acting needs the fan controller, the lighting view model and a window, and none of those
/// belong on the path a raw-input message travels.
/// </para>
/// <para>
/// The settings object is the live one, not a copy. Toggling hotkeys off in the Settings window
/// takes effect on the next keypress instead of on the next launch.
/// </para>
/// </remarks>
public sealed class HotkeyService : IDisposable
{
    private readonly IHotkeySource _raw;
    private readonly IWmiEventSource _wmi;
    private readonly HotkeySettings _settings;
    private readonly Func<FanMode> _currentMode;
    private readonly Func<long> _clock;
    private readonly SignalDebouncer _debouncer;

    private bool _disposed;

    /// <summary>Raised once per keypress the app can service.</summary>
    public event Action<HotkeyAction>? ActionRequested;

    /// <param name="raw">The raw-input channel.</param>
    /// <param name="wmi">The WMI event channel.</param>
    /// <param name="settings">The owner's live hotkey settings.</param>
    /// <param name="currentMode">What the app believes the fans are running.</param>
    /// <param name="clock">A monotonic millisecond clock; defaults to
    /// <see cref="Environment.TickCount64"/>.</param>
    /// <param name="debouncer">Injectable so a test can shorten the window.</param>
    public HotkeyService(
        IHotkeySource raw,
        IWmiEventSource wmi,
        HotkeySettings settings,
        Func<FanMode> currentMode,
        Func<long>? clock = null,
        SignalDebouncer? debouncer = null)
    {
        _raw = raw;
        _wmi = wmi;
        _settings = settings;
        _currentMode = currentMode;
        _clock = clock ?? (() => Environment.TickCount64);
        _debouncer = debouncer ?? new SignalDebouncer();

        _raw.ReportReceived += OnReport;
        _wmi.EventReceived += OnWmiEvent;
    }

    /// <summary>Opens both channels.</summary>
    public void Start()
    {
        _raw.Start();
        _wmi.Start();
    }

    private void OnReport(byte[] report) => Handle(RawInputDecoder.Decode(report));

    private void OnWmiEvent(int data) => Handle(WmiEventDecoder.Decode(data));

    private void Handle(HotkeyEvent? signal)
    {
        if (_disposed || signal is null) return;
        if (!_debouncer.ShouldFire(signal, _clock())) return;

        var action = HotkeyPolicy.Decide(signal, _currentMode(), _settings);
        if (action.Outcome == HotkeyOutcome.Ignore) return;

        ActionRequested?.Invoke(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _raw.ReportReceived -= OnReport;
        _wmi.EventReceived -= OnWmiEvent;
        _raw.Dispose();
        _wmi.Dispose();
    }
}
```

- [ ] **Step 4: Let the lighting panel take the firmware's level**

In `src/OpenAorus.App/ViewModels/LightingViewModel.cs`, beside the other public members:

```csharp
    /// <summary>
    /// Moves the brightness slider to the level the keyboard's firmware has just set itself to.
    /// </summary>
    /// <remarks>
    /// The keyboard changed its own backlight before this app heard about it, so nothing is
    /// written back: a write here would be a redundant 264-byte report racing the firmware for
    /// the same value. The live write is suppressed for exactly that reason, and the setting is
    /// recorded so a restart restores what the owner last left the keyboard on.
    /// </remarks>
    /// <param name="percent">The level the firmware reported, 0-100.</param>
    public void NoteFirmwareBacklight(int percent)
    {
        var level = Math.Clamp(percent, 0, 100);
        _suppressLiveWrite = true;
        try { BrightnessPercent = level; }
        finally { _suppressLiveWrite = false; }

        _s.Settings.Lighting.BrightnessPercent = level;
        try { _s.Store.Save(_s.Settings); }
        catch (Exception ex) { _banner(BannerKind.Error, $"Lighting could not be saved: {ex.Message}"); }
    }
```

- [ ] **Step 5: Act on the action**

In `src/OpenAorus.App/ViewModels/MainViewModel.cs`:

```csharp
    /// <summary>Raised when a serviced hotkey wants the overlay shown. The window owns the card.</summary>
    public event Action<string>? OverlayRequested;

    /// <summary>
    /// Acts on one hotkey. <see cref="Hotkeys.HotkeyService"/> decides; this does.
    /// </summary>
    /// <remarks>Public because the whole point of the pure decision is that the acting can be
    /// driven an action at a time in a test.</remarks>
    /// <param name="action">What the policy decided.</param>
    public async Task OnHotkeyAsync(HotkeyAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        switch (action.Outcome)
        {
            case HotkeyOutcome.CycleFanMode when action.Mode is { } mode:
                // Not gated on IsBusy: FanController serializes its own sequences, and a press
                // that lands during another write has to queue rather than be dropped. Each
                // apply is seven steps at 500 ms, so a burst takes seconds to settle.
                await SelectModeAsync(mode);
                break;

            case HotkeyOutcome.SetBacklightLevel:
                Lighting.NoteFirmwareBacklight(action.Level);
                break;

            case HotkeyOutcome.Notify:
                // The firmware already did it. There is nothing to write and nothing to save.
                break;
        }

        if (action.ShowOverlay) OverlayRequested?.Invoke(action.Text);
    }
```

`SelectModeAsync` is currently a private `[RelayCommand]` method; widen it to `internal` or call
`SelectModeCommand.ExecuteAsync(mode)`. Prefer widening - the command wrapper adds a
`CanExecute` gate this path does not want.

- [ ] **Step 6: Build the service in AppServices and start it**

In `AppServices`, add:

```csharp
    /// <summary>The Fn hotkey channels. Null when hotkeys are switched off in settings.</summary>
    public Hotkeys.HotkeyService? Hotkeys { get; init; }
```

built in `Create()` as
`new HotkeyService(new RawInputWindow(), new WmiEventListener(), settings.Hotkeys, () => settings.Mode)`
when `settings.Hotkeys.Enabled`, and `null` otherwise. In `MainViewModel.InitializeAsync`,
subscribe `_s.Hotkeys.ActionRequested += a => _ = OnHotkeyAsync(a);` and call `Start()`; in
`Shutdown()`, unsubscribe and `Dispose()`.

Dropping the task rather than awaiting it mirrors the sensor poller: the raw-input hook has
nowhere to await, and `FanController`'s own gate is what stops two sequences overlapping.

Note `--apply` and `--dump` exit before a window exists, so neither should start the service;
build it only on the path that constructs `MainViewModel`.

- [ ] **Step 7: Run tests and build Release**

Run: `dotnet test OpenAorus.sln`
Run: `dotnet build OpenAorus.sln -c Release`
Expected: all pass, no warnings.

- [ ] **Step 8: Commit**

```bash
git add src/OpenAorus.App/Hotkeys/HotkeyService.cs \
        src/OpenAorus.App/ViewModels/MainViewModel.cs \
        src/OpenAorus.App/ViewModels/LightingViewModel.cs \
        src/OpenAorus.App/AppServices.cs \
        tests/OpenAorus.Hardware.Tests/HotkeyServiceTests.cs
git commit -m "feat(hotkeys): act on the Fn row through the fan and lighting paths"
```

---

### Task 9: The overlay and the Settings card

**Files:**
- Create: `src/OpenAorus.App/Views/OverlayPlacement.cs`, `src/OpenAorus.App/Views/OverlayWindow.xaml`, `src/OpenAorus.App/Views/OverlayWindow.xaml.cs`, `src/OpenAorus.App/ViewModels/HotkeysViewModel.cs`
- Modify: `src/OpenAorus.App/Views/SettingsWindow.xaml`, `src/OpenAorus.App/Views/SettingsWindow.xaml.cs`, `src/OpenAorus.App/ViewModels/SettingsViewModel.cs`, `src/OpenAorus.App/Views/MainWindow.xaml.cs`
- Test: `tests/OpenAorus.Hardware.Tests/OverlayPlacementTests.cs`, `tests/OpenAorus.Hardware.Tests/HotkeySettingsUiTests.cs`
- Read: `src/OpenAorus.App/Views/SettingsWindow.xaml` (the card pattern), `src/OpenAorus.App/Themes/Dark.xaml` (the keys that exist), `tests/OpenAorus.Hardware.Tests/XamlResourceTests.cs`

**Interfaces:**
- Produces:
  - `static class OverlayPlacement` with `const double BottomMarginDip = 96`,
    `static double Left(double areaLeft, double areaWidth, double widthDip)`,
    `static double Top(double areaTop, double areaHeight, double heightDip)`
  - `sealed partial class OverlayWindow : Window` with `void Show(string text, int seconds)`
  - `sealed partial class HotkeysViewModel : ObservableObject` with `Enabled`,
    `OverlayForFanMode`, `OverlayForBacklight`, `OverlayForTouchpad`, `OverlayForWifi`,
    `OverlaySeconds`, `SaveCommand`
- Consumes: `HotkeySettings` (Task 5), `MainViewModel.OverlayRequested` (Task 8).

**The hotkey card sits outside `SettingsWindow`'s `CanWrite` gate.** On a model the app does not
recognise, everything inside that gate is disabled because the WMI writes are withheld. Three of
the four hotkey halves - touchpad, Wi-Fi and backlight - need no WMI at all, so gating them would
disable working features to protect one that does not work. Only fan cycling is affected on such
a model, and the fan controller already refuses it with a clear message. A test pins this.

**XAML constraints for this task.** Do not give any element an `x:Name` matching a `UserControl`
type name. Use only `{StaticResource}` keys that exist in `Themes/Dark.xaml` when you write this
- the theme is under active revision, so check rather than copy from this plan. Bind
`IsChecked` two-way and let `SaveCommand` persist. No `DataTrigger` with `Value="True"` against
an object-typed property.

**What cannot be tested here**, and goes to `VERIFY.md`: the extended window styles
(`WS_EX_TRANSPARENT`, `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`), and the DPI transform, which is
correct on a single-DPI setup by construction and takes its scale from the wrong monitor on a
mixed-DPI one.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/OverlayPlacementTests.cs`:

```csharp
using OpenAorus.App.Views;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Where the card sits, in device-independent units, given a work area and a card size.
/// </summary>
/// <remarks>
/// Pure doubles rather than <c>Rect</c> or <c>Size</c>: this project enables WPF and WinForms
/// together, so <c>Point</c> and <c>Size</c> are ambiguous types and keeping them out of the
/// signature is cheaper than aliasing them at every call site. Which monitor's work area gets
/// passed in, and the DPI it is measured in, are the code-behind's problem and VERIFY's.
/// </remarks>
public class OverlayPlacementTests
{
    [Fact]
    public void The_card_is_centred_horizontally_on_its_screen()
    {
        Assert.Equal(860, OverlayPlacement.Left(areaLeft: 0, areaWidth: 1920, widthDip: 200));
    }

    [Fact]
    public void A_second_monitor_to_the_left_is_centred_on_too()
    {
        // Negative origins are ordinary on a multi-monitor desktop.
        Assert.Equal(-1060, OverlayPlacement.Left(areaLeft: -1920, areaWidth: 1920, widthDip: 200));
    }

    [Fact]
    public void The_card_sits_above_the_bottom_of_the_work_area_by_the_margin()
    {
        var top = OverlayPlacement.Top(areaTop: 0, areaHeight: 1080, heightDip: 80);

        Assert.Equal(1080 - 80 - OverlayPlacement.BottomMarginDip, top);
    }

    [Fact]
    public void The_work_areas_own_origin_is_respected()
    {
        var top = OverlayPlacement.Top(areaTop: 100, areaHeight: 900, heightDip: 80);

        Assert.Equal(100 + 900 - 80 - OverlayPlacement.BottomMarginDip, top);
    }

    [Fact]
    public void A_card_taller_than_its_screen_is_pinned_to_the_top_rather_than_pushed_off_it()
    {
        var top = OverlayPlacement.Top(areaTop: 0, areaHeight: 100, heightDip: 400);

        Assert.Equal(0, top);
    }

    [Fact]
    public void A_card_wider_than_its_screen_is_pinned_to_the_left_edge()
    {
        Assert.Equal(0, OverlayPlacement.Left(areaLeft: 0, areaWidth: 200, widthDip: 400));
    }
}
```

`tests/OpenAorus.Hardware.Tests/HotkeySettingsUiTests.cs`:

```csharp
using System.IO;
using System.Xml.Linq;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The two things about the hotkey card that the XAML compiler cannot check.
/// </summary>
/// <remarks>
/// Read as plain XML for the same reason as <see cref="XamlResourceTests"/>: loading it properly
/// needs an Application on an STA thread, which is process-global and would make every other test
/// in this assembly order-dependent.
/// </remarks>
public class HotkeySettingsUiTests
{
    private static XDocument Settings() =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "xaml", "Views", "SettingsWindow.xaml"));

    private static bool BindsTo(XElement e, string property) =>
        e.Attributes().Any(a => a.Value.Contains($"Binding {property}", StringComparison.Ordinal));

    [Fact]
    public void The_hotkey_toggles_are_in_the_settings_window()
    {
        var doc = Settings();

        foreach (var property in new[]
                 {
                     "HotkeysEnabled", "OverlayForFanMode", "OverlayForBacklight",
                     "OverlayForTouchpad", "OverlayForWifi",
                 })
            Assert.True(doc.Descendants().Any(e => BindsTo(e, property)), $"nothing binds {property}");
    }

    [Fact]
    public void The_hotkey_card_is_outside_the_gate_that_disables_the_dialog_on_an_unknown_model()
    {
        // Touchpad, Wi-Fi and backlight need no WMI at all, so an unrecognised model must not
        // lose them. Only fan cycling is affected there, and the fan controller already refuses
        // that with a message of its own.
        var doc = Settings();

        var gated = doc.Descendants()
            .Where(e => e.Attribute("IsEnabled")?.Value.Contains("CanWrite", StringComparison.Ordinal) == true)
            .ToList();
        Assert.NotEmpty(gated);

        var toggles = doc.Descendants().Where(e => BindsTo(e, "HotkeysEnabled")).ToList();
        Assert.NotEmpty(toggles);

        foreach (var toggle in toggles)
            Assert.DoesNotContain(gated, g => g.Descendants().Contains(toggle));
    }

    [Fact]
    public void There_is_no_overlay_toggle_for_anything_windows_already_draws()
    {
        // Offering one would imply the app could draw for volume or display brightness. It
        // cannot: it never receives the volume keys, and it ignores the brightness report.
        var text = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "xaml", "Views", "SettingsWindow.xaml"));

        Assert.DoesNotContain("OverlayForVolume", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OverlayForBrightness", text, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter "FullyQualifiedName~OverlayPlacementTests|FullyQualifiedName~HotkeySettingsUiTests"`
Expected: compile error, `OverlayPlacement` not found; the XAML tests fail on the missing bindings.

- [ ] **Step 3: Implement the placement**

`src/OpenAorus.App/Views/OverlayPlacement.cs`:

```csharp
namespace OpenAorus.App.Views;

/// <summary>
/// Where the overlay card sits, in device-independent units.
/// </summary>
/// <remarks>
/// Doubles rather than <c>Rect</c>: this project enables WPF and WinForms together, so
/// <c>Point</c> and <c>Size</c> are ambiguous and keeping them out of the signature costs
/// nothing. Choosing the monitor and converting its bounds to DIPs stays in the code-behind,
/// where a test cannot reach it - see VERIFY 8.8.
/// </remarks>
public static class OverlayPlacement
{
    /// <summary>How far above the bottom of the work area the card sits, in DIPs.</summary>
    /// <remarks>Roughly where Windows puts its own volume card, so the two do not stack when
    /// both are up during the verification steps.</remarks>
    public const double BottomMarginDip = 96;

    /// <summary>The card's left edge, centred on the work area.</summary>
    public static double Left(double areaLeft, double areaWidth, double widthDip) =>
        areaLeft + Math.Max(0, (areaWidth - widthDip) / 2);

    /// <summary>The card's top edge, sitting <see cref="BottomMarginDip"/> above the bottom.</summary>
    /// <remarks>Clamped to the top of the work area: a card taller than the screen belongs on
    /// screen rather than above it.</remarks>
    public static double Top(double areaTop, double areaHeight, double heightDip) =>
        areaTop + Math.Max(0, areaHeight - heightDip - BottomMarginDip);
}
```

- [ ] **Step 4: Implement the overlay window**

`src/OpenAorus.App/Views/OverlayWindow.xaml` - borderless, transparent, never activated, and
click-through. Check `Themes/Dark.xaml` for the current key names before using any:

```xml
<Window x:Class="OpenAorus.App.Views.OverlayWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        ShowActivated="False" ShowInTaskbar="False" Topmost="True"
        ResizeMode="NoResize" SizeToContent="WidthAndHeight" Focusable="False"
        IsHitTestVisible="False">
    <Border Style="{StaticResource CardStyle}" Padding="18,12" MinWidth="200">
        <TextBlock x:Name="OverlayText" Style="{StaticResource CardTitle}"
                   HorizontalAlignment="Center" TextAlignment="Center"/>
    </Border>
</Window>
```

`src/OpenAorus.App/Views/OverlayWindow.xaml.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace OpenAorus.App.Views;

/// <summary>
/// The small card the owner sees for the things Windows knows nothing about: fan mode, keyboard
/// backlight, the touchpad and the radio.
/// </summary>
/// <remarks>
/// <para>
/// Never for volume or display brightness. Windows draws those itself, and a second card on top
/// of the first is the complaint this whole release answers.
/// </para>
/// <para>
/// One window, reused. It is never activated and never takes hit-testing, so it cannot pull focus
/// out of whatever the owner is typing into and a click where it sits reaches what is behind it.
/// Three extended styles enforce that - <c>WS_EX_TRANSPARENT</c>, <c>WS_EX_NOACTIVATE</c>,
/// <c>WS_EX_TOOLWINDOW</c> - and none of the three can be exercised without a desktop.
/// </para>
/// </remarks>
public sealed partial class OverlayWindow : System.Windows.Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private readonly DispatcherTimer _hide;

    public OverlayWindow()
    {
        InitializeComponent();
        _hide = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);
    }

    /// <summary>Shows <paramref name="text"/> for <paramref name="seconds"/>, then hides.</summary>
    /// <remarks>A second call while one is up replaces the text and restarts the timer rather
    /// than stacking a second card - one keypress, one overlay at most.</remarks>
    public void ShowNotice(string text, int seconds)
    {
        OverlayText.Text = text;

        // Measured before placing: SizeToContent means the card's size is only known once the
        // new text is laid out, and placing it first would centre the previous size.
        UpdateLayout();

        var area = ScreenWorkArea();
        Left = OverlayPlacement.Left(area.Left, area.Width, ActualWidth);
        Top = OverlayPlacement.Top(area.Top, area.Height, ActualHeight);

        Show();

        _hide.Stop();
        _hide.Interval = TimeSpan.FromSeconds(seconds);
        _hide.Start();
    }

    /// <summary>
    /// The work area of the screen the mouse is on, in device-independent units.
    /// </summary>
    /// <remarks>
    /// Correct on a single-DPI desktop by construction. On a mixed-DPI one the transform comes
    /// from this window's own source, which is the scale of whichever screen it was last shown
    /// on - so the card can land off-centre the first time it appears on the other monitor. That
    /// is the one placement case that could not be reasoned out without hardware; VERIFY 8.8.
    /// </remarks>
    private System.Windows.Rect ScreenWorkArea()
    {
        var mouse = System.Windows.Forms.Control.MousePosition;
        var screen = System.Windows.Forms.Screen.FromPoint(mouse);
        var w = screen.WorkingArea;

        var source = PresentationSource.FromVisual(this);
        var m = source?.CompositionTarget?.TransformFromDevice;
        var scaleX = m?.M11 ?? 1.0;
        var scaleY = m?.M22 ?? 1.0;

        return new System.Windows.Rect(w.Left * scaleX, w.Top * scaleY, w.Width * scaleX, w.Height * scaleY);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
```

`OverlayText` is safe as an `x:Name`: there is no `OverlayText` type. Do **not** name anything
`ContextPanel`, `CaptionBar`, `LightingPanel`, `KeyboardCanvas`, `BatteryPanel` or `CurveEditor`.

- [ ] **Step 5: Own the overlay from the main window**

In `MainWindow.xaml.cs`, create one `OverlayWindow` lazily and subscribe:

```csharp
        vm.OverlayRequested += text =>
            Dispatcher.Invoke(() =>
            {
                _overlay ??= new OverlayWindow();
                _overlay.ShowNotice(text, App.Services.Settings.Hotkeys.OverlaySeconds);
            });
```

with `private OverlayWindow? _overlay;` beside the other fields, and closed in `OnClosing`'s
path only when the app is really exiting - the main window hides to tray rather than closing, so
disposing there would take the overlay with it.

`Dispatcher.Invoke` matters: raw input arrives on the thread that pumped the message, which is the
UI thread here, but the WMI events arrive on a `ManagementEventWatcher` thread pool thread.

- [ ] **Step 6: Add the Settings card**

`HotkeysViewModel` follows `SettingsViewModel`'s shape: `[ObservableProperty]` fields seeded from
`AppServices.Settings.Hotkeys`, a `[RelayCommand] Save()` that copies them back and calls
`Store.Save`. Expose it from `SettingsViewModel` as `public HotkeysViewModel Hotkeys { get; }`, or
flatten the five properties onto `SettingsViewModel` directly - whichever fits the card markup the
concurrent UI work leaves behind. The tests above bind against `HotkeysEnabled`,
`OverlayForFanMode`, `OverlayForBacklight`, `OverlayForTouchpad` and `OverlayForWifi`; keep those
names.

In `SettingsWindow.xaml`, add the card **after** the `StackPanel` that carries
`IsEnabled="{Binding CanWrite}"`, as a sibling inside the same `DockPanel`, so the gate cannot
reach it. A short explanatory `TextBlock` in the shape of the existing cards should say plainly
that the app never draws over Windows' own volume and brightness overlays.

- [ ] **Step 7: Run tests and build Release**

Run: `dotnet test OpenAorus.sln`
Expected: all pass, including `XamlResourceTests`, which now covers `OverlayWindow.xaml`
automatically through the test project's `Views/*.xaml` glob.
Run: `dotnet build OpenAorus.sln -c Release`
Expected: no warnings.

- [ ] **Step 8: `OWNER VERIFY` - record what could not be checked here**

The three extended styles, and the DPI transform on a mixed-DPI desktop. Both go to `VERIFY.md`
section 8 in Task 10.

- [ ] **Step 9: Commit**

```bash
git add src/OpenAorus.App/Views/OverlayPlacement.cs \
        src/OpenAorus.App/Views/OverlayWindow.xaml \
        src/OpenAorus.App/Views/OverlayWindow.xaml.cs \
        src/OpenAorus.App/Views/MainWindow.xaml.cs \
        src/OpenAorus.App/Views/SettingsWindow.xaml \
        src/OpenAorus.App/Views/SettingsWindow.xaml.cs \
        src/OpenAorus.App/ViewModels/HotkeysViewModel.cs \
        src/OpenAorus.App/ViewModels/SettingsViewModel.cs \
        tests/OpenAorus.Hardware.Tests/OverlayPlacementTests.cs \
        tests/OpenAorus.Hardware.Tests/HotkeySettingsUiTests.cs
git commit -m "feat(hotkeys): add the opt-in overlay and its settings card"
```

---

### Task 10: Documentation, and the hardware checklist

**Files:**
- Modify: `README.md`, `VERIFY.md`
- Read: every `OWNER VERIFY` note raised in Tasks 1, 3, 4, 7, 8 and 9; `docs/research/fn-hotkey-signals.md` (the three open questions)

Nothing in v0.3 has been observed on hardware. Both channels were recovered from Gigabyte's
software rather than seen on this chassis, so **v0.3 could ship and do nothing at all on the
owner's machine without a single test noticing**. This section is the only thing that would say so.

> **Numbering.** `VERIFY.md` currently runs to section 6, and the concurrent UI work is expected
> to land a section 7. Add this as **section 8**; if only sections 1-6 exist when you get here,
> number it 7 and say so in the commit message.

- [ ] **Step 1: README**

Add Fn hotkeys to the feature list. State plainly:

- what works without Gigabyte's software: fan-mode cycling, the keyboard backlight level
  following the firmware, and touchpad and Wi-Fi notices;
- that the overlay is **off by default** and opt-in per signal;
- that OpenAorus never draws for volume or display brightness, because Windows already does -
  and that this is the bug the release exists to fix;
- that raw input needs no administrator rights and the WMI event subscription does;
- that the app registers **three vendor collections** and never the standard keyboard, mouse or
  consumer-control pages, so it never sees ordinary typing;
- that none of it is confirmed on hardware and reports from other models are welcome.

- [ ] **Step 2: VERIFY.md section 8**

Add before the `## Assumptions this checklist is really testing` heading. Work the steps in this
order: the first one partitions every other unknown.

```markdown
## 8. Fn hotkeys and the overlay

Nothing in this section has ever been observed on hardware. Both channels were recovered from
Gigabyte's software rather than seen on this chassis, so it is entirely possible that neither
opens and that v0.3 does nothing at all here. That is a finding, not a failure - record it.

Work through 8.1 first: one line of output partitions every other unknown below.

### 8.1 Which channels open at all

- [ ] Start OpenAorus and press the fan-mode key. If the fan mode changes, the raw-input
      channel is open and its byte indexing is right
- [ ] Toggle the touchpad with its Fn key. If the app reports it (switch the touchpad
      overlay on in Settings first), the WMI channel is open **and** the event's `Data`
      property is named what the research says it is
- [ ] If neither happens, stop here and record it. Nothing else in this section is worth
      attempting, and the finding is that this chassis needs a different approach

### 8.2 Press every Fn combination and write down which do nothing

- [ ] Go along the whole Fn row and note, for each key, whether OpenAorus reacted
- [ ] **Every key dead** means one wrong assumption, and it is probably the byte indexing:
      the decoder reads `bRawData1` as `report[0]`, and the other reading shifts every
      pattern by one byte
- [ ] **Most working and two dead** is a research gap, not an indexing error. Record which
      two
- [ ] Note in particular whether the Fn key alone is visible, or only the combinations. The
      research could not say

### 8.3 The bug this release exists to fix

- [ ] With **every** overlay switched on in Settings, press volume up, volume down and mute.
      Exactly one overlay must appear - Windows' own - and the volume must actually change
- [ ] Repeat with Gigabyte Control Center taken over
- [ ] Do the same with the display-brightness keys. Again: exactly one overlay, Windows' own
- [ ] Everything else in this section is a feature. This is the bug

### 8.4 One keypress, one action

- [ ] Press the fan-mode key rapidly five times: five mode changes, not ten and not two.
      More means the de-duplication window is too short, fewer means too long or the key
      repeats. Each apply is a seven-step sequence paced at 500 ms, so a burst of five takes
      about fifteen seconds to settle - what is being counted is the changes, not the speed
- [ ] Hold the fan-mode key down. It must not queue one mode change per repeat

### 8.5 Whether the firmware runs its own fan rotation underneath

- [ ] Note the mode OpenAorus shows, press the fan key once, and note it again. Then press
      it three more times and check the app has walked Quiet, Normal, Gaming, Turbo in order
- [ ] If the fans audibly do something the app did not ask for, or the app's mode and the
      machine's behaviour diverge, the firmware is running its own rotation and the app
      should honour the mode each code names instead of cycling. `HotkeyPolicy.NamedMode`
      already holds that table
- [ ] This is the one observation that could change a design decision rather than a constant

### 8.6 The keyboard backlight

- [ ] Cycle the backlight key through all its steps and check the lighting panel's
      brightness slider follows: 0 %, 50 %, 100 %
- [ ] A step the slider does not follow means this keyboard has more than the three levels
      the research documents. Record the step that was missed - the app deliberately refuses
      to guess a scale rather than inventing one

### 8.7 The overlay behaves like a notification, not a window

- [ ] Press a serviced key while typing in another program: the overlay appears, the caret
      stays put, and nothing typed is lost
- [ ] Click where the overlay is drawn while it is up: the click reaches what is behind it
- [ ] Press a serviced key while a full-screen game is running: the overlay appears over it
      or not at all. What must not happen is the game losing focus or minimising

### 8.8 Placement

- [ ] On a single monitor, the card centres at the bottom of the screen
- [ ] With a second monitor attached, it centres at the bottom of the screen the mouse is on
- [ ] With the two monitors at **different** scaling factors, check it again on each. This is
      the one placement case that could not be reasoned out without hardware: the scale comes
      from the window's own source, which is whichever screen it was last shown on

### 8.9 It must never react to anything that is not an Fn key

- [ ] Type normally in another program for a minute with every overlay switched on. If
      OpenAorus reacts to anything at all, stop and report it
- [ ] It should be impossible: the app registers three vendor collections and never the
      keyboard, mouse or consumer-control pages. A reaction would mean a decoder matching far
      too loosely, and it is the one failure in this section that is worth stopping for

### 8.10 The questions the research could not answer

- [ ] Is the Fn key itself observable, or only the resulting combinations? (8.2)
- [ ] Does this chassis emit the fan codes 37, 38 and 39 at all? (8.1)
- [ ] What is the 9-byte **output** report on collection `0xFF00/0xFF00` for? Gigabyte writes
      to it and the decompiled path does not explain it. OpenAorus never writes to it. If
      something about the keyboard behaves differently under GCC than under OpenAorus, this
      is the first place to look
```

- [ ] **Step 3: Add the assumptions to the existing list**

Append to `## Assumptions this checklist is really testing`, in the voice of the entries already
there:

```markdown
- **That either hotkey channel exists on this chassis.** Both were recovered from Gigabyte's
  software and neither has been observed here. v0.3 could ship and do nothing at all without
  a single test noticing; section 8.1 is the only thing that would say so.

- **That `bRawData1..4` means indices 0..3.** The 4-byte and 9-byte report tables only agree
  with each other under that reading, but it is a reading of decompiled field names. If it is
  wrong, every pattern shifts by one byte and every Fn key does nothing - which is exactly
  what section 8.2 asks you to look for.

- **That the fan-mode key should cycle.** The wire codes 37, 38 and 39 name Gigabyte's own
  firmware modes, and three distinct codes describe a state rather than a "next" edge. The
  app cycles anyway, because it has taken the mode machine over and there is no code for
  Turbo at all. Only hardware can say whether the firmware runs its own rotation underneath,
  in which case honouring the named mode is better and `HotkeyPolicy.NamedMode` is the table
  to switch to. Section 8.5.

- **That the backlight has exactly three levels** - 0, 25 and 50 on the wire, meaning 0 %,
  50 % and 100 %. Doubling the byte would invent a scale nobody measured, so an undocumented
  value is reported as not understood rather than guessed at. A four-step keyboard shows up in
  section 8.6 as a level the slider does not follow.

- **That 250 ms is the right de-duplication window.** Chosen to sit above the gap between two
  channels describing one press and below a deliberate double tap. Not measured. On the
  documented data the two channels' vocabularies do not even overlap, so the window is really
  insurance against key auto-repeat rather than against the cross-channel echo the design
  names. Section 8.4.

- **That the three extended window styles are enough** to make the overlay click-through and
  focus-proof: `WS_EX_TRANSPARENT`, `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW`. None of the
  three is exercisable without a desktop. Section 8.7.

- **That the DPI transform is the right one.** Correct on a single-DPI setup by construction,
  and taken from the wrong monitor on a mixed-DPI one. Section 8.8.

- **That the WMI event's payload is a `Data` property that converts to an integer.** The
  research names it and nothing has checked it. A wrong name is a subscription that runs and
  reports nothing, silently, which is why section 8.1 asks for it directly.

- **That cutting Gigabyte's five raw-input usages to three loses nothing.** The two dropped
  are the standard keyboard and mouse pages, which under `RIDEV_INPUTSINK` would deliver every
  keystroke typed anywhere on the machine into this elevated process for no purpose. Every
  documented signal arrives on a vendor collection. If section 8.2 finds a key that does
  nothing and section 8.9 finds nothing spurious, the missing signal may be one that only
  arrives on the keyboard page - that is the case for adding `0x0001/0x0006` back, and the
  only one.
```

- [ ] **Step 4: Check every OWNER VERIFY item has a home**

Walk Tasks 1, 3, 4, 7, 8 and 9 and confirm each item appears above:
`RegisterRawInputDevices` succeeding (8.1), the message-only window receiving `WM_INPUT` (8.1,
8.2), the `ManagementEventWatcher` delivering (8.1), the `Data` property name (8.1, assumptions),
byte indexing (8.2), the three backlight levels (8.6), cycling versus the named mode (8.5), the
250 ms window (8.4), the extended window styles (8.7), the DPI transform (8.8), exactly one
volume overlay (8.3), never reacting to ordinary typing (8.9), and the three research silences
(8.10).

- [ ] **Step 5: Bump the version**

`src/OpenAorus.App/OpenAorus.App.csproj`: `<Version>0.3.0</Version>`.

- [ ] **Step 6: Run tests and build Release**

Run: `dotnet test OpenAorus.sln`
Run: `dotnet build OpenAorus.sln -c Release`
Expected: all pass, no warnings at either configuration.

- [ ] **Step 7: Commit**

```bash
git add README.md VERIFY.md src/OpenAorus.App/OpenAorus.App.csproj
git commit -m "docs: document the Fn hotkeys and the guesses hardware has to settle"
```

---

## Owner acceptance checklist (end of v0.3)

- [ ] Volume up, down and mute show exactly one overlay - Windows' own - with every OpenAorus
      overlay switched on
- [ ] Display brightness shows exactly one overlay, Windows' own
- [ ] The fan-mode key changes the fan mode, and the tray tooltip and the window agree with it
- [ ] The backlight key moves the lighting panel's brightness slider to match the keyboard
- [ ] The touchpad and Wi-Fi keys are reported if, and only if, their overlay is switched on
- [ ] With every overlay off - the shipped default - nothing is ever drawn, and the fan and
      backlight keys still work
- [ ] The overlay never steals focus, never blocks a click, and never minimises a game
- [ ] OpenAorus reacts to nothing that is not an Fn key
- [ ] The hotkey settings still work on a model the app cannot write fan modes to
- [ ] Nothing in the fan, sensor, battery or lighting behaviour regressed

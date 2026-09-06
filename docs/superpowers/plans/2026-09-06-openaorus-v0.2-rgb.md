# OpenAorus v0.2 Keyboard RGB Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Control the AORUS per-key keyboard ("Fusion RGB KB", Ione family) from OpenAorus: all 18 built-in effects with real parameters, per-key custom colours, brightness, presets, and persistence across restart and resume.

**Architecture:** The whole wire protocol lives in pure byte-array builders (`EffectPacket`, `PerKeyPacket`, `KeyLayout`) with no IO, so it is unit-testable byte for byte. `IKeyboardHid` is the single seam to `hid.dll`/`setupapi.dll`, mirroring how `IGigabyteWmi` isolates WMI in v0.1. `LightingController` sequences writes through a paced queue. The app grows a Lighting section that hides itself when no supported keyboard is present.

**Tech Stack:** .NET 8 (`net8.0-windows`), C# 12, WPF, `CommunityToolkit.Mvvm`, P/Invoke to `hid.dll` and `setupapi.dll`, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-06-openaorus-v0.2-rgb-design.md`
**Protocol reference:** `docs/research/ione-keyboard-protocol.md`

## Global Constraints

- Target framework `net8.0-windows`; `Nullable` and `ImplicitUsings` enabled.
- Every keyboard exchange is a **264-byte HID feature report**, report id `0x07` in byte 0, command in byte 1.
- Only `src/OpenAorus.Hardware/Lighting/KeyboardHid.cs` may call `hid.dll` or `setupapi.dll`. Everything else goes through `IKeyboardHid`.
- Consecutive feature reports are paced **65 ms** apart (matches Gigabyte's own software; faster writes are dropped).
- Lighting needs **no elevation**. Never add an elevation requirement to this path.
- Accept `VID 0x1044` or `0x0414`, `PID` in `{0x7A3C, 0x7A3D, 0x7A3F}`, and select the collection by `UsagePage 0xFF01` + `Usage 0x0001` + `FeatureReportByteLength == 264` — never by parsing the device path.
- Speed on the wire is inverted: `wire = 10 - round(ui / 10)` for a 0–100 UI value.
- Commit messages must not contain a `Co-Authored-By` trailer or name any AI tool.
- Run tests with `dotnet test OpenAorus.sln`; build with `dotnet build OpenAorus.sln -c Release`.
- Steps marked `OWNER VERIFY` need the physical laptop. Never claim lighting behaviour is confirmed without it; collect them into `VERIFY.md`.

## File Structure

```
src/OpenAorus.Hardware/Lighting/IKeyboardHid.cs        seam: SetFeature / GetFeature / IsPresent
src/OpenAorus.Hardware/Lighting/KeyboardHid.cs         SetupAPI + HidD_* discovery and IO
src/OpenAorus.Hardware/Lighting/LightEffect.cs         enum, 0x00..0x12
src/OpenAorus.Hardware/Lighting/EffectParameters.cs    colour(s), speed, direction, random, brightness
src/OpenAorus.Hardware/Lighting/EffectPacket.cs        pure: parameters -> 264 bytes (+ offset table)
src/OpenAorus.Hardware/Lighting/KeyLayout.cs           128-slot maps, ENG-US and ENG-UK
src/OpenAorus.Hardware/Lighting/PerKeyPacket.cs        pure: 128 colours <-> two 264-byte reports
src/OpenAorus.Hardware/Lighting/LightingController.cs  apply effect / per-key / read back, paced queue
src/OpenAorus.Hardware/Config/AppSettings.cs           (modified) lighting fields + presets
src/OpenAorus.App/ViewModels/LightingViewModel.cs      effect list, parameters, presets
src/OpenAorus.App/ViewModels/PerKeyEditorViewModel.cs  paint model over the 128 slots
src/OpenAorus.App/Views/LightingPanel.xaml(.cs)        effect picker + parameter controls
src/OpenAorus.App/Views/KeyboardCanvas.xaml(.cs)       keyboard-shaped per-key editor
src/OpenAorus.App/Views/MainWindow.xaml(.cs)           (modified) Cooling | Lighting switch
tests/OpenAorus.Hardware.Tests/FakeKeyboardHid.cs      records reports, scripted responses
tests/OpenAorus.Hardware.Tests/EffectPacketTests.cs
tests/OpenAorus.Hardware.Tests/PerKeyPacketTests.cs
tests/OpenAorus.Hardware.Tests/KeyLayoutTests.cs
tests/OpenAorus.Hardware.Tests/LightingControllerTests.cs
```

---

### Task 1: Keyboard HID seam, fake, and real device discovery

**Files:**
- Create: `src/OpenAorus.Hardware/Lighting/IKeyboardHid.cs`, `src/OpenAorus.Hardware/Lighting/KeyboardHid.cs`
- Test: `tests/OpenAorus.Hardware.Tests/FakeKeyboardHid.cs`, `tests/OpenAorus.Hardware.Tests/KeyboardHidTests.cs`

**Interfaces:**
- Produces:
  - `sealed record KeyboardIdentity(ushort Vid, ushort Pid, string Path, string Product)`
  - `interface IKeyboardHid : IDisposable { bool IsPresent { get; } KeyboardIdentity? Identity { get; } bool SetFeature(byte[] report); byte[]? GetFeature(); }`
  - `const int KeyboardHid.ReportLength = 264`, `const byte KeyboardHid.ReportId = 0x07`
  - `static IReadOnlyList<ushort> KeyboardHid.SupportedVids` = `{0x1044, 0x0414}`, `SupportedPids` = `{0x7A3C, 0x7A3D, 0x7A3F}`
  - `sealed class KeyboardHid : IKeyboardHid` with `static KeyboardHid Open()`
  - test double `FakeKeyboardHid` with `List<byte[]> Written`, `Queue<byte[]> Responses`, `bool FailNextWrite`

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/FakeKeyboardHid.cs`:

```csharp
using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

public sealed class FakeKeyboardHid : IKeyboardHid
{
    public List<byte[]> Written { get; } = new();
    public Queue<byte[]> Responses { get; } = new();
    public bool FailNextWrite { get; set; }
    public bool IsPresent { get; set; } = true;
    public KeyboardIdentity? Identity { get; set; } =
        new(0x1044, 0x7A3D, @"\\?\hid#vid_1044&pid_7a3d&mi_02&col06#fake", "Fusion RGB KB");

    public bool SetFeature(byte[] report)
    {
        Written.Add((byte[])report.Clone());
        if (!FailNextWrite) return true;
        FailNextWrite = false;
        return false;
    }

    public byte[]? GetFeature() => Responses.Count > 0 ? Responses.Dequeue() : null;

    public void Dispose() { }

    /// <summary>The command byte of the Nth report written.</summary>
    public byte Command(int index) => Written[index][1];
}
```

`tests/OpenAorus.Hardware.Tests/KeyboardHidTests.cs`:

```csharp
using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

public class KeyboardHidTests
{
    [Fact]
    public void Report_constants_match_the_protocol()
    {
        Assert.Equal(264, KeyboardHid.ReportLength);
        Assert.Equal(0x07, KeyboardHid.ReportId);
    }

    [Fact]
    public void Supported_ids_cover_both_vendor_ids_and_the_three_ione_pids()
    {
        Assert.Contains((ushort)0x1044, KeyboardHid.SupportedVids);
        Assert.Contains((ushort)0x0414, KeyboardHid.SupportedVids);
        Assert.Equal(new ushort[] { 0x7A3C, 0x7A3D, 0x7A3F }, KeyboardHid.SupportedPids);
    }

    [Fact]
    public void Fake_records_written_reports_and_reports_their_command()
    {
        var hid = new FakeKeyboardHid();
        var report = new byte[264];
        report[0] = 0x07;
        report[1] = 0x02;
        Assert.True(hid.SetFeature(report));
        Assert.Single(hid.Written);
        Assert.Equal(0x02, hid.Command(0));
    }

    [Fact]
    public void Fake_write_failure_is_one_shot()
    {
        var hid = new FakeKeyboardHid { FailNextWrite = true };
        Assert.False(hid.SetFeature(new byte[264]));
        Assert.True(hid.SetFeature(new byte[264]));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~KeyboardHidTests`
Expected: compile error, `IKeyboardHid` not found.

- [ ] **Step 3: Implement the seam**

`src/OpenAorus.Hardware/Lighting/IKeyboardHid.cs`:

```csharp
namespace OpenAorus.Hardware.Lighting;

public sealed record KeyboardIdentity(ushort Vid, ushort Pid, string Path, string Product);

/// <summary>
/// The only door to the keyboard's lighting collection. Real implementation:
/// <see cref="KeyboardHid"/>. Unlike the fan path this needs no elevation.
/// </summary>
public interface IKeyboardHid : IDisposable
{
    bool IsPresent { get; }
    KeyboardIdentity? Identity { get; }
    bool SetFeature(byte[] report);
    byte[]? GetFeature();
}
```

- [ ] **Step 4: Implement discovery and IO**

`src/OpenAorus.Hardware/Lighting/KeyboardHid.cs`:

```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenAorus.Hardware.Lighting;

/// <summary>
/// Finds the Ione lighting collection and exchanges 264-byte feature reports with it.
/// The collection is identified by its HID capabilities (usage page 0xFF01, usage 1,
/// feature length 264), never by parsing the device path: the same PID exposes about
/// ten collections and only one of them is the lighting endpoint.
/// </summary>
public sealed class KeyboardHid : IKeyboardHid
{
    public const int ReportLength = 264;
    public const byte ReportId = 0x07;

    public static IReadOnlyList<ushort> SupportedVids { get; } = new ushort[] { 0x1044, 0x0414 };
    public static IReadOnlyList<ushort> SupportedPids { get; } = new ushort[] { 0x7A3C, 0x7A3D, 0x7A3F };

    private const ushort LightingUsagePage = 0xFF01;
    private const ushort LightingUsage = 0x0001;

    private readonly SafeFileHandle? _handle;

    public bool IsPresent => _handle is { IsInvalid: false };
    public KeyboardIdentity? Identity { get; }

    private KeyboardHid(SafeFileHandle? handle, KeyboardIdentity? identity)
    {
        _handle = handle;
        Identity = identity;
    }

    public static KeyboardHid Open()
    {
        foreach (var path in EnumerateHidPaths())
        {
            SafeFileHandle handle = CreateFile(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); continue; }

            var attrs = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
            if (!HidD_GetAttributes(handle, ref attrs)
                || !SupportedVids.Contains(attrs.VendorId)
                || !SupportedPids.Contains(attrs.ProductId))
            {
                handle.Dispose();
                continue;
            }

            if (!HidD_GetPreparsedData(handle, out var preparsed)) { handle.Dispose(); continue; }
            try
            {
                if (HidP_GetCaps(preparsed, out var caps) != HidpStatusSuccess
                    || caps.UsagePage != LightingUsagePage
                    || caps.Usage != LightingUsage
                    || caps.FeatureReportByteLength != ReportLength)
                {
                    handle.Dispose();
                    continue;
                }
            }
            finally { HidD_FreePreparsedData(preparsed); }

            return new KeyboardHid(handle, new KeyboardIdentity(attrs.VendorId, attrs.ProductId, path, ReadProduct(handle)));
        }
        return new KeyboardHid(null, null);
    }

    public bool SetFeature(byte[] report)
    {
        if (_handle is null || _handle.IsInvalid) return false;
        if (report.Length != ReportLength) throw new ArgumentException($"Report must be {ReportLength} bytes.", nameof(report));
        return HidD_SetFeature(_handle, report, (uint)report.Length);
    }

    public byte[]? GetFeature()
    {
        if (_handle is null || _handle.IsInvalid) return null;
        var buffer = new byte[ReportLength];
        buffer[0] = ReportId;
        return HidD_GetFeature(_handle, buffer, (uint)buffer.Length) ? buffer : null;
    }

    public void Dispose() => _handle?.Dispose();

    private static string ReadProduct(SafeFileHandle handle)
    {
        var buffer = new byte[256];
        return HidD_GetProductString(handle, buffer, (uint)buffer.Length)
            ? System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0')
            : string.Empty;
    }

    private static IEnumerable<string> EnumerateHidPaths()
    {
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) yield break;
        try
        {
            var data = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0) continue;
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringAuto(buffer + 4);
                    if (!string.IsNullOrEmpty(path)) yield return path;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private const int FileShareReadWrite = 0x3;
    private const int OpenExisting = 3;
    private const int DigcfPresent = 0x2;
    private const int DigcfDeviceInterface = 0x10;
    private const int HidpStatusSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes { public int Size; public ushort VendorId; public ushort ProductId; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData { public int CbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HiddAttributes attributes);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsed);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsed, out HidpCaps caps);
    [DllImport("hid.dll")] private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] buffer, uint length);
    [DllImport("hid.dll")] private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] buffer, uint length);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] private static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] buffer, uint length);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll")] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid guid, uint index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, uint detailSize, out uint required, IntPtr deviceInfo);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, int access, int share, IntPtr security, int disposition, int flags, IntPtr template);
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass, including the earlier v0.1 suites.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add keyboard HID seam and Ione lighting-collection discovery"
```

---

### Task 2: Effect model and packet builder

**Files:**
- Create: `src/OpenAorus.Hardware/Lighting/LightEffect.cs`, `src/OpenAorus.Hardware/Lighting/EffectParameters.cs`, `src/OpenAorus.Hardware/Lighting/EffectPacket.cs`
- Test: `tests/OpenAorus.Hardware.Tests/EffectPacketTests.cs`

**Interfaces:**
- Consumes: `KeyboardHid.ReportLength`, `KeyboardHid.ReportId`
- Produces:
  - `enum LightEffect : byte` — `Static=0x00 … Crash=0x11, Custom=0x12`
  - `readonly record struct RgbColor(byte R, byte G, byte B)` with `static RgbColor White/Black`
  - `enum LightDirection { Right, Left, Up, Down, Clockwise, CounterClockwise }`
  - `sealed record EffectParameters(LightEffect Effect, RgbColor Color, RgbColor SecondColor, int SpeedPercent, int BrightnessPercent, LightDirection Direction, bool Random)` with `static EffectParameters Default(LightEffect)`
  - `static class EffectPacket` with `byte[] Build(EffectParameters p)`, `IReadOnlyDictionary<LightEffect,int> Offsets`, `int EncodeSpeed(int uiPercent)`, `bool SupportsColor/SupportsSecondColor/SupportsSpeed/SupportsDirection/SupportsRandom(LightEffect)`

Protocol recap (authoritative copy in `docs/research/ione-keyboard-protocol.md`): byte 0 = `0x07`, byte 1 = `0x02`, bytes 2..9 zero, byte 10 = effect id, byte 11 = `0xFF` for Static and StarShining else 0, byte 12 = brightness, then the effect's configuration bytes at `13 + Offsets[effect]`.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/EffectPacketTests.cs`:

```csharp
using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

public class EffectPacketTests
{
    private static byte[] Build(LightEffect effect) => EffectPacket.Build(EffectParameters.Default(effect));

    [Fact]
    public void Every_packet_is_264_bytes_with_the_report_id_and_set_mode_command()
    {
        foreach (var effect in Enum.GetValues<LightEffect>())
        {
            var packet = Build(effect);
            Assert.Equal(264, packet.Length);
            Assert.Equal(0x07, packet[0]);
            Assert.Equal(0x02, packet[1]);
            Assert.Equal((byte)effect, packet[10]);
        }
    }

    [Fact]
    public void Offset_table_matches_the_documented_protocol()
    {
        var expected = new Dictionary<LightEffect, int>
        {
            [LightEffect.Static] = 0, [LightEffect.Breathing] = 4, [LightEffect.Flow] = 9,
            [LightEffect.Firework] = 11, [LightEffect.Ripple] = 16, [LightEffect.Rain] = 21,
            [LightEffect.Cycling] = 26, [LightEffect.Trigger] = 27, [LightEffect.Pulse] = 32,
            [LightEffect.Radar] = 37, [LightEffect.StarShining] = 43, [LightEffect.Wave] = 48,
            [LightEffect.Cross] = 54, [LightEffect.Dragonstrike] = 59, [LightEffect.Bloom] = 68,
            [LightEffect.Spiral] = 76, [LightEffect.Merge] = 78, [LightEffect.Crash] = 86,
            [LightEffect.Custom] = 0,
        };
        Assert.Equal(expected.Count, EffectPacket.Offsets.Count);
        foreach (var (effect, offset) in expected)
            Assert.Equal(offset, EffectPacket.Offsets[effect]);
    }

    [Fact]
    public void Static_writes_marker_brightness_and_colour_at_the_documented_offsets()
    {
        var p = EffectParameters.Default(LightEffect.Static) with
        {
            Color = new RgbColor(0x11, 0x22, 0x33),
            BrightnessPercent = 60,
        };
        var packet = EffectPacket.Build(p);
        Assert.Equal(0xFF, packet[11]);
        Assert.Equal(60, packet[12]);
        Assert.Equal(0x00, packet[13]);
        Assert.Equal(0x11, packet[14]);
        Assert.Equal(0x22, packet[15]);
        Assert.Equal(0x33, packet[16]);
    }

    [Fact]
    public void Wave_writes_speed_random_direction_and_colour_at_its_own_offset()
    {
        var p = EffectParameters.Default(LightEffect.Wave) with
        {
            Color = new RgbColor(1, 2, 3),
            SpeedPercent = 100,
            Random = true,
            Direction = LightDirection.Left,
        };
        var packet = EffectPacket.Build(p);
        var at = 13 + EffectPacket.Offsets[LightEffect.Wave];
        Assert.Equal(0, packet[at]);            // speed 100 % -> wire 0 (fastest)
        Assert.Equal(1, packet[at + 1]);        // random
        Assert.Equal(1, packet[at + 2]);        // direction Left
        Assert.Equal(1, packet[at + 3]);
        Assert.Equal(2, packet[at + 4]);
        Assert.Equal(3, packet[at + 5]);
    }

    [Fact]
    public void Cycling_writes_only_a_speed_byte()
    {
        var p = EffectParameters.Default(LightEffect.Cycling) with { SpeedPercent = 50 };
        var packet = EffectPacket.Build(p);
        var at = 13 + EffectPacket.Offsets[LightEffect.Cycling];
        Assert.Equal(5, packet[at]);
        Assert.Equal(0, packet[at + 1]);
    }

    [Fact]
    public void Two_colour_effects_write_both_colours()
    {
        var p = EffectParameters.Default(LightEffect.Dragonstrike) with
        {
            Color = new RgbColor(0xAA, 0xBB, 0xCC),
            SecondColor = new RgbColor(0xDD, 0xEE, 0xFF),
        };
        var packet = EffectPacket.Build(p);
        var at = 13 + EffectPacket.Offsets[LightEffect.Dragonstrike];
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, packet[(at + 3)..(at + 6)]);
        Assert.Equal(new byte[] { 0xDD, 0xEE, 0xFF }, packet[(at + 6)..(at + 9)]);
    }

    [Fact]
    public void Custom_writes_no_configuration_bytes_beyond_brightness()
    {
        var packet = EffectPacket.Build(EffectParameters.Default(LightEffect.Custom) with { BrightnessPercent = 40 });
        Assert.Equal(0x12, packet[10]);
        Assert.Equal(40, packet[12]);
        Assert.All(packet[13..], b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(50, 5)]
    [InlineData(100, 0)]
    [InlineData(150, 0)]
    [InlineData(-10, 10)]
    public void Speed_is_inverted_and_clamped_on_the_wire(int ui, int wire)
        => Assert.Equal(wire, EffectPacket.EncodeSpeed(ui));

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(140, 100)]
    public void Brightness_is_clamped_to_0_100(int ui, int expected)
    {
        var packet = EffectPacket.Build(EffectParameters.Default(LightEffect.Static) with { BrightnessPercent = ui });
        Assert.Equal(expected, packet[12]);
    }

    [Fact]
    public void Capability_helpers_describe_each_effect_family()
    {
        Assert.True(EffectPacket.SupportsColor(LightEffect.Static));
        Assert.False(EffectPacket.SupportsSpeed(LightEffect.Static));
        Assert.False(EffectPacket.SupportsColor(LightEffect.Cycling));
        Assert.True(EffectPacket.SupportsSpeed(LightEffect.Cycling));
        Assert.True(EffectPacket.SupportsDirection(LightEffect.Flow));
        Assert.True(EffectPacket.SupportsSecondColor(LightEffect.Merge));
        Assert.False(EffectPacket.SupportsSecondColor(LightEffect.Wave));
        Assert.True(EffectPacket.SupportsRandom(LightEffect.Firework));
        Assert.False(EffectPacket.SupportsColor(LightEffect.Custom));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~EffectPacketTests`
Expected: compile error, `LightEffect` not found.

- [ ] **Step 3: Implement the model**

`src/OpenAorus.Hardware/Lighting/LightEffect.cs`:

```csharp
namespace OpenAorus.Hardware.Lighting;

/// <summary>Effect ids as written to byte 10 of the 0x02 report.</summary>
public enum LightEffect : byte
{
    Static = 0x00,
    Breathing = 0x01,
    Flow = 0x02,
    Firework = 0x03,
    Ripple = 0x04,
    Rain = 0x05,
    Cycling = 0x06,
    Trigger = 0x07,
    Pulse = 0x08,
    Radar = 0x09,
    StarShining = 0x0A,
    Wave = 0x0B,
    Cross = 0x0C,
    Dragonstrike = 0x0D,
    Bloom = 0x0E,
    Spiral = 0x0F,
    Merge = 0x10,
    Crash = 0x11,
    Custom = 0x12,
}
```

`src/OpenAorus.Hardware/Lighting/EffectParameters.cs`:

```csharp
namespace OpenAorus.Hardware.Lighting;

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor White => new(0xFF, 0xFF, 0xFF);
    public static RgbColor Black => new(0x00, 0x00, 0x00);
}

public enum LightDirection { Right, Left, Up, Down, Clockwise, CounterClockwise }

public sealed record EffectParameters(
    LightEffect Effect,
    RgbColor Color,
    RgbColor SecondColor,
    int SpeedPercent,
    int BrightnessPercent,
    LightDirection Direction,
    bool Random)
{
    public static EffectParameters Default(LightEffect effect) => new(
        Effect: effect,
        Color: RgbColor.White,
        SecondColor: new RgbColor(0x00, 0x00, 0xFF),
        SpeedPercent: 50,
        BrightnessPercent: 50,
        Direction: LightDirection.Right,
        Random: false);
}
```

- [ ] **Step 4: Implement the packet builder**

`src/OpenAorus.Hardware/Lighting/EffectPacket.cs`:

```csharp
namespace OpenAorus.Hardware.Lighting;

/// <summary>
/// Builds the 264-byte "set effect" feature report (command 0x02).
///
/// Layout: [0]=0x07 report id, [1]=0x02 command, [2..9]=0, [10]=effect id,
/// [11]=0xFF for Static and StarShining else 0, [12]=brightness,
/// [13 + Offsets[effect] ...]=the effect's own configuration bytes.
/// Each effect owns a private slice, so changing one leaves the others intact.
///
/// Protocol reconstructed from Gigabyte's own binaries and cross-checked against
/// rcassani/keyboard-fusion-rgb (GPL-3.0). See docs/research/ione-keyboard-protocol.md.
/// </summary>
public static class EffectPacket
{
    public static IReadOnlyDictionary<LightEffect, int> Offsets { get; } = new Dictionary<LightEffect, int>
    {
        [LightEffect.Static] = 0,
        [LightEffect.Breathing] = 4,
        [LightEffect.Flow] = 9,
        [LightEffect.Firework] = 11,
        [LightEffect.Ripple] = 16,
        [LightEffect.Rain] = 21,
        [LightEffect.Cycling] = 26,
        [LightEffect.Trigger] = 27,
        [LightEffect.Pulse] = 32,
        [LightEffect.Radar] = 37,
        [LightEffect.StarShining] = 43,
        [LightEffect.Wave] = 48,
        [LightEffect.Cross] = 54,
        [LightEffect.Dragonstrike] = 59,
        [LightEffect.Bloom] = 68,
        [LightEffect.Spiral] = 76,
        [LightEffect.Merge] = 78,
        [LightEffect.Crash] = 86,
        [LightEffect.Custom] = 0,
    };

    private static readonly HashSet<LightEffect> NoColor = new()
    {
        LightEffect.Flow, LightEffect.Cycling, LightEffect.Spiral, LightEffect.Custom,
    };

    private static readonly HashSet<LightEffect> TwoColor = new()
    {
        LightEffect.Dragonstrike, LightEffect.Bloom, LightEffect.Merge, LightEffect.Crash,
    };

    private static readonly HashSet<LightEffect> NoSpeed = new()
    {
        LightEffect.Static, LightEffect.Custom,
    };

    private static readonly HashSet<LightEffect> WithDirection = new()
    {
        LightEffect.Flow, LightEffect.Wave, LightEffect.Radar, LightEffect.Spiral,
        LightEffect.Dragonstrike, LightEffect.Crash,
    };

    private static readonly HashSet<LightEffect> WithRandom = new()
    {
        LightEffect.Firework, LightEffect.Rain, LightEffect.Trigger, LightEffect.Pulse,
        LightEffect.Radar, LightEffect.StarShining, LightEffect.Wave, LightEffect.Cross,
        LightEffect.Dragonstrike, LightEffect.Bloom, LightEffect.Merge, LightEffect.Crash,
    };

    public static bool SupportsColor(LightEffect e) => !NoColor.Contains(e);
    public static bool SupportsSecondColor(LightEffect e) => TwoColor.Contains(e);
    public static bool SupportsSpeed(LightEffect e) => !NoSpeed.Contains(e);
    public static bool SupportsDirection(LightEffect e) => WithDirection.Contains(e);
    public static bool SupportsRandom(LightEffect e) => WithRandom.Contains(e);

    /// <summary>UI 0-100 (slow to fast) becomes wire 10-0. The keyboard treats 0 as fastest.</summary>
    public static int EncodeSpeed(int uiPercent) =>
        10 - (int)Math.Round(Math.Clamp(uiPercent, 0, 100) / 10.0, MidpointRounding.AwayFromZero);

    public static byte EncodeDirection(LightEffect effect, LightDirection direction) => direction switch
    {
        LightDirection.Right => 0,
        LightDirection.Left => 1,
        // Gigabyte's own code swaps 2 and 3 for Flow.
        LightDirection.Up => effect == LightEffect.Flow ? (byte)3 : (byte)2,
        LightDirection.Down => effect == LightEffect.Flow ? (byte)2 : (byte)3,
        LightDirection.Clockwise => 0,
        LightDirection.CounterClockwise => 1,
        _ => 0,
    };

    public static byte[] Build(EffectParameters p)
    {
        var packet = new byte[KeyboardHid.ReportLength];
        packet[0] = KeyboardHid.ReportId;
        packet[1] = 0x02;
        packet[10] = (byte)p.Effect;
        packet[11] = p.Effect is LightEffect.Static or LightEffect.StarShining ? (byte)0xFF : (byte)0x00;
        packet[12] = (byte)Math.Clamp(p.BrightnessPercent, 0, 100);

        var at = 13 + Offsets[p.Effect];
        var speed = (byte)EncodeSpeed(p.SpeedPercent);
        var random = p.Random ? (byte)1 : (byte)0;
        var direction = EncodeDirection(p.Effect, p.Direction);

        switch (p.Effect)
        {
            case LightEffect.Custom:
                break;

            case LightEffect.Static:
                packet[at] = 0x00;
                WriteColor(packet, at + 1, p.Color);
                break;

            case LightEffect.Cycling:
                packet[at] = speed;
                break;

            case LightEffect.Flow:
            case LightEffect.Spiral:
                packet[at] = speed;
                packet[at + 1] = direction;
                break;

            case LightEffect.Wave:
                packet[at] = speed;
                packet[at + 1] = random;
                packet[at + 2] = direction;
                WriteColor(packet, at + 3, p.Color);
                break;

            case LightEffect.Dragonstrike:
            case LightEffect.Crash:
                packet[at] = speed;
                packet[at + 1] = random;
                packet[at + 2] = direction;
                WriteColor(packet, at + 3, p.Color);
                WriteColor(packet, at + 6, p.SecondColor);
                break;

            case LightEffect.Bloom:
            case LightEffect.Merge:
                packet[at] = speed;
                packet[at + 1] = random;
                packet[at + 2] = 0x00;
                WriteColor(packet, at + 3, p.Color);
                WriteColor(packet, at + 6, p.SecondColor);
                break;

            default:
                // Breathing, Firework, Ripple, Rain, Trigger, Pulse, Radar, StarShining, Cross
                packet[at] = speed;
                packet[at + 1] = random;
                WriteColor(packet, at + 2, p.Color);
                break;
        }

        return packet;
    }

    private static void WriteColor(byte[] packet, int at, RgbColor c)
    {
        packet[at] = c.R;
        packet[at + 1] = c.G;
        packet[at + 2] = c.B;
    }
}
```

Two of the assertions above pin choices the hardware has not confirmed yet: the Wave
test expects colour at `at + 3` (after speed, random, direction) and the two-colour
test expects `at + 3` / `at + 6`. Those match the documented shapes; if the owner's
hardware disagrees, the fix is a one-line offset change plus the matching test.

- [ ] **Step 5: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add lighting effect model and 264-byte packet builder"
```

---

### Task 3: Key layout maps

**Files:**
- Create: `src/OpenAorus.Hardware/Lighting/KeyLayout.cs`
- Test: `tests/OpenAorus.Hardware.Tests/KeyLayoutTests.cs`
- Read (do not modify): `docs/research/ione-keymap-eng-us.txt`, `docs/research/ione-keymap-eng-uk.txt`

**Interfaces:**
- Produces:
  - `enum KeyboardLayout { EngUs, EngUk }`
  - `sealed class KeyLayout` with `const int SlotCount = 128`, `KeyboardLayout Layout`, `IReadOnlyList<string> Slots`, `static KeyLayout For(KeyboardLayout)`, `static KeyLayout ForProduct(ushort pid)`, `int IndexOf(string keyName)`, `string NameAt(int slot)`, `IEnumerable<(int Slot, string Name)> RealKeys`
  - `const string KeyLayout.Unused = "N/A"`

The two source files are the slot orders recovered from Gigabyte's software and the
reference client. Each holds exactly 128 comma-separated quoted names in slot order,
`'N/A'` for slots the keyboard does not populate. Transcribe them verbatim; do not
re-order, de-duplicate or "fix" names.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/KeyLayoutTests.cs`:

```csharp
using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

public class KeyLayoutTests
{
    [Theory]
    [InlineData(KeyboardLayout.EngUs)]
    [InlineData(KeyboardLayout.EngUk)]
    public void Every_layout_has_exactly_128_slots(KeyboardLayout layout)
        => Assert.Equal(128, KeyLayout.For(layout).Slots.Count);

    [Theory]
    [InlineData(KeyboardLayout.EngUs)]
    [InlineData(KeyboardLayout.EngUk)]
    public void Real_key_names_are_unique_within_a_layout(KeyboardLayout layout)
    {
        var names = KeyLayout.For(layout).RealKeys.Select(k => k.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(KeyboardLayout.EngUs)]
    [InlineData(KeyboardLayout.EngUk)]
    public void A_layout_populates_around_a_hundred_real_keys(KeyboardLayout layout)
        => Assert.InRange(KeyLayout.For(layout).RealKeys.Count(), 95, 110);

    [Fact]
    public void Known_slots_match_the_recovered_order()
    {
        var us = KeyLayout.For(KeyboardLayout.EngUs);
        Assert.Equal("Ctrl-R", us.NameAt(4));
        Assert.Equal("Q", us.NameAt(8));
        Assert.Equal("ESC", us.NameAt(11));
        Assert.Equal(KeyLayout.Unused, us.NameAt(0));
        Assert.Equal(8, us.IndexOf("Q"));
    }

    [Fact]
    public void IndexOf_returns_minus_one_for_an_unknown_or_unused_name()
    {
        var us = KeyLayout.For(KeyboardLayout.EngUs);
        Assert.Equal(-1, us.IndexOf("NoSuchKey"));
        Assert.Equal(-1, us.IndexOf(KeyLayout.Unused));
    }

    [Fact]
    public void The_two_layouts_are_not_identical()
    {
        var us = KeyLayout.For(KeyboardLayout.EngUs).Slots;
        var uk = KeyLayout.For(KeyboardLayout.EngUk).Slots;
        Assert.NotEqual(us, uk);
    }

    [Fact]
    public void Product_id_7A3D_defaults_to_the_UK_order_and_7A3C_to_US()
    {
        Assert.Equal(KeyboardLayout.EngUk, KeyLayout.ForProduct(0x7A3D).Layout);
        Assert.Equal(KeyboardLayout.EngUs, KeyLayout.ForProduct(0x7A3C).Layout);
    }

    [Fact]
    public void NameAt_rejects_a_slot_outside_the_report()
    {
        var us = KeyLayout.For(KeyboardLayout.EngUs);
        Assert.Throws<ArgumentOutOfRangeException>(() => us.NameAt(128));
        Assert.Throws<ArgumentOutOfRangeException>(() => us.NameAt(-1));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~KeyLayoutTests`
Expected: compile error, `KeyLayout` not found.

- [ ] **Step 3: Implement**

`src/OpenAorus.Hardware/Lighting/KeyLayout.cs` — the shape below is complete except for
the two 128-entry arrays, which you transcribe from the two research files named above:

```csharp
namespace OpenAorus.Hardware.Lighting;

public enum KeyboardLayout { EngUs, EngUk }

/// <summary>
/// Maps the keyboard's 128 lighting slots to key names. Slot order is a property of the
/// firmware, not of the visual layout, and it differs between the US and UK variants.
/// Gigabyte's own software flags product 0x7A3D as the UK order and 0x7A3C as US;
/// that mapping is unverified on hardware, so the app exposes a manual override.
/// </summary>
public sealed class KeyLayout
{
    public const int SlotCount = 128;
    public const string Unused = "N/A";

    private static readonly string[] EngUsSlots =
    {
        // transcribe all 128 entries from docs/research/ione-keymap-eng-us.txt, in order
    };

    private static readonly string[] EngUkSlots =
    {
        // transcribe all 128 entries from docs/research/ione-keymap-eng-uk.txt, in order
    };

    private static readonly KeyLayout Us = new(KeyboardLayout.EngUs, EngUsSlots);
    private static readonly KeyLayout Uk = new(KeyboardLayout.EngUk, EngUkSlots);

    public KeyboardLayout Layout { get; }
    public IReadOnlyList<string> Slots { get; }

    private KeyLayout(KeyboardLayout layout, string[] slots)
    {
        if (slots.Length != SlotCount)
            throw new InvalidOperationException($"{layout} layout must define exactly {SlotCount} slots, found {slots.Length}.");
        Layout = layout;
        Slots = slots;
    }

    public static KeyLayout For(KeyboardLayout layout) => layout == KeyboardLayout.EngUk ? Uk : Us;

    /// <summary>Gigabyte's software treats 0x7A3D as the UK slot order and 0x7A3C as US.</summary>
    public static KeyLayout ForProduct(ushort productId) => productId == 0x7A3D ? Uk : Us;

    public string NameAt(int slot)
    {
        if (slot is < 0 or >= SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
        return Slots[slot];
    }

    public int IndexOf(string keyName)
    {
        if (string.IsNullOrEmpty(keyName) || keyName == Unused) return -1;
        for (var i = 0; i < SlotCount; i++)
            if (string.Equals(Slots[i], keyName, StringComparison.Ordinal)) return i;
        return -1;
    }

    public IEnumerable<(int Slot, string Name)> RealKeys
    {
        get
        {
            for (var i = 0; i < SlotCount; i++)
                if (Slots[i] != Unused) yield return (i, Slots[i]);
        }
    }
}
```

If a transcribed array turns out to contain a duplicate real key name, do not silently
rename it — the uniqueness test will fail, and that failure is information about the
source data. Report it instead of editing the name.

- [ ] **Step 4: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add Ione key-slot layouts for ENG-US and ENG-UK"
```

---

### Task 4: Per-key colour packets

**Files:**
- Create: `src/OpenAorus.Hardware/Lighting/PerKeyPacket.cs`
- Test: `tests/OpenAorus.Hardware.Tests/PerKeyPacketTests.cs`

**Interfaces:**
- Consumes: `KeyboardHid.ReportLength/ReportId`, `KeyLayout.SlotCount`, `RgbColor`
- Produces:
  - `static class PerKeyPacket` with
    `(byte[] First, byte[] Second) BuildWrite(IReadOnlyList<RgbColor> colors)`,
    `(byte[] First, byte[] Second) BuildRead()`,
    `RgbColor[] Parse(byte[] firstResponse, byte[] secondResponse)`,
    `const int HeaderLength = 8`

Wire format, from the protocol reference: colours travel plane-major over the 128
slots — all 128 reds, then all 128 greens, then all 128 blues, 384 bytes total, split
across two feature reports.

```
write 1: [0x07, 0x06, 0x00, 0x01] + 4 zero bytes + 128 reds + 128 greens   (264 bytes)
write 2: [0x07, 0x06, 0x00, 0x02] + 4 zero bytes + 128 blues + 128 zeros   (264 bytes)
read  1: [0x07, 0x86, 0x00, 0x01] + 260 zero bytes
read  2: [0x07, 0x86, 0x00, 0x02] + 260 zero bytes
```

Responses carry the same 8-byte header; concatenating the two payloads after their
headers gives the same 384-byte plane order.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/PerKeyPacketTests.cs`:

```csharp
using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

public class PerKeyPacketTests
{
    private static RgbColor[] Ramp()
    {
        var colors = new RgbColor[KeyLayout.SlotCount];
        for (var i = 0; i < colors.Length; i++)
            colors[i] = new RgbColor((byte)i, (byte)(255 - i), (byte)(i * 2 % 256));
        return colors;
    }

    [Fact]
    public void Write_produces_two_264_byte_reports_with_the_documented_headers()
    {
        var (first, second) = PerKeyPacket.BuildWrite(Ramp());
        Assert.Equal(264, first.Length);
        Assert.Equal(264, second.Length);
        Assert.Equal(new byte[] { 0x07, 0x06, 0x00, 0x01, 0, 0, 0, 0 }, first[..8]);
        Assert.Equal(new byte[] { 0x07, 0x06, 0x00, 0x02, 0, 0, 0, 0 }, second[..8]);
    }

    [Fact]
    public void Colours_are_laid_out_plane_major_across_the_two_reports()
    {
        var colors = Ramp();
        var (first, second) = PerKeyPacket.BuildWrite(colors);

        for (var i = 0; i < 128; i++)
        {
            Assert.Equal(colors[i].R, first[8 + i]);
            Assert.Equal(colors[i].G, first[8 + 128 + i]);
            Assert.Equal(colors[i].B, second[8 + i]);
        }
    }

    [Fact]
    public void The_tail_of_the_second_report_is_padding()
        => Assert.All(PerKeyPacket.BuildWrite(Ramp()).Second[(8 + 128)..], b => Assert.Equal(0, b));

    [Fact]
    public void Read_requests_use_command_0x86_and_the_two_page_selectors()
    {
        var (first, second) = PerKeyPacket.BuildRead();
        Assert.Equal(new byte[] { 0x07, 0x86, 0x00, 0x01 }, first[..4]);
        Assert.Equal(new byte[] { 0x07, 0x86, 0x00, 0x02 }, second[..4]);
        Assert.All(first[4..], b => Assert.Equal(0, b));
        Assert.All(second[4..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Parse_is_the_inverse_of_BuildWrite()
    {
        var colors = Ramp();
        var (first, second) = PerKeyPacket.BuildWrite(colors);
        Assert.Equal(colors, PerKeyPacket.Parse(first, second));
    }

    [Fact]
    public void BuildWrite_rejects_a_wrong_number_of_colours()
    {
        Assert.Throws<ArgumentException>(() => PerKeyPacket.BuildWrite(new RgbColor[127]));
        Assert.Throws<ArgumentException>(() => PerKeyPacket.BuildWrite(new RgbColor[129]));
    }

    [Fact]
    public void Parse_rejects_short_responses()
    {
        Assert.Throws<ArgumentException>(() => PerKeyPacket.Parse(new byte[8], new byte[264]));
        Assert.Throws<ArgumentException>(() => PerKeyPacket.Parse(new byte[264], new byte[8]));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~PerKeyPacketTests`
Expected: compile error, `PerKeyPacket` not found.

- [ ] **Step 3: Implement**

`src/OpenAorus.Hardware/Lighting/PerKeyPacket.cs`:

```csharp
namespace OpenAorus.Hardware.Lighting;

/// <summary>
/// Builds and parses the two feature reports that carry the 128 per-key colours.
///
/// Colours are plane-major: 128 reds, then 128 greens, then 128 blues. Report one
/// carries reds and greens, report two carries blues followed by padding. Reads use
/// command 0x86 with the same two page selectors and answer with the same layout
/// behind an 8-byte header.
/// </summary>
public static class PerKeyPacket
{
    public const int HeaderLength = 8;
    private const byte WriteCommand = 0x06;
    private const byte ReadCommand = 0x86;

    public static (byte[] First, byte[] Second) BuildWrite(IReadOnlyList<RgbColor> colors)
    {
        if (colors is null) throw new ArgumentNullException(nameof(colors));
        if (colors.Count != KeyLayout.SlotCount)
            throw new ArgumentException($"Expected exactly {KeyLayout.SlotCount} colours, got {colors.Count}.", nameof(colors));

        var first = NewReport(WriteCommand, page: 1);
        var second = NewReport(WriteCommand, page: 2);

        for (var i = 0; i < KeyLayout.SlotCount; i++)
        {
            first[HeaderLength + i] = colors[i].R;
            first[HeaderLength + KeyLayout.SlotCount + i] = colors[i].G;
            second[HeaderLength + i] = colors[i].B;
        }

        return (first, second);
    }

    public static (byte[] First, byte[] Second) BuildRead() =>
        (NewReport(ReadCommand, page: 1, headerOnly: true), NewReport(ReadCommand, page: 2, headerOnly: true));

    public static RgbColor[] Parse(byte[] firstResponse, byte[] secondResponse)
    {
        if (firstResponse is null) throw new ArgumentNullException(nameof(firstResponse));
        if (secondResponse is null) throw new ArgumentNullException(nameof(secondResponse));
        if (firstResponse.Length != KeyboardHid.ReportLength)
            throw new ArgumentException($"First response must be {KeyboardHid.ReportLength} bytes.", nameof(firstResponse));
        if (secondResponse.Length != KeyboardHid.ReportLength)
            throw new ArgumentException($"Second response must be {KeyboardHid.ReportLength} bytes.", nameof(secondResponse));

        var colors = new RgbColor[KeyLayout.SlotCount];
        for (var i = 0; i < KeyLayout.SlotCount; i++)
            colors[i] = new RgbColor(
                firstResponse[HeaderLength + i],
                firstResponse[HeaderLength + KeyLayout.SlotCount + i],
                secondResponse[HeaderLength + i]);
        return colors;
    }

    private static byte[] NewReport(byte command, byte page, bool headerOnly = false)
    {
        var report = new byte[KeyboardHid.ReportLength];
        report[0] = KeyboardHid.ReportId;
        report[1] = command;
        report[2] = 0x00;
        report[3] = page;
        // headerOnly reports carry nothing else; the remaining bytes stay zero either way.
        _ = headerOnly;
        return report;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add per-key colour packet builder and parser"
```

---

### Task 5: LightingController

**Files:**
- Create: `src/OpenAorus.Hardware/Lighting/LightingController.cs`
- Test: `tests/OpenAorus.Hardware.Tests/LightingControllerTests.cs`

**Interfaces:**
- Consumes: `IKeyboardHid`, `EffectPacket`, `PerKeyPacket`, `KeyLayout`, `EffectParameters`, `RgbColor`
- Produces:
  - `sealed record LightingResult(bool Success, string? Error)` with `static Ok()`, `static Fail(string)`
  - `sealed class LightingController(IKeyboardHid hid, KeyLayout layout, Func<int, Task>? delay = null)`
  - `const int WriteDelayMs = 65`
  - `bool IsPresent`, `KeyLayout Layout`
  - `Task<LightingResult> ApplyEffectAsync(EffectParameters p, CancellationToken ct = default)`
  - `Task<LightingResult> ApplyPerKeyAsync(IReadOnlyList<RgbColor> colors, int brightnessPercent, CancellationToken ct = default)`
  - `Task<RgbColor[]?> ReadPerKeyAsync(CancellationToken ct = default)`
  - `Task<LightingResult> SetBrightnessAsync(EffectParameters current, int brightnessPercent, CancellationToken ct = default)`

The controller owns the pacing and the ordering. Writes are serialized behind a
semaphore, 65 ms apart, exactly like `FanController` in v0.1 — the keyboard silently
drops reports sent faster.

Applying per-key colours is three writes in order: the two colour reports, then the
`0x02` report selecting effect `0x12`. Selecting the effect first would show the
previous custom colours for a moment.

> **AMENDMENT, supersedes the sample code below where they conflict.**
>
> `ApplyEffectAsync` must **read-modify-write**, not send a freshly zeroed buffer.
>
> Every effect keeps its configuration in its own slice of one shared 264-byte block,
> and command `0x82` reads that whole block back — so the keyboard stores it. Sending a
> zeroed buffer with only the active effect's slice filled would therefore erase every
> other effect's saved settings on each switch: set up Wave, select Ripple, come back,
> and Wave is at defaults. See section 6a of the design spec.
>
> The sequence is: `SetFeature` a `0x82` request, `GetFeature` 264 bytes, patch only what
> this effect owns, then `SetFeature` the `0x02` report. Patching means bytes 0 and 1
> (framing), 2..9 (zero), 10 (effect id), 11 (the `0xFF` marker, which must be cleared
> when the new effect is not Static or StarShining), 12 (brightness), and the effect's
> own slice. Everything else in the buffer is preserved untouched.
>
> If the read fails or returns anything other than 264 bytes, fall back to a zeroed
> buffer — that is the old behaviour and no worse than it. Do not fail the call.
>
> This needs a new `EffectPacket.Build(EffectParameters, byte[] existing)` overload that
> patches a caller-supplied buffer; the existing single-argument `Build` stays and is
> defined as patching a fresh zeroed buffer. Both must produce identical output when the
> supplied buffer is all zeroes — pin that with a test.
>
> Consequences for the tests written below, which assume a single write:
> `ApplyEffect_writes_one_report_built_by_EffectPacket` becomes a read followed by one
> write, so assert on the **last** `hid.Written` entry rather than `Assert.Single`.
> Add a test proving the surrounding buffer survives: seed the fake's read-back with a
> recognisable non-zero pattern outside the target slice, apply an effect, and assert
> those bytes are unchanged in what was written. Add a test that a failed or short read
> falls back to zeroes and still succeeds.
>
> This is correct whether the firmware persists the whole block or reads only the active
> mode's slice, so it does not wait on hardware. It costs one extra feature report per
> change, well inside the 65 ms pacing budget. Per-key colours are unaffected: research
> document line 89 confirms they live in separate storage and survive a `0x02` write.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/LightingControllerTests.cs`:

```csharp
using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

public class LightingControllerTests
{
    private static Task NoDelay(int _) => Task.CompletedTask;

    private static (LightingController ctl, FakeKeyboardHid hid) Make()
    {
        var hid = new FakeKeyboardHid();
        return (new LightingController(hid, KeyLayout.For(KeyboardLayout.EngUk), NoDelay), hid);
    }

    private static RgbColor[] Solid(RgbColor c) => Enumerable.Repeat(c, KeyLayout.SlotCount).ToArray();

    [Fact]
    public async Task ApplyEffect_writes_one_report_built_by_EffectPacket()
    {
        var (ctl, hid) = Make();
        var p = EffectParameters.Default(LightEffect.Wave) with { Color = new RgbColor(9, 8, 7) };

        var result = await ctl.ApplyEffectAsync(p);

        Assert.True(result.Success);
        Assert.Single(hid.Written);
        Assert.Equal(EffectPacket.Build(p), hid.Written[0]);
    }

    [Fact]
    public async Task ApplyPerKey_writes_both_colour_reports_then_selects_custom_mode()
    {
        var (ctl, hid) = Make();
        var colors = Solid(new RgbColor(0x10, 0x20, 0x30));

        var result = await ctl.ApplyPerKeyAsync(colors, brightnessPercent: 70);

        Assert.True(result.Success);
        Assert.Equal(3, hid.Written.Count);
        Assert.Equal(0x06, hid.Command(0));
        Assert.Equal(1, hid.Written[0][3]);
        Assert.Equal(0x06, hid.Command(1));
        Assert.Equal(2, hid.Written[1][3]);
        Assert.Equal(0x02, hid.Command(2));
        Assert.Equal((byte)LightEffect.Custom, hid.Written[2][10]);
        Assert.Equal(70, hid.Written[2][12]);
    }

    [Fact]
    public async Task ApplyPerKey_stops_and_reports_when_the_first_report_fails()
    {
        var (ctl, hid) = Make();
        hid.FailNextWrite = true;

        var result = await ctl.ApplyPerKeyAsync(Solid(RgbColor.White), 50);

        Assert.False(result.Success);
        Assert.Contains("colour", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Single(hid.Written);
    }

    [Fact]
    public async Task ApplyEffect_reports_a_failed_write()
    {
        var (ctl, hid) = Make();
        hid.FailNextWrite = true;

        var result = await ctl.ApplyEffectAsync(EffectParameters.Default(LightEffect.Static));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ReadPerKey_round_trips_through_the_two_page_responses()
    {
        var (ctl, hid) = Make();
        var colors = Solid(new RgbColor(1, 2, 3));
        var (first, second) = PerKeyPacket.BuildWrite(colors);
        hid.Responses.Enqueue(first);
        hid.Responses.Enqueue(second);

        var read = await ctl.ReadPerKeyAsync();

        Assert.Equal(colors, read);
        Assert.Equal(2, hid.Written.Count);
        Assert.Equal(0x86, hid.Command(0));
        Assert.Equal(0x86, hid.Command(1));
    }

    [Fact]
    public async Task ReadPerKey_returns_null_when_the_keyboard_does_not_answer()
    {
        var (ctl, _) = Make();
        Assert.Null(await ctl.ReadPerKeyAsync());
    }

    [Fact]
    public async Task SetBrightness_resends_the_current_effect_with_the_new_value()
    {
        var (ctl, hid) = Make();
        var current = EffectParameters.Default(LightEffect.Breathing) with { BrightnessPercent = 20 };

        var result = await ctl.SetBrightnessAsync(current, 90);

        Assert.True(result.Success);
        Assert.Single(hid.Written);
        Assert.Equal((byte)LightEffect.Breathing, hid.Written[0][10]);
        Assert.Equal(90, hid.Written[0][12]);
    }

    [Fact]
    public async Task An_absent_keyboard_fails_every_call_without_writing()
    {
        var hid = new FakeKeyboardHid { IsPresent = false, Identity = null };
        var ctl = new LightingController(hid, KeyLayout.For(KeyboardLayout.EngUs), NoDelay);

        Assert.False((await ctl.ApplyEffectAsync(EffectParameters.Default(LightEffect.Static))).Success);
        Assert.False((await ctl.ApplyPerKeyAsync(Solid(RgbColor.White), 50)).Success);
        Assert.Null(await ctl.ReadPerKeyAsync());
        Assert.Empty(hid.Written);
        Assert.False(ctl.IsPresent);
    }

    [Fact]
    public async Task Writes_are_paced_between_reports_but_not_after_the_last()
    {
        var delays = 0;
        var hid = new FakeKeyboardHid();
        var ctl = new LightingController(hid, KeyLayout.For(KeyboardLayout.EngUk), _ => { delays++; return Task.CompletedTask; });

        await ctl.ApplyPerKeyAsync(Solid(RgbColor.White), 50);

        Assert.Equal(2, delays);
    }

    [Fact]
    public async Task Concurrent_applies_are_serialized()
    {
        var hid = new FakeKeyboardHid();
        var gate = new SemaphoreSlim(0);
        var ctl = new LightingController(hid, KeyLayout.For(KeyboardLayout.EngUk), async _ => await gate.WaitAsync());

        var first = ctl.ApplyPerKeyAsync(Solid(RgbColor.Black), 10);
        var second = ctl.ApplyEffectAsync(EffectParameters.Default(LightEffect.Static));

        Assert.Single(hid.Written);
        for (var i = 0; i < 4; i++) gate.Release();
        await first;
        await second;

        Assert.Equal(4, hid.Written.Count);
        Assert.Equal((byte)LightEffect.Static, hid.Written[^1][10]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~LightingControllerTests`
Expected: compile error, `LightingController` not found.

- [ ] **Step 3: Implement**

`src/OpenAorus.Hardware/Lighting/LightingController.cs`:

```csharp
namespace OpenAorus.Hardware.Lighting;

public sealed record LightingResult(bool Success, string? Error)
{
    public static LightingResult Ok() => new(true, null);
    public static LightingResult Fail(string error) => new(false, error);
}

/// <summary>
/// Sequences lighting writes. Reports are serialized and paced 65 ms apart, matching
/// Gigabyte's own software: the keyboard drops reports sent faster than that.
/// </summary>
public sealed class LightingController
{
    public const int WriteDelayMs = 65;

    private readonly IKeyboardHid _hid;
    private readonly Func<int, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public KeyLayout Layout { get; }
    public bool IsPresent => _hid.IsPresent;

    public LightingController(IKeyboardHid hid, KeyLayout layout, Func<int, Task>? delay = null)
    {
        _hid = hid;
        Layout = layout;
        _delay = delay ?? (ms => Task.Delay(ms));
    }

    public async Task<LightingResult> ApplyEffectAsync(EffectParameters p, CancellationToken ct = default)
    {
        if (!_hid.IsPresent) return LightingResult.Fail("No supported keyboard found.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _hid.SetFeature(EffectPacket.Build(p))
                ? LightingResult.Ok()
                : LightingResult.Fail($"The keyboard rejected the {p.Effect} effect report.");
        }
        finally { _gate.Release(); }
    }

    public async Task<LightingResult> ApplyPerKeyAsync(IReadOnlyList<RgbColor> colors, int brightnessPercent, CancellationToken ct = default)
    {
        if (!_hid.IsPresent) return LightingResult.Fail("No supported keyboard found.");

        var (first, second) = PerKeyPacket.BuildWrite(colors);
        var select = EffectPacket.Build(
            EffectParameters.Default(LightEffect.Custom) with { BrightnessPercent = brightnessPercent });

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_hid.SetFeature(first)) return LightingResult.Fail("The keyboard rejected the first per-key colour report.");
            await _delay(WriteDelayMs).ConfigureAwait(false);

            if (!_hid.SetFeature(second)) return LightingResult.Fail("The keyboard rejected the second per-key colour report.");
            await _delay(WriteDelayMs).ConfigureAwait(false);

            return _hid.SetFeature(select)
                ? LightingResult.Ok()
                : LightingResult.Fail("The colours were written but switching to custom mode failed.");
        }
        finally { _gate.Release(); }
    }

    public async Task<RgbColor[]?> ReadPerKeyAsync(CancellationToken ct = default)
    {
        if (!_hid.IsPresent) return null;

        var (firstRequest, secondRequest) = PerKeyPacket.BuildRead();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_hid.SetFeature(firstRequest)) return null;
            var firstResponse = _hid.GetFeature();
            if (firstResponse is null) return null;
            await _delay(WriteDelayMs).ConfigureAwait(false);

            if (!_hid.SetFeature(secondRequest)) return null;
            var secondResponse = _hid.GetFeature();
            if (secondResponse is null) return null;

            return PerKeyPacket.Parse(firstResponse, secondResponse);
        }
        finally { _gate.Release(); }
    }

    public Task<LightingResult> SetBrightnessAsync(EffectParameters current, int brightnessPercent, CancellationToken ct = default) =>
        ApplyEffectAsync(current with { BrightnessPercent = brightnessPercent }, ct);
}
```

Note on the read path: `ReadPerKeyAsync` writes a request and immediately reads the
answer, so the fake's response queue is consumed in the same order the hardware
answers. The pacing delay sits between the two page exchanges, not inside one.

- [ ] **Step 4: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass. The `Concurrent_applies_are_serialized` test needs exactly four
gate releases: the per-key apply awaits two delays and the effect apply awaits none,
plus two spare releases that are harmless.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add LightingController with paced, serialized writes"
```

---

### Task 6: Settings, presets and app wiring

**Files:**
- Modify: `src/OpenAorus.Hardware/Config/AppSettings.cs`, `src/OpenAorus.App/AppServices.cs`
- Create: `src/OpenAorus.Hardware/Config/LightingSettings.cs`
- Test: `tests/OpenAorus.Hardware.Tests/LightingSettingsTests.cs`

**Interfaces:**
- Produces:
  - `sealed class LightingSettings` — `LightEffect Effect`, `RgbColor Color`, `RgbColor SecondColor`, `int SpeedPercent`, `int BrightnessPercent`, `LightDirection Direction`, `bool Random`, `List<RgbColor> PerKeyColors`, `KeyboardLayout? LayoutOverride`, `List<LightingPreset> Presets`
  - `sealed class LightingPreset` — `string Name`, `LightEffect Effect`, `RgbColor Color`, `RgbColor SecondColor`, `int SpeedPercent`, `int BrightnessPercent`, `LightDirection Direction`, `bool Random`, `List<RgbColor>? PerKeyColors`
  - `EffectParameters LightingSettings.ToParameters()` and `void LightingSettings.From(EffectParameters)`
  - `static IReadOnlyList<LightingPreset> LightingSettings.BuiltInPresets` — Off, Warm White, Aorus Orange
  - `AppSettings.Lighting` (`LightingSettings`, never null)
  - `AppServices.Lighting` (`LightingController`), `AppServices.KeyboardPresent` (`bool`)
  - `AppServices.ApplySavedAsync` also re-applies the saved lighting

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/LightingSettingsTests.cs`:

```csharp
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

public class LightingSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_dir, "settings.json");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void A_fresh_AppSettings_has_usable_lighting_defaults()
    {
        var lighting = new AppSettings().Lighting;
        Assert.NotNull(lighting);
        Assert.Equal(LightEffect.Static, lighting.Effect);
        Assert.Equal(50, lighting.BrightnessPercent);
        Assert.Empty(lighting.PerKeyColors);
        Assert.Null(lighting.LayoutOverride);
    }

    [Fact]
    public void ToParameters_and_From_round_trip()
    {
        var p = EffectParameters.Default(LightEffect.Wave) with
        {
            Color = new RgbColor(1, 2, 3),
            SecondColor = new RgbColor(4, 5, 6),
            SpeedPercent = 70,
            BrightnessPercent = 30,
            Direction = LightDirection.Down,
            Random = true,
        };
        var settings = new LightingSettings();
        settings.From(p);
        Assert.Equal(p, settings.ToParameters());
    }

    [Fact]
    public void Lighting_survives_a_save_and_load_including_per_key_colours()
    {
        var store = new SettingsStore(File);
        var settings = store.Load();
        settings.Lighting.Effect = LightEffect.Custom;
        settings.Lighting.BrightnessPercent = 80;
        settings.Lighting.LayoutOverride = KeyboardLayout.EngUs;
        settings.Lighting.PerKeyColors = Enumerable.Range(0, KeyLayout.SlotCount)
            .Select(i => new RgbColor((byte)i, 0, 0)).ToList();
        store.Save(settings);

        var back = new SettingsStore(File).Load();
        Assert.Equal(LightEffect.Custom, back.Lighting.Effect);
        Assert.Equal(80, back.Lighting.BrightnessPercent);
        Assert.Equal(KeyboardLayout.EngUs, back.Lighting.LayoutOverride);
        Assert.Equal(settings.Lighting.PerKeyColors, back.Lighting.PerKeyColors);
    }

    [Fact]
    public void Enums_persist_as_names_not_numbers()
    {
        var store = new SettingsStore(File);
        var settings = store.Load();
        settings.Lighting.Effect = LightEffect.Breathing;
        settings.Lighting.Direction = LightDirection.Left;
        store.Save(settings);
        var json = System.IO.File.ReadAllText(File);
        Assert.Contains("\"Breathing\"", json);
        Assert.Contains("\"Left\"", json);
    }

    [Fact]
    public void Built_in_presets_are_named_and_valid()
    {
        var presets = LightingSettings.BuiltInPresets;
        Assert.Equal(3, presets.Count);
        Assert.All(presets, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
        Assert.Contains(presets, p => p.Name == "Off" && p.BrightnessPercent == 0);
        Assert.Equal(presets.Select(p => p.Name).Distinct().Count(), presets.Count);
    }

    [Fact]
    public void Settings_written_before_lighting_existed_still_load()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{ \"Mode\": \"Quiet\", \"FixedPercent\": 40 }");
        var loaded = new SettingsStore(File).Load();
        Assert.NotNull(loaded.Lighting);
        Assert.Equal(LightEffect.Static, loaded.Lighting.Effect);
    }
}
```

The last test matters: owners upgrading from v0.1 have a settings file with no
lighting section, and the app must not crash or reset their fan settings.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~LightingSettingsTests`
Expected: compile error, `LightingSettings` not found.

- [ ] **Step 3: Implement the settings types**

`src/OpenAorus.Hardware/Config/LightingSettings.cs`:

```csharp
using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Config;

public sealed class LightingPreset
{
    public string Name { get; set; } = "";
    public LightEffect Effect { get; set; } = LightEffect.Static;
    public RgbColor Color { get; set; } = RgbColor.White;
    public RgbColor SecondColor { get; set; } = new(0x00, 0x00, 0xFF);
    public int SpeedPercent { get; set; } = 50;
    public int BrightnessPercent { get; set; } = 50;
    public LightDirection Direction { get; set; } = LightDirection.Right;
    public bool Random { get; set; }
    public List<RgbColor>? PerKeyColors { get; set; }
}

public sealed class LightingSettings
{
    public LightEffect Effect { get; set; } = LightEffect.Static;
    public RgbColor Color { get; set; } = RgbColor.White;
    public RgbColor SecondColor { get; set; } = new(0x00, 0x00, 0xFF);
    public int SpeedPercent { get; set; } = 50;
    public int BrightnessPercent { get; set; } = 50;
    public LightDirection Direction { get; set; } = LightDirection.Right;
    public bool Random { get; set; }
    public List<RgbColor> PerKeyColors { get; set; } = new();

    /// <summary>Set when the owner's keyboard disagrees with the slot order implied by its product id.</summary>
    public KeyboardLayout? LayoutOverride { get; set; }

    public List<LightingPreset> Presets { get; set; } = new();

    public EffectParameters ToParameters() =>
        new(Effect, Color, SecondColor, SpeedPercent, BrightnessPercent, Direction, Random);

    public void From(EffectParameters p)
    {
        Effect = p.Effect;
        Color = p.Color;
        SecondColor = p.SecondColor;
        SpeedPercent = p.SpeedPercent;
        BrightnessPercent = p.BrightnessPercent;
        Direction = p.Direction;
        Random = p.Random;
    }

    public static IReadOnlyList<LightingPreset> BuiltInPresets { get; } = new[]
    {
        new LightingPreset { Name = "Off", Effect = LightEffect.Static, Color = RgbColor.Black, BrightnessPercent = 0 },
        new LightingPreset { Name = "Warm White", Effect = LightEffect.Static, Color = new RgbColor(0xFF, 0xC8, 0x80), BrightnessPercent = 60 },
        new LightingPreset { Name = "Aorus Orange", Effect = LightEffect.Static, Color = new RgbColor(0xFF, 0x7A, 0x1A), BrightnessPercent = 70 },
    };
}
```

Add to `AppSettings`:

```csharp
    public LightingSettings Lighting { get; set; } = new();
```

`SettingsStore` already ignores unknown properties and already registers
`JsonStringEnumConverter`, so a v0.1 file without a `Lighting` object deserializes with
the property initialiser intact. Do not add migration code.

- [ ] **Step 4: Wire the controller into the app**

In `src/OpenAorus.App/AppServices.cs` add the required members and construct them in
`Create()`:

```csharp
    public required LightingController Lighting { get; init; }
    public required bool KeyboardPresent { get; init; }
```

`Create()` currently loads settings inline into the object initialiser. Hoist that into
a local first, because the layout choice depends on the saved override:

```csharp
        var settings = store.Load();
        var keyboard = KeyboardHid.Open();
        var layout = settings.Lighting.LayoutOverride is { } forced
            ? KeyLayout.For(forced)
            : KeyLayout.ForProduct(keyboard.Identity?.Pid ?? 0);
```

then pass `settings` to the `Settings` member instead of calling `store.Load()` again,
and add `Lighting = new LightingController(keyboard, layout)` and
`KeyboardPresent = keyboard.IsPresent` to the initialiser. Loading the settings twice
would give the app two divergent copies, so make sure only one `store.Load()` call
remains in the method.

and extend `ApplySavedAsync` so lighting is restored next to the fan mode:

```csharp
        if (KeyboardPresent)
        {
            var lighting = Settings.Lighting.Effect == LightEffect.Custom && Settings.Lighting.PerKeyColors.Count == KeyLayout.SlotCount
                ? await Lighting.ApplyPerKeyAsync(Settings.Lighting.PerKeyColors, Settings.Lighting.BrightnessPercent)
                : await Lighting.ApplyEffectAsync(Settings.Lighting.ToParameters());
            if (!lighting.Success) return WmiResult.Fail(lighting.Error!);
        }
```

Keep the existing fan and battery behaviour ahead of this block, unchanged. A missing
keyboard must never make `ApplySavedAsync` fail.

- [ ] **Step 5: Run tests and build**

Run: `dotnet test OpenAorus.sln` then `dotnet build OpenAorus.sln -c Release`
Expected: all tests pass, Release build clean.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Persist lighting settings and restore them at startup"
```

---

### Task 7: Lighting view model

**Files:**
- Create: `src/OpenAorus.App/ViewModels/LightingViewModel.cs`
- Modify: `src/OpenAorus.App/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `AppServices`, `LightingController`, `EffectPacket` capability helpers, `LightingSettings`, `BannerKind`, `MainViewModel.SetBanner`
- Produces:
  - `partial class LightingViewModel : ObservableObject` with observable
    `LightEffect SelectedEffect`, `Color PickedColor`, `Color PickedSecondColor`,
    `int SpeedPercent`, `int BrightnessPercent`, `LightDirection Direction`, `bool Random`,
    `string StatusText`, `bool IsBusy`
  - read-only `bool KeyboardPresent`, `string DeviceLine`, `IReadOnlyList<LightEffect> Effects`,
    `IReadOnlyList<LightDirection> Directions`, and the four capability flags
    `SupportsColor`, `SupportsSecondColor`, `SupportsSpeed`, `SupportsDirection`, `SupportsRandom`
  - `ObservableCollection<LightingPreset> Presets`
  - commands `ApplyEffectCommand`, `ApplyPresetCommand(LightingPreset)`, `SavePresetCommand`, `DeletePresetCommand(LightingPreset)`
  - `PerKeyEditorViewModel PerKey` (Task 8 fills the editor; declare the property there)
  - `MainViewModel.Lighting` (`LightingViewModel`)

WPF's colour type is `System.Windows.Media.Color` and the hardware type is `RgbColor`;
convert at this boundary and nowhere else.

- [ ] **Step 1: Implement**

`src/OpenAorus.App/ViewModels/LightingViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Lighting;
using MediaColor = System.Windows.Media.Color;

namespace OpenAorus.App.ViewModels;

public partial class LightingViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Action<BannerKind, string> _banner;

    [ObservableProperty] private LightEffect _selectedEffect;
    [ObservableProperty] private MediaColor _pickedColor;
    [ObservableProperty] private MediaColor _pickedSecondColor;
    [ObservableProperty] private int _speedPercent;
    [ObservableProperty] private int _brightnessPercent;
    [ObservableProperty] private LightDirection _direction;
    [ObservableProperty] private bool _random;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    public bool KeyboardPresent => _s.KeyboardPresent;

    public string DeviceLine => _s.Lighting.IsPresent
        ? $"Keyboard {_s.Lighting.Layout.Layout} layout"
        : "No supported keyboard found";

    public IReadOnlyList<LightEffect> Effects { get; } = Enum.GetValues<LightEffect>();
    public IReadOnlyList<LightDirection> Directions { get; } = Enum.GetValues<LightDirection>();

    public bool SupportsColor => EffectPacket.SupportsColor(SelectedEffect);
    public bool SupportsSecondColor => EffectPacket.SupportsSecondColor(SelectedEffect);
    public bool SupportsSpeed => EffectPacket.SupportsSpeed(SelectedEffect);
    public bool SupportsDirection => EffectPacket.SupportsDirection(SelectedEffect);
    public bool SupportsRandom => EffectPacket.SupportsRandom(SelectedEffect);

    public ObservableCollection<LightingPreset> Presets { get; }

    public LightingViewModel(AppServices services, Action<BannerKind, string> banner)
    {
        _s = services;
        _banner = banner;

        var saved = _s.Settings.Lighting;
        _selectedEffect = saved.Effect;
        _pickedColor = ToMedia(saved.Color);
        _pickedSecondColor = ToMedia(saved.SecondColor);
        _speedPercent = saved.SpeedPercent;
        _brightnessPercent = saved.BrightnessPercent;
        _direction = saved.Direction;
        _random = saved.Random;

        Presets = new ObservableCollection<LightingPreset>(
            LightingSettings.BuiltInPresets.Concat(saved.Presets));
    }

    partial void OnSelectedEffectChanged(LightEffect value)
    {
        OnPropertyChanged(nameof(SupportsColor));
        OnPropertyChanged(nameof(SupportsSecondColor));
        OnPropertyChanged(nameof(SupportsSpeed));
        OnPropertyChanged(nameof(SupportsDirection));
        OnPropertyChanged(nameof(SupportsRandom));
    }

    private static MediaColor ToMedia(RgbColor c) => MediaColor.FromRgb(c.R, c.G, c.B);
    private static RgbColor ToRgb(MediaColor c) => new(c.R, c.G, c.B);

    private EffectParameters CurrentParameters() => new(
        SelectedEffect, ToRgb(PickedColor), ToRgb(PickedSecondColor),
        SpeedPercent, BrightnessPercent, Direction, Random);

    [RelayCommand]
    private async Task ApplyEffectAsync()
    {
        if (!KeyboardPresent || IsBusy) return;
        IsBusy = true;
        try
        {
            var parameters = CurrentParameters();
            var result = await _s.Lighting.ApplyEffectAsync(parameters);
            if (!result.Success)
            {
                StatusText = "Failed";
                _banner(BannerKind.Error, result.Error!);
                return;
            }
            _s.Settings.Lighting.From(parameters);
            _s.Store.Save(_s.Settings);
            StatusText = $"{parameters.Effect} applied";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ApplyPresetAsync(LightingPreset? preset)
    {
        if (preset is null || !KeyboardPresent) return;
        SelectedEffect = preset.Effect;
        PickedColor = ToMedia(preset.Color);
        PickedSecondColor = ToMedia(preset.SecondColor);
        SpeedPercent = preset.SpeedPercent;
        BrightnessPercent = preset.BrightnessPercent;
        Direction = preset.Direction;
        Random = preset.Random;

        if (preset.PerKeyColors is { Count: KeyLayout.SlotCount } colors)
        {
            var result = await _s.Lighting.ApplyPerKeyAsync(colors, preset.BrightnessPercent);
            if (!result.Success) { _banner(BannerKind.Error, result.Error!); return; }
            _s.Settings.Lighting.PerKeyColors = colors.ToList();
            _s.Settings.Lighting.Effect = LightEffect.Custom;
            _s.Store.Save(_s.Settings);
            StatusText = $"{preset.Name} applied";
            return;
        }

        await ApplyEffectAsync();
    }

    [RelayCommand]
    private void SavePreset()
    {
        var name = $"Preset {_s.Settings.Lighting.Presets.Count + 1}";
        var preset = new LightingPreset
        {
            Name = name,
            Effect = SelectedEffect,
            Color = ToRgb(PickedColor),
            SecondColor = ToRgb(PickedSecondColor),
            SpeedPercent = SpeedPercent,
            BrightnessPercent = BrightnessPercent,
            Direction = Direction,
            Random = Random,
            PerKeyColors = SelectedEffect == LightEffect.Custom && _s.Settings.Lighting.PerKeyColors.Count == KeyLayout.SlotCount
                ? _s.Settings.Lighting.PerKeyColors.ToList()
                : null,
        };
        _s.Settings.Lighting.Presets.Add(preset);
        _s.Store.Save(_s.Settings);
        Presets.Add(preset);
        StatusText = $"Saved {name}";
    }

    [RelayCommand]
    private void DeletePreset(LightingPreset? preset)
    {
        if (preset is null) return;
        if (!_s.Settings.Lighting.Presets.Remove(preset)) return; // built-ins are not deletable
        _s.Store.Save(_s.Settings);
        Presets.Remove(preset);
        StatusText = $"Deleted {preset.Name}";
    }
}
```

Add to `MainViewModel`:

```csharp
    public LightingViewModel Lighting { get; }
```

constructed after `Battery`:

```csharp
        Lighting = new LightingViewModel(_s, SetBanner);
```

- [ ] **Step 2: Build**

Run: `dotnet build OpenAorus.sln`
Expected: succeeds. Remember the App project has both WPF and WinForms, so ambiguous
names (`Color`, `Application`, `MessageBox`, `Brushes`) must be aliased or fully
qualified — the `MediaColor` alias above exists for exactly that reason.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "Add lighting view model with effects, presets and persistence"
```

---

### Task 8: Lighting UI and per-key editor

**Files:**
- Create: `src/OpenAorus.App/ViewModels/PerKeyEditorViewModel.cs`, `src/OpenAorus.App/Views/LightingPanel.xaml(.cs)`, `src/OpenAorus.App/Views/KeyboardCanvas.xaml(.cs)`
- Modify: `src/OpenAorus.App/Views/MainWindow.xaml(.cs)`, `src/OpenAorus.App/ViewModels/LightingViewModel.cs`

**Interfaces:**
- Produces:
  - `partial class KeySlotViewModel : ObservableObject` — `int Slot`, `string Name`, `MediaColor Color`, `bool IsSelected`
  - `partial class PerKeyEditorViewModel : ObservableObject` — `ObservableCollection<KeySlotViewModel> Keys`, `MediaColor BrushColor`, `bool IsPainting`, commands `ApplyCommand`, `ReadFromKeyboardCommand`, `FillAllCommand`, `ClearCommand`, `PaintCommand(KeySlotViewModel)`
  - `LightingViewModel.PerKey` (`PerKeyEditorViewModel`)
  - `MainWindow` gains a Cooling | Lighting switch

The editor lists only real keys (`KeyLayout.RealKeys`), so unused slots cannot be
painted; the 128-slot array sent to the hardware is rebuilt from the full slot count
with black in the unused positions.

- [ ] **Step 1: Per-key editor view model**

`src/OpenAorus.App/ViewModels/PerKeyEditorViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAorus.Hardware.Lighting;
using MediaColor = System.Windows.Media.Color;

namespace OpenAorus.App.ViewModels;

public partial class KeySlotViewModel : ObservableObject
{
    [ObservableProperty] private MediaColor _color;
    public int Slot { get; }
    public string Name { get; }

    public KeySlotViewModel(int slot, string name, MediaColor color)
    {
        Slot = slot;
        Name = name;
        _color = color;
    }
}

public partial class PerKeyEditorViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Action<BannerKind, string> _banner;

    [ObservableProperty] private MediaColor _brushColor = MediaColor.FromRgb(0xFF, 0x7A, 0x1A);
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<KeySlotViewModel> Keys { get; } = new();

    public PerKeyEditorViewModel(AppServices services, Action<BannerKind, string> banner)
    {
        _s = services;
        _banner = banner;
        Reload(_s.Settings.Lighting.PerKeyColors);
    }

    private void Reload(IReadOnlyList<RgbColor> colors)
    {
        Keys.Clear();
        foreach (var (slot, name) in _s.Lighting.Layout.RealKeys)
        {
            var c = slot < colors.Count ? colors[slot] : RgbColor.Black;
            Keys.Add(new KeySlotViewModel(slot, name, MediaColor.FromRgb(c.R, c.G, c.B)));
        }
    }

    /// <summary>Expands the painted real keys back into the full 128-slot array the hardware expects.</summary>
    private RgbColor[] ToSlotArray()
    {
        var colors = new RgbColor[KeyLayout.SlotCount];
        foreach (var key in Keys)
            colors[key.Slot] = new RgbColor(key.Color.R, key.Color.G, key.Color.B);
        return colors;
    }

    [RelayCommand]
    private void Paint(KeySlotViewModel? key)
    {
        if (key is null) return;
        key.Color = BrushColor;
    }

    [RelayCommand]
    private void FillAll()
    {
        foreach (var key in Keys) key.Color = BrushColor;
    }

    [RelayCommand]
    private void Clear()
    {
        foreach (var key in Keys) key.Color = MediaColor.FromRgb(0, 0, 0);
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!_s.KeyboardPresent || IsBusy) return;
        IsBusy = true;
        try
        {
            var colors = ToSlotArray();
            var brightness = _s.Settings.Lighting.BrightnessPercent;
            var result = await _s.Lighting.ApplyPerKeyAsync(colors, brightness);
            if (!result.Success)
            {
                StatusText = "Failed";
                _banner(BannerKind.Error, result.Error!);
                return;
            }
            _s.Settings.Lighting.PerKeyColors = colors.ToList();
            _s.Settings.Lighting.Effect = LightEffect.Custom;
            _s.Store.Save(_s.Settings);
            StatusText = "Per-key colours applied";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ReadFromKeyboardAsync()
    {
        if (!_s.KeyboardPresent || IsBusy) return;
        IsBusy = true;
        try
        {
            var colors = await _s.Lighting.ReadPerKeyAsync();
            if (colors is null) { StatusText = "The keyboard did not answer"; return; }
            Reload(colors);
            StatusText = "Read the keyboard's current colours";
        }
        finally { IsBusy = false; }
    }
}
```

Add to `LightingViewModel`:

```csharp
    public PerKeyEditorViewModel PerKey { get; }
```

constructed in its constructor: `PerKey = new PerKeyEditorViewModel(services, banner);`

- [ ] **Step 2: Keyboard canvas**

`src/OpenAorus.App/Views/KeyboardCanvas.xaml` — an `ItemsControl` over `Keys` using a
`WrapPanel`, each key a bordered button showing its name over its colour, click bound
to `PaintCommand` with the key as parameter:

```xml
<UserControl x:Class="OpenAorus.App.Views.KeyboardCanvas"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <StackPanel>
        <DockPanel Margin="0,0,0,6">
            <Button DockPanel.Dock="Right" Content="Apply" Margin="6,0,0,0" Command="{Binding ApplyCommand}"/>
            <Button DockPanel.Dock="Right" Content="Read" Margin="6,0,0,0" Command="{Binding ReadFromKeyboardCommand}"/>
            <Button DockPanel.Dock="Right" Content="Clear" Margin="6,0,0,0" Command="{Binding ClearCommand}"/>
            <Button DockPanel.Dock="Right" Content="Fill" Command="{Binding FillAllCommand}"/>
            <TextBlock Text="{Binding StatusText}" VerticalAlignment="Center" Foreground="#FF8B919C"/>
        </DockPanel>
        <ScrollViewer VerticalScrollBarVisibility="Auto" MaxHeight="240">
            <ItemsControl ItemsSource="{Binding Keys}">
                <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Button Width="46" Height="30" Margin="2" Content="{Binding Name}" FontSize="9"
                                Command="{Binding DataContext.PaintCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                CommandParameter="{Binding}">
                            <Button.Background>
                                <SolidColorBrush Color="{Binding Color}"/>
                            </Button.Background>
                        </Button>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </ScrollViewer>
    </StackPanel>
</UserControl>
```

A `WrapPanel` of named keys is deliberately not a physical keyboard picture. It is
honest about the fact that the slot-to-key mapping is unverified, and it makes a wrong
mapping obvious the moment the owner paints one key and sees a different one light up.
A pixel-accurate keyboard image can come later, once the mapping is confirmed.

- [ ] **Step 3: Lighting panel**

`src/OpenAorus.App/Views/LightingPanel.xaml` — effect picker, the parameter controls
bound to the capability flags so unsupported controls hide themselves, brightness,
presets, and the per-key editor shown only when `SelectedEffect` is `Custom`:

```xml
<UserControl x:Class="OpenAorus.App.Views.LightingPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:app="clr-namespace:OpenAorus.App"
             xmlns:views="clr-namespace:OpenAorus.App.Views">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolVis"/>
        <app:EnumEqualsVisibilityConverter x:Key="EnumVis"/>
    </UserControl.Resources>
    <StackPanel IsEnabled="{Binding KeyboardPresent}">
        <TextBlock Style="{StaticResource Muted}" Text="{Binding DeviceLine}" Margin="0,0,0,6"/>
        <DockPanel Margin="0,0,0,6">
            <Button DockPanel.Dock="Right" Content="Apply" Margin="6,0,0,0" Command="{Binding ApplyEffectCommand}"/>
            <ComboBox ItemsSource="{Binding Effects}" SelectedItem="{Binding SelectedEffect}"/>
        </DockPanel>
        <DockPanel Margin="0,0,0,6" Visibility="{Binding SupportsSpeed, Converter={StaticResource BoolVis}}">
            <TextBlock DockPanel.Dock="Left" Width="70" Text="Speed"/>
            <Slider Minimum="0" Maximum="100" TickFrequency="10" Value="{Binding SpeedPercent}"/>
        </DockPanel>
        <DockPanel Margin="0,0,0,6">
            <TextBlock DockPanel.Dock="Left" Width="70" Text="Brightness"/>
            <Slider Minimum="0" Maximum="100" TickFrequency="10" Value="{Binding BrightnessPercent}"/>
        </DockPanel>
        <DockPanel Margin="0,0,0,6" Visibility="{Binding SupportsDirection, Converter={StaticResource BoolVis}}">
            <TextBlock DockPanel.Dock="Left" Width="70" Text="Direction"/>
            <ComboBox ItemsSource="{Binding Directions}" SelectedItem="{Binding Direction}"/>
        </DockPanel>
        <CheckBox Content="Random colour" IsChecked="{Binding Random}" Margin="0,0,0,6"
                  Visibility="{Binding SupportsRandom, Converter={StaticResource BoolVis}}"/>
        <ItemsControl ItemsSource="{Binding Presets}" Margin="0,0,0,6">
            <ItemsControl.ItemsPanel><ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate></ItemsControl.ItemsPanel>
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <Button Content="{Binding Name}" Margin="0,0,4,4"
                            Command="{Binding DataContext.ApplyPresetCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                            CommandParameter="{Binding}"/>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
        <Button Content="Save current as preset" HorizontalAlignment="Left" Command="{Binding SavePresetCommand}" Margin="0,0,0,8"/>
        <views:KeyboardCanvas DataContext="{Binding PerKey}"
                              Visibility="{Binding DataContext.SelectedEffect, RelativeSource={RelativeSource AncestorType=UserControl}, Converter={StaticResource EnumVis}, ConverterParameter=Custom}"/>
        <TextBlock Style="{StaticResource Muted}" Text="{Binding StatusText}" Margin="0,6,0,0"/>
    </StackPanel>
</UserControl>
```

Colour pickers: WPF ships none. Use two `ComboBox`es of named swatches bound to
`PickedColor` / `PickedSecondColor` via a small `ObservableCollection<MediaColor>` of
about a dozen colours, plus a hex `TextBox`. Do not add a NuGet colour-picker package;
the whole point of this app is that it is small.

- [ ] **Step 4: Main window switch**

Add a two-button segmented control above the existing content that toggles a
`SelectedSection` enum on `MainViewModel` (`Cooling` / `Lighting`), and place the
existing cooling content and the new `LightingPanel` in the same grid cell with their
visibility bound to it. Hide the Lighting button entirely when
`MainViewModel.Lighting.KeyboardPresent` is false. Grow `MainWindow` height to 640.

- [ ] **Step 5: Build**

Run: `dotnet build OpenAorus.sln -c Release`
Expected: succeeds.

- [ ] **Step 6: OWNER VERIFY (deferred — append to VERIFY.md, do not block on it)**

Add a "Keyboard lighting" section to `VERIFY.md` covering: every effect visibly matches
its name; brightness changes without changing the effect; painting a single key lights
that same key (this is what settles the US versus UK slot order); Read pulls the
keyboard's current colours back; presets apply; the chosen effect returns after a
reboot and after resume.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add lighting panel, per-key editor and section switch"
```

---

### Task 9: Documentation and release notes

**Files:**
- Modify: `README.md`, `VERIFY.md`
- Create: `docs/research/ione-keyboard-protocol.md` (if not already committed with the spec)

- [ ] **Step 1: README**

Add keyboard lighting to the feature list, note that lighting needs no administrator
rights, name the supported keyboard ids (`1044`/`0414` with `7A3C`/`7A3D`/`7A3F`), and
credit `rcassani/keyboard-fusion-rgb` for the protocol cross-check under a Credits
heading. State plainly that the per-key slot mapping is unverified on hardware and that
reports from other models are welcome.

- [ ] **Step 2: Verify checklist**

Confirm the keyboard section added in Task 8 is present and complete.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "Document keyboard lighting support"
```

---

## Owner acceptance checklist (end of v0.2)

- [ ] Every effect in the picker visibly matches its name
- [ ] Brightness changes take effect without switching effect
- [ ] Painting one key lights that same key, confirming the slot order
- [ ] Read from keyboard returns the colours currently stored
- [ ] Presets apply, save and delete
- [ ] Lighting returns after a reboot and after sleep or resume
- [ ] Nothing in the fan, sensor or battery behaviour regressed

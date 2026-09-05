# OpenAorus v0.1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A single-exe tray app that replaces Gigabyte Control Center's fan, sensor and battery features on the AORUS 17G KD (and, untested, other Gigabyte laptops).

**Architecture:** `OpenAorus.Hardware` talks to the EC through one `IGigabyteWmi` seam (real impl = `System.Management` calls to `root\WMI:GB_WMIACPI_Get/Set`), and holds all protocol logic, settings and takeover bookkeeping so it is fully unit-testable with a recording fake. `OpenAorus.App` (WPF + WinForms `NotifyIcon`) is a thin shell: elevation bootstrap, tray, one compact window, view models. Every fan mode is the exact WMI call sequence GCC issues, pinned by tests.

**Tech Stack:** .NET 8 (`net8.0-windows`), C# 12, WPF, `System.Management` 8.0.0, `System.ServiceProcess.ServiceController` 8.0.1, `CommunityToolkit.Mvvm` 8.4.0, xUnit. Built with the .NET 10 SDK already on the machine (`dotnet --version` → 10.0.100).

**Spec:** `docs/superpowers/specs/2026-09-06-openaorus-v0.1-design.md`

## Global Constraints

- Target framework `net8.0-windows` for all three projects; `LangVersion` default; `Nullable` and `ImplicitUsings` enabled.
- No direct `System.Management` use outside `src/OpenAorus.Hardware/GigabyteWmi.cs` and `SystemInfo.cs`.
- All EC writes go through `FanController`/`BatteryController`; the App never calls `IGigabyteWmi.Invoke` itself.
- Duty values written to the EC are `0..DutyMax` (`DutyMax = 229` on AORUS 17G KD). The UI only ever shows percent.
- 500 ms pause between consecutive writes inside one fan-mode sequence (GCC does this).
- v0.1 never calls light-bar, RGB, `SetNvPowerConfig`, `SetPEG2orSG2`, `SetAIBoostStatus`, `SetDynamicBoostStatus`, `SetTppStatus` or any keyboard HID path.
- Settings live in `%LOCALAPPDATA%\OpenAorus\settings.json`.
- Every commit message ends with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` (repo convention).
- **The assistant's shell cannot elevate.** Any step marked `OWNER VERIFY` must be run by the repo owner in an elevated session; the executor must stop and ask, not fake the result.
- Run tests with `dotnet test OpenAorus.sln` from the repo root; build with `dotnet build OpenAorus.sln -c Release`.

## File Structure

```
OpenAorus.sln
Directory.Build.props                                  shared: net8.0-windows, nullable, implicit usings
src/OpenAorus.Hardware/OpenAorus.Hardware.csproj       System.Management, ServiceController packages
src/OpenAorus.Hardware/Wmi/IGigabyteWmi.cs             seam: Invoke(WmiClass, method, args) → WmiResult
src/OpenAorus.Hardware/Wmi/WmiResult.cs                result record + typed getters
src/OpenAorus.Hardware/Wmi/GigabyteWmi.cs              real System.Management implementation
src/OpenAorus.Hardware/Wmi/SystemInfo.cs               SMBIOS product name via Win32_ComputerSystemProduct
src/OpenAorus.Hardware/Profiles/ModelProfile.cs        capability table, Detect(), duty<->percent
src/OpenAorus.Hardware/Fans/FanMode.cs                 enum
src/OpenAorus.Hardware/Fans/FanCurve.cs                points, Default, Validate()
src/OpenAorus.Hardware/Fans/FanController.cs           mode sequences, serialized writes
src/OpenAorus.Hardware/Sensors/SensorReader.cs         temps, RPM (byte swap), duty %
src/OpenAorus.Hardware/Battery/BatteryController.cs    policy 0/4 + stop 60..100
src/OpenAorus.Hardware/Diagnostics/DiagnosticsDump.cs  every Get* → text
src/OpenAorus.Hardware/Config/AppSettings.cs           POCO
src/OpenAorus.Hardware/Config/SettingsStore.cs         JSON load/save, .bad on corruption
src/OpenAorus.Hardware/System/IGccSystem.cs            OS actions seam (task, run key, service, processes)
src/OpenAorus.Hardware/System/GccTakeover.cs           takeover/restore bookkeeping (pure)
src/OpenAorus.Hardware/System/WindowsGccSystem.cs      real schtasks / registry / sc / Process impl
src/OpenAorus.Hardware/System/StartupTask.cs           schtasks logon task for OpenAorus itself
src/OpenAorus.Hardware/System/Elevation.cs             IsElevated / RelaunchElevated
src/OpenAorus.App/OpenAorus.App.csproj                 UseWPF + UseWindowsForms, CommunityToolkit.Mvvm
src/OpenAorus.App/App.xaml(.cs)                        startup: args, elevation, services, CLI modes
src/OpenAorus.App/AppServices.cs                       composition root
src/OpenAorus.App/TrayIcon.cs                          NotifyIcon, menu, tooltip, generated icon
src/OpenAorus.App/ViewModels/MainViewModel.cs          modes, sensors, banner, polling
src/OpenAorus.App/ViewModels/CurveEditorViewModel.cs   editable points + validation
src/OpenAorus.App/ViewModels/BatteryViewModel.cs
src/OpenAorus.App/ViewModels/SettingsViewModel.cs      start with Windows, takeover, poll interval
src/OpenAorus.App/Views/MainWindow.xaml(.cs)           single compact window
src/OpenAorus.App/Views/CurveEditor.xaml(.cs)          canvas drag + table
src/OpenAorus.App/Themes/Dark.xaml                     colors, button styles
tests/OpenAorus.Hardware.Tests/OpenAorus.Hardware.Tests.csproj
tests/OpenAorus.Hardware.Tests/FakeGigabyteWmi.cs      records calls, scripted responses
tests/OpenAorus.Hardware.Tests/*Tests.cs               one file per Hardware class
.github/workflows/build.yml                            build + test on push, publish on v* tags
```

Deviation from the spec's sketch: `Settings`, `GccTakeover`, `StartupTask` and `Elevation` live in `OpenAorus.Hardware` (namespace `OpenAorus.Hardware.*`) instead of `OpenAorus.App`, so they are unit-testable without referencing the WPF exe. The rule "Hardware has no UI references" still holds.

---

### Task 1: Solution scaffold

**Files:**
- Create: `Directory.Build.props`, `OpenAorus.sln`
- Create: `src/OpenAorus.Hardware/OpenAorus.Hardware.csproj`
- Create: `src/OpenAorus.App/OpenAorus.App.csproj`, `src/OpenAorus.App/App.xaml`, `src/OpenAorus.App/App.xaml.cs`
- Create: `tests/OpenAorus.Hardware.Tests/OpenAorus.Hardware.Tests.csproj`, `tests/OpenAorus.Hardware.Tests/SmokeTests.cs`

**Interfaces:**
- Produces: three projects wired into `OpenAorus.sln`; `dotnet test` runs one passing test.

- [ ] **Step 1: Create the projects with the dotnet CLI**

Run from `C:\Users\slobb\dev\OpenAorus`:

```bash
dotnet new sln -n OpenAorus
dotnet new classlib -n OpenAorus.Hardware -o src/OpenAorus.Hardware --framework net8.0
dotnet new wpf -n OpenAorus.App -o src/OpenAorus.App --framework net8.0
dotnet new xunit -n OpenAorus.Hardware.Tests -o tests/OpenAorus.Hardware.Tests --framework net8.0
dotnet sln add src/OpenAorus.Hardware src/OpenAorus.App tests/OpenAorus.Hardware.Tests
dotnet add src/OpenAorus.App reference src/OpenAorus.Hardware
dotnet add tests/OpenAorus.Hardware.Tests reference src/OpenAorus.Hardware
dotnet add src/OpenAorus.Hardware package System.Management --version 8.0.0
dotnet add src/OpenAorus.Hardware package System.ServiceProcess.ServiceController --version 8.0.1
dotnet add src/OpenAorus.App package CommunityToolkit.Mvvm --version 8.4.0
rm src/OpenAorus.Hardware/Class1.cs tests/OpenAorus.Hardware.Tests/UnitTest1.cs
```

- [ ] **Step 2: Write `Directory.Build.props`** (forces `net8.0-windows` everywhere so `Registry`, `ServiceController` and `System.Management` compile without CA1416 noise)

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <Version>0.1.0</Version>
    <Authors>OpenAorus contributors</Authors>
    <RepositoryUrl>https://github.com/CHEYSOFF/OpenAorus</RepositoryUrl>
  </PropertyGroup>
</Project>
```

Then delete the `<TargetFramework>` line from each of the three generated `.csproj` files so the props file wins.

- [ ] **Step 3: Set App csproj to WPF + WinForms + single exe**

Replace `src/OpenAorus.App/OpenAorus.App.csproj` content with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <AssemblyName>OpenAorus</AssemblyName>
    <RootNamespace>OpenAorus.App</RootNamespace>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>false</SelfContained>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
    <ProjectReference Include="..\OpenAorus.Hardware\OpenAorus.Hardware.csproj" />
  </ItemGroup>
</Project>
```

Create `src/OpenAorus.App/app.manifest` (asInvoker, we self-relaunch with runas instead of forcing UAC on every start; DPI aware):

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="0.1.0.0" name="OpenAorus"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

- [ ] **Step 4: Write a smoke test**

`tests/OpenAorus.Hardware.Tests/SmokeTests.cs`:

```csharp
namespace OpenAorus.Hardware.Tests;

public class SmokeTests
{
    [Fact]
    public void Solution_builds_and_tests_run() => Assert.True(true);
}
```

- [ ] **Step 5: Build and test**

Run: `dotnet build OpenAorus.sln && dotnet test OpenAorus.sln`
Expected: build succeeds (WPF template's `MainWindow` still present, fine for now), `Passed! - Failed: 0, Passed: 1`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Scaffold solution: Hardware lib, WPF app, xunit tests

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: WMI seam, result type, recording fake, real implementation

**Files:**
- Create: `src/OpenAorus.Hardware/Wmi/IGigabyteWmi.cs`, `src/OpenAorus.Hardware/Wmi/WmiResult.cs`, `src/OpenAorus.Hardware/Wmi/GigabyteWmi.cs`, `src/OpenAorus.Hardware/Wmi/SystemInfo.cs`
- Create: `tests/OpenAorus.Hardware.Tests/FakeGigabyteWmi.cs`, `tests/OpenAorus.Hardware.Tests/WmiResultTests.cs`

**Interfaces:**
- Produces:
  - `enum WmiClass { Get, Set }`
  - `interface IGigabyteWmi { WmiResult Invoke(WmiClass cls, string method, IReadOnlyDictionary<string, object>? args = null); }`
  - `sealed record WmiResult(bool Success, IReadOnlyDictionary<string, object> Out, string? Error)` with `static Ok(...)`, `static Fail(string)`, `int GetInt(string name, int fallback = 0)`
  - extension `WmiResult SetData(this IGigabyteWmi wmi, string method, byte data)` and `WmiResult Get(this IGigabyteWmi wmi, string method)`
  - `static string? SystemInfo.GetProductName()`
  - test helper `FakeGigabyteWmi` with `List<WmiCall> Calls`, `Dictionary<string, Dictionary<string, object>> Responses`, `HashSet<string> FailOn`, and `sealed record WmiCall(WmiClass Class, string Method, IReadOnlyDictionary<string, object> Args)`

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/FakeGigabyteWmi.cs`:

```csharp
using OpenAorus.Hardware.Wmi;

namespace OpenAorus.Hardware.Tests;

public sealed record WmiCall(WmiClass Class, string Method, IReadOnlyDictionary<string, object> Args)
{
    public int Data => Convert.ToInt32(Args["Data"]);
    public override string ToString() =>
        $"{Class}.{Method}({string.Join(",", Args.Select(kv => $"{kv.Key}={kv.Value}"))})";
}

public sealed class FakeGigabyteWmi : IGigabyteWmi
{
    public List<WmiCall> Calls { get; } = new();
    public Dictionary<string, Dictionary<string, object>> Responses { get; } = new();
    public HashSet<string> FailOn { get; } = new();

    public WmiResult Invoke(WmiClass cls, string method, IReadOnlyDictionary<string, object>? args = null)
    {
        Calls.Add(new WmiCall(cls, method, args ?? new Dictionary<string, object>()));
        if (FailOn.Contains(method)) return WmiResult.Fail($"{method} failed (fake)");
        return Responses.TryGetValue(method, out var o) ? WmiResult.Ok(o) : WmiResult.Ok();
    }

    public void Respond(string method, object data) =>
        Responses[method] = new Dictionary<string, object> { ["Data"] = data };

    public IEnumerable<string> MethodsCalled => Calls.Select(c => c.Method);
}
```

`tests/OpenAorus.Hardware.Tests/WmiResultTests.cs`:

```csharp
using OpenAorus.Hardware.Wmi;

namespace OpenAorus.Hardware.Tests;

public class WmiResultTests
{
    [Fact]
    public void GetInt_converts_byte_and_ushort_outputs()
    {
        var r = WmiResult.Ok(new Dictionary<string, object> { ["Data"] = (byte)229, ["Value"] = (ushort)700 });
        Assert.Equal(229, r.GetInt("Data"));
        Assert.Equal(700, r.GetInt("Value"));
    }

    [Fact]
    public void GetInt_returns_fallback_when_missing_or_failed()
    {
        Assert.Equal(-1, WmiResult.Ok().GetInt("Data", -1));
        Assert.Equal(-1, WmiResult.Fail("boom").GetInt("Data", -1));
    }

    [Fact]
    public void SetData_extension_sends_byte_named_Data_to_Set_class()
    {
        var fake = new FakeGigabyteWmi();
        fake.SetData("SetFixedFanSpeed", 100);
        var call = Assert.Single(fake.Calls);
        Assert.Equal(WmiClass.Set, call.Class);
        Assert.Equal("SetFixedFanSpeed", call.Method);
        Assert.Equal((byte)100, call.Args["Data"]);
    }

    [Fact]
    public void Get_extension_calls_Get_class_with_no_args()
    {
        var fake = new FakeGigabyteWmi();
        fake.Respond("getCpuTemp", (ushort)61);
        Assert.Equal(61, fake.Get("getCpuTemp").GetInt("Data"));
        Assert.Equal(WmiClass.Get, fake.Calls[0].Class);
        Assert.Empty(fake.Calls[0].Args);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln`
Expected: compile errors, `IGigabyteWmi`/`WmiResult` not found.

- [ ] **Step 3: Implement the seam and result type**

`src/OpenAorus.Hardware/Wmi/IGigabyteWmi.cs`:

```csharp
namespace OpenAorus.Hardware.Wmi;

public enum WmiClass { Get, Set }

/// <summary>The only door to the embedded controller. Real impl: <see cref="GigabyteWmi"/>.</summary>
public interface IGigabyteWmi
{
    WmiResult Invoke(WmiClass cls, string method, IReadOnlyDictionary<string, object>? args = null);
}

public static class GigabyteWmiExtensions
{
    public static WmiResult SetData(this IGigabyteWmi wmi, string method, byte data) =>
        wmi.Invoke(WmiClass.Set, method, new Dictionary<string, object> { ["Data"] = data });

    public static WmiResult Get(this IGigabyteWmi wmi, string method) =>
        wmi.Invoke(WmiClass.Get, method);
}
```

`src/OpenAorus.Hardware/Wmi/WmiResult.cs`:

```csharp
namespace OpenAorus.Hardware.Wmi;

public sealed record WmiResult(bool Success, IReadOnlyDictionary<string, object> Out, string? Error)
{
    private static readonly IReadOnlyDictionary<string, object> Empty = new Dictionary<string, object>();

    public static WmiResult Ok(IReadOnlyDictionary<string, object>? outParams = null) => new(true, outParams ?? Empty, null);
    public static WmiResult Fail(string error) => new(false, Empty, error);

    public int GetInt(string name, int fallback = 0)
    {
        if (!Success || !Out.TryGetValue(name, out var v) || v is null) return fallback;
        try { return Convert.ToInt32(v); } catch (Exception) { return fallback; }
    }
}
```

- [ ] **Step 4: Implement the real WMI client and SystemInfo** (not unit-tested; verified by the owner in Task 8's `--dump`)

`src/OpenAorus.Hardware/Wmi/GigabyteWmi.cs`:

```csharp
using System.Management;

namespace OpenAorus.Hardware.Wmi;

/// <summary>Calls GB_WMIACPI_Get / GB_WMIACPI_Set in root\WMI. Requires an elevated process.</summary>
public sealed class GigabyteWmi : IGigabyteWmi
{
    private const string ScopePath = @"root\WMI";
    private readonly object _lock = new();

    public WmiResult Invoke(WmiClass cls, string method, IReadOnlyDictionary<string, object>? args = null)
    {
        var className = cls == WmiClass.Get ? "GB_WMIACPI_Get" : "GB_WMIACPI_Set";
        lock (_lock)
        {
            try
            {
                var options = new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate,
                };
                var scope = new ManagementScope(ScopePath, options);
                scope.Connect();
                using var mc = new ManagementClass(scope, new ManagementPath(className), null);
                using var instances = mc.GetInstances();
                foreach (ManagementObject instance in instances)
                {
                    using (instance)
                    {
                        using var inParams = instance.GetMethodParameters(method);
                        if (args is not null)
                            foreach (var (k, v) in args) inParams[k] = v;
                        using var outParams = instance.InvokeMethod(method, inParams, null);
                        var dict = new Dictionary<string, object>();
                        if (outParams is not null)
                            foreach (var p in outParams.Properties)
                                if (p.Value is not null) dict[p.Name] = p.Value;
                        return WmiResult.Ok(dict);
                    }
                }
                return WmiResult.Fail($"No instance of {className}. Is Gigabyte Control Center's WMI schema (acpimof.dll) installed?");
            }
            catch (ManagementException ex) { return WmiResult.Fail($"{className}.{method}: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { return WmiResult.Fail($"{className}.{method}: access denied ({ex.Message}). Run elevated."); }
            catch (Exception ex) { return WmiResult.Fail($"{className}.{method}: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
```

`src/OpenAorus.Hardware/Wmi/SystemInfo.cs`:

```csharp
using System.Management;

namespace OpenAorus.Hardware.Wmi;

public static class SystemInfo
{
    /// <summary>SMBIOS product name, e.g. "AORUS 17G KD". Null if WMI is unavailable.</summary>
    public static string? GetProductName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_ComputerSystemProduct");
            foreach (ManagementObject o in searcher.Get())
                using (o) return o["Name"]?.ToString()?.Trim();
        }
        catch (Exception) { }
        return null;
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: 5 passed.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add IGigabyteWmi seam, WmiResult, System.Management client and test fake

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Model profiles and duty scaling

**Files:**
- Create: `src/OpenAorus.Hardware/Profiles/ModelProfile.cs`
- Test: `tests/OpenAorus.Hardware.Tests/ModelProfileTests.cs`

**Interfaces:**
- Produces:
  - `enum ProfileStatus { Tested, Untested, Unknown }`
  - `sealed record ModelProfile(string Name, ProfileStatus Status, int DutyMax, int FanCount, bool HasGpuTemp1, bool RpmByteSwapped)`
  - `static ModelProfile ModelProfile.Detect(string? productName)`
  - `byte ToDuty(int percent)`, `int ToPercent(int duty)`
  - `bool CanWrite => Status != ProfileStatus.Unknown`

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/ModelProfileTests.cs`:

```csharp
using OpenAorus.Hardware.Profiles;

namespace OpenAorus.Hardware.Tests;

public class ModelProfileTests
{
    [Fact]
    public void Aorus_17G_KD_is_tested_with_duty_max_229()
    {
        var p = ModelProfile.Detect("AORUS 17G KD");
        Assert.Equal(ProfileStatus.Tested, p.Status);
        Assert.Equal(229, p.DutyMax);
        Assert.Equal(2, p.FanCount);
        Assert.True(p.RpmByteSwapped);
        Assert.True(p.CanWrite);
    }

    [Theory]
    [InlineData("AORUS 15P XD")]
    [InlineData("AERO 16 KE5")]
    [InlineData("GIGABYTE GAMING A16")]
    [InlineData("aorus 17x ye5")]
    public void Gigabyte_family_names_are_untested_but_writable(string name)
    {
        var p = ModelProfile.Detect(name);
        Assert.Equal(ProfileStatus.Untested, p.Status);
        Assert.True(p.CanWrite);
        Assert.Equal(229, p.DutyMax);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ROG Zephyrus G14")]
    public void Other_names_are_unknown_and_read_only(string? name)
    {
        var p = ModelProfile.Detect(name);
        Assert.Equal(ProfileStatus.Unknown, p.Status);
        Assert.False(p.CanWrite);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 115)]   // 114.5 rounds away from zero
    [InlineData(100, 229)]
    [InlineData(150, 229)]  // clamped
    [InlineData(-5, 0)]     // clamped
    public void ToDuty_scales_percent_to_229(int percent, int duty)
    {
        var p = ModelProfile.Detect("AORUS 17G KD");
        Assert.Equal((byte)duty, p.ToDuty(percent));
    }

    [Theory]
    [InlineData(229, 100)]
    [InlineData(115, 50)]
    [InlineData(0, 0)]
    [InlineData(255, 100)]  // clamped
    public void ToPercent_scales_duty_to_percent(int duty, int percent)
    {
        var p = ModelProfile.Detect("AORUS 17G KD");
        Assert.Equal(percent, p.ToPercent(duty));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln`
Expected: compile error, `ModelProfile` not found.

- [ ] **Step 3: Implement**

`src/OpenAorus.Hardware/Profiles/ModelProfile.cs`:

```csharp
namespace OpenAorus.Hardware.Profiles;

public enum ProfileStatus { Tested, Untested, Unknown }

/// <summary>Per-model capability table. Add a tested entry once an owner confirms duty read-back matches GCC.</summary>
public sealed record ModelProfile(
    string Name,
    ProfileStatus Status,
    int DutyMax,
    int FanCount,
    bool HasGpuTemp1,
    bool RpmByteSwapped)
{
    private static readonly string[] FamilyPrefixes = { "AORUS", "AERO", "GIGABYTE" };

    private static readonly Dictionary<string, ModelProfile> Tested = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AORUS 17G KD"] = new("AORUS 17G KD", ProfileStatus.Tested, DutyMax: 229, FanCount: 2, HasGpuTemp1: true, RpmByteSwapped: true),
    };

    public bool CanWrite => Status != ProfileStatus.Unknown;

    public static ModelProfile Detect(string? productName)
    {
        var name = (productName ?? string.Empty).Trim();
        if (Tested.TryGetValue(name, out var tested)) return tested;
        if (FamilyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return new ModelProfile(name, ProfileStatus.Untested, 229, 2, true, true);
        return new ModelProfile(name.Length == 0 ? "(unknown)" : name, ProfileStatus.Unknown, 229, 2, true, true);
    }

    public byte ToDuty(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        return (byte)Math.Round(clamped * DutyMax / 100.0, MidpointRounding.AwayFromZero);
    }

    public int ToPercent(int duty)
    {
        var clamped = Math.Clamp(duty, 0, DutyMax);
        return (int)Math.Round(clamped * 100.0 / DutyMax, MidpointRounding.AwayFromZero);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add ModelProfile detection and duty/percent scaling

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Fan curve model and validation

**Files:**
- Create: `src/OpenAorus.Hardware/Fans/FanMode.cs`, `src/OpenAorus.Hardware/Fans/FanCurve.cs`
- Test: `tests/OpenAorus.Hardware.Tests/FanCurveTests.cs`

**Interfaces:**
- Produces:
  - `enum FanMode { Quiet, Normal, Gaming, Turbo, Fixed, Custom }`
  - `readonly record struct FanCurvePoint(int Temperature, int DutyPercent)`
  - `sealed class FanCurve` with `IReadOnlyList<FanCurvePoint> Points`, `const int MaxPoints = 15`, `const int MinPoints = 2`, `static FanCurve Default`, `IReadOnlyList<string> Validate()`, `bool IsValid`, constructor `FanCurve(IEnumerable<FanCurvePoint>)`

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/FanCurveTests.cs`:

```csharp
using OpenAorus.Hardware.Fans;

namespace OpenAorus.Hardware.Tests;

public class FanCurveTests
{
    [Fact]
    public void Default_curve_is_valid_and_ends_at_full_duty()
    {
        Assert.True(FanCurve.Default.IsValid);
        Assert.Equal(100, FanCurve.Default.Points[^1].DutyPercent);
        Assert.InRange(FanCurve.Default.Points.Count, 2, 15);
    }

    [Fact]
    public void Too_few_points_is_invalid()
    {
        var c = new FanCurve(new[] { new FanCurvePoint(50, 40) });
        Assert.Contains(c.Validate(), e => e.Contains("at least 2"));
    }

    [Fact]
    public void More_than_15_points_is_invalid()
    {
        var pts = Enumerable.Range(0, 16).Select(i => new FanCurvePoint(20 + i * 5, 20 + i * 5));
        Assert.Contains(new FanCurve(pts).Validate(), e => e.Contains("at most 15"));
    }

    [Fact]
    public void Temperatures_must_strictly_increase()
    {
        var c = new FanCurve(new[] { new FanCurvePoint(50, 30), new FanCurvePoint(50, 40) });
        Assert.Contains(c.Validate(), e => e.Contains("increasing"));
    }

    [Fact]
    public void Duty_must_not_decrease()
    {
        var c = new FanCurve(new[] { new FanCurvePoint(40, 50), new FanCurvePoint(60, 40) });
        Assert.Contains(c.Validate(), e => e.Contains("decrease"));
    }

    [Fact]
    public void Values_outside_ranges_are_invalid()
    {
        var c = new FanCurve(new[] { new FanCurvePoint(-1, 0), new FanCurvePoint(101, 120) });
        Assert.Contains(c.Validate(), e => e.Contains("0-100"));
    }

    [Fact]
    public void Valid_curve_has_no_errors()
    {
        var c = new FanCurve(new[] { new FanCurvePoint(40, 30), new FanCurvePoint(70, 60), new FanCurvePoint(90, 100) });
        Assert.Empty(c.Validate());
        Assert.True(c.IsValid);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln`
Expected: compile error, `FanCurve` not found.

- [ ] **Step 3: Implement**

`src/OpenAorus.Hardware/Fans/FanMode.cs`:

```csharp
namespace OpenAorus.Hardware.Fans;

public enum FanMode { Quiet, Normal, Gaming, Turbo, Fixed, Custom }
```

`src/OpenAorus.Hardware/Fans/FanCurve.cs`:

```csharp
namespace OpenAorus.Hardware.Fans;

public readonly record struct FanCurvePoint(int Temperature, int DutyPercent);

/// <summary>Temperature→duty table that the EC runs by itself (SetFanIndexValue slots 0..14).</summary>
public sealed class FanCurve
{
    public const int MinPoints = 2;
    public const int MaxPoints = 15;

    public IReadOnlyList<FanCurvePoint> Points { get; }

    public FanCurve(IEnumerable<FanCurvePoint> points) => Points = points.ToList();

    public static FanCurve Default { get; } = new(new[]
    {
        new FanCurvePoint(40, 25),
        new FanCurvePoint(50, 30),
        new FanCurvePoint(60, 40),
        new FanCurvePoint(70, 55),
        new FanCurvePoint(80, 75),
        new FanCurvePoint(90, 100),
    });

    public bool IsValid => Validate().Count == 0;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Points.Count < MinPoints) errors.Add($"Curve needs at least {MinPoints} points.");
        if (Points.Count > MaxPoints) errors.Add($"Curve can have at most {MaxPoints} points.");
        for (var i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            if (p.Temperature is < 0 or > 100 || p.DutyPercent is < 0 or > 100)
                errors.Add($"Point {i + 1}: temperature and duty must be 0-100.");
            if (i > 0)
            {
                if (p.Temperature <= Points[i - 1].Temperature)
                    errors.Add($"Point {i + 1}: temperatures must be strictly increasing.");
                if (p.DutyPercent < Points[i - 1].DutyPercent)
                    errors.Add($"Point {i + 1}: duty must not decrease.");
            }
        }
        return errors;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add FanMode and FanCurve with validation

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: FanController mode sequences

**Files:**
- Create: `src/OpenAorus.Hardware/Fans/FanController.cs`
- Modify: `docs/superpowers/specs/2026-09-06-openaorus-v0.1-design.md` (§2.2 Custom order)
- Test: `tests/OpenAorus.Hardware.Tests/FanControllerTests.cs`

**Interfaces:**
- Consumes: `IGigabyteWmi`, `ModelProfile`, `FanMode`, `FanCurve`, `WmiResult`
- Produces:
  - `sealed class FanController(IGigabyteWmi wmi, ModelProfile profile, Func<int, Task>? delay = null)`
  - `Task<WmiResult> ApplyAsync(FanMode mode, int fixedPercent = 50, FanCurve? curve = null, CancellationToken ct = default)`
  - `const int StepDelayMs = 500`
  - `FanMode? LastApplied { get; }`

Protocol (mirrors GCC `ucNotebook.Helper.FanControl` and `applyFANprofile`): every sequence begins `SetCurrentFanStep(0)`, then flags, then duties. Custom enables step mode **before** writing points (GCC `applyFANprofile` method 4 order); the spec's §2.2 sentence "points → Step=1" is corrected in this task.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/FanControllerTests.cs`:

```csharp
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Wmi;

namespace OpenAorus.Hardware.Tests;

public class FanControllerTests
{
    private static readonly ModelProfile Kd = ModelProfile.Detect("AORUS 17G KD");
    private static Task NoDelay(int _) => Task.CompletedTask;

    private static (FanController ctl, FakeGigabyteWmi wmi) Make(ModelProfile? p = null)
    {
        var wmi = new FakeGigabyteWmi();
        return (new FanController(wmi, p ?? Kd, NoDelay), wmi);
    }

    private static string[] Seq(FakeGigabyteWmi wmi) =>
        wmi.Calls.Select(c => c.Args.Count == 1 ? $"{c.Method}={c.Data}" : c.ToString()).ToArray();

    [Fact]
    public async Task Quiet_sets_only_NvThermalTarget()
    {
        var (ctl, wmi) = Make();
        var r = await ctl.ApplyAsync(FanMode.Quiet);
        Assert.True(r.Success);
        Assert.Equal(new[]
        {
            "SetCurrentFanStep=0", "SetFixedFanStatus=0", "SetStepFanStatus=0",
            "SetAutoFanStatus=0", "SetNvThermalTarget=1",
        }, Seq(wmi));
        Assert.All(wmi.Calls, c => Assert.Equal(WmiClass.Set, c.Class));
    }

    [Fact]
    public async Task Normal_clears_every_flag()
    {
        var (ctl, wmi) = Make();
        await ctl.ApplyAsync(FanMode.Normal);
        Assert.Equal(new[]
        {
            "SetCurrentFanStep=0", "SetFixedFanStatus=0", "SetStepFanStatus=0",
            "SetAutoFanStatus=0", "SetNvThermalTarget=0",
        }, Seq(wmi));
    }

    [Fact]
    public async Task Gaming_enables_auto_fan()
    {
        var (ctl, wmi) = Make();
        await ctl.ApplyAsync(FanMode.Gaming);
        Assert.Equal(new[]
        {
            "SetCurrentFanStep=0", "SetFixedFanStatus=0", "SetStepFanStatus=0",
            "SetAutoFanStatus=1", "SetNvThermalTarget=0",
        }, Seq(wmi));
    }

    [Fact]
    public async Task Turbo_writes_max_duty_then_enables_fixed_and_step()
    {
        var (ctl, wmi) = Make();
        await ctl.ApplyAsync(FanMode.Turbo);
        Assert.Equal(new[]
        {
            "SetCurrentFanStep=0", "SetAutoFanStatus=0", "SetNvThermalTarget=0",
            "SetFixedFanSpeed=229", "SetGPUFanDuty=229",
            "SetStepFanStatus=1", "SetFixedFanStatus=1",
        }, Seq(wmi));
    }

    [Fact]
    public async Task Fixed_scales_percent_to_profile_duty()
    {
        var (ctl, wmi) = Make();
        await ctl.ApplyAsync(FanMode.Fixed, fixedPercent: 50);
        Assert.Equal(new[]
        {
            "SetCurrentFanStep=0", "SetAutoFanStatus=0", "SetNvThermalTarget=0",
            "SetFixedFanSpeed=115", "SetGPUFanDuty=115",
            "SetStepFanStatus=1", "SetFixedFanStatus=1",
        }, Seq(wmi));
    }

    [Fact]
    public async Task Custom_enables_step_mode_then_writes_points_and_terminator()
    {
        var (ctl, wmi) = Make();
        var curve = new FanCurve(new[] { new FanCurvePoint(40, 30), new FanCurvePoint(80, 100) });
        var r = await ctl.ApplyAsync(FanMode.Custom, curve: curve);
        Assert.True(r.Success);
        Assert.Equal(new[]
        {
            "SetCurrentFanStep=0", "SetFixedFanStatus=0", "SetAutoFanStatus=0",
            "SetNvThermalTarget=0", "SetStepFanStatus=1",
            "Set.SetFanIndexValue(Index=0,Temperture=40,Value=69)",
            "Set.SetFanIndexValue(Index=1,Temperture=80,Value=229)",
            "Set.SetFanIndexValue(Index=2,Temperture=0,Value=0)",
        }, Seq(wmi));
    }

    [Fact]
    public async Task Custom_with_15_points_writes_no_terminator()
    {
        var (ctl, wmi) = Make();
        var pts = Enumerable.Range(0, 15).Select(i => new FanCurvePoint(30 + i * 4, Math.Min(100, 20 + i * 6)));
        await ctl.ApplyAsync(FanMode.Custom, curve: new FanCurve(pts));
        Assert.Equal(15, wmi.Calls.Count(c => c.Method == "SetFanIndexValue"));
    }

    [Fact]
    public async Task Custom_with_invalid_curve_fails_without_touching_hardware()
    {
        var (ctl, wmi) = Make();
        var bad = new FanCurve(new[] { new FanCurvePoint(50, 50) });
        var r = await ctl.ApplyAsync(FanMode.Custom, curve: bad);
        Assert.False(r.Success);
        Assert.Contains("at least 2", r.Error);
        Assert.Empty(wmi.Calls);
    }

    [Fact]
    public async Task Custom_without_curve_uses_default()
    {
        var (ctl, wmi) = Make();
        await ctl.ApplyAsync(FanMode.Custom);
        Assert.Equal(FanCurve.Default.Points.Count, wmi.Calls.Count(c => c.Method == "SetFanIndexValue" && c.Args["Temperture"] is byte b && b != 0));
    }

    [Fact]
    public async Task Failure_mid_sequence_stops_and_reports_step()
    {
        var (ctl, wmi) = Make();
        wmi.FailOn.Add("SetAutoFanStatus");
        var r = await ctl.ApplyAsync(FanMode.Gaming);
        Assert.False(r.Success);
        Assert.Contains("SetAutoFanStatus", r.Error);
        Assert.Equal(4, wmi.Calls.Count);
        Assert.Null(ctl.LastApplied);
    }

    [Fact]
    public async Task Read_only_profile_refuses_to_write()
    {
        var (ctl, wmi) = Make(ModelProfile.Detect("ROG Zephyrus"));
        var r = await ctl.ApplyAsync(FanMode.Normal);
        Assert.False(r.Success);
        Assert.Empty(wmi.Calls);
    }

    [Fact]
    public async Task Delay_is_called_between_writes_not_after_last()
    {
        var delays = 0;
        var wmi = new FakeGigabyteWmi();
        var ctl = new FanController(wmi, Kd, _ => { delays++; return Task.CompletedTask; });
        await ctl.ApplyAsync(FanMode.Normal);
        Assert.Equal(wmi.Calls.Count - 1, delays);
    }

    [Fact]
    public async Task Concurrent_applies_are_serialized()
    {
        var wmi = new FakeGigabyteWmi();
        var gate = new SemaphoreSlim(0);
        var ctl = new FanController(wmi, Kd, async _ => await gate.WaitAsync());
        var first = ctl.ApplyAsync(FanMode.Normal);
        var second = ctl.ApplyAsync(FanMode.Quiet);
        Assert.Single(wmi.Calls);           // first sequence blocked in its delay, second waiting
        for (var i = 0; i < 8; i++) gate.Release();
        await first; await second;
        Assert.Equal("SetNvThermalTarget", wmi.Calls[^1].Method);
        Assert.Equal(1, wmi.Calls[^1].Data); // Quiet ran entirely after Normal
        Assert.Equal(FanMode.Quiet, ctl.LastApplied);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln`
Expected: compile error, `FanController` not found.

- [ ] **Step 3: Implement**

`src/OpenAorus.Hardware/Fans/FanController.cs`:

```csharp
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Wmi;

namespace OpenAorus.Hardware.Fans;

/// <summary>
/// Applies fan modes with the exact GB_WMIACPI_Set sequences Gigabyte Control Center uses.
/// All writes are serialized; a 500 ms pause separates consecutive writes (GCC does the same).
/// </summary>
public sealed class FanController
{
    public const int StepDelayMs = 500;

    private readonly IGigabyteWmi _wmi;
    private readonly ModelProfile _profile;
    private readonly Func<int, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FanMode? LastApplied { get; private set; }

    public FanController(IGigabyteWmi wmi, ModelProfile profile, Func<int, Task>? delay = null)
    {
        _wmi = wmi;
        _profile = profile;
        _delay = delay ?? (ms => Task.Delay(ms));
    }

    private sealed record Step(string Method, IReadOnlyDictionary<string, object> Args)
    {
        public static Step Data(string method, byte value) =>
            new(method, new Dictionary<string, object> { ["Data"] = value });

        public static Step Point(byte index, byte temperature, byte duty) =>
            new("SetFanIndexValue", new Dictionary<string, object>
            {
                ["Index"] = index, ["Temperture"] = temperature, ["Value"] = duty, // "Temperture" is the BIOS's spelling
            });
    }

    public async Task<WmiResult> ApplyAsync(FanMode mode, int fixedPercent = 50, FanCurve? curve = null, CancellationToken ct = default)
    {
        if (!_profile.CanWrite)
            return WmiResult.Fail($"Model '{_profile.Name}' is not recognised as a Gigabyte laptop; fan control is disabled.");

        List<Step> steps;
        try { steps = Build(mode, fixedPercent, curve ?? FanCurve.Default); }
        catch (ArgumentException ex) { return WmiResult.Fail(ex.Message); }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var s = steps[i];
                var r = _wmi.Invoke(WmiClass.Set, s.Method, s.Args);
                if (!r.Success)
                    return WmiResult.Fail($"Step {i + 1}/{steps.Count} {s.Method} failed: {r.Error}");
                if (i < steps.Count - 1)
                    await _delay(StepDelayMs).ConfigureAwait(false);
            }
            LastApplied = mode;
            return WmiResult.Ok();
        }
        finally { _gate.Release(); }
    }

    private List<Step> Build(FanMode mode, int fixedPercent, FanCurve curve)
    {
        var s = new List<Step> { Step.Data("SetCurrentFanStep", 0) };
        switch (mode)
        {
            case FanMode.Quiet:
            case FanMode.Normal:
            case FanMode.Gaming:
                s.Add(Step.Data("SetFixedFanStatus", 0));
                s.Add(Step.Data("SetStepFanStatus", 0));
                s.Add(Step.Data("SetAutoFanStatus", (byte)(mode == FanMode.Gaming ? 1 : 0)));
                s.Add(Step.Data("SetNvThermalTarget", (byte)(mode == FanMode.Quiet ? 1 : 0)));
                break;

            case FanMode.Turbo:
            case FanMode.Fixed:
            {
                var duty = mode == FanMode.Turbo ? (byte)_profile.DutyMax : _profile.ToDuty(fixedPercent);
                s.Add(Step.Data("SetAutoFanStatus", 0));
                s.Add(Step.Data("SetNvThermalTarget", 0));
                s.Add(Step.Data("SetFixedFanSpeed", duty));
                s.Add(Step.Data("SetGPUFanDuty", duty));
                s.Add(Step.Data("SetStepFanStatus", 1));
                s.Add(Step.Data("SetFixedFanStatus", 1));
                break;
            }

            case FanMode.Custom:
            {
                var errors = curve.Validate();
                if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
                s.Add(Step.Data("SetFixedFanStatus", 0));
                s.Add(Step.Data("SetAutoFanStatus", 0));
                s.Add(Step.Data("SetNvThermalTarget", 0));
                s.Add(Step.Data("SetStepFanStatus", 1));
                byte index = 0;
                foreach (var p in curve.Points)
                    s.Add(Step.Point(index++, (byte)p.Temperature, _profile.ToDuty(p.DutyPercent)));
                if (index < FanCurve.MaxPoints)
                    s.Add(Step.Point(index, 0, 0)); // terminator: EC reads the table until (0,0)
                break;
            }

            default:
                throw new ArgumentException($"Unknown fan mode {mode}");
        }
        return s;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass. If `Concurrent_applies_are_serialized` hangs, the gate release count is too low: `Normal` has 4 delays and `Quiet` has 4 delays, so 8 releases are required.

- [ ] **Step 5: Confirm the spec's Custom order** (already corrected when the plan was written; verify the sentence below is present, edit only if missing)

In `docs/superpowers/specs/2026-09-06-openaorus-v0.1-design.md` §2.2 replace
`Order for Custom: step0 → Fixed=0 → Auto=0 → NvThermalTarget=0 → points → Step=1.`
with
`Order for Custom: step0 → Fixed=0 → Auto=0 → NvThermalTarget=0 → Step=1 → points → (0,0) terminator if fewer than 15 (GCC applyFANprofile method 4 order).`

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add FanController with GCC-exact mode sequences

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: SensorReader

**Files:**
- Create: `src/OpenAorus.Hardware/Sensors/SensorReader.cs`
- Test: `tests/OpenAorus.Hardware.Tests/SensorReaderTests.cs`

**Interfaces:**
- Consumes: `IGigabyteWmi` (+ `Get` extension), `ModelProfile`
- Produces:
  - `sealed record SensorSnapshot(int CpuTemp, int GpuTemp, int Fan1Rpm, int Fan2Rpm, int Fan1DutyPercent, int Fan2DutyPercent, bool Ok, string? Error)`
  - `sealed class SensorReader(IGigabyteWmi wmi, ModelProfile profile)` with `SensorSnapshot Read()` and `static int SwapBytes(int raw)`

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/SensorReaderTests.cs`:

```csharp
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Sensors;

namespace OpenAorus.Hardware.Tests;

public class SensorReaderTests
{
    private static readonly ModelProfile Kd = ModelProfile.Detect("AORUS 17G KD");

    [Fact]
    public void SwapBytes_swaps_low_and_high_byte()
    {
        Assert.Equal(0x1234, SensorReader.SwapBytes(0x3412));
        Assert.Equal(0, SensorReader.SwapBytes(0));
    }

    [Fact]
    public void Read_collects_temps_rpm_and_duty_percent()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("getCpuTemp", (ushort)61);
        wmi.Respond("getGpuTemp1", (ushort)55);
        wmi.Respond("getRpm1", (ushort)0x6009);   // swapped -> 0x0960 = 2400
        wmi.Respond("getRpm2", (ushort)0xFC08);   // swapped -> 0x08FC = 2300
        wmi.Respond("GetCPUFanDuty", (byte)115);
        wmi.Respond("GetGPUFanDuty", (byte)229);
        var s = new SensorReader(wmi, Kd).Read();
        Assert.True(s.Ok);
        Assert.Equal(61, s.CpuTemp);
        Assert.Equal(55, s.GpuTemp);
        Assert.Equal(2400, s.Fan1Rpm);
        Assert.Equal(2300, s.Fan2Rpm);
        Assert.Equal(50, s.Fan1DutyPercent);
        Assert.Equal(100, s.Fan2DutyPercent);
    }

    [Fact]
    public void Read_does_not_swap_rpm_when_profile_says_so()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("getRpm1", (ushort)2400);
        var p = Kd with { RpmByteSwapped = false };
        Assert.Equal(2400, new SensorReader(wmi, p).Read().Fan1Rpm);
    }

    [Fact]
    public void Gpu_temp_falls_back_to_thermal_data_when_zero()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("getGpuTemp1", (ushort)0);
        wmi.Responses["GetThermalData"] = new Dictionary<string, object>
        {
            ["Thermal1"] = (byte)48, ["Thermal2"] = (byte)57, ["Thermal3"] = (byte)40,
        };
        Assert.Equal(57, new SensorReader(wmi, Kd).Read().GpuTemp);
    }

    [Fact]
    public void Cpu_failure_marks_snapshot_not_ok_but_keeps_other_values()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.FailOn.Add("getCpuTemp");
        wmi.Respond("getRpm1", (ushort)0x6009);
        var s = new SensorReader(wmi, Kd).Read();
        Assert.False(s.Ok);
        Assert.Contains("getCpuTemp", s.Error);
        Assert.Equal(2400, s.Fan1Rpm);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln`
Expected: compile error, `SensorReader` not found.

- [ ] **Step 3: Implement**

`src/OpenAorus.Hardware/Sensors/SensorReader.cs`:

```csharp
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Wmi;

namespace OpenAorus.Hardware.Sensors;

public sealed record SensorSnapshot(
    int CpuTemp, int GpuTemp, int Fan1Rpm, int Fan2Rpm,
    int Fan1DutyPercent, int Fan2DutyPercent, bool Ok, string? Error)
{
    public static SensorSnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0, false, "not read yet");
}

/// <summary>Reads temps, RPM and duty through GB_WMIACPI_Get. Cheap enough to call every second.</summary>
public sealed class SensorReader
{
    private readonly IGigabyteWmi _wmi;
    private readonly ModelProfile _profile;

    public SensorReader(IGigabyteWmi wmi, ModelProfile profile)
    {
        _wmi = wmi;
        _profile = profile;
    }

    /// <summary>GCC's GetBytesInt16 for Gigabyte models: the EC reports RPM big-endian.</summary>
    public static int SwapBytes(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    public SensorSnapshot Read()
    {
        var errors = new List<string>();

        int Value(string method, string param = "Data")
        {
            var r = _wmi.Get(method);
            if (!r.Success) { errors.Add($"{method}: {r.Error}"); return 0; }
            return r.GetInt(param);
        }

        var cpu = Value("getCpuTemp");
        var gpu = _profile.HasGpuTemp1 ? Value("getGpuTemp1") : 0;
        if (gpu <= 0)
        {
            var thermal = _wmi.Get("GetThermalData");
            if (thermal.Success) gpu = thermal.GetInt("Thermal2");
        }

        var rpm1 = Value("getRpm1");
        var rpm2 = _profile.FanCount >= 2 ? Value("getRpm2") : 0;
        if (_profile.RpmByteSwapped) { rpm1 = SwapBytes(rpm1); rpm2 = SwapBytes(rpm2); }

        var duty1 = _profile.ToPercent(Value("GetCPUFanDuty"));
        var duty2 = _profile.FanCount >= 2 ? _profile.ToPercent(Value("GetGPUFanDuty")) : 0;

        return new SensorSnapshot(cpu, gpu, rpm1, rpm2, duty1, duty2,
            Ok: errors.Count == 0,
            Error: errors.Count == 0 ? null : string.Join("; ", errors));
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add SensorReader for temps, RPM and duty

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: BatteryController

**Files:**
- Create: `src/OpenAorus.Hardware/Battery/BatteryController.cs`
- Test: `tests/OpenAorus.Hardware.Tests/BatteryControllerTests.cs`

**Interfaces:**
- Consumes: `IGigabyteWmi` (+ `Get`/`SetData` extensions)
- Produces:
  - `sealed record BatteryStatus(bool CustomLimitEnabled, int StopPercent, int CycleCount, int Health, bool Ok, string? Error)`
  - `sealed class BatteryController(IGigabyteWmi wmi)` with `BatteryStatus Read()` and `WmiResult SetLimit(bool enabled, int stopPercent)`
  - constants `PolicyStandard = 0`, `PolicyCustom = 4`, `MinStop = 60`, `MaxStop = 100`

GCC encoding (from decompiled `GeneralVD.ChargeMode_Change`): `SetChargePolicy(0)` + `SetChargeStop(100)` for standard, `SetChargePolicy(4)` + `SetChargeStop(80|60)` for the limited modes.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/BatteryControllerTests.cs`:

```csharp
using OpenAorus.Hardware.Battery;

namespace OpenAorus.Hardware.Tests;

public class BatteryControllerTests
{
    [Fact]
    public void SetLimit_enabled_writes_policy_4_then_stop()
    {
        var wmi = new FakeGigabyteWmi();
        var r = new BatteryController(wmi).SetLimit(true, 80);
        Assert.True(r.Success);
        Assert.Equal(new[] { "SetChargePolicy=4", "SetChargeStop=80" }, wmi.Calls.Select(c => $"{c.Method}={c.Data}"));
    }

    [Fact]
    public void SetLimit_disabled_writes_policy_0_and_stop_100()
    {
        var wmi = new FakeGigabyteWmi();
        new BatteryController(wmi).SetLimit(false, 70);
        Assert.Equal(new[] { "SetChargePolicy=0", "SetChargeStop=100" }, wmi.Calls.Select(c => $"{c.Method}={c.Data}"));
    }

    [Theory]
    [InlineData(10, 60)]
    [InlineData(59, 60)]
    [InlineData(101, 100)]
    public void SetLimit_clamps_stop_to_60_100(int requested, int written)
    {
        var wmi = new FakeGigabyteWmi();
        new BatteryController(wmi).SetLimit(true, requested);
        Assert.Equal(written, wmi.Calls[1].Data);
    }

    [Fact]
    public void SetLimit_reports_failed_step()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.FailOn.Add("SetChargeStop");
        var r = new BatteryController(wmi).SetLimit(true, 80);
        Assert.False(r.Success);
        Assert.Contains("SetChargeStop", r.Error);
    }

    [Fact]
    public void Read_maps_policy_stop_cycles_health()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargePolicy", (ushort)4);
        wmi.Respond("GetChargeStop", (ushort)80);
        wmi.Respond("GetBatteryCount", (ushort)123);
        wmi.Respond("GetBatteryHealth", (byte)2);
        var s = new BatteryController(wmi).Read();
        Assert.True(s.Ok);
        Assert.True(s.CustomLimitEnabled);
        Assert.Equal(80, s.StopPercent);
        Assert.Equal(123, s.CycleCount);
        Assert.Equal(2, s.Health);
    }

    [Fact]
    public void Read_policy_0_means_standard()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargePolicy", (ushort)0);
        wmi.Respond("GetChargeStop", (ushort)100);
        Assert.False(new BatteryController(wmi).Read().CustomLimitEnabled);
    }

    [Fact]
    public void Read_failure_is_reported()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.FailOn.Add("GetChargePolicy");
        var s = new BatteryController(wmi).Read();
        Assert.False(s.Ok);
        Assert.Contains("GetChargePolicy", s.Error);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln`
Expected: compile error, `BatteryController` not found.

- [ ] **Step 3: Implement**

`src/OpenAorus.Hardware/Battery/BatteryController.cs`:

```csharp
using OpenAorus.Hardware.Wmi;

namespace OpenAorus.Hardware.Battery;

public sealed record BatteryStatus(bool CustomLimitEnabled, int StopPercent, int CycleCount, int Health, bool Ok, string? Error);

/// <summary>Charge policy 0 = standard, 4 = custom stop (values GCC writes). Stop is 60..100 %.</summary>
public sealed class BatteryController
{
    public const byte PolicyStandard = 0;
    public const byte PolicyCustom = 4;
    public const int MinStop = 60;
    public const int MaxStop = 100;

    private readonly IGigabyteWmi _wmi;

    public BatteryController(IGigabyteWmi wmi) => _wmi = wmi;

    public WmiResult SetLimit(bool enabled, int stopPercent)
    {
        var policy = enabled ? PolicyCustom : PolicyStandard;
        var stop = enabled ? (byte)Math.Clamp(stopPercent, MinStop, MaxStop) : (byte)MaxStop;

        var r1 = _wmi.SetData("SetChargePolicy", policy);
        if (!r1.Success) return WmiResult.Fail($"SetChargePolicy failed: {r1.Error}");
        var r2 = _wmi.SetData("SetChargeStop", stop);
        if (!r2.Success) return WmiResult.Fail($"SetChargeStop failed: {r2.Error}");
        return WmiResult.Ok();
    }

    public BatteryStatus Read()
    {
        var errors = new List<string>();
        int Value(string method)
        {
            var r = _wmi.Get(method);
            if (!r.Success) { errors.Add($"{method}: {r.Error}"); return 0; }
            return r.GetInt("Data");
        }

        var policy = Value("GetChargePolicy");
        var stop = Value("GetChargeStop");
        var cycles = Value("GetBatteryCount");
        var health = Value("GetBatteryHealth");

        return new BatteryStatus(
            CustomLimitEnabled: policy != PolicyStandard,
            StopPercent: stop == 0 ? MaxStop : Math.Clamp(stop, MinStop, MaxStop),
            CycleCount: cycles,
            Health: health,
            Ok: errors.Count == 0,
            Error: errors.Count == 0 ? null : string.Join("; ", errors));
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add BatteryController (charge policy + stop)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Diagnostics dump

**Files:**
- Create: `src/OpenAorus.Hardware/Diagnostics/DiagnosticsDump.cs`
- Test: `tests/OpenAorus.Hardware.Tests/DiagnosticsDumpTests.cs`

**Interfaces:**
- Consumes: `IGigabyteWmi`, `ModelProfile`
- Produces: `static string DiagnosticsDump.Render(IGigabyteWmi wmi, ModelProfile profile, string appVersion)` and `static readonly string[] DiagnosticsDump.GetMethods`

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/DiagnosticsDumpTests.cs`:

```csharp
using OpenAorus.Hardware.Diagnostics;
using OpenAorus.Hardware.Profiles;

namespace OpenAorus.Hardware.Tests;

public class DiagnosticsDumpTests
{
    [Fact]
    public void Render_lists_model_profile_and_every_get_method()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("getCpuTemp", (ushort)61);
        wmi.FailOn.Add("GetVRStatus");
        var text = DiagnosticsDump.Render(wmi, ModelProfile.Detect("AORUS 17G KD"), "0.1.0-test");

        Assert.Contains("OpenAorus 0.1.0-test", text);
        Assert.Contains("Model: AORUS 17G KD (Tested, DutyMax=229)", text);
        Assert.Contains("getCpuTemp: Data=61", text);
        Assert.Contains("GetVRStatus: ERROR", text);
        Assert.All(DiagnosticsDump.GetMethods, m => Assert.Contains(m + ":", text));
        Assert.Contains("Fan table", text);
        Assert.Equal(DiagnosticsDump.GetMethods.Length + 15, wmi.Calls.Count); // + 15 GetFanIndexValue reads
    }

    [Fact]
    public void GetMethods_includes_fan_thermal_and_battery_reads_and_no_setters()
    {
        Assert.Contains("GetDeepFan", DiagnosticsDump.GetMethods);
        Assert.Contains("GetThermalData", DiagnosticsDump.GetMethods);
        Assert.Contains("GetChargeStop", DiagnosticsDump.GetMethods);
        Assert.DoesNotContain(DiagnosticsDump.GetMethods, m => m.StartsWith("Set"));
        Assert.DoesNotContain("GetFanIndexValue", DiagnosticsDump.GetMethods); // needs an Index arg
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln`
Expected: compile error.

- [ ] **Step 3: Implement**

`src/OpenAorus.Hardware/Diagnostics/DiagnosticsDump.cs` (method list is every argument-less method of `GB_WMIACPI_Get` from `docs/research/gb-wmiacpi-methods-aorus-17g-kd.txt`; `GetLightBar` and `GetFanIndexValue` need an `Index` input and are handled separately):

```csharp
using System.Text;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Wmi;

namespace OpenAorus.Hardware.Diagnostics;

/// <summary>Reads every argument-less GB_WMIACPI_Get method so untested models can be profiled from a bug report.</summary>
public static class DiagnosticsDump
{
    public static readonly string[] GetMethods =
    {
        "GetCPUFanDuty", "GetGPUFanDuty", "GetBatteryCount", "GetMaxCharge", "GetPEGorSG", "GetNvPowerConfig",
        "GetThermalData", "GetNvThermalTarget", "CheckHeavyLoading", "GetDeepFan", "GetBatteryHealth", "GetFanHealth",
        "GetPowerOnTime", "GetChargePolicy", "GetChargeStop", "GetStepFanStatus", "GetVRStatus", "GetFixedFanStatus",
        "GetFixedFanSpeed", "IsDeviceExist", "getBattCyc1", "getBattCyc", "GetFanPWMStatus", "GetFanAdjustStatus",
        "GetAutoFanStatus", "GetSleepUSBCharge", "GetHibernationUSBCharge", "CheckUCFSupport", "GetFanSpeed",
        "GetCamera2", "GetTouchScreenSupport", "GetAIBoostStatus", "GetFirstDate", "GetEcValueBoostStatus",
        "GetWhisperMode", "GetBrightness", "GetBluetooth", "GetWiFi", "GetW35G", "GetBirightnessOff", "GetCamera",
        "GetTouchPad", "GetWinkeyBlocking", "GetTurboMode", "GetSmartCharge", "CheckSmartCharge", "CheckSmartTurbo",
        "GetUSB30", "CheckUSB30", "GetDockingStatus", "GetTouchScreen", "GetSmartTurbo", "GetLid1Status",
        "GetKeyboardMatrix", "GetSmartTurboStatus", "GetGSensorStatus", "GetOnboardLANStatus", "CheckDocking",
        "GetKeyBoardBackLight", "QueryLightSensor", "QueryThermalSensor", "GetSmartCool", "GetLightSensorVersion",
        "CheckBIOSMode", "Check3GModule", "getCpuTemp", "getGpuTemp1", "getGpuTemp2", "getRpm1", "getRpm2",
        "GetPEG2orSG2", "GetDynamicBoostStatus",
    };

    public static string Render(IGigabyteWmi wmi, ModelProfile profile, string appVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OpenAorus {appVersion} diagnostics - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Model: {profile.Name} ({profile.Status}, DutyMax={profile.DutyMax}, Fans={profile.FanCount})");
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine();
        sb.AppendLine("GB_WMIACPI_Get:");
        foreach (var m in GetMethods)
        {
            var r = wmi.Get(m);
            sb.Append("  ").Append(m).Append(": ");
            sb.AppendLine(r.Success
                ? string.Join(" ", r.Out.Select(kv => $"{kv.Key}={kv.Value}"))
                : $"ERROR {r.Error}");
        }
        sb.AppendLine();
        sb.AppendLine("Fan table (GetFanIndexValue 0..14):");
        for (byte i = 0; i < 15; i++)
        {
            var r = wmi.Invoke(WmiClass.Get, "GetFanIndexValue", new Dictionary<string, object> { ["Index"] = i });
            sb.AppendLine(r.Success
                ? $"  [{i}] temp={r.GetInt("Temperture")} duty={r.GetInt("Value")}"
                : $"  [{i}] ERROR {r.Error}");
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add DiagnosticsDump over every argument-less Get method

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: Settings model and JSON store

**Files:**
- Create: `src/OpenAorus.Hardware/Config/AppSettings.cs`, `src/OpenAorus.Hardware/Config/SettingsStore.cs`
- Test: `tests/OpenAorus.Hardware.Tests/SettingsStoreTests.cs`

**Interfaces:**
- Consumes: `FanMode`, `FanCurvePoint`, `FanCurve.Default`
- Produces:
  - `sealed class AppSettings` (mutable POCO): `FanMode Mode = Normal`, `int FixedPercent = 50`, `List<FanCurvePoint> Curve`, `bool ChargeLimitEnabled`, `int ChargeStopPercent = 80`, `int PollIntervalVisibleMs = 1000`, `int PollIntervalHiddenMs = 5000`, `bool StartWithWindows`, `GccTakeoverState? Takeover` (defined in Task 10; declare the property in Task 10, not here)
  - `sealed class SettingsStore(string path)` with `AppSettings Load()`, `void Save(AppSettings)`, `string Path`, `static string DefaultPath` (= `%LOCALAPPDATA%\OpenAorus\settings.json`)

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/SettingsStoreTests.cs`:

```csharp
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Fans;

namespace OpenAorus.Hardware.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OpenAorusTests", Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_dir, "settings.json");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Load_returns_defaults_when_file_missing()
    {
        var s = new SettingsStore(File).Load();
        Assert.Equal(FanMode.Normal, s.Mode);
        Assert.Equal(50, s.FixedPercent);
        Assert.Equal(FanCurve.Default.Points, s.Curve);
        Assert.False(s.ChargeLimitEnabled);
        Assert.Equal(80, s.ChargeStopPercent);
        Assert.Equal(1000, s.PollIntervalVisibleMs);
        Assert.Equal(5000, s.PollIntervalHiddenMs);
    }

    [Fact]
    public void Save_then_Load_round_trips()
    {
        var store = new SettingsStore(File);
        var s = store.Load();
        s.Mode = FanMode.Custom;
        s.FixedPercent = 73;
        s.Curve = new List<FanCurvePoint> { new(45, 20), new(85, 100) };
        s.ChargeLimitEnabled = true;
        s.ChargeStopPercent = 60;
        s.StartWithWindows = true;
        store.Save(s);

        var back = new SettingsStore(File).Load();
        Assert.Equal(FanMode.Custom, back.Mode);
        Assert.Equal(73, back.FixedPercent);
        Assert.Equal(s.Curve, back.Curve);
        Assert.True(back.ChargeLimitEnabled);
        Assert.Equal(60, back.ChargeStopPercent);
        Assert.True(back.StartWithWindows);
    }

    [Fact]
    public void Save_creates_directory_and_writes_enum_as_string()
    {
        var store = new SettingsStore(File);
        store.Save(new AppSettings { Mode = FanMode.Gaming });
        Assert.True(System.IO.File.Exists(File));
        Assert.Contains("\"Gaming\"", System.IO.File.ReadAllText(File));
    }

    [Fact]
    public void Corrupt_file_is_renamed_to_bad_and_defaults_returned()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{ not json");
        var s = new SettingsStore(File).Load();
        Assert.Equal(FanMode.Normal, s.Mode);
        Assert.True(System.IO.File.Exists(File + ".bad"));
        Assert.False(System.IO.File.Exists(File));
    }

    [Fact]
    public void Unknown_properties_are_ignored()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{ \"Mode\": \"Quiet\", \"Future\": 1 }");
        Assert.Equal(FanMode.Quiet, new SettingsStore(File).Load().Mode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln`
Expected: compile error.

- [ ] **Step 3: Implement**

`src/OpenAorus.Hardware/Config/AppSettings.cs`:

```csharp
using OpenAorus.Hardware.Fans;

namespace OpenAorus.Hardware.Config;

public sealed class AppSettings
{
    public FanMode Mode { get; set; } = FanMode.Normal;
    public int FixedPercent { get; set; } = 50;
    public List<FanCurvePoint> Curve { get; set; } = FanCurve.Default.Points.ToList();
    public bool ChargeLimitEnabled { get; set; }
    public int ChargeStopPercent { get; set; } = 80;
    public int PollIntervalVisibleMs { get; set; } = 1000;
    public int PollIntervalHiddenMs { get; set; } = 5000;
    public bool StartWithWindows { get; set; }

    public FanCurve ToCurve() => new(Curve);
}
```

`src/OpenAorus.Hardware/Config/SettingsStore.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAorus.Hardware.Config;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        IncludeFields = false,
    };

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAorus", "settings.json");

    public string Path { get; }

    public SettingsStore(string path) => Path = path;

    public AppSettings Load()
    {
        if (!File.Exists(Path)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(Path);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (JsonException)
        {
            var bad = Path + ".bad";
            File.Delete(bad);
            File.Move(Path, bad);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, Path, overwrite: true);
    }
}
```

`FanCurvePoint` is a `record struct` with a primary constructor; `System.Text.Json` on .NET 8 deserializes it through that constructor because the parameter names match the properties.

- [ ] **Step 4: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add AppSettings and JSON SettingsStore

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: GCC takeover bookkeeping and Windows implementation

**Files:**
- Create: `src/OpenAorus.Hardware/System/IGccSystem.cs`, `src/OpenAorus.Hardware/System/GccTakeover.cs`, `src/OpenAorus.Hardware/System/WindowsGccSystem.cs`
- Modify: `src/OpenAorus.Hardware/Config/AppSettings.cs` (add `Takeover` property)
- Test: `tests/OpenAorus.Hardware.Tests/GccTakeoverTests.cs`

**Interfaces:**
- Produces:
  - `sealed class GccTakeoverState { bool TaskDisabled; string? RunValue; bool ServiceDisabled; DateTime When; }`
  - `interface IGccSystem` with `bool TaskExists(string name)`, `bool IsTaskEnabled(string name)`, `bool DisableTask(string name)`, `bool EnableTask(string name)`, `string? ReadRunValue(string name)`, `void DeleteRunValue(string name)`, `void WriteRunValue(string name, string value)`, `bool ServiceExists(string name)`, `bool StopAndDisableService(string name)`, `bool EnableService(string name)`, `int KillProcesses(IEnumerable<string> names)`, `bool AnyProcessRunning(IEnumerable<string> names)`
  - `static class GccTakeover` with `const string TaskName = "GCC"`, `const string RunValueName = "AorusFusion"`, `const string ServiceName = "SMV4_Service"`, `static readonly string[] ProcessNames`, `GccTakeoverState TakeOver(IGccSystem)`, `void Restore(IGccSystem, GccTakeoverState)`, `bool IsGccActive(IGccSystem)`
  - `sealed class WindowsGccSystem : IGccSystem` (schtasks / Registry / ServiceController / Process)
  - `AppSettings.Takeover` (`GccTakeoverState?`)

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/GccTakeoverTests.cs`:

```csharp
using OpenAorus.Hardware.System;

namespace OpenAorus.Hardware.Tests;

public sealed class FakeGccSystem : IGccSystem
{
    public HashSet<string> Tasks { get; } = new() { "GCC" };
    public HashSet<string> DisabledTasks { get; } = new();
    public Dictionary<string, string> RunValues { get; } = new() { ["AorusFusion"] = @"C:\Program Files\ControlCenter\FusionStartUp.exe" };
    public HashSet<string> Services { get; } = new() { "SMV4_Service" };
    public HashSet<string> DisabledServices { get; } = new();
    public HashSet<string> Running { get; } = new() { "GCC", "FusionStation" };
    public List<string> Log { get; } = new();

    public bool TaskExists(string name) => Tasks.Contains(name);
    public bool IsTaskEnabled(string name) => Tasks.Contains(name) && !DisabledTasks.Contains(name);
    public bool DisableTask(string name) { Log.Add($"disable-task {name}"); return DisabledTasks.Add(name); }
    public bool EnableTask(string name) { Log.Add($"enable-task {name}"); return DisabledTasks.Remove(name); }
    public string? ReadRunValue(string name) => RunValues.GetValueOrDefault(name);
    public void DeleteRunValue(string name) { Log.Add($"delete-run {name}"); RunValues.Remove(name); }
    public void WriteRunValue(string name, string value) { Log.Add($"write-run {name}"); RunValues[name] = value; }
    public bool ServiceExists(string name) => Services.Contains(name);
    public bool StopAndDisableService(string name) { Log.Add($"disable-service {name}"); return DisabledServices.Add(name); }
    public bool EnableService(string name) { Log.Add($"enable-service {name}"); return DisabledServices.Remove(name); }
    public int KillProcesses(IEnumerable<string> names) { var n = Running.RemoveWhere(names.Contains); Log.Add($"kill {n}"); return n; }
    public bool AnyProcessRunning(IEnumerable<string> names) => Running.Overlaps(names);
}

public class GccTakeoverTests
{
    [Fact]
    public void TakeOver_disables_task_run_key_service_and_kills_processes()
    {
        var sys = new FakeGccSystem();
        var state = GccTakeover.TakeOver(sys);

        Assert.True(state.TaskDisabled);
        Assert.Equal(@"C:\Program Files\ControlCenter\FusionStartUp.exe", state.RunValue);
        Assert.True(state.ServiceDisabled);
        Assert.Contains("GCC", sys.DisabledTasks);
        Assert.DoesNotContain("AorusFusion", sys.RunValues.Keys);
        Assert.Contains("SMV4_Service", sys.DisabledServices);
        Assert.Empty(sys.Running);
        Assert.False(GccTakeover.IsGccActive(sys));
    }

    [Fact]
    public void TakeOver_records_only_what_existed()
    {
        var sys = new FakeGccSystem();
        sys.Tasks.Clear(); sys.RunValues.Clear(); sys.Services.Clear();
        var state = GccTakeover.TakeOver(sys);
        Assert.False(state.TaskDisabled);
        Assert.Null(state.RunValue);
        Assert.False(state.ServiceDisabled);
        Assert.DoesNotContain(sys.Log, l => l.StartsWith("disable"));
    }

    [Fact]
    public void Restore_reverses_exactly_the_recorded_changes()
    {
        var sys = new FakeGccSystem();
        var state = GccTakeover.TakeOver(sys);
        sys.Log.Clear();

        GccTakeover.Restore(sys, state);

        Assert.Empty(sys.DisabledTasks);
        Assert.Equal(@"C:\Program Files\ControlCenter\FusionStartUp.exe", sys.RunValues["AorusFusion"]);
        Assert.Empty(sys.DisabledServices);
        Assert.Equal(new[] { "enable-task GCC", "write-run AorusFusion", "enable-service SMV4_Service" }, sys.Log);
    }

    [Fact]
    public void Restore_with_partial_state_skips_untouched_items()
    {
        var sys = new FakeGccSystem();
        GccTakeover.Restore(sys, new GccTakeoverState { TaskDisabled = true });
        Assert.Equal(new[] { "enable-task GCC" }, sys.Log);
    }

    [Fact]
    public void IsGccActive_true_when_task_enabled_or_process_running()
    {
        var sys = new FakeGccSystem();
        Assert.True(GccTakeover.IsGccActive(sys));
        sys.DisabledTasks.Add("GCC"); sys.RunValues.Clear(); sys.DisabledServices.Add("SMV4_Service");
        Assert.True(GccTakeover.IsGccActive(sys));   // processes still running
        sys.Running.Clear();
        Assert.False(GccTakeover.IsGccActive(sys));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln`
Expected: compile error.

- [ ] **Step 3: Implement the seam and pure logic**

`src/OpenAorus.Hardware/System/IGccSystem.cs`:

```csharp
namespace OpenAorus.Hardware.System;

/// <summary>OS actions needed to park Gigabyte Control Center. Real impl: <see cref="WindowsGccSystem"/>.</summary>
public interface IGccSystem
{
    bool TaskExists(string name);
    bool IsTaskEnabled(string name);
    bool DisableTask(string name);
    bool EnableTask(string name);
    string? ReadRunValue(string name);
    void DeleteRunValue(string name);
    void WriteRunValue(string name, string value);
    bool ServiceExists(string name);
    bool StopAndDisableService(string name);
    bool EnableService(string name);
    int KillProcesses(IEnumerable<string> names);
    bool AnyProcessRunning(IEnumerable<string> names);
}
```

`src/OpenAorus.Hardware/System/GccTakeover.cs`:

```csharp
namespace OpenAorus.Hardware.System;

public sealed class GccTakeoverState
{
    public bool TaskDisabled { get; set; }
    public string? RunValue { get; set; }
    public bool ServiceDisabled { get; set; }
    public DateTime When { get; set; } = DateTime.Now;
}

/// <summary>Disables GCC's autostart pieces so two controllers do not fight over the EC. Fully reversible.</summary>
public static class GccTakeover
{
    public const string TaskName = "GCC";                 // \GCC, RunLevel Highest, created by GCC installer
    public const string RunValueName = "AorusFusion";     // HKLM\...\Run, legacy ControlCenter autostart
    public const string ServiceName = "SMV4_Service";     // legacy ControlCenter LocalSystem service

    public static readonly string[] ProcessNames =
    {
        "GCC", "ControlCenter", "FusionStation", "FusionShortcut", "OSDwindow",
        "CloudMatrixControlCenter", "GbtCloudMatrix", "GBT_DL_LIB", "LaunchGCC",
    };

    public static GccTakeoverState TakeOver(IGccSystem sys)
    {
        var state = new GccTakeoverState();
        if (sys.TaskExists(TaskName))
            state.TaskDisabled = sys.DisableTask(TaskName);

        var run = sys.ReadRunValue(RunValueName);
        if (run is not null)
        {
            sys.DeleteRunValue(RunValueName);
            state.RunValue = run;
        }

        if (sys.ServiceExists(ServiceName))
            state.ServiceDisabled = sys.StopAndDisableService(ServiceName);

        sys.KillProcesses(ProcessNames);
        return state;
    }

    public static void Restore(IGccSystem sys, GccTakeoverState state)
    {
        if (state.TaskDisabled) sys.EnableTask(TaskName);
        if (state.RunValue is not null) sys.WriteRunValue(RunValueName, state.RunValue);
        if (state.ServiceDisabled) sys.EnableService(ServiceName);
    }

    public static bool IsGccActive(IGccSystem sys) =>
        sys.AnyProcessRunning(ProcessNames)
        || sys.IsTaskEnabled(TaskName)
        || sys.ReadRunValue(RunValueName) is not null;
}
```

Add to `AppSettings`:

```csharp
public OpenAorus.Hardware.System.GccTakeoverState? Takeover { get; set; }
```

- [ ] **Step 4: Implement the Windows side** (no unit tests; verified by owner in Task 15)

`src/OpenAorus.Hardware/System/WindowsGccSystem.cs`:

```csharp
using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace OpenAorus.Hardware.System;

public sealed class WindowsGccSystem : IGccSystem
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private static (int code, string output) Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.ExitCode, output);
    }

    public bool TaskExists(string name) => Run("schtasks", $"/Query /TN \"{name}\"").code == 0;

    public bool IsTaskEnabled(string name)
    {
        var (code, output) = Run("schtasks", $"/Query /TN \"{name}\" /FO LIST /V");
        return code == 0 && !output.Contains("Disabled", StringComparison.OrdinalIgnoreCase);
    }

    public bool DisableTask(string name)
    {
        var wasEnabled = IsTaskEnabled(name);
        return Run("schtasks", $"/Change /TN \"{name}\" /Disable").code == 0 && wasEnabled;
    }

    public bool EnableTask(string name) => Run("schtasks", $"/Change /TN \"{name}\" /Enable").code == 0;

    public string? ReadRunValue(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(name)?.ToString();
    }

    public void DeleteRunValue(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void WriteRunValue(string name, string value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RunKey, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public bool ServiceExists(string name) =>
        ServiceController.GetServices().Any(s => s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase));

    public bool StopAndDisableService(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            var wasEnabled = sc.StartType != ServiceStartMode.Disabled;
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            Run("sc", $"config \"{name}\" start= disabled");
            return wasEnabled;
        }
        catch (Exception) { return false; }
    }

    public bool EnableService(string name)
    {
        var ok = Run("sc", $"config \"{name}\" start= auto").code == 0;
        Run("sc", $"start \"{name}\"");
        return ok;
    }

    public int KillProcesses(IEnumerable<string> names)
    {
        var killed = 0;
        foreach (var n in names)
            foreach (var p in Process.GetProcessesByName(n))
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); killed++; }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
        return killed;
    }

    public bool AnyProcessRunning(IEnumerable<string> names) =>
        names.Any(n => Process.GetProcessesByName(n).Length > 0);
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add reversible GCC takeover with IGccSystem seam and Windows impl

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 11: Startup task and elevation helpers

**Files:**
- Create: `src/OpenAorus.Hardware/System/StartupTask.cs`, `src/OpenAorus.Hardware/System/Elevation.cs`
- Test: `tests/OpenAorus.Hardware.Tests/StartupTaskTests.cs`

**Interfaces:**
- Produces:
  - `static class StartupTask` with `const string TaskName = "OpenAorus"`, `static string BuildCreateArguments(string exePath)`, `static string BuildDeleteArguments()`, `static string BuildQueryArguments()`, `bool IsEnabled()`, `bool Enable(string exePath)`, `bool Disable()`
  - `static class Elevation` with `bool IsElevated()`, `bool RelaunchElevated(string exePath, IEnumerable<string> args)`

- [ ] **Step 1: Write the failing tests** (argument builders are pure; the schtasks calls are owner-verified)

`tests/OpenAorus.Hardware.Tests/StartupTaskTests.cs`:

```csharp
using OpenAorus.Hardware.System;

namespace OpenAorus.Hardware.Tests;

public class StartupTaskTests
{
    [Fact]
    public void Create_arguments_register_logon_task_with_highest_privileges()
    {
        var args = StartupTask.BuildCreateArguments(@"C:\Tools\OpenAorus.exe");
        Assert.Contains("/Create", args);
        Assert.Contains("/SC ONLOGON", args);
        Assert.Contains("/RL HIGHEST", args);
        Assert.Contains("/TN \"OpenAorus\"", args);
        Assert.Contains("/TR \"\\\"C:\\Tools\\OpenAorus.exe\\\" --tray\"", args);
        Assert.Contains("/F", args);
    }

    [Fact]
    public void Delete_and_query_arguments_target_the_task()
    {
        Assert.Equal("/Delete /TN \"OpenAorus\" /F", StartupTask.BuildDeleteArguments());
        Assert.Equal("/Query /TN \"OpenAorus\"", StartupTask.BuildQueryArguments());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln`
Expected: compile error.

- [ ] **Step 3: Implement**

`src/OpenAorus.Hardware/System/StartupTask.cs`:

```csharp
using System.Diagnostics;

namespace OpenAorus.Hardware.System;

/// <summary>
/// "Start with Windows" via a logon task with RunLevel Highest, so the tray app starts elevated without a UAC prompt.
/// Same mechanism GCC and G-Helper use. Creating/deleting the task itself requires elevation.
/// </summary>
public static class StartupTask
{
    public const string TaskName = "OpenAorus";

    public static string BuildCreateArguments(string exePath) =>
        $"/Create /SC ONLOGON /RL HIGHEST /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\" --tray\" /F";

    public static string BuildDeleteArguments() => $"/Delete /TN \"{TaskName}\" /F";

    public static string BuildQueryArguments() => $"/Query /TN \"{TaskName}\"";

    public static bool IsEnabled() => RunSchtasks(BuildQueryArguments()) == 0;

    public static bool Enable(string exePath) => RunSchtasks(BuildCreateArguments(exePath)) == 0;

    public static bool Disable() => RunSchtasks(BuildDeleteArguments()) == 0;

    private static int RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks", args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode;
        }
        catch (Exception) { return -1; }
    }
}
```

`src/OpenAorus.Hardware/System/Elevation.cs`:

```csharp
using System.Diagnostics;
using System.Security.Principal;

namespace OpenAorus.Hardware.System;

public static class Elevation
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Starts a new elevated copy (UAC prompt). Returns false if the user cancelled.</summary>
    public static bool RelaunchElevated(string exePath, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
        };
        try { Process.Start(psi); return true; }
        catch (System.ComponentModel.Win32Exception) { return false; } // ERROR_CANCELLED
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add StartupTask (schtasks logon task) and Elevation helpers

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 12: App bootstrap, composition root and CLI modes

**Files:**
- Modify: `src/OpenAorus.App/App.xaml`, `src/OpenAorus.App/App.xaml.cs`
- Create: `src/OpenAorus.App/AppServices.cs`, `src/OpenAorus.App/ConsoleAttach.cs`
- Delete: template `src/OpenAorus.App/MainWindow.xaml(.cs)` (a real one arrives in Task 14; until then the app only supports `--dump` / `--apply`)

**Interfaces:**
- Consumes: everything from Tasks 2–11
- Produces:
  - `sealed class AppServices` with `ModelProfile Profile`, `IGigabyteWmi Wmi`, `FanController Fans`, `SensorReader Sensors`, `BatteryController Battery`, `SettingsStore Store`, `AppSettings Settings`, `IGccSystem Gcc`, `string Version`, `string ExePath`, `static AppServices Create()`, `Task<WmiResult> ApplySavedAsync()`, `string WriteDiagnostics()`
  - CLI: `OpenAorus.exe --dump` (prints diagnostics, exit 0/1), `--apply` (re-applies saved fan mode + battery limit, exits), `--tray` (start hidden, used by the logon task), `--show` (start with window visible, default when launched by hand)

- [ ] **Step 1: Composition root**

`src/OpenAorus.App/AppServices.cs`:

```csharp
using System.Reflection;
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Diagnostics;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Sensors;
using OpenAorus.Hardware.System;
using OpenAorus.Hardware.Wmi;

namespace OpenAorus.App;

public sealed class AppServices
{
    public required ModelProfile Profile { get; init; }
    public required IGigabyteWmi Wmi { get; init; }
    public required FanController Fans { get; init; }
    public required SensorReader Sensors { get; init; }
    public required BatteryController Battery { get; init; }
    public required SettingsStore Store { get; init; }
    public required AppSettings Settings { get; init; }
    public required IGccSystem Gcc { get; init; }
    public required string Version { get; init; }
    public required string ExePath { get; init; }

    public static AppServices Create()
    {
        var profile = ModelProfile.Detect(SystemInfo.GetProductName());
        var wmi = new GigabyteWmi();
        var store = new SettingsStore(SettingsStore.DefaultPath);
        return new AppServices
        {
            Profile = profile,
            Wmi = wmi,
            Fans = new FanController(wmi, profile),
            Sensors = new SensorReader(wmi, profile),
            Battery = new BatteryController(wmi),
            Store = store,
            Settings = store.Load(),
            Gcc = new WindowsGccSystem(),
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
            ExePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location,
        };
    }

    /// <summary>Re-applies the persisted fan mode and charge limit (startup, resume, --apply).</summary>
    public async Task<WmiResult> ApplySavedAsync()
    {
        if (!Profile.CanWrite) return WmiResult.Fail("read-only model");
        var fans = await Fans.ApplyAsync(Settings.Mode, Settings.FixedPercent, Settings.ToCurve());
        if (!fans.Success) return fans;
        return Battery.SetLimit(Settings.ChargeLimitEnabled, Settings.ChargeStopPercent);
    }

    public string WriteDiagnostics()
    {
        var text = DiagnosticsDump.Render(Wmi, Profile, Version);
        var dir = Path.GetDirectoryName(Store.Path)!;
        Directory.CreateDirectory(dir);
        var safe = string.Concat(Profile.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Replace(' ', '-');
        var path = Path.Combine(dir, $"diagnostics-{safe}.txt");
        File.WriteAllText(path, text);
        return path;
    }
}
```

- [ ] **Step 2: Console attach helper for `--dump`** (a WinExe has no console; attach to the parent's)

`src/OpenAorus.App/ConsoleAttach.cs`:

```csharp
using System.Runtime.InteropServices;

namespace OpenAorus.App;

internal static class ConsoleAttach
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    /// <summary>Returns true if stdout now goes to the launching console.</summary>
    public static bool TryAttach()
    {
        if (!AttachConsole(AttachParentProcess)) return false;
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        return true;
    }
}
```

- [ ] **Step 3: App startup**

`src/OpenAorus.App/App.xaml` (remove `StartupUri`):

```xml
<Application x:Class="OpenAorus.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Themes/Dark.xaml"/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

Create a placeholder `src/OpenAorus.App/Themes/Dark.xaml` now (Task 14 fills it):

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"/>
```

`src/OpenAorus.App/App.xaml.cs`:

```csharp
using System.Windows;
using OpenAorus.Hardware.System;

namespace OpenAorus.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    public static bool StartHidden { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToArray();
        StartHidden = args.Contains("--tray");

        if (!Elevation.IsElevated())
        {
            var exe = Environment.ProcessPath!;
            if (!Elevation.RelaunchElevated(exe, e.Args))
                MessageBox.Show("OpenAorus needs administrator rights to talk to the embedded controller.",
                    "OpenAorus", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        Services = AppServices.Create();

        if (args.Contains("--dump"))
        {
            var attached = ConsoleAttach.TryAttach();
            var path = Services.WriteDiagnostics();
            if (attached) Console.WriteLine(File.ReadAllText(path));
            else MessageBox.Show($"Diagnostics written to:\n{path}", "OpenAorus");
            Shutdown(0);
            return;
        }

        if (args.Contains("--apply"))
        {
            var r = await Services.ApplySavedAsync();
            Shutdown(r.Success ? 0 : 1);
            return;
        }

        // Task 14 replaces this with tray + window startup.
        MessageBox.Show($"OpenAorus {Services.Version} on {Services.Profile.Name} ({Services.Profile.Status}). UI arrives in Task 14.", "OpenAorus");
        Shutdown(0);
    }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build OpenAorus.sln`
Expected: succeeds with no errors (warnings about async void are acceptable).

- [ ] **Step 5: OWNER VERIFY — first hardware contact**

Ask the owner to run, from an elevated terminal:

```
dotnet run --project src/OpenAorus.App -- --dump
```

Expected: UAC is not prompted again (already elevated), and the output starts with `OpenAorus 0.1.0 diagnostics`, `Model: AORUS 17G KD (Tested, DutyMax=229, Fans=2)`, then `getCpuTemp: Data=<plausible °C>`, `getRpm1: Data=<raw>`, `GetCPUFanDuty: Data=<0..229>`, and a fan table. Record which of `getGpuTemp1` / `GetThermalData.Thermal2` matches the real GPU temperature (compare with Task Manager) and paste the whole output into `docs/research/dump-aorus-17g-kd.txt`. **Do not continue to Task 13 until this output exists**; if `getGpuTemp1` is 0 and `Thermal2` is not the GPU, change `SensorReader`'s fallback parameter to the correct `ThermalN` and adjust `Gpu_temp_falls_back_to_thermal_data_when_zero` accordingly.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add app bootstrap: elevation, composition root, --dump and --apply CLI modes

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 13: MainViewModel (modes, sensors, banner, polling, resume)

**Files:**
- Create: `src/OpenAorus.App/ViewModels/MainViewModel.cs`, `src/OpenAorus.App/ViewModels/SensorPoller.cs`

**Interfaces:**
- Consumes: `AppServices`, `FanMode`, `SensorSnapshot`, `ProfileStatus`
- Produces:
  - `partial class MainViewModel : ObservableObject` with observable `FanMode SelectedMode`, `int FixedPercent`, `SensorSnapshot Sensors`, `string BannerText`, `BannerKind Banner` (`None|Info|Warning|Error`), `bool IsBusy`, `bool CanWrite`, `string ModelLine`, `string StatusLine`, `bool IsWindowVisible`
  - commands `IAsyncRelayCommand<FanMode> SelectModeCommand`, `IAsyncRelayCommand ApplyFixedCommand`, `IAsyncRelayCommand ApplyCurveCommand`, `IRelayCommand ExportDiagnosticsCommand`
  - `CurveEditorViewModel Curve` (Task 15), `BatteryViewModel Battery` (Task 16), `SettingsViewModel SettingsVm` (Task 16) — declare the properties in those tasks
  - `Task InitializeAsync()` (apply saved mode, start polling, hook resume), `void Shutdown()`
  - `sealed class SensorPoller` with `Start()`, `Stop()`, `SetInterval(int ms)`, event `Action<SensorSnapshot> Updated`

- [ ] **Step 1: SensorPoller**

`src/OpenAorus.App/ViewModels/SensorPoller.cs`:

```csharp
using System.Windows.Threading;
using OpenAorus.Hardware.Sensors;

namespace OpenAorus.App.ViewModels;

/// <summary>Reads sensors on a thread-pool thread on a timer and raises Updated on the UI thread.</summary>
public sealed class SensorPoller
{
    private readonly SensorReader _reader;
    private readonly DispatcherTimer _timer = new();
    private bool _reading;

    public event Action<SensorSnapshot>? Updated;

    public SensorPoller(SensorReader reader, int intervalMs)
    {
        _reader = reader;
        _timer.Interval = TimeSpan.FromMilliseconds(intervalMs);
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public void Start() { _timer.Start(); _ = TickAsync(); }
    public void Stop() => _timer.Stop();
    public void SetInterval(int ms) => _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(250, ms));

    private async Task TickAsync()
    {
        if (_reading) return;
        _reading = true;
        try
        {
            var snap = await Task.Run(_reader.Read);
            Updated?.Invoke(snap);
        }
        finally { _reading = false; }
    }
}
```

- [ ] **Step 2: MainViewModel**

`src/OpenAorus.App/ViewModels/MainViewModel.cs`:

```csharp
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Sensors;

namespace OpenAorus.App.ViewModels;

public enum BannerKind { None, Info, Warning, Error }

public partial class MainViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly SensorPoller _poller;

    [ObservableProperty] private FanMode _selectedMode;
    [ObservableProperty] private int _fixedPercent;
    [ObservableProperty] private SensorSnapshot _sensors = SensorSnapshot.Empty;
    [ObservableProperty] private string _bannerText = "";
    [ObservableProperty] private BannerKind _banner = BannerKind.None;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private bool _isWindowVisible;

    public bool CanWrite => _s.Profile.CanWrite;
    public string ModelLine => $"{_s.Profile.Name} · {_s.Profile.Status} · v{_s.Version}";
    public IReadOnlyList<FanMode> Modes { get; } = Enum.GetValues<FanMode>();

    public MainViewModel(AppServices services)
    {
        _s = services;
        _selectedMode = _s.Settings.Mode;
        _fixedPercent = _s.Settings.FixedPercent;
        _poller = new SensorPoller(_s.Sensors, _s.Settings.PollIntervalHiddenMs);
        _poller.Updated += OnSensors;

        Banner = _s.Profile.Status switch
        {
            ProfileStatus.Unknown => BannerKind.Error,
            ProfileStatus.Untested => BannerKind.Warning,
            _ => BannerKind.None,
        };
        BannerText = _s.Profile.Status switch
        {
            ProfileStatus.Unknown => $"'{_s.Profile.Name}' is not a recognised Gigabyte laptop. Read-only mode. Export diagnostics and open an issue.",
            ProfileStatus.Untested => "Untested model - compare fan duty read-back with Gigabyte Control Center before trusting it.",
            _ => "",
        };
    }

    public async Task InitializeAsync()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _poller.Start();
        if (CanWrite)
        {
            var r = await _s.ApplySavedAsync();
            StatusLine = r.Success ? $"Applied {SelectedMode} at startup" : $"Startup apply failed: {r.Error}";
            if (!r.Success) SetBanner(BannerKind.Error, r.Error!);
        }
    }

    public void Shutdown()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _poller.Stop();
    }

    partial void OnIsWindowVisibleChanged(bool value) =>
        _poller.SetInterval(value ? _s.Settings.PollIntervalVisibleMs : _s.Settings.PollIntervalHiddenMs);

    private void OnSensors(SensorSnapshot snap)
    {
        Sensors = snap;
        if (!snap.Ok && Banner == BannerKind.None) SetBanner(BannerKind.Error, snap.Error ?? "sensor read failed");
        else if (snap.Ok && Banner == BannerKind.Error && _s.Profile.Status != ProfileStatus.Unknown) ClearBanner();
    }

    private async void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || !CanWrite) return;
        await Task.Delay(3000); // let the EC and WMI provider wake up
        var r = await _s.ApplySavedAsync();
        StatusLine = r.Success ? $"Re-applied {SelectedMode} after resume" : $"Resume apply failed: {r.Error}";
    }

    [RelayCommand]
    private async Task SelectModeAsync(FanMode mode)
    {
        if (!CanWrite || IsBusy) return;
        IsBusy = true;
        try
        {
            var r = await _s.Fans.ApplyAsync(mode, FixedPercent, _s.Settings.ToCurve());
            if (r.Success)
            {
                SelectedMode = mode;
                _s.Settings.Mode = mode;
                _s.Store.Save(_s.Settings);
                StatusLine = $"{mode} applied";
                if (Banner == BannerKind.Error) ClearBanner();
            }
            else
            {
                StatusLine = $"{mode} failed";
                SetBanner(BannerKind.Error, r.Error!);
            }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ApplyFixedAsync()
    {
        _s.Settings.FixedPercent = FixedPercent;
        await SelectModeAsync(FanMode.Fixed);
    }

    [RelayCommand]
    private async Task ApplyCurveAsync() => await SelectModeAsync(FanMode.Custom);

    [RelayCommand]
    private void ExportDiagnostics()
    {
        try
        {
            var path = _s.WriteDiagnostics();
            StatusLine = $"Diagnostics saved to {path}";
            Clipboard.SetText(path);
        }
        catch (Exception ex) { SetBanner(BannerKind.Error, $"Export failed: {ex.Message}"); }
    }

    public void SetBanner(BannerKind kind, string text) { Banner = kind; BannerText = text; }

    public void ClearBanner()
    {
        Banner = _s.Profile.Status == ProfileStatus.Untested ? BannerKind.Warning : BannerKind.None;
        BannerText = Banner == BannerKind.Warning ? "Untested model - compare fan duty read-back with Gigabyte Control Center before trusting it." : "";
    }

    public string TrayTooltip => Sensors.Ok
        ? $"CPU {Sensors.CpuTemp}° · GPU {Sensors.GpuTemp}° · {Sensors.Fan1Rpm}/{Sensors.Fan2Rpm} rpm · {SelectedMode}"
        : "OpenAorus - sensor read failed";
}
```

- [ ] **Step 3: Build**

Run: `dotnet build OpenAorus.sln`
Expected: succeeds. (`SystemEvents` lives in `Microsoft.Win32.SystemEvents`, shipped with the Windows Desktop runtime; `Clipboard` is `System.Windows.Clipboard` via WPF.)

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "Add MainViewModel with mode commands, sensor polling and resume re-apply

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 14: Dark theme, tray icon, main window shell (modes + sensors + banner)

**Files:**
- Modify: `src/OpenAorus.App/Themes/Dark.xaml`, `src/OpenAorus.App/App.xaml.cs`
- Create: `src/OpenAorus.App/TrayIcon.cs`, `src/OpenAorus.App/Views/MainWindow.xaml`, `src/OpenAorus.App/Views/MainWindow.xaml.cs`, `src/OpenAorus.App/Converters.cs`

**Interfaces:**
- Consumes: `MainViewModel`, `AppServices`, `App.StartHidden`
- Produces: `sealed class TrayIcon : IDisposable` (`TrayIcon(MainViewModel vm, Action toggleWindow, Action quit)`, `void Refresh()`), `MainWindow(MainViewModel vm)` with `ToggleVisibility()`; converters `EnumEqualsConverter`, `EnumEqualsVisibilityConverter`, `BannerBrushConverter`, `BannerVisibleConverter`; static `FanModes` fields for `x:Static` command parameters

- [ ] **Step 1: Theme**

`src/OpenAorus.App/Themes/Dark.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <SolidColorBrush x:Key="Bg" Color="#FF15171B"/>
    <SolidColorBrush x:Key="Card" Color="#FF1E2127"/>
    <SolidColorBrush x:Key="CardBorder" Color="#FF2C3038"/>
    <SolidColorBrush x:Key="Fg" Color="#FFE6E8EC"/>
    <SolidColorBrush x:Key="FgMuted" Color="#FF8B919C"/>
    <SolidColorBrush x:Key="Accent" Color="#FFFF7A1A"/>
    <SolidColorBrush x:Key="AccentFg" Color="#FF15171B"/>
    <SolidColorBrush x:Key="Warn" Color="#FFC9A227"/>
    <SolidColorBrush x:Key="Err" Color="#FFD64545"/>
    <SolidColorBrush x:Key="Info" Color="#FF3A7BD5"/>

    <Style TargetType="Window">
        <Setter Property="Background" Value="{StaticResource Bg}"/>
        <Setter Property="Foreground" Value="{StaticResource Fg}"/>
        <Setter Property="FontFamily" Value="Segoe UI"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="UseLayoutRounding" Value="True"/>
    </Style>
    <Style TargetType="TextBlock">
        <Setter Property="Foreground" Value="{StaticResource Fg}"/>
    </Style>
    <Style x:Key="Muted" TargetType="TextBlock" BasedOn="{StaticResource {x:Type TextBlock}}">
        <Setter Property="Foreground" Value="{StaticResource FgMuted}"/>
        <Setter Property="FontSize" Value="11"/>
    </Style>
    <Style x:Key="CardStyle" TargetType="Border">
        <Setter Property="Background" Value="{StaticResource Card}"/>
        <Setter Property="BorderBrush" Value="{StaticResource CardBorder}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="CornerRadius" Value="6"/>
        <Setter Property="Padding" Value="10"/>
        <Setter Property="Margin" Value="0,0,0,8"/>
    </Style>
    <!-- Mode buttons: selected state comes from Tag (bound to SelectedMode == X), never from a local IsChecked,
         because a click on a ToggleButton would replace a one-way IsChecked binding with a local value. -->
    <Style x:Key="ModeButton" TargetType="Button">
        <Setter Property="Foreground" Value="{StaticResource Fg}"/>
        <Setter Property="Background" Value="{StaticResource Card}"/>
        <Setter Property="BorderBrush" Value="{StaticResource CardBorder}"/>
        <Setter Property="Padding" Value="0,7"/>
        <Setter Property="Margin" Value="2,0"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="B" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="1" CornerRadius="5" Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="B" Property="BorderBrush" Value="{StaticResource Accent}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.4"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
        <Style.Triggers>
            <DataTrigger Binding="{Binding Tag, RelativeSource={RelativeSource Self}}" Value="True">
                <Setter Property="Background" Value="{StaticResource Accent}"/>
                <Setter Property="Foreground" Value="{StaticResource AccentFg}"/>
                <Setter Property="FontWeight" Value="SemiBold"/>
            </DataTrigger>
        </Style.Triggers>
    </Style>
    <Style TargetType="Button">
        <Setter Property="Foreground" Value="{StaticResource Fg}"/>
        <Setter Property="Background" Value="{StaticResource Card}"/>
        <Setter Property="BorderBrush" Value="{StaticResource CardBorder}"/>
        <Setter Property="Padding" Value="10,5"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="1" CornerRadius="5" Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="BorderBrush" Value="{StaticResource Accent}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.4"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style TargetType="Slider">
        <Setter Property="Foreground" Value="{StaticResource Accent}"/>
        <Setter Property="IsSnapToTickEnabled" Value="True"/>
    </Style>
    <Style TargetType="CheckBox">
        <Setter Property="Foreground" Value="{StaticResource Fg}"/>
    </Style>
</ResourceDictionary>
```

- [ ] **Step 2: Converters**

`src/OpenAorus.App/Converters.cs`:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using OpenAorus.App.ViewModels;

namespace OpenAorus.App;

/// <summary>Button.Tag ⇐ (SelectedMode == parameter). Drives the selected look; clicks go through the command.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        value is not null && parameter is not null && value.ToString() == parameter.ToString();
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => Binding.DoNothing;
}

public sealed class BannerBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) => value switch
    {
        BannerKind.Warning => Application.Current.FindResource("Warn"),
        BannerKind.Error => Application.Current.FindResource("Err"),
        BannerKind.Info => Application.Current.FindResource("Info"),
        _ => Brushes.Transparent,
    };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class BannerVisibleConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        value is BannerKind k && k != BannerKind.None ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class EnumEqualsVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
```

- [ ] **Step 3: Tray icon** (WinForms `NotifyIcon`; icon drawn at runtime so no asset pipeline is needed)

`src/OpenAorus.App/TrayIcon.cs`:

```csharp
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using OpenAorus.App.ViewModels;
using OpenAorus.Hardware.Fans;

namespace OpenAorus.App;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon = new();
    private readonly MainViewModel _vm;
    private readonly Icon _ok = Draw(Color.FromArgb(0xFF, 0x7A, 0x1A));
    private readonly Icon _err = Draw(Color.FromArgb(0xD6, 0x45, 0x45));

    public TrayIcon(MainViewModel vm, Action toggleWindow, Action quit)
    {
        _vm = vm;
        _icon.Icon = _ok;
        _icon.Text = "OpenAorus";
        _icon.Visible = true;
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) toggleWindow(); };

        var menu = new ContextMenuStrip();
        foreach (var mode in Enum.GetValues<FanMode>())
        {
            var m = mode;
            menu.Items.Add(mode.ToString(), null, async (_, _) => await _vm.SelectModeCommand.ExecuteAsync(m));
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open", null, (_, _) => toggleWindow());
        menu.Items.Add("Quit", null, (_, _) => quit());
        _icon.ContextMenuStrip = menu;

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.Sensors) or nameof(MainViewModel.SelectedMode) or nameof(MainViewModel.Banner))
                Refresh();
        };
    }

    public void Refresh()
    {
        var text = _vm.TrayTooltip;
        _icon.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon limit
        _icon.Icon = _vm.Banner == BannerKind.Error ? _err : _ok;
    }

    private static Icon Draw(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 3, 3, 26, 26);
            using var pen = new Pen(Color.FromArgb(0x15, 0x17, 0x1B), 3);
            g.DrawLine(pen, 16, 8, 16, 24);   // simple "fan blade" glyph
            g.DrawLine(pen, 8, 16, 24, 16);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _ok.Dispose();
        _err.Dispose();
    }
}
```

- [ ] **Step 4: Main window**

`src/OpenAorus.App/Views/MainWindow.xaml`:

```xml
<Window x:Class="OpenAorus.App.Views.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:app="clr-namespace:OpenAorus.App"
        xmlns:views="clr-namespace:OpenAorus.App.Views"
        Title="OpenAorus" Width="440" Height="600" ResizeMode="CanMinimize"
        WindowStartupLocation="Manual" ShowInTaskbar="False" Topmost="False">
    <Window.Resources>
        <app:EnumEqualsConverter x:Key="EnumEq"/>
        <app:EnumEqualsVisibilityConverter x:Key="EnumVis"/>
        <app:BannerBrushConverter x:Key="BannerBrush"/>
        <app:BannerVisibleConverter x:Key="BannerVis"/>
    </Window.Resources>
    <Grid Margin="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Banner -->
        <Border Grid.Row="0" CornerRadius="5" Padding="8" Margin="0,0,0,8"
                Background="{Binding Banner, Converter={StaticResource BannerBrush}}"
                Visibility="{Binding Banner, Converter={StaticResource BannerVis}}">
            <TextBlock Text="{Binding BannerText}" TextWrapping="Wrap" Foreground="#FF15171B"/>
        </Border>

        <!-- Modes -->
        <UniformGrid Grid.Row="1" Columns="6" Margin="0,0,0,8" IsEnabled="{Binding CanWrite}">
            <Button Style="{StaticResource ModeButton}" Content="Quiet"
                    Tag="{Binding SelectedMode, Converter={StaticResource EnumEq}, ConverterParameter=Quiet}"
                    Command="{Binding SelectModeCommand}" CommandParameter="{x:Static app:FanModes.Quiet}"/>
            <Button Style="{StaticResource ModeButton}" Content="Normal"
                    Tag="{Binding SelectedMode, Converter={StaticResource EnumEq}, ConverterParameter=Normal}"
                    Command="{Binding SelectModeCommand}" CommandParameter="{x:Static app:FanModes.Normal}"/>
            <Button Style="{StaticResource ModeButton}" Content="Gaming"
                    Tag="{Binding SelectedMode, Converter={StaticResource EnumEq}, ConverterParameter=Gaming}"
                    Command="{Binding SelectModeCommand}" CommandParameter="{x:Static app:FanModes.Gaming}"/>
            <Button Style="{StaticResource ModeButton}" Content="Turbo"
                    Tag="{Binding SelectedMode, Converter={StaticResource EnumEq}, ConverterParameter=Turbo}"
                    Command="{Binding SelectModeCommand}" CommandParameter="{x:Static app:FanModes.Turbo}"/>
            <Button Style="{StaticResource ModeButton}" Content="Fixed"
                    Tag="{Binding SelectedMode, Converter={StaticResource EnumEq}, ConverterParameter=Fixed}"
                    Command="{Binding SelectModeCommand}" CommandParameter="{x:Static app:FanModes.Fixed}"/>
            <Button Style="{StaticResource ModeButton}" Content="Custom"
                    Tag="{Binding SelectedMode, Converter={StaticResource EnumEq}, ConverterParameter=Custom}"
                    Command="{Binding SelectModeCommand}" CommandParameter="{x:Static app:FanModes.Custom}"/>
        </UniformGrid>

        <!-- Sensors -->
        <Border Grid.Row="2" Style="{StaticResource CardStyle}">
            <UniformGrid Columns="5">
                <StackPanel><TextBlock Style="{StaticResource Muted}" Text="CPU"/><TextBlock FontSize="20" Text="{Binding Sensors.CpuTemp, StringFormat={}{0}°}"/></StackPanel>
                <StackPanel><TextBlock Style="{StaticResource Muted}" Text="GPU"/><TextBlock FontSize="20" Text="{Binding Sensors.GpuTemp, StringFormat={}{0}°}"/></StackPanel>
                <StackPanel><TextBlock Style="{StaticResource Muted}" Text="Fan 1"/><TextBlock FontSize="20" Text="{Binding Sensors.Fan1Rpm}"/></StackPanel>
                <StackPanel><TextBlock Style="{StaticResource Muted}" Text="Fan 2"/><TextBlock FontSize="20" Text="{Binding Sensors.Fan2Rpm}"/></StackPanel>
                <StackPanel><TextBlock Style="{StaticResource Muted}" Text="Duty"/><TextBlock FontSize="20" Text="{Binding Sensors.Fan1DutyPercent, StringFormat={}{0}%}"/></StackPanel>
            </UniformGrid>
        </Border>

        <!-- Context panel: filled by Task 15 (Fixed slider, curve editor) and mode descriptions -->
        <Border Grid.Row="3" Style="{StaticResource CardStyle}">
            <ContentControl x:Name="ContextPanel"/>
        </Border>

        <!-- Battery card: Task 16 -->
        <ContentControl Grid.Row="4" x:Name="BatteryPanel"/>

        <!-- Footer -->
        <DockPanel Grid.Row="5" LastChildFill="True">
            <Button DockPanel.Dock="Right" Content="⚙" Width="32" Click="Settings_Click" ToolTip="Settings"/>
            <Button DockPanel.Dock="Right" Content="Diagnostics" Margin="0,0,6,0" Command="{Binding ExportDiagnosticsCommand}"/>
            <StackPanel>
                <TextBlock Style="{StaticResource Muted}" Text="{Binding ModelLine}"/>
                <TextBlock Style="{StaticResource Muted}" Text="{Binding StatusLine}" TextTrimming="CharacterEllipsis"/>
            </StackPanel>
        </DockPanel>
    </Grid>
</Window>
```

`CommandParameter="{x:Static ...}"` needs static fields; add to `Converters.cs`:

```csharp
public static class FanModes
{
    public static readonly Hardware.Fans.FanMode Quiet = Hardware.Fans.FanMode.Quiet;
    public static readonly Hardware.Fans.FanMode Normal = Hardware.Fans.FanMode.Normal;
    public static readonly Hardware.Fans.FanMode Gaming = Hardware.Fans.FanMode.Gaming;
    public static readonly Hardware.Fans.FanMode Turbo = Hardware.Fans.FanMode.Turbo;
    public static readonly Hardware.Fans.FanMode Fixed = Hardware.Fans.FanMode.Fixed;
    public static readonly Hardware.Fans.FanMode Custom = Hardware.Fans.FanMode.Custom;
}
```

`src/OpenAorus.App/Views/MainWindow.xaml.cs`:

```csharp
using System.ComponentModel;
using System.Windows;
using OpenAorus.App.ViewModels;

namespace OpenAorus.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        IsVisibleChanged += (_, _) => _vm.IsWindowVisible = IsVisible;
    }

    /// <summary>Close hides to tray; Quit in the tray menu really exits.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    public void ToggleVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized) { Hide(); return; }
        PlaceNearTray();
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void PlaceNearTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;
    }

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke();

    public event Action? SettingsRequested; // wired in Task 16
}
```

- [ ] **Step 5: Wire startup**

Replace the tail of `App.OnStartup` (the "Task 14 replaces this" block) with:

```csharp
        var vm = new ViewModels.MainViewModel(Services);
        _window = new Views.MainWindow(vm);
        _tray = new TrayIcon(vm, () => _window.ToggleVisibility(), () => Shutdown(0));
        if (!StartHidden) _window.ToggleVisibility();
        await vm.InitializeAsync();
        _vm = vm;
```

and add fields + shutdown to `App`:

```csharp
    private Views.MainWindow? _window;
    private TrayIcon? _tray;
    private ViewModels.MainViewModel? _vm;

    protected override void OnExit(ExitEventArgs e)
    {
        _vm?.Shutdown();
        _tray?.Dispose();
        base.OnExit(e);
    }
```

Add `using System.Windows;` is already present; add `using OpenAorus.App.Views;` if you prefer short names.

- [ ] **Step 6: Build and OWNER VERIFY**

Run: `dotnet build OpenAorus.sln` → succeeds.

Owner runs `dotnet run --project src/OpenAorus.App -- --show` elevated. Expected: orange tray icon appears, window opens bottom-right with six mode buttons, live temps/RPM updating every second, status line "Applied Normal at startup". Clicking **Gaming** makes fans audibly ramp within ~2 s and `Duty` rises; clicking **Quiet** brings them back. Tray right-click menu switches modes too. Closing the window hides it; Quit exits. Paste the status line text and any banner into the task notes.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add dark theme, tray icon and main window with modes and live sensors

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 15: Fixed-duty slider and curve editor

**Files:**
- Create: `src/OpenAorus.App/ViewModels/CurveEditorViewModel.cs`, `src/OpenAorus.App/Views/CurveEditor.xaml`, `src/OpenAorus.App/Views/CurveEditor.xaml.cs`, `src/OpenAorus.App/Views/ContextPanel.xaml`, `src/OpenAorus.App/Views/ContextPanel.xaml.cs`
- Modify: `src/OpenAorus.App/ViewModels/MainViewModel.cs` (add `Curve` property), `src/OpenAorus.App/Views/MainWindow.xaml.cs` (set `ContextPanel.Content`)

**Interfaces:**
- Consumes: `FanCurve`, `FanCurvePoint`, `AppSettings.Curve`, `MainViewModel.ApplyCurveCommand`, `MainViewModel.ApplyFixedCommand`, `MainViewModel.FixedPercent`
- Produces: `partial class CurveEditorViewModel : ObservableObject` with `ObservableCollection<CurvePointVm> Points`, `string ValidationText`, `bool IsValid`, `FanCurve ToCurve()`, `void LoadFrom(IEnumerable<FanCurvePoint>)`, commands `AddPointCommand`, `RemovePointCommand(CurvePointVm)`, `ResetCommand`; `partial class CurvePointVm : ObservableObject` with `int Temperature`, `int DutyPercent`; `MainViewModel.Curve` (`CurveEditorViewModel`)

- [ ] **Step 1: Curve editor view model**

`src/OpenAorus.App/ViewModels/CurveEditorViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAorus.Hardware.Fans;

namespace OpenAorus.App.ViewModels;

public partial class CurvePointVm : ObservableObject
{
    [ObservableProperty] private int _temperature;
    [ObservableProperty] private int _dutyPercent;
    public CurvePointVm(int t, int d) { _temperature = t; _dutyPercent = d; }
    public FanCurvePoint ToPoint() => new(Temperature, DutyPercent);
}

public partial class CurveEditorViewModel : ObservableObject
{
    public ObservableCollection<CurvePointVm> Points { get; } = new();

    [ObservableProperty] private string _validationText = "";
    [ObservableProperty] private bool _isValid = true;

    public event Action? Changed;

    public CurveEditorViewModel(IEnumerable<FanCurvePoint> initial)
    {
        Points.CollectionChanged += OnCollectionChanged;
        LoadFrom(initial);
    }

    public void LoadFrom(IEnumerable<FanCurvePoint> points)
    {
        foreach (var p in Points) p.PropertyChanged -= OnPointChanged;
        Points.Clear();
        foreach (var p in points) Points.Add(Attach(new CurvePointVm(p.Temperature, p.DutyPercent)));
        Revalidate();
    }

    public FanCurve ToCurve() => new(Points.Select(p => p.ToPoint()));

    private CurvePointVm Attach(CurvePointVm vm) { vm.PropertyChanged += OnPointChanged; return vm; }

    private void OnCollectionChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) foreach (CurvePointVm p in e.NewItems) Attach(p);
        if (e.OldItems is not null) foreach (CurvePointVm p in e.OldItems) p.PropertyChanged -= OnPointChanged;
        Revalidate();
    }

    private void OnPointChanged(object? s, PropertyChangedEventArgs e) => Revalidate();

    public void Revalidate()
    {
        var errors = ToCurve().Validate();
        IsValid = errors.Count == 0;
        ValidationText = IsValid ? $"{Points.Count} points" : errors[0];
        Changed?.Invoke();
    }

    [RelayCommand]
    private void AddPoint()
    {
        if (Points.Count >= FanCurve.MaxPoints) return;
        var last = Points.LastOrDefault();
        var t = Math.Min(100, (last?.Temperature ?? 35) + 5);
        var d = Math.Min(100, (last?.DutyPercent ?? 25) + 5);
        Points.Add(new CurvePointVm(t, d));
    }

    [RelayCommand]
    private void RemovePoint(CurvePointVm? p)
    {
        if (p is not null && Points.Count > FanCurve.MinPoints) Points.Remove(p);
    }

    [RelayCommand]
    private void Reset() => LoadFrom(FanCurve.Default.Points);
}
```

Add to `MainViewModel`:

```csharp
    public CurveEditorViewModel Curve { get; }
```

and in its constructor, after `_poller.Updated += OnSensors;`:

```csharp
        Curve = new CurveEditorViewModel(_s.Settings.Curve);
```

and change `ApplyCurveAsync` to persist the editor's points before applying:

```csharp
    [RelayCommand]
    private async Task ApplyCurveAsync()
    {
        if (!Curve.IsValid) { SetBanner(BannerKind.Warning, Curve.ValidationText); return; }
        _s.Settings.Curve = Curve.ToCurve().Points.ToList();
        await SelectModeAsync(FanMode.Custom);
    }
```

- [ ] **Step 2: Curve editor view (canvas drag + table)**

`src/OpenAorus.App/Views/CurveEditor.xaml`:

```xml
<UserControl x:Class="OpenAorus.App.Views.CurveEditor"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="160"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        <Border Grid.Row="0" Background="#FF15171B" CornerRadius="4">
            <Canvas x:Name="Plot" ClipToBounds="True" Background="Transparent"
                    MouseLeftButtonDown="Plot_MouseLeftButtonDown" MouseMove="Plot_MouseMove"
                    MouseLeftButtonUp="Plot_MouseLeftButtonUp" SizeChanged="Plot_SizeChanged"/>
        </Border>
        <DataGrid Grid.Row="1" Margin="0,6,0,6" ItemsSource="{Binding Points}" AutoGenerateColumns="False"
                  CanUserAddRows="False" HeadersVisibility="Column" Background="#FF1E2127" Foreground="#FFE6E8EC"
                  RowBackground="#FF1E2127" AlternatingRowBackground="#FF23272E" GridLinesVisibility="None"
                  BorderThickness="0" MaxHeight="140" SelectionMode="Single" x:Name="Table">
            <DataGrid.Columns>
                <DataGridTextColumn Header="°C" Binding="{Binding Temperature, UpdateSourceTrigger=PropertyChanged}" Width="*"/>
                <DataGridTextColumn Header="Duty %" Binding="{Binding DutyPercent, UpdateSourceTrigger=PropertyChanged}" Width="*"/>
                <DataGridTemplateColumn Width="40">
                    <DataGridTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <Button Content="✕" Padding="4,0" Command="{Binding DataContext.RemovePointCommand, RelativeSource={RelativeSource AncestorType=DataGrid}}" CommandParameter="{Binding}"/>
                        </DataTemplate>
                    </DataGridTemplateColumn.CellTemplate>
                </DataGridTemplateColumn>
            </DataGrid.Columns>
        </DataGrid>
        <DockPanel Grid.Row="2">
            <Button DockPanel.Dock="Right" Content="Apply" Margin="6,0,0,0" x:Name="ApplyButton"/>
            <Button DockPanel.Dock="Right" Content="Reset" Margin="6,0,0,0" Command="{Binding ResetCommand}"/>
            <Button DockPanel.Dock="Right" Content="+ Point" Command="{Binding AddPointCommand}"/>
            <TextBlock Text="{Binding ValidationText}" VerticalAlignment="Center" Foreground="#FF8B919C"/>
        </DockPanel>
    </Grid>
</UserControl>
```

`src/OpenAorus.App/Views/CurveEditor.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenAorus.App.ViewModels;

namespace OpenAorus.App.Views;

public partial class CurveEditor : UserControl
{
    private CurveEditorViewModel? _vm;
    private CurvePointVm? _dragging;

    public CurveEditor()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.Changed -= Redraw;
            _vm = e.NewValue as CurveEditorViewModel;
            if (_vm is not null) _vm.Changed += Redraw;
            Redraw();
        };
    }

    public Button Apply => ApplyButton;

    private double W => Math.Max(1, Plot.ActualWidth);
    private double H => Math.Max(1, Plot.ActualHeight);
    private Point ToCanvas(int temp, int duty) => new(temp / 100.0 * W, H - duty / 100.0 * H);
    private (int temp, int duty) FromCanvas(Point p) =>
        ((int)Math.Round(Math.Clamp(p.X / W, 0, 1) * 100), (int)Math.Round(Math.Clamp(1 - p.Y / H, 0, 1) * 100));

    private void Redraw()
    {
        Plot.Children.Clear();
        if (_vm is null) return;
        var grid = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        for (var i = 1; i < 10; i++)
        {
            Plot.Children.Add(new Line { X1 = i * W / 10, X2 = i * W / 10, Y1 = 0, Y2 = H, Stroke = grid, StrokeThickness = 1 });
            Plot.Children.Add(new Line { X1 = 0, X2 = W, Y1 = i * H / 10, Y2 = i * H / 10, Stroke = grid, StrokeThickness = 1 });
        }
        var accent = (Brush)FindResource("Accent");
        var poly = new Polyline { Stroke = accent, StrokeThickness = 2 };
        foreach (var p in _vm.Points) poly.Points.Add(ToCanvas(p.Temperature, p.DutyPercent));
        Plot.Children.Add(poly);
        foreach (var p in _vm.Points)
        {
            var c = ToCanvas(p.Temperature, p.DutyPercent);
            var dot = new Ellipse { Width = 10, Height = 10, Fill = accent, Tag = p, Cursor = Cursors.Hand };
            Canvas.SetLeft(dot, c.X - 5); Canvas.SetTop(dot, c.Y - 5);
            Plot.Children.Add(dot);
        }
    }

    private void Plot_SizeChanged(object s, SizeChangedEventArgs e) => Redraw();

    private void Plot_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Ellipse { Tag: CurvePointVm p })
        {
            _dragging = p;
            Plot.CaptureMouse();
        }
    }

    private void Plot_MouseMove(object s, MouseEventArgs e)
    {
        if (_dragging is null || _vm is null) return;
        var (t, d) = FromCanvas(e.GetPosition(Plot));
        var i = _vm.Points.IndexOf(_dragging);
        var lo = i > 0 ? _vm.Points[i - 1].Temperature + 1 : 0;
        var hi = i < _vm.Points.Count - 1 ? _vm.Points[i + 1].Temperature - 1 : 100;
        _dragging.Temperature = Math.Clamp(t, lo, hi);
        var dlo = i > 0 ? _vm.Points[i - 1].DutyPercent : 0;
        var dhi = i < _vm.Points.Count - 1 ? _vm.Points[i + 1].DutyPercent : 100;
        _dragging.DutyPercent = Math.Clamp(d, dlo, dhi);
    }

    private void Plot_MouseLeftButtonUp(object s, MouseButtonEventArgs e)
    {
        _dragging = null;
        Plot.ReleaseMouseCapture();
    }
}
```

- [ ] **Step 3: Context panel that switches on the selected mode**

`src/OpenAorus.App/Views/ContextPanel.xaml`:

```xml
<UserControl x:Class="OpenAorus.App.Views.ContextPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:app="clr-namespace:OpenAorus.App"
             xmlns:views="clr-namespace:OpenAorus.App.Views">
    <UserControl.Resources>
        <app:EnumEqualsVisibilityConverter x:Key="EnumVis"/>
    </UserControl.Resources>
    <Grid>
        <!-- Fixed -->
        <StackPanel Visibility="{Binding SelectedMode, Converter={StaticResource EnumVis}, ConverterParameter=Fixed}">
            <TextBlock Text="Fixed duty for both fans" Margin="0,0,0,6"/>
            <DockPanel>
                <TextBlock DockPanel.Dock="Right" Width="44" TextAlignment="Right" Text="{Binding FixedPercent, StringFormat={}{0}%}"/>
                <Slider Minimum="0" Maximum="100" TickFrequency="5" Value="{Binding FixedPercent}" x:Name="FixedSlider"
                        Thumb.DragCompleted="FixedSlider_DragCompleted"/>
            </DockPanel>
            <TextBlock Style="{StaticResource Muted}" Text="Applied when you release the slider." Margin="0,4,0,0"/>
        </StackPanel>

        <!-- Custom -->
        <views:CurveEditor x:Name="Editor" DataContext="{Binding Curve}"
                           Visibility="{Binding DataContext.SelectedMode, RelativeSource={RelativeSource AncestorType=UserControl}, Converter={StaticResource EnumVis}, ConverterParameter=Custom}"/>

        <!-- Descriptions -->
        <TextBlock TextWrapping="Wrap" Style="{StaticResource Muted}" Visibility="{Binding SelectedMode, Converter={StaticResource EnumVis}, ConverterParameter=Quiet}"
                   Text="Quiet: firmware fan table with the EC's 'quiet' bias. Lowest noise, thermals a little higher."/>
        <TextBlock TextWrapping="Wrap" Style="{StaticResource Muted}" Visibility="{Binding SelectedMode, Converter={StaticResource EnumVis}, ConverterParameter=Normal}"
                   Text="Normal: the EC's default fan table."/>
        <TextBlock TextWrapping="Wrap" Style="{StaticResource Muted}" Visibility="{Binding SelectedMode, Converter={StaticResource EnumVis}, ConverterParameter=Gaming}"
                   Text="Gaming: EC auto mode with an aggressive table. What GCC calls Gaming / Power."/>
        <TextBlock TextWrapping="Wrap" Style="{StaticResource Muted}" Visibility="{Binding SelectedMode, Converter={StaticResource EnumVis}, ConverterParameter=Turbo}"
                   Text="Turbo: both fans pinned at 100 %."/>
    </Grid>
</UserControl>
```

`src/OpenAorus.App/Views/ContextPanel.xaml.cs`:

```csharp
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenAorus.App.ViewModels;

namespace OpenAorus.App.Views;

public partial class ContextPanel : UserControl
{
    public ContextPanel()
    {
        InitializeComponent();
        Editor.Apply.Click += async (_, _) =>
        {
            if (DataContext is MainViewModel vm) await vm.ApplyCurveCommand.ExecuteAsync(null);
        };
    }

    private async void FixedSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DataContext is MainViewModel vm) await vm.ApplyFixedCommand.ExecuteAsync(null);
    }
}
```

In `MainWindow.xaml.cs` constructor, after `DataContext = vm;`:

```csharp
        ContextPanel.Content = new ContextPanel { DataContext = vm };
```

- [ ] **Step 4: Build and OWNER VERIFY**

Run: `dotnet build OpenAorus.sln` → succeeds.

Owner: select **Fixed**, drag the slider to 30 % and release; fans settle lower and `Duty` reads ~30 %. Select **Custom**, drag the 80 °C point up, click Apply; status "Custom applied"; run `--dump` afterwards and confirm the fan table lines `[0]..[n]` match the editor (temperatures equal, duties equal `round(percent*229/100)`). Report both.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add fixed-duty slider and draggable 15-point curve editor

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 16: Battery card and settings panel (start with Windows, GCC takeover)

**Files:**
- Create: `src/OpenAorus.App/ViewModels/BatteryViewModel.cs`, `src/OpenAorus.App/ViewModels/SettingsViewModel.cs`, `src/OpenAorus.App/Views/BatteryPanel.xaml(.cs)`, `src/OpenAorus.App/Views/SettingsWindow.xaml(.cs)`
- Modify: `src/OpenAorus.App/ViewModels/MainViewModel.cs` (add `Battery`, `SettingsVm`), `src/OpenAorus.App/Views/MainWindow.xaml.cs` (set `BatteryPanel.Content`, open settings)

**Interfaces:**
- Consumes: `BatteryController`, `BatteryStatus`, `StartupTask`, `GccTakeover`, `IGccSystem`, `AppSettings`, `SettingsStore`
- Produces: `BatteryViewModel` (`bool LimitEnabled`, `int StopPercent`, `int CycleCount`, `string StatusText`, `IAsyncRelayCommand ApplyCommand`, `Task RefreshAsync()`); `SettingsViewModel` (`bool StartWithWindows`, `bool GccTakenOver`, `bool GccActive`, `int PollVisibleMs`, `int PollHiddenMs`, `string Message`, commands `ToggleStartupCommand`, `ToggleTakeoverCommand`, `SaveIntervalsCommand`, `Task RefreshAsync()`)

- [ ] **Step 1: Battery view model**

`src/OpenAorus.App/ViewModels/BatteryViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAorus.Hardware.Battery;

namespace OpenAorus.App.ViewModels;

public partial class BatteryViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Action<BannerKind, string> _banner;

    [ObservableProperty] private bool _limitEnabled;
    [ObservableProperty] private int _stopPercent;
    [ObservableProperty] private int _cycleCount;
    [ObservableProperty] private string _statusText = "";

    public bool CanWrite => _s.Profile.CanWrite;

    public BatteryViewModel(AppServices s, Action<BannerKind, string> banner)
    {
        _s = s;
        _banner = banner;
        _limitEnabled = s.Settings.ChargeLimitEnabled;
        _stopPercent = s.Settings.ChargeStopPercent;
    }

    public async Task RefreshAsync()
    {
        var st = await Task.Run(_s.Battery.Read);
        if (!st.Ok) { StatusText = "battery read failed"; return; }
        CycleCount = st.CycleCount;
        LimitEnabled = st.CustomLimitEnabled;
        if (st.CustomLimitEnabled) StopPercent = st.StopPercent;
        StatusText = st.CustomLimitEnabled ? $"EC stops charging at {st.StopPercent} %" : "EC charges to 100 %";
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanWrite) return;
        StopPercent = Math.Clamp(StopPercent / 5 * 5, BatteryController.MinStop, BatteryController.MaxStop);
        var r = await Task.Run(() => _s.Battery.SetLimit(LimitEnabled, StopPercent));
        if (!r.Success) { _banner(BannerKind.Error, r.Error!); return; }
        _s.Settings.ChargeLimitEnabled = LimitEnabled;
        _s.Settings.ChargeStopPercent = StopPercent;
        _s.Store.Save(_s.Settings);
        await RefreshAsync();
    }
}
```

- [ ] **Step 2: Settings view model**

`src/OpenAorus.App/ViewModels/SettingsViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAorus.Hardware.System;

namespace OpenAorus.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _s;

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _gccTakenOver;
    [ObservableProperty] private bool _gccActive;
    [ObservableProperty] private int _pollVisibleMs;
    [ObservableProperty] private int _pollHiddenMs;
    [ObservableProperty] private string _message = "";

    public string SettingsPath => _s.Store.Path;

    public SettingsViewModel(AppServices s)
    {
        _s = s;
        _pollVisibleMs = s.Settings.PollIntervalVisibleMs;
        _pollHiddenMs = s.Settings.PollIntervalHiddenMs;
        _gccTakenOver = s.Settings.Takeover is not null;
    }

    public async Task RefreshAsync()
    {
        StartWithWindows = await Task.Run(StartupTask.IsEnabled);
        GccActive = await Task.Run(() => GccTakeover.IsGccActive(_s.Gcc));
        _s.Settings.StartWithWindows = StartWithWindows;
        if (GccTakenOver && GccActive)
            Message = "Gigabyte Control Center is running again (an update may have re-enabled it). Toggle takeover off and on to re-park it.";
    }

    [RelayCommand]
    private async Task ToggleStartupAsync()
    {
        var ok = await Task.Run(() => StartWithWindows ? StartupTask.Disable() : StartupTask.Enable(_s.ExePath));
        if (!ok) { Message = "schtasks failed - are you elevated?"; return; }
        StartWithWindows = !StartWithWindows;
        _s.Settings.StartWithWindows = StartWithWindows;
        _s.Store.Save(_s.Settings);
        Message = StartWithWindows ? "Logon task 'OpenAorus' created (highest privileges, no UAC prompt)." : "Logon task removed.";
    }

    [RelayCommand]
    private async Task ToggleTakeoverAsync()
    {
        if (GccTakenOver)
        {
            var state = _s.Settings.Takeover;
            if (state is not null) await Task.Run(() => GccTakeover.Restore(_s.Gcc, state));
            _s.Settings.Takeover = null;
            GccTakenOver = false;
            Message = "Gigabyte Control Center autostart restored. It will run again after the next logon.";
        }
        else
        {
            _s.Settings.Takeover = await Task.Run(() => GccTakeover.TakeOver(_s.Gcc));
            GccTakenOver = true;
            Message = "GCC task, autostart entry and service disabled; running GCC processes closed.";
        }
        _s.Store.Save(_s.Settings);
        await RefreshAsync();
    }

    [RelayCommand]
    private void SaveIntervals()
    {
        _s.Settings.PollIntervalVisibleMs = Math.Clamp(PollVisibleMs, 250, 10000);
        _s.Settings.PollIntervalHiddenMs = Math.Clamp(PollHiddenMs, 1000, 60000);
        PollVisibleMs = _s.Settings.PollIntervalVisibleMs;
        PollHiddenMs = _s.Settings.PollIntervalHiddenMs;
        _s.Store.Save(_s.Settings);
        Message = "Poll intervals saved (take effect on next show/hide).";
    }
}
```

Add to `MainViewModel`:

```csharp
    public BatteryViewModel Battery { get; }
    public SettingsViewModel SettingsVm { get; }
```

constructor (after `Curve = ...`):

```csharp
        Battery = new BatteryViewModel(_s, SetBanner);
        SettingsVm = new SettingsViewModel(_s);
```

and at the end of `InitializeAsync()`:

```csharp
        await Battery.RefreshAsync();
        await SettingsVm.RefreshAsync();
```

- [ ] **Step 3: Battery panel**

`src/OpenAorus.App/Views/BatteryPanel.xaml`:

```xml
<UserControl x:Class="OpenAorus.App.Views.BatteryPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Border Style="{StaticResource CardStyle}" IsEnabled="{Binding CanWrite}">
        <StackPanel>
            <DockPanel>
                <TextBlock DockPanel.Dock="Right" Style="{StaticResource Muted}" Text="{Binding CycleCount, StringFormat={}{0} cycles}"/>
                <CheckBox Content="Limit charge" IsChecked="{Binding LimitEnabled}" Checked="Limit_Changed" Unchecked="Limit_Changed"/>
            </DockPanel>
            <DockPanel Margin="0,6,0,0" IsEnabled="{Binding LimitEnabled}">
                <TextBlock DockPanel.Dock="Right" Width="44" TextAlignment="Right" Text="{Binding StopPercent, StringFormat={}{0}%}"/>
                <Slider Minimum="60" Maximum="100" TickFrequency="5" Value="{Binding StopPercent}"
                        Thumb.DragCompleted="Stop_DragCompleted"/>
            </DockPanel>
            <TextBlock Style="{StaticResource Muted}" Text="{Binding StatusText}" Margin="0,4,0,0"/>
        </StackPanel>
    </Border>
</UserControl>
```

`src/OpenAorus.App/Views/BatteryPanel.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenAorus.App.ViewModels;

namespace OpenAorus.App.Views;

public partial class BatteryPanel : UserControl
{
    public BatteryPanel() => InitializeComponent();

    private async void Limit_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && DataContext is BatteryViewModel vm) await vm.ApplyCommand.ExecuteAsync(null);
    }

    private async void Stop_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DataContext is BatteryViewModel vm) await vm.ApplyCommand.ExecuteAsync(null);
    }
}
```

In `MainWindow.xaml.cs` constructor: `BatteryPanel.Content = new BatteryPanel { DataContext = vm.Battery };`

- [ ] **Step 4: Settings window**

`src/OpenAorus.App/Views/SettingsWindow.xaml`:

```xml
<Window x:Class="OpenAorus.App.Views.SettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="OpenAorus settings" Width="420" SizeToContent="Height" ResizeMode="NoResize"
        WindowStartupLocation="CenterOwner" ShowInTaskbar="False">
    <StackPanel Margin="14">
        <Border Style="{StaticResource CardStyle}">
            <DockPanel>
                <Button DockPanel.Dock="Right" Content="Toggle" Command="{Binding ToggleStartupCommand}"/>
                <StackPanel>
                    <CheckBox Content="Start with Windows (no UAC prompt)" IsChecked="{Binding StartWithWindows, Mode=OneWay}" IsHitTestVisible="False"/>
                    <TextBlock Style="{StaticResource Muted}" Text="Creates a logon task that runs OpenAorus elevated in the tray."/>
                </StackPanel>
            </DockPanel>
        </Border>
        <Border Style="{StaticResource CardStyle}">
            <DockPanel>
                <Button DockPanel.Dock="Right" Content="Toggle" Command="{Binding ToggleTakeoverCommand}"/>
                <StackPanel>
                    <CheckBox Content="Take over from Gigabyte Control Center" IsChecked="{Binding GccTakenOver, Mode=OneWay}" IsHitTestVisible="False"/>
                    <TextBlock Style="{StaticResource Muted}" TextWrapping="Wrap"
                               Text="Disables GCC's logon task, AorusFusion autostart and SMV4_Service, and closes GCC processes. Reversible. GCC stays installed so its WMI schema keeps working."/>
                    <TextBlock Style="{StaticResource Muted}" Text="GCC is currently running." Foreground="{StaticResource Warn}">
                        <TextBlock.Style>
                            <Style TargetType="TextBlock" BasedOn="{StaticResource Muted}">
                                <Setter Property="Visibility" Value="Collapsed"/>
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding GccActive}" Value="True"><Setter Property="Visibility" Value="Visible"/></DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </TextBlock.Style>
                    </TextBlock>
                </StackPanel>
            </DockPanel>
        </Border>
        <Border Style="{StaticResource CardStyle}">
            <StackPanel>
                <TextBlock Text="Sensor polling (ms)"/>
                <UniformGrid Columns="3" Margin="0,4,0,0">
                    <StackPanel Margin="0,0,6,0"><TextBlock Style="{StaticResource Muted}" Text="Window open"/><TextBox Text="{Binding PollVisibleMs}"/></StackPanel>
                    <StackPanel Margin="0,0,6,0"><TextBlock Style="{StaticResource Muted}" Text="In tray"/><TextBox Text="{Binding PollHiddenMs}"/></StackPanel>
                    <Button Content="Save" VerticalAlignment="Bottom" Command="{Binding SaveIntervalsCommand}"/>
                </UniformGrid>
            </StackPanel>
        </Border>
        <TextBlock Style="{StaticResource Muted}" Text="{Binding SettingsPath}" TextTrimming="CharacterEllipsis"/>
        <TextBlock Text="{Binding Message}" TextWrapping="Wrap" Margin="0,8,0,0"/>
    </StackPanel>
</Window>
```

`src/OpenAorus.App/Views/SettingsWindow.xaml.cs`:

```csharp
using System.Windows;
using OpenAorus.App.ViewModels;

namespace OpenAorus.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => await vm.RefreshAsync();
    }
}
```

In `MainWindow.xaml.cs` replace the `Settings_Click` / `SettingsRequested` pair with:

```csharp
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow(_vm.SettingsVm) { Owner = this };
        w.ShowDialog();
    }
```

- [ ] **Step 5: Build and OWNER VERIFY**

Run: `dotnet build OpenAorus.sln` → succeeds.

Owner, elevated:
1. Battery: tick "Limit charge", set 80 %. `--dump` shows `GetChargePolicy: Data=4` and `GetChargeStop: Data=80`. Untick → `Data=0` / `Data=100`.
2. Settings → Start with Windows → Toggle. `schtasks /Query /TN OpenAorus /V /FO LIST` shows `Run As User: <you>`, `Highest`, trigger at logon. Log off/on: tray icon appears without a UAC prompt. Toggle again removes it.
3. Settings → Take over → Toggle. `Get-Process GCC,FusionStation -ErrorAction SilentlyContinue` returns nothing; `schtasks /Query /TN GCC /FO LIST /V` shows `Disabled`; `Get-Service SMV4_Service` shows `Stopped`/`Disabled`. Toggle back restores all three. Confirm that after takeover, switching modes in OpenAorus is no longer overridden after a minute (GCC's "AI" watcher is gone).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add battery limit card and settings window (startup task, GCC takeover)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 17: CI, publish profiles, README, release

**Files:**
- Create: `.github/workflows/build.yml`
- Modify: `README.md`, `src/OpenAorus.App/OpenAorus.App.csproj` (publish properties)

- [ ] **Step 1: Publish settings**

Add to the `PropertyGroup` in `src/OpenAorus.App/OpenAorus.App.csproj`:

```xml
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <DebugType>embedded</DebugType>
```

Verify both flavours locally:

```bash
dotnet publish src/OpenAorus.App -c Release -r win-x64 --self-contained true  -o publish/sc
dotnet publish src/OpenAorus.App -c Release -r win-x64 --self-contained false -o publish/fd
ls -la publish/sc/OpenAorus.exe publish/fd/OpenAorus.exe
```

Expected: one `OpenAorus.exe` in each folder (self-contained ≈ 70 MB, framework-dependent ≈ 2 MB). Owner runs `publish\fd\OpenAorus.exe --dump` once to confirm the published exe elevates and works.

- [ ] **Step 2: GitHub Actions**

`.github/workflows/build.yml`:

```yaml
name: build
on:
  push:
    branches: [main]
    tags: ['v*']
  pull_request:
jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - run: dotnet restore OpenAorus.sln
      - run: dotnet build OpenAorus.sln -c Release --no-restore
      - run: dotnet test OpenAorus.sln -c Release --no-build --logger "trx"
      - run: dotnet publish src/OpenAorus.App -c Release -r win-x64 --self-contained true  -o publish/sc
      - run: dotnet publish src/OpenAorus.App -c Release -r win-x64 --self-contained false -o publish/fd
      - run: |
          Copy-Item publish/sc/OpenAorus.exe OpenAorus-${{ github.ref_name }}-win-x64.exe
          Copy-Item publish/fd/OpenAorus.exe OpenAorus-${{ github.ref_name }}-win-x64-framework-dependent.exe
        shell: pwsh
      - uses: actions/upload-artifact@v4
        with:
          name: OpenAorus-${{ github.sha }}
          path: OpenAorus-*.exe
      - uses: softprops/action-gh-release@v2
        if: startsWith(github.ref, 'refs/tags/v')
        with:
          files: OpenAorus-*.exe
          prerelease: ${{ contains(github.ref_name, '-') }}
          generate_release_notes: true
```

- [ ] **Step 3: README**

Replace the "Planned for v0.1" section of `README.md` with a "Features (v0.1)" list matching what shipped, add:

```markdown
## Install

Download `OpenAorus-vX.Y.Z-win-x64.exe` from Releases (or the smaller
`-framework-dependent` build if you have the .NET 8 Desktop Runtime). Run it;
accept the UAC prompt. In ⚙ Settings turn on **Start with Windows** to get a
silent elevated tray start, and **Take over from Gigabyte Control Center** so
GCC stops fighting your fan mode. Keep GCC installed: it provides the WMI schema
(`acpimof.dll`) that OpenAorus talks to.

## Supported models

| Model | Status | Notes |
|---|---|---|
| AORUS 17G KD | tested | DutyMax 229, 2 fans |
| other AORUS / AERO / GIGABYTE Gaming | untested, writable | yellow banner; please compare duty with GCC and send `--dump` |
| anything else | read-only | sensors only |

To help: run `OpenAorus.exe --dump > dump.txt` from an elevated terminal and
open an issue with the file and your model name.

## Safety

OpenAorus only calls the same `GB_WMIACPI` methods Gigabyte Control Center
calls, never writes fan duty above the model's maximum, and the custom curve
runs inside the embedded controller, so closing the app does not stop the fans.
```

- [ ] **Step 4: Tag the alpha**

```bash
git add -A
git commit -m "Add CI workflow, publish settings and install docs

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git tag v0.1.0-alpha.1
git push && git push --tags
```

Expected: the `build` workflow goes green on GitHub and a pre-release with two exes appears. Owner downloads the framework-dependent exe on the 17G KD and repeats the Task 14–16 checks once from the published binary.

---

## Owner acceptance checklist (end of v0.1)

- [ ] `--dump` output saved as `docs/research/dump-aorus-17g-kd.txt`
- [ ] Each of the six modes changes fan behaviour audibly and `Duty` read-back moves accordingly
- [ ] Custom curve written by the app equals the table `--dump` reads back
- [ ] Charge limit 80 % holds overnight on AC (battery stays at ~80 %)
- [ ] Reboot with "Start with Windows" on: tray icon present, no UAC prompt, saved mode re-applied (status line)
- [ ] Sleep → resume: status line shows "Re-applied … after resume"
- [ ] With takeover on, GCC no longer overrides the mode; toggling takeover off brings GCC back after re-logon
- [ ] All unit tests green in CI; pre-release published

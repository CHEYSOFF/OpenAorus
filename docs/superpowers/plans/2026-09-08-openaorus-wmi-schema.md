# OpenAorus — Registering the WMI schema ourselves — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make OpenAorus work on a machine with no Gigabyte software installed at all, by
registering the `GB_WMIACPI_*` classes ourselves. The owner uninstalled Gigabyte Control Center,
which removed `acpimof.dll`, which was the only thing declaring those classes to WMI. The firmware
interface is untouched — `ACPI\PNP0C14\DCK` is present and healthy — but every fan and battery
write now fails at its first step, and the machine later froze on a fan duty nothing could change.

**Architecture:** A MOF maps names to **numbers**, and a wrong `WmiMethodId` calls a different
firmware method than the one the app believes it is calling, with an argument meant for something
else, on the controller that governs cooling and charging. Everything here is arranged around not
doing that. The schema is **parsed** from the recovered dump and **printed** as MOF by two pure
functions, so no id is ever typed by a human. The generated MOF is checked in and a drift test
regenerates it on every `dotnet test` run, so the checked-in bytes cannot silently diverge from the
dump they claim to come from. Registration is the same shape as `GccTakeover`: an explicit,
elevated, reversible action that records what it changed. And nothing unlocks a write until two
gates pass — a read gate against the known-good dump, then one harmless `GetChargeStop` →
`SetChargeStop(same value)` → read-back round trip, which is the only thing that proves the **Set**
class maps correctly, because `Get` and `Set` are separate classes with separate id spaces.

**Tech Stack:** .NET 8 (`net8.0-windows`, `win-x64`), C# 12, WPF + WinForms, `CommunityToolkit.Mvvm`,
`System.Management`, `mofcomp.exe` (ships with Windows), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-08-openaorus-wmi-schema-design.md`
**Recovered schema:** `docs/research/gb-wmiacpi-methods-aorus-17g-kd.txt` — 4 class GUIDs,
143 methods (74 `Get`, 69 `Set`), 249 parameters, two parameter types only (`UInt8`, `UInt16`).
**Known-good reading:** `docs/research/dump-aorus-17g-kd-known-good.txt` — 72 method lines, 30 of
which answered `Invalid object`, plus a 15-slot fan table.

## Global Constraints

- Target framework `net8.0-windows`, `RuntimeIdentifier win-x64`; `Nullable` and `ImplicitUsings`
  enabled. **Implicit usings do not include `System.IO`** — add `using System.IO;` in any file that
  touches `File`, `Directory` or `Path`, and prefer `System.IO.Path` fully qualified where a local
  `Path` member exists (see `SettingsStore`).
- **The three rules from section 4 of the design are not negotiable, and every task enforces one:**
  1. **Reproduce, do not invent.** Tasks 1–3. Nothing hand-typed; a drift test proves it.
  2. **Never register over a working schema.** Tasks 4–6. Checked twice — by our own state
     classification, and again by `mofcomp -class:createonly`, which refuses at the tool.
  3. **Earn the right to write.** Tasks 7–9. Both gates, recorded, and re-validated at startup.
- **Only `src/OpenAorus.Hardware/Platform/WindowsSchemaSystem.cs` may launch `mofcomp.exe` or read
  live WMI class metadata.** Everything else goes through `ISchemaSystem`. This is the same rule
  `IGccSystem`/`WindowsGccSystem` already keeps, and it is where the OS begins.
- **Only `GigabyteWmi` may invoke firmware methods.** The gates read and write through
  `IGigabyteWmi`, so both gates are exercised over `FakeGigabyteWmi` in the suite.
- No `#pragma autorecover`. See Task 2 for why, and the Risks section for what it costs.
- The build is warning-free at Debug **and** Release and must stay so. The suite stands at
  **1012 tests**; every task adds to it and none may go red.
- Commit messages describe the change and nothing else: no attribution trailers, no tooling named.
  Follow the existing prefixes (`feat(schema):`, `test(schema):`, `fix(...)`, `docs:`).
- **Stage explicitly by path.** Never `git add -A` and never `git add .`: agent worktrees live under
  `.claude/` inside the repository and would be swept into the commit.
- Run tests with `dotnet test OpenAorus.sln`; build with `dotnet build OpenAorus.sln -c Release`.
- Steps marked `OWNER VERIFY` need the physical laptop. **The owner's machine currently has no
  Gigabyte software on it at all, which makes it the ideal machine to test this on** — it is the
  exact starting state the feature is for, and it will never be this clean again once Control
  Center is reinstalled to compare. Every `OWNER VERIFY` item has a home in `VERIFY.md` section 8
  (Task 10).

## What is testable, and what is not

| Piece | Testable how |
|---|---|
| `WmiSchemaParser` — dump text → model | Fully. Against the checked-in dump. |
| `MofWriter` — model → MOF text | Fully. Golden file, byte-compared. |
| The checked-in `.mof` matching the dump | Fully. `MofDriftTests` regenerates and compares. |
| `SchemaFingerprint`, `SchemaState` | Fully. Pure functions over data. |
| `SchemaRegistrar` — the ordered install/remove | Over `FakeSchemaSystem`. Every failure branch. |
| `SchemaGate` — Gate A and Gate B verdicts | Fully. Replayed from the known-good dump. |
| `SchemaGateRunner` — the gates against hardware | Over `FakeGigabyteWmi`. |
| `SchemaRecord.Repair()` | Fully. |
| **Whether `mofcomp` binds the classes to the firmware** | **Not at all. Hardware only.** |
| **Whether `WMIEvent` is the right base class** | **Not at all. `mofcomp -check` on the laptop.** |

The seam is `ISchemaSystem`. Above it everything is data and decisions and is covered. Below it is
one file that starts a process and reads `ManagementClass` metadata, and its correctness is a
question only the laptop can answer.

## File Structure

```
src/OpenAorus.Hardware/Wmi/Schema/WmiSchema.cs             the model: class, method, parameter
src/OpenAorus.Hardware/Wmi/Schema/WmiSchemaParser.cs       pure: recovered dump text -> WmiSchema
src/OpenAorus.Hardware/Wmi/Schema/MofWriter.cs             pure: WmiSchema -> install MOF + remove MOF
src/OpenAorus.Hardware/Wmi/Schema/SchemaMof.cs             reads the two checked-in MOFs as resources
src/OpenAorus.Hardware/Wmi/Schema/GB_WMIACPI.mof           GENERATED, checked in, embedded
src/OpenAorus.Hardware/Wmi/Schema/GB_WMIACPI-remove.mof    GENERATED, checked in, embedded
src/OpenAorus.Hardware/Wmi/Schema/SchemaFingerprint.cs     pure: schema (or live ids) -> one string
src/OpenAorus.Hardware/Wmi/Schema/SchemaState.cs           pure: presence + fingerprint -> SchemaStatus
src/OpenAorus.Hardware/Wmi/Schema/SchemaRegistrar.cs       the ordered install/remove over the seam
src/OpenAorus.Hardware/Wmi/Schema/KnownGoodReading.cs      pure: known-good dump text -> reference
src/OpenAorus.Hardware/Wmi/Schema/SchemaGate.cs            pure: readings + reference -> verdict
src/OpenAorus.Hardware/Wmi/Schema/SchemaGateRunner.cs      runs both gates through IGigabyteWmi
src/OpenAorus.Hardware/Platform/ISchemaSystem.cs           the seam: mofcomp + live class metadata
src/OpenAorus.Hardware/Platform/WindowsSchemaSystem.cs     the only file that touches either
src/OpenAorus.Hardware/Config/SchemaRecord.cs              the persisted gate record + Repair()
src/OpenAorus.Hardware/Config/AppSettings.cs               (modified) Schema property
src/OpenAorus.Hardware/Config/SettingsStore.cs             (modified) LastLoadSchemaRepaired
src/OpenAorus.Hardware/Fans/FanController.cs               (modified) the write gate
src/OpenAorus.Hardware/Battery/BatteryController.cs        (modified) the write gate
src/OpenAorus.App/AppServices.cs                           (modified) wires the seam and the gate
src/OpenAorus.App/ViewModels/SchemaViewModel.cs            the Settings card
src/OpenAorus.App/ViewModels/MainViewModel.cs              (modified) the startup notice
src/OpenAorus.App/Views/SettingsWindow.xaml                (modified) the schema card
tests/OpenAorus.Hardware.Tests/WmiSchemaParserTests.cs
tests/OpenAorus.Hardware.Tests/MofWriterTests.cs
tests/OpenAorus.Hardware.Tests/MofDriftTests.cs
tests/OpenAorus.Hardware.Tests/SchemaFingerprintTests.cs
tests/OpenAorus.Hardware.Tests/SchemaStateTests.cs
tests/OpenAorus.Hardware.Tests/FakeSchemaSystem.cs
tests/OpenAorus.Hardware.Tests/SchemaRegistrarTests.cs
tests/OpenAorus.Hardware.Tests/KnownGoodReadingTests.cs
tests/OpenAorus.Hardware.Tests/SchemaGateTests.cs
tests/OpenAorus.Hardware.Tests/SchemaGateRunnerTests.cs
tests/OpenAorus.Hardware.Tests/SchemaRecordTests.cs
tests/OpenAorus.Hardware.Tests/SchemaWriteGateTests.cs
tests/OpenAorus.Hardware.Tests/SchemaViewModelTests.cs
tests/OpenAorus.Hardware.Tests/SchemaSettingsUiTests.cs
```

---

### Task 1: The schema model and the parser

**Files:**
- Create: `src/OpenAorus.Hardware/Wmi/Schema/WmiSchema.cs`,
  `src/OpenAorus.Hardware/Wmi/Schema/WmiSchemaParser.cs`
- Modify: `tests/OpenAorus.Hardware.Tests/OpenAorus.Hardware.Tests.csproj` (copy the two research
  files to the test output, exactly as the keymaps already are)
- Test: `tests/OpenAorus.Hardware.Tests/WmiSchemaParserTests.cs`
- Read: `docs/research/gb-wmiacpi-methods-aorus-17g-kd.txt` (all of it),
  `tests/OpenAorus.Hardware.Tests/KeyLayoutTests.cs` (the research-file-as-test-data pattern)

**Interfaces:**
- Produces:
  - `enum WmiParamDirection { In, Out }`
  - `enum WmiParamType { UInt8, UInt16, UInt32, UInt8Array, UInt64, Boolean, String }`
  - `sealed record WmiSchemaParam(string Name, WmiParamType Type, WmiParamDirection Direction, int DataId, string Description)`
  - `sealed record WmiSchemaMethod(string Name, int MethodId, string Description, IReadOnlyList<WmiSchemaParam> Parameters)`
  - `sealed record WmiSchemaProperty(string Name, WmiParamType Type, bool IsKey, bool CanRead, bool CanWrite, int? DataId, int? Max, string Description)`
  - `sealed record WmiSchemaClass(string Name, string Guid, string Description, IReadOnlyList<WmiSchemaProperty> Properties, IReadOnlyList<WmiSchemaMethod> Methods)`
  - `sealed record WmiSchema(IReadOnlyList<WmiSchemaClass> Classes)` with
    `WmiSchemaClass Class(string name)`
  - `static class WmiSchemaParser` with `static WmiSchema Parse(string dumpText)`,
    `const string GetClass = "GB_WMIACPI_Get"`, `SetClass`, `DataClass`, `EventClass`
- Consumes: nothing.

**This file is the only source of truth for 143 numbers, so the parser is strict rather than
forgiving.** A line it does not recognise inside a class body is a `FormatException`, not a skip.
The failure mode a lenient parser produces is a MOF that is missing a method nobody noticed was
dropped, and the app then calls a name WMI has never heard of — which at least fails loudly. The
worse failure is a *mis-parse* that lands a method on the wrong id, so every field the parser reads
is required and every id is checked for uniqueness within its class.

**Two invariants the parser asserts, because they are what the MOF writer relies on.** Within one
class, `WmiMethodId` is unique (confirmed on the recovered dump: no duplicates in either class).
Within one method, `WmiDataId` is unique. Neither is guaranteed by the file format; both are
guaranteed by the file we have, and if a future dump breaks one the parser must stop rather than
emit a MOF with two parameters fighting over one buffer slot.

**Types.** The recovered dump uses exactly two parameter types — `UInt8` (228 parameters) and
`UInt16` (21) — and three more appear only on properties (`UInt32`, `UInt8Array`, `UInt64`,
`Boolean`, `String`). The enum carries all of them so a property is never silently dropped, but
`MofWriter` will reject anything it was not taught to print.

- [ ] **Step 1: Wire the research files into the test project**

`tests/OpenAorus.Hardware.Tests/OpenAorus.Hardware.Tests.csproj`, beside the keymap block:

```xml
  <!-- The recovered schema and the known-good reading are test data, for the same reason the
       keymaps are: the generator is compared against the file it claims to come from, and the
       gate is compared against the one reading we know was correct. -->
  <ItemGroup>
    <None Include="..\..\docs\research\gb-wmiacpi-methods-aorus-17g-kd.txt" CopyToOutputDirectory="PreserveNewest" Link="schema\%(Filename)%(Extension)" />
    <None Include="..\..\docs\research\dump-aorus-17g-kd-known-good.txt" CopyToOutputDirectory="PreserveNewest" Link="schema\%(Filename)%(Extension)" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/WmiSchemaParserTests.cs`:

```csharp
using System.IO;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The recovered schema, read back out of the file it was recovered into.
/// </summary>
/// <remarks>
/// Every number the MOF will contain comes through here. Nothing downstream re-reads the dump, so
/// a mis-parse is indistinguishable from a wrong firmware call - which is the failure the whole
/// design exists to prevent. These tests pin the totals, the four GUIDs, and a spot check of every
/// parameter shape the file contains.
/// </remarks>
public class WmiSchemaParserTests
{
    internal static string DumpPath =>
        Path.Combine(AppContext.BaseDirectory, "schema", "gb-wmiacpi-methods-aorus-17g-kd.txt");

    internal static WmiSchema Recovered() => WmiSchemaParser.Parse(File.ReadAllText(DumpPath));

    [Fact]
    public void The_dump_holds_four_classes_and_one_hundred_and_forty_three_methods()
    {
        var s = Recovered();

        Assert.Equal(4, s.Classes.Count);
        Assert.Equal(74, s.Class(WmiSchemaParser.GetClass).Methods.Count);
        Assert.Equal(69, s.Class(WmiSchemaParser.SetClass).Methods.Count);
        Assert.Equal(143, s.Classes.Sum(c => c.Methods.Count));
        Assert.Equal(249, s.Classes.Sum(c => c.Methods.Sum(m => m.Parameters.Count)));
    }

    [Theory]
    [InlineData("GB_WMIACPI_Get", "{ABBC0F6F-8EA1-11d1-00A0-C90629100000}")]
    [InlineData("GB_WMIACPI_Set", "{ABBC0F75-8EA1-11d1-00A0-C90629100000}")]
    [InlineData("GB_WMIACPI_Data", "{ABBC0F6C-8EA1-11d1-00A0-C90629100000}")]
    [InlineData("GB_WMIACPI_Event", "{ABBC0F72-8EA1-11d1-00A0-C90629100000}")]
    public void Every_class_carries_the_guid_that_names_its_firmware_block(string name, string guid)
    {
        // The guid is what binds the class to an ACPI data block. Wrong here and the class
        // registers cleanly and binds to nothing, or worse, to something else.
        Assert.Equal(guid, s_recovered.Class(name).Guid);
    }

    private static readonly WmiSchema s_recovered = Recovered();

    [Fact]
    public void A_single_out_parameter_method_parses_whole()
    {
        var m = s_recovered.Class(WmiSchemaParser.GetClass).Methods.Single(x => x.Name == "GetCPUFanDuty");

        Assert.Equal(70, m.MethodId);
        Assert.Equal("Get CPU Fan Duty", m.Description);
        var p = Assert.Single(m.Parameters);
        Assert.Equal("Data", p.Name);
        Assert.Equal(WmiParamType.UInt8, p.Type);
        Assert.Equal(WmiParamDirection.Out, p.Direction);
        Assert.Equal(0, p.DataId);
    }

    [Fact]
    public void A_method_that_takes_an_index_and_returns_two_values_parses_whole()
    {
        // The strongest anchor in Gate A runs through this method, and it is the only Get whose
        // parameter directions are mixed. If ids 0/1/2 were ever shuffled, the fan table would
        // read back as noise.
        var m = s_recovered.Class(WmiSchemaParser.GetClass).Methods.Single(x => x.Name == "GetFanIndexValue");

        Assert.Equal(104, m.MethodId);
        Assert.Equal(
            new[] { ("Index", 0, WmiParamDirection.In), ("Temperture", 1, WmiParamDirection.Out), ("Value", 2, WmiParamDirection.Out) },
            m.Parameters.OrderBy(p => p.DataId).Select(p => (p.Name, p.DataId, p.Direction)).ToArray());
    }

    [Fact]
    public void The_ten_slot_deep_fan_method_keeps_all_ten_ids()
    {
        var m = s_recovered.Class(WmiSchemaParser.GetClass).Methods.Single(x => x.Name == "GetDeepFan");

        Assert.Equal(96, m.MethodId);
        Assert.Equal(10, m.Parameters.Count);
        Assert.Equal(Enumerable.Range(0, 10), m.Parameters.Select(p => p.DataId).OrderBy(i => i));
        Assert.All(m.Parameters, p => Assert.Equal(WmiParamDirection.Out, p.Direction));
    }

    [Fact]
    public void A_set_method_keeps_its_in_parameter_and_its_out_parameter_apart()
    {
        var m = s_recovered.Class(WmiSchemaParser.SetClass).Methods.Single(x => x.Name == "SetChargeStop");

        Assert.Equal(101, m.MethodId);
        var data = m.Parameters.Single(p => p.Name == "Data");
        var dataOut = m.Parameters.Single(p => p.Name == "DataOut");
        Assert.Equal((WmiParamDirection.In, 0), (data.Direction, data.DataId));
        Assert.Equal((WmiParamDirection.Out, 1), (dataOut.Direction, dataOut.DataId));
    }

    [Fact]
    public void Get_and_Set_share_method_ids_that_mean_different_things()
    {
        // THE TRAP THE WHOLE DESIGN IS BUILT AROUND, stated as a test. Id 101 is GetChargeStop in
        // one class and SetChargeStop in the other; id 88 is CheckHeavyLoading in Get and
        // SetSuperQuiet in Set. The two id spaces are unrelated, which is why a correct Get
        // mapping proves nothing at all about Set - see Gate B.
        var get = s_recovered.Class(WmiSchemaParser.GetClass);
        var set = s_recovered.Class(WmiSchemaParser.SetClass);

        Assert.Equal("CheckHeavyLoading", get.Methods.Single(m => m.MethodId == 88).Name);
        Assert.Equal("SetSuperQuiet", set.Methods.Single(m => m.MethodId == 88).Name);
    }

    [Fact]
    public void The_only_parameter_types_in_the_recovered_schema_are_uint8_and_uint16()
    {
        var types = s_recovered.Classes.SelectMany(c => c.Methods).SelectMany(m => m.Parameters)
            .Select(p => p.Type).Distinct().OrderBy(t => t).ToArray();

        Assert.Equal(new[] { WmiParamType.UInt8, WmiParamType.UInt16 }, types);
    }

    [Fact]
    public void Method_ids_are_unique_inside_a_class()
    {
        // Not guaranteed by the file format; guaranteed by this file. If a future dump breaks it,
        // the parser must stop rather than let MofWriter print two methods on one id.
        foreach (var c in s_recovered.Classes)
            Assert.Equal(c.Methods.Count, c.Methods.Select(m => m.MethodId).Distinct().Count());
    }

    [Fact]
    public void Data_ids_are_unique_inside_a_method()
    {
        foreach (var m in s_recovered.Classes.SelectMany(c => c.Methods))
            Assert.Equal(m.Parameters.Count, m.Parameters.Select(p => p.DataId).Distinct().Count());
    }

    [Fact]
    public void The_two_data_carrying_classes_keep_their_properties()
    {
        var data = s_recovered.Class(WmiSchemaParser.DataClass);
        var value = data.Properties.Single(p => p.Name == "Data");
        Assert.Equal(WmiParamType.UInt32, value.Type);
        Assert.Equal(1, value.DataId);

        var evt = s_recovered.Class(WmiSchemaParser.EventClass);
        var payload = evt.Properties.Single(p => p.Name == "Data");
        Assert.Equal(WmiParamType.UInt8Array, payload.Type);
        Assert.Equal(1, payload.DataId);
        Assert.Equal(4, payload.Max);
    }

    [Fact]
    public void The_event_class_is_the_one_the_hotkey_listener_already_subscribes_to()
    {
        // WmiEventDecoder.EventClass and the class we are about to register must be the same
        // string, or v0.3's WMI hotkey channel opens against a class this MOF never declared.
        Assert.Equal(OpenAorus.Hardware.Hotkeys.WmiEventDecoder.EventClass, WmiSchemaParser.EventClass);
    }

    [Fact]
    public void Every_method_the_diagnostics_dump_calls_exists_in_the_recovered_schema()
    {
        // The dump's list was written by hand from the same research. If the two ever diverge,
        // the app is calling a name that will not be registered.
        var names = s_recovered.Class(WmiSchemaParser.GetClass).Methods.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var missing = OpenAorus.Hardware.Diagnostics.DiagnosticsDump.GetMethods.Where(m => !names.Contains(m)).ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_method_the_fan_controller_writes_exists_in_the_recovered_Set_class()
    {
        var names = s_recovered.Class(WmiSchemaParser.SetClass).Methods.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var m in new[]
                 {
                     "SetCurrentFanStep", "SetFixedFanStatus", "SetStepFanStatus", "SetAutoFanStatus",
                     "SetNvThermalTarget", "SetFixedFanSpeed", "SetGPUFanDuty", "SetFanIndexValue",
                     "SetChargePolicy", "SetChargeStop",
                 })
            Assert.Contains(m, names);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no class headers at all")]
    [InlineData("=========== GB_WMIACPI_Get ===========\nQualifiers: dynamic=True\n")]
    public void A_dump_it_cannot_read_is_a_hard_failure_not_a_partial_schema(string text)
    {
        // A lenient parser produces a MOF that is quietly missing methods. That is the one
        // outcome nobody would notice until the app called a name WMI had never heard of.
        Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
    }

    [Fact]
    public void A_null_dump_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => WmiSchemaParser.Parse(null!));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~WmiSchemaParserTests`
Expected: compile error, `WmiSchema` not found.

- [ ] **Step 4: Implement the model**

`src/OpenAorus.Hardware/Wmi/Schema/WmiSchema.cs`. Plain records, no logic beyond `Class(name)`,
which throws `KeyNotFoundException` naming the class it wanted. Document on `WmiSchemaMethod` that
`MethodId` is the number the firmware dispatches on and that it is meaningful **only within its own
class**.

- [ ] **Step 5: Implement the parser**

`src/OpenAorus.Hardware/Wmi/Schema/WmiSchemaParser.cs`. A line-oriented state machine:

- `=========== NAME ===========` starts a class.
- `Qualifiers: k=v; k=v; ...` supplies `guid` and `Description`.
- `--- Properties ---` / `--- Methods ---` switch section.
- A property line is `  Name : Type  [k=v; ...]`.
- A method line is `  Name [k=v; ...]` carrying `WmiMethodId`.
- A parameter line is `      param Name : Type [k=v; ...]` carrying `ID` and one of `in`/`out`.

Rules: parameter lines before any method line are a `FormatException`. A method with no
`WmiMethodId` is a `FormatException`. A parameter with neither `in=True` nor `out=True`, or with
both, is a `FormatException` — the design says a direction that was not recorded does not get a
guess. A class with no `guid` is a `FormatException`. Duplicate method ids within a class, or
duplicate data ids within a method, are a `FormatException`. Fewer than four classes is a
`FormatException`. Trailing `# ...` comments on a line are stripped before parsing.

- [ ] **Step 6: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass, including the 1012 already there.

- [ ] **Step 7: Commit**

```bash
git add src/OpenAorus.Hardware/Wmi/Schema/WmiSchema.cs \
        src/OpenAorus.Hardware/Wmi/Schema/WmiSchemaParser.cs \
        tests/OpenAorus.Hardware.Tests/WmiSchemaParserTests.cs \
        tests/OpenAorus.Hardware.Tests/OpenAorus.Hardware.Tests.csproj
git commit -m "feat(schema): parse the recovered GB_WMIACPI schema from the research dump"
```

---

### Task 2: The MOF writer

**Files:**
- Create: `src/OpenAorus.Hardware/Wmi/Schema/MofWriter.cs`
- Test: `tests/OpenAorus.Hardware.Tests/MofWriterTests.cs`
- Read: `src/OpenAorus.Hardware/Wmi/Schema/WmiSchema.cs` (Task 1),
  `docs/research/gb-wmiacpi-methods-aorus-17g-kd.txt` (the four `Qualifiers:` lines)

**Interfaces:**
- Produces:
  - `static class MofWriter` with
    `static string WriteInstall(WmiSchema schema, string fingerprint)`,
    `static string WriteRemove(WmiSchema schema)`,
    `const string Namespace = @"root\WMI"`,
    `const string MarkerClass = "OpenAorus_SchemaMarker"`,
    `const string MarkerInstanceId = "OpenAorus"`,
    `const string EventBaseClass = "WMIEvent"`
- Consumes: `WmiSchema` and friends (Task 1).

**The output is deterministic to the byte**, because Task 3's drift test compares it to a checked-in
file. `\r\n` line endings throughout (this is a Windows tool reading a Windows file), invariant
culture, no timestamps, no machine names, nothing that changes between two runs on two machines.

**Parameters are emitted in `WmiDataId` order, not in the order the dump lists them.** The dump
lists them alphabetically, which is how WMI enumerates a method's parameter collection and not how
they were declared. Declaration order does not affect binding — `WmiDataId` does, and
`System.Management` fills parameters by name — so ordering by id is free, and it makes the generated
file readable as what it is: a buffer layout. On the recovered schema this also puts every `in`
parameter before every `out` parameter without a second rule, because `Index`/`Data` are always id 0.

**`Locale` is deliberately not emitted.** The dump records `Locale=MS\0x409` on all four classes.
That is a localisation qualifier pointing at an amended-namespace resource, and emitting it without
the matching `#pragma amendment` block would declare a translation that does not exist. It is not
part of the binding — `guid`, `WmiMethodId` and `WmiDataId` are — so it is left out rather than
half-copied. This is the one place the generated schema is knowingly not byte-identical to
Gigabyte's, and it is recorded in `VERIFY.md`.

**The event class derives from `WMIEvent`, and this is the single largest unknown in the file.**
The recovered dump lists `SECURITY_DESCRIPTOR` and `TIME_CREATED` among `GB_WMIACPI_Event`'s
properties. Those two are not Gigabyte's; they are the fingerprint of WMI's own event base class,
which means the dump is showing inherited members and the original MOF said
`class GB_WMIACPI_Event : WMIEvent`. So the writer emits that base and declares only `Data`,
`Active` and `InstanceName`. **The dump does not record any class's superclass**, so this is
inference. If `mofcomp -check` rejects it on the laptop, the next thing to try is
`__ExtrinsicEvent`; `EventBaseClass` is a single constant so that is a one-line change.
`OWNER VERIFY` — `VERIFY.md` 8.2.

**All four classes are registered, including `GB_WMIACPI_Data`, which this app never calls.**
Registering three of four would put the machine in a state nobody has ever run, and the recovered
schema is one artefact describing one firmware interface. Restoring it whole is the smaller claim.

**No `#pragma autorecover`.** `mofcomp -AutoRecover` would add our MOF to the registry list WMI
replays when its repository is rebuilt, which would make the registration survive
`winmgmt /resetrepository`. It is not used, for two reasons: it writes into a machine-wide list that
our Remove button cannot cleanly take us back out of, which breaks the reversibility this whole
feature is modelled on; and a repository rebuild that drops our classes is detected at the next
startup by the state check in Task 4, which tells the owner and offers the button again. The cost is
one extra click after a repository rebuild. See Risks.

**The marker class is how we know the registration is ours.** It carries the schema fingerprint,
which is the same string Task 4 recomputes from the *live* classes — so a machine can be checked two
independent ways, and neither depends on the other having survived. It is emitted **last** in the
install MOF and deleted **last** in the remove MOF, so a removal that fails part-way leaves the
marker behind and the state reads as `Partial` (which the installer knows how to clean up) rather
than as `Foreign` (which it refuses to touch).

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/MofWriterTests.cs`:

```csharp
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The MOF text, checked line by line against the recovered schema.
/// </summary>
/// <remarks>
/// This is the last point at which a number is still readable. After mofcomp runs it is a row in
/// a WMI repository, and a wrong WmiMethodId there is a fan method called with a charge argument.
/// So the tests here are about exact strings rather than about "it looks like a MOF".
/// </remarks>
public class MofWriterTests
{
    private static readonly WmiSchema Schema = WmiSchemaParserTests.Recovered();
    private static readonly string Install = MofWriter.WriteInstall(Schema, "test-fingerprint");
    private static readonly string Remove = MofWriter.WriteRemove(Schema);

    [Fact]
    public void It_targets_root_WMI_and_says_so_once_per_file()
    {
        // The namespace is stated in the file rather than passed to mofcomp on the command line,
        // so there is exactly one place it is written down.
        Assert.Contains(@"#pragma namespace(""\\\\.\\root\\WMI"")", Install, StringComparison.Ordinal);
        Assert.Contains(@"#pragma namespace(""\\\\.\\root\\WMI"")", Remove, StringComparison.Ordinal);
    }

    [Fact]
    public void It_never_asks_for_autorecover()
    {
        // AutoRecover would survive a repository rebuild by writing into a machine-wide list the
        // Remove button cannot take us back out of. Reversibility wins; Task 4 detects the loss.
        Assert.DoesNotContain("autorecover", Install, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("autorecover", Remove, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Each_class_carries_the_four_qualifiers_that_bind_it_to_the_firmware()
    {
        foreach (var c in Schema.Classes)
        {
            var header = HeaderOf(c.Name);
            Assert.Contains("dynamic", header, StringComparison.Ordinal);
            Assert.Contains(@"provider(""WmiProv"")", header, StringComparison.Ordinal);
            Assert.Contains("WMI", header, StringComparison.Ordinal);
            Assert.Contains($@"guid(""{c.Guid}"")", header, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_simple_get_method_prints_exactly()
    {
        Assert.Contains(
            "    [WmiMethodId(70), Implemented, read, write, Description(\"Get CPU Fan Duty\")]\r\n" +
            "    void GetCPUFanDuty([out, WmiDataId(0), Description(\"Data\")] uint8 Data);\r\n",
            Install, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mixed_direction_method_prints_its_parameters_in_data_id_order()
    {
        // Not the alphabetical order the dump lists them in - that is how WMI enumerates a
        // parameter collection, not how the original was declared. Data id order reads as what
        // it is: the layout of the ACPI buffer.
        Assert.Contains(
            "    void GetFanIndexValue([in, WmiDataId(0), Description(\"Index\")] uint8 Index, " +
            "[out, WmiDataId(1), Description(\"Temperature\")] uint8 Temperture, " +
            "[out, WmiDataId(2), Description(\"Speed\")] uint8 Value);\r\n",
            Install, StringComparison.Ordinal);
    }

    [Fact]
    public void A_uint16_out_parameter_keeps_its_width()
    {
        // GetChargeStop answers UInt16 while SetChargeStop takes UInt8. Both are reproduced as
        // recovered; Gate B's round trip crosses that boundary and Task 8 guards the crossing.
        Assert.Contains("[out, WmiDataId(0), Description(\"Data\")] uint16 Data);", Install, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_recovered_method_reaches_the_file_on_its_own_id()
    {
        foreach (var c in Schema.Classes)
            foreach (var m in c.Methods)
                Assert.Contains($"[WmiMethodId({m.MethodId}), Implemented, read, write, ", Install, StringComparison.Ordinal);

        // 143 methods, 143 WmiMethodId qualifiers. Not one more, not one fewer.
        Assert.Equal(143, CountOf(Install, "WmiMethodId("));
    }

    [Fact]
    public void The_event_class_derives_from_the_WMI_event_base_rather_than_redeclaring_its_members()
    {
        // SECURITY_DESCRIPTOR and TIME_CREATED are in the dump because WMI enumerated the
        // inherited members. Declaring them here would be redeclaring the base class.
        Assert.Contains("class GB_WMIACPI_Event : WMIEvent\r\n", Install, StringComparison.Ordinal);
        Assert.DoesNotContain("SECURITY_DESCRIPTOR", Install, StringComparison.Ordinal);
        Assert.DoesNotContain("TIME_CREATED", Install, StringComparison.Ordinal);
    }

    [Fact]
    public void The_event_payload_keeps_its_array_bound()
    {
        Assert.Contains(
            "    [read, write, WmiDataId(1), MAX(4), Description(\"WMI Event Data\")] uint8 Data[];\r\n",
            Install, StringComparison.Ordinal);
    }

    [Fact]
    public void The_three_classes_the_app_actually_uses_are_all_there()
    {
        foreach (var name in new[] { "GB_WMIACPI_Get", "GB_WMIACPI_Set", "GB_WMIACPI_Event", "GB_WMIACPI_Data" })
            Assert.Contains($"class {name}", Install, StringComparison.Ordinal);
    }

    [Fact]
    public void The_marker_records_the_fingerprint_and_comes_last()
    {
        Assert.Contains($"class {MofWriter.MarkerClass}", Install, StringComparison.Ordinal);
        Assert.Contains("Fingerprint = \"test-fingerprint\";", Install, StringComparison.Ordinal);
        Assert.Contains($"Id = \"{MofWriter.MarkerInstanceId}\";", Install, StringComparison.Ordinal);

        // Last, so a removal that fails part-way leaves the machine reading as Partial - which
        // the installer knows how to clean up - rather than as Foreign, which it refuses to touch.
        Assert.True(Install.IndexOf($"class {MofWriter.MarkerClass}", StringComparison.Ordinal)
                    > Install.LastIndexOf("class GB_WMIACPI_", StringComparison.Ordinal));
    }

    [Fact]
    public void The_remove_file_deletes_all_five_classes_and_fails_on_none_of_them()
    {
        // NOFAIL makes removal idempotent, which is what lets the installer use it as a rollback
        // after a compile that may or may not have created anything.
        foreach (var name in new[] { "GB_WMIACPI_Get", "GB_WMIACPI_Set", "GB_WMIACPI_Data", "GB_WMIACPI_Event", MofWriter.MarkerClass })
            Assert.Contains($@"#pragma deleteclass(""{name}"", NOFAIL)", Remove, StringComparison.Ordinal);

        Assert.Equal(5, CountOf(Remove, "#pragma deleteclass("));
    }

    [Fact]
    public void The_remove_file_takes_the_marker_out_last()
    {
        Assert.True(Remove.IndexOf(MofWriter.MarkerClass, StringComparison.Ordinal)
                    > Remove.LastIndexOf("GB_WMIACPI_", StringComparison.Ordinal));
    }

    [Fact]
    public void The_remove_file_creates_nothing()
    {
        Assert.DoesNotContain("class GB_WMIACPI", Remove, StringComparison.Ordinal);
        Assert.DoesNotContain("instance of", Remove, StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_says_it_is_generated_and_names_what_regenerates_it()
    {
        Assert.StartsWith("//", Install, StringComparison.Ordinal);
        Assert.Contains("DO NOT EDIT", Install, StringComparison.Ordinal);
        Assert.Contains("gb-wmiacpi-methods-aorus-17g-kd.txt", Install, StringComparison.Ordinal);
        Assert.Contains("MofDriftTests", Install, StringComparison.Ordinal);
    }

    [Fact]
    public void It_is_byte_for_byte_the_same_on_every_run()
    {
        // Task 3 compares this against a checked-in file, so a timestamp or a machine name in
        // the output would turn every build into a diff.
        Assert.Equal(Install, MofWriter.WriteInstall(WmiSchemaParserTests.Recovered(), "test-fingerprint"));
        Assert.Equal(Remove, MofWriter.WriteRemove(WmiSchemaParserTests.Recovered()));
        Assert.DoesNotContain(DateTime.Now.Year.ToString(), Install, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.MachineName, Install, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void It_uses_windows_line_endings_throughout()
    {
        Assert.DoesNotContain("\n", Install.Replace("\r\n", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_it_was_never_taught_to_print_is_refused_rather_than_guessed_at()
    {
        var bad = new WmiSchema(new[]
        {
            new WmiSchemaClass("X", "{0}", "", Array.Empty<WmiSchemaProperty>(), new[]
            {
                new WmiSchemaMethod("M", 1, "", new[]
                {
                    new WmiSchemaParam("P", WmiParamType.UInt64, WmiParamDirection.In, 0, ""),
                }),
            }),
        });

        Assert.Throws<NotSupportedException>(() => MofWriter.WriteInstall(bad, "fp"));
    }

    [Fact]
    public void Nulls_are_programming_errors()
    {
        Assert.Throws<ArgumentNullException>(() => MofWriter.WriteInstall(null!, "fp"));
        Assert.Throws<ArgumentNullException>(() => MofWriter.WriteRemove(null!));
    }

    private static string HeaderOf(string className)
    {
        var i = Install.IndexOf($"class {className}", StringComparison.Ordinal);
        var start = Install.LastIndexOf('[', i);
        return Install[start..i];
    }

    private static int CountOf(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~MofWriterTests`
Expected: compile error, `MofWriter` not found.

- [ ] **Step 3: Implement**

`src/OpenAorus.Hardware/Wmi/Schema/MofWriter.cs`. A `StringBuilder` with `"\r\n"` written
explicitly rather than `AppendLine` (whose separator is environment-dependent). Shape:

```
// GENERATED FILE - DO NOT EDIT.
// Printed by OpenAorus.Hardware.Wmi.Schema.MofWriter from
// docs/research/gb-wmiacpi-methods-aorus-17g-kd.txt, the schema recovered from an AORUS 17G KD
// while Gigabyte Control Center was still installed.
//
// Every number below is a firmware method id or an ACPI buffer offset. None of them was typed by
// hand. MofDriftTests regenerates this file on every test run and fails if it and the research
// file disagree, so editing this file directly will turn the suite red rather than change anything.

#pragma namespace("\\\\.\\root\\WMI")

[dynamic, provider("WmiProv"), WMI, guid("{ABBC0F6F-8EA1-11d1-00A0-C90629100000}"),
 Description("Gigabyte WMI Get method")]
class GB_WMIACPI_Get
{
    [key, read] string InstanceName;
    [read] boolean Active;

    [WmiMethodId(70), Implemented, read, write, Description("Get CPU Fan Duty")]
    void GetCPUFanDuty([out, WmiDataId(0), Description("Data")] uint8 Data);
    ...
};
```

The type map is a `switch` covering `UInt8 -> "uint8"`, `UInt16 -> "uint16"`, `UInt32 -> "uint32"`,
`UInt8Array -> "uint8"` (with `[]` on the name), `Boolean -> "boolean"`, `String -> "string"`, and
`default -> throw new NotSupportedException(...)` naming the type and the parameter. Methods are
emitted in the order the dump lists them; the parser preserves it, and preserving it keeps a diff
against the research file readable.

`WriteRemove` is five `#pragma deleteclass(..., NOFAIL)` lines after the namespace pragma, marker
last, and nothing else at all.

- [ ] **Step 4: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/OpenAorus.Hardware/Wmi/Schema/MofWriter.cs \
        tests/OpenAorus.Hardware.Tests/MofWriterTests.cs
git commit -m "feat(schema): print the recovered schema as a MOF"
```

---

### Task 3: The fingerprint, the checked-in MOF, and the drift test

**Files:**
- Create: `src/OpenAorus.Hardware/Wmi/Schema/SchemaFingerprint.cs`,
  `src/OpenAorus.Hardware/Wmi/Schema/SchemaMof.cs`,
  `src/OpenAorus.Hardware/Wmi/Schema/GB_WMIACPI.mof` (**generated**),
  `src/OpenAorus.Hardware/Wmi/Schema/GB_WMIACPI-remove.mof` (**generated**)
- Modify: `src/OpenAorus.Hardware/OpenAorus.Hardware.csproj` (embed the two MOFs)
- Test: `tests/OpenAorus.Hardware.Tests/SchemaFingerprintTests.cs`,
  `tests/OpenAorus.Hardware.Tests/MofDriftTests.cs`
- Read: `src/OpenAorus.Hardware/Wmi/Schema/MofWriter.cs` (Task 2)

**Interfaces:**
- Produces:
  - `static class SchemaFingerprint` with
    `static string Of(WmiSchema schema)`,
    `static string OfLive(IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> liveMethodIds)`,
    `const string Missing = "(absent)"`
  - `static class SchemaMof` with
    `static string Install { get; }`, `static string Remove { get; }`,
    `static string Fingerprint { get; }`,
    `const string InstallFileName = "OpenAorus-GB_WMIACPI.mof"`,
    `const string RemoveFileName = "OpenAorus-GB_WMIACPI-remove.mof"`
- Consumes: `WmiSchema` (Task 1), `MofWriter` (Task 2).

**Where the generator lives, and how a reader checks the two agree.** The design says the generated
file is checked in beside the script. The script here is not a `.ps1` nobody would run — it is
`MofDriftTests`, in two modes:

- **Verify (default).** Parse `docs/research/gb-wmiacpi-methods-aorus-17g-kd.txt`, print it with
  `MofWriter`, and assert the result is byte-identical to the checked-in `.mof`. This runs on every
  `dotnet test`, which means the checked-in file cannot drift from the research file by so much as a
  space without the suite going red. That is the answer to "how does a reader verify the two agree":
  they run the tests, and so does anyone who touches either file.
- **Regenerate.** With `OPENAORUS_WRITE_MOF=1` in the environment, the same test writes the
  generated text over the checked-in file instead of asserting. That is the whole regeneration
  procedure, and it lives in the same place as the check so the two cannot come apart.

A build-time generator was considered and rejected: it would put a file nobody can read into `obj/`,
and the entire point of checking the MOF in is that **a human can read the 143 numbers before they
reach the controller**.

**Why the fingerprint exists and what it covers.** It is SHA-256 over a canonical rendering of
exactly what binds — for each class in name order, the class name, then each method in name order as
`name=id`. Nothing else: not descriptions, not parameter names, not the MOF's comment header. So it
changes when and only when the mapping changes, and it is computable two independent ways: from the
parsed research file (what we intended to register) and from the live WMI classes (what is actually
there). Task 4 compares them, and that comparison is the whole of "a schema that changes underneath
invalidates the record".

**`OfLive` takes a dictionary rather than reading WMI**, so it is a pure function and the comparison
logic is tested without a WMI provider anywhere.

**Only `Get` and `Set` are fingerprinted.** `GB_WMIACPI_Data` and `GB_WMIACPI_Event` declare no
methods, so they contribute no ids. Keeping them out means the fingerprint is exactly "the 143
numbers", which is exactly the thing that can be wrong. Their presence is covered by the class check
in Task 4 instead.

**The GUID is not part of it, and that is deliberate.** `Of` and `OfLive` must be the same rendering
over the same shape, and the cheap live read is method metadata, not class qualifiers. A wrong GUID
cannot go undetected anyway: WMI will not hold two classes on one GUID, so a class that exists under
our name is bound to the block our MOF named.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/SchemaFingerprintTests.cs`:

```csharp
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// One string that changes when, and only when, a name-to-number mapping changes.
/// </summary>
/// <remarks>
/// This is what makes "the gate result is remembered" safe. A recorded pass is worth something
/// only for as long as the schema it was earned against is still the schema on the machine, and
/// this is the cheap comparison that says so - no firmware method is invoked to compute it, only
/// class metadata is read.
/// </remarks>
public class SchemaFingerprintTests
{
    private static readonly WmiSchema Schema = WmiSchemaParserTests.Recovered();

    [Fact]
    public void The_same_schema_always_fingerprints_the_same()
    {
        Assert.Equal(SchemaFingerprint.Of(Schema), SchemaFingerprint.Of(WmiSchemaParserTests.Recovered()));
    }

    [Fact]
    public void A_live_schema_that_matches_the_recovered_one_fingerprints_the_same()
    {
        // The comparison the app actually makes at startup, with the WMI half faked.
        Assert.Equal(SchemaFingerprint.Of(Schema), SchemaFingerprint.OfLive(LiveFrom(Schema)));
    }

    [Fact]
    public void One_wrong_method_id_changes_it()
    {
        // The failure this whole design exists to catch, reduced to a string comparison.
        var live = LiveFrom(Schema);
        live["GB_WMIACPI_Get"] = With(live["GB_WMIACPI_Get"], "GetCPUFanDuty", 71);

        Assert.NotEqual(SchemaFingerprint.Of(Schema), SchemaFingerprint.OfLive(live));
    }

    [Fact]
    public void A_method_that_disappeared_changes_it()
    {
        var live = LiveFrom(Schema);
        var set = new Dictionary<string, int>(live["GB_WMIACPI_Set"], StringComparer.Ordinal);
        set.Remove("SetChargeStop");
        live["GB_WMIACPI_Set"] = set;

        Assert.NotEqual(SchemaFingerprint.Of(Schema), SchemaFingerprint.OfLive(live));
    }

    [Fact]
    public void A_method_that_appeared_changes_it()
    {
        // A schema that is a superset of ours must not read as "still ours". Whoever registered
        // the extra method registered all of them, and we did not check the rest.
        var live = LiveFrom(Schema);
        live["GB_WMIACPI_Get"] = With(live["GB_WMIACPI_Get"], "GetSomethingNew", 240);

        Assert.NotEqual(SchemaFingerprint.Of(Schema), SchemaFingerprint.OfLive(live));
    }

    [Fact]
    public void A_class_that_is_not_there_at_all_fingerprints_differently_rather_than_being_skipped()
    {
        var live = LiveFrom(Schema);
        live.Remove("GB_WMIACPI_Set");

        var fp = SchemaFingerprint.OfLive(live);
        Assert.NotEqual(SchemaFingerprint.Of(Schema), fp);
        // And a machine with one class is not the same as an empty one.
        Assert.NotEqual(fp, SchemaFingerprint.OfLive(new Dictionary<string, IReadOnlyDictionary<string, int>>()));
    }

    [Fact]
    public void Enumeration_order_does_not_change_it()
    {
        // WMI hands back methods in whatever order the repository holds them. A fingerprint that
        // depended on that would go off on a machine nothing had happened to.
        var live = LiveFrom(Schema);
        var reversed = live.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, int>)kv.Value.Reverse()
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);

        Assert.Equal(SchemaFingerprint.OfLive(live), SchemaFingerprint.OfLive(reversed));
    }

    [Fact]
    public void Descriptions_are_not_part_of_it()
    {
        // Only what binds. A Description that differs between Gigabyte's MOF and ours must not
        // read as a changed mapping.
        var altered = new WmiSchema(Schema.Classes
            .Select(c => new WmiSchemaClass(c.Name, c.Guid, "different", c.Properties,
                c.Methods.Select(m => m with { Description = "also different" }).ToList()))
            .ToList());

        Assert.Equal(SchemaFingerprint.Of(Schema), SchemaFingerprint.Of(altered));
    }

    [Fact]
    public void It_is_short_enough_to_sit_in_a_settings_file_and_be_read_by_eye()
    {
        var fp = SchemaFingerprint.Of(Schema);

        Assert.Equal(64, fp.Length);
        Assert.All(fp, c => Assert.True(char.IsAsciiHexDigitLower(c)));
    }

    [Fact]
    public void Nulls_are_programming_errors()
    {
        Assert.Throws<ArgumentNullException>(() => SchemaFingerprint.Of(null!));
        Assert.Throws<ArgumentNullException>(() => SchemaFingerprint.OfLive(null!));
    }

    private static IReadOnlyDictionary<string, int> With(IReadOnlyDictionary<string, int> src, string name, int id)
    {
        var copy = new Dictionary<string, int>(src, StringComparer.Ordinal) { [name] = id };
        return copy;
    }

    internal static Dictionary<string, IReadOnlyDictionary<string, int>> LiveFrom(WmiSchema schema) =>
        schema.Classes
            .Where(c => c.Methods.Count > 0)
            .ToDictionary(
                c => c.Name,
                c => (IReadOnlyDictionary<string, int>)c.Methods
                    .ToDictionary(m => m.Name, m => m.MethodId, StringComparer.Ordinal),
                StringComparer.Ordinal);
}
```

`tests/OpenAorus.Hardware.Tests/MofDriftTests.cs`:

```csharp
using System.IO;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The checked-in MOF and the research file it claims to come from, held together.
/// </summary>
/// <remarks>
/// <para>
/// This is the mechanism behind the design's first rule: reproduce, do not invent. The MOF is
/// checked in so a human can read the 143 numbers before they reach the controller, and it is
/// regenerated here on every test run so that reading stays honest. Edit the .mof by hand and this
/// goes red; edit the research file and this goes red until the .mof is regenerated.
/// </para>
/// <para>
/// TO REGENERATE: set <c>OPENAORUS_WRITE_MOF=1</c> and run this test. It writes the generated text
/// over the checked-in files instead of asserting, and the diff is then reviewable in the commit.
/// That is the whole procedure - there is deliberately no separate script, because a script that
/// lives somewhere else is a script that stops matching the check.
/// </para>
/// </remarks>
public class MofDriftTests
{
    [Fact]
    public void The_checked_in_mof_is_what_the_generator_prints_from_the_research_file()
    {
        var schema = WmiSchemaParser.Parse(File.ReadAllText(WmiSchemaParserTests.DumpPath));
        var expectedInstall = MofWriter.WriteInstall(schema, SchemaFingerprint.Of(schema));
        var expectedRemove = MofWriter.WriteRemove(schema);

        if (Environment.GetEnvironmentVariable("OPENAORUS_WRITE_MOF") == "1")
        {
            File.WriteAllText(RepoPath("GB_WMIACPI.mof"), expectedInstall);
            File.WriteAllText(RepoPath("GB_WMIACPI-remove.mof"), expectedRemove);
            return;
        }

        Assert.Equal(expectedInstall, SchemaMof.Install);
        Assert.Equal(expectedRemove, SchemaMof.Remove);
    }

    [Fact]
    public void The_fingerprint_the_mof_records_is_the_fingerprint_of_the_schema_it_registers()
    {
        // The marker class carries this string into WMI and the startup check reads it back. If
        // it were computed from anything but the schema, that comparison would be circular.
        var schema = WmiSchemaParser.Parse(File.ReadAllText(WmiSchemaParserTests.DumpPath));

        Assert.Equal(SchemaFingerprint.Of(schema), SchemaMof.Fingerprint);
        Assert.Contains($"Fingerprint = \"{SchemaMof.Fingerprint}\";", SchemaMof.Install, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_files_ship_inside_the_assembly()
    {
        // mofcomp needs a path on disk, and a single-file publish has no loose files beside the
        // exe. They are embedded and written out at registration time - see Task 6.
        Assert.NotEmpty(SchemaMof.Install);
        Assert.NotEmpty(SchemaMof.Remove);
        Assert.Contains("GB_WMIACPI_Get", SchemaMof.Install, StringComparison.Ordinal);
    }

    private static string RepoPath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenAorus.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "OpenAorus.Hardware", "Wmi", "Schema", fileName);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~SchemaFingerprintTests`
Expected: compile error, `SchemaFingerprint` not found.

- [ ] **Step 3: Implement the fingerprint**

`src/OpenAorus.Hardware/Wmi/Schema/SchemaFingerprint.cs`. Build the canonical text, hash it with
`SHA256.HashData`, return lowercase hex:

```
GB_WMIACPI_Get
Check3GModule=254
CheckBIOSMode=252
...
GB_WMIACPI_Set
BlockWinkey=203
...
```

Classes in ordinal name order, methods in ordinal name order, `\n` separators, invariant culture on
the integers, `Missing` written where a class the schema expects is not in the live map.

- [ ] **Step 4: Implement the resource accessor and embed the files**

`src/OpenAorus.Hardware/Wmi/Schema/SchemaMof.cs` reads the two embedded resources once into
`Lazy<string>` fields and exposes `Fingerprint` by pulling the `Fingerprint = "..."` line out of the
install text — so the number the app compares against is the number the file it is about to compile
actually carries, not one recomputed on the side.

`src/OpenAorus.Hardware/OpenAorus.Hardware.csproj`:

```xml
  <!-- mofcomp needs a path on disk and a single-file publish has no loose files, so both MOFs
       ship inside the assembly and are written out at registration time. -->
  <ItemGroup>
    <EmbeddedResource Include="Wmi\Schema\GB_WMIACPI.mof" LogicalName="OpenAorus.Hardware.Wmi.Schema.GB_WMIACPI.mof" />
    <EmbeddedResource Include="Wmi\Schema\GB_WMIACPI-remove.mof" LogicalName="OpenAorus.Hardware.Wmi.Schema.GB_WMIACPI-remove.mof" />
  </ItemGroup>
```

- [ ] **Step 5: Generate the two MOFs and check them in**

Create both files empty first so the `EmbeddedResource` items resolve and the project builds, then:

```bash
OPENAORUS_WRITE_MOF=1 dotnet test OpenAorus.sln --filter FullyQualifiedName~MofDriftTests
```

Then **read the generated `GB_WMIACPI.mof` before committing it.** Spot-check against the research
file: `GetCPUFanDuty` on 70, `GetChargeStop` on 101 answering `uint16`, `SetChargeStop` on 101 taking
`uint8`, `SetCurrentFanStep` on 102, `GetFanIndexValue` on 104 with ids 0/1/2, and 143 occurrences of
`WmiMethodId(`. This is the one manual review in the plan and it is the reason the file is checked in
at all.

- [ ] **Step 6: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass. `MofDriftTests` now compares rather than writes.

- [ ] **Step 7: Commit**

```bash
git add src/OpenAorus.Hardware/Wmi/Schema/SchemaFingerprint.cs \
        src/OpenAorus.Hardware/Wmi/Schema/SchemaMof.cs \
        src/OpenAorus.Hardware/Wmi/Schema/GB_WMIACPI.mof \
        src/OpenAorus.Hardware/Wmi/Schema/GB_WMIACPI-remove.mof \
        src/OpenAorus.Hardware/OpenAorus.Hardware.csproj \
        tests/OpenAorus.Hardware.Tests/SchemaFingerprintTests.cs \
        tests/OpenAorus.Hardware.Tests/MofDriftTests.cs
git commit -m "feat(schema): check in the generated MOF and pin it to the research file"
```

---

### Task 4: What is on the machine, and whose it is

**Files:**
- Create: `src/OpenAorus.Hardware/Wmi/Schema/SchemaState.cs`
- Test: `tests/OpenAorus.Hardware.Tests/SchemaStateTests.cs`
- Read: `src/OpenAorus.Hardware/Wmi/Schema/SchemaFingerprint.cs` (Task 3),
  `src/OpenAorus.Hardware/Platform/GccTakeover.cs` (the shape being copied)

**Interfaces:**
- Produces:
  - `enum SchemaStatus { Absent, Foreign, Partial, Ours, OursGated }`
  - `sealed record SchemaSnapshot(bool GetPresent, bool SetPresent, bool DataPresent, bool EventPresent, bool MarkerPresent, string? LiveFingerprint)`
    with `bool AllClassesPresent`, `bool AnyClassPresent`
  - `static class SchemaState` with
    `static SchemaStatus Classify(SchemaSnapshot live, string expectedFingerprint, bool gatesRecorded)`,
    `static bool CanInstall(SchemaStatus s)`, `static bool CanRemove(SchemaStatus s)`,
    `static bool WritesUnlocked(SchemaStatus s)`, `static string Explain(SchemaStatus s)`
  - `static class SchemaClasses` with `static IReadOnlyList<string> All` — the four `GB_WMIACPI_*`
    names plus the marker, and the one list every caller enumerates
- Consumes: `SchemaFingerprint` (Task 3), `MofWriter.MarkerClass` (Task 2).

**Five states, and each one has exactly one right answer.**

| State | What it means | Install | Remove | Writes |
|---|---|---|---|---|
| `Absent` | No `GB_WMIACPI_*` class, no marker. The machine as the owner left it. | yes | no | no |
| `Foreign` | All four classes, no marker — or all four with a marker and a fingerprint that does not match. Control Center, or a hand-run `mofcomp`. | **no** | **no** | no |
| `Partial` | Some classes but not all, or a marker with classes missing. A half-removal, or a repository rebuild. | yes, after a remove | yes | no |
| `Ours` | All four classes, our marker, fingerprint matches. Registered but not yet proven. | no | yes | **no** |
| `OursGated` | `Ours`, and both gates passed and are still valid. | no | yes | **yes** |

**Two rules follow, and neither is negotiable.**

*Never register over a working schema.* `CanInstall(Foreign)` is false. Two schemas over one GUID is
a state nobody has tested, and the state afterwards is one where neither the owner nor the app can
say which one won.

*Never remove someone else's.* `CanRemove(Foreign)` is false too, and **this is the half the design
does not spell out.** Our remove MOF `#pragma deleteclass`es by *name*, and the names are Gigabyte's.
If Control Center is installed and the owner presses Remove, we would take Control Center's own
registration away and break software we did not install. The button is disabled and says why.

**Control Center installed later, on top of ours** — the case the design asks for explicitly — lands
on `Foreign` through the fingerprint arm rather than the marker arm, because GCC's installer does not
know about `OpenAorus_SchemaMarker` and the marker survives. If GCC's schema is byte-for-byte the
mapping we recovered — which it will be on this chassis, because that is where we recovered it from —
the fingerprint matches and the state stays `Ours`/`OursGated`. That is the right answer for writes:
the mapping the gates were earned against is still the mapping on the machine. What is now wrong is
that a Remove would delete a registration GCC also depends on, so Task 6 re-checks at the moment of
removal rather than trusting a classification taken at startup, and Task 9's card warns when GCC is
detected running. `OWNER VERIFY` — `VERIFY.md` 8.6.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/SchemaStateTests.cs`:

```csharp
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Five states, and the two things that must never happen in any of them: registering over a
/// working schema, and removing one that is not ours.
/// </summary>
public class SchemaStateTests
{
    private const string Expected = "aaaa";
    private const string Different = "bbbb";

    private static SchemaSnapshot Snapshot(bool classes, bool marker, string? fingerprint) =>
        new(classes, classes, classes, classes, marker, fingerprint);

    [Fact]
    public void A_machine_with_no_gigabyte_software_reads_as_absent()
    {
        // The owner's machine right now, and the state this whole feature is for.
        var s = SchemaState.Classify(Snapshot(classes: false, marker: false, fingerprint: null), Expected, gatesRecorded: false);

        Assert.Equal(SchemaStatus.Absent, s);
        Assert.True(SchemaState.CanInstall(s));
        Assert.False(SchemaState.CanRemove(s));
        Assert.False(SchemaState.WritesUnlocked(s));
    }

    [Fact]
    public void Control_center_installed_reads_as_foreign_and_is_left_completely_alone()
    {
        var s = SchemaState.Classify(Snapshot(classes: true, marker: false, fingerprint: Expected), Expected, gatesRecorded: false);

        Assert.Equal(SchemaStatus.Foreign, s);
        // Two schemas over one GUID is a state nobody has tested.
        Assert.False(SchemaState.CanInstall(s));
        // And our remove MOF deletes by name - Gigabyte's names - so it would break GCC.
        Assert.False(SchemaState.CanRemove(s));
        Assert.False(SchemaState.WritesUnlocked(s));
    }

    [Fact]
    public void Someone_who_ran_mofcomp_by_hand_reads_as_foreign_even_with_our_marker_present()
    {
        // The marker says we were here once. The fingerprint says what is there now is not what
        // we put there, and the fingerprint is the one that decides.
        var s = SchemaState.Classify(Snapshot(classes: true, marker: true, fingerprint: Different), Expected, gatesRecorded: true);

        Assert.Equal(SchemaStatus.Foreign, s);
        Assert.False(SchemaState.WritesUnlocked(s));
    }

    [Fact]
    public void Our_registration_does_not_unlock_writes_on_its_own()
    {
        // Registering the schema is not the same as proving it. The design's third rule, as one
        // assertion.
        var s = SchemaState.Classify(Snapshot(classes: true, marker: true, fingerprint: Expected), Expected, gatesRecorded: false);

        Assert.Equal(SchemaStatus.Ours, s);
        Assert.False(SchemaState.WritesUnlocked(s));
        Assert.True(SchemaState.CanRemove(s));
        Assert.False(SchemaState.CanInstall(s));
    }

    [Fact]
    public void Our_registration_with_both_gates_passed_unlocks_writes()
    {
        var s = SchemaState.Classify(Snapshot(classes: true, marker: true, fingerprint: Expected), Expected, gatesRecorded: true);

        Assert.Equal(SchemaStatus.OursGated, s);
        Assert.True(SchemaState.WritesUnlocked(s));
        Assert.True(SchemaState.CanRemove(s));
    }

    [Fact]
    public void A_recorded_pass_stops_counting_the_moment_the_schema_underneath_changes()
    {
        // The whole reason the record is safe to keep rather than re-earn every launch.
        var s = SchemaState.Classify(Snapshot(classes: true, marker: true, fingerprint: Different), Expected, gatesRecorded: true);

        Assert.False(SchemaState.WritesUnlocked(s));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    public void Some_classes_but_not_all_reads_as_partial(bool get, bool set, bool data, bool evt)
    {
        var s = SchemaState.Classify(new SchemaSnapshot(get, set, data, evt, MarkerPresent: false, LiveFingerprint: null), Expected, false);

        Assert.Equal(SchemaStatus.Partial, s);
        // A half-registered machine can be cleaned up and then registered again.
        Assert.True(SchemaState.CanInstall(s));
        Assert.True(SchemaState.CanRemove(s));
        Assert.False(SchemaState.WritesUnlocked(s));
    }

    [Fact]
    public void A_marker_left_behind_by_a_failed_removal_reads_as_partial()
    {
        // The marker is deleted last for exactly this reason: what is left over is ours, and is
        // safe for us to clean up.
        var s = SchemaState.Classify(Snapshot(classes: false, marker: true, fingerprint: null), Expected, gatesRecorded: false);

        Assert.Equal(SchemaStatus.Partial, s);
        Assert.True(SchemaState.CanRemove(s));
    }

    [Fact]
    public void Writes_are_locked_in_four_of_the_five_states()
    {
        var unlocked = Enum.GetValues<SchemaStatus>().Where(SchemaState.WritesUnlocked).ToArray();

        Assert.Equal(new[] { SchemaStatus.OursGated }, unlocked);
    }

    [Fact]
    public void Nothing_can_be_installed_and_removed_at_once_except_a_half_state()
    {
        foreach (var s in Enum.GetValues<SchemaStatus>())
            if (SchemaState.CanInstall(s) && SchemaState.CanRemove(s))
                Assert.Equal(SchemaStatus.Partial, s);
    }

    [Fact]
    public void Every_state_can_explain_itself_to_the_owner()
    {
        foreach (var s in Enum.GetValues<SchemaStatus>())
        {
            var text = SchemaState.Explain(s);

            Assert.False(string.IsNullOrWhiteSpace(text));
            // Not "step 1/5 setCurrentFanStep failed: not found" - a cause, not a symptom.
            Assert.DoesNotContain("not found", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_class_list_names_the_four_recovered_classes_and_our_marker()
    {
        Assert.Equal(
            new[] { "GB_WMIACPI_Data", "GB_WMIACPI_Event", "GB_WMIACPI_Get", "GB_WMIACPI_Set", MofWriter.MarkerClass },
            SchemaClasses.All.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Nulls_are_programming_errors()
    {
        Assert.Throws<ArgumentNullException>(() => SchemaState.Classify(null!, Expected, false));
        Assert.Throws<ArgumentNullException>(() => SchemaState.Classify(Snapshot(false, false, null), null!, false));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~SchemaStateTests`
Expected: compile error, `SchemaState` not found.

- [ ] **Step 3: Implement**

`src/OpenAorus.Hardware/Wmi/Schema/SchemaState.cs`. `Classify` in this order — the order *is* the
logic, so write it as a sequence of guards rather than a `switch`:

1. No class present and no marker → `Absent`.
2. Not all four classes present → `Partial`, marker or no marker.
3. All four present, no marker → `Foreign`.
4. All four present, marker present, `LiveFingerprint != expectedFingerprint` → `Foreign`.
5. Otherwise `gatesRecorded ? OursGated : Ours`.

`Explain` returns owner-facing prose, one or two sentences each, in the voice of `BannerState`'s
baseline texts: what the machine is in, and what the app will and will not do about it.

- [ ] **Step 4: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/OpenAorus.Hardware/Wmi/Schema/SchemaState.cs \
        tests/OpenAorus.Hardware.Tests/SchemaStateTests.cs
git commit -m "feat(schema): classify what is registered and whose it is"
```

---

### Task 5: The seam, and the ordered install and removal

**Files:**
- Create: `src/OpenAorus.Hardware/Platform/ISchemaSystem.cs`,
  `src/OpenAorus.Hardware/Wmi/Schema/SchemaRegistrar.cs`,
  `tests/OpenAorus.Hardware.Tests/FakeSchemaSystem.cs`
- Test: `tests/OpenAorus.Hardware.Tests/SchemaRegistrarTests.cs`
- Read: `src/OpenAorus.Hardware/Platform/IGccSystem.cs` and
  `src/OpenAorus.Hardware/Platform/GccTakeover.cs` (the shape this copies exactly),
  `src/OpenAorus.Hardware/Wmi/Schema/SchemaState.cs` (Task 4)

**Interfaces:**
- Produces:
  - `sealed record MofCompResult(int ExitCode, string Output)` with `bool Success => ExitCode == 0`
  - `interface ISchemaSystem` with
    `bool ClassExists(string className)`,
    `IReadOnlyDictionary<string, int> ReadMethodIds(string className)`,
    `string? ReadMarkerFingerprint()`,
    `string? MofCompPath { get; }`,
    `MofCompResult RunMofComp(string arguments)`,
    `string WriteMofFile(string fileName, string content)`,
    `void DeleteMofFile(string path)`
  - `sealed record SchemaOperationResult(bool Success, SchemaStatus Status, string Message)`
  - `static class SchemaRegistrar` with
    `static SchemaSnapshot Look(ISchemaSystem sys, IReadOnlyList<string> fingerprintedClasses)`,
    `static string BuildCheckArguments(string mofPath)`,
    `static string BuildCompileArguments(string mofPath)`,
    `static SchemaOperationResult Install(ISchemaSystem sys, bool elevated, string expectedFingerprint)`,
    `static SchemaOperationResult Remove(ISchemaSystem sys, bool elevated, string expectedFingerprint)`
- Consumes: `SchemaState`, `SchemaSnapshot`, `SchemaClasses` (Task 4), `SchemaMof` (Task 3).

**This is `GccTakeover` again, and it is meant to be.** An explicit, elevated, reversible system
change the owner initiates, which records what it changed so it can be put back. The differences are
that the thing being changed is a WMI class rather than a service, and that the "record" is a marker
class living in WMI rather than a `GccTakeoverState` in the settings file — because a settings file
can be deleted while the classes stay behind, and a schema whose only record of itself is gone is a
schema nobody can remove.

**The install sequence, and where each step's failure leaves the machine.**

1. **Not elevated** → refuse, change nothing. (In practice this cannot happen: `App.OnStartup`
   relaunches under UAC and exits before `AppServices.Create` runs, so every code path that reaches
   here is already elevated. The check is here anyway, because "the app already has elevation" is a
   fact about a startup path someone could change, not a property of this function.)
2. **`MofCompPath is null`** → refuse, change nothing, and say exactly that: `mofcomp.exe` ships with
   Windows, so its absence means the WMI installation is damaged and registering a schema on top of
   that is not the right repair.
3. **State is not installable** → refuse, change nothing, name the state.
4. **State is `Partial`** → run the remove MOF first, then re-look. A leftover marker or half a class
   set would make step 6 fail on `-class:createonly`, and cleaning up our own leavings is exactly what
   `NOFAIL` deleteclass is for.
5. **Write both MOFs to disk**, then `mofcomp -check` the install one. A syntax error, a rejected base
   class, a qualifier `mofcomp` does not know — all of it surfaces here, **before anything is written
   into the repository**. Failure → delete the files, report the compiler's own output, change nothing.
6. **`mofcomp -class:createonly` the install MOF.** `createonly` is the tool-enforced version of rule
   2: if any class already exists, `mofcomp` refuses and changes nothing, which closes the window
   between the look in step 3 and the compile here.
7. **Re-look.** If the state is not `Ours`, something is wrong even though `mofcomp` said zero — run
   the remove MOF, re-look again, and report. Nothing half-registered.
8. Delete the temporary files and report success with the new state.

**The remove sequence is shorter and has one extra refusal.**

1. Not elevated → refuse.
2. `MofCompPath is null` → refuse.
3. **Re-look right now, not at startup**, and refuse if the state is `Foreign`. This is the second
   place the "never remove someone else's" rule is enforced, and it is the one that matters: a
   classification taken when the window opened is stale by the time a button is clicked, and Control
   Center could have been installed in between.
4. Run the remove MOF. `deleteclass ... NOFAIL` makes it idempotent, so a second press is harmless
   and a partial first press is recoverable by pressing again.
5. Re-look; expect `Absent`. Anything else is reported with the state it actually reached.

**`Look` reads method ids only for the classes it is told to fingerprint**, which is `Get` and `Set`.
Reading ids for `Data` and `Event` would be two more `ManagementClass.Get()` round trips for two
classes that have no methods.

- [ ] **Step 1: Write the fake**

`tests/OpenAorus.Hardware.Tests/FakeSchemaSystem.cs`:

```csharp
using OpenAorus.Hardware.Platform;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// A machine's WMI repository and its copy of mofcomp, as a dictionary and a queue.
/// </summary>
/// <remarks>
/// The point of the seam. Everything above <see cref="ISchemaSystem"/> is decisions about what to
/// run and what to conclude, and all of it is exercised here: a mofcomp that refuses, a mofcomp
/// that reports success and creates nothing, a machine where Control Center is already installed,
/// a half-removed one. None of that is reachable on real hardware without breaking the laptop.
/// </remarks>
public sealed class FakeSchemaSystem : ISchemaSystem
{
    /// <summary>The classes this machine holds. Mutated by <see cref="OnCompile"/>.</summary>
    public HashSet<string> Classes { get; } = new(StringComparer.Ordinal);

    /// <summary>Live method ids per class, for the fingerprint.</summary>
    public Dictionary<string, IReadOnlyDictionary<string, int>> MethodIds { get; } = new(StringComparer.Ordinal);

    public string? MarkerFingerprint { get; set; }
    public string? MofCompPath { get; set; } = @"C:\Windows\System32\wbem\mofcomp.exe";

    public List<string> MofCompCalls { get; } = new();
    public List<string> FilesWritten { get; } = new();
    public List<string> FilesDeleted { get; } = new();

    /// <summary>What mofcomp does, keyed by whether the arguments contain "-check".</summary>
    public Func<string, MofCompResult> OnCompile { get; set; } = _ => new MofCompResult(0, "");

    public bool ClassExists(string className) => Classes.Contains(className);

    public IReadOnlyDictionary<string, int> ReadMethodIds(string className) =>
        MethodIds.TryGetValue(className, out var ids) ? ids : new Dictionary<string, int>(StringComparer.Ordinal);

    public string? ReadMarkerFingerprint() => Classes.Contains(MofWriter.MarkerClass) ? MarkerFingerprint : null;

    public MofCompResult RunMofComp(string arguments)
    {
        MofCompCalls.Add(arguments);
        return OnCompile(arguments);
    }

    public string WriteMofFile(string fileName, string content)
    {
        FilesWritten.Add(fileName);
        return $@"C:\fake\{fileName}";
    }

    public void DeleteMofFile(string path) => FilesDeleted.Add(path);

    /// <summary>A mofcomp that actually registers the five classes, for the happy path.</summary>
    public void CompileForReal(string fingerprint, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> ids) =>
        OnCompile = args =>
        {
            if (args.Contains("-check", StringComparison.Ordinal)) return new MofCompResult(0, "syntax ok");
            if (args.Contains("remove", StringComparison.Ordinal))
            {
                Classes.Clear();
                MethodIds.Clear();
                MarkerFingerprint = null;
                return new MofCompResult(0, "deleted");
            }
            foreach (var n in SchemaClasses.All) Classes.Add(n);
            foreach (var (k, v) in ids) MethodIds[k] = v;
            MarkerFingerprint = fingerprint;
            return new MofCompResult(0, "compiled");
        };
}
```

- [ ] **Step 2: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/SchemaRegistrarTests.cs`:

```csharp
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Registering and removing a WMI schema, and every way both can fail.
/// </summary>
/// <remarks>
/// Modelled on <see cref="OpenAorus.Hardware.Platform.GccTakeover"/>: an elevated, owner-initiated,
/// reversible system change that records what it changed. The one thing the tests here cannot say
/// anything about is whether the classes mofcomp creates actually bind to the firmware, which is
/// why Tasks 7 and 8 exist.
/// </remarks>
public class SchemaRegistrarTests
{
    private static readonly string Fingerprint = SchemaMof.Fingerprint;

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> RealIds() =>
        SchemaFingerprintTests.LiveFrom(WmiSchemaParserTests.Recovered());

    private static FakeSchemaSystem EmptyMachine()
    {
        var sys = new FakeSchemaSystem();
        sys.CompileForReal(Fingerprint, RealIds());
        return sys;
    }

    private static FakeSchemaSystem MachineWithControlCenter()
    {
        var sys = new FakeSchemaSystem();
        foreach (var n in new[] { "GB_WMIACPI_Get", "GB_WMIACPI_Set", "GB_WMIACPI_Data", "GB_WMIACPI_Event" })
            sys.Classes.Add(n);
        foreach (var (k, v) in RealIds()) sys.MethodIds[k] = v;
        return sys;
    }

    [Fact]
    public void Installing_on_an_empty_machine_leaves_it_registered_but_not_yet_trusted()
    {
        var sys = EmptyMachine();

        var r = SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        Assert.True(r.Success);
        // Registered, and writes still locked. Registration is not proof - Gates A and B are.
        Assert.Equal(SchemaStatus.Ours, r.Status);
        Assert.False(SchemaState.WritesUnlocked(r.Status));
    }

    [Fact]
    public void It_syntax_checks_before_it_touches_the_repository()
    {
        var sys = EmptyMachine();

        SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        Assert.Contains("-check", sys.MofCompCalls[0], StringComparison.Ordinal);
        Assert.DoesNotContain("-check", sys.MofCompCalls[1], StringComparison.Ordinal);
    }

    [Fact]
    public void It_asks_mofcomp_to_refuse_rather_than_overwrite()
    {
        // -class:createonly is the tool-enforced half of "never register over a working schema".
        // It closes the window between our own look and the compile.
        var sys = EmptyMachine();

        SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        Assert.Contains("-class:createonly", sys.MofCompCalls.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_syntax_error_changes_nothing_at_all()
    {
        var sys = new FakeSchemaSystem
        {
            OnCompile = args => args.Contains("-check", StringComparison.Ordinal)
                ? new MofCompResult(3, "Parse error at line 41: Unknown class WMIEvent")
                : throw new InvalidOperationException("must not compile after a failed check"),
        };

        var r = SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        Assert.False(r.Success);
        Assert.Equal(SchemaStatus.Absent, r.Status);
        Assert.Empty(sys.Classes);
        // The compiler's own words, because they name the line and the problem and we cannot.
        Assert.Contains("Parse error at line 41", r.Message, StringComparison.Ordinal);
        Assert.NotEmpty(sys.FilesDeleted);
    }

    [Fact]
    public void A_compile_that_fails_half_way_is_rolled_back_and_reported()
    {
        // "If mofcomp reports failure, nothing is half-registered." The rollback is the remove
        // MOF, which is idempotent by construction.
        var sys = new FakeSchemaSystem();
        sys.OnCompile = args =>
        {
            if (args.Contains("-check", StringComparison.Ordinal)) return new MofCompResult(0, "ok");
            if (args.Contains("remove", StringComparison.Ordinal)) { sys.Classes.Clear(); return new MofCompResult(0, "deleted"); }
            sys.Classes.Add("GB_WMIACPI_Get");
            sys.Classes.Add("GB_WMIACPI_Set");
            return new MofCompResult(3, "Error creating class GB_WMIACPI_Event");
        };

        var r = SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        Assert.False(r.Success);
        Assert.Equal(SchemaStatus.Absent, r.Status);
        Assert.Empty(sys.Classes);
        Assert.Contains(sys.MofCompCalls, a => a.Contains("remove", StringComparison.Ordinal));
    }

    [Fact]
    public void A_compile_that_reports_success_and_creates_nothing_is_still_a_failure()
    {
        // The state that would otherwise record a passing install on a machine with no schema,
        // and then lock the owner out of writes with no explanation.
        var sys = new FakeSchemaSystem { OnCompile = _ => new MofCompResult(0, "") };

        var r = SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        Assert.False(r.Success);
        Assert.Contains("mofcomp reported success", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void It_refuses_to_register_over_control_center()
    {
        var sys = MachineWithControlCenter();

        var r = SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        Assert.False(r.Success);
        Assert.Equal(SchemaStatus.Foreign, r.Status);
        Assert.Empty(sys.MofCompCalls);
        Assert.Empty(sys.FilesWritten);
    }

    [Fact]
    public void It_refuses_to_remove_control_centers_registration()
    {
        // Our remove MOF deletes by name, and the names are Gigabyte's. Pressing Remove on a
        // machine with GCC installed would break GCC.
        var sys = MachineWithControlCenter();

        var r = SchemaRegistrar.Remove(sys, elevated: true, Fingerprint);

        Assert.False(r.Success);
        Assert.Equal(SchemaStatus.Foreign, r.Status);
        Assert.Empty(sys.MofCompCalls);
        Assert.Equal(4, sys.Classes.Count);
    }

    [Fact]
    public void It_checks_again_at_the_moment_of_removal_rather_than_trusting_the_startup_look()
    {
        // Control Center installed after the Settings window opened. The classification the card
        // is showing is stale, and this is the check that catches it.
        var sys = EmptyMachine();
        SchemaRegistrar.Install(sys, elevated: true, Fingerprint);
        sys.Classes.Remove(MofWriter.MarkerClass);   // GCC's installer replaced our registration

        var r = SchemaRegistrar.Remove(sys, elevated: true, Fingerprint);

        Assert.False(r.Success);
        Assert.Equal(SchemaStatus.Foreign, r.Status);
    }

    [Fact]
    public void A_half_removed_machine_is_cleaned_up_before_it_is_registered_again()
    {
        var sys = EmptyMachine();
        sys.Classes.Add(MofWriter.MarkerClass);      // a removal that died before the classes went

        var r = SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        Assert.True(r.Success);
        Assert.Equal(SchemaStatus.Ours, r.Status);
        // The remove ran first: -class:createonly would have refused otherwise.
        Assert.Contains("remove", sys.MofCompCalls[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_ours_puts_the_machine_back_exactly_as_it_was()
    {
        var sys = EmptyMachine();
        SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        var r = SchemaRegistrar.Remove(sys, elevated: true, Fingerprint);

        Assert.True(r.Success);
        Assert.Equal(SchemaStatus.Absent, r.Status);
        Assert.Empty(sys.Classes);
    }

    [Fact]
    public void Removing_twice_is_harmless()
    {
        // deleteclass NOFAIL. A second press must not be an error the owner has to interpret.
        var sys = EmptyMachine();
        SchemaRegistrar.Install(sys, elevated: true, Fingerprint);
        SchemaRegistrar.Remove(sys, elevated: true, Fingerprint);

        var r = SchemaRegistrar.Remove(sys, elevated: true, Fingerprint);

        Assert.Equal(SchemaStatus.Absent, r.Status);
    }

    [Fact]
    public void Without_elevation_it_does_nothing_and_says_which_right_is_missing()
    {
        var sys = EmptyMachine();

        var install = SchemaRegistrar.Install(sys, elevated: false, Fingerprint);
        var remove = SchemaRegistrar.Remove(sys, elevated: false, Fingerprint);

        Assert.False(install.Success);
        Assert.False(remove.Success);
        Assert.Contains("administrator", install.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(sys.MofCompCalls);
    }

    [Fact]
    public void A_machine_with_no_mofcomp_is_told_the_truth_about_why()
    {
        // mofcomp ships with Windows. Its absence is a damaged WMI installation, and registering
        // a schema on top of that is not the repair.
        var sys = EmptyMachine();
        sys.MofCompPath = null;

        var r = SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        Assert.False(r.Success);
        Assert.Contains("mofcomp", r.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(sys.FilesWritten);
    }

    [Fact]
    public void The_temporary_mof_files_are_cleaned_up_whatever_happens()
    {
        foreach (var compile in new Func<string, MofCompResult>[]
                 {
                     _ => new MofCompResult(0, ""),
                     _ => new MofCompResult(3, "boom"),
                 })
        {
            var sys = new FakeSchemaSystem { OnCompile = compile };
            SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

            Assert.Equal(sys.FilesWritten.Count, sys.FilesDeleted.Count);
        }
    }

    [Fact]
    public void The_arguments_quote_the_path_and_name_nothing_else()
    {
        // The namespace lives in the MOF's own #pragma, so there is one place it is written down.
        var check = SchemaRegistrar.BuildCheckArguments(@"C:\Program Files\x\y.mof");
        var compile = SchemaRegistrar.BuildCompileArguments(@"C:\Program Files\x\y.mof");

        Assert.Contains(@"""C:\Program Files\x\y.mof""", check, StringComparison.Ordinal);
        Assert.Contains("-check", check, StringComparison.Ordinal);
        Assert.Contains("-class:createonly", compile, StringComparison.Ordinal);
        Assert.DoesNotContain("-N:", compile, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoRecover", compile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Looking_reads_ids_only_for_the_two_classes_that_have_any()
    {
        var sys = EmptyMachine();
        SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        var snapshot = SchemaRegistrar.Look(sys, new[] { "GB_WMIACPI_Get", "GB_WMIACPI_Set" });

        Assert.True(snapshot.AllClassesPresent);
        Assert.True(snapshot.MarkerPresent);
        Assert.Equal(Fingerprint, snapshot.LiveFingerprint);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~SchemaRegistrarTests`
Expected: compile error, `ISchemaSystem` not found.

- [ ] **Step 4: Implement the seam**

`src/OpenAorus.Hardware/Platform/ISchemaSystem.cs`. Document on the interface that this is the
boundary: **only `WindowsSchemaSystem` may implement it against the real OS**, and nothing above it
may start a process or open a `ManagementScope`. `MofCompPath` is a property rather than a method
because "is `mofcomp` there" is a fact about the machine, not an action.

- [ ] **Step 5: Implement the registrar**

`src/OpenAorus.Hardware/Wmi/Schema/SchemaRegistrar.cs`, following the two sequences above exactly.
Both files are always written before either is used, so the rollback path never has to write one
under failure conditions. `try`/`finally` around the whole compile block deletes whatever was
written. The message on every failure carries `MofCompResult.Output` verbatim (trimmed, capped at
about 500 characters) — the compiler names the line and the qualifier and we cannot.

- [ ] **Step 6: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/OpenAorus.Hardware/Platform/ISchemaSystem.cs \
        src/OpenAorus.Hardware/Wmi/Schema/SchemaRegistrar.cs \
        tests/OpenAorus.Hardware.Tests/FakeSchemaSystem.cs \
        tests/OpenAorus.Hardware.Tests/SchemaRegistrarTests.cs
git commit -m "feat(schema): register and remove the schema reversibly under elevation"
```

---

### Task 6: `WindowsSchemaSystem` — the OS-facing half

**Files:**
- Create: `src/OpenAorus.Hardware/Platform/WindowsSchemaSystem.cs`
- Test: `tests/OpenAorus.Hardware.Tests/WindowsSchemaSystemTests.cs`
- Read: `src/OpenAorus.Hardware/Platform/WindowsGccSystem.cs` (the process-launch pattern),
  `src/OpenAorus.Hardware/Platform/StartupTask.cs` (`RunSchtasks`, and the one thing to do
  differently), `src/OpenAorus.Hardware/Wmi/GigabyteWmi.cs` (the `ManagementScope` pattern)

**Interfaces:**
- Produces:
  - `sealed class WindowsSchemaSystem : ISchemaSystem` with
    `static string ExpectedMofCompPath { get; }`,
    `static string MofDirectory { get; }`,
    `const int TimeoutMs = 60_000`
- Consumes: `ISchemaSystem` (Task 5), `MofWriter.MarkerClass` (Task 2).

**This is the whole of the untestable part, and it is deliberately about seventy lines.** Everything
that decides anything is above it. What is here is: resolve a path, start a process, read two class
metadata collections, write two files.

**`mofcomp.exe` is resolved absolutely and never taken off `PATH`.**
`Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wbem", "mofcomp.exe")`.
This process is elevated, and starting a bare-named executable from an elevated process lets anything
that can write a `PATH` directory decide what runs as SYSTEM-adjacent. `StartupTask` starts
`schtasks` by bare name today; that is a separate latent issue and not this task's to fix, but the
new file must not copy it. If the resolved path does not exist, `MofCompPath` returns null and the
registrar refuses without launching anything.

**Class existence is a metadata read, not a method call.** `new ManagementClass(scope, path, null)`
followed by `.Get()`, with `ManagementException` meaning "not there". Nothing is invoked, so this is
safe to run on every startup and it cannot touch the firmware.

**`ReadMethodIds` walks `ManagementClass.Methods` and pulls the `WmiMethodId` qualifier**, which is
the same number our MOF put there. A method with no such qualifier is skipped — it is not part of the
mapping and including it under a sentinel value would make the fingerprint depend on how WMI reports
an absence.

**The MOFs are written to `%LOCALAPPDATA%\OpenAorus\schema\`**, beside `settings.json`, and deleted
after each operation. They are not written to `%TEMP%`: a file an elevated process is about to feed
to a compiler should live somewhere a non-elevated process on the machine cannot replace between the
write and the read, and the app's own LocalAppData directory is already the place it trusts.

**The timeout is 60 seconds, not 15.** `mofcomp` on a cold WMI repository can take a while, and
killing it half way through a class creation is the one way to produce the half-registered state the
whole design refuses to allow. If it does time out, the process is killed and the caller reports a
timeout — which lands the machine in whatever state it reached, and the next `Look` will read it as
`Partial` and offer the cleanup.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/WindowsSchemaSystemTests.cs`:

```csharp
using System.IO;
using OpenAorus.Hardware.Platform;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The little that can be checked about the OS-facing half without an OS to check it against.
/// </summary>
/// <remarks>
/// Everything this class actually does - starting mofcomp, reading class metadata - needs a
/// machine. What is testable is where it looks, which is the part that would be a security problem
/// if it were wrong, and that it never invents a path.
/// </remarks>
public class WindowsSchemaSystemTests
{
    [Fact]
    public void It_looks_for_mofcomp_in_the_one_place_windows_puts_it()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "wbem", "mofcomp.exe");

        Assert.Equal(expected, WindowsSchemaSystem.ExpectedMofCompPath);
    }

    [Fact]
    public void It_never_falls_back_to_the_path_environment_variable()
    {
        // This process is elevated. Starting a bare-named executable would let anything that can
        // write a PATH directory choose what runs.
        Assert.True(Path.IsPathFullyQualified(WindowsSchemaSystem.ExpectedMofCompPath));
    }

    [Fact]
    public void A_machine_without_mofcomp_reports_null_rather_than_a_path_that_is_not_there()
    {
        var sys = new WindowsSchemaSystem();

        // On any real Windows box this is present; the assertion is that the property is a lookup
        // and not a constant, so a damaged install is reported rather than launched.
        Assert.Equal(File.Exists(WindowsSchemaSystem.ExpectedMofCompPath), sys.MofCompPath is not null);
    }

    [Fact]
    public void The_mof_files_land_beside_the_settings_file_and_not_in_temp()
    {
        // A file an elevated process is about to feed to a compiler must not live somewhere a
        // non-elevated process can replace between the write and the read.
        var settingsDir = Path.GetDirectoryName(OpenAorus.Hardware.Config.SettingsStore.DefaultPath)!;

        Assert.StartsWith(settingsDir, WindowsSchemaSystem.MofDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Temp", WindowsSchemaSystem.MofDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_timeout_is_long_enough_that_a_slow_repository_is_not_killed_mid_class()
    {
        // Killing mofcomp part-way through creating a class is the one reliable way to produce
        // the half-registered state the whole design refuses to allow.
        Assert.True(WindowsSchemaSystem.TimeoutMs >= 60_000);
    }

    [Fact]
    public void Asking_about_a_class_that_cannot_exist_is_false_rather_than_an_exception()
    {
        // Runs against the live root\WMI on the test machine and must be safe there: it reads
        // class metadata and invokes nothing.
        var sys = new WindowsSchemaSystem();

        Assert.False(sys.ClassExists("OpenAorus_ThisClassDoesNotExist"));
        Assert.Empty(sys.ReadMethodIds("OpenAorus_ThisClassDoesNotExist"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~WindowsSchemaSystemTests`
Expected: compile error, `WindowsSchemaSystem` not found.

- [ ] **Step 3: Implement**

`src/OpenAorus.Hardware/Platform/WindowsSchemaSystem.cs`. `RunMofComp` follows
`StartupTask.RunSchtasks` — `UseShellExecute = false`, `CreateNoWindow = true`, both streams
redirected — but reads **both** streams and returns them joined in `MofCompResult.Output`, because
`mofcomp` reports parse errors on stdout and the whole value of the failure path is passing its own
words through.

Read the streams before `WaitForExit` (or asynchronously) so a chatty compile cannot deadlock on a
full pipe buffer. On timeout, `Kill(entireProcessTree: true)` and return `new MofCompResult(-1,
"mofcomp did not finish within 60 seconds and was stopped.")`.

- [ ] **Step 4: Run tests and build Release**

Run: `dotnet test OpenAorus.sln`
Run: `dotnet build OpenAorus.sln -c Release`
Expected: all pass, no warnings at either configuration.

- [ ] **Step 5:** `OWNER VERIFY` — nothing below the seam has been run. `VERIFY.md` 8.2 and 8.3.

- [ ] **Step 6: Commit**

```bash
git add src/OpenAorus.Hardware/Platform/WindowsSchemaSystem.cs \
        tests/OpenAorus.Hardware.Tests/WindowsSchemaSystemTests.cs
git commit -m "feat(schema): run mofcomp and read live class metadata"
```

---

### Task 7: Gate A — the reads, against the reading we know was correct

**Files:**
- Create: `src/OpenAorus.Hardware/Wmi/Schema/KnownGoodReading.cs`,
  `src/OpenAorus.Hardware/Wmi/Schema/SchemaGate.cs`
- Test: `tests/OpenAorus.Hardware.Tests/KnownGoodReadingTests.cs`,
  `tests/OpenAorus.Hardware.Tests/SchemaGateTests.cs`
- Read: `docs/research/dump-aorus-17g-kd-known-good.txt` (**all of it, including the three notes at
  the bottom — they change what this gate can and cannot check**),
  `src/OpenAorus.Hardware/Diagnostics/DiagnosticsDump.cs`,
  `src/OpenAorus.Hardware/Fans/FanSafety.cs` (the shape of a pure policy object)

**Interfaces:**
- Produces:
  - `sealed record MethodReading(string Method, bool Answered, IReadOnlyDictionary<string, int> Values)`
  - `sealed record FanSlot(int Index, int Temperature, int Duty)`
  - `sealed record GateReadings(IReadOnlyList<MethodReading> Methods, IReadOnlyList<FanSlot> FanTable)`
  - `sealed record GateVerdict(bool Passed, IReadOnlyList<string> Failures, IReadOnlyList<string> Warnings, int Compared)`
    with `static GateVerdict Pass(int compared, params string[] warnings)`,
    `string Summary()`
  - `static class KnownGoodReading` with `static GateReadings Parse(string dumpText)`
  - `static class SchemaGate` with
    `static GateVerdict CheckReads(GateReadings actual, GateReadings reference, ModelProfile profile)`,
    `static IReadOnlyList<string> StaleBufferMethods`,
    `const int MinTemperature = 20`, `const int MaxTemperature = 110`,
    `const double MinPartitionAgreement = 0.90`,
    `const int MinFanSlots = 2`
- Consumes: `ModelProfile`, `FanSafety`.

**This is the `FanSafety` of the schema work: a small pure object that decides, with the untestable
part kept out of it entirely.** It is handed two `GateReadings` — one taken from the machine, one
parsed from the checked-in known-good dump — and returns a verdict. It never calls WMI. Task 8 does
the calling.

**Four things the gate checks, in order of how much they would catch.**

**A1 — the implemented/unimplemented partition.** This is the strongest signal in the file and the
design does not mention it. The known-good dump answers 42 of its 72 `Get` methods and returns
`Invalid object` for the other 30, and its own notes say why that matters: *"A registration that made
them all succeed would be more suspicious than one that reproduced them."* Those 30 are methods the
firmware does not implement, and the firmware decides that by **method id**. So if our ids are right,
the same 30 fail; if a block of ids is shifted, methods that used to fail start answering and
methods that used to answer start failing. Agreement below `MinPartitionAgreement` is a failure that
names every method that changed side. Compared only over methods present in both readings — the
known-good was rendered by a 0.2.0 build whose method list is not necessarily today's — and the
verdict reports how many were actually compared.

**A2 — plausible temperatures.** `getCpuTemp` between 20 and 110, and `getGpuTemp1` likewise when
`profile.HasGpuTemp1`. A wrong id here reads a duty or a status byte as a temperature; the
known-good had 90 and 53.

**A3 — the fan table, read to its terminator.** The design says "a fifteen-slot fan table that is
monotonic in temperature". **That is only true when no custom curve is applied, and the research says
so explicitly**: a later dump on this machine, taken with a custom curve in force, read back six
points, then a `(0,0)` terminator at slot 6, then stale values in slots 7–14. A gate demanding
fifteen monotonic slots would fail on a machine whose only fault is that the owner uses a curve. So
the gate reads slot 0 upward, stops at the first `(0,0)`, and requires that prefix to be at least
`MinFanSlots` long, non-decreasing in temperature, and to hold no duty above `profile.DutyMax`. That
is what actually exercises `GetFanIndexValue`'s three data ids, which is the point.

**A4 — `GetChargeStop` in 0..100.** Not `== 97`: the owner can change the charge limit, and pinning
the value would fail on a machine that is fine. Gate B needs this method to answer at all, so a
failure here is worth catching before Gate B tries to write.

**And three methods that are never checked, because the research says their values are lies.**
`GetPEGorSG`, `GetFanPWMStatus` and `GetFanAdjustStatus` are unimplemented and return whatever the
last call left in the buffer — all three returned 115 in the known-good and all three returned 229 in
a later dump where the duty was 229. They cannot appear in A1's partition either, because a stale
buffer can answer or not depending on what ran before. `StaleBufferMethods` names them and every
check skips them.

**RPM is a warning, never a failure.** `getRpm1`/`getRpm2` byte-swap to plausible speeds in the
known-good, but a cool idle machine legitimately reads zero, so a rule strict enough to catch a wrong
id would also fail a machine sitting on a desk doing nothing.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/KnownGoodReadingTests.cs`:

```csharp
using System.IO;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The last reading taken while the schema still worked, read back off disk.
/// </summary>
public class KnownGoodReadingTests
{
    internal static string Path =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "schema", "dump-aorus-17g-kd-known-good.txt");

    internal static GateReadings Reference() => KnownGoodReading.Parse(File.ReadAllText(Path));

    [Fact]
    public void It_reads_every_method_line_and_keeps_which_ones_answered()
    {
        var r = Reference();

        Assert.Equal(72, r.Methods.Count);
        Assert.Equal(30, r.Methods.Count(m => !m.Answered));
        Assert.Equal(42, r.Methods.Count(m => m.Answered));
    }

    [Fact]
    public void An_invalid_object_answer_is_recorded_as_a_method_this_model_does_not_implement()
    {
        // Not a failure. It is part of the shape Gate A compares against, and reproducing it is
        // evidence the ids are right.
        var m = Reference().Methods.Single(x => x.Method == "GetBatteryCount");

        Assert.False(m.Answered);
        Assert.Empty(m.Values);
    }

    [Fact]
    public void A_single_valued_answer_keeps_its_number()
    {
        Assert.Equal(115, Reference().Methods.Single(m => m.Method == "GetCPUFanDuty").Values["Data"]);
        Assert.Equal(90, Reference().Methods.Single(m => m.Method == "getCpuTemp").Values["Data"]);
        Assert.Equal(53, Reference().Methods.Single(m => m.Method == "getGpuTemp1").Values["Data"]);
    }

    [Fact]
    public void A_multi_valued_answer_keeps_all_of_them()
    {
        var m = Reference().Methods.Single(x => x.Method == "GetPowerOnTime");

        Assert.Equal(37, m.Values["Year"]);
        Assert.Equal(9, m.Values["Month"]);
        Assert.Equal(23, m.Values["Day"]);
    }

    [Fact]
    public void The_anchor_comments_are_stripped_rather_than_parsed_as_part_of_the_value()
    {
        // "GetChargeStop: Data=97      # ANCHOR - Gate B round-trips this"
        Assert.Equal(97, Reference().Methods.Single(m => m.Method == "GetChargeStop").Values["Data"]);
    }

    [Fact]
    public void The_fan_table_comes_back_as_fifteen_slots()
    {
        var t = Reference().FanTable;

        Assert.Equal(15, t.Count);
        Assert.Equal(new FanSlot(0, 0, 57), t[0]);
        Assert.Equal(new FanSlot(14, 89, 229), t[14]);
    }

    [Fact]
    public void The_known_good_table_is_the_firmwares_own_and_tops_out_at_the_duty_maximum()
    {
        var t = Reference().FanTable;

        Assert.Equal(229, t.Max(s => s.Duty));
        Assert.True(t.Zip(t.Skip(1)).All(p => p.Second.Temperature >= p.First.Temperature));
    }

    [Fact]
    public void Comment_lines_and_the_header_are_not_mistaken_for_readings()
    {
        Assert.DoesNotContain(Reference().Methods, m => m.Method.StartsWith("#", StringComparison.Ordinal));
        Assert.DoesNotContain(Reference().Methods, m => m.Method.Contains("Model", StringComparison.Ordinal));
    }

    [Fact]
    public void A_text_with_no_readings_in_it_is_a_hard_failure()
    {
        Assert.Throws<FormatException>(() => KnownGoodReading.Parse("nothing here"));
        Assert.Throws<ArgumentNullException>(() => KnownGoodReading.Parse(null!));
    }
}
```

`tests/OpenAorus.Hardware.Tests/SchemaGateTests.cs`:

```csharp
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Gate A: is this registration reading the firmware, or reading something else?
/// </summary>
/// <remarks>
/// <para>
/// The strongest test in this file is the first one: the reading we know was correct is fed
/// straight back in and must pass. A gate that rejected the one dump taken while the schema
/// demonstrably worked would be useless, and a gate nobody had shown accepting anything would be
/// worse - it would lock the owner out with no way to tell a bad registration from a bad gate.
/// </para>
/// <para>
/// The second strongest is the one after it: a reading with two methods' values swapped, which is
/// what a shifted method id looks like from up here, must fail. Together they say the gate has
/// both a floor and teeth.
/// </para>
/// </remarks>
public class SchemaGateTests
{
    private static readonly ModelProfile Kd = ModelProfile.Detect("AORUS 17G KD");
    private static GateReadings Reference() => KnownGoodReadingTests.Reference();

    [Fact]
    public void The_reading_we_know_was_correct_passes()
    {
        var v = SchemaGate.CheckReads(Reference(), Reference(), Kd);

        Assert.True(v.Passed, v.Summary());
        Assert.Empty(v.Failures);
        Assert.True(v.Compared > 60);
    }

    [Fact]
    public void A_machine_where_every_method_suddenly_answers_is_more_suspicious_than_one_that_reproduces_the_failures()
    {
        // Straight out of the research file's own notes. The 30 "Invalid object" answers are the
        // firmware saying "I do not implement that method id", so reproducing them is evidence.
        var reference = Reference();
        var everythingWorks = reference with
        {
            Methods = reference.Methods
                .Select(m => m.Answered ? m : m with { Answered = true, Values = new Dictionary<string, int> { ["Data"] = 1 } })
                .ToList(),
        };

        var v = SchemaGate.CheckReads(everythingWorks, reference, Kd);

        Assert.False(v.Passed);
        Assert.Contains(v.Failures, f => f.Contains("GetBatteryCount", StringComparison.Ordinal));
    }

    [Fact]
    public void A_block_of_shifted_method_ids_fails()
    {
        // What a wrong WmiMethodId actually looks like from up here: methods that used to answer
        // stop, and methods that used to fail start.
        var reference = Reference();
        var shifted = reference with
        {
            Methods = reference.Methods.Select(m => m with { Answered = !m.Answered }).ToList(),
        };

        var v = SchemaGate.CheckReads(shifted, reference, Kd);

        Assert.False(v.Passed);
    }

    [Fact]
    public void A_handful_of_methods_changing_side_is_tolerated()
    {
        // Firmware revisions and machine state move a few of these. The gate is looking for a
        // shifted block, not for a perfect match.
        var reference = Reference();
        var moved = reference.Methods.Take(3).Select(m => m with { Answered = !m.Answered });
        var actual = reference with { Methods = moved.Concat(reference.Methods.Skip(3)).ToList() };

        var v = SchemaGate.CheckReads(actual, reference, Kd);

        Assert.True(v.Passed, v.Summary());
        Assert.NotEmpty(v.Warnings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    [InlineData(19)]
    [InlineData(111)]
    public void A_cpu_temperature_that_is_not_a_temperature_fails(int value)
    {
        var v = SchemaGate.CheckReads(WithValue(Reference(), "getCpuTemp", value), Reference(), Kd);

        Assert.False(v.Passed);
        Assert.Contains(v.Failures, f => f.Contains("getCpuTemp", StringComparison.Ordinal));
    }

    [Fact]
    public void A_gpu_temperature_is_only_checked_on_a_model_that_has_one()
    {
        var noGpu = Kd with { HasGpuTemp1 = false };

        Assert.True(SchemaGate.CheckReads(WithValue(Reference(), "getGpuTemp1", 0), Reference(), noGpu).Passed);
        Assert.False(SchemaGate.CheckReads(WithValue(Reference(), "getGpuTemp1", 0), Reference(), Kd).Passed);
    }

    [Fact]
    public void The_fan_table_is_read_to_its_terminator_and_not_beyond()
    {
        // THE CORRECTION THE RESEARCH FORCES. A machine with a custom curve applied reads back
        // the curve, a (0,0) terminator, and stale values in the slots after it. Demanding
        // fifteen monotonic slots would fail every machine whose owner uses a curve.
        var custom = new List<FanSlot>
        {
            new(0, 40, 57), new(1, 50, 69), new(2, 60, 92), new(3, 70, 126),
            new(4, 80, 172), new(5, 90, 229), new(6, 0, 0),
        };
        for (var i = 7; i < 15; i++) custom.Add(new FanSlot(i, 86, 206));   // stale, and not in order

        var v = SchemaGate.CheckReads(Reference() with { FanTable = custom }, Reference(), Kd);

        Assert.True(v.Passed, v.Summary());
    }

    [Fact]
    public void A_fan_table_that_is_not_ordered_before_its_terminator_fails()
    {
        var scrambled = new List<FanSlot> { new(0, 80, 200), new(1, 40, 57), new(2, 0, 0) };

        var v = SchemaGate.CheckReads(Reference() with { FanTable = scrambled }, Reference(), Kd);

        Assert.False(v.Passed);
        Assert.Contains(v.Failures, f => f.Contains("fan table", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_fan_table_that_terminates_immediately_fails()
    {
        // GetFanIndexValue answering zeroes for everything is what an unbound method looks like.
        var empty = Enumerable.Range(0, 15).Select(i => new FanSlot(i, 0, 0)).ToList();

        var v = SchemaGate.CheckReads(Reference() with { FanTable = empty }, Reference(), Kd);

        Assert.False(v.Passed);
    }

    [Fact]
    public void A_fan_table_holding_a_duty_above_the_models_maximum_fails()
    {
        var tooHot = new List<FanSlot> { new(0, 40, 57), new(1, 90, 255), new(2, 0, 0) };

        var v = SchemaGate.CheckReads(Reference() with { FanTable = tooHot }, Reference(), Kd);

        Assert.False(v.Passed);
    }

    [Theory]
    [InlineData("GetPEGorSG")]
    [InlineData("GetFanPWMStatus")]
    [InlineData("GetFanAdjustStatus")]
    public void The_three_methods_that_return_stale_buffer_contents_are_never_used_as_evidence(string method)
    {
        // The research is explicit: all three returned 115 in the known-good dump and all three
        // returned 229 in a later one where the duty was 229. They are unimplemented, and their
        // value is whatever the last call left behind.
        Assert.Contains(method, SchemaGate.StaleBufferMethods);

        var reference = Reference();
        var flipped = reference with
        {
            Methods = reference.Methods
                .Select(m => m.Method == method ? m with { Answered = !m.Answered, Values = new Dictionary<string, int>() } : m)
                .ToList(),
        };

        Assert.True(SchemaGate.CheckReads(flipped, reference, Kd).Passed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(100)]
    public void The_charge_stop_is_checked_for_range_and_not_pinned_to_the_value_it_had(int value)
    {
        // The owner can change the charge limit. Pinning 97 would fail a machine that is fine.
        Assert.True(SchemaGate.CheckReads(WithValue(Reference(), "GetChargeStop", value), Reference(), Kd).Passed);
    }

    [Fact]
    public void A_charge_stop_outside_zero_to_a_hundred_fails()
    {
        Assert.False(SchemaGate.CheckReads(WithValue(Reference(), "GetChargeStop", 4096), Reference(), Kd).Passed);
    }

    [Fact]
    public void A_charge_stop_that_does_not_answer_at_all_fails_here_rather_than_in_gate_b()
    {
        // Gate B writes to this method. Finding out it is not there before writing is the point.
        var reference = Reference();
        var gone = reference with
        {
            Methods = reference.Methods
                .Select(m => m.Method == "GetChargeStop" ? m with { Answered = false, Values = new Dictionary<string, int>() } : m)
                .ToList(),
        };

        Assert.False(SchemaGate.CheckReads(gone, reference, Kd).Passed);
    }

    [Fact]
    public void An_rpm_that_byte_swaps_to_nonsense_is_a_warning_and_not_a_failure()
    {
        // A cool machine sitting on a desk legitimately reads zero, so a rule strict enough to
        // catch a wrong id here would fail a machine doing nothing wrong.
        var v = SchemaGate.CheckReads(WithValue(Reference(), "getRpm1", 0), Reference(), Kd);

        Assert.True(v.Passed);
        Assert.NotEmpty(v.Warnings);
    }

    [Fact]
    public void A_reading_where_nothing_answered_fails_loudly()
    {
        // The machine the owner has right now, if the registration did not take.
        var reference = Reference();
        var nothing = reference with
        {
            Methods = reference.Methods.Select(m => m with { Answered = false, Values = new Dictionary<string, int>() }).ToList(),
            FanTable = Array.Empty<FanSlot>(),
        };

        var v = SchemaGate.CheckReads(nothing, reference, Kd);

        Assert.False(v.Passed);
        Assert.True(v.Failures.Count >= 3);
    }

    [Fact]
    public void The_verdict_says_what_it_compared_so_a_thin_comparison_is_visible()
    {
        var reference = Reference();
        var few = reference with { Methods = reference.Methods.Take(4).ToList() };

        var v = SchemaGate.CheckReads(few, reference, Kd);

        Assert.Equal(4, v.Compared);
        Assert.Contains("4", v.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void Nulls_are_programming_errors()
    {
        Assert.Throws<ArgumentNullException>(() => SchemaGate.CheckReads(null!, Reference(), Kd));
        Assert.Throws<ArgumentNullException>(() => SchemaGate.CheckReads(Reference(), null!, Kd));
        Assert.Throws<ArgumentNullException>(() => SchemaGate.CheckReads(Reference(), Reference(), null!));
    }

    private static GateReadings WithValue(GateReadings r, string method, int value) => r with
    {
        Methods = r.Methods
            .Select(m => m.Method == method
                ? m with { Answered = true, Values = new Dictionary<string, int> { ["Data"] = value } }
                : m)
            .ToList(),
    };
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~SchemaGateTests`
Expected: compile error, `SchemaGate` not found.

- [ ] **Step 3: Implement the known-good parser**

`src/OpenAorus.Hardware/Wmi/Schema/KnownGoodReading.cs`. Strip `#` comments from each line first,
then two blocks: lines under `GB_WMIACPI_Get:` matching `  Name: rest`, where `rest` beginning
`ERROR` means not answered and otherwise splits on spaces into `Key=Value` pairs; and lines under
`Fan table` matching `  [i] temp=T duty=D`. Zero readings is a `FormatException`. The format is
exactly what `DiagnosticsDump.Render` produces, and that is the point — the same parser reads a
dump the owner exports today.

- [ ] **Step 4: Implement the gate**

`src/OpenAorus.Hardware/Wmi/Schema/SchemaGate.cs`. Accumulate into two `List<string>` and return
`new GateVerdict(failures.Count == 0, failures, warnings, compared)`. Order the checks A1 → A4 so
the first failure a reader sees is the most informative one. `Summary()` renders one line:
`"Gate A: passed, 69 methods compared, 2 warnings"` or `"Gate A: FAILED, 69 compared - <first
failure>"`.

- [ ] **Step 5: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/OpenAorus.Hardware/Wmi/Schema/KnownGoodReading.cs \
        src/OpenAorus.Hardware/Wmi/Schema/SchemaGate.cs \
        tests/OpenAorus.Hardware.Tests/KnownGoodReadingTests.cs \
        tests/OpenAorus.Hardware.Tests/SchemaGateTests.cs
git commit -m "feat(schema): judge a fresh registration against the known-good reading"
```

---

### Task 8: Gate B — the one harmless round trip, and the record

**Files:**
- Create: `src/OpenAorus.Hardware/Wmi/Schema/SchemaGateRunner.cs`,
  `src/OpenAorus.Hardware/Config/SchemaRecord.cs`
- Modify: `src/OpenAorus.Hardware/Config/AppSettings.cs`,
  `src/OpenAorus.Hardware/Config/SettingsStore.cs`
- Test: `tests/OpenAorus.Hardware.Tests/SchemaGateRunnerTests.cs`,
  `tests/OpenAorus.Hardware.Tests/SchemaRecordTests.cs`
- Read: `src/OpenAorus.Hardware/Battery/BatteryController.cs` (`MinStop`, `MaxStop`, and how it
  already casts to `byte`), `src/OpenAorus.Hardware/Config/HotkeySettings.cs` (the `Repair()`
  pattern), `src/OpenAorus.Hardware/Wmi/Schema/SchemaGate.cs` (Task 7)

**Interfaces:**
- Produces:
  - `static class SchemaGateRunner` with
    `static GateReadings Read(IGigabyteWmi wmi)`,
    `static GateVerdict RunGateA(IGigabyteWmi wmi, GateReadings reference, ModelProfile profile)`,
    `static GateVerdict RunGateB(IGigabyteWmi wmi)`,
    `const string RoundTripGet = "GetChargeStop"`, `const string RoundTripSet = "SetChargeStop"`
  - `sealed class SchemaRecord` with
    `bool Registered`, `string? Fingerprint`, `bool GatesPassed`, `DateTime? When`,
    `string? GateSummary`, `bool Repair()`
  - `AppSettings.Schema` (never null), `SettingsStore.LastLoadSchemaRepaired`
- Consumes: `IGigabyteWmi`, `SchemaGate` (Task 7), `BatteryController` constants.

**Why Gate B exists at all, restated because it is the easiest thing in this plan to skip.**
`GB_WMIACPI_Get` and `GB_WMIACPI_Set` are separate classes with separate GUIDs and separate
`WmiMethodId` spaces. Id 88 is `CheckHeavyLoading` in one and `SetSuperQuiet` in the other; id 101 is
`GetChargeStop` and `SetChargeStop`. Gate A can pass perfectly with every `Set` id wrong, and the
first thing the owner would then do is apply a fan mode.

**Why `SetChargeStop` and nothing else.** It is the only write in the recovered schema that is a
no-op by construction — writing back the value just read changes nothing whatever the policy is —
and it cannot heat the machine. Every fan write either changes cooling or latches a mode.

**The refusal the design does not mention, and it matters.** `SetChargeStop` takes a `uint8`; if
`GetChargeStop` answers something outside a sane charge percentage, writing it back is not harmless.
A read of `0` written back would be "stop charging at 0 %", which is a laptop that does not charge.
A read of `4096` truncates to `0` in the `byte` cast and does the same thing. So Gate B refuses to
write unless the value it read is inside `BatteryController.MinStop..MaxStop` (60..100), and reports
the refusal as an *inconclusive* gate rather than a failure — the registration might be fine and the
battery reading merely odd, and the honest answer is that this test could not be run. Writes stay
locked either way.

**The round trip is three calls with the read-back last**, and the read-back must match. A `Set` that
returns success and changes nothing looks identical to a correct no-op, which is why the value read
first has to come back — it proves the `Set` reached the same firmware slot the `Get` reads.

**The record is a `GccTakeoverState` in spirit**: a small persisted object saying what was done, with
enough in it to know when it has stopped being true. `Repair()` follows `HotkeySettings.Repair()`
exactly — one choke point in `SettingsStore.Load`, one notice — and clears `GatesPassed` whenever the
record is internally inconsistent (passed but no fingerprint, passed but not registered, a `When` in
the future). A hand-edited settings file must not be a way to unlock writes.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/SchemaGateRunnerTests.cs`:

```csharp
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Wmi;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Both gates, run against a fake controller.
/// </summary>
/// <remarks>
/// Gate B is the one that matters here. Get and Set are different classes with different id
/// spaces - id 88 is CheckHeavyLoading in one and SetSuperQuiet in the other - so a perfect Gate A
/// says nothing at all about whether a fan write will land where it is aimed.
/// </remarks>
public class SchemaGateRunnerTests
{
    private static readonly ModelProfile Kd = ModelProfile.Detect("AORUS 17G KD");

    private static FakeGigabyteWmi HealthyMachine()
    {
        var wmi = new FakeGigabyteWmi();
        foreach (var m in KnownGoodReadingTests.Reference().Methods)
        {
            if (!m.Answered) { wmi.FailOn.Add(m.Method); continue; }
            wmi.Responses[m.Method] = m.Values.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        }
        return wmi;
    }

    [Fact]
    public void Reading_the_machine_produces_the_shape_gate_a_compares()
    {
        var readings = SchemaGateRunner.Read(HealthyMachine());

        Assert.NotEmpty(readings.Methods);
        Assert.Equal(15, readings.FanTable.Count);
    }

    [Fact]
    public void Gate_a_passes_on_a_machine_answering_exactly_what_the_known_good_dump_recorded()
    {
        var wmi = HealthyMachine();
        // The fan table has to be answered per index, which Respond cannot express.
        StubFanTable(wmi, KnownGoodReadingTests.Reference().FanTable);

        var v = SchemaGateRunner.RunGateA(wmi, KnownGoodReadingTests.Reference(), Kd);

        Assert.True(v.Passed, v.Summary());
    }

    [Fact]
    public void Gate_a_reads_and_never_writes()
    {
        // "Reads cannot damage anything, so this gate is free to fail loudly." It is only free to
        // if it really is all reads.
        var wmi = HealthyMachine();
        SchemaGateRunner.RunGateA(wmi, KnownGoodReadingTests.Reference(), Kd);

        Assert.All(wmi.Calls, c => Assert.Equal(WmiClass.Get, c.Class));
    }

    [Fact]
    public void Gate_b_writes_back_exactly_what_it_read()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargeStop", 97);

        var v = SchemaGateRunner.RunGateB(wmi);

        Assert.True(v.Passed, v.Summary());
        var write = Assert.Single(wmi.Calls.Where(c => c.Class == WmiClass.Set));
        Assert.Equal("SetChargeStop", write.Method);
        Assert.Equal(97, write.Data);
    }

    [Fact]
    public void Gate_b_reads_back_afterwards_because_a_set_that_did_nothing_looks_the_same_as_one_that_worked()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargeStop", 80);

        SchemaGateRunner.RunGateB(wmi);

        Assert.Equal(2, wmi.Calls.Count(c => c.Method == "GetChargeStop"));
        Assert.True(wmi.Calls.FindLastIndex(c => c.Method == "GetChargeStop")
                    > wmi.Calls.FindIndex(c => c.Method == "SetChargeStop"));
    }

    [Fact]
    public void A_value_that_does_not_survive_the_round_trip_fails_the_gate()
    {
        // The Set class resolving somewhere else entirely.
        var wmi = new SequencedChargeStop(first: 80, second: 42);

        var v = SchemaGateRunner.RunGateB(wmi);

        Assert.False(v.Passed);
        Assert.Contains(v.Failures, f => f.Contains("80", StringComparison.Ordinal) && f.Contains("42", StringComparison.Ordinal));
    }

    [Fact]
    public void A_set_that_fails_outright_fails_the_gate_and_says_what_the_controller_said()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargeStop", 80);
        wmi.FailOn.Add("SetChargeStop");

        var v = SchemaGateRunner.RunGateB(wmi);

        Assert.False(v.Passed);
        Assert.Contains(v.Failures, f => f.Contains("SetChargeStop", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(59)]
    [InlineData(101)]
    [InlineData(4096)]
    public void A_charge_stop_it_would_not_be_safe_to_write_back_is_refused_rather_than_written(int value)
    {
        // THE REFUSAL THE DESIGN DOES NOT MENTION. "Write back what you read" is only harmless
        // while what you read is a charge percentage. Writing 0 back is a laptop that does not
        // charge, and 4096 truncates to 0 in the byte cast and does the same thing.
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargeStop", value);

        var v = SchemaGateRunner.RunGateB(wmi);

        Assert.False(v.Passed);
        Assert.DoesNotContain(wmi.Calls, c => c.Class == WmiClass.Set);
        // Inconclusive, not condemned: the registration may be fine and this reading merely odd.
        Assert.Contains(v.Warnings, w => w.Contains("could not be run", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_band_it_refuses_outside_is_the_one_the_battery_controller_already_enforces()
    {
        // One definition, in the shape FanSafety keeps for the fan floors.
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargeStop", BatteryController.MinStop);
        Assert.True(SchemaGateRunner.RunGateB(wmi).Passed);

        wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargeStop", BatteryController.MaxStop);
        Assert.True(SchemaGateRunner.RunGateB(wmi).Passed);
    }

    [Fact]
    public void A_get_that_does_not_answer_at_all_never_reaches_the_write()
    {
        var wmi = new FakeGigabyteWmi();
        wmi.FailOn.Add("GetChargeStop");

        var v = SchemaGateRunner.RunGateB(wmi);

        Assert.False(v.Passed);
        Assert.DoesNotContain(wmi.Calls, c => c.Class == WmiClass.Set);
    }

    [Fact]
    public void Gate_b_touches_nothing_but_the_charge_stop()
    {
        // No fan method, no policy method. The one write in this plan is the one write it makes.
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargeStop", 80);

        SchemaGateRunner.RunGateB(wmi);

        Assert.All(wmi.Calls, c => Assert.Equal("ChargeStop", c.Method[3..]));
    }

    [Fact]
    public void Nulls_are_programming_errors()
    {
        Assert.Throws<ArgumentNullException>(() => SchemaGateRunner.RunGateB(null!));
        Assert.Throws<ArgumentNullException>(() => SchemaGateRunner.Read(null!));
    }

    private static void StubFanTable(FakeGigabyteWmi wmi, IReadOnlyList<FanSlot> table) =>
        wmi.FanTable = table.ToDictionary(s => s.Index, s => (s.Temperature, s.Duty));

    /// <summary>A machine whose charge stop reads differently the second time.</summary>
    private sealed class SequencedChargeStop : IGigabyteWmi
    {
        private readonly int _first, _second;
        private int _reads;
        public SequencedChargeStop(int first, int second) { _first = first; _second = second; }

        public WmiResult Invoke(WmiClass cls, string method, IReadOnlyDictionary<string, object>? args = null) =>
            cls == WmiClass.Get && method == "GetChargeStop"
                ? WmiResult.Ok(new Dictionary<string, object> { ["Data"] = _reads++ == 0 ? _first : _second })
                : WmiResult.Ok();
    }
}
```

> **`FakeGigabyteWmi` needs one addition.** `Respond` cannot express an answer that depends on an
> input parameter, and `GetFanIndexValue` needs exactly that. Add a
> `Dictionary<int, (int Temperature, int Duty)> FanTable` property and one branch in `Invoke` that
> serves `GetFanIndexValue` from it, returning `Temperture`/`Value` under the BIOS's spelling.
> Everything else about the fake is unchanged, and the existing 1012 tests do not see it.

`tests/OpenAorus.Hardware.Tests/SchemaRecordTests.cs`:

```csharp
using OpenAorus.Hardware.Config;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The persisted proof, and every way a file on disk could claim more than it earned.
/// </summary>
public class SchemaRecordTests
{
    [Fact]
    public void A_fresh_record_claims_nothing()
    {
        var r = new SchemaRecord();

        Assert.False(r.Registered);
        Assert.False(r.GatesPassed);
        Assert.Null(r.Fingerprint);
        Assert.False(r.Repair());
    }

    [Fact]
    public void A_passed_record_with_no_fingerprint_cannot_be_trusted_and_is_cleared()
    {
        // Without the fingerprint there is nothing to compare the live schema against, so the
        // pass could not be invalidated even if the schema changed underneath it.
        var r = new SchemaRecord { Registered = true, GatesPassed = true, Fingerprint = null };

        Assert.True(r.Repair());
        Assert.False(r.GatesPassed);
    }

    [Fact]
    public void A_record_that_claims_a_pass_without_a_registration_is_cleared()
    {
        var r = new SchemaRecord { Registered = false, GatesPassed = true, Fingerprint = "aaa" };

        Assert.True(r.Repair());
        Assert.False(r.GatesPassed);
    }

    [Fact]
    public void A_record_dated_in_the_future_is_cleared()
    {
        var r = new SchemaRecord
        {
            Registered = true, GatesPassed = true, Fingerprint = "aaa",
            When = DateTime.Now.AddDays(2),
        };

        Assert.True(r.Repair());
        Assert.False(r.GatesPassed);
    }

    [Fact]
    public void A_sound_record_is_left_alone()
    {
        var r = new SchemaRecord
        {
            Registered = true, GatesPassed = true, Fingerprint = "aaa", When = DateTime.Now.AddHours(-1),
        };

        Assert.False(r.Repair());
        Assert.True(r.GatesPassed);
    }

    [Fact]
    public void Repairing_never_grants_a_pass_it_only_takes_one_away()
    {
        foreach (var r in new[]
                 {
                     new SchemaRecord(),
                     new SchemaRecord { Registered = true },
                     new SchemaRecord { Registered = true, Fingerprint = "aaa" },
                 })
        {
            r.Repair();
            Assert.False(r.GatesPassed);
        }
    }

    [Fact]
    public void Settings_always_have_a_schema_record_even_from_an_older_file()
    {
        // Same rule as Lighting and Hotkeys: a file written by v0.1 has no schema section at all.
        var s = new AppSettings { Schema = null! };

        Assert.NotNull(s.Schema);
        Assert.False(s.Schema.GatesPassed);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~SchemaGateRunnerTests`
Expected: compile error, `SchemaGateRunner` not found.

- [ ] **Step 3: Implement the runner**

`src/OpenAorus.Hardware/Wmi/Schema/SchemaGateRunner.cs`. `Read` walks
`DiagnosticsDump.GetMethods` — the same list the dump uses, so the gate and the dump can never
disagree about what was asked — and then `GetFanIndexValue` for indices 0..14. `RunGateA` is
`Read` followed by `SchemaGate.CheckReads`. `RunGateB` is the three calls with the band check in
front of the write.

- [ ] **Step 4: Implement the record and wire it into settings**

`src/OpenAorus.Hardware/Config/SchemaRecord.cs`, then `AppSettings.Schema` with the
never-null backing field the other two sections already use, then
`SettingsStore.LastLoadSchemaRepaired` and its inclusion in `LastLoadRepaired`.

- [ ] **Step 5: Run tests**

Run: `dotnet test OpenAorus.sln`
Expected: all pass. `SettingsStoreTests` and `MainViewModelRepairNoticeTests` may need the new
repair flag added to their expectations.

- [ ] **Step 6: Commit**

```bash
git add src/OpenAorus.Hardware/Wmi/Schema/SchemaGateRunner.cs \
        src/OpenAorus.Hardware/Config/SchemaRecord.cs \
        src/OpenAorus.Hardware/Config/AppSettings.cs \
        src/OpenAorus.Hardware/Config/SettingsStore.cs \
        tests/OpenAorus.Hardware.Tests/FakeGigabyteWmi.cs \
        tests/OpenAorus.Hardware.Tests/SchemaGateRunnerTests.cs \
        tests/OpenAorus.Hardware.Tests/SchemaRecordTests.cs
git commit -m "feat(schema): earn the right to write with one harmless round trip"
```

---

### Task 9: Locking the writes, and what the owner sees

**Files:**
- Modify: `src/OpenAorus.Hardware/Fans/FanController.cs`,
  `src/OpenAorus.Hardware/Battery/BatteryController.cs`,
  `src/OpenAorus.App/AppServices.cs`,
  `src/OpenAorus.App/ViewModels/MainViewModel.cs`,
  `src/OpenAorus.App/ViewModels/SettingsViewModel.cs`,
  `src/OpenAorus.App/Views/SettingsWindow.xaml`
- Create: `src/OpenAorus.App/ViewModels/SchemaViewModel.cs`
- Test: `tests/OpenAorus.Hardware.Tests/SchemaWriteGateTests.cs`,
  `tests/OpenAorus.Hardware.Tests/SchemaViewModelTests.cs`,
  `tests/OpenAorus.Hardware.Tests/SchemaSettingsUiTests.cs`
- Read: `src/OpenAorus.App/ViewModels/HotkeysViewModel.cs` and
  `tests/OpenAorus.Hardware.Tests/HotkeySettingsUiTests.cs` (the Settings-card pattern and how its
  placement is pinned), `src/OpenAorus.Hardware/Ui/BannerState.cs`

**Interfaces:**
- Produces:
  - `FanController(IGigabyteWmi wmi, ModelProfile profile, Func<int, Task>? delay = null, Func<bool>? writesUnlocked = null)`
  - `BatteryController(IGigabyteWmi wmi, Func<bool>? writesUnlocked = null)`
  - `AppServices.Schema { get; }` — a `SchemaService` holding `ISchemaSystem`, the current
    `SchemaStatus`, `bool WritesUnlocked`, and `void Refresh()`
  - `SchemaViewModel` with `Status`, `StatusText`, `CanInstall`, `CanRemove`, `NeedsElevation`,
    `InstallCommand`, `RemoveCommand`, `RunGatesCommand`, `Message`
- Consumes: everything from Tasks 4–8.

**The gate goes inside the two controllers, not at the view models.** `--apply` runs from a
scheduled task with no window at all, and it is the path that most needs stopping: on an
unregistered machine today it produces exactly the symptom the design opens with — *"step 1/5
setCurrentFanStep failed: not found"* — with nobody there to read it. `FanController` already refuses
on `!_profile.CanWrite` for the same reason the fan floors live there rather than at the slider:
**every caller passes through it, and a caller added later cannot forget.** The schema gate joins it,
one line above.

`Func<bool>` rather than a `bool`, because the state changes while the app runs: the owner presses
Install, then Run gates, and the fan buttons have to come alive without a restart.

Defaulted to `() => true` so all 1012 existing tests and every existing construction site keep
working unchanged.

**The refusal names the cause, not the symptom.** `FanController` returns
`"The Gigabyte WMI interface is not registered on this machine, so fan control is switched off.
Settings has a button that registers it."` — never `"not found"`.

**Three `CanWrite` properties become two conditions.** `MainViewModel.CanWrite`,
`BatteryViewModel.CanWrite` and `SettingsViewModel.CanWrite` all become
`_s.Profile.CanWrite && _s.Schema.WritesUnlocked`. The XAML binds to them already, so the fan strip,
the curve editor, the battery card and the top half of the Settings window all disable together with
no markup change beyond the new card.

**The schema card sits outside that gate**, and for the same structural reason the hotkey card does:
it is the card that fixes the condition disabling everything else, and greying it out would leave the
owner in a dialog whose only working control is the one that cannot help. `SchemaSettingsUiTests`
pins the placement as plain XML, exactly as `HotkeySettingsUiTests` already does for the hotkey card.

**What the card says, in three sentences and no jargon.** That it registers the interface description
that Gigabyte Control Center used to install and that uninstalling it took away; that it needs
administrator rights; and that Remove puts the machine back exactly as it was. Below that, the state
in the owner's terms and the button that applies to it. After a successful install the button becomes
**Check it works**, which runs both gates and reports the verdict — because registering and proving
are two separate things and the card should not pretend otherwise.

**One startup notice, once, through `ReportOverrideNotice`.** A machine with no schema currently says
nothing until the first write fails. Instead, `MainViewModel` reports on `SchemaStatus.Absent`:
what is missing, what it means, and where the button is. Routed through `ReportOverrideNotice` for
the reason the settings notices are: this is not a per-model condition, so an owner on an
unrecognised model has to hear it too.

- [ ] **Step 1: Write the failing tests**

`tests/OpenAorus.Hardware.Tests/SchemaWriteGateTests.cs`:

```csharp
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Wmi;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Until both gates pass, nothing is written to the controller.
/// </summary>
/// <remarks>
/// Gated inside the two controllers rather than at the view models, for the reason the fan floors
/// are: --apply runs from a scheduled task with no window, and it is the path that most needs
/// stopping. On an unregistered machine today it produces "step 1/5 setCurrentFanStep failed: not
/// found" with nobody there to read it.
/// </remarks>
public class SchemaWriteGateTests
{
    private static readonly ModelProfile Kd = ModelProfile.Detect("AORUS 17G KD");

    [Theory]
    [InlineData(FanMode.Quiet)]
    [InlineData(FanMode.Normal)]
    [InlineData(FanMode.Gaming)]
    [InlineData(FanMode.Turbo)]
    [InlineData(FanMode.Fixed)]
    [InlineData(FanMode.Custom)]
    public async Task No_fan_mode_reaches_the_controller_while_writes_are_locked(FanMode mode)
    {
        var wmi = new FakeGigabyteWmi();
        var fans = new FanController(wmi, Kd, ms => Task.CompletedTask, writesUnlocked: () => false);

        var r = await fans.ApplyAsync(mode);

        Assert.False(r.Success);
        Assert.Empty(wmi.Calls);
    }

    [Fact]
    public async Task The_refusal_names_the_cause_and_where_the_fix_is()
    {
        var fans = new FanController(new FakeGigabyteWmi(), Kd, ms => Task.CompletedTask, () => false);

        var r = await fans.ApplyAsync(FanMode.Normal);

        Assert.Contains("not registered", r.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Settings", r.Error!, StringComparison.Ordinal);
        // Never the symptom the owner saw before this release.
        Assert.DoesNotContain("step 1/", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nothing_is_applied_and_nothing_is_announced_as_applied()
    {
        var fans = new FanController(new FakeGigabyteWmi(), Kd, ms => Task.CompletedTask, () => false);
        var applied = 0;
        fans.Applied += _ => applied++;

        await fans.ApplyAsync(FanMode.Turbo);

        Assert.Equal(0, applied);
        Assert.Null(fans.LastApplied);
    }

    [Fact]
    public void No_charge_limit_reaches_the_controller_while_writes_are_locked()
    {
        var wmi = new FakeGigabyteWmi();
        var battery = new BatteryController(wmi, writesUnlocked: () => false);

        var r = battery.SetLimit(enabled: true, stopPercent: 80);

        Assert.False(r.Success);
        Assert.Empty(wmi.Calls);
    }

    [Fact]
    public void Reads_are_never_gated()
    {
        // "Reads and lighting work, writes are refused with a reason." A locked machine must
        // still show temperatures, fan speeds and battery health.
        var wmi = new FakeGigabyteWmi();
        wmi.Respond("GetChargePolicy", 0);
        var battery = new BatteryController(wmi, () => false);

        battery.Read();

        Assert.NotEmpty(wmi.Calls);
        Assert.All(wmi.Calls, c => Assert.Equal(WmiClass.Get, c.Class));
    }

    [Fact]
    public async Task Unlocking_takes_effect_without_a_restart()
    {
        // The owner presses Install, then Check it works, and the fan buttons come alive. A bool
        // captured at construction would need the app restarted.
        var unlocked = false;
        var wmi = new FakeGigabyteWmi();
        var fans = new FanController(wmi, Kd, ms => Task.CompletedTask, () => unlocked);

        Assert.False((await fans.ApplyAsync(FanMode.Normal)).Success);
        unlocked = true;
        Assert.True((await fans.ApplyAsync(FanMode.Normal)).Success);
    }

    [Fact]
    public async Task The_model_gate_and_the_schema_gate_are_both_required()
    {
        var unknown = ModelProfile.Detect("Some Other Laptop");
        var fans = new FanController(new FakeGigabyteWmi(), unknown, ms => Task.CompletedTask, () => true);

        var r = await fans.ApplyAsync(FanMode.Normal);

        Assert.False(r.Success);
        Assert.Contains("not recognised", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Leaving_the_gate_out_keeps_every_existing_call_site_working()
    {
        // Defaulted to open, so the 1012 tests already in this suite and every existing
        // construction site are untouched.
        var fans = new FanController(new FakeGigabyteWmi(), Kd, ms => Task.CompletedTask);

        Assert.True((await fans.ApplyAsync(FanMode.Normal)).Success);
        Assert.True(new BatteryController(new FakeGigabyteWmi()).SetLimit(true, 80).Success);
    }
}
```

`tests/OpenAorus.Hardware.Tests/SchemaViewModelTests.cs` — the card's behaviour over
`FakeSchemaSystem`: that `CanInstall`/`CanRemove` follow `SchemaState`, that `InstallCommand` on a
`Foreign` machine does nothing and says why, that a successful install leaves `Status == Ours` with
writes still locked and the button now reading **Check it works**, that `RunGatesCommand` on a
healthy fake reaches `OursGated`, that a failing Gate A leaves writes locked and puts the first
failure in `Message`, and that `Message` never contains the string `"not found"`.

`tests/OpenAorus.Hardware.Tests/SchemaSettingsUiTests.cs` — the markup as plain XML, in the shape
`HotkeySettingsUiTests` already uses: the schema card exists, it is a sibling of the `CanWrite`
panel and never a child of it, and its buttons bind to `InstallCommand`, `RemoveCommand` and
`RunGatesCommand`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test OpenAorus.sln --filter FullyQualifiedName~SchemaWriteGateTests`
Expected: compile error, `FanController` has no such constructor.

- [ ] **Step 3: Gate the two controllers**

One guard each, immediately after the existing `CanWrite` guard, with the comment saying why it is
here and not at the callers.

- [ ] **Step 4: Wire `AppServices`**

A `SchemaService` built in `Create()` over `new WindowsSchemaSystem()`, which does one `Look` and one
`SchemaState.Classify` against `settings.Schema` and `SchemaMof.Fingerprint`. `Refresh()` redoes it.
`fans` and `battery` are constructed with `() => Schema.WritesUnlocked`.

Nothing here invokes a firmware method — `Look` reads class metadata only — so `--dump` and
`--apply` pay one cheap WMI connection for the state, and `--apply` on a locked machine now exits
with a message that names the cause.

- [ ] **Step 5: The card and the startup notice**

`SchemaViewModel`, the `SettingsWindow.xaml` card as a sibling of the `CanWrite` panel, and the
`MainViewModel` constructor branch that raises the override notice on `SchemaStatus.Absent` and on
`SchemaStatus.Foreign` where the fingerprint changed under a recorded pass.

- [ ] **Step 6: Run tests and build Release**

Run: `dotnet test OpenAorus.sln`
Run: `dotnet build OpenAorus.sln -c Release`
Expected: all pass, no warnings at either configuration. `XamlResourceTests` picks up the new markup
automatically.

- [ ] **Step 7: Commit**

```bash
git add src/OpenAorus.Hardware/Fans/FanController.cs \
        src/OpenAorus.Hardware/Battery/BatteryController.cs \
        src/OpenAorus.App/AppServices.cs \
        src/OpenAorus.App/ViewModels/SchemaViewModel.cs \
        src/OpenAorus.App/ViewModels/MainViewModel.cs \
        src/OpenAorus.App/ViewModels/SettingsViewModel.cs \
        src/OpenAorus.App/Views/SettingsWindow.xaml \
        tests/OpenAorus.Hardware.Tests/SchemaWriteGateTests.cs \
        tests/OpenAorus.Hardware.Tests/SchemaViewModelTests.cs \
        tests/OpenAorus.Hardware.Tests/SchemaSettingsUiTests.cs
git commit -m "feat(schema): refuse every write until the schema has been proved"
```

---

### Task 10: Documentation, and the hardware checklist

**Files:**
- Modify: `README.md`, `VERIFY.md`, `src/OpenAorus.App/OpenAorus.App.csproj`
- Read: every `OWNER VERIFY` note raised in Tasks 2, 4, 6, 7, 8 and 9; the three notes at the bottom
  of `docs/research/dump-aorus-17g-kd-known-good.txt`

**Nothing in this release has been run against a WMI repository.** The MOF has never been compiled,
the classes have never been created, and neither gate has ever seen a firmware answer. Every test in
the suite is a test of a decision, and all of the decisions are downstream of one unverified fact:
that a MOF printed from a decompiled schema dump binds to the same ACPI blocks Gigabyte's did.

**The owner's machine is the right one to find out on, and it will not stay that way.** It has no
Gigabyte software on it at all — which is the exact starting state this feature is for, the state
that makes `SchemaStatus.Absent` reachable, and the only state in which a clean install can be
observed without a foreign schema confusing the result. The moment Control Center is reinstalled to
compare behaviour, that is gone.

> **Numbering.** `VERIFY.md` runs to section 7. Add this as **section 8**, before the
> `## Assumptions this checklist is really testing` heading.

- [ ] **Step 1: README**

Add a section after "Fn hotkeys and the overlay (v0.3)". State plainly:

- that OpenAorus no longer needs Gigabyte Control Center installed to talk to the hardware, and that
  it used to — not because it called GCC, but because GCC's installer was the only thing providing
  `acpimof.dll`, the file that declares the `GB_WMIACPI_*` classes to WMI;
- that Settings has a button that registers those classes itself, that it needs administrator rights,
  and that it is reversible;
- that the schema is **generated** from a dump recovered from a real machine, is checked into the
  repository so it can be read, and is regenerated by the test suite on every run so it cannot drift;
- that registering does not enable fan control on its own: writes stay locked until a read gate and a
  one-value round trip both pass, because a wrong method id would call a different firmware method
  than the one the app believes it is calling;
- that OpenAorus never registers over an existing schema and never removes one it did not create, so
  it is safe to have Control Center installed alongside;
- that **none of it has been confirmed on hardware**, and that the schema was recovered from an
  AORUS 17G KD — other models will have other ids, and a dump from one is the thing to send.

- [ ] **Step 2: `VERIFY.md` section 8**

```markdown
## 8. Registering the WMI schema ourselves

Nothing here has ever been run. The MOF has never been compiled, the classes have never been
created, and neither gate has ever seen a firmware answer. If the base class is wrong or the ids
are off, this section is what says so - and it is the only thing that would.

**This machine is the right one to do it on.** It has no Gigabyte software on it at all, which is
exactly the state the feature is for and the only state in which a clean registration can be
watched without a foreign schema in the way. Once Control Center goes back on to compare, that is
gone. Do 8.1 to 8.5 before reinstalling anything.

Work in order. 8.2 partitions everything below it.

### 8.1 What the machine says before anything is registered

- [ ] Start OpenAorus. It must say once, in the banner, that the interface is not registered -
      what that means and where the button is. Not "step 1/5 setCurrentFanStep failed: not found"
- [ ] The fan mode strip, the curve editor and the battery card are all disabled
- [ ] Lighting still works and the temperatures still update. Reads and lighting are never gated
- [ ] Settings shows the schema card, enabled, even though everything above it is greyed out
- [ ] Run `OpenAorus.exe --apply` from an elevated prompt. It must exit non-zero with a message
      naming the missing registration, not with a step number

### 8.2 Does mofcomp accept the file at all

- [ ] From an elevated prompt, run the syntax check by hand first:
      `%SystemRoot%\System32\wbem\mofcomp.exe -check "<path to GB_WMIACPI.mof>"`
      (the app writes it to `%LOCALAPPDATA%\OpenAorus\schema\` when Install is pressed; copy it
      out of the repository instead if you want to check before pressing anything)
- [ ] **If it rejects `class GB_WMIACPI_Event : WMIEvent`**, that is the single largest guess in
      this release. The dump lists `SECURITY_DESCRIPTOR` and `TIME_CREATED` on that class, which
      are WMI's own event-base members, so the original almost certainly derived from something -
      but the dump does not record superclasses. Try `__ExtrinsicEvent`. `MofWriter.EventBaseClass`
      is one constant
- [ ] Record the exact error text. It names the line and the qualifier and nothing else can
- [ ] If it rejects anything else, stop here and record it. Nothing below is worth attempting

### 8.3 Registration

- [ ] Press Install in Settings and accept the UAC prompt if one appears
- [ ] It reports success, and the card now shows the schema as registered but not yet checked
- [ ] Fan control is **still disabled**. That is correct: registering is not proving
- [ ] Check the classes exist:
      `Get-CimClass -Namespace root/WMI -ClassName GB_WMIACPI_Get` (and `_Set`, `_Data`, `_Event`)
- [ ] Check ours is the one that is there:
      `Get-CimInstance -Namespace root/WMI -ClassName OpenAorus_SchemaMarker`
- [ ] If mofcomp failed, check that **nothing** was left behind - all four classes absent and no
      marker. A half-registered machine is the failure this design most wants to avoid, and if it
      happened, say so with the exact output

### 8.4 Gate A - the reads

- [ ] Press "Check it works". Gate A runs first and reports how many methods it compared
- [ ] Export a diagnostics dump from the button in the window and compare it by eye against
      `docs/research/dump-aorus-17g-kd-known-good.txt`. The comparison that matters most is
      **which methods answer and which say "Invalid object"** - those 30 failures are the firmware
      saying it does not implement those method ids, and reproducing them is the evidence
- [ ] `getCpuTemp` and `getGpuTemp1` must be plausible temperatures. 0 or 255 means a wrong id
- [ ] The fan table must read back a rising temperature series. If a custom curve is applied it
      will stop at a (0,0) terminator with stale values after it - that is expected and the gate
      allows for it
- [ ] **If Gate A fails**, do not press anything else. Export the dump, press Remove, and record
      both. A failing Gate A on a fresh registration means the ids are wrong

### 8.5 Gate B - the one write

- [ ] Note the charge limit the battery card shows before pressing anything
- [ ] Gate B reads `GetChargeStop`, writes the same value back, and reads it again. It must come
      back unchanged, and the charge limit in the card must be exactly what it was
- [ ] If the machine's charge stop reads outside 60-100 %, the gate refuses to write and reports
      that it could not be run. That is not a failure of the registration; record the value it read
- [ ] After both gates pass, the fan strip, curve editor and battery card come alive **without
      restarting the app**
- [ ] Set a fan mode. Compare the duty read-back against what the mode should be. This is the
      first moment anything in this release has driven the hardware

### 8.6 Living beside Control Center

- [ ] Reinstall Gigabyte Control Center. Start OpenAorus
- [ ] Install must be refused - it must not register over GCC's schema
- [ ] **Remove must also be refused**, and this is the one that matters: our removal deletes by
      class name, and the names are Gigabyte's, so removing here would break GCC
- [ ] Whether OpenAorus still reports writes as unlocked depends on whether GCC's schema maps the
      same ids as the one we recovered from this machine. It should, because that is where the
      dump came from. If it does not, the app locks writes again and says the schema changed -
      record which
- [ ] Uninstall GCC again. OpenAorus must go back to reporting the interface as missing and
      offering Install

### 8.7 Removal puts the machine back

- [ ] With our schema registered and gated, press Remove
- [ ] All four classes and the marker are gone (`Get-CimClass` errors on each)
- [ ] Fan control is disabled again and the banner says why
- [ ] Press Install again. It works, and the gates have to be re-run - a removed schema is not a
      remembered pass
- [ ] Press Remove twice in a row. The second press must be harmless

### 8.8 The record survives a reboot, and stops counting when it should not

- [ ] With both gates passed, reboot. Fan control works immediately, with no gate run and no
      prompt. That is the point of recording it
- [ ] Run `%SystemRoot%\System32\wbem\mofcomp.exe` by hand against some other MOF that touches
      one of these classes, or delete one class with
      `Remove-CimClass -Namespace root/WMI -ClassName GB_WMIACPI_Data`, and restart the app. It
      must notice, lock writes and say the schema changed underneath it
- [ ] Edit `settings.json` by hand to set `"GatesPassed": true` with no fingerprint. The app must
      clear it on load and say settings were repaired

### 8.9 The things nobody could check without this machine

- [ ] Does `mofcomp` accept the event class's base? (8.2)
- [ ] Do the registered classes bind to the firmware at all, or do they register cleanly and
      answer "Invalid object" to everything? (8.4 - a Gate A where *nothing* answers is this)
- [ ] Does `SetChargeStop` on our registration reach the same firmware slot `GetChargeStop`
      reads? (8.5 - this is the entire content of Gate B)
- [ ] Does a Windows update, or a `winmgmt /resetrepository`, drop the classes? The app does not
      use `-AutoRecover`, so it should - and should then say so at the next start (8.8)
```

- [ ] **Step 3: Add the assumptions to the existing list**

Append to `## Assumptions this checklist is really testing`, in the voice of the entries there:

```markdown
- **That a MOF printed from a decompiled schema dump binds to the same ACPI blocks Gigabyte's
  did.** The whole release rests on this and nothing has tested it. The GUIDs, the 143 method ids
  and the 249 parameter ids all came out of one machine's registry while Control Center was
  installed. Section 8.4.

- **That `GB_WMIACPI_Event` derives from `WMIEvent`.** The dump records qualifiers, properties and
  methods but never a superclass, and `SECURITY_DESCRIPTOR` and `TIME_CREATED` appearing among its
  properties is the only evidence there was one. If `mofcomp` rejects it, `__ExtrinsicEvent` is the
  next guess and `MofWriter.EventBaseClass` is one constant. Section 8.2.

- **That omitting `Locale(1033)` changes nothing.** The recovered classes carry `Locale=MS\0x409`
  and ours do not, because emitting a localisation qualifier without the amended namespace it
  points at would declare a translation that does not exist. It is not part of what binds. Section
  8.4 would show it if it were.

- **That the 30 methods answering "Invalid object" are a property of the method ids and not of the
  moment.** The research file says so - they are methods this model does not implement - and Gate
  A's strongest check is built on it. If they turn out to move with machine state, that check
  becomes noise and the gate needs re-thinking. Section 8.4.

- **That writing a charge stop back to itself is harmless.** It is a no-op by construction, and
  the gate refuses to run at all unless the value read is inside 60-100 % - because writing back a
  0 that was really a failed read is a laptop that does not charge. Section 8.5.

- **That `-class:createonly` really does refuse rather than merge.** It is the tool-enforced half
  of "never register over a working schema", and the half that closes the gap between our own
  check and the compile. Section 8.6.

- **That not using `-AutoRecover` is the right trade.** It keeps the registration removable, at
  the cost of not surviving a WMI repository rebuild. The app detects the loss and offers the
  button again, which is one extra click rather than a machine-wide list we cannot cleanly get
  back out of. Section 8.8.

- **That the fingerprint is enough to notice a schema changing underneath a recorded pass.** It
  covers every method name and id in both classes and nothing else. A change that left all 143
  mappings identical would not be noticed - and would not matter, because the mapping is the
  thing the gates proved. Section 8.8.
```

- [ ] **Step 4: Check every `OWNER VERIFY` item has a home**

Walk Tasks 2, 4, 6, 7, 8 and 9 and confirm each appears above: the event base class (8.2), `mofcomp`
accepting the file (8.2), nothing half-registered on failure (8.3), the classes binding to firmware
(8.4), the "Invalid object" partition (8.4), the fan table terminator (8.4), the round trip (8.5),
the charge-stop refusal band (8.5), Control Center alongside (8.6), removal (8.7), the record
surviving a reboot and being invalidated (8.8), and `-AutoRecover` (8.8, assumptions).

- [ ] **Step 5: Bump the version**

`src/OpenAorus.App/OpenAorus.App.csproj`: `<Version>0.3.1</Version>`. A patch rather than a minor:
this restores the app's ability to run at all on a machine it used to work on, and adds no feature
the owner asked for. If the v0.4 profiles work lands first, this becomes `0.4.x` and the number is
the only thing that changes.

- [ ] **Step 6: Run tests and build Release**

Run: `dotnet test OpenAorus.sln`
Run: `dotnet build OpenAorus.sln -c Release`
Expected: all pass, no warnings at either configuration.

- [ ] **Step 7: Commit**

```bash
git add README.md VERIFY.md src/OpenAorus.App/OpenAorus.App.csproj
git commit -m "docs: document registering the WMI schema and what hardware must settle"
```

---

## Owner acceptance checklist (end of this release)

- [ ] With no Gigabyte software installed, OpenAorus starts and says the interface is not
      registered — a cause, not a step number
- [ ] Reads, sensors and lighting all work while writes are locked
- [ ] Install registers the schema, and fan control stays disabled until the gates are run
- [ ] Both gates pass, and the fan strip comes alive without restarting the app
- [ ] A fan mode applies and the duty read-back matches
- [ ] The charge limit is exactly what it was before Gate B ran
- [ ] Remove takes all four classes and the marker back out, and Install works again afterwards
- [ ] With Control Center installed, both Install and Remove are refused with a reason
- [ ] Rebooting does not re-run the gates; deleting a class by hand does
- [ ] Nothing in the fan, sensor, battery, lighting or hotkey behaviour regressed

---

## Risks and open questions

### What rests on reconstruction rather than observation

**The whole binding.** Four GUIDs, 143 method ids and 249 parameter ids were read out of one
machine's WMI repository while Gigabyte's `acpimof.dll` was still registering them. That is a strong
provenance — it is the mapping that demonstrably worked on this exact chassis — but it was read out
of a registry, not out of the firmware, and nothing has yet compiled it back in. Everything else in
this plan is downstream of it.

**The event class's superclass.** The single largest guess in the release, and the one most likely
to fail first because it fails at `mofcomp -check` rather than at runtime. The dump records what WMI
enumerated, and WMI enumerates inherited members without saying they are inherited, so
`SECURITY_DESCRIPTOR` and `TIME_CREATED` are the only evidence there was a base class at all. If
`WMIEvent` is wrong, nothing registers; if it is right but `Active`/`InstanceName` are also inherited
and we redeclare them, `mofcomp` will say so. Both are cheap to find out and cheap to fix.

**That the classes bind rather than merely exist.** A MOF with a wrong GUID compiles perfectly and
creates a class that answers `Invalid object` to everything. Gate A's failure mode where *nothing*
answers is exactly this, and it is indistinguishable from a machine whose firmware is not responding
— which is why 8.4 asks for the dump rather than just the verdict.

**Gate A's strongest check rests on one sentence in a research file.** The claim that the 30
`Invalid object` answers are a property of the method ids and not of machine state comes from the
known-good dump's own notes and from one comparison against a later dump. If those methods turn out
to move for other reasons, the partition check becomes noise and the gate falls back to four much
weaker checks.

**The design's fan-table description is wrong and the plan corrects it.** Section 6 asks for "a
fifteen-slot fan table that is monotonic in temperature". The research says a machine with a custom
curve applied reads back the curve, a `(0,0)` terminator, and stale values after it. A gate written
to the design's words would fail every machine whose owner uses a curve.

**The design's Gate B description is incomplete and the plan adds a refusal.** "Call `SetChargeStop`
with the value it just returned" is harmless only while the returned value is a charge percentage.
`GetChargeStop` answers `uint16` and `SetChargeStop` takes `uint8`; a read of `0`, or of anything
above 255, becomes a written `0`, which is a laptop that does not charge. The gate refuses outside
60–100 % and reports itself inconclusive.

**Removal is as dangerous as registration and the design does not say so.** The remove MOF deletes
by class name, and the names are Gigabyte's. On a machine with Control Center installed, pressing
Remove would break Control Center. `CanRemove(Foreign)` is false, and the registrar re-checks at the
moment of the click rather than trusting a classification taken when the window opened.

**No `-AutoRecover`, so a WMI repository rebuild silently drops the schema.** The state check at
startup catches it and the owner presses Install again. The alternative — writing our MOF into the
machine-wide auto-recovery list — would survive the rebuild at the cost of a footprint the Remove
button cannot cleanly undo, which contradicts the reversibility the whole feature is modelled on.

**`GB_WMIACPI_Data` is registered and never used.** Registering three of four would put the machine
in a configuration nobody has ever run. Registering all four means one more class we cannot say
anything about.

### What the first hardware session should do, in order

The order below is chosen so that each step either eliminates the largest remaining uncertainty or
makes the next step interpretable. **Do all of it before reinstalling Control Center**, because the
machine's current state — no Gigabyte software at all — is the only one in which a clean install can
be watched, and it is not recoverable once GCC has been on again.

1. **`mofcomp -check` the MOF by hand, before pressing anything in the app.** One command, and it
   settles the event base class, every qualifier `mofcomp` might not know, and whether the file is
   syntactically a MOF at all. If it fails, nothing else in the session is worth doing, and the fix
   is a one-constant change plus a regenerate. This is the cheapest step and it eliminates the
   largest single risk.
2. **Press Install and check the four classes and the marker exist.** Separates "the file compiles"
   from "the repository accepted it", which are different failures with different fixes.
3. **Export a diagnostics dump and read it against the known-good file.** Do this *before* looking
   at the gate verdict, so the verdict is checked against the evidence rather than believed. The
   line to look at first is which methods say `Invalid object`: if that set is close to the
   known-good 30, the ids are right and everything after is detail. If *everything* fails, the
   classes registered and did not bind. If everything succeeds, something is answering that should
   not be, and the ids are probably shifted.
4. **Run the gates.** Gate A's verdict should now be unsurprising. Gate B is the first write.
5. **Apply a fan mode and read the duty back.** The first time anything in this release drives the
   hardware, and the first confirmation that the `Set` class is doing what it says.
6. **Press Remove, confirm the machine is exactly as it started, and press Install again.** Proves
   reversibility while the machine is still in the clean state that makes "exactly as it started"
   checkable.
7. **Only now reinstall Control Center**, and check that both buttons refuse and that the app says
   something true about the schema it finds.

If time runs out, steps 1 to 3 are the ones worth having: they convert the largest guesses in this
plan into facts, and they are all reads.

using System.Diagnostics;
using System.IO;
using System.Text;
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

    // ---------------------------------------------------------------------------------------
    // Beyond the checklist: the properties that make the file safe rather than merely shaped
    // like a MOF.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_parameter_Gigabyte_spelled_wrong_is_still_spelled_wrong()
    {
        // "Temperture", missing an "a", with a correctly spelled Description beside it. The name
        // is what System.Management binds by, so correcting it would rename a buffer slot. It
        // appears twice: once out of GetFanIndexValue and once into SetFanIndexValue.
        Assert.Equal(2, CountOf(Install, "uint8 Temperture"));
        Assert.Contains("Description(\"Temperature\")] uint8 Temperture", Install, StringComparison.Ordinal);

        // The five correctly spelled GetDeepFan slots are untouched by that, which is the point:
        // nothing here normalises a name in either direction.
        for (var i = 0; i < 5; i++)
            Assert.Contains($"uint8 Temperature{i}", Install, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_method_prints_its_whole_parameter_list_with_no_slot_missing_or_repeated()
    {
        // The count test above proves 143 methods reached the file. This proves each of them
        // took all of its parameters along, on the ids the dump recorded, in ascending order -
        // which is what makes the printed signature readable as the ACPI buffer layout.
        foreach (var m in Schema.Classes.SelectMany(c => c.Methods))
        {
            var line = SoleLineContaining($"void {m.Name}(");

            var printed = new List<int>();
            for (var i = line.IndexOf("WmiDataId(", StringComparison.Ordinal); i >= 0;
                 i = line.IndexOf("WmiDataId(", i + 1, StringComparison.Ordinal))
            {
                var open = i + "WmiDataId(".Length;
                printed.Add(int.Parse(line[open..line.IndexOf(')', open)]));
            }

            Assert.Equal(m.Parameters.Select(p => p.DataId).OrderBy(id => id).ToArray(), printed.ToArray());

            foreach (var p in m.Parameters)
            {
                var direction = p.Direction == WmiParamDirection.In ? "in" : "out";
                Assert.Contains($"[{direction}, WmiDataId({p.DataId}), ", line, StringComparison.Ordinal);
                Assert.Contains($" {p.Name}", line, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void A_description_carrying_a_quote_or_a_backslash_is_escaped_rather_than_printed_raw()
    {
        // No recovered description contains either, so nothing exercises this today. An
        // unescaped quote would close the string early and take the rest of the qualifier list
        // with it - which mofcomp would either reject or, worse, read as something else.
        var text = MofWriter.WriteInstall(Awkward(), "fp\"ing\\erprint");

        Assert.Contains(@"Description(""a \""quoted\"" word"")", text, StringComparison.Ordinal);
        Assert.Contains(@"Description(""a back\\slash"")", text, StringComparison.Ordinal);
        Assert.Contains(@"Fingerprint = ""fp\""ing\\erprint"";", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // The strongest test available: Windows' own compiler reads the file back.
    //
    // -check parses and stops. It opens no repository, writes nothing, and needs no elevation,
    // so it is safe in a test run. Where mofcomp is not present - CI on anything but Windows -
    // these skip rather than fail, and the string assertions above still stand on their own.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_install_file_parses_in_the_compiler_that_will_register_it()
    {
        AssertMofComp(Install, nameof(Install));
    }

    [Fact]
    public void The_remove_file_parses_in_the_compiler_that_will_run_it()
    {
        AssertMofComp(Remove, nameof(Remove));
    }

    [Fact]
    public void An_awkwardly_described_schema_still_parses_in_the_real_compiler()
    {
        AssertMofComp(MofWriter.WriteInstall(Awkward(), "fp\"ing\\erprint"), "awkward");
    }

    /// <summary>A schema whose descriptions contain the two characters MOF strings must escape.</summary>
    private static WmiSchema Awkward() => new(new[]
    {
        new WmiSchemaClass(
            "OpenAorus_Awkward",
            "{ABBC0F6F-8EA1-11d1-00A0-C90629100000}",
            "a \"quoted\" word",
            new[] { new WmiSchemaProperty("InstanceName", WmiParamType.String, true, true, false, null, null, "") },
            new[]
            {
                new WmiSchemaMethod("Awkward", 1, "a back\\slash", new[]
                {
                    new WmiSchemaParam("Data", WmiParamType.UInt8, WmiParamDirection.Out, 0, "a back\\slash"),
                }),
            }),
    });

    private static void AssertMofComp(string mof, string what)
    {
        var mofcomp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wbem", "mofcomp.exe");
        if (!File.Exists(mofcomp)) return;

        var path = Path.Combine(Path.GetTempPath(), $"openaorus-{what}-{Guid.NewGuid():N}.mof");
        try
        {
            // ASCII on the wire. mofcomp reads ANSI or UTF-16-with-BOM, and a stray UTF-8 BOM is
            // one of the few ways a byte-correct file still fails to parse.
            File.WriteAllText(path, mof, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            using var p = Process.Start(new ProcessStartInfo(mofcomp)
            {
                // -check parses only. Without it this would write to the live WMI repository.
                Arguments = $"-check \"{path}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;

            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();

            Assert.True(p.ExitCode == 0, $"mofcomp -check rejected the {what} MOF:\r\n{output}");
            Assert.Contains("successfully parsed", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string HeaderOf(string className)
    {
        var i = Install.IndexOf($"class {className}", StringComparison.Ordinal);
        var start = Install.LastIndexOf('[', i);
        return Install[start..i];
    }

    /// <summary>The one line of the install MOF containing <paramref name="needle"/>.</summary>
    private static string SoleLineContaining(string needle)
    {
        var hits = Install.Split("\r\n").Where(l => l.Contains(needle, StringComparison.Ordinal)).ToArray();
        Assert.Single(hits);
        return hits[0];
    }

    private static int CountOf(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}

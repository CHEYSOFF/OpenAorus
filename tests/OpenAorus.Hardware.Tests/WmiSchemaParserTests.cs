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

    /// <summary>
    /// Reads the recovered dump, refusing to continue if it is not where the build put it.
    /// </summary>
    /// <remarks>
    /// A test that quietly parsed an empty string would agree with every assertion below that
    /// counts something, because zero methods never contradicts a spot check of one. Reading the
    /// real file is the only thing that makes any of this meaningful, so a missing file says so
    /// instead of passing.
    /// </remarks>
    internal static string ReadDump()
    {
        Assert.True(File.Exists(DumpPath),
            $"The recovered schema is not at {DumpPath}. The <None Include=...> item in the test " +
            "project copies it there; without it these tests prove nothing.");
        var text = File.ReadAllText(DumpPath);
        Assert.False(string.IsNullOrWhiteSpace(text), $"The recovered schema at {DumpPath} is empty.");
        return text;
    }

    internal static WmiSchema Recovered() => WmiSchemaParser.Parse(ReadDump());

    // Parsed once. Lazy rather than a plain initialiser so that a missing dump fails the tests
    // that read it, by name, instead of turning every test in the class into a type-initialiser
    // error that says nothing about the file.
    private static readonly Lazy<WmiSchema> s_recovered = new(Recovered);

    private static WmiSchema Schema => s_recovered.Value;

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

    [Fact]
    public void The_two_classes_that_carry_no_methods_carry_none()
    {
        // Stated rather than implied by the total: a parser that folded the Data and Event bodies
        // into the class above them would still add up to 143.
        Assert.Empty(Schema.Class(WmiSchemaParser.DataClass).Methods);
        Assert.Empty(Schema.Class(WmiSchemaParser.EventClass).Methods);
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
        Assert.Equal(guid, Schema.Class(name).Guid);
    }

    [Fact]
    public void Asking_for_a_class_the_dump_does_not_have_names_the_class_it_wanted()
    {
        var ex = Assert.Throws<KeyNotFoundException>(() => Schema.Class("GB_WMIACPI_Nope"));
        Assert.Contains("GB_WMIACPI_Nope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_out_parameter_method_parses_whole()
    {
        var m = Schema.Class(WmiSchemaParser.GetClass).Methods.Single(x => x.Name == "GetCPUFanDuty");

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
        var m = Schema.Class(WmiSchemaParser.GetClass).Methods.Single(x => x.Name == "GetFanIndexValue");

        Assert.Equal(104, m.MethodId);
        Assert.Equal(
            new[]
            {
                ("Index", 0, WmiParamDirection.In),
                ("Temperture", 1, WmiParamDirection.Out),
                ("Value", 2, WmiParamDirection.Out),
            },
            m.Parameters.OrderBy(p => p.DataId).Select(p => (p.Name, p.DataId, p.Direction)).ToArray());
    }

    [Fact]
    public void The_ten_slot_deep_fan_method_keeps_all_ten_ids()
    {
        var m = Schema.Class(WmiSchemaParser.GetClass).Methods.Single(x => x.Name == "GetDeepFan");

        Assert.Equal(96, m.MethodId);
        Assert.Equal(10, m.Parameters.Count);
        Assert.Equal(Enumerable.Range(0, 10), m.Parameters.Select(p => p.DataId).OrderBy(i => i));
        Assert.All(m.Parameters, p => Assert.Equal(WmiParamDirection.Out, p.Direction));
    }

    [Fact]
    public void A_set_method_keeps_its_in_parameter_and_its_out_parameter_apart()
    {
        var m = Schema.Class(WmiSchemaParser.SetClass).Methods.Single(x => x.Name == "SetChargeStop");

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
        var get = Schema.Class(WmiSchemaParser.GetClass);
        var set = Schema.Class(WmiSchemaParser.SetClass);

        Assert.Equal("CheckHeavyLoading", get.Methods.Single(m => m.MethodId == 88).Name);
        Assert.Equal("SetSuperQuiet", set.Methods.Single(m => m.MethodId == 88).Name);
        Assert.Equal("GetChargeStop", get.Methods.Single(m => m.MethodId == 101).Name);
        Assert.Equal("SetChargeStop", set.Methods.Single(m => m.MethodId == 101).Name);
    }

    [Fact]
    public void The_only_parameter_types_in_the_recovered_schema_are_uint8_and_uint16()
    {
        var types = Schema.Classes.SelectMany(c => c.Methods).SelectMany(m => m.Parameters)
            .Select(p => p.Type).Distinct().OrderBy(t => t).ToArray();

        Assert.Equal(new[] { WmiParamType.UInt8, WmiParamType.UInt16 }, types);
    }

    [Fact]
    public void The_two_parameter_types_appear_exactly_as_often_as_the_file_says()
    {
        // Counted straight out of the file. A type read at the wrong width prints a MOF the
        // firmware then fills from the wrong number of bytes, and a count is the cheapest way to
        // notice that every UInt16 quietly became a UInt8.
        var all = Schema.Classes.SelectMany(c => c.Methods).SelectMany(m => m.Parameters).ToArray();

        Assert.Equal(228, all.Count(p => p.Type == WmiParamType.UInt8));
        Assert.Equal(21, all.Count(p => p.Type == WmiParamType.UInt16));
    }

    [Fact]
    public void Method_ids_are_unique_inside_a_class()
    {
        // Not guaranteed by the file format; guaranteed by this file. If a future dump breaks it,
        // the parser must stop rather than let MofWriter print two methods on one id.
        foreach (var c in Schema.Classes)
            Assert.Equal(c.Methods.Count, c.Methods.Select(m => m.MethodId).Distinct().Count());
    }

    [Fact]
    public void Method_names_are_unique_inside_a_class()
    {
        foreach (var c in Schema.Classes)
            Assert.Equal(c.Methods.Count, c.Methods.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Data_ids_are_unique_inside_a_method()
    {
        foreach (var m in Schema.Classes.SelectMany(c => c.Methods))
            Assert.Equal(m.Parameters.Count, m.Parameters.Select(p => p.DataId).Distinct().Count());
    }

    [Fact]
    public void Every_recovered_method_carries_at_least_one_parameter()
    {
        // True of all 143, and worth pinning: a method whose parameter block went missing still
        // parses and still prints, and would then call the firmware with an empty buffer.
        Assert.All(Schema.Classes.SelectMany(c => c.Methods), m => Assert.NotEmpty(m.Parameters));
    }

    [Fact]
    public void The_two_data_carrying_classes_keep_their_properties()
    {
        var data = Schema.Class(WmiSchemaParser.DataClass);
        var value = data.Properties.Single(p => p.Name == "Data");
        Assert.Equal(WmiParamType.UInt32, value.Type);
        Assert.Equal(1, value.DataId);

        var evt = Schema.Class(WmiSchemaParser.EventClass);
        var payload = evt.Properties.Single(p => p.Name == "Data");
        Assert.Equal(WmiParamType.UInt8Array, payload.Type);
        Assert.Equal(1, payload.DataId);
        Assert.Equal(4, payload.Max);
    }

    [Fact]
    public void A_property_with_an_empty_qualifier_list_still_parses()
    {
        // GB_WMIACPI_Event declares SECURITY_DESCRIPTOR and TIME_CREATED with "[]" and nothing
        // else. They are the only lines in the file carrying no qualifiers at all.
        var evt = Schema.Class(WmiSchemaParser.EventClass);

        var sd = evt.Properties.Single(p => p.Name == "SECURITY_DESCRIPTOR");
        Assert.Equal(WmiParamType.UInt8Array, sd.Type);
        Assert.Null(sd.DataId);
        Assert.Null(sd.Max);
        Assert.False(sd.CanRead);
        Assert.False(sd.CanWrite);
        Assert.False(sd.IsKey);

        Assert.Equal(WmiParamType.UInt64, evt.Properties.Single(p => p.Name == "TIME_CREATED").Type);
    }

    [Fact]
    public void The_key_property_every_class_shares_is_read_as_a_key()
    {
        foreach (var c in Schema.Classes)
        {
            var instance = c.Properties.Single(p => p.Name == "InstanceName");
            Assert.Equal(WmiParamType.String, instance.Type);
            Assert.True(instance.IsKey);
            Assert.True(instance.CanRead);
            Assert.False(instance.CanWrite);
        }
    }

    [Fact]
    public void Every_property_line_in_the_file_is_accounted_for()
    {
        // Twelve, counted out of the file: two each on Get and Set, three on Data, five on Event.
        Assert.Equal(12, Schema.Classes.Sum(c => c.Properties.Count));
        Assert.Equal(2, Schema.Class(WmiSchemaParser.GetClass).Properties.Count);
        Assert.Equal(2, Schema.Class(WmiSchemaParser.SetClass).Properties.Count);
        Assert.Equal(3, Schema.Class(WmiSchemaParser.DataClass).Properties.Count);
        Assert.Equal(5, Schema.Class(WmiSchemaParser.EventClass).Properties.Count);
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
        var names = Schema.Class(WmiSchemaParser.GetClass).Methods.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var missing = OpenAorus.Hardware.Diagnostics.DiagnosticsDump.GetMethods.Where(m => !names.Contains(m)).ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_method_the_fan_controller_writes_exists_in_the_recovered_Set_class()
    {
        var names = Schema.Class(WmiSchemaParser.SetClass).Methods.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

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

    // ---------------------------------------------------------------------------------------
    // Every case below mutates one line of a dump that is known to parse, so a FormatException
    // can only have come from the mutation. Asserting over a bare fragment instead would let a
    // case pass because the fragment had three classes rather than because the parser noticed
    // anything - the same silence the parser exists to break.
    // ---------------------------------------------------------------------------------------

    private const string WellformedSource = """
        =========== GB_WMIACPI_Get ===========
        Qualifiers: Description=Gigabyte WMI Get method; dynamic=True; guid={ABBC0F6F-8EA1-11d1-00A0-C90629100000}; Locale=MS\0x409; provider=WmiProv; WMI=True
        --- Properties ---
          Active : Boolean  [read=True]
          InstanceName : String  [key=True; read=True]
        --- Methods ---
          GetCPUFanDuty [Description=Get CPU Fan Duty; Implemented=True; read=True; WmiMethodId=70; write=True]
              param Data : UInt8 [Description=Data; ID=0; out=True]
          GetFanIndexValue [Description=Get Fan Index Value; Implemented=True; read=True; WmiMethodId=104; write=True]
              param Index : UInt8 [Description=Index; ID=0; in=True]
              param Value : UInt8 [Description=Speed; ID=2; out=True]
        =========== GB_WMIACPI_Set ===========
        Qualifiers: Description=Gigabyte WMI Set method; dynamic=True; guid={ABBC0F75-8EA1-11d1-00A0-C90629100000}; Locale=MS\0x409; provider=WmiProv; WMI=True
        --- Properties ---
          Active : Boolean  [read=True]
        --- Methods ---
          SetChargeStop [Description=Set Charge Stop; Implemented=True; read=True; WmiMethodId=101; write=True]
              param Data : UInt8 [Description=SetChargeStopData; ID=0; in=True]
              param DataOut : UInt8 [Description=Data; ID=1; out=True]
        =========== GB_WMIACPI_Data ===========
        Qualifiers: Description=Class to test Query/Set Gigabyte WMI ACPI; dynamic=True; guid={ABBC0F6C-8EA1-11d1-00A0-C90629100000}; Locale=MS\0x409; provider=WmiProv; WMI=True
        --- Properties ---
          Data : UInt32  [Description=Read A ULONG value; read=True; WmiDataId=1; write=True]
        --- Methods ---
        =========== GB_WMIACPI_Event ===========
        Qualifiers: Description=Class containing event generated ULong data; dynamic=True; guid={ABBC0F72-8EA1-11d1-00A0-C90629100000}; Locale=MS\0x409; provider=WmiProv; WMI=True
        --- Properties ---
          Data : UInt8Array  [Description=WMI Event Data; MAX=4; read=True; WmiDataId=1; write=True]
        --- Methods ---
        """;

    // Normalised so the mutations below can be written with "\n" whatever this source file was
    // checked out with.
    private static readonly string s_wellformed = WellformedSource.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void The_wellformed_sample_the_mutation_cases_start_from_parses()
    {
        // Without this, every case below could be passing for the wrong reason.
        var s = WmiSchemaParser.Parse(s_wellformed);

        Assert.Equal(4, s.Classes.Count);
        Assert.Equal(2, s.Class(WmiSchemaParser.GetClass).Methods.Count);
        Assert.Single(s.Class(WmiSchemaParser.SetClass).Methods);
        Assert.Empty(s.Class(WmiSchemaParser.DataClass).Methods);
    }

    /// <summary>Replaces the one occurrence of <paramref name="original"/> in the sample.</summary>
    private static string Mutate(string original, string replacement)
    {
        var mutated = s_wellformed.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(s_wellformed, mutated);
        return mutated;
    }

    [Fact]
    public void A_method_with_no_WmiMethodId_is_refused()
    {
        // The number is the whole point of the line. A method that reached the MOF without one
        // would be printed on whatever id the writer invented for it.
        var text = Mutate(
            "  GetCPUFanDuty [Description=Get CPU Fan Duty; Implemented=True; read=True; WmiMethodId=70; write=True]",
            "  GetCPUFanDuty [Description=Get CPU Fan Duty; Implemented=True; read=True; write=True]");

        var ex = Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
        Assert.Contains("WmiMethodId", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GetCPUFanDuty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_method_whose_WmiMethodId_is_not_a_number_is_refused()
    {
        var text = Mutate("WmiMethodId=70;", "WmiMethodId=seventy;");

        Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
    }

    [Fact]
    public void A_parameter_type_it_has_never_been_taught_is_refused()
    {
        // Guessing a width here is how a one-byte duty ends up written into a two-byte slot.
        var text = Mutate(
            "      param Data : UInt8 [Description=Data; ID=0; out=True]",
            "      param Data : Sint8 [Description=Data; ID=0; out=True]");

        var ex = Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
        Assert.Contains("Sint8", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_property_type_it_has_never_been_taught_is_refused()
    {
        var text = Mutate("  Data : UInt32  [", "  Data : Real32  [");

        Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
    }

    [Fact]
    public void A_parameter_with_no_direction_is_refused()
    {
        // The design says a direction that was not recorded does not get a guess.
        var text = Mutate(
            "      param Data : UInt8 [Description=Data; ID=0; out=True]",
            "      param Data : UInt8 [Description=Data; ID=0]");

        var ex = Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
        Assert.Contains("direction", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_parameter_claiming_both_directions_is_refused()
    {
        var text = Mutate(
            "      param Data : UInt8 [Description=Data; ID=0; out=True]",
            "      param Data : UInt8 [Description=Data; ID=0; in=True; out=True]");

        Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
    }

    [Fact]
    public void A_parameter_with_no_ID_is_refused()
    {
        var text = Mutate(
            "      param Data : UInt8 [Description=Data; ID=0; out=True]",
            "      param Data : UInt8 [Description=Data; out=True]");

        var ex = Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
        Assert.Contains("ID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parameter_before_any_method_is_refused()
    {
        var text = Mutate(
            "--- Methods ---\n  GetCPUFanDuty",
            "--- Methods ---\n      param Stray : UInt8 [Description=Data; ID=0; out=True]\n  GetCPUFanDuty");

        Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
    }

    [Fact]
    public void Two_methods_on_one_id_inside_a_class_are_refused()
    {
        // MofWriter would print both, and the second would take the first's number.
        var text = Mutate("WmiMethodId=104;", "WmiMethodId=70;");

        var ex = Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
        Assert.Contains("70", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_methods_of_one_name_inside_a_class_are_refused()
    {
        var text = Mutate("  GetFanIndexValue [", "  GetCPUFanDuty [");

        var ex = Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
        Assert.Contains("GetCPUFanDuty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_reused_across_the_two_classes_is_fine()
    {
        // Get and Set are separate id spaces and separate name spaces; only a collision inside
        // one class is a problem.
        var text = Mutate("  SetChargeStop [", "  GetCPUFanDuty [");

        var s = WmiSchemaParser.Parse(text);
        Assert.Equal(70, s.Class(WmiSchemaParser.GetClass).Methods.Single(m => m.Name == "GetCPUFanDuty").MethodId);
        Assert.Equal(101, s.Class(WmiSchemaParser.SetClass).Methods.Single(m => m.Name == "GetCPUFanDuty").MethodId);
    }

    [Fact]
    public void Two_parameters_on_one_data_id_inside_a_method_are_refused()
    {
        var text = Mutate(
            "      param Value : UInt8 [Description=Speed; ID=2; out=True]",
            "      param Value : UInt8 [Description=Speed; ID=0; out=True]");

        Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
    }

    [Fact]
    public void Two_parameters_of_one_name_inside_a_method_are_refused()
    {
        var text = Mutate(
            "      param Value : UInt8 [Description=Speed; ID=2; out=True]",
            "      param Index : UInt8 [Description=Speed; ID=2; out=True]");

        Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
    }

    [Fact]
    public void Two_classes_of_one_name_are_refused()
    {
        var text = Mutate("=========== GB_WMIACPI_Data ===========", "=========== GB_WMIACPI_Get ===========");

        Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
    }

    [Fact]
    public void A_class_with_no_guid_is_refused()
    {
        var text = Mutate("guid={ABBC0F72-8EA1-11d1-00A0-C90629100000}; ", string.Empty);

        var ex = Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
        Assert.Contains("guid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_line_inside_a_class_that_it_does_not_recognise_is_refused()
    {
        var text = Mutate("--- Methods ---\n  SetChargeStop", "--- Methods ---\n  and then some prose\n  SetChargeStop");

        Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
    }

    [Fact]
    public void A_class_body_that_never_declares_a_section_is_refused()
    {
        var text = Mutate("--- Properties ---\n  Data : UInt32  [", "  Data : UInt32  [");

        Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
    }

    [Fact]
    public void A_property_line_sitting_in_the_methods_section_is_refused()
    {
        var text = Mutate(
            "--- Methods ---\n  SetChargeStop",
            "--- Methods ---\n  Active : Boolean  [read=True]\n  SetChargeStop");

        Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(18)]
    [InlineData(24)]
    public void A_dump_that_stops_part_way_through_is_refused(int keepLines)
    {
        // The way a copy-paste out of a console really goes wrong. Every prefix of the sample is
        // short of four classes or short of a class's closing detail, and none of them is a
        // schema - so none of them may come back as one.
        var text = string.Join("\n", s_wellformed.Split('\n').Take(keepLines));

        Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
    }

    [Fact]
    public void A_line_cut_off_mid_qualifier_is_refused()
    {
        // Truncation inside a line rather than between two, which loses the closing bracket and
        // with it any guarantee that the qualifiers on the line are all of them.
        var text = Mutate(
            "  GetCPUFanDuty [Description=Get CPU Fan Duty; Implemented=True; read=True; WmiMethodId=70; write=True]",
            "  GetCPUFanDuty [Description=Get CPU Fan Duty; Implemented=True; read=True; WmiMethod");

        Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(text));
    }

    [Fact]
    public void A_dump_cut_between_two_classes_is_caught_by_the_class_count_alone()
    {
        // The complement of the case above, and the reason truncation has to be caught by shape
        // rather than by "did any line fail to parse": a dump cut at a class boundary is
        // well-formed all the way down, and only the missing fourth class gives it away.
        var upToThreeClasses =
            s_wellformed[..s_wellformed.IndexOf("=========== GB_WMIACPI_Event ===========", StringComparison.Ordinal)];

        var ex = Assert.Throws<FormatException>(() => WmiSchemaParser.Parse(upToThreeClasses));
        Assert.Contains("4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_lines_and_carriage_returns_do_not_change_the_reading()
    {
        // The dump gets pasted between machines and shells; a CRLF round trip must not move a
        // single number.
        var plain = Canonical(WmiSchemaParser.Parse(s_wellformed));

        Assert.Equal(plain, Canonical(WmiSchemaParser.Parse(s_wellformed.Replace("\n", "\r\n", StringComparison.Ordinal))));
        Assert.Equal(plain, Canonical(WmiSchemaParser.Parse(s_wellformed.Replace("\n", "\n\n", StringComparison.Ordinal))));
    }

    [Fact]
    public void A_comment_outside_the_qualifier_list_is_stripped_and_one_inside_it_is_not()
    {
        // Stripping from the first '#' anywhere on the line would silently truncate any
        // description that ever held one, and a description is a value the MOF prints verbatim.
        var text = Mutate(
            "  GetCPUFanDuty [Description=Get CPU Fan Duty; Implemented=True; read=True; WmiMethodId=70; write=True]",
            "  GetCPUFanDuty [Description=Get CPU Fan Duty #1; Implemented=True; read=True; WmiMethodId=70; write=True]  # recovered 2026-09-05");

        var m = WmiSchemaParser.Parse(text).Class(WmiSchemaParser.GetClass).Methods.Single(x => x.Name == "GetCPUFanDuty");

        Assert.Equal(70, m.MethodId);
        Assert.Equal("Get CPU Fan Duty #1", m.Description);
    }

    /// <summary>
    /// Every number and name a schema carries, flattened into one string.
    /// </summary>
    /// <remarks>
    /// The records hold <c>IReadOnlyList</c>s, which compare by reference, so two schemas parsed
    /// from equivalent text are never equal to each other. This compares what the MOF would
    /// actually be written from instead.
    /// </remarks>
    private static string Canonical(WmiSchema s) => string.Join("\n", s.Classes.Select(c =>
        $"{c.Name} {c.Guid} {c.Description}\n" +
        string.Join("\n", c.Properties.Select(p =>
            $"  {p.Name}:{p.Type}:{p.IsKey}{p.CanRead}{p.CanWrite}:{p.DataId}:{p.Max}:{p.Description}")) + "\n" +
        string.Join("\n", c.Methods.Select(m =>
            $"  {m.Name}#{m.MethodId}:{m.Description}(" +
            string.Join(",", m.Parameters.Select(p => $"{p.Name}:{p.Type}:{p.Direction}:{p.DataId}:{p.Description}")) + ")"))));
}

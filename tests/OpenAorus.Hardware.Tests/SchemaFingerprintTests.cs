using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// One string that changes when, and only when, a name-to-number mapping changes.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes "the gate result is remembered" safe. A recorded pass is worth something
/// only for as long as the schema it was earned against is still the schema on the machine, and
/// this is the cheap comparison that says so - no firmware method is invoked to compute it, only
/// class metadata is read.
/// </para>
/// <para>
/// It is also the only thing in the suite that catches a <em>permutation</em>. Every other check
/// counts: 143 methods, 249 parameters, four classes. Two methods that trade ids keep every count
/// intact, and the counts stay green while the generated MOF binds each name to the other's
/// firmware method. The two fingerprints below are written down as literals for exactly that
/// reason - see the note on <see cref="RecoveredBinding"/>.
/// </para>
/// </remarks>
public class SchemaFingerprintTests
{
    private static readonly WmiSchema Schema = WmiSchemaParserTests.Recovered();

    /// <summary>
    /// The binding fingerprint of the recovered schema, written down rather than computed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS NUMBER IS THE POINT OF THE FILE. Without it, the fingerprint, the generated MOF and
    /// the drift test all move together the moment the research file is edited, and the suite
    /// stays green while every name points somewhere new. A literal in test source is the one
    /// artefact no regeneration step can rewrite: changing it is a deliberate edit by a person
    /// who has to say what it is for.
    /// </para>
    /// <para>
    /// It covers, and covers only, what binds a name to the firmware: for each of the two
    /// method-bearing classes, in name order, the class name and then every method as
    /// <c>Name=WmiMethodId</c> in name order. 145 lines, 143 of them a method. So it moves for a
    /// method that vanished, a method that appeared, an id that changed, and - the case nothing
    /// else here catches - two methods that swapped.
    /// </para>
    /// <para>
    /// TO CHANGE IT you must have changed the recovered schema, which means a different machine
    /// or a corrected reading, and the diff on
    /// <c>docs/research/gb-wmiacpi-methods-aorus-17g-kd.txt</c> in the same commit must say which.
    /// Updating this constant to make a red suite go green, without that diff, is the failure the
    /// whole design exists to prevent.
    /// </para>
    /// </remarks>
    public const string RecoveredBinding = "482f5601e0bed6ba4f417c4dae11e44fe69638b81aa81ed572bc2a24d0fda93d";

    /// <summary>
    /// The signature fingerprint of the recovered schema, written down for the same reason.
    /// </summary>
    /// <remarks>
    /// Everything <see cref="RecoveredBinding"/> covers, plus every parameter's
    /// <c>WmiDataId</c>, name, CIM type and direction. A parameter that flipped from <c>in</c> to
    /// <c>out</c> leaves the binding fingerprint alone - the method is still on its id - and would
    /// print a charge-limit write that carries no value. This is what notices.
    /// </remarks>
    public const string RecoveredSignature = "c6f2a6a717f71bd6075d2dadbbdb72b46e8ce1570e4fbf0d3e70f836a9a53779";

    // -------------------------------------------------------------------------------------
    // The pins. Everything below these two is about the shape of the function; these two are
    // about the schema itself, and they are what a permutation runs into.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void The_recovered_schema_binds_exactly_the_names_to_the_numbers_written_down_here()
    {
        var actual = SchemaFingerprint.Of(Schema);

        Assert.True(actual == RecoveredBinding,
            $"The recovered schema's name-to-WmiMethodId mapping is no longer the one this suite was " +
            $"written against.\r\n  expected {RecoveredBinding}\r\n  actual   {actual}\r\n" +
            "Something in docs/research/gb-wmiacpi-methods-aorus-17g-kd.txt changed which firmware " +
            "method a name reaches. That is not necessarily a dropped method - the counts would have " +
            "caught that - it can be two methods that traded ids, which leaves every count intact. " +
            "The failures beside this one name the fan and charge writes if they are the ones that " +
            "moved; for anything else, diff the research file and the regenerated MOF. Update this " +
            "constant only once you know what moved and why.");
    }

    [Fact]
    public void The_recovered_schema_has_exactly_the_parameter_layout_written_down_here()
    {
        var actual = SchemaFingerprint.OfSignatures(Schema);

        Assert.True(actual == RecoveredSignature,
            $"The recovered schema's parameter layout is no longer the one this suite was written " +
            $"against.\r\n  expected {RecoveredSignature}\r\n  actual   {actual}\r\n" +
            "A WmiDataId, a CIM type or a parameter direction changed. A direction is the one of " +
            "those that leaves the method on its own id, so nothing else here would notice: an " +
            "in-parameter turned out prints a write that carries no value. Diff the research file.");
    }

    /// <summary>
    /// The ten writes the app actually performs, on the ids and with the buffer layouts it
    /// performs them with.
    /// </summary>
    /// <remarks>
    /// The fingerprints above cover all 143 methods but can only say "something moved". These ten
    /// are the ones that can leave a fan stuck or a charge controller mis-set, so they are pinned
    /// by name - which means a failure here says <em>which</em> method moved, in the message,
    /// without anyone diffing anything.
    /// </remarks>
    [Fact]
    public void The_ten_writes_the_app_performs_are_on_the_ids_and_layouts_written_down_here()
    {
        var expected = new[]
        {
            "SetGPUFanDuty=71(0:Data:UInt8:in,1:DataOut:UInt8:out)",
            "SetNvThermalTarget=87(0:Data:UInt8:in)",
            "SetChargePolicy=100(0:Data:UInt8:in,1:DataOut:UInt8:out)",
            "SetChargeStop=101(0:Data:UInt8:in,1:DataOut:UInt8:out)",
            "SetCurrentFanStep=102(0:Data:UInt8:in,1:DataOut:UInt8:out)",
            "SetStepFanStatus=103(0:Data:UInt8:in,1:DataOut:UInt8:out)",
            "SetFanIndexValue=104(0:Index:UInt8:in,1:Temperture:UInt8:in,2:Value:UInt8:in)",
            "SetFixedFanStatus=106(0:Data:UInt8:in,1:DataOut:UInt8:out)",
            "SetFixedFanSpeed=107(0:Data:UInt8:in,1:DataOut:UInt8:out)",
            "SetAutoFanStatus=113(0:Data:UInt8:in,1:DataOut:UInt8:out)",
        };

        var set = Schema.Class(WmiSchemaParser.SetClass);
        var wrong = new List<string>();

        foreach (var want in expected)
        {
            var name = want[..want.IndexOf('=', StringComparison.Ordinal)];
            var method = set.Methods.SingleOrDefault(m => m.Name == name);

            if (method is null) wrong.Add($"  {name} is gone; it should be {want}");
            else if (SchemaFingerprint.SignatureOf(method) is var got && got != want)
                wrong.Add($"  {name} is now {got}; it should be {want}");
        }

        Assert.True(wrong.Count == 0,
            "A write the app performs no longer reaches the firmware method it used to. These are " +
            "the calls that set fan duty, fan mode and the charge limit, so a name bound to the " +
            "wrong number here is a fan command answered by something else:\r\n" +
            string.Join("\r\n", wrong));
    }

    [Fact]
    public void A_method_the_app_never_calls_is_pinned_too()
    {
        // DecreaseBrigtness - Gigabyte's spelling, missing the "h", sitting one line above a
        // correctly spelled IncreaseBrightness - is referenced nowhere in the app, so no other
        // test would notice it moving. The fingerprints cover it by construction; this states
        // that the coverage is by construction rather than by anyone having remembered to list
        // it, and pins the two misspellings so a well-meaning correction cannot rename a method
        // WMI binds by.
        Assert.Equal("DecreaseBrigtness=204(0:Data:UInt8:in,1:DataOut:UInt8:out)",
            SchemaFingerprint.SignatureOf(
                Schema.Class(WmiSchemaParser.SetClass).Methods.Single(m => m.Name == "DecreaseBrigtness")));
        Assert.Equal("GetBirightnessOff=196(0:Data:UInt8:out)",
            SchemaFingerprint.SignatureOf(
                Schema.Class(WmiSchemaParser.GetClass).Methods.Single(m => m.Name == "GetBirightnessOff")));
    }

    [Fact]
    public void Two_methods_that_trade_ids_change_both_fingerprints()
    {
        // The permutation, reduced to an assertion. Nothing that counts can see this: the schema
        // still has 143 methods, 249 parameters and four classes afterwards.
        var swapped = Permuted(Schema, WmiSchemaParser.SetClass, "SetFixedFanStatus", "SetFixedFanSpeed");

        Assert.Equal(Schema.Classes.Sum(c => c.Methods.Count), swapped.Classes.Sum(c => c.Methods.Count));
        Assert.NotEqual(SchemaFingerprint.Of(Schema), SchemaFingerprint.Of(swapped));
        Assert.NotEqual(SchemaFingerprint.OfSignatures(Schema), SchemaFingerprint.OfSignatures(swapped));
    }

    [Fact]
    public void A_parameter_that_changed_direction_moves_the_signature_and_not_the_binding()
    {
        // Both halves matter. The binding fingerprint must not move, or it would stop being
        // comparable against live WMI metadata, which carries method ids and not directions.
        var flipped = WithFlippedDirection(Schema, WmiSchemaParser.SetClass, "SetMaxCharge");

        Assert.Equal(SchemaFingerprint.Of(Schema), SchemaFingerprint.Of(flipped));
        Assert.NotEqual(SchemaFingerprint.OfSignatures(Schema), SchemaFingerprint.OfSignatures(flipped));
    }

    [Fact]
    public void A_parameter_that_changed_width_moves_the_signature()
    {
        // GetChargeStop answers UInt16 while SetChargeStop takes UInt8. A width that drifted
        // would have the firmware fill a slot from the wrong number of bytes.
        var narrowed = new WmiSchema(Schema.Classes
            .Select(c => c with
            {
                Methods = c.Methods.Select(m => m.Name != "GetChargeStop" ? m : m with
                {
                    Parameters = m.Parameters.Select(p => p with { Type = WmiParamType.UInt8 }).ToList(),
                }).ToList(),
            })
            .ToList());

        Assert.NotEqual(SchemaFingerprint.OfSignatures(Schema), SchemaFingerprint.OfSignatures(narrowed));
    }

    [Fact]
    public void The_order_the_dump_happened_to_list_parameters_in_does_not_change_the_signature()
    {
        // The dump lists in-parameters then out, alphabetical within each group, so its order is
        // not WmiDataId order - GetDeepFan lists slot 5 before slot 0, and nine methods differ.
        // A fingerprint that depended on that would go off for a reason that binds nothing.
        var reversed = new WmiSchema(Schema.Classes
            .Select(c => c with
            {
                Methods = c.Methods.Select(m => m with { Parameters = m.Parameters.Reverse().ToList() }).ToList(),
            })
            .ToList());

        Assert.Equal(SchemaFingerprint.OfSignatures(Schema), SchemaFingerprint.OfSignatures(reversed));
    }

    [Fact]
    public void The_order_the_dump_happened_to_list_methods_in_does_not_change_either_fingerprint()
    {
        var reversed = new WmiSchema(Schema.Classes
            .Select(c => c with { Methods = c.Methods.Reverse().ToList() })
            .Reverse()
            .ToList());

        Assert.Equal(SchemaFingerprint.Of(Schema), SchemaFingerprint.Of(reversed));
        Assert.Equal(SchemaFingerprint.OfSignatures(Schema), SchemaFingerprint.OfSignatures(reversed));
    }

    // -------------------------------------------------------------------------------------
    // The shape of the function.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void The_same_schema_always_fingerprints_the_same()
    {
        Assert.Equal(SchemaFingerprint.Of(Schema), SchemaFingerprint.Of(WmiSchemaParserTests.Recovered()));
        Assert.Equal(SchemaFingerprint.OfSignatures(Schema), SchemaFingerprint.OfSignatures(WmiSchemaParserTests.Recovered()));
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
    public void A_class_that_is_there_but_declares_nothing_is_not_the_same_as_one_that_is_absent()
    {
        // A repository that half-rebuilt can leave a class present with no methods on it. That is
        // a distinct state from the class being gone, and reading them as one would have the app
        // report the wrong thing about a machine it is refusing to write to.
        var present = LiveFrom(Schema);
        present["GB_WMIACPI_Set"] = new Dictionary<string, int>(StringComparer.Ordinal);

        var absent = LiveFrom(Schema);
        absent.Remove("GB_WMIACPI_Set");

        Assert.NotEqual(SchemaFingerprint.OfLive(present), SchemaFingerprint.OfLive(absent));
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
                c.Methods.Select(m => m with
                {
                    Description = "also different",
                    Parameters = m.Parameters.Select(p => p with { Description = "and different" }).ToList(),
                }).ToList()))
            .ToList());

        Assert.Equal(SchemaFingerprint.Of(Schema), SchemaFingerprint.Of(altered));
        Assert.Equal(SchemaFingerprint.OfSignatures(Schema), SchemaFingerprint.OfSignatures(altered));
    }

    [Fact]
    public void The_classes_that_declare_no_methods_contribute_nothing()
    {
        // Get and Set carry every id there is. Keeping Data and Event out is what makes the
        // fingerprint exactly "the 143 numbers", which is exactly the thing that can be wrong -
        // and it is what lets OfLive compute the same string from method metadata alone.
        var methodsOnly = new WmiSchema(Schema.Classes.Where(c => c.Methods.Count > 0).ToList());

        Assert.Equal(SchemaFingerprint.Of(Schema), SchemaFingerprint.Of(methodsOnly));
        Assert.Equal(SchemaFingerprint.OfSignatures(Schema), SchemaFingerprint.OfSignatures(methodsOnly));
    }

    [Fact]
    public void The_guid_is_not_part_of_it()
    {
        // Of and OfLive have to be the same rendering over the same shape, and the cheap live
        // read is method metadata, not class qualifiers. A wrong guid cannot hide anyway: WMI
        // will not hold two classes on one guid, so a class under our name is bound to the block
        // our MOF named. WmiSchemaParserTests pins all four guids by hand.
        var moved = new WmiSchema(Schema.Classes
            .Select(c => c with { Guid = "{00000000-0000-0000-0000-000000000000}" })
            .ToList());

        Assert.Equal(SchemaFingerprint.Of(Schema), SchemaFingerprint.Of(moved));
    }

    [Fact]
    public void Both_fingerprints_are_short_enough_to_sit_in_a_settings_file_and_be_read_by_eye()
    {
        foreach (var fp in new[] { SchemaFingerprint.Of(Schema), SchemaFingerprint.OfSignatures(Schema), SchemaFingerprint.OfLive(LiveFrom(Schema)) })
        {
            Assert.Equal(64, fp.Length);
            Assert.All(fp, c => Assert.True(char.IsAsciiHexDigitLower(c)));
        }
    }

    [Fact]
    public void The_two_fingerprints_are_not_each_other()
    {
        // Stated because they are both 64 hex characters over the same schema, and a settings
        // file that stored one where the other belonged would compare cleanly forever.
        Assert.NotEqual(SchemaFingerprint.Of(Schema), SchemaFingerprint.OfSignatures(Schema));
    }

    [Fact]
    public void Nulls_are_programming_errors()
    {
        Assert.Throws<ArgumentNullException>(() => SchemaFingerprint.Of(null!));
        Assert.Throws<ArgumentNullException>(() => SchemaFingerprint.OfLive(null!));
        Assert.Throws<ArgumentNullException>(() => SchemaFingerprint.OfSignatures(null!));
        Assert.Throws<ArgumentNullException>(() => SchemaFingerprint.SignatureOf(null!));
    }

    private static IReadOnlyDictionary<string, int> With(IReadOnlyDictionary<string, int> src, string name, int id)
    {
        var copy = new Dictionary<string, int>(src, StringComparer.Ordinal) { [name] = id };
        return copy;
    }

    /// <summary>The schema with two methods of one class holding each other's ids.</summary>
    private static WmiSchema Permuted(WmiSchema schema, string className, string first, string second)
    {
        var target = schema.Class(className);
        var a = target.Methods.Single(m => m.Name == first);
        var b = target.Methods.Single(m => m.Name == second);

        return new WmiSchema(schema.Classes
            .Select(c => c.Name != className ? c : c with
            {
                Methods = c.Methods.Select(m =>
                    m.Name == first ? m with { MethodId = b.MethodId } :
                    m.Name == second ? m with { MethodId = a.MethodId } : m).ToList(),
            })
            .ToList());
    }

    private static WmiSchema WithFlippedDirection(WmiSchema schema, string className, string methodName) =>
        new(schema.Classes
            .Select(c => c.Name != className ? c : c with
            {
                Methods = c.Methods.Select(m => m.Name != methodName ? m : m with
                {
                    Parameters = m.Parameters
                        .Select(p => p with
                        {
                            Direction = p.Direction == WmiParamDirection.In
                                ? WmiParamDirection.Out
                                : WmiParamDirection.In,
                        })
                        .ToList(),
                }).ToList(),
            })
            .ToList());

    internal static Dictionary<string, IReadOnlyDictionary<string, int>> LiveFrom(WmiSchema schema) =>
        schema.Classes
            .Where(c => c.Methods.Count > 0)
            .ToDictionary(
                c => c.Name,
                c => (IReadOnlyDictionary<string, int>)c.Methods
                    .ToDictionary(m => m.Name, m => m.MethodId, StringComparer.Ordinal),
                StringComparer.Ordinal);
}

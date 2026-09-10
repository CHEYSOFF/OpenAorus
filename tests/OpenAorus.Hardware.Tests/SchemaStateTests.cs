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
    public void No_two_states_explain_themselves_the_same_way()
    {
        // Five states the owner is told apart by, so five texts. Two that read alike would make
        // "someone else registered this" and "we registered this" the same screen.
        var texts = Enum.GetValues<SchemaStatus>().Select(SchemaState.Explain).ToArray();

        Assert.Equal(texts.Length, texts.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_class_list_names_the_four_recovered_classes_and_our_marker()
    {
        Assert.Equal(
            new[] { "GB_WMIACPI_Data", "GB_WMIACPI_Event", "GB_WMIACPI_Get", "GB_WMIACPI_Set", MofWriter.MarkerClass },
            SchemaClasses.All.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void The_classes_carrying_methods_are_the_ones_the_fingerprint_is_computed_over()
    {
        // SchemaFingerprint.Of renders only the classes that declare methods, so the live half of
        // the comparison has to be gathered from exactly those two and no others.
        Assert.Equal(
            new[] { WmiSchemaParser.GetClass, WmiSchemaParser.SetClass },
            SchemaClasses.MethodBearing.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(SchemaClasses.MethodBearing, n => Assert.Contains(n, SchemaClasses.All));
    }

    [Fact]
    public void A_registration_we_cannot_fingerprint_is_not_ours()
    {
        // All four names on the machine and our marker beside them, but nothing readable to
        // compare. "Probably ours" is not a state; the fingerprint either matches or it does not.
        var s = SchemaState.Classify(Snapshot(classes: true, marker: true, fingerprint: null), Expected, gatesRecorded: true);

        Assert.Equal(SchemaStatus.Foreign, s);
    }

    [Fact]
    public void The_fingerprint_is_compared_exactly()
    {
        // Lowercase hex on both sides, and a comparison that ignored case would let a settings
        // file written by some other tool match a schema it had never seen.
        var s = SchemaState.Classify(Snapshot(classes: true, marker: true, fingerprint: "AAAA"), Expected, gatesRecorded: true);

        Assert.Equal(SchemaStatus.Foreign, s);
    }

    [Fact]
    public void A_snapshot_says_whether_all_or_any_of_the_four_classes_are_there()
    {
        Assert.True(Snapshot(classes: true, marker: false, fingerprint: null).AllClassesPresent);
        Assert.True(Snapshot(classes: true, marker: false, fingerprint: null).AnyClassPresent);
        Assert.False(Snapshot(classes: false, marker: true, fingerprint: null).AllClassesPresent);
        Assert.False(Snapshot(classes: false, marker: true, fingerprint: null).AnyClassPresent);

        var one = new SchemaSnapshot(false, false, false, true, MarkerPresent: false, LiveFingerprint: null);
        Assert.False(one.AllClassesPresent);
        Assert.True(one.AnyClassPresent);
    }

    [Fact]
    public void Nulls_are_programming_errors()
    {
        Assert.Throws<ArgumentNullException>(() => SchemaState.Classify(null!, Expected, false));
        Assert.Throws<ArgumentNullException>(() => SchemaState.Classify(Snapshot(false, false, null), null!, false));
    }
}

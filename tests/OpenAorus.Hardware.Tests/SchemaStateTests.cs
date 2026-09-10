using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Seven states, and the two things that must never happen in any of them: registering over a
/// working schema, and removing one that is not ours. Neither of which is the same question as
/// whether a write may go through - which is what the gates, and only the gates, answer.
/// </summary>
public class SchemaStateTests
{
    private const string Expected = "aaaa";
    private const string Different = "bbbb";

    /// <summary>A machine whose marker records the same schema its classes are binding.</summary>
    private static SchemaSnapshot Snapshot(bool classes, bool marker, string? fingerprint) =>
        Snapshot(classes, marker, fingerprint, markerRecords: fingerprint);

    /// <summary>A machine where what the marker wrote down and what the classes bind can differ.</summary>
    private static SchemaSnapshot Snapshot(bool classes, bool marker, string? fingerprint, string? markerRecords) =>
        new(classes, classes, classes, classes, marker, markerRecords, fingerprint);

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
        var s = SchemaState.Classify(Snapshot(classes: true, marker: true, fingerprint: Different), Expected, gatesRecorded: false);

        Assert.Equal(SchemaStatus.Foreign, s);
        Assert.False(SchemaState.WritesUnlocked(s));
        Assert.False(SchemaState.CanInstall(s));
        Assert.False(SchemaState.CanRemove(s));
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
        // The whole reason the record is safe to keep rather than re-earn every launch. Asked the
        // way the app asks it - the record against the fingerprint the classes bind now - rather
        // than by handing Classify a "yes" the record would never have given, which is what this
        // used to do and what let it pass while proving nothing.
        var record = new SchemaRecord
        {
            Registered = true, GatesPassed = true, Fingerprint = Expected,
            When = DateTime.Now.AddMinutes(-1),
        };
        var drifted = Snapshot(classes: true, marker: true, fingerprint: Different);

        var s = SchemaState.Classify(drifted, Expected, record.ProvenFor(drifted.LiveFingerprint));

        Assert.False(SchemaState.WritesUnlocked(s));
        Assert.Equal(SchemaStatus.Foreign, s);
    }

    [Fact]
    public void A_pass_that_still_matches_the_schema_underneath_survives_the_registration_not_being_ours()
    {
        // The same record and the same question on a Control Center machine. Nothing about it is
        // ours, and everything the gates checked is still exactly where they checked it.
        var record = new SchemaRecord
        {
            GatesPassed = true, Fingerprint = Different, When = DateTime.Now.AddMinutes(-1),
        };
        var theirs = Snapshot(classes: true, marker: false, fingerprint: Different);

        var s = SchemaState.Classify(theirs, Expected, record.ProvenFor(theirs.LiveFingerprint));

        Assert.Equal(SchemaStatus.ForeignGated, s);
        Assert.True(SchemaState.WritesUnlocked(s));
        Assert.False(SchemaState.CanInstall(s));
        Assert.False(SchemaState.CanRemove(s));
    }

    [Fact]
    public void Control_center_that_has_passed_both_gates_unlocks_writes_and_is_still_left_alone()
    {
        // The machine this app is normally installed onto, and the one the design error made
        // read-only. Gate A reads the firmware and checks the answers against the known-good
        // reading, Gate B resolves the Set class, and neither asks whose registration it is.
        // Refusing writes here was worse than the behaviour it replaced, which wrote to
        // Gigabyte's schema with no verification at all - and there was no way back, because
        // CanInstall is rightly false too.
        var s = SchemaState.Classify(
            Snapshot(classes: true, marker: false, fingerprint: Expected), Expected, gatesRecorded: true);

        Assert.Equal(SchemaStatus.ForeignGated, s);
        Assert.True(SchemaState.WritesUnlocked(s));
        Assert.False(SchemaState.CanInstall(s));
        Assert.False(SchemaState.CanRemove(s));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    public void Some_classes_but_not_all_reads_as_partial(bool get, bool set, bool data, bool evt)
    {
        var s = SchemaState.Classify(new SchemaSnapshot(get, set, data, evt, MarkerPresent: false, MarkerFingerprint: null, LiveFingerprint: null), Expected, false);

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

    // ---- The registration of ours that a repository rebuild emptied ---------------------------

    /// <summary>
    /// The live fingerprint a machine renders when every method-bearing class is present and
    /// declares nothing.
    /// </summary>
    /// <remarks>Computed here from the seam's own rendering rather than read off SchemaState, so
    /// that the two agreeing is a fact and not a shared constant.</remarks>
    private static string Emptied() => SchemaFingerprint.OfLive(
        SchemaClasses.MethodBearing.ToDictionary(
            name => name,
            _ => (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(StringComparer.Ordinal),
            StringComparer.Ordinal));

    [Fact]
    public void A_rebuilt_repository_that_emptied_our_own_registration_offers_remove_and_nothing_else()
    {
        // The owner's way out. All four class names still resolve, our marker beside them records
        // the schema our install file writes, and the classes declare nothing - which is our own
        // registration after a WMI repository rebuild and is not anything else. Refusing Remove
        // here left an elevated mofcomp prompt as the only escape from a mess the app made.
        var s = SchemaState.Classify(
            Snapshot(classes: true, marker: true, fingerprint: Emptied(), markerRecords: Expected),
            Expected, gatesRecorded: true);

        Assert.Equal(SchemaStatus.OursEmptied, s);
        Assert.True(SchemaState.CanRemove(s));

        // Never install: the names are taken, the classes bind nothing, and undoing is the whole
        // of the extra permission this state carries.
        Assert.False(SchemaState.CanInstall(s));

        // And a recorded gate pass counts for nothing against classes that declare no methods.
        Assert.False(SchemaState.WritesUnlocked(s));
    }

    [Fact]
    public void A_control_center_machine_is_never_read_as_a_registration_of_ours_that_emptied()
    {
        // Two independent facts have to hold, and a machine carrying somebody else's working
        // schema breaks both. Neither a marker standing beside classes that declare methods nor
        // emptied classes with no marker of ours is enough on its own.
        var declaresMethods = SchemaState.Classify(
            Snapshot(classes: true, marker: true, fingerprint: Different, markerRecords: Expected),
            Expected, gatesRecorded: false);

        var noMarkerOfOurs = SchemaState.Classify(
            Snapshot(classes: true, marker: false, fingerprint: Emptied(), markerRecords: null),
            Expected, gatesRecorded: false);

        Assert.Equal(SchemaStatus.Foreign, declaresMethods);
        Assert.Equal(SchemaStatus.Foreign, noMarkerOfOurs);
        Assert.False(SchemaState.CanRemove(declaresMethods));
        Assert.False(SchemaState.CanRemove(noMarkerOfOurs));
    }

    [Fact]
    public void Emptied_classes_under_a_marker_recording_another_schema_stay_foreign()
    {
        // An older version of us, or a hand-compiled MOF, registered what is there. We have no
        // account of what those names reached, so we do not delete them by Gigabyte's names.
        var s = SchemaState.Classify(
            Snapshot(classes: true, marker: true, fingerprint: Emptied(), markerRecords: Different),
            Expected, gatesRecorded: false);

        Assert.Equal(SchemaStatus.Foreign, s);
        Assert.False(SchemaState.CanRemove(s));
    }

    [Fact]
    public void Emptied_classes_under_a_marker_that_would_not_answer_stay_foreign()
    {
        // A marker that recorded nothing readable is not evidence. Null matches no fingerprint,
        // which is what makes an unreadable marker withhold the permission rather than grant it.
        var s = SchemaState.Classify(
            Snapshot(classes: true, marker: true, fingerprint: Emptied(), markerRecords: null),
            Expected, gatesRecorded: false);

        Assert.Equal(SchemaStatus.Foreign, s);
        Assert.False(SchemaState.CanRemove(s));
    }

    [Fact]
    public void Writes_are_unlocked_only_where_both_gates_have_been_passed()
    {
        // Two states, and they are the two gated ones. Whose registration it is does not appear
        // in this answer, and nothing that has not been proved appears in it either.
        var unlocked = Enum.GetValues<SchemaStatus>().Where(SchemaState.WritesUnlocked).ToArray();

        Assert.Equal(new[] { SchemaStatus.ForeignGated, SchemaStatus.OursGated }, unlocked.Order().ToArray());
    }

    [Fact]
    public void Nothing_that_writes_over_somebody_elses_registration_may_touch_it()
    {
        // The half of the model the gates must never reach. Passing them buys writes and buys
        // nothing else, so both foreign states answer install and remove exactly alike.
        foreach (var s in new[] { SchemaStatus.Foreign, SchemaStatus.ForeignGated })
        {
            Assert.False(SchemaState.CanInstall(s));
            Assert.False(SchemaState.CanRemove(s));
        }
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
        // Seven states the owner is told apart by, so seven texts. Two that read alike would
        // make "someone else registered this" and "we registered this" the same screen - or,
        // worse, make a machine that writes look like one that does not.
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
        // file written by some other tool match a schema it had never seen. It reads as somebody
        // else's - proved, because the gates were run against what is there - and never as ours
        // to install over or remove.
        var s = SchemaState.Classify(Snapshot(classes: true, marker: true, fingerprint: "AAAA"), Expected, gatesRecorded: true);

        Assert.Equal(SchemaStatus.ForeignGated, s);
        Assert.False(SchemaState.CanInstall(s));
        Assert.False(SchemaState.CanRemove(s));
    }

    [Fact]
    public void A_snapshot_says_whether_all_or_any_of_the_four_classes_are_there()
    {
        Assert.True(Snapshot(classes: true, marker: false, fingerprint: null).AllClassesPresent);
        Assert.True(Snapshot(classes: true, marker: false, fingerprint: null).AnyClassPresent);
        Assert.False(Snapshot(classes: false, marker: true, fingerprint: null).AllClassesPresent);
        Assert.False(Snapshot(classes: false, marker: true, fingerprint: null).AnyClassPresent);

        var one = new SchemaSnapshot(false, false, false, true, MarkerPresent: false, MarkerFingerprint: null, LiveFingerprint: null);
        Assert.False(one.AllClassesPresent);
        Assert.True(one.AnyClassPresent);
    }

    [Fact]
    public void A_state_nobody_has_answered_for_is_rejected_at_all_four_sites()
    {
        // The reason adding ForeignGated could not quietly go wrong. A default arm at any of these
        // would have given a new state the locked, unregisterable, unremovable "no" at three sites
        // and a blank explanation at the fourth - which is exactly the shape of the regression
        // ForeignGated exists to undo.
        var undefined = (SchemaStatus)99;

        Assert.Throws<ArgumentOutOfRangeException>(() => SchemaState.CanInstall(undefined));
        Assert.Throws<ArgumentOutOfRangeException>(() => SchemaState.CanRemove(undefined));
        Assert.Throws<ArgumentOutOfRangeException>(() => SchemaState.WritesUnlocked(undefined));
        Assert.Throws<ArgumentOutOfRangeException>(() => SchemaState.Explain(undefined));
    }

    [Fact]
    public void Every_defined_state_is_answered_for_at_all_four_sites()
    {
        // The other half: nothing in the enum throws. Read together with the test above, the four
        // switches are exhaustive over exactly the states that exist.
        foreach (var s in Enum.GetValues<SchemaStatus>())
        {
            SchemaState.CanInstall(s);
            SchemaState.CanRemove(s);
            SchemaState.WritesUnlocked(s);
            SchemaState.Explain(s);
        }
    }

    [Fact]
    public void Nulls_are_programming_errors()
    {
        Assert.Throws<ArgumentNullException>(() => SchemaState.Classify(null!, Expected, false));
        Assert.Throws<ArgumentNullException>(() => SchemaState.Classify(Snapshot(false, false, null), null!, false));
    }
}

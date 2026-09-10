using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// Asking the machine what is on it, without touching the firmware and without throwing.
/// </summary>
/// <remarks>
/// <para>
/// The three answers that must never be confused: nothing is registered, something is registered,
/// and we could not tell. The first two decide whether a button is offered; the third means no
/// button is offered at all, and reporting it as the first would have the app run
/// <c>mofcomp</c> against a repository it never managed to read.
/// </para>
/// <para>
/// Every test here goes through <see cref="IWmiClassSource"/>, which is the whole of the OS-facing
/// surface. That seam reads class definitions and one instance - our own marker, a plain data
/// class with no provider behind it. It has no way to enumerate an instance of a
/// <c>GB_WMIACPI_*</c> class, which is the operation that would reach the firmware.
/// </para>
/// </remarks>
public class SchemaProbeTests
{
    private static readonly WmiSchema Schema = WmiSchemaParserTests.Recovered();
    private static readonly string Expected = SchemaFingerprint.Of(Schema);

    [Fact]
    public void A_machine_with_no_gigabyte_software_reads_as_absent()
    {
        // The owner's machine as of today: Get-CimClass answers nothing for all three classes,
        // no Control Center, no acpimof.dll.
        var report = SchemaProbe.Read(FakeWmiClassSource.Nothing(), Expected, gatesRecorded: false);

        Assert.Equal(SchemaStatus.Absent, report.Status);
        Assert.Null(report.Failure);
        Assert.NotNull(report.Snapshot);
        Assert.False(report.Snapshot!.AnyClassPresent);
        Assert.False(report.Snapshot.MarkerPresent);
        Assert.Null(report.Snapshot.LiveFingerprint);
        Assert.True(report.CanInstall);
        Assert.False(report.CanRemove);
        Assert.False(report.WritesUnlocked);
    }

    [Fact]
    public void Every_class_the_feature_touches_is_asked_about_exactly_once()
    {
        var source = FakeWmiClassSource.Nothing();

        SchemaProbe.Read(source, Expected, gatesRecorded: false);

        Assert.Equal(SchemaClasses.All.OrderBy(n => n, StringComparer.Ordinal),
            source.Asked.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Our_own_registration_reads_back_as_ours()
    {
        var source = FakeWmiClassSource.Registered(Schema, marker: true);

        Assert.Equal(SchemaStatus.Ours, SchemaProbe.Read(source, Expected, gatesRecorded: false).Status);
        Assert.Equal(SchemaStatus.OursGated, SchemaProbe.Read(source, Expected, gatesRecorded: true).Status);
    }

    [Fact]
    public void A_machine_carrying_the_schema_the_install_file_declares_matches_the_fingerprint_it_records()
    {
        // The two halves that have to agree: the string written onto the marker instance by the
        // checked-in MOF, and the string recomputed from the classes that MOF would create.
        var report = SchemaProbe.Read(
            FakeWmiClassSource.Registered(Schema, marker: true), SchemaMof.Fingerprint, gatesRecorded: true);

        Assert.Equal(SchemaStatus.OursGated, report.Status);
        Assert.Equal(SchemaMof.Fingerprint, report.Snapshot!.LiveFingerprint);
    }

    [Fact]
    public void Control_center_reads_as_foreign_and_neither_button_is_offered()
    {
        // Same classes, same ids - it is where ours was recovered from - but no marker.
        var report = SchemaProbe.Read(
            FakeWmiClassSource.Registered(Schema, marker: false), Expected, gatesRecorded: false);

        Assert.Equal(SchemaStatus.Foreign, report.Status);
        Assert.False(report.CanInstall);
        Assert.False(report.CanRemove);
        Assert.False(report.WritesUnlocked);
    }

    [Fact]
    public void A_method_bound_to_a_different_number_reads_as_foreign()
    {
        // Every name in place, one of them reaching a different firmware method. This is the
        // failure the whole design exists to catch, and no count can see it.
        var live = SchemaFingerprintTests.LiveFrom(Schema);
        var get = new Dictionary<string, int>(live[WmiSchemaParser.GetClass], StringComparer.Ordinal)
        {
            ["GetCPUFanDuty"] = 71,
        };

        var source = FakeWmiClassSource.Registered(Schema, marker: true)
            .Declares(WmiSchemaParser.GetClass, get);

        // Whether the gates have been passed against it is a different question with a different
        // answer, and neither answer makes this registration ours: both come back refusing
        // install and refusing remove.
        Assert.Equal(SchemaStatus.Foreign, SchemaProbe.Read(source, Expected, gatesRecorded: false).Status);
        Assert.Equal(SchemaStatus.ForeignGated, SchemaProbe.Read(source, Expected, gatesRecorded: true).Status);
        foreach (var recorded in new[] { false, true })
        {
            var report = SchemaProbe.Read(source, Expected, recorded);
            Assert.False(report.CanInstall);
            Assert.False(report.CanRemove);
        }
    }

    [Fact]
    public void A_schema_that_declares_more_than_ours_reads_as_foreign()
    {
        // A superset is not "still ours": whoever registered the extra method registered all of
        // them, and the gates were never earned against the rest.
        var live = SchemaFingerprintTests.LiveFrom(Schema);
        var set = new Dictionary<string, int>(live[WmiSchemaParser.SetClass], StringComparer.Ordinal)
        {
            ["SetSomethingNobodyRecovered"] = 240,
        };

        var source = FakeWmiClassSource.Registered(Schema, marker: true)
            .Declares(WmiSchemaParser.SetClass, set);

        var report = SchemaProbe.Read(source, Expected, gatesRecorded: false);

        Assert.Equal(SchemaStatus.Foreign, report.Status);
        Assert.False(report.CanInstall);
        Assert.False(report.CanRemove);
    }

    [Fact]
    public void A_class_that_is_there_but_declares_nothing_is_not_read_as_one_that_is_gone()
    {
        // What a half-rebuilt repository leaves behind. The class name resolves, so the machine is
        // not one we may register over; the mapping is not there, so it is not one we may write
        // through either. The two must fingerprint differently or the empty class would vanish
        // from the comparison and a machine with no methods on it would read as fully ours.
        // No pass is recorded here, and none could be: Gate A reads every method off these
        // classes, and a class declaring none of them fails it.
        var empty = SchemaProbe.Read(
            FakeWmiClassSource.Registered(Schema, marker: true).Declares(WmiSchemaParser.GetClass),
            Expected, gatesRecorded: false);

        var gone = SchemaProbe.Read(
            FakeWmiClassSource.Registered(Schema, marker: true).Lacks(WmiSchemaParser.GetClass),
            Expected, gatesRecorded: false);

        Assert.True(empty.Snapshot!.GetPresent);
        Assert.False(gone.Snapshot!.GetPresent);
        Assert.NotEqual(empty.Snapshot.LiveFingerprint, gone.Snapshot.LiveFingerprint);
        Assert.NotEqual(Expected, empty.Snapshot.LiveFingerprint);

        Assert.Equal(SchemaStatus.Foreign, empty.Status);
        Assert.Equal(SchemaStatus.Partial, gone.Status);
        Assert.False(empty.WritesUnlocked);
    }

    // ---- The registration of ours that a repository rebuild emptied ---------------------------

    /// <summary>Our own registration with every method gone off both method-bearing classes.</summary>
    private static FakeWmiClassSource RebuiltOverOurRegistration() =>
        FakeWmiClassSource.Registered(Schema, marker: true)
            .Declares(WmiSchemaParser.GetClass)
            .Declares(WmiSchemaParser.SetClass);

    [Fact]
    public void A_registration_of_ours_that_a_rebuild_emptied_offers_remove_and_only_remove()
    {
        // The button the owner needs, and the reason this state exists. Read as Foreign, the app
        // offers nothing at all and the only way out is running the removal MOF by hand from an
        // elevated prompt.
        var report = SchemaProbe.Read(
            RebuiltOverOurRegistration().Records(Expected), Expected, gatesRecorded: true);

        Assert.Equal(SchemaStatus.OursEmptied, report.Status);
        Assert.True(report.CanRemove);
        Assert.False(report.CanInstall);
        Assert.False(report.WritesUnlocked);
        Assert.Equal(Expected, report.Snapshot!.MarkerFingerprint);
        Assert.NotEqual(Expected, report.Snapshot.LiveFingerprint);
    }

    [Fact]
    public void Emptied_classes_whose_marker_records_nothing_offer_no_button_at_all()
    {
        // The marker class being there is not the evidence. What it recorded is.
        var report = SchemaProbe.Read(RebuiltOverOurRegistration(), Expected, gatesRecorded: false);

        Assert.Equal(SchemaStatus.Foreign, report.Status);
        Assert.False(report.CanRemove);
        Assert.False(report.CanInstall);
    }

    [Fact]
    public void A_marker_that_will_not_answer_withholds_the_permission_rather_than_granting_it()
    {
        var source = RebuiltOverOurRegistration().Records(Expected);
        source.MarkerFailure = new InvalidOperationException("the repository is being rebuilt");

        var report = SchemaProbe.Read(source, Expected, gatesRecorded: false);

        // Not an unreadable machine - the five class readings all answered. Just a marker that
        // said nothing, which matches no fingerprint and so can only ever refuse.
        Assert.Equal(SchemaStatus.Foreign, report.Status);
        Assert.Null(report.Failure);
        Assert.False(report.CanRemove);
    }

    [Fact]
    public void Classes_that_still_declare_methods_are_never_read_as_a_registration_of_ours_that_emptied()
    {
        // The marker records our schema, but the classes are not empty - one method sits on a
        // different firmware number. A machine whose classes declare methods is a machine whose
        // names reach something, and this app has no account of what. Both buttons stay withheld.
        var live = SchemaFingerprintTests.LiveFrom(Schema);
        var get = new Dictionary<string, int>(live[WmiSchemaParser.GetClass], StringComparer.Ordinal)
        {
            ["GetCPUFanDuty"] = 71,
        };

        var source = FakeWmiClassSource.Registered(Schema, marker: true)
            .Declares(WmiSchemaParser.GetClass, get)
            .Records(Expected);

        var report = SchemaProbe.Read(source, Expected, gatesRecorded: false);

        Assert.Equal(SchemaStatus.Foreign, report.Status);
        Assert.False(report.CanRemove);
        Assert.False(report.CanInstall);
    }

    [Fact]
    public void Half_a_registration_reads_as_partial()
    {
        var report = SchemaProbe.Read(
            FakeWmiClassSource.Registered(Schema, marker: true).Lacks(WmiSchemaParser.EventClass),
            Expected, gatesRecorded: true);

        Assert.Equal(SchemaStatus.Partial, report.Status);
        Assert.True(report.CanRemove);
        Assert.True(report.CanInstall);
        Assert.False(report.WritesUnlocked);
    }

    [Fact]
    public void A_marker_left_behind_by_a_failed_removal_reads_as_partial()
    {
        var source = FakeWmiClassSource.Nothing().Declares(MofWriter.MarkerClass);

        var report = SchemaProbe.Read(source, Expected, gatesRecorded: false);

        Assert.Equal(SchemaStatus.Partial, report.Status);
        Assert.True(report.Snapshot!.MarkerPresent);
        Assert.True(report.CanRemove);
    }

    // -------------------------------------------------------------------------------------
    // "We could not tell" is its own answer, and it is not "nothing is registered".
    // -------------------------------------------------------------------------------------

    [Fact]
    public void A_namespace_that_will_not_open_is_not_a_machine_with_nothing_on_it()
    {
        var report = SchemaProbe.Read(
            FakeWmiClassSource.NamespaceUnavailable("Invalid namespace"), Expected, gatesRecorded: true);

        Assert.Null(report.Status);
        Assert.Null(report.Snapshot);
        Assert.False(report.Known);
        Assert.Contains("Invalid namespace", report.Failure);
        // Nothing is offered and nothing is unlocked while the answer is unknown.
        Assert.False(report.CanInstall);
        Assert.False(report.CanRemove);
        Assert.False(report.WritesUnlocked);
    }

    [Fact]
    public void One_class_that_cannot_be_read_stops_the_whole_reading()
    {
        // A repository that answers for four names and errors on the fifth has not told us what
        // is on the machine. Guessing at the fifth is how a registration ends up on top of one
        // that was already there.
        var source = FakeWmiClassSource.Registered(Schema, marker: true)
            .CannotRead(WmiSchemaParser.SetClass, "WMI provider load failure");

        var report = SchemaProbe.Read(source, Expected, gatesRecorded: true);

        Assert.Null(report.Status);
        Assert.Contains("WMI provider load failure", report.Failure);
    }

    [Fact]
    public void An_unreadable_machine_explains_itself_and_names_what_windows_said()
    {
        var report = SchemaProbe.Read(
            FakeWmiClassSource.NamespaceUnavailable("Invalid namespace"), Expected, gatesRecorded: false);

        Assert.False(string.IsNullOrWhiteSpace(report.Explanation));
        Assert.Contains("Invalid namespace", report.Explanation);
        Assert.NotEqual(SchemaState.Explain(SchemaStatus.Absent), report.Explanation);
    }

    [Fact]
    public void A_known_machine_explains_itself_in_the_words_of_its_state()
    {
        var report = SchemaProbe.Read(FakeWmiClassSource.Nothing(), Expected, gatesRecorded: false);

        Assert.Equal(SchemaState.Explain(SchemaStatus.Absent), report.Explanation);
    }

    [Fact]
    public void A_source_that_throws_does_not_take_the_startup_with_it()
    {
        // This runs before the window is shown, to decide what the window says.
        var report = SchemaProbe.Read(
            FakeWmiClassSource.Throwing(new InvalidOperationException("the WMI service is not running")),
            Expected, gatesRecorded: true);

        Assert.Null(report.Status);
        Assert.Contains("the WMI service is not running", report.Failure);
        Assert.False(report.WritesUnlocked);
    }

    [Fact]
    public void Nulls_are_programming_errors()
    {
        Assert.Throws<ArgumentNullException>(() => SchemaProbe.Read(null!, Expected, false));
        Assert.Throws<ArgumentNullException>(() => SchemaProbe.Read(FakeWmiClassSource.Nothing(), null!, false));
    }

    // -------------------------------------------------------------------------------------
    // The three answers one class read can carry.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void A_class_reading_is_present_absent_or_unreadable_and_never_two_of_them()
    {
        var present = WmiClassReading.Present(new Dictionary<string, int>(StringComparer.Ordinal) { ["A"] = 1 });
        var absent = WmiClassReading.Absent;
        var unreadable = WmiClassReading.Unreadable("access denied");

        Assert.True(present.IsPresent);
        Assert.False(present.IsUnreadable);
        Assert.Equal(1, present.MethodIds!["A"]);

        Assert.False(absent.IsPresent);
        Assert.False(absent.IsUnreadable);
        Assert.Null(absent.MethodIds);

        Assert.False(unreadable.IsPresent);
        Assert.True(unreadable.IsUnreadable);
        Assert.Equal("access denied", unreadable.Failure);
    }

    [Fact]
    public void A_class_that_is_present_and_declares_nothing_is_still_present()
    {
        var reading = WmiClassReading.Present(new Dictionary<string, int>(StringComparer.Ordinal));

        Assert.True(reading.IsPresent);
        Assert.Empty(reading.MethodIds!);
    }

    [Fact]
    public void The_live_reader_targets_the_namespace_the_mof_declares()
    {
        // Both files carry #pragma namespace(\\.\root\WMI); reading somewhere else would report
        // on a repository nothing was ever registered into.
        Assert.Equal(MofWriter.Namespace, new WmiClassSource().NamespacePath);
    }
}

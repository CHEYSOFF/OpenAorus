using System.IO;
using OpenAorus.Hardware.Platform;
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
        Assert.False(SchemaState.WritesUnlocked(r.Status!.Value));
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

    // ---- The removal file's own arguments -------------------------------------------------

    [Fact]
    public void The_removal_arguments_carry_no_createonly_and_no_check()
    {
        // createonly is meaningless against #pragma deleteclass, and -check would parse the file
        // and delete nothing - a rollback that silently did not roll back.
        var remove = SchemaRegistrar.BuildRemoveArguments(@"C:\Program Files\x\y-remove.mof");

        Assert.Contains(@"""C:\Program Files\x\y-remove.mof""", remove, StringComparison.Ordinal);
        Assert.DoesNotContain("-check", remove, StringComparison.Ordinal);
        Assert.DoesNotContain("-class:", remove, StringComparison.Ordinal);
        Assert.DoesNotContain("-N:", remove, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoRecover", remove, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Install refusals beyond Foreign ---------------------------------------------------

    [Fact]
    public void It_refuses_to_register_a_second_time_over_its_own_registration()
    {
        // CanInstall(Ours) is false, and this honours it rather than deciding again. Two
        // registrations over one GUID is the state nobody has tested.
        var sys = EmptyMachine();
        SchemaRegistrar.Install(sys, elevated: true, Fingerprint);
        var callsAfterFirst = sys.MofCompCalls.Count;

        var r = SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        Assert.False(r.Success);
        Assert.Equal(SchemaStatus.Ours, r.Status);
        Assert.Equal(callsAfterFirst, sys.MofCompCalls.Count);
    }

    [Fact]
    public void A_sweep_that_does_not_clear_the_leftovers_stops_before_the_compile()
    {
        // The dangerous shortcut would be to sweep, assume it worked, and compile anyway. What is
        // left might be Control Center's, and -class:createonly's refusal is not something to
        // provoke deliberately.
        var sys = new FakeSchemaSystem { OnCompile = _ => new MofCompResult(0, "nothing to do") };
        sys.Classes.Add(MofWriter.MarkerClass);

        var r = SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        Assert.False(r.Success);
        Assert.Equal(SchemaStatus.Partial, r.Status);
        Assert.Single(sys.MofCompCalls);
        Assert.Contains("remove", sys.MofCompCalls[0], StringComparison.Ordinal);
        Assert.Equal(sys.FilesWritten.Count, sys.FilesDeleted.Count);
    }

    // ---- Removal from every state that needs it --------------------------------------------

    [Fact]
    public void Removal_clears_a_machine_the_install_never_finished()
    {
        // The state that most needs cleaning up is a half-finished one: Partial is the only
        // status other than Ours that CanRemove allows, and it is the reason it does.
        var sys = EmptyMachine();
        sys.Classes.Add("GB_WMIACPI_Get");
        sys.Classes.Add(MofWriter.MarkerClass);

        var r = SchemaRegistrar.Remove(sys, elevated: true, Fingerprint);

        Assert.True(r.Success);
        Assert.Equal(SchemaStatus.Absent, r.Status);
        Assert.Empty(sys.Classes);
    }

    [Fact]
    public void Removing_from_a_machine_with_nothing_on_it_succeeds_without_running_anything()
    {
        var sys = EmptyMachine();

        var r = SchemaRegistrar.Remove(sys, elevated: true, Fingerprint);

        Assert.True(r.Success);
        Assert.Equal(SchemaStatus.Absent, r.Status);
        Assert.Empty(sys.MofCompCalls);
        Assert.Empty(sys.FilesWritten);
    }

    [Fact]
    public void A_removal_with_no_mofcomp_refuses_before_it_writes_anything()
    {
        var sys = EmptyMachine();
        SchemaRegistrar.Install(sys, elevated: true, Fingerprint);
        sys.MofCompPath = null;
        sys.FilesWritten.Clear();

        var r = SchemaRegistrar.Remove(sys, elevated: true, Fingerprint);

        Assert.False(r.Success);
        Assert.Contains("mofcomp", r.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(sys.FilesWritten);
    }

    [Fact]
    public void A_removal_mofcomp_refuses_reports_the_state_the_machine_is_actually_in()
    {
        var sys = EmptyMachine();
        SchemaRegistrar.Install(sys, elevated: true, Fingerprint);
        sys.OnCompile = _ => new MofCompResult(3, "0x80041003 Access denied");

        var r = SchemaRegistrar.Remove(sys, elevated: true, Fingerprint);

        Assert.False(r.Success);
        Assert.Equal(SchemaStatus.Ours, r.Status);   // re-read, not assumed from the exit code
        Assert.Contains("Access denied", r.Message, StringComparison.Ordinal);
        Assert.Equal(sys.FilesWritten.Count, sys.FilesDeleted.Count);
    }

    [Fact]
    public void A_removal_that_reports_success_and_deletes_half_says_so()
    {
        // deleteclass NOFAIL means a run that could only take some of the classes still exits
        // zero. Re-probing is the only way to find out, and the owner is told what is left.
        var sys = EmptyMachine();
        SchemaRegistrar.Install(sys, elevated: true, Fingerprint);
        sys.OnCompile = _ =>
        {
            sys.Classes.Remove("GB_WMIACPI_Data");
            sys.Classes.Remove("GB_WMIACPI_Event");
            return new MofCompResult(0, "");
        };

        var r = SchemaRegistrar.Remove(sys, elevated: true, Fingerprint);

        Assert.False(r.Success);
        Assert.Equal(SchemaStatus.Partial, r.Status);
        Assert.Contains(SchemaState.Explain(SchemaStatus.Partial), r.Message, StringComparison.Ordinal);
    }

    // ---- Nothing may throw out of either path -----------------------------------------------

    [Fact]
    public void A_repository_that_will_not_be_read_comes_back_as_a_failure_and_not_an_exception()
    {
        var sys = new FakeSchemaSystem { LookFailure = new InvalidOperationException("WMI is restarting") };

        var install = SchemaRegistrar.Install(sys, elevated: true, Fingerprint);
        var remove = SchemaRegistrar.Remove(sys, elevated: true, Fingerprint);

        Assert.False(install.Success);
        Assert.False(remove.Success);
        // No status at all, rather than a guess. A guess here is how an unreadable machine gets
        // registered over.
        Assert.Null(install.Status);
        Assert.Null(remove.Status);
        Assert.Contains("WMI is restarting", install.Message, StringComparison.Ordinal);
        Assert.Empty(sys.MofCompCalls);
    }

    [Fact]
    public void A_temporary_directory_that_will_not_take_the_file_is_reported_not_thrown()
    {
        var sys = EmptyMachine();
        sys.WriteFailure = new UnauthorizedAccessException("Access to the path is denied");

        var r = SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        Assert.False(r.Success);
        Assert.Contains("Access to the path is denied", r.Message, StringComparison.Ordinal);
        Assert.Empty(sys.MofCompCalls);
        Assert.Empty(sys.Classes);
    }

    [Fact]
    public void A_temporary_file_that_will_not_delete_does_not_undo_a_good_install()
    {
        // Cleanup is housekeeping. A leftover file in %TEMP% must not be reported as a failed
        // registration, because the registration succeeded and the Remove button now applies.
        var sys = EmptyMachine();
        sys.DeleteFailure = new IOException("The process cannot access the file");

        var r = SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        Assert.True(r.Success);
        Assert.Equal(SchemaStatus.Ours, r.Status);
    }

    [Fact]
    public void A_marker_that_will_not_be_read_still_produces_a_refusal()
    {
        var sys = MachineWithControlCenter();
        sys.MarkerFailure = new InvalidOperationException("no");

        var r = SchemaRegistrar.Remove(sys, elevated: true, Fingerprint);

        Assert.False(r.Success);
        Assert.Equal(SchemaStatus.Foreign, r.Status);
    }

    // ---- The refusal before either path has looked -------------------------------------------

    [Fact]
    public void A_refusal_taken_before_the_look_reports_no_status_rather_than_a_placeholder()
    {
        var sys = EmptyMachine();

        var unelevated = SchemaRegistrar.Install(sys, elevated: false, Fingerprint);
        sys.MofCompPath = null;
        var uncompilable = SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        Assert.Null(unelevated.Status);
        Assert.Null(uncompilable.Status);
    }

    // ---- The class that is present but declares nothing ---------------------------------------

    /// <summary>Our own registration after a WMI repository rebuild took its methods away.</summary>
    /// <remarks>Every class name still resolves, our marker still records the schema the install
    /// file wrote, and both method-bearing classes declare nothing.</remarks>
    private static FakeSchemaSystem MachineWeRegisteredThatRebuiltItself()
    {
        var sys = new FakeSchemaSystem { MarkerFingerprint = Fingerprint };
        foreach (var n in SchemaClasses.All) sys.Classes.Add(n);
        sys.CompileForReal(Fingerprint, RealIds());
        return sys;
    }

    [Fact]
    public void A_registration_of_ours_whose_methods_vanished_can_still_be_removed()
    {
        // The owner's way out, and the whole reason OursEmptied exists. Before it, this machine
        // read as Foreign, which withholds Remove as well as Install - so the app refused to undo
        // something the app itself had done, and an elevated mofcomp prompt was the only escape.
        var sys = MachineWeRegisteredThatRebuiltItself();

        var r = SchemaRegistrar.Remove(sys, elevated: true, Fingerprint);

        Assert.True(r.Success);
        Assert.Equal(SchemaStatus.Absent, r.Status);
        Assert.NotEmpty(sys.MofCompCalls);
        Assert.Empty(sys.Classes);
    }

    [Fact]
    public void A_registration_of_ours_whose_methods_vanished_is_still_never_installed_over()
    {
        // Remove only. The names are taken, the classes bind nothing, and -class:createonly would
        // refuse anyway - so nothing is compiled and the owner is told what the machine is in.
        var sys = MachineWeRegisteredThatRebuiltItself();

        var r = SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        Assert.False(r.Success);
        Assert.Equal(SchemaStatus.OursEmptied, r.Status);
        Assert.Empty(sys.MofCompCalls);
        Assert.Contains("no longer declare any methods", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_control_center_machine_is_left_exactly_as_it_was()
    {
        // The other direction of the same change. Nothing about OursEmptied may reach a machine
        // whose classes declare methods: no marker of ours, both buttons refused, no mofcomp.
        var sys = MachineWithControlCenter();

        var removed = SchemaRegistrar.Remove(sys, elevated: true, Fingerprint);
        var installed = SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        Assert.False(removed.Success);
        Assert.False(installed.Success);
        Assert.Equal(SchemaStatus.Foreign, removed.Status);
        Assert.Equal(SchemaStatus.Foreign, installed.Status);
        Assert.Empty(sys.MofCompCalls);
        Assert.Equal(4, sys.Classes.Count);
    }

    [Fact]
    public void A_marker_beside_classes_that_still_declare_methods_is_refused_and_told_why()
    {
        // A marker of ours over a mapping that is not ours. The classes declare methods, so this
        // is not our registration emptied, and it stays Foreign - but the refusal names the
        // marker rather than telling the owner a stranger did it.
        var sys = MachineWithControlCenter();
        sys.Classes.Add(MofWriter.MarkerClass);
        sys.MarkerFingerprint = Fingerprint;
        sys.MethodIds[WmiSchemaParser.GetClass] =
            new Dictionary<string, int>(StringComparer.Ordinal) { ["GetSomethingElse"] = 1 };

        var r = SchemaRegistrar.Remove(sys, elevated: true, Fingerprint);

        Assert.False(r.Success);
        Assert.Equal(SchemaStatus.Foreign, r.Status);
        Assert.Contains("marker", r.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Fingerprint, r.Message, StringComparison.Ordinal);
        Assert.Empty(sys.MofCompCalls);
    }

    [Fact]
    public void An_install_refusal_over_a_stranger_carries_no_marker_note()
    {
        var sys = MachineWithControlCenter();

        var r = SchemaRegistrar.Install(sys, elevated: true, Fingerprint);

        Assert.DoesNotContain("marker", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Look -------------------------------------------------------------------------------

    [Fact]
    public void Looking_at_an_untouched_machine_finds_nothing_to_fingerprint()
    {
        var snapshot = SchemaRegistrar.Look(new FakeSchemaSystem(), SchemaClasses.MethodBearing);

        Assert.False(snapshot.AnyClassPresent);
        Assert.False(snapshot.MarkerPresent);
        Assert.Null(snapshot.LiveFingerprint);
    }

    [Fact]
    public void A_class_that_is_present_and_declares_nothing_fingerprints_differently_from_an_absent_one()
    {
        var emptied = new FakeSchemaSystem();
        foreach (var n in SchemaClasses.All) emptied.Classes.Add(n);

        var snapshot = SchemaRegistrar.Look(emptied, SchemaClasses.MethodBearing);

        Assert.True(snapshot.AllClassesPresent);
        Assert.NotNull(snapshot.LiveFingerprint);
        Assert.NotEqual(Fingerprint, snapshot.LiveFingerprint);
    }
}

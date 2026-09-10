using System.Diagnostics;
using System.IO;
using OpenAorus.Hardware.Platform;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// What can be checked about the OS-facing half without registering anything on the machine
/// running the suite.
/// </summary>
/// <remarks>
/// <para>
/// The one thing this class exists to do - compile a MOF into the live repository - is not
/// testable here and must not be attempted: a test run that registered the schema would change
/// the developer's machine, and there is no assertion worth that. What is exercised instead is
/// everything around it. Where it looks for the compiler, which is the part that would be a
/// security problem if it were wrong. Where it puts the file it is about to hand over. That a run
/// which will not start, or will not finish, comes back as a result rather than as an exception,
/// because the interface forbids the exception and <see cref="SchemaRegistrar"/> has no way to
/// tell one apart from an unreadable machine.
/// </para>
/// <para>
/// <c>mofcomp -check</c> parses and stops - no elevation, no repository - so the real compiler
/// does get to read a real file written by the real writer. Where it is absent, those skip.
/// </para>
/// </remarks>
public class WindowsSchemaSystemTests
{
    // -------------------------------------------------------------------------------------
    // Where it looks
    // -------------------------------------------------------------------------------------

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

    // -------------------------------------------------------------------------------------
    // Reading the live repository. Metadata only - nothing below is an invocation.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Asking_about_a_class_that_cannot_exist_is_false_rather_than_an_exception()
    {
        // Runs against the live root\WMI on the test machine and must be safe there: it reads
        // class metadata and invokes nothing.
        var sys = new WindowsSchemaSystem();

        Assert.False(sys.ClassExists("OpenAorus_ThisClassDoesNotExist"));
        Assert.Empty(sys.ReadMethodIds("OpenAorus_ThisClassDoesNotExist"));
    }

    [Fact]
    public void A_marker_that_is_not_registered_reads_as_nothing_rather_than_throwing()
    {
        // The suite does not register the schema, so there is no marker to find. A machine that
        // refuses the read entirely has to land here too: the registrar quotes this into a
        // message and must never have a failed read become a failed operation.
        var sys = new WindowsSchemaSystem();

        Assert.Null(sys.ReadMarkerFingerprint());
    }

    // -------------------------------------------------------------------------------------
    // The files
    // -------------------------------------------------------------------------------------

    [Fact]
    public void A_written_mof_lands_in_the_schema_directory_and_reads_back_verbatim()
    {
        var sys = new WindowsSchemaSystem();
        var name = TempName();
        const string content = "// one\r\n// two\r\n";

        var path = sys.WriteMofFile(name, content);
        try
        {
            Assert.Equal(Path.Combine(WindowsSchemaSystem.MofDirectory, name), path);
            Assert.Equal(content, File.ReadAllText(path));
        }
        finally
        {
            sys.DeleteMofFile(path);
        }
    }

    [Fact]
    public void A_written_mof_carries_no_byte_order_mark()
    {
        // mofcomp reads ANSI or UTF-16-with-BOM. A stray UTF-8 BOM is one of the few ways a
        // byte-correct file still fails to parse, and it would fail at registration time on the
        // owner's machine rather than anywhere a test could see it.
        var sys = new WindowsSchemaSystem();
        var name = TempName();

        var path = sys.WriteMofFile(name, "class X { };\r\n");
        try
        {
            var bytes = File.ReadAllBytes(path);

            Assert.Equal((byte)'c', bytes[0]);
            Assert.False(bytes.Length > 2 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        }
        finally
        {
            sys.DeleteMofFile(path);
        }
    }

    [Fact]
    public void Deleting_a_written_mof_takes_it_away_and_a_second_delete_is_not_an_error()
    {
        var sys = new WindowsSchemaSystem();
        var path = sys.WriteMofFile(TempName(), "class X { };\r\n");

        sys.DeleteMofFile(path);
        Assert.False(File.Exists(path));

        // The registrar deletes in a finally that can run twice over the same list on some paths,
        // and it swallows failures there - so a throw here would be silently pointless noise.
        sys.DeleteMofFile(path);
    }

    [Fact]
    public void A_file_name_that_would_escape_the_schema_directory_is_refused()
    {
        // The directory was chosen for its permissions. A name that walks out of it hands the
        // elevated compiler a file from somewhere those permissions do not apply.
        var sys = new WindowsSchemaSystem();

        Assert.Throws<ArgumentException>(() => { sys.WriteMofFile(@"..\escaped.mof", "//"); });
        Assert.Throws<ArgumentException>(() => { sys.WriteMofFile(@"C:\Windows\Temp\escaped.mof", "//"); });
        Assert.Throws<ArgumentException>(() => { sys.WriteMofFile("sub/escaped.mof", "//"); });
    }

    // -------------------------------------------------------------------------------------
    // Starting a process. Nothing below reaches the repository: every mofcomp run is -check,
    // and the rest are cmd.exe standing in for a compiler that misbehaves.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void The_real_compiler_accepts_the_real_file_through_the_real_arguments()
    {
        var sys = new WindowsSchemaSystem();
        if (sys.MofCompPath is null) return;

        var path = sys.WriteMofFile(TempName(), SchemaMof.Install);
        try
        {
            // -check parses and stops. Without it this would write to the live WMI repository.
            var result = sys.RunMofComp(SchemaRegistrar.BuildCheckArguments(path));

            Assert.True(result.Success, result.Output);
            Assert.Contains("successfully parsed", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            sys.DeleteMofFile(path);
        }
    }

    [Fact]
    public void A_rejected_file_comes_back_as_the_compilers_own_words()
    {
        // The whole value of the failure path is that mofcomp names the line. Summarising it away
        // would leave the owner with "it did not work" and nothing to act on.
        var sys = new WindowsSchemaSystem();
        if (sys.MofCompPath is null) return;

        var path = sys.WriteMofFile(TempName(), "class Broken { this is not a MOF };\r\n");
        try
        {
            var result = sys.RunMofComp(SchemaRegistrar.BuildCheckArguments(path));

            Assert.False(result.Success);
            Assert.NotEqual(string.Empty, result.Output);
            Assert.Contains("error", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            sys.DeleteMofFile(path);
        }
    }

    [Fact]
    public void A_compiler_that_is_not_on_this_machine_is_a_failed_result_and_not_an_exception()
    {
        // Process.Start on a path that is not there throws Win32Exception. Letting that out would
        // reach the registrar as an unreadable machine, which is a different thing entirely.
        var result = WindowsSchemaSystem.Run(
            Path.Combine(WindowsSchemaSystem.MofDirectory, "no-such-compiler.exe"), "-check", 15_000);

        Assert.False(result.Success);
        Assert.True(result.ExitCode < 0);
        Assert.NotEqual(string.Empty, result.Output);
    }

    [Fact]
    public void A_run_that_will_not_finish_is_killed_and_reported_rather_than_left_going()
    {
        var started = Stopwatch.StartNew();

        // ~5 seconds of child, half a second of patience.
        var result = WindowsSchemaSystem.Run(Cmd, "/c ping -n 6 127.0.0.1", 500);

        started.Stop();

        Assert.True(result.ExitCode < 0);
        Assert.Contains("did not finish", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(4), $"waited {started.Elapsed}");
    }

    [Fact]
    public void Both_streams_come_back_because_mofcomp_reports_parse_errors_on_stdout()
    {
        var result = WindowsSchemaSystem.Run(Cmd, "/c \"echo FROM-STDOUT& echo FROM-STDERR 1>&2\"", 15_000);

        Assert.True(result.Success);
        Assert.Contains("FROM-STDOUT", result.Output, StringComparison.Ordinal);
        Assert.Contains("FROM-STDERR", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_output_is_trimmed_so_the_owner_facing_message_does_not_open_with_blank_lines()
    {
        var result = WindowsSchemaSystem.Run(Cmd, "/c echo SPACED", 15_000);

        Assert.Equal(result.Output.Trim(), result.Output);
    }

    [Fact]
    public void A_nonzero_exit_code_is_carried_through_as_it_stands()
    {
        // The registrar prints this number. A run that failed 3 must not read as having failed -1,
        // which is this class's own code for "it never ran".
        var result = WindowsSchemaSystem.Run(Cmd, "/c exit 3", 15_000);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Success);
    }

    private static string Cmd =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    private static string TempName() => $"openaorus-test-{Guid.NewGuid():N}.mof";
}

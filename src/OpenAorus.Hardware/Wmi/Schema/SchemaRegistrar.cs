using OpenAorus.Hardware.Platform;

namespace OpenAorus.Hardware.Wmi.Schema;

/// <summary>What one install or removal did, and where it left the machine.</summary>
/// <param name="Success">Whether the machine reached the state the operation was asking for. It is
/// decided by re-reading the repository afterwards, never by <c>mofcomp</c>'s exit code alone.</param>
/// <param name="Status">The state the machine is in now, or null if the operation refused before it
/// had looked - not elevated, no compiler - or if the repository could not be read at all. Null is
/// not a seventh state; it is the absence of an answer, exactly as in
/// <see cref="SchemaReport.Status"/>.</param>
/// <param name="Message">Owner-facing prose saying what was done, what state the machine ended in,
/// and, on any failure involving the compiler, what the compiler itself said.</param>
public sealed record SchemaOperationResult(bool Success, SchemaStatus? Status, string Message);

/// <summary>
/// Registers the schema, and takes it back out again. Reversibly, under elevation, and only when
/// the machine says it is safe at the moment of acting.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS <see cref="GccTakeover"/> AGAIN, AND IT IS MEANT TO BE. An explicit, elevated, reversible
/// system change the owner initiates, which records what it changed so it can be put back. The
/// differences are that the thing being changed is a WMI class rather than a service, and that the
/// record is a marker class living in WMI rather than a state object in the settings file - because
/// a settings file can be deleted while the classes stay behind.
/// </para>
/// <para>
/// EVERY DECISION IS TAKEN TWICE, AND THE SECOND ONE IS THE ONE THAT COUNTS. A classification made
/// when the Settings window opened is stale by the time a button is clicked: Control Center could
/// have been installed in between, or a repository rebuild could have emptied our classes. So both
/// operations re-read the machine immediately before acting, and both re-read it again immediately
/// afterwards. <c>mofcomp</c> saying zero is not evidence that the repository holds what it should.
/// </para>
/// <para>
/// PARTIAL IS THE OUTCOME TO FEAR. A machine carrying some of the classes and not others is worse
/// than one carrying none: the app cannot tell whose they are, the owner has no button that applies
/// to it, and a later install would be refused by <c>-class:createonly</c> with nothing said about
/// why. Every failure path therefore runs the removal file - which is idempotent by construction,
/// <c>#pragma deleteclass ... NOFAIL</c> - and reports the state the machine actually reached
/// rather than the one that was intended.
/// </para>
/// <para>
/// <c>#pragma autorecover</c> is deliberately absent from the MOF and from these command lines. It
/// writes into a machine-wide replay list that removal cannot cleanly undo, which would make this
/// the one irreversible thing in a feature whose whole point is being reversible.
/// </para>
/// </remarks>
public static class SchemaRegistrar
{
    /// <summary>
    /// How much of <c>mofcomp</c>'s output a message carries before it is cut short.
    /// </summary>
    /// <remarks>Enough for a parse error with its line number and the qualifier it choked on,
    /// which is what the first few lines of that output always are.</remarks>
    public const int MaxCompilerOutput = 500;

    /// <summary>Reads the machine: which classes are there, and what the method-bearing ones bind.</summary>
    /// <param name="sys">The seam onto the repository.</param>
    /// <param name="fingerprintedClasses">The classes to read method ids from - in practice
    /// <see cref="SchemaClasses.MethodBearing"/>, which is <c>Get</c> and <c>Set</c>. Reading ids
    /// for <c>Data</c> and <c>Event</c> would be two more round trips for two classes that declare
    /// no methods and so contribute nothing to the fingerprint either way.</param>
    /// <returns>What was found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sys"/> or
    /// <paramref name="fingerprintedClasses"/> is null.</exception>
    /// <remarks>Presence is asked for every class a registration creates, including the marker,
    /// because a leftover marker with no classes beside it is exactly the half-finished state the
    /// installer has to clean up before it may compile anything.</remarks>
    public static SchemaSnapshot Look(ISchemaSystem sys, IReadOnlyList<string> fingerprintedClasses)
    {
        ArgumentNullException.ThrowIfNull(sys);
        ArgumentNullException.ThrowIfNull(fingerprintedClasses);

        var present = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var className in SchemaClasses.All) present[className] = sys.ClassExists(className);
        foreach (var className in fingerprintedClasses)
            if (!present.ContainsKey(className)) present[className] = sys.ClassExists(className);

        var live = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var className in fingerprintedClasses)
        {
            // A present class goes in even when it declares nothing, and renders as
            // SchemaFingerprint.Missing. Dropping it would let a repository that lost every method
            // off GB_WMIACPI_Get fingerprint as though the class had never been there.
            if (present[className]) live[className] = sys.ReadMethodIds(className);
        }

        return new SchemaSnapshot(
            GetPresent: present[WmiSchemaParser.GetClass],
            SetPresent: present[WmiSchemaParser.SetClass],
            DataPresent: present[WmiSchemaParser.DataClass],
            EventPresent: present[WmiSchemaParser.EventClass],
            MarkerPresent: present[MofWriter.MarkerClass],
            MarkerFingerprint: MarkerRecord(sys),
            LiveFingerprint: live.Count == 0 ? null : SchemaFingerprint.OfLive(live));
    }

    /// <summary>The command line that parses the install MOF without writing anything.</summary>
    /// <param name="mofPath">The file to parse.</param>
    /// <returns>The arguments.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mofPath"/> is null.</exception>
    /// <remarks><c>-check</c> needs no elevation and touches no repository. A syntax error, a
    /// rejected base class, a qualifier this machine's <c>mofcomp</c> does not know - all of it
    /// surfaces here, before anything has been written.</remarks>
    public static string BuildCheckArguments(string mofPath) => $"-check {Quoted(mofPath)}";

    /// <summary>The command line that registers the install MOF.</summary>
    /// <param name="mofPath">The file to compile.</param>
    /// <returns>The arguments.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mofPath"/> is null.</exception>
    /// <remarks><c>-class:createonly</c> is the tool-enforced half of "never register over a
    /// working schema": if any class in the file already exists, <c>mofcomp</c> refuses and changes
    /// nothing. That closes the window between the look this code takes and the compile it then
    /// runs. The namespace is not passed with <c>-N:</c> - it lives in the MOF's own
    /// <c>#pragma namespace</c>, so there is one place it is written down.</remarks>
    public static string BuildCompileArguments(string mofPath) => $"-class:createonly {Quoted(mofPath)}";

    /// <summary>The command line that runs the removal MOF.</summary>
    /// <param name="mofPath">The file to compile.</param>
    /// <returns>The arguments.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mofPath"/> is null.</exception>
    /// <remarks>No <c>-class:</c> switch: that governs class creation and means nothing against
    /// <c>#pragma deleteclass</c>. No <c>-check</c> either, which would parse the file and delete
    /// nothing - a rollback that silently did not roll back.</remarks>
    public static string BuildRemoveArguments(string mofPath) => Quoted(mofPath);

    /// <summary>Registers the schema, if the machine is one where that is safe right now.</summary>
    /// <param name="sys">The seam onto the machine.</param>
    /// <param name="elevated">Whether this process is running as administrator.</param>
    /// <param name="expectedFingerprint">The fingerprint the install file records - normally
    /// <see cref="SchemaMof.Fingerprint"/>.</param>
    /// <returns>What happened, and the state the machine is in afterwards.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sys"/> or
    /// <paramref name="expectedFingerprint"/> is null.</exception>
    /// <remarks>
    /// <para>The sequence, and where each step's failure leaves the machine:</para>
    /// <list type="number">
    /// <item>Not elevated - refuse, change nothing. In practice this cannot happen, because startup
    /// relaunches under UAC before any of this is reachable. The check is here because "the app
    /// already has elevation" is a fact about a startup path someone could change, not a property
    /// of this function.</item>
    /// <item>No <c>mofcomp</c> - refuse, and say exactly that.</item>
    /// <item>A state <see cref="SchemaState.CanInstall"/> forbids - refuse and name it. That
    /// decision is honoured here, not taken again.</item>
    /// <item>Write both MOFs out. Both, always, so the rollback below never has to write one under
    /// whatever conditions have just gone wrong.</item>
    /// <item><see cref="SchemaStatus.Partial"/> - run the removal file first and re-look. A
    /// leftover marker or half a class set would make the compile fail on
    /// <c>-class:createonly</c>, and clearing our own leavings is what <c>NOFAIL deleteclass</c>
    /// is for. If the sweep does not clear it, stop: what is left may not be ours.</item>
    /// <item><c>-check</c> the install MOF. Failure - delete the files, report the compiler's own
    /// output, change nothing.</item>
    /// <item>Compile it, then re-look. Anything other than <see cref="SchemaStatus.Ours"/> - even
    /// after an exit code of zero - runs the removal file, re-looks again, and reports.</item>
    /// </list>
    /// </remarks>
    public static SchemaOperationResult Install(ISchemaSystem sys, bool elevated, string expectedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(sys);
        ArgumentNullException.ThrowIfNull(expectedFingerprint);

        if (!elevated) return Unelevated("register the Gigabyte WMI interface");
        if (sys.MofCompPath is null) return NoCompiler("Registering");

        try
        {
            var before = Classify(sys, expectedFingerprint);

            if (!SchemaState.CanInstall(before))
            {
                return new SchemaOperationResult(
                    false, before,
                    "OpenAorus registered nothing. " + SchemaState.Explain(before) + MarkerNote(sys, before));
            }

            return Register(sys, before, expectedFingerprint);
        }
        catch (Exception ex)
        {
            return Unreadable("registering", ex);
        }
    }

    /// <summary>Takes the schema back out, if what is there is ours to take.</summary>
    /// <param name="sys">The seam onto the machine.</param>
    /// <param name="elevated">Whether this process is running as administrator.</param>
    /// <param name="expectedFingerprint">The fingerprint the install file records - normally
    /// <see cref="SchemaMof.Fingerprint"/>.</param>
    /// <returns>What happened, and the state the machine is in afterwards.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sys"/> or
    /// <paramref name="expectedFingerprint"/> is null.</exception>
    /// <remarks>
    /// <para>Shorter than the install, with one refusal that matters more than any of them.</para>
    /// <list type="number">
    /// <item>Not elevated - refuse.</item>
    /// <item>No <c>mofcomp</c> - refuse.</item>
    /// <item><see cref="SchemaStatus.Absent"/> - succeed having done nothing. The desired end state
    /// is already the machine's state, and a second press of the button must not be an error the
    /// owner has to interpret.</item>
    /// <item>Re-look right now, and refuse anything <see cref="SchemaState.CanRemove"/> forbids.
    /// This is the second place "never remove someone else's" is enforced and the one that counts:
    /// the removal file deletes by name, and the names are Gigabyte's.</item>
    /// <item>Run the removal file, then re-look and expect <see cref="SchemaStatus.Absent"/>.
    /// Anything else is reported with the state it actually reached - <c>NOFAIL</c> means a run
    /// that could only take some of the classes still exits zero.</item>
    /// </list>
    /// <para>A removal that only got part-way is recoverable by pressing the button again, which is
    /// the whole reason the removal file is idempotent.</para>
    /// </remarks>
    public static SchemaOperationResult Remove(ISchemaSystem sys, bool elevated, string expectedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(sys);
        ArgumentNullException.ThrowIfNull(expectedFingerprint);

        if (!elevated) return Unelevated("remove the Gigabyte WMI registration");
        if (sys.MofCompPath is null) return NoCompiler("Removing the registration");

        try
        {
            var before = Classify(sys, expectedFingerprint);

            if (before == SchemaStatus.Absent)
            {
                return new SchemaOperationResult(
                    true, before,
                    "There was nothing to remove: no Gigabyte WMI class and no OpenAorus marker are " +
                    "registered on this machine.");
            }

            if (!SchemaState.CanRemove(before))
            {
                return new SchemaOperationResult(
                    false, before,
                    "OpenAorus removed nothing. " + SchemaState.Explain(before) + MarkerNote(sys, before));
            }

            return Delete(sys, expectedFingerprint);
        }
        catch (Exception ex)
        {
            return Unreadable("removing", ex);
        }
    }

    /// <summary>Steps 4 to 7 of the install, with the temporary files cleaned up whatever happens.</summary>
    private static SchemaOperationResult Register(ISchemaSystem sys, SchemaStatus before, string expected)
    {
        var written = new List<string>(2);
        try
        {
            var installPath = Write(sys, written, SchemaMof.InstallFileName, SchemaMof.Install);
            var removePath = Write(sys, written, SchemaMof.RemoveFileName, SchemaMof.Remove);

            if (before == SchemaStatus.Partial)
            {
                var swept = sys.RunMofComp(BuildRemoveArguments(removePath));
                var cleared = Classify(sys, expected);

                if (cleared != SchemaStatus.Absent)
                {
                    return new SchemaOperationResult(
                        false, cleared,
                        "OpenAorus could not clear what an earlier registration left behind, so it did " +
                        "not register anything over it. " + Quote(swept) + " " +
                        SchemaState.Explain(cleared));
                }
            }

            var check = sys.RunMofComp(BuildCheckArguments(installPath));
            if (!check.Success)
            {
                // Nothing has been written into the repository at this point: -check parses and
                // stops. The state reported is therefore the state the machine was already in.
                return new SchemaOperationResult(
                    false, Classify(sys, expected),
                    "The schema file did not survive mofcomp's own syntax check, so OpenAorus did not " +
                    "compile it and this machine is unchanged. " + Quote(check));
            }

            var compiled = sys.RunMofComp(BuildCompileArguments(installPath));
            var reached = Classify(sys, expected);

            if (compiled.Success && reached is SchemaStatus.Ours or SchemaStatus.OursGated)
                return new SchemaOperationResult(true, reached, SchemaState.Explain(reached));

            // Two different failures with one answer. Either mofcomp said no - and may have created
            // some of the classes before it did - or it said yes and the repository does not hold
            // what it should. Both leave a machine that might be carrying part of a registration,
            // and part is worse than none.
            sys.RunMofComp(BuildRemoveArguments(removePath));
            var rolled = Classify(sys, expected);

            var lead = compiled.Success
                ? "mofcomp reported success, but the classes it should have created are not on this " +
                  "machine. OpenAorus does not treat an exit code as proof, so it re-read the " +
                  "repository and rolled the registration back."
                : "mofcomp could not register the schema. OpenAorus ran its removal file so that " +
                  "nothing is left half-registered.";

            var landed = rolled == SchemaStatus.Absent
                ? "The machine is back as it was: " + SchemaState.Explain(rolled)
                : "The rollback did not clear everything. " + SchemaState.Explain(rolled);

            return new SchemaOperationResult(
                false, rolled, lead + " " + Quote(compiled) + " " + landed);
        }
        finally
        {
            foreach (var path in written) TryDelete(sys, path);
        }
    }

    /// <summary>Steps 4 and 5 of the removal.</summary>
    private static SchemaOperationResult Delete(ISchemaSystem sys, string expected)
    {
        var written = new List<string>(1);
        try
        {
            var removePath = Write(sys, written, SchemaMof.RemoveFileName, SchemaMof.Remove);

            var run = sys.RunMofComp(BuildRemoveArguments(removePath));
            var reached = Classify(sys, expected);

            if (reached == SchemaStatus.Absent)
            {
                return new SchemaOperationResult(
                    true, reached,
                    "OpenAorus removed the Gigabyte WMI registration it installed. " +
                    SchemaState.Explain(reached));
            }

            // NOFAIL exits zero having deleted nothing, so the exit code says very little here.
            // The re-read is what says where the machine is, and a second press is safe.
            return new SchemaOperationResult(
                false, reached,
                "OpenAorus could not take the whole registration away. " + Quote(run) + " " +
                SchemaState.Explain(reached));
        }
        finally
        {
            foreach (var path in written) TryDelete(sys, path);
        }
    }

    private static SchemaStatus Classify(ISchemaSystem sys, string expectedFingerprint) =>
        SchemaState.Classify(Look(sys, SchemaClasses.MethodBearing), expectedFingerprint, gatesRecorded: false);

    private static string Write(ISchemaSystem sys, List<string> written, string fileName, string content)
    {
        var path = sys.WriteMofFile(fileName, content);
        written.Add(path);
        return path;
    }

    /// <summary>Deletes a temporary file, and never lets that failure become the result.</summary>
    /// <remarks>A leftover file in the temporary directory is housekeeping. Reporting it as a
    /// failed registration would tell the owner to press Install again on a machine where the
    /// registration succeeded, and the second press would be refused with no explanation.</remarks>
    private static void TryDelete(ISchemaSystem sys, string path)
    {
        try { sys.DeleteMofFile(path); }
        catch (Exception) { }
    }

    /// <summary>
    /// The one extra sentence a <see cref="SchemaStatus.Foreign"/> refusal earns when our own
    /// marker is still standing beside the classes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Foreign withholds Remove as well as Install, and a marker standing beside the classes says
    /// an OpenAorus registration was made on this machine at some point. The classification is
    /// still right - what is there now does not match what we install, so the app cannot say those
    /// names reach the firmware methods it recorded - but a bare "something else registered this"
    /// would be a false account of it.
    /// </para>
    /// <para>
    /// The one shape of this the app can act on has its own state.
    /// <see cref="SchemaStatus.OursEmptied"/> - our marker recording our schema over classes that
    /// declare nothing - permits Remove, and never reaches here. What is left is a marker over a
    /// mapping that is neither ours nor empty, and this note says so without promising anything:
    /// an install over a stranger stays refused, and a removal of a stranger's classes stays
    /// refused. It only means the owner is told which situation they are in.
    /// </para>
    /// </remarks>
    private static string MarkerNote(ISchemaSystem sys, SchemaStatus status)
    {
        if (status != SchemaStatus.Foreign) return string.Empty;
        if (MarkerRecord(sys) is not { Length: > 0 } recorded) return string.Empty;

        return " An OpenAorus marker is still registered beside those classes and records schema " +
               recorded + ", so OpenAorus did register this machine at some point. What is on the " +
               "machine now is neither that schema nor an empty copy of it, so OpenAorus cannot " +
               "say what those names reach and will not delete classes carrying Gigabyte's names " +
               "on that basis.";
    }

    /// <summary>Reads the marker, treating any failure as "it did not say".</summary>
    /// <remarks>It feeds the refusal message above and one decision:
    /// <see cref="SchemaStatus.OursEmptied"/>, which needs the marker to record the schema our
    /// install file writes. Null is the safe answer for both - it matches no fingerprint, so a
    /// marker that will not answer can only ever withhold a permission, never grant one.</remarks>
    private static string? MarkerRecord(ISchemaSystem sys)
    {
        try { return sys.ReadMarkerFingerprint(); }
        catch (Exception) { return null; }
    }

    private static SchemaOperationResult Unelevated(string what) =>
        new(false, null,
            $"OpenAorus needs administrator rights to {what}, and it is not running with them. " +
            "Nothing on this machine was changed.");

    private static SchemaOperationResult NoCompiler(string what) =>
        new(false, null,
            what + " needs mofcomp.exe, which ships with Windows and is not on this machine. That " +
            "means the WMI installation itself is damaged, and registering a schema on top of a " +
            "damaged WMI installation is not the repair. Nothing was changed.");

    private static SchemaOperationResult Unreadable(string what, Exception ex) =>
        new(false, null,
            $"OpenAorus could not read this machine's WMI repository while {what} the schema, so it " +
            "stopped rather than guess at what is registered. Windows reported: " +
            $"{ex.GetType().Name}: {ex.Message}");

    /// <summary>Quotes what the compiler said, trimmed and capped.</summary>
    /// <remarks>Verbatim, because the compiler names the line and the qualifier and this code
    /// cannot. Capped because a run that goes wrong early can repeat itself for pages.</remarks>
    private static string Quote(MofCompResult result)
    {
        var output = result.Output is null ? string.Empty : result.Output.Trim();

        if (output.Length == 0) return $"mofcomp exited with {result.ExitCode} and said nothing.";
        if (output.Length > MaxCompilerOutput) output = output[..MaxCompilerOutput] + "...";

        return $"mofcomp exited with {result.ExitCode}: {output}";
    }

    private static string Quoted(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return $"\"{path}\"";
    }
}

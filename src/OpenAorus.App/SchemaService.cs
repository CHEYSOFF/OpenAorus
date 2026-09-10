using OpenAorus.Hardware.Config;
using OpenAorus.Hardware.Platform;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.App;

/// <summary>
/// The app's live answer to "what is registered on this machine, and may anything be written".
/// </summary>
/// <remarks>
/// <para>
/// It decides nothing. One <see cref="SchemaRegistrar.Look"/> and one
/// <see cref="SchemaState.Classify"/>, held so that the fan controller, the battery controller, the
/// window and the Settings card all read the same answer rather than each asking WMI for their own.
/// </para>
/// <para>
/// THE LOOK COSTS ONE WMI CONNECTION AND INVOKES NO FIRMWARE METHOD. It reads class metadata and
/// one instance of our own marker, which is why <c>--dump</c> and <c>--apply</c> can afford it at
/// startup - and why <c>--apply</c> on a locked machine now exits with a message that names the
/// cause instead of failing one step into a five-step write.
/// </para>
/// <para>
/// A repository that will not answer comes back as a null <see cref="SchemaReport.Status"/> rather
/// than as an exception or as "nothing is registered". Those are three different things, and
/// collapsing the last two is how a machine mid-rebuild ends up being registered over.
/// </para>
/// </remarks>
public sealed class SchemaService
{
    private readonly Func<SchemaReport> _look;

    /// <summary>Reads a real machine, and classifies it once immediately.</summary>
    /// <param name="system">The seam onto the repository and onto <c>mofcomp</c>.</param>
    /// <param name="record">The owner's gate record, live - the same instance
    /// <see cref="AppSettings.Schema"/> holds, so a pass written by the Settings card is visible to
    /// the next <see cref="Refresh"/> without anything being copied back.</param>
    /// <param name="expectedFingerprint">The fingerprint our install file records, normally
    /// <see cref="SchemaMof.Fingerprint"/>.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public SchemaService(ISchemaSystem system, SchemaRecord record, string expectedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(expectedFingerprint);

        System = system;
        Record = record;
        ExpectedFingerprint = expectedFingerprint;
        _look = () => Look(system, record, expectedFingerprint);
        Report = _look();
    }

    /// <summary>A service over no machine at all, pinned at one state.</summary>
    private SchemaService(SchemaStatus pinned)
    {
        System = new NoSchemaSystem();
        Record = new SchemaRecord();
        ExpectedFingerprint = SchemaMof.Fingerprint;

        var pinnedReport = new SchemaReport(pinned, Snapshot: null, Failure: null);
        _look = () => pinnedReport;
        Report = pinnedReport;
    }

    /// <summary>
    /// The default for a rig that is not exercising the schema: writes unlocked, nothing to say.
    /// </summary>
    /// <remarks>
    /// A fresh instance each time, so nothing is shared between two sets of services. It exists so
    /// that every construction site written before this feature - and every test over one - keeps
    /// behaving exactly as it did; the real app always builds the live one in
    /// <see cref="AppServices.Create"/>, so this can never become the answer on an owner's laptop.
    /// </remarks>
    public static SchemaService Unrestricted => new(SchemaStatus.OursGated);

    /// <summary>The seam the Install and Remove buttons act through.</summary>
    public ISchemaSystem System { get; }

    /// <summary>The gate record, live.</summary>
    public SchemaRecord Record { get; }

    /// <summary>The fingerprint our install file records.</summary>
    public string ExpectedFingerprint { get; }

    /// <summary>The last reading of the machine, and what may be done to it.</summary>
    public SchemaReport Report { get; private set; }

    /// <summary>The state at that reading, or null if the machine could not be read.</summary>
    public SchemaStatus? Status => Report.Status;

    /// <summary>Whether fan and battery writes are allowed right now.</summary>
    public bool WritesUnlocked => Report.WritesUnlocked;

    /// <summary>Reads the machine again.</summary>
    /// <remarks>Called after an install, a removal or a gate run, and never on a timer: this is a
    /// handful of WMI round trips, and the state only changes when something the owner pressed
    /// changed it. Callers on the UI thread run it on the thread pool.</remarks>
    public void Refresh() => Report = _look();

    private static SchemaReport Look(ISchemaSystem system, SchemaRecord record, string expected)
    {
        try
        {
            var snapshot = SchemaRegistrar.Look(system, SchemaClasses.MethodBearing);

            // ProvenFor and not GatesPassed: a recorded pass is worth exactly as long as the
            // schema it was earned against stays the same, and the live fingerprint is what says
            // whether it has.
            return new SchemaReport(
                SchemaState.Classify(snapshot, expected, record.ProvenFor(snapshot.LiveFingerprint)),
                snapshot,
                Failure: null);
        }
        catch (Exception ex)
        {
            // The seam is contracted not to throw, and SchemaRegistrar defends against one that
            // does anyway. This runs before the window exists, so nothing may come out of here.
            return new SchemaReport(Status: null, Snapshot: null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

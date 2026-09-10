using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAorus.Hardware.Platform;
using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.App.ViewModels;

/// <summary>
/// The Settings card that registers the Gigabyte WMI interface, proves it, and takes it back out.
/// </summary>
/// <remarks>
/// <para>
/// IT DECIDES NOTHING. <see cref="SchemaState"/> says which of the three actions a machine is
/// entitled to and <see cref="SchemaRegistrar"/> re-decides at the moment of acting, because a
/// classification made when the window opened is stale by the time a button is clicked. This class
/// asks, formats and reports.
/// </para>
/// <para>
/// The card sits outside the Settings window's <c>CanWrite</c> gate, for a sharper reason than the
/// hotkey card's: it is the card that fixes the condition disabling everything else, and greying it
/// out would leave the owner in a modal dialog whose only working control is the one that cannot
/// help them.
/// </para>
/// <para>
/// REGISTERING AND PROVING ARE TWO SEPARATE THINGS AND THE CARD DOES NOT PRETEND OTHERWISE. A
/// successful install leaves <see cref="SchemaStatus.Ours"/>, which unlocks nothing; what is
/// offered next is <see cref="RunGatesCommand"/> - "Check it works" - and only a run that passes
/// both gates against this exact mapping moves the machine to
/// <see cref="SchemaStatus.OursGated"/>.
/// </para>
/// <para>
/// WHICH IS WHY "CHECK IT WORKS" IS OFFERED ON A MACHINE THIS APP NEVER REGISTERED. Register and
/// Remove are refused over Control Center's schema and always will be, so on the commonest machine
/// of all the check is the only button there is - and it is the only one such an owner needs, since
/// what a passing run unlocks is the writes, not the registration.
/// </para>
/// </remarks>
public sealed partial class SchemaViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Action? _writesChanged;
    private readonly Func<bool> _elevated;

    /// <summary>What the last action reported, verbatim where a compiler was involved.</summary>
    [ObservableProperty] private string _message = "";

    /// <summary>Whether an action is running. All three buttons are withheld while one is.</summary>
    /// <remarks>Registering runs <c>mofcomp</c> twice and a gate run makes ninety WMI calls, so
    /// both go to the thread pool and both take seconds. A second press landing in the middle of
    /// the first would have two elevated compilers arguing over one repository.</remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyPropertyChangedFor(nameof(CanRunGates))]
    private bool _isBusy;

    /// <param name="s">The app's services; its <see cref="AppServices.Schema"/> is the live reading
    /// every other view model's <c>CanWrite</c> is computed from.</param>
    /// <param name="writesChanged">Called after every action that could have moved the machine, so
    /// the window and the battery card re-read <c>CanWrite</c> without a restart.</param>
    /// <param name="elevated">Whether this process is running as administrator; null asks Windows.
    /// Injected so that the unelevated refusal can be exercised, which is otherwise a state a test
    /// run cannot be in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="s"/> is null.</exception>
    public SchemaViewModel(AppServices s, Action? writesChanged = null, Func<bool>? elevated = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        _s = s;
        _writesChanged = writesChanged;
        _elevated = elevated ?? Elevation.IsElevated;
    }

    /// <summary>The state at the last look, or null if the machine could not be read.</summary>
    public SchemaStatus? Status => _s.Schema.Status;

    /// <summary>What that state means, in the state machine's own words.</summary>
    /// <remarks>Not a second copy of the prose. An explanation that drifted from
    /// <see cref="SchemaState.Explain"/> would be the window and the refused write disagreeing
    /// about the same machine.</remarks>
    public string StatusText => _s.Schema.Report.Explanation;

    /// <summary>Whether the Register button is offered.</summary>
    public bool CanInstall => !IsBusy && _s.Schema.Report.CanInstall;

    /// <summary>Whether the Remove button is offered.</summary>
    public bool CanRemove => !IsBusy && _s.Schema.Report.CanRemove;

    /// <summary>Whether the Check it works button is offered.</summary>
    /// <remarks>Wherever all four classes are there and bind something, whoever put them there.
    /// Gate B writes to the firmware, so it is not offered over half a registration or over classes
    /// that declare nothing - but it is emphatically offered on a Control Center machine, which is
    /// the only route such an owner has to a working app: Install is refused there and always will
    /// be. On a machine already gated it stays offered, because re-proving the same registration is
    /// the one diagnostic the owner has.</remarks>
    public bool CanRunGates => !IsBusy && GatesCanBeRun;

    /// <summary>Whether there is a registration on this machine for the gates to be run against.</summary>
    /// <remarks>Read by <see cref="RunGatesAsync"/> rather than <see cref="CanRunGates"/>, which
    /// has already gone false by the time the command is running: the busy latch closes first.</remarks>
    private bool GatesCanBeRun => Status is
        SchemaStatus.Ours or SchemaStatus.OursGated or
        SchemaStatus.Foreign or SchemaStatus.ForeignGated;

    /// <summary>Whether the classes on this machine are the ones this app installed.</summary>
    /// <remarks>Ownership only. It decides what the gate record writes down about who registered
    /// the schema, and nothing about whether the gates may run or what they unlock.</remarks>
    private bool RegistrationIsOurs => Status is SchemaStatus.Ours or SchemaStatus.OursGated;

    /// <summary>Whether the missing thing is administrator rights rather than the schema.</summary>
    public bool NeedsElevation => !_elevated();

    /// <summary>Re-reads the machine, for a dialog that is being opened again.</summary>
    /// <remarks>
    /// Control Center can be installed, and a WMI repository can rebuild itself, while this app is
    /// sitting in the tray. The reading taken at startup would then have the card offering an
    /// action the machine is no longer entitled to - which <see cref="SchemaRegistrar"/> would
    /// refuse anyway, but only after the owner had pressed it.
    ///
    /// Off the UI thread, because it is a handful of WMI round trips and this runs from the
    /// window's Loaded handler.
    /// </remarks>
    public async Task RefreshAsync()
    {
        if (IsBusy) return;

        await Task.Run(_s.Schema.Refresh);
        Notify();
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        await ActAsync(
            "Registering the interface. This takes a few seconds.",
            () => SchemaRegistrar.Install(_s.Schema.System, _elevated(), _s.Schema.ExpectedFingerprint),
            OnRegistered);
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        await ActAsync(
            "Removing the registration.",
            () => SchemaRegistrar.Remove(_s.Schema.System, _elevated(), _s.Schema.ExpectedFingerprint),
            OnRemoved);
    }

    /// <summary>
    /// Runs both hardware gates against the registration that is on the machine now.
    /// </summary>
    /// <remarks>
    /// The one place in this app that writes to the firmware before writes are unlocked, and it
    /// writes exactly once: <c>SetChargeStop</c> with the value <c>GetChargeStop</c> has just
    /// answered, range-checked first. See <see cref="SchemaGateRunner"/> for why that is the only
    /// write this feature is entitled to make, and why Gate B does not run at all after a failed
    /// Gate A.
    /// </remarks>
    [RelayCommand]
    private async Task RunGatesAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (!GatesCanBeRun)
            {
                // Not a state the buttons can reach, and checked anyway: the machine may have
                // moved since the window was opened, and the gates end in a write.
                Message = "There is no working Gigabyte WMI registration on this machine to " +
                          "check. " + StatusText;
                return;
            }

            Message = "Reading every method and running one charge-limit round trip.";

            // The mapping the pass is about to be recorded against. Read before the run rather
            // than after it, so a repository that changed underneath cannot have the pass filed
            // against the schema it changed to.
            var fingerprint = _s.Schema.Report.Snapshot?.LiveFingerprint ?? _s.Schema.ExpectedFingerprint;

            var run = await Task.Run(
                () => SchemaGateRunner.Run(_s.Wmi, KnownGoodReading.Reference, _s.Profile));

            var record = _s.Schema.Record;

            // Ownership is written down as the machine reports it and not as the run implies.
            // Where the marker and the fingerprint say the registration is ours, a settings file
            // that lost the flag is corrected here; where they say it is Control Center's, the
            // flag stays false, because this run registered nothing. Neither answer affects what
            // the pass below unlocks - the fingerprint does that.
            if (RegistrationIsOurs) record.Registered = true;

            record.Fingerprint = fingerprint;
            record.GatesPassed = run.Passed;
            record.When = DateTime.Now;
            record.GateSummary = run.Summary();
            _s.Store.Save(_s.Settings);

            await Task.Run(_s.Schema.Refresh);

            Message = run.Passed
                ? "Both checks passed, so fan and battery control are on. " + run.Summary()
                : "The checks did not pass, so fan and battery writes stay locked. " + run.Summary();
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    /// <summary>Runs one elevated operation off the UI thread and reports where it left the machine.</summary>
    /// <param name="progress">What the card says while it is running.</param>
    /// <param name="operation">The registrar call, which re-reads the machine itself.</param>
    /// <param name="onSuccess">What to write down when it worked.</param>
    private async Task ActAsync(
        string progress, Func<SchemaOperationResult> operation, Action onSuccess)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            Message = progress;

            // Off the UI thread: mofcomp against a cold repository is given up to a minute, and a
            // window frozen for that long is one the owner assumes has crashed.
            var result = await Task.Run(operation);

            if (result.Success) { onSuccess(); _s.Store.Save(_s.Settings); }

            Message = result.Message;
            await Task.Run(_s.Schema.Refresh);
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    /// <summary>A fresh registration: ours, and proved of nothing.</summary>
    private void OnRegistered()
    {
        var record = _s.Schema.Record;
        record.Registered = true;
        // The classes have just been replaced, so any pass in the file was earned against a schema
        // that is no longer on this machine. ProvenFor would already refuse it on the fingerprint;
        // clearing it keeps the file from claiming something it cannot support.
        record.GatesPassed = false;
        record.Fingerprint = null;
        record.When = null;
        record.GateSummary = null;
    }

    /// <summary>The registration is gone, and so is everything recorded about it.</summary>
    private void OnRemoved()
    {
        var record = _s.Schema.Record;
        record.Registered = false;
        record.GatesPassed = false;
        record.Fingerprint = null;
        record.When = null;
        record.GateSummary = null;
    }

    /// <summary>Re-reads everything computed from the machine, here and in the window behind it.</summary>
    private void Notify()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanRunGates));
        OnPropertyChanged(nameof(NeedsElevation));
        _writesChanged?.Invoke();
    }
}

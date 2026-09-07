using System.Management;
using OpenAorus.Hardware.Hotkeys;

namespace OpenAorus.App.Hotkeys;

/// <summary>
/// Subscribes to <c>GB_WMIACPI_Event</c> in <c>root\WMI</c> and hands each event's <c>Data</c>
/// value on undecoded.
/// </summary>
/// <remarks>
/// <para>
/// Needs elevation, which the app already has. The query and the property name come from
/// <see cref="WmiEventDecoder"/>, and so does the conversion: reading the boxed value is the one
/// unconfirmed thing about this subscription, and
/// <see cref="WmiEventDecoder.TryReadData(object?, out int)"/> keeps it in a pure function a test
/// can exercise instead of in a catch block down here that no test can reach.
/// </para>
/// <para>
/// The property name is unconfirmed on hardware. A wrong name is a subscription that runs and
/// reports nothing, silently - so an event whose <c>Data</c> was not where it was expected is
/// written to the trace along with the property names that did arrive, which is what turns
/// VERIFY 7.1 from a guess into a reading.
/// </para>
/// <para>
/// Thin, like the raw-input window: it receives, writes down what arrived, and hands the value
/// on. What the four documented values mean is <see cref="WmiEventDecoder"/>'s to say and what to
/// do about them is <see cref="HotkeyPolicy"/>'s; both return nothing for anything they do not
/// recognise, and the wiring above this must check that before acting.
/// </para>
/// <para>
/// Nothing here is exercised by a test beyond construction and teardown. A live subscription
/// needs an elevated process and a Gigabyte WMI provider; VERIFY 7.1 is what confirms it.
/// </para>
/// </remarks>
public sealed class WmiEventListener : IWmiEventSource
{
    private const string ScopePath = @"root\WMI";
    private const string EventSite = "GB_WMIACPI_Event";
    private const string SubscriptionSite = "WMI event subscription";
    private const string TeardownSite = "WMI teardown";

    private readonly HotkeyTrace _trace;
    private ManagementEventWatcher? _watcher;
    private bool _started;

    /// <summary>Creates the listener. Nothing happens until <see cref="Start"/>.</summary>
    /// <param name="trace">Where arriving events are written down, including the ones this app
    /// could make no sense of.</param>
    /// <exception cref="ArgumentNullException"><paramref name="trace"/> is null.</exception>
    public WmiEventListener(HotkeyTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        _trace = trace;
    }

    /// <inheritdoc />
    public event Action<int>? EventReceived;

    /// <summary>Whether the subscription is running.</summary>
    public bool IsListening { get; private set; }

    /// <summary>What went wrong, if the subscription could not be created; null otherwise.</summary>
    /// <remarks>Set once, by <see cref="Start"/>. Nothing on the callback path writes it, so a
    /// provider that misbehaves per event cannot turn into a banner per event.</remarks>
    public string? StartError { get; private set; }

    /// <inheritdoc />
    public void Start()
    {
        if (_started) return;
        _started = true;

        try
        {
            var options = new ConnectionOptions
            {
                EnablePrivileges = true,
                Impersonation = ImpersonationLevel.Impersonate,
            };
            var scope = new ManagementScope(ScopePath, options);
            scope.Connect();

            _watcher = new ManagementEventWatcher(scope, new EventQuery(WmiEventDecoder.Query));
            _watcher.EventArrived += OnEventArrived;
            _watcher.Start();
            IsListening = true;
        }
        catch (Exception ex)
        {
            // Broad on purpose, and reported once. This channel carries the touchpad and radio
            // notices only; a provider that refuses the subscription must not stop the raw-input
            // channel, and must never stop the app starting.
            IsListening = false;
            StartError = $"The touchpad and Wi-Fi notices are unavailable: {ex.GetType().Name}: {ex.Message}";
            _trace.RecordFault(SubscriptionSite, ex);
            SafeTearDown();
        }
    }

    /// <summary>Stops the subscription and releases the watcher.</summary>
    /// <remarks>A <see cref="ManagementEventWatcher"/> holds an unmanaged subscription that
    /// outlives the object if it is only dropped, so shutdown has to come through here.</remarks>
    public void Dispose() => SafeTearDown();

    /// <summary>Tears down without raising anything.</summary>
    /// <remarks>Both callers - shutdown, and a subscription that has already failed - have nobody
    /// left to report to, and a throw from either would undo the promise the failure is being
    /// handled for.</remarks>
    private void SafeTearDown()
    {
        try
        {
            TearDown();
        }
        catch (Exception ex)
        {
            _trace.RecordFault(TeardownSite, ex);
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        // Same rule as the WM_INPUT hook: an exception on a provider's callback thread stops the
        // channel and is never seen. One line per site however often it repeats.
        try
        {
            var names = new List<string>();
            object? data = null;

            // Walked rather than indexed. ManagementBaseObject's indexer throws when the property
            // is absent, and "which properties did arrive" is the answer VERIFY 7.1 needs most.
            var properties = e.NewEvent?.Properties;
            if (properties is not null)
            {
                foreach (PropertyData property in properties)
                {
                    names.Add(property.Name);
                    if (string.Equals(property.Name, WmiEventDecoder.DataProperty, StringComparison.OrdinalIgnoreCase))
                        data = property.Value;
                }
            }

            Deliver(data, names);
        }
        catch (Exception ex)
        {
            _trace.RecordFault(EventSite, ex);
        }
    }

    /// <summary>Writes one event down and then hands it on, in that order.</summary>
    /// <remarks>
    /// Internal, and visible to the tests, for the reason <see cref="RawInputWindow.Deliver"/> is:
    /// reaching it through a live subscription needs an elevated process and a Gigabyte provider,
    /// and this is the whole of what the callback does once it has the property bag. Walking the
    /// bag is what is left unreachable, and VERIFY 7.1 is what confirms that half.
    ///
    /// The order is the contract, the same one the raw-input channel keeps: a value that arrived
    /// has to be written down before anything downstream gets the chance to throw and turn it
    /// into a fault line with no event behind it.
    ///
    /// Checked, not assumed: an absent property, a value of an unexpected type and a number that
    /// will not fit all land in the second branch, and none of them is something to hand on. What
    /// they are is the one thing that would explain a subscription that runs forever and reports
    /// nothing, so they are written down with the names that did arrive.
    /// </remarks>
    /// <param name="data">The <c>Data</c> property's value, exactly as the provider boxed it, or
    /// null when the event carried no such property.</param>
    /// <param name="propertyNames">The property names the event did carry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="propertyNames"/> is null.</exception>
    internal void Deliver(object? data, IReadOnlyList<string> propertyNames)
    {
        ArgumentNullException.ThrowIfNull(propertyNames);

        if (WmiEventDecoder.TryReadData(data, out var value))
        {
            _trace.RecordEvent(value);
            EventReceived?.Invoke(value);
            return;
        }

        _trace.RecordUnreadableEvent(propertyNames);
    }

    private void TearDown()
    {
        IsListening = false;
        if (_watcher is null) return;

        var watcher = _watcher;
        _watcher = null;
        watcher.EventArrived -= OnEventArrived;

        try
        {
            watcher.Stop();
        }
        catch (Exception ex)
        {
            // Stopping can throw if the provider is already gone; the Dispose below still has to
            // run, so this one is caught here rather than at the outer boundary.
            _trace.RecordFault(TeardownSite, ex);
        }

        watcher.Dispose();
    }
}

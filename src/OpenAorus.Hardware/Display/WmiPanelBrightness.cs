using System.Globalization;
using System.Management;

namespace OpenAorus.Hardware.Display;

/// <summary>
/// The panel's brightness through <c>root\WMI</c>: <c>WmiMonitorBrightness</c> to read it and
/// <c>WmiMonitorBrightnessMethods.WmiSetBrightness</c> to write it.
/// </summary>
/// <remarks>
/// <para>
/// WINDOWS' OWN INTERFACE, NOT GIGABYTE'S, and that is not a preference. <c>GetBrightness</c> on
/// the Gigabyte WMI interface answers <c>Invalid object</c> on this model - the embedded controller
/// does not expose the panel at all - so <see cref="Wmi.IGigabyteWmi"/>, which every other hardware
/// path in this app goes through, cannot be used for this one. These two classes are the standard
/// ones every laptop with a controllable panel publishes.
/// </para>
/// <para>
/// UNLIKE THE FAN AND BATTERY PATHS, THIS NEEDS NO ELEVATION. <c>WmiSetBrightness</c> is what the
/// brightness slider in Settings calls, and an ordinary user may call it for their own session's
/// display. So the brightness keys work on a machine where the fan controls are read-only, which is
/// most of them.
/// </para>
/// <para>
/// NO INSTANCE IS THE ORDINARY CASE. A desktop, a laptop driving only an external monitor, and a
/// panel whose driver publishes no brightness instance all enumerate empty. That is answered as
/// null - no panel to control - and never as an error, because there is nothing the owner could do
/// about it and nothing has gone wrong.
/// </para>
/// <para>
/// Nothing here throws. It is reached from a raw-input window procedure by way of the dispatcher,
/// and <c>System.Management</c> can throw for reasons unrelated to this app - a WMI service
/// restarting, a repository being rebuilt, a query refused. Every one of those is a brightness key
/// that quietly does nothing for that press.
/// </para>
/// <para>
/// Untested by anything in this repository, for the reason <see cref="Wmi.GigabyteWmi"/> is: it
/// needs a real provider and a real screen. <see cref="IPanelBrightness"/> is the seam, and
/// everything above it - the ladder, the controller, the policy, the overlay - is driven against
/// <c>FakePanelBrightness</c> instead. <c>VERIFY.md</c> is what confirms this half.
/// </para>
/// </remarks>
public sealed class WmiPanelBrightness : IPanelBrightness
{
    private const string ScopePath = @"root\WMI";

    /// <summary>How long the firmware is given to make the change, in seconds.</summary>
    /// <remarks>Not a delay: <c>WmiSetBrightness</c> takes a timeout, and the call returns once the
    /// change is made. One second is long enough for a panel that is awake and short enough that a
    /// panel that is not cannot hold up the thread this runs on.</remarks>
    private const uint SetTimeoutSeconds = 1;

    private readonly object _lock = new();

    /// <inheritdoc />
    public PanelBrightnessState? Read()
    {
        lock (_lock)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    ScopePath, "SELECT * FROM WmiMonitorBrightness");
                using var found = searcher.Get();

                foreach (var instance in found)
                {
                    using (instance)
                    {
                        // The first instance that answers. A machine with two controllable panels
                        // is rare enough that choosing between them would be guessing, and the
                        // Fn keys on a laptop mean its own screen.
                        if (ReadOne(instance) is { } state) return state;
                    }
                }

                // No instance: a desktop, or nothing but external monitors. Not a failure.
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public bool Set(int level)
    {
        // The panel's own byte, so a level from outside the ladder cannot become a wild cast.
        if (level < 0 || level > 255) return false;

        lock (_lock)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    ScopePath, "SELECT * FROM WmiMonitorBrightnessMethods");
                using var found = searcher.Get();

                foreach (var instance in found)
                {
                    using (instance)
                    {
                        if (instance is not ManagementObject methods) continue;
                        methods.InvokeMethod(
                            "WmiSetBrightness",
                            new object[] { SetTimeoutSeconds, (byte)level });
                        return true;
                    }
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Reads one <c>WmiMonitorBrightness</c> instance, or null if it says nothing usable.</summary>
    /// <remarks>Every property is read defensively. <c>Level</c> is documented as an ascending
    /// array of the supported levels and <c>CurrentBrightness</c> as the one in force, both as
    /// percentages; a driver that publishes the class without filling them in has to read as "no
    /// panel" rather than as a panel with an empty ladder that something later divides by.</remarks>
    private static PanelBrightnessState? ReadOne(ManagementBaseObject instance)
    {
        if (instance["Level"] is not byte[] { Length: > 0 } levels) return null;

        var ladder = new int[levels.Length];
        for (var i = 0; i < levels.Length; i++) ladder[i] = levels[i];

        var current = instance["CurrentBrightness"];
        if (current is null) return null;

        return new PanelBrightnessState(Convert.ToInt32(current, CultureInfo.InvariantCulture), ladder);
    }
}

namespace OpenAorus.Hardware.Hotkeys;

/// <summary>
/// Turns one <c>GB_WMIACPI_Event</c> <c>Data</c> value into a <see cref="HotkeyEvent"/>.
/// </summary>
/// <remarks>
/// <para>
/// The firmware has already performed these toggles by the time the event arrives - the touchpad
/// is off, the radio is on - so this channel is only ever telling the app what happened. Nothing
/// downstream writes anything back.
/// </para>
/// <para>
/// The same class also delivers a brightness event carrying a <c>Brightness</c> property rather
/// than a <c>Data</c> value. It is deliberately not decoded: Windows draws that overlay itself,
/// and a second one is the complaint this release exists to answer.
/// </para>
/// <para>
/// The four values look like a device code with one bit for the new state - <c>0xCA</c> touchpad,
/// <c>0xC2</c> Wi-Fi, <c>0x100</c> set for "on". Nothing documents that structure, so it is a
/// lookup of the four values the research lists and not a rule. Reading the bit would decode
/// values nobody has seen; a chassis that speaks a fifth one has to surface as unknown instead of
/// as a confident wrong answer.
/// </para>
/// <para>
/// Pure and total, like <see cref="RawInputDecoder"/>, and for a sharper reason: this runs on the
/// event watcher's callback thread, where an exception stops the channel and is never seen. Every
/// input that is not an exact documented match returns null - including the shapes an unobserved
/// WMI provider might hand over, which is what the <see cref="Decode(object?)"/> overload is for.
/// </para>
/// </remarks>
public static class WmiEventDecoder
{
    /// <summary>The WMI class the events arrive on, in <c>root\WMI</c>.</summary>
    public const string EventClass = "GB_WMIACPI_Event";

    /// <summary>The subscription <c>WmiEventListener</c> opens.</summary>
    public const string Query = "SELECT * FROM " + EventClass;

    /// <summary>The property carrying the value this decodes.</summary>
    /// <remarks>Unconfirmed on hardware: the research names it and nothing has checked it. A
    /// wrong name here is a subscription that runs and reports nothing - see VERIFY 8.1.</remarks>
    public const string DataProperty = "Data";

    /// <summary>Decodes one event's <c>Data</c> value.</summary>
    /// <param name="data">The value the event carried.</param>
    /// <returns>The signal, or null for a value this app does not understand.</returns>
    public static HotkeyEvent? Decode(int data) => data switch
    {
        202 => new HotkeyEvent(HotkeySignal.TouchpadDisabled),
        458 => new HotkeyEvent(HotkeySignal.TouchpadEnabled),
        450 => new HotkeyEvent(HotkeySignal.WifiEnabled),
        194 => new HotkeyEvent(HotkeySignal.WifiDisabled),
        _ => null,
    };

    /// <summary>Decodes one event's <c>Data</c> property exactly as the provider boxed it.</summary>
    /// <param name="value">The property's value, or null when the event carried no such property.</param>
    /// <returns>The signal, or null for anything this app does not understand - a missing
    /// property, a value of an unexpected type, or a number outside the documented set.</returns>
    /// <remarks>The CIM type of <c>Data</c> is unconfirmed: the recovered code converts rather
    /// than casts, so it does not say. Taking the value as an <see cref="object"/> keeps that one
    /// unknown in a pure function a test can exercise, instead of in a catch block inside the
    /// listener that no test can reach. VERIFY 8.1 is what settles the type.</remarks>
    public static HotkeyEvent? Decode(object? value) =>
        TryReadInt32(value, out var data) ? Decode(data) : null;

    private static bool TryReadInt32(object? value, out int data)
    {
        data = 0;

        // Widen first, then range-check. Convert.ToInt32 would do the same job for the integral
        // types and then throw on the ones below - an OverflowException on a callback thread.
        long widened;
        switch (value)
        {
            case int v: widened = v; break;
            case uint v: widened = v; break;
            case ushort v: widened = v; break;
            case short v: widened = v; break;
            case byte v: widened = v; break;
            case sbyte v: widened = v; break;
            case long v: widened = v; break;
            case ulong v when v <= long.MaxValue: widened = (long)v; break;

            // Null (no such property on this event), anything non-integral, and a ulong past
            // long.MaxValue all land here. Text and reals are refused rather than converted:
            // "202" or 202.0 would mean the property is not the shape this app assumed, and that
            // belongs in VERIFY as a key that does nothing, not hidden by a coercion.
            default: return false;
        }

        if (widened is < int.MinValue or > int.MaxValue) return false;

        data = (int)widened;
        return true;
    }
}

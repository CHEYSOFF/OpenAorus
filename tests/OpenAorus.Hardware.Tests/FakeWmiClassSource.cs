using OpenAorus.Hardware.Wmi.Schema;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// A machine that answers whatever a test says is on it, with no WMI provider anywhere.
/// </summary>
/// <remarks>
/// The states this has to be able to describe are the ones a real repository can be in and that
/// no unit test could otherwise reach: a class that is simply not there, a class that is there
/// but declares nothing, a namespace that will not open at all, and a read that throws outright.
/// </remarks>
internal sealed class FakeWmiClassSource : IWmiClassSource
{
    private readonly Dictionary<string, WmiClassReading> _answers = new(StringComparer.Ordinal);
    private WmiClassReading _otherwise = WmiClassReading.Absent;
    private Exception? _throws;

    /// <summary>Every class name this source was asked about, in the order it was asked.</summary>
    public List<string> Asked { get; } = new();

    /// <summary>A machine with none of the classes on it - the owner's, right now.</summary>
    public static FakeWmiClassSource Nothing() => new();

    /// <summary>A machine carrying the given schema, optionally with our marker beside it.</summary>
    public static FakeWmiClassSource Registered(WmiSchema schema, bool marker)
    {
        var source = new FakeWmiClassSource();

        foreach (var (className, methodIds) in SchemaFingerprintTests.LiveFrom(schema))
            source.Declares(className, methodIds);

        source.Declares(WmiSchemaParser.DataClass);
        source.Declares(WmiSchemaParser.EventClass);
        if (marker) source.Declares(MofWriter.MarkerClass);

        return source;
    }

    /// <summary>A machine whose namespace will not open, so every class comes back unreadable.</summary>
    public static FakeWmiClassSource NamespaceUnavailable(string reason) =>
        new() { _otherwise = WmiClassReading.Unreadable(reason) };

    /// <summary>A source whose every read throws, which the probe is not allowed to pass on.</summary>
    public static FakeWmiClassSource Throwing(Exception ex) => new() { _throws = ex };

    /// <summary>Says the class is there and declares these methods on these ids.</summary>
    public FakeWmiClassSource Declares(string className, IReadOnlyDictionary<string, int> methodIds)
    {
        _answers[className] = WmiClassReading.Present(methodIds);
        return this;
    }

    /// <summary>Says the class is there and declares no methods at all.</summary>
    public FakeWmiClassSource Declares(string className) =>
        Declares(className, new Dictionary<string, int>(StringComparer.Ordinal));

    /// <summary>Says the class is not on the machine.</summary>
    public FakeWmiClassSource Lacks(string className)
    {
        _answers[className] = WmiClassReading.Absent;
        return this;
    }

    /// <summary>Says the class could not be read, which is neither present nor absent.</summary>
    public FakeWmiClassSource CannotRead(string className, string reason)
    {
        _answers[className] = WmiClassReading.Unreadable(reason);
        return this;
    }

    public WmiClassReading Read(string className)
    {
        Asked.Add(className);
        if (_throws is not null) throw _throws;
        return _answers.TryGetValue(className, out var reading) ? reading : _otherwise;
    }
}

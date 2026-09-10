using System.Globalization;
using System.IO;
using System.Text;

namespace OpenAorus.Hardware.Wmi.Schema;

/// <summary>What one <c>GB_WMIACPI_Get</c> method answered.</summary>
/// <param name="Method">The method name, as the recovered schema spells it.</param>
/// <param name="Answered">Whether the call came back at all. False covers both a method this
/// model does not implement - which answers <c>Invalid object</c> and is part of the expected
/// shape - and a method whose class does not resolve.</param>
/// <param name="Values">The out parameters by name, empty when <paramref name="Answered"/> is
/// false. Most methods answer a single <c>Data</c>; a few answer several.</param>
/// <remarks>
/// The reason this records <em>whether</em> a method answered and not only what it said is that
/// the firmware decides which methods exist by method id. Reproducing the same set of refusals is
/// the strongest evidence available that the ids we bound are the ids the firmware knows.
/// </remarks>
public sealed record MethodReading(string Method, bool Answered, IReadOnlyDictionary<string, int> Values);

/// <summary>One slot of the controller's fan table.</summary>
/// <param name="Index">The slot, 0..14, as passed to <c>GetFanIndexValue</c>.</param>
/// <param name="Temperature">The temperature the slot switches at, in °C.</param>
/// <param name="Duty">The duty the slot runs, on the controller's own scale - not percent.</param>
/// <remarks>A slot reading <c>(0, 0)</c> is the table's terminator, not a point. The controller
/// leaves whatever was in the slots after it untouched, so those are stale and mean nothing.</remarks>
public sealed record FanSlot(int Index, int Temperature, int Duty);

/// <summary>Everything Gate A judges: one pass over the <c>Get</c> class and one over the table.</summary>
/// <param name="Methods">One entry per method that was asked, in the order it was asked.</param>
/// <param name="FanTable">The slots that could be read, in index order.</param>
public sealed record GateReadings(IReadOnlyList<MethodReading> Methods, IReadOnlyList<FanSlot> FanTable);

/// <summary>
/// The last reading taken while the Gigabyte schema still worked, parsed back off disk.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/research/dump-aorus-17g-kd-known-good.txt</c> was captured on 2026-09-06 and kept for
/// exactly one purpose: Gate A has to compare a fresh registration against something, and the only
/// honest something is a reading this machine has already produced through a schema that
/// demonstrably reached the firmware.
/// </para>
/// <para>
/// The format read here is character for character what <see cref="Diagnostics.DiagnosticsDump"/>
/// renders, and that is deliberate. The same parser reads a dump an owner exports today, so a
/// machine that fails Gate A can have its reading pasted beside the reference and diffed by hand.
/// </para>
/// <para>
/// Everything from a <c>#</c> to the end of a line is stripped before anything is parsed. The
/// research file annotates its anchors in the margin, and those annotations are prose - they must
/// never become part of a value.
/// </para>
/// </remarks>
public static class KnownGoodReading
{
    /// <summary>The line that opens the block of method answers.</summary>
    private const string MethodsHeader = "GB_WMIACPI_Get:";

    /// <summary>The prefix of the line that opens the fan table block.</summary>
    private const string FanTableHeader = "Fan table";

    private enum Block { None, Methods, FanTable }

    /// <summary>The checked-in dump, as it is embedded in this assembly.</summary>
    private const string ReferenceResource =
        "OpenAorus.Hardware.Wmi.Schema.dump-aorus-17g-kd-known-good.txt";

    private static readonly Lazy<GateReadings> s_reference = new(() => Parse(ReadEmbedded()));

    /// <summary>
    /// The reading Gate A compares a fresh registration against, read out of this assembly.
    /// </summary>
    /// <exception cref="InvalidOperationException">The dump is not embedded in this assembly.</exception>
    /// <exception cref="FormatException">The embedded dump holds no method readings.</exception>
    /// <remarks>
    /// Embedded rather than shipped beside the exe, because the app publishes as a single file and
    /// the gate runs on the owner's laptop rather than in a test. It is parsed once: Gate A is
    /// something the owner presses a button for, not something on a poll loop, but the reference is
    /// 72 methods and 15 slots and there is no reason to re-read it per press.
    /// </remarks>
    public static GateReadings Reference => s_reference.Value;

    /// <summary>Reads a diagnostics dump into the shape Gate A compares.</summary>
    /// <param name="dumpText">The whole dump, as rendered by
    /// <see cref="Diagnostics.DiagnosticsDump.Render"/>.</param>
    /// <returns>The methods and the fan table it recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dumpText"/> is null.</exception>
    /// <exception cref="FormatException">The text holds no method readings at all. A reference
    /// with nothing in it would make Gate A compare a machine against silence and pass, so this
    /// is a hard failure rather than an empty result.</exception>
    public static GateReadings Parse(string dumpText)
    {
        ArgumentNullException.ThrowIfNull(dumpText);

        var methods = new List<MethodReading>();
        var fanTable = new List<FanSlot>();
        var block = Block.None;

        foreach (var rawLine in dumpText.Split('\n'))
        {
            var line = StripComment(rawLine);
            if (line.Length == 0) continue;

            // Only the two block headers sit at column 0. Everything the dump reports is indented
            // under one of them, which is what keeps the version banner, the model line and the OS
            // line - all of which contain a colon - from reading as method answers.
            if (!line.StartsWith(" ", StringComparison.Ordinal))
            {
                block = HeaderBlock(line.Trim());
                continue;
            }

            var body = line.Trim();
            if (body.Length == 0) continue;

            switch (block)
            {
                case Block.Methods:
                    if (ParseMethod(body) is { } method) methods.Add(method);
                    break;
                case Block.FanTable:
                    if (ParseSlot(body) is { } slot) fanTable.Add(slot);
                    break;
            }
        }

        if (methods.Count == 0)
            throw new FormatException("No GB_WMIACPI_Get readings were found in the text; this is not a diagnostics dump.");

        return new GateReadings(methods, fanTable);
    }

    private static Block HeaderBlock(string header) =>
        header.Equals(MethodsHeader, StringComparison.Ordinal) ? Block.Methods
        : header.StartsWith(FanTableHeader, StringComparison.Ordinal) ? Block.FanTable
        : Block.None;

    /// <summary>Drops the margin annotations and the trailing carriage return.</summary>
    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        return (hash >= 0 ? line[..hash] : line).TrimEnd();
    }

    /// <summary>Reads <c>Name: Key=Value Key=Value</c>, or <c>Name: ERROR ...</c>.</summary>
    private static MethodReading? ParseMethod(string body)
    {
        var colon = body.IndexOf(':');
        if (colon <= 0) return null;

        var name = body[..colon].Trim();
        if (name.Length == 0) return null;

        var answer = body[(colon + 1)..].Trim();

        // "ERROR GB_WMIACPI_Get.GetBatteryCount: Invalid object" - the firmware declining a method
        // id it does not implement. Recorded, not discarded: Gate A compares the refusals too.
        if (answer.StartsWith("ERROR", StringComparison.Ordinal))
            return new MethodReading(name, Answered: false, Empty);

        return new MethodReading(name, Answered: true, ReadPairs(answer));
    }

    /// <summary>Reads <c>[i] temp=T duty=D</c>, skipping a slot that would not read.</summary>
    private static FanSlot? ParseSlot(string body)
    {
        if (!body.StartsWith("[", StringComparison.Ordinal)) return null;

        var close = body.IndexOf(']');
        if (close < 0) return null;
        if (!int.TryParse(body[1..close], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) return null;

        var pairs = ReadPairs(body[(close + 1)..].Trim());
        return pairs.TryGetValue("temp", out var temperature) && pairs.TryGetValue("duty", out var duty)
            ? new FanSlot(index, temperature, duty)
            : null;
    }

    /// <summary>Splits <c>Key=Value Key=Value</c> into numbers, ignoring anything that is not one.</summary>
    private static IReadOnlyDictionary<string, int> ReadPairs(string text)
    {
        if (text.Length == 0) return Empty;

        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = token.IndexOf('=');
            if (equals <= 0) continue;
            if (int.TryParse(token[(equals + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                values[token[..equals]] = value;
        }
        return values;
    }

    /// <summary>Reads the embedded dump out of this assembly.</summary>
    /// <remarks>A missing resource is a build that shipped without the thing Gate A judges
    /// against, and a gate with nothing to compare would pass every registration. It throws rather
    /// than returning an empty reading for exactly that reason - see <see cref="Parse"/>.</remarks>
    private static string ReadEmbedded()
    {
        using var stream = typeof(KnownGoodReading).Assembly.GetManifestResourceStream(ReferenceResource)
            ?? throw new InvalidOperationException(
                $"The known-good reading '{ReferenceResource}' is not embedded in this assembly. " +
                "Gate A has nothing to judge a registration against without it.");

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static readonly IReadOnlyDictionary<string, int> Empty =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

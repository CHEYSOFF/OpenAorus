using System.Globalization;

namespace OpenAorus.Hardware.Wmi.Schema;

/// <summary>
/// Reads the recovered <c>GB_WMIACPI</c> schema dump into a <see cref="WmiSchema"/>.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS THE ONLY PLACE 143 FIRMWARE METHOD NUMBERS ARE READ. Everything downstream - the MOF,
/// the registration, fan control - is generated from what comes out of here, and nothing
/// downstream re-reads the dump. So a method that lands on the wrong <c>WmiMethodId</c>, or a
/// parameter that lands on the wrong <c>WmiDataId</c>, is indistinguishable from the app calling a
/// different firmware method than it believes, with an argument meant for something else, on the
/// controller that governs cooling and charging.
/// </para>
/// <para>
/// WHICH IS WHY THIS PARSER IS STRICT RATHER THAN FORGIVING. A line it does not recognise is a
/// <see cref="FormatException"/>, not a skip. A method with no id, a parameter with no id or no
/// recorded direction, a type it has not been taught, a duplicate id - all stop the parse. The
/// failure a lenient parser produces is a MOF quietly missing a method nobody noticed was dropped,
/// and silence is the one failure mode that would not be caught before it reached the hardware.
/// </para>
/// <para>
/// Two invariants are asserted because the MOF writer relies on them, and neither is guaranteed by
/// the file format: within one class a <c>WmiMethodId</c> is unique, and within one method a
/// <c>WmiDataId</c> is unique. Both hold on the recovered dump. If a future dump breaks one, this
/// stops rather than emitting a MOF with two methods on one number or two parameters fighting over
/// one buffer slot.
/// </para>
/// <para>
/// The dump's shape, line by line:
/// <code>
/// =========== GB_WMIACPI_Get ===========
/// Qualifiers: Description=...; dynamic=True; guid={...}; provider=WmiProv; WMI=True
/// --- Properties ---
///   Active : Boolean  [read=True]
/// --- Methods ---
///   GetCPUFanDuty [Description=...; WmiMethodId=70; ...]
///       param Data : UInt8 [Description=Data; ID=0; out=True]
/// </code>
/// </para>
/// </remarks>
public static class WmiSchemaParser
{
    /// <summary>The class carrying every read method.</summary>
    public const string GetClass = "GB_WMIACPI_Get";

    /// <summary>The class carrying every write method. Its ids are unrelated to <see cref="GetClass"/>'s.</summary>
    public const string SetClass = "GB_WMIACPI_Set";

    /// <summary>The data class, which carries a property and no methods.</summary>
    public const string DataClass = "GB_WMIACPI_Data";

    /// <summary>The event class the hotkey listener subscribes to.</summary>
    public const string EventClass = "GB_WMIACPI_Event";

    /// <summary>How many classes a dump must describe to be one at all.</summary>
    /// <remarks>The recovered dump has exactly four. A dump with fewer has been truncated, and a
    /// truncated dump is well-formed all the way down to the cut - so the count is the only thing
    /// that catches a paste that stopped at a class boundary.</remarks>
    public const int MinimumClassCount = 4;

    private const string ClassHeaderMarker = "===========";
    private const string QualifiersPrefix = "Qualifiers:";
    private const string PropertiesMarker = "--- Properties ---";
    private const string MethodsMarker = "--- Methods ---";
    private const string ParamPrefix = "param ";

    private enum Section
    {
        /// <summary>Inside a class but before either section marker.</summary>
        None,
        Properties,
        Methods,
    }

    /// <summary>Parses a whole dump.</summary>
    /// <param name="dumpText">The dump file's text.</param>
    /// <returns>The schema it describes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dumpText"/> is null.</exception>
    /// <exception cref="FormatException">Any line could not be read, any required field was
    /// missing, any id collided, or fewer than <see cref="MinimumClassCount"/> classes were
    /// described. The message names the line number and the line.</exception>
    public static WmiSchema Parse(string dumpText)
    {
        // Null is this app calling itself wrongly rather than anything about the file, so it is
        // told apart from every malformed dump below.
        ArgumentNullException.ThrowIfNull(dumpText);

        var classes = new List<WmiSchemaClass>();
        var seenClassNames = new HashSet<string>(StringComparer.Ordinal);

        ClassBuilder? current = null;
        var section = Section.None;

        var lines = dumpText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var raw = StripComment(lines[i]).TrimEnd();
            if (raw.Trim().Length == 0) continue;

            if (IsClassHeader(raw))
            {
                if (current is not null) classes.Add(current.Build());

                var name = ReadClassName(raw, lineNumber);
                if (!seenClassNames.Add(name))
                    throw Bad(lineNumber, raw, $"class '{name}' is declared twice");

                current = new ClassBuilder(name, lineNumber);
                section = Section.None;
                continue;
            }

            if (current is null)
                throw Bad(lineNumber, raw, "content before the first '=========== ClassName ===========' header");

            if (raw.StartsWith(QualifiersPrefix, StringComparison.Ordinal))
            {
                if (section != Section.None)
                    throw Bad(lineNumber, raw, "a Qualifiers line after a section marker");
                current.ApplyQualifiers(raw[QualifiersPrefix.Length..], lineNumber, raw);
                continue;
            }

            var trimmed = raw.Trim();

            if (string.Equals(trimmed, PropertiesMarker, StringComparison.Ordinal))
            {
                section = Section.Properties;
                continue;
            }

            if (string.Equals(trimmed, MethodsMarker, StringComparison.Ordinal))
            {
                section = Section.Methods;
                continue;
            }

            switch (section)
            {
                case Section.Properties:
                    current.AddProperty(ParseProperty(trimmed, lineNumber, raw));
                    break;

                case Section.Methods when trimmed.StartsWith(ParamPrefix, StringComparison.Ordinal):
                    current.AddParam(ParseParam(trimmed, lineNumber, raw), lineNumber, raw);
                    break;

                case Section.Methods:
                    current.StartMethod(ParseMethodHeader(trimmed, lineNumber, raw), lineNumber, raw);
                    break;

                default:
                    throw Bad(lineNumber, raw, "a line before this class declared '--- Properties ---' or '--- Methods ---'");
            }
        }

        if (current is not null) classes.Add(current.Build());

        if (classes.Count < MinimumClassCount)
        {
            throw new FormatException(
                $"The dump describes {classes.Count} class(es); a complete one describes at least " +
                $"{MinimumClassCount}. This dump is truncated, and a truncated schema would produce a " +
                "MOF that is quietly missing whatever the cut removed.");
        }

        return new WmiSchema(classes);
    }

    /// <summary>
    /// Removes a <c>#</c> comment, counting only a <c>#</c> that falls outside a qualifier list.
    /// </summary>
    /// <remarks>
    /// Cutting at the first <c>#</c> anywhere on the line would be simpler and would silently
    /// truncate any Description that ever contained one - and a Description is a value the MOF
    /// prints verbatim. The recovered dump has no <c>#</c> at all, so nothing here is exercised by
    /// it; this is the reading that cannot lose data if a future dump does.
    /// </remarks>
    private static string StripComment(string line)
    {
        var depth = 0;
        for (var i = 0; i < line.Length; i++)
        {
            switch (line[i])
            {
                case '[': depth++; break;
                case ']': if (depth > 0) depth--; break;
                case '#' when depth == 0: return line[..i];
            }
        }

        return line;
    }

    private static bool IsClassHeader(string line) =>
        line.TrimStart().StartsWith(ClassHeaderMarker, StringComparison.Ordinal);

    private static string ReadClassName(string line, int lineNumber)
    {
        var name = line.Trim().Trim('=').Trim();
        if (name.Length == 0 || name.Any(char.IsWhiteSpace))
            throw Bad(lineNumber, line, "a class header with no single name between the '=' runs");
        return name;
    }

    /// <summary>Splits <c>head [k=v; k=v]</c> into the head and the parsed qualifiers.</summary>
    private static (string Head, Qualifiers Quals) SplitLine(string line, int lineNumber, string raw)
    {
        var open = line.IndexOf('[', StringComparison.Ordinal);
        var close = line.LastIndexOf(']');

        // The ']' must close the line. Anything else means the line was cut short, and a line cut
        // short carries no guarantee that the qualifiers on it are all of them.
        if (open < 0 || close != line.Length - 1 || close < open)
            throw Bad(lineNumber, raw, "no '[...]' qualifier list closing the line");

        return (line[..open].Trim(), Qualifiers.Parse(line[(open + 1)..close], lineNumber, raw));
    }

    private static WmiSchemaProperty ParseProperty(string line, int lineNumber, string raw)
    {
        var (head, quals) = SplitLine(line, lineNumber, raw);
        var (name, type) = SplitNameAndType(head, lineNumber, raw);

        return new WmiSchemaProperty(
            name,
            type,
            quals.IsTrue("key"),
            quals.IsTrue("read"),
            quals.IsTrue("write"),
            quals.OptionalInt("WmiDataId", lineNumber, raw),
            quals.OptionalInt("MAX", lineNumber, raw),
            quals.Text("Description"));
    }

    private static WmiSchemaMethod ParseMethodHeader(string line, int lineNumber, string raw)
    {
        var (name, quals) = SplitLine(line, lineNumber, raw);

        if (name.Length == 0 || name.Any(char.IsWhiteSpace) || name.Contains(':', StringComparison.Ordinal))
            throw Bad(lineNumber, raw, $"'{name}' is not a method name");

        var id = quals.RequiredInt("WmiMethodId", lineNumber, raw,
            $"method '{name}' carries no WmiMethodId, and the id is the number the firmware dispatches on");

        return new WmiSchemaMethod(name, id, quals.Text("Description"), Array.Empty<WmiSchemaParam>());
    }

    private static WmiSchemaParam ParseParam(string line, int lineNumber, string raw)
    {
        var (head, quals) = SplitLine(line, lineNumber, raw);
        var (name, type) = SplitNameAndType(head[ParamPrefix.Length..], lineNumber, raw);

        var isIn = quals.IsTrue("in");
        var isOut = quals.IsTrue("out");
        if (isIn == isOut)
        {
            throw Bad(lineNumber, raw, isIn
                ? $"parameter '{name}' claims both directions"
                : $"parameter '{name}' records no direction, and a direction that was not recorded does not get a guess");
        }

        var dataId = quals.RequiredInt("ID", lineNumber, raw,
            $"parameter '{name}' carries no ID, and the ID is its slot in the method's buffer");

        return new WmiSchemaParam(name, type, isIn ? WmiParamDirection.In : WmiParamDirection.Out, dataId,
            quals.Text("Description"));
    }

    private static (string Name, WmiParamType Type) SplitNameAndType(string head, int lineNumber, string raw)
    {
        var colon = head.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0) throw Bad(lineNumber, raw, $"'{head.Trim()}' has no ' : Type'");

        var name = head[..colon].Trim();
        var typeText = head[(colon + 1)..].Trim();

        if (name.Length == 0 || name.Any(char.IsWhiteSpace))
            throw Bad(lineNumber, raw, $"'{name}' is not a name");

        return (name, ParseType(typeText, lineNumber, raw));
    }

    /// <summary>Maps a CIM type name to the enum, refusing anything it was not taught.</summary>
    /// <remarks>Guessing a width here is how a one-byte value ends up written into a two-byte
    /// slot, so an unrecognised type stops the parse rather than falling back to a default.</remarks>
    private static WmiParamType ParseType(string text, int lineNumber, string raw) => text switch
    {
        "UInt8" => WmiParamType.UInt8,
        "UInt16" => WmiParamType.UInt16,
        "UInt32" => WmiParamType.UInt32,
        "UInt8Array" => WmiParamType.UInt8Array,
        "UInt64" => WmiParamType.UInt64,
        "Boolean" => WmiParamType.Boolean,
        "String" => WmiParamType.String,
        _ => throw Bad(lineNumber, raw, $"'{text}' is not a type this parser knows"),
    };

    private static FormatException Bad(int lineNumber, string line, string what) =>
        new($"Line {lineNumber} of the schema dump: {what}. Line: \"{line.Trim()}\"");

    /// <summary>The <c>k=v; k=v</c> list inside one line's brackets.</summary>
    private sealed class Qualifiers
    {
        private readonly Dictionary<string, string> _values;

        private Qualifiers(Dictionary<string, string> values) => _values = values;

        /// <summary>
        /// Splits a qualifier list. Every entry must be <c>key=value</c>, and no key may repeat.
        /// </summary>
        /// <remarks>
        /// Splitting on ';' means a value that itself contained one would break into a fragment
        /// with no '=' - which throws here rather than silently keeping half a description. No
        /// value in the recovered dump contains a semicolon.
        /// </remarks>
        public static Qualifiers Parse(string inside, int lineNumber, string raw)
        {
            // Case-insensitive because the dump mixes conventions freely - "guid", "WmiMethodId",
            // "ID", "MAX", "in" - and no two qualifiers differ only in case.
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var part in inside.Split(';'))
            {
                var entry = part.Trim();
                if (entry.Length == 0) continue;

                var eq = entry.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0) throw Bad(lineNumber, raw, $"qualifier '{entry}' is not 'key=value'");

                var key = entry[..eq].Trim();
                if (!values.TryAdd(key, entry[(eq + 1)..].Trim()))
                    throw Bad(lineNumber, raw, $"qualifier '{key}' appears twice");
            }

            return new Qualifiers(values);
        }

        public bool Has(string key) => _values.ContainsKey(key);

        public string Text(string key) => _values.TryGetValue(key, out var v) ? v : string.Empty;

        /// <summary>Whether a boolean qualifier is present and set.</summary>
        /// <remarks>The dump only ever writes <c>=True</c>. Anything else reads as absent, which
        /// is right for the flags this is used on: a parameter's direction is checked separately
        /// and refuses to be inferred from an absent qualifier.</remarks>
        public bool IsTrue(string key) =>
            _values.TryGetValue(key, out var v) && string.Equals(v, "True", StringComparison.OrdinalIgnoreCase);

        public int RequiredInt(string key, int lineNumber, string raw, string missing)
        {
            if (!Has(key)) throw Bad(lineNumber, raw, missing);
            return ReadInt(key, lineNumber, raw);
        }

        public int? OptionalInt(string key, int lineNumber, string raw) =>
            Has(key) ? ReadInt(key, lineNumber, raw) : null;

        private int ReadInt(string key, int lineNumber, string raw)
        {
            var text = _values[key];
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
                throw Bad(lineNumber, raw, $"'{key}={text}' is not a number this parser will use as an id");
            return value;
        }
    }

    /// <summary>Accumulates one class as its lines arrive, checking the invariants as it goes.</summary>
    private sealed class ClassBuilder
    {
        private readonly string _name;
        private readonly int _headerLine;
        private readonly List<WmiSchemaProperty> _properties = new();
        private readonly List<WmiSchemaMethod> _methods = new();
        private readonly HashSet<int> _methodIds = new();
        private readonly HashSet<string> _methodNames = new(StringComparer.Ordinal);

        private WmiSchemaMethod? _method;
        private List<WmiSchemaParam> _params = new();
        private string? _guid;
        private string _description = string.Empty;

        public ClassBuilder(string name, int headerLine)
        {
            _name = name;
            _headerLine = headerLine;
        }

        public void ApplyQualifiers(string inside, int lineNumber, string raw)
        {
            var quals = Qualifiers.Parse(inside, lineNumber, raw);
            _guid = quals.Has("guid") ? quals.Text("guid") : null;
            _description = quals.Text("Description");
        }

        public void AddProperty(WmiSchemaProperty property) => _properties.Add(property);

        public void StartMethod(WmiSchemaMethod method, int lineNumber, string raw)
        {
            CloseMethod();

            if (!_methodIds.Add(method.MethodId))
            {
                throw Bad(lineNumber, raw,
                    $"class '{_name}' already has a method on WmiMethodId {method.MethodId}, and two " +
                    "methods on one id cannot both be dispatched");
            }

            if (!_methodNames.Add(method.Name))
                throw Bad(lineNumber, raw, $"class '{_name}' already has a method named '{method.Name}'");

            _method = method;
            _params = new List<WmiSchemaParam>();
        }

        public void AddParam(WmiSchemaParam param, int lineNumber, string raw)
        {
            if (_method is null)
                throw Bad(lineNumber, raw, "a parameter line before any method line");

            foreach (var existing in _params)
            {
                if (existing.DataId == param.DataId)
                {
                    throw Bad(lineNumber, raw,
                        $"method '{_method.Name}' already has a parameter on ID {param.DataId}, and two " +
                        "parameters cannot share one buffer slot");
                }

                if (string.Equals(existing.Name, param.Name, StringComparison.Ordinal))
                    throw Bad(lineNumber, raw, $"method '{_method.Name}' already has a parameter named '{param.Name}'");
            }

            _params.Add(param);
        }

        public WmiSchemaClass Build()
        {
            CloseMethod();

            if (string.IsNullOrEmpty(_guid))
            {
                throw Bad(_headerLine, $"=========== {_name} ===========",
                    $"class '{_name}' carries no guid qualifier, and the guid is what binds the class to " +
                    "an ACPI data block");
            }

            return new WmiSchemaClass(_name, _guid, _description, _properties, _methods);
        }

        private void CloseMethod()
        {
            if (_method is null) return;
            _methods.Add(_method with { Parameters = _params });
            _method = null;
        }
    }
}

using System.Globalization;
using System.Text;

namespace OpenAorus.Hardware.Wmi.Schema;

/// <summary>
/// Prints a <see cref="WmiSchema"/> as the MOF text Windows' own compiler registers.
/// </summary>
/// <remarks>
/// <para>
/// THIS WRITER REPRODUCES; IT DOES NOT INTERPRET. A MOF maps names to numbers, and after
/// <c>mofcomp</c> has run those numbers are rows in a WMI repository that nothing reads back. A
/// <c>WmiMethodId</c> that arrived here wrong, or a <c>WmiDataId</c> that got renumbered on the
/// way, is the app calling a different firmware method than it believes, with an argument meant
/// for something else, on the controller that governs cooling and charging.
/// </para>
/// <para>
/// So every method the parser found is printed, on the id the parser read, with the name the dump
/// recorded. In particular <c>Temperture</c> - Gigabyte's spelling, missing an "a", with a
/// correctly spelled description beside it - is printed as recovered. That name is what
/// <c>System.Management</c> binds a parameter by; correcting it would rename a buffer slot.
/// </para>
/// <para>
/// Ordering. Classes and methods come out in the order the dump listed them, so a diff against
/// the research file reads straight down. Parameters come out in <see cref="WmiSchemaParam.DataId"/>
/// order instead, which is <em>not</em> the order the dump lists them in - the dump shows WMI's
/// enumeration of a parameter collection (in-parameters, then out, alphabetical within each), so
/// <c>GetDeepFan</c> lists slot 5 before slot 0. Declaration order does not affect binding and
/// <c>WmiDataId</c> does, so sorting by id costs nothing and makes the printed signature readable
/// as what it is: the layout of the ACPI buffer.
/// </para>
/// <para>
/// Determinism. The output is byte-for-byte the same for the same input, on any machine, in any
/// culture: <c>\r\n</c> written explicitly rather than <see cref="StringBuilder.AppendLine()"/>,
/// numbers formatted invariantly, and no timestamp, path or machine name anywhere. Task 3's drift
/// test compares this against a checked-in file, so anything that varied between two runs would
/// turn every build into a diff.
/// </para>
/// <para>
/// <c>Locale</c> is deliberately not printed. The dump records <c>Locale=MS\0x409</c> on all four
/// classes; that is a localisation qualifier pointing at an amended-namespace resource, and
/// printing it without the matching <c>#pragma amendment</c> block would declare a translation
/// that does not exist. It binds nothing - <c>guid</c>, <c>WmiMethodId</c> and <c>WmiDataId</c>
/// do - so it is left out rather than half-copied.
/// </para>
/// <para>
/// <c>#pragma autorecover</c> is deliberately not printed either. It would add this MOF to the
/// machine-wide list WMI replays when its repository is rebuilt, which the Remove button cannot
/// cleanly take us back out of; reversibility is what this whole feature is modelled on, and a
/// repository rebuild that drops the classes is detected at the next startup instead. It is also
/// mutually exclusive with <c>mofcomp -check</c>, which is how the generated text is validated.
/// </para>
/// </remarks>
public static class MofWriter
{
    /// <summary>The WMI namespace both files target.</summary>
    public const string Namespace = @"root\WMI";

    /// <summary>The class whose single instance records that a registration is ours.</summary>
    public const string MarkerClass = "OpenAorus_SchemaMarker";

    /// <summary>The key of that single instance.</summary>
    public const string MarkerInstanceId = "OpenAorus";

    /// <summary>
    /// The base class <c>GB_WMIACPI_Event</c> derives from.
    /// </summary>
    /// <remarks>
    /// INFERRED, NOT RECOVERED. The dump records no class's superclass. It does list
    /// <c>SECURITY_DESCRIPTOR</c> and <c>TIME_CREATED</c> among the event class's properties, and
    /// those two are not Gigabyte's - they are the fingerprint of WMI's own event base - so the
    /// dump is showing inherited members and the original MOF said <c>: WMIEvent</c>. If a machine
    /// ever rejects that, <c>__ExtrinsicEvent</c> is the next thing to try, and this constant is
    /// the whole change.
    /// </remarks>
    public const string EventBaseClass = "WMIEvent";

    private const string Nl = "\r\n";
    private const string Indent = "    ";

    /// <summary>The local WMI path form <c>mofcomp</c>'s namespace pragma accepts.</summary>
    /// <remarks>The bare <c>root\WMI</c> form is rejected outright with
    /// <c>SYNTAX 0X80044013: Invalid namespace path syntax</c>; only the <c>\\.\</c>-prefixed
    /// path parses.</remarks>
    private const string LocalNamespacePath = @"\\.\" + Namespace;

    private const string GeneratedBanner =
        "// GENERATED FILE - DO NOT EDIT." + Nl +
        "// Printed by OpenAorus.Hardware.Wmi.Schema.MofWriter from" + Nl +
        "// docs/research/gb-wmiacpi-methods-aorus-17g-kd.txt, the schema recovered from an AORUS" + Nl +
        "// 17G KD while Gigabyte Control Center was still installed." + Nl;

    /// <summary>Prints the MOF that declares the recovered classes and marks the registration.</summary>
    /// <param name="schema">The recovered schema. Every class, method and parameter in it is
    /// printed; nothing is added, dropped or renamed.</param>
    /// <param name="fingerprint">The schema fingerprint to record on the marker instance. Task 4
    /// recomputes the same string from the live classes, so a machine can be checked two
    /// independent ways and neither depends on the other having survived.</param>
    /// <returns>The MOF text, with <c>\r\n</c> line endings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> or
    /// <paramref name="fingerprint"/> is null.</exception>
    /// <exception cref="NotSupportedException">A property or parameter carries a CIM type this
    /// writer has not been taught to print. Thrown rather than guessed at, because a width
    /// invented here is how a one-byte value ends up written into a two-byte slot.</exception>
    public static string WriteInstall(WmiSchema schema, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(fingerprint);

        var mof = new StringBuilder();

        mof.Append(GeneratedBanner)
           .Append("//").Append(Nl)
           .Append("// Every number below is a firmware method id or an ACPI buffer offset. None of them was").Append(Nl)
           .Append("// typed by hand. MofDriftTests reprints this file on every test run and fails if it and").Append(Nl)
           .Append("// the research file disagree, so editing this file directly will turn the suite red").Append(Nl)
           .Append("// rather than change anything.").Append(Nl)
           .Append(Nl)
           .Append(NamespacePragma()).Append(Nl);

        foreach (var declared in schema.Classes)
        {
            mof.Append(Nl);
            AppendClass(mof, declared);
        }

        mof.Append(Nl);
        AppendMarker(mof, fingerprint);

        return mof.ToString();
    }

    /// <summary>Prints the MOF that takes the registration back out again.</summary>
    /// <param name="schema">The recovered schema, read only for the names of the classes to
    /// delete.</param>
    /// <returns>The MOF text, with <c>\r\n</c> line endings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    /// <remarks>
    /// It declares nothing and writes no instances - deleting a class is the whole of it. Every
    /// deletion is <c>NOFAIL</c>, which makes the file idempotent and therefore usable as the
    /// rollback for an install that may or may not have created anything. The marker is deleted
    /// last, so a removal that stops part-way leaves the marker behind and the machine reads as
    /// <c>Partial</c> - which the installer knows how to clean up - rather than as <c>Foreign</c>,
    /// which it refuses to touch.
    /// </remarks>
    public static string WriteRemove(WmiSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var mof = new StringBuilder();

        mof.Append(GeneratedBanner)
           .Append("//").Append(Nl)
           .Append("// Undoes what the install file did, and nothing else: it declares nothing and writes no").Append(Nl)
           .Append("// data. NOFAIL makes every deletion idempotent, which is what lets the installer run").Append(Nl)
           .Append("// this as a rollback after a compile that may or may not have created anything.").Append(Nl)
           .Append(Nl)
           .Append(NamespacePragma()).Append(Nl)
           .Append(Nl);

        foreach (var declared in schema.Classes)
            AppendDeleteClass(mof, declared.Name);

        // The marker goes last for the same reason it is written last: a removal that stops
        // half way through must leave the machine readable as ours.
        AppendDeleteClass(mof, MarkerClass);

        return mof.ToString();
    }

    private static string NamespacePragma() => $"#pragma namespace({Quoted(LocalNamespacePath)})";

    private static void AppendDeleteClass(StringBuilder mof, string className) =>
        mof.Append("#pragma deleteclass(").Append(Quoted(className)).Append(", NOFAIL)").Append(Nl);

    private static void AppendClass(StringBuilder mof, WmiSchemaClass declared)
    {
        var qualifiers = new List<string>
        {
            "dynamic",
            $"provider({Quoted("WmiProv")})",
            "WMI",
            $"guid({Quoted(declared.Guid)})",
        };

        // The Description is split onto its own continuation line because it is the only
        // qualifier whose length is not bounded by the format.
        mof.Append('[').Append(string.Join(", ", qualifiers));
        if (declared.Description.Length > 0)
            mof.Append(',').Append(Nl).Append(' ').Append("Description(").Append(Quoted(declared.Description)).Append(')');
        mof.Append(']').Append(Nl);

        mof.Append("class ").Append(declared.Name);
        if (string.Equals(declared.Name, WmiSchemaParser.EventClass, StringComparison.Ordinal))
            mof.Append(" : ").Append(EventBaseClass);
        mof.Append(Nl).Append('{').Append(Nl);

        foreach (var property in declared.Properties)
        {
            if (IsInherited(property)) continue;
            AppendProperty(mof, property);
        }

        foreach (var method in declared.Methods)
        {
            mof.Append(Nl);
            AppendMethod(mof, method);
        }

        mof.Append("};").Append(Nl);
    }

    /// <summary>
    /// Whether a recovered property was enumerated off a base class rather than declared.
    /// </summary>
    /// <remarks>
    /// The two the dump shows - <c>SECURITY_DESCRIPTOR</c> and <c>TIME_CREATED</c> on the event
    /// class - are the only lines in the whole file carrying an empty qualifier list, and they
    /// are WMI's own. Rather than name them, this asks the question their emptiness answers: a
    /// property that is not a key, is neither readable nor writable, has no <c>WmiDataId</c>, no
    /// <c>MAX</c> and no description carries nothing to declare. Redeclaring one on a derived
    /// class is a redefinition of an inherited member, which is why this is a skip and not a
    /// print.
    /// </remarks>
    private static bool IsInherited(WmiSchemaProperty property) =>
        !property.IsKey && !property.CanRead && !property.CanWrite &&
        property.DataId is null && property.Max is null && property.Description.Length == 0;

    private static void AppendProperty(StringBuilder mof, WmiSchemaProperty property)
    {
        var qualifiers = new List<string>();
        if (property.IsKey) qualifiers.Add("key");
        if (property.CanRead) qualifiers.Add("read");
        if (property.CanWrite) qualifiers.Add("write");
        if (property.DataId is { } dataId) qualifiers.Add($"WmiDataId({Number(dataId)})");
        if (property.Max is { } max) qualifiers.Add($"MAX({Number(max)})");
        if (property.Description.Length > 0) qualifiers.Add($"Description({Quoted(property.Description)})");

        mof.Append(Indent);
        if (qualifiers.Count > 0) mof.Append('[').Append(string.Join(", ", qualifiers)).Append("] ");
        mof.Append(CimType(property.Type, property.Name)).Append(' ').Append(property.Name)
           .Append(ArraySuffix(property.Type)).Append(';').Append(Nl);
    }

    private static void AppendMethod(StringBuilder mof, WmiSchemaMethod method)
    {
        // Implemented, read and write are constant across all 143 recovered methods and the
        // parser does not carry them, so they are printed rather than looked up. WmiMethodId
        // leads, because it is the only number on the line.
        mof.Append(Indent).Append("[WmiMethodId(").Append(Number(method.MethodId))
           .Append("), Implemented, read, write");
        if (method.Description.Length > 0)
            mof.Append(", Description(").Append(Quoted(method.Description)).Append(')');
        mof.Append(']').Append(Nl);

        // Sorted by DataId, not left in the dump's alphabetical-within-direction order. See the
        // ordering note on the class.
        var parameters = method.Parameters.OrderBy(p => p.DataId).Select(FormatParameter);

        mof.Append(Indent).Append("void ").Append(method.Name).Append('(')
           .Append(string.Join(", ", parameters)).Append(");").Append(Nl);
    }

    private static string FormatParameter(WmiSchemaParam parameter)
    {
        var direction = parameter.Direction == WmiParamDirection.In ? "in" : "out";

        var qualifiers = new StringBuilder()
            .Append(direction)
            .Append(", WmiDataId(").Append(Number(parameter.DataId)).Append(')');
        if (parameter.Description.Length > 0)
            qualifiers.Append(", Description(").Append(Quoted(parameter.Description)).Append(')');

        return $"[{qualifiers}] {CimType(parameter.Type, parameter.Name)} {parameter.Name}{ArraySuffix(parameter.Type)}";
    }

    private static void AppendMarker(StringBuilder mof, string fingerprint)
    {
        mof.Append('[').Append("Description(").Append(Quoted(
                "Written by OpenAorus when it registered the classes above. Its presence is how a later " +
                "run tells our registration apart from a vendor one, and the fingerprint is the same " +
                "string recomputed from the live classes."))
           .Append(")]").Append(Nl)
           .Append("class ").Append(MarkerClass).Append(Nl)
           .Append('{').Append(Nl)
           .Append(Indent).Append("[key] string Id;").Append(Nl)
           .Append(Indent).Append("string Fingerprint;").Append(Nl)
           .Append("};").Append(Nl)
           .Append(Nl)
           .Append("instance of ").Append(MarkerClass).Append(Nl)
           .Append('{').Append(Nl)
           .Append(Indent).Append("Id = ").Append(Quoted(MarkerInstanceId)).Append(';').Append(Nl)
           .Append(Indent).Append("Fingerprint = ").Append(Quoted(fingerprint)).Append(';').Append(Nl)
           .Append("};").Append(Nl);
    }

    /// <summary>Maps a recovered CIM type to its MOF spelling.</summary>
    /// <remarks>A type this has not been taught stops the print. The alternative - falling back
    /// to some default width - declares a buffer slot of a size the firmware does not use, and
    /// the mismatch would not show up until something read back as noise.</remarks>
    private static string CimType(WmiParamType type, string member) => type switch
    {
        WmiParamType.UInt8 or WmiParamType.UInt8Array => "uint8",
        WmiParamType.UInt16 => "uint16",
        WmiParamType.UInt32 => "uint32",
        WmiParamType.Boolean => "boolean",
        WmiParamType.String => "string",
        _ => throw new NotSupportedException(
            $"'{member}' has CIM type {type}, which this writer has not been taught to print. A width " +
            "guessed here would declare a buffer slot the firmware does not use."),
    };

    private static string ArraySuffix(WmiParamType type) => type == WmiParamType.UInt8Array ? "[]" : string.Empty;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Wraps a value as a MOF string literal, escaping what MOF requires escaped.</summary>
    /// <remarks>No recovered description contains a quote or a backslash, so nothing in the
    /// generated file exercises this today. An unescaped quote would close the literal early and
    /// take the rest of the qualifier list with it, which mofcomp would either reject or - worse -
    /// read as something else.</remarks>
    private static string Quoted(string value)
    {
        var quoted = new StringBuilder(value.Length + 2).Append('"');

        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': quoted.Append(@"\\"); break;
                case '"': quoted.Append("\\\""); break;
                case '\r': quoted.Append("\\r"); break;
                case '\n': quoted.Append("\\n"); break;
                case '\t': quoted.Append("\\t"); break;
                default: quoted.Append(ch); break;
            }
        }

        return quoted.Append('"').ToString();
    }
}

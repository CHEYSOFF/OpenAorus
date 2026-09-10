using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenAorus.Hardware.Wmi.Schema;

/// <summary>
/// Reduces a schema to one short string that changes when, and only when, something that binds
/// changes.
/// </summary>
/// <remarks>
/// <para>
/// WHAT THIS IS FOR. A gate result that was earned once is remembered, and a remembered pass is
/// worth something only for as long as the schema it was earned against is still the schema on
/// the machine. Recomputing this from live WMI is the cheap way to say so: it reads class
/// metadata and invokes no firmware method at all.
/// </para>
/// <para>
/// THE TWO RENDERINGS, AND WHY THERE ARE TWO.
/// </para>
/// <para>
/// <see cref="Of"/> is the <em>binding</em> fingerprint: for each method-bearing class in name
/// order, the class name, then every method as <c>Name=WmiMethodId</c> in name order. Nothing
/// else - not descriptions, not the guid, not a parameter. That restraint is what makes
/// <see cref="OfLive"/> possible: the same string is computable from a live class's method
/// metadata, which carries names and ids and nothing this side cares about. The guid is left out
/// deliberately and cannot hide a fault anyway - WMI will not hold two classes on one guid, so a
/// class living under our name is bound to the block our MOF named.
/// </para>
/// <para>
/// <see cref="OfSignatures"/> adds every parameter's <c>WmiDataId</c>, name, CIM type and
/// direction. It has no live counterpart on purpose: reading 249 parameter qualifiers off WMI is
/// not a cheap startup check. Its job is to be pinned against a literal in the test suite, where
/// it catches the one class of fault the binding fingerprint is blind to - a parameter that
/// changed direction or width while its method stayed on its own id, which would print a write
/// that carries no value.
/// </para>
/// <para>
/// ORDER. Classes and methods are sorted by ordinal name, parameters by <c>WmiDataId</c>. None of
/// those is the order anything arrives in: the research dump lists methods as the original MOF
/// declared them and parameters as WMI enumerates a collection - in-parameters then out,
/// alphabetical within each group, so <c>GetDeepFan</c> lists slot 5 before slot 0 and nine
/// methods differ. WMI hands back live methods in repository order, which is a third thing again.
/// A fingerprint that moved with any of those would go off on a machine nothing had happened to.
/// </para>
/// </remarks>
public static class SchemaFingerprint
{
    /// <summary>
    /// Written in place of a class's methods when the class is there but declares none.
    /// </summary>
    /// <remarks>
    /// A repository that half-rebuilt leaves classes present and empty. That is a different state
    /// from the class being gone - one is a registration to repair, the other a registration to
    /// make - and rendering them as the same empty run of lines would have the app say the wrong
    /// thing about a machine it is refusing to write to.
    /// </remarks>
    public const string Missing = "(absent)";

    /// <summary>Fingerprints what the recovered schema binds: every method name and its id.</summary>
    /// <param name="schema">The parsed schema. Classes carrying no methods contribute nothing.</param>
    /// <returns>64 lowercase hex characters.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    public static string Of(WmiSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        return Hash(Render(schema, withParameters: false));
    }

    /// <summary>
    /// Fingerprints the same mapping plus every parameter's slot, name, type and direction.
    /// </summary>
    /// <param name="schema">The parsed schema. Classes carrying no methods contribute nothing.</param>
    /// <returns>64 lowercase hex characters, never equal to this schema's <see cref="Of"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    public static string OfSignatures(WmiSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        return Hash(Render(schema, withParameters: true));
    }

    /// <summary>
    /// Fingerprints what a live machine binds, from the method metadata read off its classes.
    /// </summary>
    /// <param name="liveMethodIds">Class name to that class's method names and
    /// <c>WmiMethodId</c>s. A class WMI could not find is simply absent from the map; a class that
    /// is present but enumerated no methods maps to an empty one, and the two render
    /// differently.</param>
    /// <returns>64 lowercase hex characters, equal to <see cref="Of"/> of the schema this machine
    /// would have to be carrying for the two to be the same schema.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="liveMethodIds"/> is null.</exception>
    /// <remarks>This takes a dictionary rather than reading WMI so that it is a pure function and
    /// the comparison is tested without a WMI provider anywhere.</remarks>
    public static string OfLive(IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> liveMethodIds)
    {
        ArgumentNullException.ThrowIfNull(liveMethodIds);

        var text = new StringBuilder();

        foreach (var className in liveMethodIds.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            text.Append(className).Append('\n');

            var methods = liveMethodIds[className];
            if (methods.Count == 0)
            {
                text.Append(Missing).Append('\n');
                continue;
            }

            foreach (var name in methods.Keys.OrderBy(n => n, StringComparer.Ordinal))
                text.Append(name).Append('=').Append(Number(methods[name])).Append('\n');
        }

        return Hash(text.ToString());
    }

    /// <summary>Renders one method the way <see cref="OfSignatures"/> renders it.</summary>
    /// <param name="method">The method.</param>
    /// <returns><c>Name=Id(slot:name:type:direction, ...)</c>, parameters in
    /// <c>WmiDataId</c> order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="method"/> is null.</exception>
    /// <remarks>Exposed so that a test pinning one named method against a literal compares the
    /// same rendering the fingerprint is built from, rather than a second spelling of it that
    /// could drift.</remarks>
    public static string SignatureOf(WmiSchemaMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);

        var text = new StringBuilder();
        AppendMethod(text, method, withParameters: true);
        return text.ToString(0, text.Length - 1); // Without the line break the rendering ends on.
    }

    private static string Render(WmiSchema schema, bool withParameters)
    {
        var text = new StringBuilder();

        foreach (var declared in schema.Classes
                     .Where(c => c.Methods.Count > 0)
                     .OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            text.Append(declared.Name).Append('\n');

            foreach (var method in declared.Methods.OrderBy(m => m.Name, StringComparer.Ordinal))
                AppendMethod(text, method, withParameters);
        }

        return text.ToString();
    }

    private static void AppendMethod(StringBuilder text, WmiSchemaMethod method, bool withParameters)
    {
        text.Append(method.Name).Append('=').Append(Number(method.MethodId));

        if (withParameters)
        {
            text.Append('(');

            var first = true;
            foreach (var parameter in method.Parameters.OrderBy(p => p.DataId))
            {
                if (!first) text.Append(',');
                first = false;

                text.Append(Number(parameter.DataId)).Append(':')
                    .Append(parameter.Name).Append(':')
                    .Append(parameter.Type).Append(':')
                    .Append(parameter.Direction == WmiParamDirection.In ? "in" : "out");
            }

            text.Append(')');
        }

        text.Append('\n');
    }

    /// <summary>SHA-256 of the UTF-8 bytes, as lowercase hex.</summary>
    /// <remarks>Lowercase hex rather than base64 so the value survives a settings file, a log
    /// line and a command line unchanged, and can be compared by eye.</remarks>
    private static string Hash(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

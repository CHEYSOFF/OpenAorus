namespace OpenAorus.Hardware.Wmi.Schema;

/// <summary>Which way a method parameter travels.</summary>
/// <remarks>There is no third value on purpose. A direction the dump did not record is not a
/// parameter this app is willing to describe to the firmware, so <see cref="WmiSchemaParser"/>
/// refuses the line rather than defaulting it.</remarks>
public enum WmiParamDirection
{
    /// <summary>The caller supplies it.</summary>
    In,

    /// <summary>The firmware fills it in.</summary>
    Out,
}

/// <summary>The CIM types the recovered dump uses, for parameters and for properties alike.</summary>
/// <remarks>
/// The dump's methods use only <see cref="UInt8"/> and <see cref="UInt16"/>; the rest appear on
/// class properties. All of them are named here so that a property is never silently dropped for
/// having a type the enum could not hold - the MOF writer is what decides which of these it is
/// willing to print.
/// </remarks>
public enum WmiParamType
{
    /// <summary>One byte.</summary>
    UInt8,

    /// <summary>Two bytes.</summary>
    UInt16,

    /// <summary>Four bytes.</summary>
    UInt32,

    /// <summary>A counted array of bytes; see <see cref="WmiSchemaProperty.Max"/> for its length.</summary>
    UInt8Array,

    /// <summary>Eight bytes.</summary>
    UInt64,

    /// <summary>A CIM boolean.</summary>
    Boolean,

    /// <summary>A CIM string.</summary>
    String,
}

/// <summary>One parameter of one method, exactly as the dump recorded it.</summary>
/// <param name="Name">The parameter name, as the MOF will declare it.</param>
/// <param name="Type">Its CIM type, which fixes how many bytes the firmware reads or writes.</param>
/// <param name="Direction">Whether the caller supplies it or the firmware fills it in.</param>
/// <param name="DataId">The <c>WmiDataId</c> - the parameter's slot in the method's buffer.
/// Unique within its method, and meaningless outside it.</param>
/// <param name="Description">The recorded description, or empty if the dump carried none.</param>
public sealed record WmiSchemaParam(
    string Name,
    WmiParamType Type,
    WmiParamDirection Direction,
    int DataId,
    string Description);

/// <summary>One method of one class, exactly as the dump recorded it.</summary>
/// <param name="Name">The method name callers invoke.</param>
/// <param name="MethodId">The <c>WmiMethodId</c>: the number the firmware actually dispatches on.
/// <para>
/// MEANINGFUL ONLY WITHIN ITS OWN CLASS. <c>GB_WMIACPI_Get</c> and <c>GB_WMIACPI_Set</c> are
/// separate classes with separate id spaces, and they overlap: 101 is <c>GetChargeStop</c> in one
/// and <c>SetChargeStop</c> in the other, 88 is <c>CheckHeavyLoading</c> in one and
/// <c>SetSuperQuiet</c> in the other. Comparing an id from one class against an id from the other,
/// or carrying one between them, calls a different firmware method than the name says.
/// </para></param>
/// <param name="Description">The recorded description, or empty if the dump carried none.</param>
/// <param name="Parameters">The parameters, in the order the dump listed them: the in-parameters
/// first and then the out-parameters, alphabetical by name within each group. That is emphatically
/// <em>not</em> <see cref="WmiSchemaParam.DataId"/> order - <c>GetDeepFan</c> lists slot 5 first -
/// so order by <c>DataId</c> before laying out a buffer.</param>
public sealed record WmiSchemaMethod(
    string Name,
    int MethodId,
    string Description,
    IReadOnlyList<WmiSchemaParam> Parameters);

/// <summary>One property of one class.</summary>
/// <param name="Name">The property name.</param>
/// <param name="Type">Its CIM type.</param>
/// <param name="IsKey">Whether it carries the <c>key</c> qualifier.</param>
/// <param name="CanRead">Whether it carries <c>read=True</c>.</param>
/// <param name="CanWrite">Whether it carries <c>write=True</c>.</param>
/// <param name="DataId">The <c>WmiDataId</c>, or null if the dump recorded none - which is the
/// case for the system properties every WMI class carries.</param>
/// <param name="Max">The <c>MAX</c> qualifier, or null. Set only on array properties, where it is
/// the element count.</param>
/// <param name="Description">The recorded description, or empty if the dump carried none.</param>
public sealed record WmiSchemaProperty(
    string Name,
    WmiParamType Type,
    bool IsKey,
    bool CanRead,
    bool CanWrite,
    int? DataId,
    int? Max,
    string Description);

/// <summary>One WMI class, with everything the MOF needs to declare it.</summary>
/// <param name="Name">The class name, as WMI will know it.</param>
/// <param name="Guid">The <c>guid</c> qualifier naming the ACPI data block this class binds to,
/// braces included. Wrong here and the class registers cleanly and binds to nothing.</param>
/// <param name="Description">The recorded description, or empty if the dump carried none.</param>
/// <param name="Properties">The properties, in the order the dump listed them.</param>
/// <param name="Methods">The methods, in the order the dump listed them. Empty for the classes
/// that carry data rather than methods.</param>
public sealed record WmiSchemaClass(
    string Name,
    string Guid,
    string Description,
    IReadOnlyList<WmiSchemaProperty> Properties,
    IReadOnlyList<WmiSchemaMethod> Methods);

/// <summary>A whole recovered schema: every class the dump described.</summary>
/// <param name="Classes">The classes, in the order the dump listed them.</param>
public sealed record WmiSchema(IReadOnlyList<WmiSchemaClass> Classes)
{
    /// <summary>Finds a class by name.</summary>
    /// <param name="name">The class name, matched exactly.</param>
    /// <returns>The class.</returns>
    /// <exception cref="KeyNotFoundException">No class of that name was parsed. Thrown rather than
    /// returning null because every caller of this is about to read numbers out of the result, and
    /// a schema that is missing a class is not one anything should be generated from.</exception>
    public WmiSchemaClass Class(string name)
    {
        foreach (var c in Classes)
        {
            if (string.Equals(c.Name, name, StringComparison.Ordinal))
                return c;
        }

        var known = string.Join(", ", Classes.Select(c => c.Name));
        throw new KeyNotFoundException(
            $"The schema has no class named '{name}'. It has: {(known.Length == 0 ? "(none)" : known)}.");
    }
}

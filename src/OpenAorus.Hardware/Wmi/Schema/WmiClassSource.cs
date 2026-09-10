using System.Globalization;
using System.Management;

namespace OpenAorus.Hardware.Wmi.Schema;

/// <summary>Reads class definitions out of a live WMI namespace.</summary>
/// <remarks>
/// <para>
/// THIS READS NO GIGABYTE INSTANCE. <c>ManagementClass.Get</c> fetches the class's own metadata
/// out of the WMI repository; it does not enumerate an instance, and enumerating an instance of
/// <c>GB_WMIACPI_Get</c> is what dispatches into the ACPI provider. This runs at startup, on a
/// machine whose firmware interface is the thing being investigated, so it stays on the metadata
/// side of that line. The single exception is <see cref="ReadMarkerFingerprint"/>, which fetches
/// an instance of <c>OpenAorus_SchemaMarker</c> - our own class, declared by our own install file,
/// with no provider behind it. That read goes to the repository and nowhere near ACPI.
/// </para>
/// <para>
/// A missing class and an unwell repository are separated here rather than downstream, because
/// this is the only place the distinction still exists: <c>ManagementStatus.NotFound</c> and
/// <c>InvalidClass</c> are WMI saying "no such class", and everything else is WMI saying it could
/// not answer. Two floors up they would both just be an exception.
/// </para>
/// </remarks>
public sealed class WmiClassSource : IWmiClassSource
{
    private const string MethodIdQualifier = "WmiMethodId";

    /// <summary>The property the marker instance carries the fingerprint in, as the MOF names it.</summary>
    private const string MarkerFingerprintProperty = "Fingerprint";

    /// <summary>Reads from the namespace both MOF files declare.</summary>
    public WmiClassSource() : this(MofWriter.Namespace)
    {
    }

    /// <summary>Reads from a named namespace.</summary>
    /// <param name="namespacePath">The namespace, e.g. <c>root\WMI</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="namespacePath"/> is null.</exception>
    public WmiClassSource(string namespacePath)
    {
        ArgumentNullException.ThrowIfNull(namespacePath);
        NamespacePath = namespacePath;
    }

    /// <summary>The namespace being read.</summary>
    public string NamespacePath { get; }

    /// <inheritdoc/>
    /// <remarks>Never throws: every failure comes back as
    /// <see cref="WmiClassReading.Unreadable"/> carrying what Windows said.</remarks>
    public WmiClassReading Read(string className)
    {
        ArgumentNullException.ThrowIfNull(className);

        try
        {
            // Default connection options on purpose. Reading a class definition needs no
            // privilege beyond namespace access, and asking for more would make a metadata read
            // fail on a machine where it did not have to.
            var scope = new ManagementScope(NamespacePath);
            scope.Connect();

            using var declared = new ManagementClass(scope, new ManagementPath(className), null);
            declared.Get();

            var methodIds = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (MethodData method in declared.Methods)
            {
                // A method carrying no readable WmiMethodId is left out rather than guessed at.
                // It binds to nothing, and its absence moves the fingerprint - which is the right
                // outcome, since a class in that shape is not one we installed.
                if (MethodIdOf(method) is { } methodId) methodIds[method.Name] = methodId;
            }

            return WmiClassReading.Present(methodIds);
        }
        catch (ManagementException ex)
            when (ex.ErrorCode is ManagementStatus.NotFound or ManagementStatus.InvalidClass)
        {
            // WMI's answer to "is this class here", and the whole reason this class exists.
            return WmiClassReading.Absent;
        }
        catch (Exception ex)
        {
            return WmiClassReading.Unreadable($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    /// <remarks>Never throws: a marker that will not answer reads as one that recorded nothing,
    /// which matches no fingerprint and so can only withhold a permission, never grant one.</remarks>
    public string? ReadMarkerFingerprint()
    {
        try
        {
            var scope = new ManagementScope(NamespacePath);
            scope.Connect();

            // Addressed by key rather than enumerated. The install file writes exactly one
            // instance, and asking for that one by name cannot pick up a second that some other
            // run left behind.
            var path = new ManagementPath(
                $"{MofWriter.MarkerClass}.Id=\"{MofWriter.MarkerInstanceId}\"");

            using var marker = new ManagementObject(scope, path, null);
            marker.Get();

            return marker[MarkerFingerprintProperty] as string;
        }
        catch (Exception)
        {
            // Including the class not being there at all, which is the ordinary case on a machine
            // we have never registered.
            return null;
        }
    }

    /// <summary>Reads a method's <c>WmiMethodId</c> qualifier.</summary>
    /// <returns>The id, or null if the method carries none or carries one that is not a number.</returns>
    private static int? MethodIdOf(MethodData method)
    {
        foreach (QualifierData qualifier in method.Qualifiers)
        {
            // WMI qualifier names are case-insensitive; the MOF spells it WmiMethodId.
            if (!string.Equals(qualifier.Name, MethodIdQualifier, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                return Convert.ToInt32(qualifier.Value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                return null;
            }
        }

        return null;
    }
}

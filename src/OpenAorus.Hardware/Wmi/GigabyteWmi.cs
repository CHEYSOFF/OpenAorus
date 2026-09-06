using System.Management;

namespace OpenAorus.Hardware.Wmi;

/// <summary>Calls GB_WMIACPI_Get / GB_WMIACPI_Set in root\WMI. Requires an elevated process.</summary>
public sealed class GigabyteWmi : IGigabyteWmi
{
    private const string ScopePath = @"root\WMI";
    private readonly object _lock = new();

    public WmiResult Invoke(WmiClass cls, string method, IReadOnlyDictionary<string, object>? args = null)
    {
        var className = cls == WmiClass.Get ? "GB_WMIACPI_Get" : "GB_WMIACPI_Set";
        lock (_lock)
        {
            try
            {
                var options = new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate,
                };
                var scope = new ManagementScope(ScopePath, options);
                scope.Connect();
                using var mc = new ManagementClass(scope, new ManagementPath(className), null);
                using var instances = mc.GetInstances();
                foreach (ManagementObject instance in instances)
                {
                    using (instance)
                    {
                        using var inParams = instance.GetMethodParameters(method);
                        if (args is not null)
                            foreach (var (k, v) in args) inParams[k] = v;
                        using var outParams = instance.InvokeMethod(method, inParams, null);
                        var dict = new Dictionary<string, object>();
                        if (outParams is not null)
                            foreach (var p in outParams.Properties)
                                if (p.Value is not null) dict[p.Name] = p.Value;
                        return WmiResult.Ok(dict);
                    }
                }
                return WmiResult.Fail($"No instance of {className}. Is Gigabyte Control Center's WMI schema (acpimof.dll) installed?");
            }
            catch (ManagementException ex) { return WmiResult.Fail($"{className}.{method}: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { return WmiResult.Fail($"{className}.{method}: access denied ({ex.Message}). Run elevated."); }
            catch (Exception ex) { return WmiResult.Fail($"{className}.{method}: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}

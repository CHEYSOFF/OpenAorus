using System.Management;

namespace OpenAorus.Hardware.Wmi;

public static class SystemInfo
{
    /// <summary>SMBIOS product name, e.g. "AORUS 17G KD". Null if WMI is unavailable.</summary>
    public static string? GetProductName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_ComputerSystemProduct");
            foreach (ManagementObject o in searcher.Get())
                using (o) return o["Name"]?.ToString()?.Trim();
        }
        catch (Exception) { }
        return null;
    }
}

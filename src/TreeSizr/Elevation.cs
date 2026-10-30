using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace TreeSizr;

/// <summary>Administrator rights: needed for the fast MFT scan.</summary>
public static class Elevation
{
    public static bool IsElevated { get; } = CheckElevated();

    /// <summary>Starts a new, elevated instance with <paramref name="argument"/> on its command line. False if the user declined.</summary>
    public static bool TryRestartElevated(string argument)
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add(argument); // ArgumentList takes care of quoting, also for "C:\"
            Process.Start(start);
            return true;
        }
        catch (Win32Exception)
        {
            return false; // the user declined the UAC prompt
        }
    }

    private static bool CheckElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

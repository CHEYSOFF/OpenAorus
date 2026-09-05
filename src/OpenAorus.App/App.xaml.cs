using System.IO;
using System.Windows;
using OpenAorus.Hardware.Platform;

namespace OpenAorus.App;

public partial class App : System.Windows.Application
{
    public static AppServices Services { get; private set; } = null!;
    public static bool StartHidden { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToArray();
        StartHidden = args.Contains("--tray");

        if (!Elevation.IsElevated())
        {
            var exe = Environment.ProcessPath!;
            if (!Elevation.RelaunchElevated(exe, e.Args))
                System.Windows.MessageBox.Show("OpenAorus needs administrator rights to talk to the embedded controller.",
                    "OpenAorus", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        Services = AppServices.Create();

        if (args.Contains("--dump"))
        {
            var attached = ConsoleAttach.TryAttach();
            var path = Services.WriteDiagnostics();
            if (attached) Console.WriteLine(File.ReadAllText(path));
            else System.Windows.MessageBox.Show($"Diagnostics written to:\n{path}", "OpenAorus");
            Shutdown(0);
            return;
        }

        if (args.Contains("--apply"))
        {
            var r = await Services.ApplySavedAsync();
            Shutdown(r.Success ? 0 : 1);
            return;
        }

        // Task 14 replaces this with tray + window startup.
        System.Windows.MessageBox.Show($"OpenAorus {Services.Version} on {Services.Profile.Name} ({Services.Profile.Status}). UI arrives in Task 14.", "OpenAorus");
        Shutdown(0);
    }
}

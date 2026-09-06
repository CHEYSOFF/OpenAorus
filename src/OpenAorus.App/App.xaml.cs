using System.IO;
using System.Windows;
using System.Windows.Threading;
using OpenAorus.Hardware.Platform;

namespace OpenAorus.App;

public partial class App : System.Windows.Application
{
    public static AppServices Services { get; private set; } = null!;
    public static bool StartHidden { get; private set; }

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);
            var args = e.Args.Select(a => a.ToLowerInvariant()).ToArray();
            // --tray starts hidden in the tray (used by the scheduled autostart task); --show forces the
            // window visible even alongside --tray; anything else defaults to visible.
            StartHidden = args.Contains("--tray") && !args.Contains("--show");

            if (!Elevation.IsElevated())
            {
                var exe = Environment.ProcessPath!;
                var relaunched = Elevation.RelaunchElevated(exe, e.Args);
                if (!relaunched)
                    System.Windows.MessageBox.Show("OpenAorus needs administrator rights to talk to the embedded controller.",
                        "OpenAorus", MessageBoxButton.OK, MessageBoxImage.Warning);
                // Exit 0 when the elevated relaunch was actually started - only a refusal (UAC cancelled) is a
                // failure. --apply is meant to be usable unattended from a scheduled task, and a scheduler that
                // sees this process "fail" on every successful handoff to the elevated copy is not useful.
                Shutdown(relaunched ? 0 : 1);
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

            var vm = new ViewModels.MainViewModel(Services);
            _window = new Views.MainWindow(vm);
            _tray = new TrayIcon(vm, () => _window.ToggleVisibility(), () => Shutdown(0));
            if (!StartHidden) _window.ToggleVisibility();
            await vm.InitializeAsync();
            _vm = vm;
        }
        catch (Exception ex)
        {
            ShowFatalError(ex);
            Shutdown(1);
        }
    }

    private Views.MainWindow? _window;
    private TrayIcon? _tray;
    private ViewModels.MainViewModel? _vm;

    protected override void OnExit(ExitEventArgs e)
    {
        _vm?.Shutdown();
        _tray?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Backstop for exceptions raised after OnStartup returns (e.g. once the tray icon exists) so a
    /// later fault surfaces to the owner instead of the process silently disappearing.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatalError(e.Exception);
        e.Handled = true;
        Shutdown(1);
    }

    private static void ShowFatalError(Exception ex)
    {
        // The dump makes ~90 more WMI calls; if the fault came from System.Management (the WMI provider is
        // wedged, COM is in a bad state, etc.), running it is the slowest possible response and can hang the
        // dispatcher a second time on top of the original fault.
        var fromWmi = IsFromSystemManagement(ex);

        string? diagnosticsPath = null;
        if (!fromWmi)
        {
            try { diagnosticsPath = Services is not null ? Services.WriteDiagnostics() : null; }
            catch (Exception) { }
        }

        var message = $"{ex.GetType().Name}: {ex.Message}";
        if (diagnosticsPath is not null)
            message += $"\n\nDiagnostics written to:\n{diagnosticsPath}";
        else if (fromWmi)
            message += "\n\n(Diagnostics dump skipped: the fault came from the WMI provider, which the dump would call ~90 more times.)";

        // A failure to show this dialog (e.g. no desktop session) must not itself become a second unhandled
        // exception on top of the one being reported.
        try { System.Windows.MessageBox.Show(message, "OpenAorus", MessageBoxButton.OK, MessageBoxImage.Error); }
        catch (Exception) { }
    }

    private static bool IsFromSystemManagement(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.GetType().Namespace == "System.Management") return true;
        return false;
    }
}

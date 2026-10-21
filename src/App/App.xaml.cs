using Microsoft.UI.Xaml;
using Pdf26.Services;
using Pdf26.Views;

namespace Pdf26;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Unpackaged desktop launch: Environment.GetCommandLineArgs() carries the argument
        // passed on the command line, by a file-association "Apri con", or a shell verb -
        // args[0] is always the executable path itself.
        var initialSource = CommandLineParser.TryGetRequestedSource(Environment.GetCommandLineArgs());

        _window = new MainWindow(initialSource);
        _window.Activate();

        try
        {
            // An association registered before the rename still lists the app under its old name.
            Services.FileAssociationService.RefreshRegisteredName();
        }
        catch (Exception)
        {
            // Cosmetic and per-user registry only: never worth failing the launch.
        }
    }

    /// <summary>
    /// Last-resort safety net: without this, any exception that escapes an `async void`
    /// handler (XAML Click handlers are one) terminates the whole process instead of just
    /// failing the one operation. Logs to %LocalAppData%\PDF26\logs\crash.log and marks the
    /// exception handled so the app keeps running - this trades "some bugs fail visibly
    /// louder" for "one bad page/click doesn't kill the whole session", which is the right
    /// default for a document viewer but does mean genuinely corrupt state could linger
    /// instead of restarting clean; worth revisiting once there is in-app error reporting.
    /// </summary>
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDF26", "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "crash.log"),
                $"{DateTime.Now:O}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never itself throw from inside a crash handler.
        }

        e.Handled = true;
    }
}

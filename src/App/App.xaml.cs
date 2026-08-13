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
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Unpackaged desktop launch: Environment.GetCommandLineArgs() carries the argument
        // passed on the command line, by a file-association "Apri con", or a shell verb -
        // args[0] is always the executable path itself.
        var initialSource = CommandLineParser.TryGetRequestedSource(Environment.GetCommandLineArgs());

        _window = new MainWindow(initialSource);
        _window.Activate();
    }
}

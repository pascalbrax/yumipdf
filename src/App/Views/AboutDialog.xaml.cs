using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Pdf26.Views;

/// <summary>Nome, versione, breve descrizione e licenze di terze parti - niente che serva un aggiornamento a runtime, quindi tutto è impostato una volta sola dal costruttore.</summary>
public sealed partial class AboutDialog : ContentDialog
{
    public AboutDialog()
    {
        InitializeComponent();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppIconImage.Source = new BitmapImage(new Uri(iconPath));
        }

        // FileVersionInfo, non Assembly.GetName().Version: quest'ultima legge solo
        // AssemblyVersion (sempre a 4 cifre, es. "0.1.0.0"), mentre ProductVersion riflette
        // <Version> di Directory.Build.props esattamente come lo mostra Esplora file nella
        // scheda Dettagli di PDF26.exe - a parte l'hash del commit che l'SDK ci appende dopo
        // il "+" (utile lì per rintracciare la build esatta, troppo lungo per questo dialogo).
        var location = Assembly.GetExecutingAssembly().Location;
        var version = File.Exists(location) ? FileVersionInfo.GetVersionInfo(location).ProductVersion : null;
        var shortVersion = version?.Split('+')[0];
        VersionText.Text = $"Versione {shortVersion ?? "sconosciuta"}";

        CopyrightText.Text = $"© {DateTime.Now.Year} Pascal Brax";
    }
}

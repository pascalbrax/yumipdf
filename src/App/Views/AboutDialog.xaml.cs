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

        // about.png (the project's picture, linked from the repository root - see App.csproj); the small app icon if it is missing.
        var picturePath = Path.Combine(AppContext.BaseDirectory, "Assets", "about.png");
        var fallbackPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        var imagePath = File.Exists(picturePath) ? picturePath : File.Exists(fallbackPath) ? fallbackPath : null;
        if (imagePath is not null)
        {
            AboutImage.Source = new BitmapImage(new Uri(imagePath));
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

        // The date and time this build was made, stamped into the assembly by App.csproj ("BuildDate", ISO, local time).
        var built = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildDate")?.Value;
        BuildDateText.Text = DateTime.TryParseExact(built, "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var builtAt)
            ? $"Build del {builtAt.ToString("dd/MM/yyyy 'alle' HH:mm", System.Globalization.CultureInfo.InvariantCulture)}"
            : string.Empty;

        CopyrightText.Text = $"© {DateTime.Now.Year} Pascal Brax";
    }
}

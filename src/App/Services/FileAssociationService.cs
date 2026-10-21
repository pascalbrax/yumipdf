using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Pdf26.Services;

/// <summary>
/// Registers/unregisters PDF26 as a candidate handler for <c>.pdf</c> files, entirely under
/// <c>HKEY_CURRENT_USER</c> (no admin rights needed, and reversible by unregistering).
/// </summary>
/// <remarks>
/// Windows deliberately does not let an application silently become the *default* handler
/// for a file type (the per-user "UserChoice" registry hash cannot be forged by a program -
/// this was locked down specifically to stop default-app hijacking). So <see cref="Register"/>
/// only makes PDF26 show up as a selectable option in Explorer's "Open with" list and in
/// Windows' Default Apps settings, following the official "RegisteredApplications"
/// capabilities pattern; <see cref="OpenWindowsDefaultAppsSettings"/> is provided so the user
/// can finish making it the default themselves, from Windows' own UI.
/// </remarks>
public static class FileAssociationService
{
    private const string ProgId = "PDF26.Document";
    private const string AppRegistrationName = "PDF26";

    /// <summary>The name Windows shows for the app in "Open with" and in the default apps settings.</summary>
    private const string DisplayName = "YumiPDF";

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids");
        return key?.GetValue(ProgId) is not null;
    }

    /// <summary>
    /// Brings the name Windows lists the app under up to date for an association registered by an earlier version (which
    /// registered it under the old name). Does nothing when the user never registered it - it must not register by itself.
    /// </summary>
    public static void RefreshRegisteredName()
    {
        if (!IsRegistered())
        {
            return;
        }

        using var capabilitiesKey = Registry.CurrentUser.OpenSubKey($@"Software\{AppRegistrationName}\Capabilities", writable: true);
        if (capabilitiesKey is not null && !string.Equals(capabilitiesKey.GetValue("ApplicationName") as string, DisplayName, StringComparison.Ordinal))
        {
            capabilitiesKey.SetValue("ApplicationName", DisplayName);
            NotifyShellOfAssociationChange();
        }
    }

    public static void Register()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            throw new InvalidOperationException("Impossibile determinare il percorso dell'eseguibile corrente.");
        }

        using (var commandKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
        {
            commandKey.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");
        }

        using (var iconKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\DefaultIcon"))
        {
            iconKey.SetValue(string.Empty, $"\"{exePath}\",0");
        }

        using (var openWithKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids"))
        {
            openWithKey.SetValue(ProgId, string.Empty, RegistryValueKind.String);
        }

        using (var capabilitiesKey = Registry.CurrentUser.CreateSubKey($@"Software\{AppRegistrationName}\Capabilities"))
        {
            // The name Windows lists under "Open with" and in the default apps settings. The registry keys themselves
            // (ProgId, AppRegistrationName) keep the old internal name: they are identifiers, nobody sees them.
            capabilitiesKey.SetValue("ApplicationName", DisplayName);
            capabilitiesKey.SetValue("ApplicationDescription", "Visualizzatore, editor e compilatore PDF");
            using var fileAssociationsKey = capabilitiesKey.CreateSubKey("FileAssociations");
            fileAssociationsKey.SetValue(".pdf", ProgId);
        }

        using (var registeredAppsKey = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
        {
            registeredAppsKey.SetValue(AppRegistrationName, $@"Software\{AppRegistrationName}\Capabilities");
        }

        NotifyShellOfAssociationChange();
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);

        using (var openWithKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids", writable: true))
        {
            openWithKey?.DeleteValue(ProgId, throwOnMissingValue: false);
        }

        Registry.CurrentUser.DeleteSubKeyTree($@"Software\{AppRegistrationName}", throwOnMissingSubKey: false);

        using (var registeredAppsKey = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
        {
            registeredAppsKey?.DeleteValue(AppRegistrationName, throwOnMissingValue: false);
        }

        NotifyShellOfAssociationChange();
    }

    /// <summary>Opens Windows' own "Default apps" settings page, where the user can pick YumiPDF as the default for .pdf.</summary>
    public static void OpenWindowsDefaultAppsSettings()
    {
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
    }

    private static void NotifyShellOfAssociationChange()
    {
        const int SHCNE_ASSOCCHANGED = 0x08000000;
        const int SHCNF_IDLIST = 0x0000;
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);
}

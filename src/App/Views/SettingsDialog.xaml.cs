using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using Pdf26.Services;

namespace Pdf26.Views;

public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog(Color currentPageAreaBackground, bool isPdfAssociated)
    {
        InitializeComponent();
        BackgroundColorPicker.Color = currentPageAreaBackground;
        AssociatePdfToggle.IsOn = isPdfAssociated;
    }

    public Color SelectedColor => BackgroundColorPicker.Color;

    public bool AssociatePdf => AssociatePdfToggle.IsOn;

    private void OnOpenDefaultAppsSettingsClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => FileAssociationService.OpenWindowsDefaultAppsSettings();
}

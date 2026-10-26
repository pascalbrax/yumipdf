using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using PdfEngine.Viewing;
using Pdf26.Services;

namespace Pdf26.Views;

public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog(Color currentPageAreaBackground, bool isPdfAssociated, MixedPagesMode mixedPagesMode, WheelZoomDirection wheelZoom)
    {
        InitializeComponent();
        BackgroundColorPicker.Color = currentPageAreaBackground;
        AssociatePdfToggle.IsOn = isPdfAssociated;
        MixedPagesOptions.SelectedIndex = (int)mixedPagesMode;
        WheelZoomOptions.SelectedIndex = (int)wheelZoom;
    }

    /// <summary>The options are listed in the order of <see cref="WheelZoomDirection"/>.</summary>
    public WheelZoomDirection SelectedWheelZoom
        => Enum.IsDefined((WheelZoomDirection)WheelZoomOptions.SelectedIndex) ? (WheelZoomDirection)WheelZoomOptions.SelectedIndex : WheelZoomDirection.UpZoomsIn;

    /// <summary>The options are listed in the order of <see cref="MixedPagesMode"/>.</summary>
    public MixedPagesMode SelectedMixedPagesMode
        => Enum.IsDefined((MixedPagesMode)MixedPagesOptions.SelectedIndex) ? (MixedPagesMode)MixedPagesOptions.SelectedIndex : MixedPagesMode.CurrentPage;

    public Color SelectedColor => BackgroundColorPicker.Color;

    public bool AssociatePdf => AssociatePdfToggle.IsOn;

    private void OnOpenDefaultAppsSettingsClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => FileAssociationService.OpenWindowsDefaultAppsSettings();
}

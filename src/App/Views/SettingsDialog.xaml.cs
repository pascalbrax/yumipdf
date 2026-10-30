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
        _currentBackground = currentPageAreaBackground;
        BackgroundColors.ItemsSource = PageBackgroundPresets.All;
        BackgroundColors.SelectedItem = PageBackgroundPresets.Find(currentPageAreaBackground);
        BackgroundColors.SelectionChanged += (_, _) => ShowBackgroundName();
        ShowBackgroundName();
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

    /// <summary>The color picked in the grid - or the one the dialog opened with, if none is selected (a custom color from before the presets existed stays until another is chosen).</summary>
    public Color SelectedColor => BackgroundColors.SelectedItem is PageBackgroundPreset preset ? preset.Color : _currentBackground;

    private readonly Color _currentBackground;

    private void ShowBackgroundName()
        => BackgroundColorName.Text = BackgroundColors.SelectedItem is PageBackgroundPreset preset ? preset.Name : "Colore attuale (scegline uno per sostituirlo)";

    public bool AssociatePdf => AssociatePdfToggle.IsOn;

    private void OnOpenDefaultAppsSettingsClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => FileAssociationService.OpenWindowsDefaultAppsSettings();
}

using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace Pdf26.Views;

public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog(Color currentPageAreaBackground)
    {
        InitializeComponent();
        BackgroundColorPicker.Color = currentPageAreaBackground;
    }

    public Color SelectedColor => BackgroundColorPicker.Color;
}

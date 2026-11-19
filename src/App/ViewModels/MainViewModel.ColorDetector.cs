using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace Pdf26.ViewModels;

/// <summary>
/// The colour detector (for colour blindness): an option in "Opzioni" puts a toggle in the toolbar; while it is on, the pointer over
/// a page is a cross, and holding it still for a second shows the RGB value and the name of the colour under it
/// (<c>PdfColorDetectorLayer</c>, one on every page; the naming is <c>PdfEngine.Viewing.ColorNamer</c>).
/// </summary>
public partial class MainViewModel
{
    /// <summary>The option: whether the toolbar offers the detector. Saved between sessions.</summary>
    public bool IsColorDetectorEnabled { get; private set; }

    /// <summary>The toolbar's toggle: on while the detector is the active tool. It excludes the placement tools and "Modifica testo".</summary>
    [ObservableProperty]
    public partial bool IsColorDetectorActive { get; set; }

    /// <summary>The detector's button is in the toolbar only with the option on and a document open.</summary>
    public Visibility ColorDetectorButtonVisibility => IsColorDetectorEnabled && _document is not null ? Visibility.Visible : Visibility.Collapsed;

    public void SetColorDetectorEnabled(bool enabled)
    {
        IsColorDetectorEnabled = enabled;
        _settings.ColorDetectorEnabled = enabled;
        SaveSettings();
        if (!enabled)
        {
            IsColorDetectorActive = false;
        }

        OnPropertyChanged(nameof(ColorDetectorButtonVisibility));
    }

    partial void OnIsColorDetectorActiveChanged(bool value)
    {
        if (value)
        {
            // One tool at a time: the detector's layer sits over the page and would take the clicks of the others.
            CancelAnnotationTool();
            SelectAnnotation(null);
            IsTextEditTool = false;
        }

        OnPropertyChanged(nameof(AnnotationHint));
    }
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace Pdf26.ViewModels;

/// <summary>
/// "Aggancia alla griglia": a dotted grid over the pages (<c>PdfGridLayer</c>) that notes, stamps and form fields snap to when they
/// are placed, moved or resized - so several fields put one under the other end up with the same left edge. The grid itself
/// (10 PDF points from the page's top-left corner) is <see cref="PdfEngine.Annotations.PageGrid"/>; placement snaps
/// the top-left corner (here, in <c>PlaceAnnotationAsync</c> and <c>PlaceDesignField</c>), dragging snaps live in <c>PdfAnnotationLayer</c>.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Whether the grid is shown and snapping is on. Remembered between sessions.</summary>
    [ObservableProperty]
    public partial bool IsSnapToGridEnabled { get; set; }

    /// <summary>
    /// Whether the grid is actually in effect: the option is on <em>and</em> the Strumenti panel (where its check box lives) is
    /// open. With the panel closed there is no visible way to switch the grid off, so it must neither be drawn nor snap anything -
    /// the choice is kept and comes back when the panel is opened again.
    /// </summary>
    public bool IsGridActive => IsSnapToGridEnabled && IsToolsPaneOpen;

    partial void OnIsSnapToGridEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsGridActive));
        if (_restoringSettings)
        {
            return;
        }

        _settings.SnapToGrid = value;
        SaveSettings();
    }
}

using Microsoft.UI.Xaml;

namespace Pdf26.ViewModels;

/// <summary>
/// One rectangle drawn on top of a page to show where a search match sits - purely a screen
/// overlay (see <c>PdfPageViewModel.Highlights</c>), never written into the PDF itself.
/// Positioned like a form field's on-page overlay (<c>FormFieldEntryViewModel</c>): a
/// Margin (Left, Top) plus an explicit Width/Height, all already in on-screen pixels for the
/// page's current zoom.
/// </summary>
public sealed class PdfSearchHighlightViewModel
{
    public PdfSearchHighlightViewModel(Thickness margin, double width, double height)
    {
        Margin = margin;
        Width = width;
        Height = height;
    }

    public Thickness Margin { get; }

    public double Width { get; }

    public double Height { get; }
}

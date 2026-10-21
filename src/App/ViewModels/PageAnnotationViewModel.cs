using PdfEngine.Annotations;
using PdfEngine.Forms;

namespace Pdf26.ViewModels;

/// <summary>
/// One note or stamp on a page, as the annotation layer (<c>PdfAnnotationLayer</c>) needs it: where
/// it is and what it holds. Immutable - an edit re-reads the page's annotations and replaces the
/// instances, so a stale reference simply stops being in <see cref="PdfPageViewModel.Annotations"/>.
/// </summary>
public sealed class PageAnnotationViewModel
{
    public PageAnnotationViewModel(PdfPageViewModel page, PdfAnnotationInfo info)
    {
        Page = page;
        Index = info.Index;
        Kind = info.Kind;
        Rect = info.Rect;
        Contents = info.Contents;
        CanResize = info.CanResize;
        Id = info.Id;
    }

    /// <summary>A form field the user placed and that is not in the document yet - drawn as a placeholder (see <see cref="DesignField"/>).</summary>
    public PageAnnotationViewModel(PdfPageViewModel page, DesignField field)
    {
        Page = page;
        Index = -1;
        Kind = PdfAnnotationKind.Stamp; // unused for a field: IsField decides everything
        Rect = field.Rect;
        Contents = string.Empty;
        CanResize = true;
        Field = field;
    }

    public PdfPageViewModel Page { get; }

    /// <summary>The annotation's private identity once the app has touched it (see <c>PdfEditableAnnotations.EnsureId</c>); null before, and for a form field.</summary>
    public string? Id { get; }

    /// <summary>Non-null when this item is a form field being designed rather than an annotation of the document.</summary>
    public DesignField? Field { get; }

    public bool IsField => Field is not null;

    /// <summary>Whether resizing keeps the proportions: a stamp keeps its picture's, a check box stays square; a text field stretches freely.</summary>
    public bool KeepProportions => Field is null || Field.Kind == PdfFieldKind.CheckBox;

    /// <summary>Position in the page's own annotation list - what <c>PdfEditableAnnotations</c> addresses it by.</summary>
    public int Index { get; }

    public PdfAnnotationKind Kind { get; }

    /// <summary>In PDF points, bottom-left origin.</summary>
    public PdfRect Rect { get; }

    /// <summary>A note's text; empty for a stamp.</summary>
    public string Contents { get; }

    public bool IsNote => Kind == PdfAnnotationKind.Note;

    /// <summary>Whether the corner handle (resizing) is offered - only for stamps made by this app (see <c>PdfEditableAnnotations</c>).</summary>
    public bool CanResize { get; }
}

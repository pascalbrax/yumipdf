using PdfEngine.Annotations;
using PdfEngine.Forms;

namespace Pdf26.ViewModels;

/// <summary>
/// A form field the user has placed but that is not in the document yet: it only exists in the
/// app until the document is saved (see <c>MainViewModel.FieldDesign.cs</c>), when it is written
/// into the file as a real, fillable field. Until then the page layer draws it as a placeholder.
/// </summary>
public sealed class DesignField
{
    public DesignField(int pageIndex, PdfFieldKind kind, string name, PdfRect rect)
    {
        PageIndex = pageIndex;
        Kind = kind;
        Name = name;
        Rect = rect;
    }

    public int PageIndex { get; }

    public PdfFieldKind Kind { get; }

    /// <summary>What a reader lists the field as; unique among the document's fields (checked by <c>MainViewModel.ValidateFieldName</c>).</summary>
    public string Name { get; set; }

    /// <summary>In PDF points, bottom-left origin.</summary>
    public PdfRect Rect { get; set; }

    public string Tooltip { get; set; } = string.Empty;

    public bool Required { get; set; }

    public bool ReadOnly { get; set; }

    /// <summary>Text fields only.</summary>
    public bool Multiline { get; set; }

    /// <summary>Text fields only; 0 lets the reader fit the text to the field.</summary>
    public double FontSize { get; set; }

    /// <summary>Check boxes only.</summary>
    public bool CheckedByDefault { get; set; }

    public PdfFieldSpec ToSpec() => new(Name, Kind, Rect, Tooltip, Required, ReadOnly, Multiline, FontSize, CheckedByDefault);
}

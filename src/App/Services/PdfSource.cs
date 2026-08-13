namespace Pdf26.Services;

public enum PdfSourceKind
{
    LocalPath,
    UncPath,
    HttpUrl,
}

/// <summary>A document reference as passed on the command line, before it has been fetched/read.</summary>
public sealed record PdfSource(PdfSourceKind Kind, string Value)
{
    public override string ToString() => Value;
}

namespace PdfEngine;

/// <summary>
/// One entry of a document's outline (its "index" / table of contents).
/// </summary>
/// <param name="Title">The entry's text, as the document's author wrote it.</param>
/// <param name="PageIndex">0-based page the entry leads to, or -1 when it has no destination inside this document (a heading-only entry, or a link to a URL or another file).</param>
/// <param name="IsInitiallyOpen">Whether the document asks for this entry to start expanded.</param>
/// <param name="Children">Nested entries, in document order.</param>
public sealed record PdfBookmark(string Title, int PageIndex, bool IsInitiallyOpen, IReadOnlyList<PdfBookmark> Children);

using System.Text;
using PdfEngine.Annotations;
using PdfEngine.Native;

namespace PdfEngine.Editing;

/// <summary>One line of text on a page (a PDF "text object"): what <see cref="PdfTextEditing.List"/> returns.</summary>
/// <param name="ObjectIndex">Position among the page's objects - what the other methods take. Replacing with a fallback font keeps it; deleting a line shifts the ones after it (read the list again).</param>
/// <param name="Rect">Bounding box in page points, origin bottom-left.</param>
/// <param name="IsEmbedded">Whether the font is embedded in the file (usually as a subset of the glyphs the document uses).</param>
public sealed record PdfTextLine(int ObjectIndex, string Text, PdfRect Rect, string FontName, double FontSize, bool IsEmbedded);

/// <summary>What <see cref="PdfTextEditing.Replace"/> did.</summary>
/// <param name="UsedFallbackFont">The line was rewritten in a bundled open-source font because its own font lacks a glyph of the new text.</param>
/// <param name="FallbackFontName">The font used, when <paramref name="UsedFallbackFont"/>.</param>
/// <param name="MissingCharacters">Characters no available font can draw (they come out as an empty box).</param>
/// <param name="Undo">Reverts the edit, and re-applies it again: keep it for as long as the edit can be undone, then <see cref="IDisposable.Dispose"/> it.</param>
public sealed record PdfTextEditResult(bool UsedFallbackFont, string? FallbackFontName, string MissingCharacters, PdfEditRecord Undo);

/// <summary>
/// An edit that can be undone and redone, in that order and no other: <c>Undo</c> and <c>Redo</c> alternate, and edits made
/// after it invalidate it (the page's object indices have moved on), which the caller's undo stack guarantees by undoing in
/// reverse order. Holds, while undone, whatever the edit took off the page - so it must be disposed when discarded.
/// </summary>
public abstract class PdfEditRecord : IDisposable
{
    internal abstract void Apply(PdfPage page, bool undo);

    /// <summary>Puts the page back as it was before the edit.</summary>
    public void Undo(PdfPage page) => PdfiumRuntime.Invoke(() => { Apply(page, undo: true); return 0; });

    /// <summary>Re-applies the edit after <see cref="Undo"/>.</summary>
    public void Redo(PdfPage page) => PdfiumRuntime.Invoke(() => { Apply(page, undo: false); return 0; });

    public abstract void Dispose();
}

/// <summary>
/// Editing and deleting the text that is already on a page. PDFium can replace the text of a text object, but only with
/// glyphs its font has - and an embedded font is normally a subset holding just what the original document used, so a
/// new letter comes out as an empty box. When that would happen the line is rewritten in a bundled open-source font
/// (<see cref="FallbackFonts"/>) of similar look, embedded in the document. There is no reflow: a line is one object,
/// and changing its length moves nothing else. Text inside form XObjects (some producers wrap the whole page in one)
/// is not listed.
/// </summary>
public static class PdfTextEditing
{
    private const int TextObject = 1; // FPDF_PAGEOBJ_TEXT

    /// <summary>The text lines of a page that have something to edit (not blank).</summary>
    public static IReadOnlyList<PdfTextLine> List(PdfPage page)
        => PdfiumRuntime.Invoke(() =>
        {
            var handle = page.Handle;
            var lines = new List<PdfTextLine>();
            var textPage = PdfiumNative.FPDFText_LoadPage(handle);
            try
            {
                var count = PdfiumNative.FPDFPage_CountObjects(handle);
                for (var index = 0; index < count; index++)
                {
                    var obj = PdfiumNative.FPDFPage_GetObject(handle, index);
                    if (obj == IntPtr.Zero || PdfiumNative.FPDFPageObj_GetType(obj) != TextObject)
                    {
                        continue;
                    }

                    var text = ReadText(obj, textPage);
                    if (string.IsNullOrWhiteSpace(text) || !PdfiumNative.FPDFPageObj_GetBounds(obj, out var left, out var bottom, out var right, out var top))
                    {
                        continue;
                    }

                    var font = PdfiumNative.FPDFTextObj_GetFont(obj);
                    PdfiumNative.FPDFTextObj_GetFontSize(obj, out var size);
                    PdfiumNative.FPDFPageObj_GetMatrix(obj, out var matrix);
                    var effectiveSize = size * Math.Sqrt(matrix.C * matrix.C + matrix.D * matrix.D);
                    lines.Add(new PdfTextLine(
                        index, text, new PdfRect(left, bottom, right, top), FontName(font), Math.Round(effectiveSize, 2),
                        font != IntPtr.Zero && PdfiumNative.FPDFFont_GetIsEmbedded(font) == 1));
                }
            }
            finally
            {
                if (textPage != IntPtr.Zero)
                {
                    PdfiumNative.FPDFText_ClosePage(textPage);
                }
            }

            return (IReadOnlyList<PdfTextLine>)lines;
        });

    /// <summary>Replaces the text of line <paramref name="objectIndex"/> (from <see cref="List"/>) with <paramref name="newText"/> - one line: line breaks become spaces.</summary>
    public static PdfTextEditResult Replace(PdfPage page, int objectIndex, string newText)
        => PdfiumRuntime.Invoke(() =>
        {
            var text = NormalizeLine(newText);
            if (text.Length == 0)
            {
                throw new ArgumentException("Il nuovo testo è vuoto: per toglierlo si elimina la riga.", nameof(newText));
            }

            var handle = page.Handle;
            var document = page.Document;
            var state = document.EditingState;
            var obj = TextObjectAt(handle, objectIndex);
            var oldText = ReadObjectText(handle, obj);
            var font = PdfiumNative.FPDFTextObj_GetFont(obj);

            // What the line's own font cannot draw. Characters already in the line need no check: they are drawn today.
            var missing = text.Distinct()
                .Where(c => !oldText.Contains(c) && !state.FontHasGlyph(document.Handle, font, c))
                .ToArray();

            if (missing.Length == 0)
            {
                if (!PdfiumNative.FPDFText_SetText(obj, text) || !PdfiumNative.FPDFPage_GenerateContent(handle))
                {
                    throw new PdfException(PdfErrorCode.Unknown, "Impossibile modificare il testo.");
                }

                return new PdfTextEditResult(false, null, string.Empty, new TextChangeRecord(objectIndex, oldText, text));
            }

            return ReplaceWithFallback(page, state, objectIndex, obj, font, text);
        });

    /// <summary>Removes line <paramref name="objectIndex"/> from the page for good (the text is gone from the file too, not just hidden).</summary>
    public static PdfEditRecord Remove(PdfPage page, int objectIndex)
        => PdfiumRuntime.Invoke(() =>
        {
            var handle = page.Handle;
            var state = page.Document.EditingState;
            var obj = TextObjectAt(handle, objectIndex);
            if (!PdfiumNative.FPDFPage_RemoveObject(handle, obj))
            {
                throw new PdfException(PdfErrorCode.Unknown, "Impossibile eliminare la riga.");
            }

            state.Orphan(obj);
            if (!PdfiumNative.FPDFPage_GenerateContent(handle))
            {
                throw new PdfException(PdfErrorCode.Unknown, "Impossibile aggiornare la pagina.");
            }

            return (PdfEditRecord)new ObjectRemovalRecord(page.Document, objectIndex, obj);
        });

    private static PdfTextEditResult ReplaceWithFallback(PdfPage page, PdfEditingState state, int index, IntPtr oldObject, IntPtr oldFont, string text)
    {
        var handle = page.Handle;
        var document = page.Document.Handle;

        var (family, bold, italic) = ClassifyFont(oldFont);
        PdfiumNative.FPDFTextObj_GetFontSize(oldObject, out var fontSize);

        // First font of the chain that draws every character; failing that, the one that draws the most.
        IntPtr bestFont = IntPtr.Zero;
        string bestName = string.Empty;
        var bestMissingChars = text.Distinct().ToArray();
        foreach (var (key, name) in FallbackFonts.Chain(family, bold, italic))
        {
            var candidate = state.GetFallbackFont(document, key, () => FallbackFonts.Load(key));
            var absent = text.Distinct().Where(c => !state.FontHasGlyph(document, candidate, c)).ToArray();
            if (bestFont == IntPtr.Zero || absent.Length < bestMissingChars.Length)
            {
                bestFont = candidate;
                bestName = name;
                bestMissingChars = absent;
            }

            if (absent.Length == 0)
            {
                break;
            }
        }

        var replacement = PdfiumNative.FPDFPageObj_CreateTextObj(document, bestFont, fontSize);
        if (replacement == IntPtr.Zero || !PdfiumNative.FPDFText_SetText(replacement, text))
        {
            if (replacement != IntPtr.Zero)
            {
                PdfiumNative.FPDFPageObj_Destroy(replacement);
            }

            throw new PdfException(PdfErrorCode.Unknown, "Impossibile creare il testo di sostituzione.");
        }

        // Same position and size as the original line, same colour and drawing mode.
        if (PdfiumNative.FPDFPageObj_GetMatrix(oldObject, out var matrix))
        {
            PdfiumNative.FPDFPageObj_SetMatrix(replacement, ref matrix);
        }

        if (PdfiumNative.FPDFPageObj_GetFillColor(oldObject, out var r, out var g, out var b, out var a))
        {
            PdfiumNative.FPDFPageObj_SetFillColor(replacement, r, g, b, a);
        }

        PdfiumNative.FPDFTextObj_SetTextRenderMode(replacement, PdfiumNative.FPDFTextObj_GetTextRenderMode(oldObject));

        // The new line goes where the old one was in the drawing order; the old one is kept for an undo.
        if (!PdfiumNative.FPDFPage_RemoveObject(handle, oldObject))
        {
            PdfiumNative.FPDFPageObj_Destroy(replacement);
            throw new PdfException(PdfErrorCode.Unknown, "Impossibile sostituire la riga.");
        }

        state.Orphan(oldObject);
        if (!PdfiumNative.FPDFPage_InsertObjectAtIndex(handle, replacement, (UIntPtr)index))
        {
            // Put the original back rather than lose the line.
            PdfiumNative.FPDFPage_InsertObjectAtIndex(handle, oldObject, (UIntPtr)index);
            state.Adopt(oldObject);
            PdfiumNative.FPDFPageObj_Destroy(replacement);
            throw new PdfException(PdfErrorCode.Unknown, "Impossibile inserire il testo di sostituzione.");
        }

        if (!PdfiumNative.FPDFPage_GenerateContent(handle))
        {
            throw new PdfException(PdfErrorCode.Unknown, "Impossibile aggiornare la pagina.");
        }

        return new PdfTextEditResult(true, bestName, new string(bestMissingChars), new ObjectSwapRecord(page.Document, index, oldObject));
    }

    // ---- Helpers ------------------------------------------------------------------------

    private static string NormalizeLine(string text)
        => text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();

    private static IntPtr TextObjectAt(IntPtr pageHandle, int index)
    {
        if (index < 0 || index >= PdfiumNative.FPDFPage_CountObjects(pageHandle))
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Riga inesistente.");
        }

        var obj = PdfiumNative.FPDFPage_GetObject(pageHandle, index);
        if (obj == IntPtr.Zero || PdfiumNative.FPDFPageObj_GetType(obj) != TextObject)
        {
            throw new ArgumentException("L'oggetto indicato non è una riga di testo.", nameof(index));
        }

        return obj;
    }

    private static string ReadObjectText(IntPtr pageHandle, IntPtr obj)
    {
        var textPage = PdfiumNative.FPDFText_LoadPage(pageHandle);
        try
        {
            return ReadText(obj, textPage);
        }
        finally
        {
            if (textPage != IntPtr.Zero)
            {
                PdfiumNative.FPDFText_ClosePage(textPage);
            }
        }
    }

    private static string ReadText(IntPtr obj, IntPtr textPage)
    {
        var bytes = PdfiumNative.FPDFTextObj_GetText(obj, textPage, null, 0);
        if (bytes <= 2)
        {
            return string.Empty;
        }

        var buffer = new byte[bytes];
        PdfiumNative.FPDFTextObj_GetText(obj, textPage, buffer, bytes);
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static string FontName(IntPtr font)
    {
        if (font == IntPtr.Zero)
        {
            return string.Empty;
        }

        var length = (int)PdfiumNative.FPDFFont_GetBaseFontName(font, null, UIntPtr.Zero);
        if (length <= 1)
        {
            return string.Empty;
        }

        var buffer = new byte[length];
        PdfiumNative.FPDFFont_GetBaseFontName(font, buffer, (UIntPtr)length);
        return Encoding.UTF8.GetString(buffer, 0, length - 1);
    }

    private static (FallbackFamily Family, bool Bold, bool Italic) ClassifyFont(IntPtr font)
    {
        if (font == IntPtr.Zero)
        {
            return (FallbackFamily.Sans, false, false);
        }

        PdfiumNative.FPDFFont_GetItalicAngle(font, out var italicAngle);
        return FallbackFonts.Classify(
            FontName(font), PdfiumNative.FPDFFont_GetFlags(font), PdfiumNative.FPDFFont_GetWeight(font), italicAngle, FontFileLook(font));
    }

    /// <summary>The look the embedded font file describes (see <see cref="SfntInfo"/>); nothing for a font that is not embedded (the data would be a system substitute's).</summary>
    private static FontLook FontFileLook(IntPtr font)
    {
        if (PdfiumNative.FPDFFont_GetIsEmbedded(font) != 1
            || !PdfiumNative.FPDFFont_GetFontData(font, null, UIntPtr.Zero, out var length)
            || length == UIntPtr.Zero || (ulong)length > 64UL * 1024 * 1024)
        {
            return default;
        }

        var data = new byte[(int)length];
        return PdfiumNative.FPDFFont_GetFontData(font, data, length, out _) ? SfntInfo.Read(data) : default;
    }

    // ---- Undo records ---------------------------------------------------------------------

    /// <summary>A line edited in place: undo and redo just set the text again.</summary>
    private sealed class TextChangeRecord : PdfEditRecord
    {
        private readonly int _index;
        private readonly string _before;
        private readonly string _after;

        public TextChangeRecord(int index, string before, string after)
        {
            _index = index;
            _before = before;
            _after = after;
        }

        internal override void Apply(PdfPage page, bool undo)
        {
            var obj = TextObjectAt(page.Handle, _index);
            if (!PdfiumNative.FPDFText_SetText(obj, undo ? _before : _after) || !PdfiumNative.FPDFPage_GenerateContent(page.Handle))
            {
                throw new PdfException(PdfErrorCode.Unknown, "Impossibile ripristinare il testo.");
            }
        }

        public override void Dispose()
        {
        }
    }

    /// <summary>A line swapped for another object (the fallback-font rewrite): undo and redo swap them back and forth, the one not on the page being kept here.</summary>
    private sealed class ObjectSwapRecord : PdfEditRecord
    {
        private readonly PdfDocument _document;
        private readonly int _index;
        private IntPtr _offPage;

        public ObjectSwapRecord(PdfDocument document, int index, IntPtr offPage)
        {
            _document = document;
            _index = index;
            _offPage = offPage;
        }

        internal override void Apply(PdfPage page, bool undo)
        {
            var handle = page.Handle;
            var state = _document.EditingState;
            var current = PdfiumNative.FPDFPage_GetObject(handle, _index);
            if (current == IntPtr.Zero || _offPage == IntPtr.Zero || !PdfiumNative.FPDFPage_RemoveObject(handle, current))
            {
                throw new PdfException(PdfErrorCode.Unknown, "Impossibile ripristinare la riga.");
            }

            state.Orphan(current);
            if (!PdfiumNative.FPDFPage_InsertObjectAtIndex(handle, _offPage, (UIntPtr)_index))
            {
                PdfiumNative.FPDFPage_InsertObjectAtIndex(handle, current, (UIntPtr)_index);
                state.Adopt(current);
                throw new PdfException(PdfErrorCode.Unknown, "Impossibile ripristinare la riga.");
            }

            state.Adopt(_offPage);
            _offPage = current;
            if (!PdfiumNative.FPDFPage_GenerateContent(handle))
            {
                throw new PdfException(PdfErrorCode.Unknown, "Impossibile aggiornare la pagina.");
            }
        }

        public override void Dispose() => PdfiumRuntime.Invoke(() =>
        {
            if (_offPage != IntPtr.Zero)
            {
                _document.EditingState.Release(_offPage);
                _offPage = IntPtr.Zero;
            }

            return 0;
        });
    }

    /// <summary>A deleted line: undo puts it back, redo takes it off again.</summary>
    private sealed class ObjectRemovalRecord : PdfEditRecord
    {
        private readonly PdfDocument _document;
        private readonly int _index;
        private IntPtr _obj;
        private bool _onPage;

        public ObjectRemovalRecord(PdfDocument document, int index, IntPtr obj)
        {
            _document = document;
            _index = index;
            _obj = obj;
        }

        internal override void Apply(PdfPage page, bool undo)
        {
            var handle = page.Handle;
            var state = _document.EditingState;
            if (undo)
            {
                if (_onPage || _obj == IntPtr.Zero || !PdfiumNative.FPDFPage_InsertObjectAtIndex(handle, _obj, (UIntPtr)_index))
                {
                    throw new PdfException(PdfErrorCode.Unknown, "Impossibile ripristinare la riga.");
                }

                state.Adopt(_obj);
                _onPage = true;
            }
            else
            {
                if (!_onPage || !PdfiumNative.FPDFPage_RemoveObject(handle, _obj))
                {
                    throw new PdfException(PdfErrorCode.Unknown, "Impossibile eliminare la riga.");
                }

                state.Orphan(_obj);
                _onPage = false;
            }

            if (!PdfiumNative.FPDFPage_GenerateContent(handle))
            {
                throw new PdfException(PdfErrorCode.Unknown, "Impossibile aggiornare la pagina.");
            }
        }

        public override void Dispose() => PdfiumRuntime.Invoke(() =>
        {
            // On the page the object belongs to the page; off it, to the state - which frees it.
            if (!_onPage && _obj != IntPtr.Zero)
            {
                _document.EditingState.Release(_obj);
            }

            _obj = IntPtr.Zero;
            return 0;
        });
    }
}

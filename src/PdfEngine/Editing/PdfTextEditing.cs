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

/// <summary>What <see cref="PdfTextEditing.Insert"/> did.</summary>
/// <param name="ObjectIndex">Position of the new line among the page's objects (what <see cref="PdfTextLine.ObjectIndex"/> will say).</param>
/// <param name="FontName">The bundled font the line is drawn in.</param>
/// <param name="MissingCharacters">Characters no available font can draw (they come out as an empty box).</param>
/// <param name="Undo">Takes the line off the page, and puts it back: keep it for as long as the insertion can be undone, then <see cref="IDisposable.Dispose"/> it.</param>
public sealed record PdfTextInsertResult(int ObjectIndex, string FontName, string MissingCharacters, PdfEditRecord Undo);

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

    /// <summary>
    /// Moves line <paramref name="objectIndex"/> by (<paramref name="dx"/>, <paramref name="dy"/>) points, as the page is <em>shown</em>
    /// (x to the right, y up - so after the page's own rotation). The line keeps its index, its text and its look; any line can be moved,
    /// one that the document already had as well as one added by <see cref="Insert"/> (after a reload there is no telling them apart).
    /// </summary>
    public static PdfEditRecord Move(PdfPage page, int objectIndex, double dx, double dy)
        => PdfiumRuntime.Invoke(() =>
        {
            if (double.IsNaN(dx) || double.IsNaN(dy) || double.IsInfinity(dx) || double.IsInfinity(dy))
            {
                throw new ArgumentOutOfRangeException(nameof(dx), "Lo spostamento non è un numero valido.");
            }

            var rotation = ((PdfiumNative.FPDFPage_GetRotation(page.Handle) % 4) + 4) % 4;
            var (ux, uy) = ToUnrotatedDelta(dx, dy, rotation);
            var record = new TranslationRecord(objectIndex, ux, uy);
            record.Apply(page, undo: false);
            return (PdfEditRecord)record;
        });

    /// <summary>A shift of the page as it is shown, in the page's own (unrotated) coordinates: <see cref="ToUnrotated"/> for a vector.</summary>
    internal static (double X, double Y) ToUnrotatedDelta(double dx, double dy, int rotation)
        => rotation switch
        {
            1 => (-dy, dx),
            2 => (-dx, -dy),
            3 => (dy, -dx),
            _ => (dx, dy),
        };

    /// <summary>
    /// Adds a new line of text to the page, as page content (a text object: selectable and searchable in any reader, and
    /// afterwards editable or deletable like the lines the document already had). It is drawn in a bundled open-source
    /// font (Arial-compatible, with Latin, Greek and Cyrillic; Noto Sans for what that lacks) embedded in the document -
    /// there is no document font to borrow, since the page may have none at all.
    /// </summary>
    /// <param name="left">Where the line starts, in points on the page <em>as it is shown</em> (origin bottom-left, so after the page's own rotation).</param>
    /// <param name="top">Where the top of the letters is, same coordinates: the line hangs from this point like a text box whose top-left corner is clicked.</param>
    /// <param name="fontSize">In points.</param>
    /// <exception cref="ArgumentException">The text is empty (after turning line breaks into spaces).</exception>
    public static PdfTextInsertResult Insert(PdfPage page, double left, double top, string text, double fontSize, PdfColor color)
        => PdfiumRuntime.Invoke(() =>
        {
            var line = NormalizeLine(text);
            if (line.Length == 0)
            {
                throw new ArgumentException("Il testo è vuoto.", nameof(text));
            }

            if (!(fontSize > 0) || double.IsInfinity(fontSize))
            {
                throw new ArgumentOutOfRangeException(nameof(fontSize), "La dimensione del testo deve essere positiva.");
            }

            var handle = page.Handle;
            var document = page.Document.Handle;
            var state = page.Document.EditingState;

            // First font of the chain that draws every character; failing that, the one that draws the most.
            IntPtr bestFont = IntPtr.Zero;
            string bestName = string.Empty;
            var bestMissing = line.Distinct().ToArray();
            foreach (var (key, name) in FallbackFonts.Chain(FallbackFamily.Sans, bold: false, italic: false))
            {
                var candidate = state.GetFallbackFont(document, key, () => FallbackFonts.Load(key));
                var absent = line.Distinct().Where(c => !state.FontHasGlyph(document, candidate, c)).ToArray();
                if (bestFont == IntPtr.Zero || absent.Length < bestMissing.Length)
                {
                    bestFont = candidate;
                    bestName = name;
                    bestMissing = absent;
                }

                if (absent.Length == 0)
                {
                    break;
                }
            }

            var obj = PdfiumNative.FPDFPageObj_CreateTextObj(document, bestFont, (float)fontSize);
            if (obj == IntPtr.Zero || !PdfiumNative.FPDFText_SetText(obj, line))
            {
                if (obj != IntPtr.Zero)
                {
                    PdfiumNative.FPDFPageObj_Destroy(obj);
                }

                throw new PdfException(PdfErrorCode.Unknown, "Impossibile creare il testo.");
            }

            // The baseline, as shown, is a little under the top of the letters; then from the shown page to the page's own
            // (unrotated) coordinates, with the text turned the other way so it stands upright when shown.
            var (shownWidth, shownHeight) = page.SizeInPoints;
            var rotation = ((PdfiumNative.FPDFPage_GetRotation(handle) % 4) + 4) % 4;
            var (x, y) = ToUnrotated(left, top - fontSize * AscentRatio, shownWidth, shownHeight, rotation);
            var (a, b, c, d) = rotation switch
            {
                1 => (0.0, 1.0, -1.0, 0.0),
                2 => (-1.0, 0.0, 0.0, -1.0),
                3 => (0.0, -1.0, 1.0, 0.0),
                _ => (1.0, 0.0, 0.0, 1.0),
            };
            PdfiumNative.FPDFPageObj_Transform(obj, a, b, c, d, x, y);
            PdfiumNative.FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, color.A);

            var index = PdfiumNative.FPDFPage_CountObjects(handle);
            if (!PdfiumNative.FPDFPage_InsertObjectAtIndex(handle, obj, (UIntPtr)index))
            {
                PdfiumNative.FPDFPageObj_Destroy(obj);
                throw new PdfException(PdfErrorCode.Unknown, "Impossibile inserire il testo nella pagina.");
            }

            if (!PdfiumNative.FPDFPage_GenerateContent(handle))
            {
                throw new PdfException(PdfErrorCode.Unknown, "Impossibile aggiornare la pagina.");
            }

            return new PdfTextInsertResult(index, bestName, new string(bestMissing), new ObjectInsertionRecord(page.Document, index));
        });

    /// <summary>How far the top of the letters is above the baseline, as a fraction of the font size (a capital in Arial is 0.716 high, an ascender 0.73).</summary>
    private const double AscentRatio = 0.75;

    /// <summary>
    /// A point of the page as it is shown (<paramref name="shownWidth"/> x <paramref name="shownHeight"/>, after
    /// <paramref name="rotation"/> quarter turns clockwise) in the coordinates of the page's own content, before that rotation.
    /// </summary>
    internal static (double X, double Y) ToUnrotated(double x, double y, double shownWidth, double shownHeight, int rotation)
        => rotation switch
        {
            1 => (shownHeight - y, x),
            2 => (shownWidth - x, shownHeight - y),
            3 => (y, shownWidth - x),
            _ => (x, y),
        };

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

    /// <summary>A line moved: undo and redo shift it back and forth by the same amount (in the page's own coordinates).</summary>
    private sealed class TranslationRecord : PdfEditRecord
    {
        private readonly int _index;
        private readonly double _dx;
        private readonly double _dy;

        public TranslationRecord(int index, double dx, double dy)
        {
            _index = index;
            _dx = dx;
            _dy = dy;
        }

        internal override void Apply(PdfPage page, bool undo)
        {
            var handle = page.Handle;
            var obj = TextObjectAt(handle, _index);
            var sign = undo ? -1.0 : 1.0;
            PdfiumNative.FPDFPageObj_Transform(obj, 1, 0, 0, 1, sign * _dx, sign * _dy);
            if (!PdfiumNative.FPDFPage_GenerateContent(handle))
            {
                throw new PdfException(PdfErrorCode.Unknown, "Impossibile spostare il testo.");
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

    /// <summary>
    /// A line that was added to the page: undo takes it off (the record then holds it), redo puts it back. The object the insertion
    /// made is not remembered - a page is loaded again for every edit, so its objects are new ones each time and the handle
    /// from <see cref="Insert"/> is gone - but the line is where it was put, at <c>_index</c>, until the next undo step.
    /// </summary>
    private sealed class ObjectInsertionRecord : PdfEditRecord
    {
        private readonly PdfDocument _document;
        private readonly int _index;
        private IntPtr _offPage;

        public ObjectInsertionRecord(PdfDocument document, int index)
        {
            _document = document;
            _index = index;
        }

        internal override void Apply(PdfPage page, bool undo)
        {
            var handle = page.Handle;
            var state = _document.EditingState;
            if (undo)
            {
                var current = PdfiumNative.FPDFPage_GetObject(handle, _index);
                if (_offPage != IntPtr.Zero || current == IntPtr.Zero || PdfiumNative.FPDFPageObj_GetType(current) != TextObject
                    || !PdfiumNative.FPDFPage_RemoveObject(handle, current))
                {
                    throw new PdfException(PdfErrorCode.Unknown, "Impossibile togliere il testo inserito.");
                }

                state.Orphan(current);
                _offPage = current;
            }
            else
            {
                if (_offPage == IntPtr.Zero || !PdfiumNative.FPDFPage_InsertObjectAtIndex(handle, _offPage, (UIntPtr)_index))
                {
                    throw new PdfException(PdfErrorCode.Unknown, "Impossibile reinserire il testo.");
                }

                state.Adopt(_offPage);
                _offPage = IntPtr.Zero;
            }

            if (!PdfiumNative.FPDFPage_GenerateContent(handle))
            {
                throw new PdfException(PdfErrorCode.Unknown, "Impossibile aggiornare la pagina.");
            }
        }

        public override void Dispose() => PdfiumRuntime.Invoke(() =>
        {
            // On the page the line belongs to the page; off it, to the state - which frees it.
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

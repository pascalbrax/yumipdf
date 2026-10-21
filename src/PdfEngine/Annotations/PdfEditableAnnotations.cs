using System.Runtime.InteropServices;
using PdfEngine.Native;

namespace PdfEngine.Annotations;

/// <summary>A rectangle in PDF point space (origin bottom-left).</summary>
public readonly record struct PdfRect(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;

    public double Height => Top - Bottom;
}

/// <summary>The kinds of annotation the app lets the user place and edit.</summary>
public enum PdfAnnotationKind
{
    /// <summary>A sticky note: a small icon on the page with the text in <see cref="PdfAnnotationInfo.Contents"/>.</summary>
    Note,

    /// <summary>A stamp: a fixed picture (text in a box, or an image) placed on the page.</summary>
    Stamp,
}

/// <param name="Index">Position in the page's annotation list - what the other methods take. Removing an annotation shifts the ones after it, so read the list again after a removal.</param>
/// <param name="Contents">The note's text (<c>/Contents</c>); empty for a stamp.</param>
/// <param name="CanResize">Whether <see cref="PdfEditableAnnotations.SetRect"/> may change the size, not just the position: true only for stamps this library made (see the class remarks - a foreign stamp's appearance can misbehave when enlarged).</param>
/// <param name="Id">The private identity (<c>/PDF26Id</c>) given to an annotation the app has touched, which - unlike <paramref name="Index"/> - survives other annotations being added or removed; null until then (see <see cref="PdfEditableAnnotations.EnsureId"/>).</param>
public sealed record PdfAnnotationInfo(int Index, PdfAnnotationKind Kind, PdfRect Rect, string Contents, bool CanResize, string? Id = null);

/// <summary>
/// Notes and stamps as ordinary PDF annotations (<c>/Subtype /Text</c> and <c>/Stamp</c>): other
/// readers show them, and they stay editable here - move, resize, change the text, delete -
/// until a document is flattened. All coordinates are PDF points, origin bottom-left.
/// </summary>
/// <remarks>
/// Findings from probing PDFium 153 (docs/ARCHITECTURE.md "Annotazioni"): a note needs nothing but
/// a rectangle and /Contents (PDFium draws the yellow note icon itself); a stamp has no look of
/// its own, so its picture is page objects appended to it (<c>FPDFAnnot_AppendObject</c>) - and
/// the annotation's rectangle must be set <em>before</em> appending them, because the
/// appearance stream's bounding box is taken from the rectangle at that moment (set afterwards,
/// the box stays empty and nothing is drawn). Moving or resizing later is just changing the
/// rectangle: renderers scale the appearance's box to it.
/// <para>
/// One PDFium quirk shapes how stamps are built: when a new rectangle <em>contains</em> the
/// appearance's box, PDFium rewrites the box to the new rectangle instead of scaling to it - an
/// enlarged stamp keeps its old size inside a bigger empty frame (measured: every rectangle that
/// contains the old box does it; moves, shrinks and disjoint rectangles never do). So a stamp's
/// appearance is built far off the page (<see cref="CanonicalOrigin"/>), where no on-page
/// rectangle can ever contain it, and only then placed on the page by its rectangle: from then on
/// every move and resize is plain scaling, whatever the size. Stamps made elsewhere have their box
/// on the page and cannot be enlarged reliably, so they are marked as not resizable
/// (<see cref="PdfAnnotationInfo.CanResize"/>) - ours carry a private <c>/PDF26</c> entry.
/// </para>
/// </remarks>
public static class PdfEditableAnnotations
{
    /// <summary>Size of a note's icon.</summary>
    public const double NoteSize = 24;

    private const int FlagPrint = 4;
    private const double MinSize = 8;

    /// <summary>Where a stamp's appearance is drawn (bottom-left corner): far outside any page - see the class remarks.</summary>
    private const double CanonicalOrigin = -20000;

    /// <summary>The private entry that marks a stamp as ours: its appearance is built so that resizing it is safe.</summary>
    private const string OwnStampKey = "PDF26";

    /// <summary>The private entry holding an annotation's identity, for undo - see <see cref="EnsureId"/>.</summary>
    private const string IdKey = "PDF26Id";

    /// <summary>The page's notes and stamps (nothing else: not links, form widgets, highlights or popups), in list order.</summary>
    public static IReadOnlyList<PdfAnnotationInfo> List(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return PdfiumRuntime.Invoke(() =>
        {
            var result = new List<PdfAnnotationInfo>();
            var count = PdfiumNative.FPDFPage_GetAnnotCount(page.Handle);
            for (var index = 0; index < count; index++)
            {
                var annot = PdfiumNative.FPDFPage_GetAnnot(page.Handle, index);
                if (annot == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    var kind = PdfiumNative.FPDFAnnot_GetSubtype(annot) switch
                    {
                        PdfiumNative.FPDF_ANNOTATION_SUBTYPE.TEXT => (PdfAnnotationKind?)PdfAnnotationKind.Note,
                        PdfiumNative.FPDF_ANNOTATION_SUBTYPE.STAMP => PdfAnnotationKind.Stamp,
                        _ => null,
                    };
                    // Hidden (2) and no-view (32) annotations are not on screen, so nothing to select.
                    if (kind is null || (PdfiumNative.FPDFAnnot_GetFlags(annot) & (2 | 32)) != 0)
                    {
                        continue;
                    }

                    var rect = default(PdfiumNative.FS_RECTF);
                    if (!PdfiumNative.FPDFAnnot_GetRect(annot, ref rect))
                    {
                        continue;
                    }

                    result.Add(new PdfAnnotationInfo(
                        index,
                        kind.Value,
                        new PdfRect(rect.Left, rect.Bottom, rect.Right, rect.Top),
                        kind == PdfAnnotationKind.Note ? ReadString(annot, "Contents") : string.Empty,
                        CanResize: kind == PdfAnnotationKind.Stamp && ReadString(annot, OwnStampKey).Length > 0,
                        Id: ReadString(annot, IdKey) is { Length: > 0 } id ? id : null));
                }
                finally
                {
                    PdfiumNative.FPDFPage_CloseAnnot(annot);
                }
            }

            return (IReadOnlyList<PdfAnnotationInfo>)result;
        });
    }

    /// <summary>Adds a note whose icon is centered on (<paramref name="centerX"/>, <paramref name="centerY"/>), kept inside the page. Returns its index.</summary>
    public static int AddNote(PdfPage page, double centerX, double centerY, string text)
    {
        ArgumentNullException.ThrowIfNull(page);
        return PdfiumRuntime.Invoke(() =>
        {
            var rect = CenteredInPage(page, centerX, centerY, NoteSize, NoteSize);
            var annot = Create(page, PdfiumNative.FPDF_ANNOTATION_SUBTYPE.TEXT);
            try
            {
                SetRectOrThrow(annot, rect);
                SetStringOrThrow(annot, "Contents", text);
                SetModified(annot);
                PdfiumNative.FPDFAnnot_SetColor(annot, PdfiumNative.FPDFANNOT_COLORTYPE.Color, 255, 220, 0, 255);
                PdfiumNative.FPDFAnnot_SetFlags(annot, FlagPrint);
                return PdfiumNative.FPDFPage_GetAnnotIndex(page.Handle, annot);
            }
            catch
            {
                RemoveQuietly(page, annot);
                throw;
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        });
    }

    /// <summary>
    /// Adds a text stamp: <paramref name="text"/> in bold capitals-style type inside a box of the
    /// same color, with an optional smaller <paramref name="subtitle"/> (typically a date) under
    /// it, centered on the given point and kept inside the page. Returns its index.
    /// </summary>
    public static int AddTextStamp(PdfPage page, double centerX, double centerY, string text, PdfColor color, string? subtitle = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return PdfiumRuntime.Invoke(() =>
        {
            const float mainSize = 22f;
            const float subSize = 10f;
            const float pad = 8f;
            const float gap = 3f;
            const float stroke = 2.5f;
            var hasSubtitle = !string.IsNullOrWhiteSpace(subtitle);

            var objects = new List<IntPtr>();
            try
            {
                var main = NewText(page, text, mainSize, color);
                objects.Add(main);
                Bounds(main, out var ml, out var mb, out var mr, out var mt);
                var mainW = mr - ml;
                var mainH = mt - mb;

                var sub = IntPtr.Zero;
                float subW = 0, subH = 0, sl = 0, sb = 0;
                if (hasSubtitle)
                {
                    sub = NewText(page, subtitle!, subSize, color);
                    objects.Add(sub);
                    Bounds(sub, out sl, out sb, out var sr, out var st);
                    subW = sr - sl;
                    subH = st - sb;
                }

                var innerW = Math.Max(mainW, subW);
                var innerH = mainH + (hasSubtitle ? gap + subH : 0);
                var boxW = innerW + 2 * pad;
                var boxH = innerH + 2 * pad;
                var rect = CenteredInPage(page, centerX, centerY, boxW + stroke, boxH + stroke);
                // The picture is laid out at the canonical origin and only placed on the page afterwards.
                var canonical = new PdfRect(CanonicalOrigin, CanonicalOrigin, CanonicalOrigin + boxW + stroke, CanonicalOrigin + boxH + stroke);
                var boxLeft = (float)canonical.Left + stroke / 2;
                var boxBottom = (float)canonical.Bottom + stroke / 2;

                // Lay the objects out inside the box, each centered horizontally.
                var mainX = boxLeft + pad + (innerW - mainW) / 2 - ml;
                var mainY = boxBottom + pad + (hasSubtitle ? subH + gap : 0) - mb;
                PdfiumNative.FPDFPageObj_Transform(main, 1, 0, 0, 1, mainX, mainY);
                if (hasSubtitle)
                {
                    PdfiumNative.FPDFPageObj_Transform(sub, 1, 0, 0, 1, boxLeft + pad + (innerW - subW) / 2 - sl, boxBottom + pad - sb);
                }

                var frame = PdfiumNative.FPDFPageObj_CreateNewRect(boxLeft, boxBottom, boxW, boxH);
                if (frame == IntPtr.Zero)
                {
                    throw new PdfException(PdfErrorCode.Unknown, "Impossibile creare il bordo del timbro.");
                }

                objects.Insert(0, frame);
                PdfiumNative.FPDFPath_SetDrawMode(frame, 0, true);
                PdfiumNative.FPDFPageObj_SetStrokeColor(frame, color.R, color.G, color.B, color.A);
                PdfiumNative.FPDFPageObj_SetStrokeWidth(frame, stroke);

                return CreateStamp(page, canonical, rect, objects);
            }
            catch
            {
                foreach (var obj in objects)
                {
                    PdfiumNative.FPDFPageObj_Destroy(obj);
                }

                throw;
            }
        });
    }

    /// <summary>
    /// Adds an image stamp from straight-alpha BGRA pixels (top-down, 4 bytes per pixel), scaled
    /// so its longer side is <paramref name="longSidePt"/> points, centered on the given point and
    /// kept inside the page. Returns its index.
    /// </summary>
    public static int AddImageStamp(PdfPage page, double centerX, double centerY, byte[] bgra, int pixelWidth, int pixelHeight, double longSidePt = 160)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(bgra);
        if (pixelWidth <= 0 || pixelHeight <= 0 || bgra.Length < pixelWidth * pixelHeight * 4)
        {
            throw new ArgumentException("Pixel dell'immagine non validi.", nameof(bgra));
        }

        return PdfiumRuntime.Invoke(() =>
        {
            var scale = longSidePt / Math.Max(pixelWidth, pixelHeight);
            var rect = CenteredInPage(page, centerX, centerY, pixelWidth * scale, pixelHeight * scale);

            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            var bitmap = IntPtr.Zero;
            var image = IntPtr.Zero;
            try
            {
                bitmap = PdfiumNative.FPDFBitmap_CreateEx(pixelWidth, pixelHeight, PdfiumNative.FPDFBitmap_BGRA, pin.AddrOfPinnedObject(), pixelWidth * 4);
                image = PdfiumNative.FPDFPageObj_NewImageObj(page.Document.Handle);
                if (bitmap == IntPtr.Zero || image == IntPtr.Zero || !PdfiumNative.FPDFImageObj_SetBitmap(IntPtr.Zero, 0, image, bitmap))
                {
                    throw new PdfException(PdfErrorCode.Unknown, "Impossibile creare l'immagine del timbro.");
                }

                // The image object is a unit square scaled/placed by its matrix - at the canonical origin, like any stamp picture.
                var canonical = new PdfRect(CanonicalOrigin, CanonicalOrigin, CanonicalOrigin + rect.Width, CanonicalOrigin + rect.Height);
                var matrix = new PdfiumNative.FS_MATRIX { A = (float)rect.Width, D = (float)rect.Height, E = (float)canonical.Left, F = (float)canonical.Bottom };
                PdfiumNative.FPDFPageObj_SetMatrix(image, ref matrix);

                var objects = new List<IntPtr> { image };
                try
                {
                    var index = CreateStamp(page, canonical, rect, objects);
                    image = IntPtr.Zero; // now owned by the annotation
                    return index;
                }
                catch
                {
                    // CreateStamp empties the list of whatever the annotation took ownership of
                    // (and destroyed with it when it removed the half-built annotation).
                    if (objects.Count == 0)
                    {
                        image = IntPtr.Zero;
                    }

                    throw;
                }
            }
            finally
            {
                if (image != IntPtr.Zero)
                {
                    PdfiumNative.FPDFPageObj_Destroy(image);
                }

                // SetBitmap copied the pixels into the image object, so the source can go.
                if (bitmap != IntPtr.Zero)
                {
                    PdfiumNative.FPDFBitmap_Destroy(bitmap);
                }

                pin.Free();
            }
        });
    }

    /// <summary>Moves and/or resizes an annotation (a note keeps whatever size it is given; a stamp's picture is scaled to the new rectangle).</summary>
    public static void SetRect(PdfPage page, int index, PdfRect rect)
    {
        ArgumentNullException.ThrowIfNull(page);
        PdfiumRuntime.Invoke(() =>
        {
            var annot = Open(page, index);
            try
            {
                var clamped = ClampToPage(page, rect);
                SetRectOrThrow(annot, clamped);
                SetModified(annot);
                return 0;
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        });
    }

    /// <summary>Replaces a note's text.</summary>
    public static void SetContents(PdfPage page, int index, string text)
    {
        ArgumentNullException.ThrowIfNull(page);
        PdfiumRuntime.Invoke(() =>
        {
            var annot = Open(page, index);
            try
            {
                SetStringOrThrow(annot, "Contents", text);
                SetModified(annot);
                return 0;
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        });
    }

    /// <summary>
    /// The identity of annotation <paramref name="index"/>, given one first if it has none (a private <c>/PDF26Id</c> entry, which
    /// other readers ignore). Undo addresses annotations by identity, because deleting one and putting it back changes the
    /// positions of the others.
    /// </summary>
    public static string EnsureId(PdfPage page, int index)
    {
        ArgumentNullException.ThrowIfNull(page);
        return PdfiumRuntime.Invoke(() =>
        {
            var annot = Open(page, index);
            try
            {
                if (ReadString(annot, IdKey) is { Length: > 0 } existing)
                {
                    return existing;
                }

                var id = Guid.NewGuid().ToString("N");
                SetStringOrThrow(annot, IdKey, id);
                return id;
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        });
    }

    /// <summary>Gives annotation <paramref name="index"/> a known identity: used to put back an annotation under the one it had.</summary>
    public static void SetId(PdfPage page, int index, string id)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(id);
        PdfiumRuntime.Invoke(() =>
        {
            var annot = Open(page, index);
            try
            {
                SetStringOrThrow(annot, IdKey, id);
                return 0;
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        });
    }

    /// <summary>The current index of the annotation with identity <paramref name="id"/>, or -1 if the page has none.</summary>
    public static int IndexOfId(PdfPage page, string id)
    {
        ArgumentNullException.ThrowIfNull(page);
        return PdfiumRuntime.Invoke(() =>
        {
            var count = PdfiumNative.FPDFPage_GetAnnotCount(page.Handle);
            for (var index = 0; index < count; index++)
            {
                var annot = PdfiumNative.FPDFPage_GetAnnot(page.Handle, index);
                if (annot == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    if (ReadString(annot, IdKey) == id)
                    {
                        return index;
                    }
                }
                finally
                {
                    PdfiumNative.FPDFPage_CloseAnnot(annot);
                }
            }

            return -1;
        });
    }

    /// <summary>Deletes an annotation. The ones after it move up by one in the page's list.</summary>
    public static void Remove(PdfPage page, int index)
    {
        ArgumentNullException.ThrowIfNull(page);
        PdfiumRuntime.Invoke(() =>
        {
            if (index < 0 || index >= PdfiumNative.FPDFPage_GetAnnotCount(page.Handle) || !PdfiumNative.FPDFPage_RemoveAnnot(page.Handle, index))
            {
                throw PdfException.FromLastPdfiumError("Impossibile eliminare l'annotazione.");
            }

            return 0;
        });
    }

    // ---- internals (all called inside PdfiumRuntime.Invoke) ----------------------------

    private static IntPtr Create(PdfPage page, PdfiumNative.FPDF_ANNOTATION_SUBTYPE subtype)
    {
        var annot = PdfiumNative.FPDFPage_CreateAnnot(page.Handle, subtype);
        return annot != IntPtr.Zero ? annot : throw PdfException.FromLastPdfiumError("Impossibile creare l'annotazione.");
    }

    private static IntPtr Open(PdfPage page, int index)
    {
        var annot = index >= 0 ? PdfiumNative.FPDFPage_GetAnnot(page.Handle, index) : IntPtr.Zero;
        return annot != IntPtr.Zero ? annot : throw new ArgumentOutOfRangeException(nameof(index), "Annotazione inesistente.");
    }

    /// <summary>
    /// Builds a stamp annotation around <paramref name="objects"/>, which are already placed inside
    /// <paramref name="canonical"/> (the appearance's box), then puts it on the page at
    /// <paramref name="onPage"/>. The order is the whole trick - see the class remarks.
    /// </summary>
    private static int CreateStamp(PdfPage page, PdfRect canonical, PdfRect onPage, List<IntPtr> objects)
    {
        var annot = Create(page, PdfiumNative.FPDF_ANNOTATION_SUBTYPE.STAMP);
        var appended = 0;
        try
        {
            SetRectOrThrow(annot, canonical);
            foreach (var obj in objects)
            {
                if (!PdfiumNative.FPDFAnnot_AppendObject(annot, obj))
                {
                    throw PdfException.FromLastPdfiumError("Impossibile aggiungere il disegno al timbro.");
                }

                appended++;
            }

            SetRectOrThrow(annot, onPage);
            SetStringOrThrow(annot, OwnStampKey, "stamp");
            SetModified(annot);
            PdfiumNative.FPDFAnnot_SetFlags(annot, FlagPrint);
            var index = PdfiumNative.FPDFPage_GetAnnotIndex(page.Handle, annot);
            objects.RemoveRange(0, appended); // the annotation owns them now: the caller must not destroy them
            return index;
        }
        catch
        {
            objects.RemoveRange(0, appended);
            RemoveQuietly(page, annot);
            throw;
        }
        finally
        {
            PdfiumNative.FPDFPage_CloseAnnot(annot);
        }
    }

    private static IntPtr NewText(PdfPage page, string text, float size, PdfColor color)
    {
        var obj = PdfiumNative.FPDFPageObj_NewTextObj(page.Document.Handle, "Helvetica-Bold", size);
        if (obj == IntPtr.Zero)
        {
            throw new PdfException(PdfErrorCode.Unknown, "Impossibile creare il testo del timbro.");
        }

        if (!PdfiumNative.FPDFText_SetText(obj, text))
        {
            PdfiumNative.FPDFPageObj_Destroy(obj);
            throw new PdfException(PdfErrorCode.Unknown, "Impossibile impostare il testo del timbro.");
        }

        PdfiumNative.FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, color.A);
        return obj;
    }

    private static void Bounds(IntPtr obj, out float left, out float bottom, out float right, out float top)
    {
        if (!PdfiumNative.FPDFPageObj_GetBounds(obj, out left, out bottom, out right, out top))
        {
            throw new PdfException(PdfErrorCode.Unknown, "Impossibile misurare il timbro.");
        }
    }

    private static PdfRect CenteredInPage(PdfPage page, double cx, double cy, double width, double height)
        => ClampToPage(page, new PdfRect(cx - width / 2, cy - height / 2, cx + width / 2, cy + height / 2));

    /// <summary>Keeps a rectangle inside the page, shifting it before shrinking it (and never below a minimum size).</summary>
    private static PdfRect ClampToPage(PdfPage page, PdfRect rect)
    {
        var (pageW, pageH) = page.SizeInPoints;
        var width = Math.Clamp(Math.Max(rect.Width, MinSize), MinSize, pageW);
        var height = Math.Clamp(Math.Max(rect.Height, MinSize), MinSize, pageH);
        var left = Math.Clamp(rect.Left, 0, pageW - width);
        var bottom = Math.Clamp(rect.Bottom, 0, pageH - height);
        return new PdfRect(left, bottom, left + width, bottom + height);
    }

    private static void SetRectOrThrow(IntPtr annot, PdfRect rect)
    {
        var native = new PdfiumNative.FS_RECTF { Left = (float)rect.Left, Bottom = (float)rect.Bottom, Right = (float)rect.Right, Top = (float)rect.Top };
        if (!PdfiumNative.FPDFAnnot_SetRect(annot, ref native))
        {
            throw PdfException.FromLastPdfiumError("Impossibile impostare l'area dell'annotazione.");
        }
    }

    private static void SetStringOrThrow(IntPtr annot, string key, string value)
    {
        if (!PdfiumNative.FPDFAnnot_SetStringValue(annot, key, value))
        {
            throw PdfException.FromLastPdfiumError($"Impossibile impostare '{key}' dell'annotazione.");
        }
    }

    /// <summary>Stamps /M (modification date, <c>D:YYYYMMDDHHmmSS</c>) - what other readers show as "last modified".</summary>
    private static void SetModified(IntPtr annot)
        => PdfiumNative.FPDFAnnot_SetStringValue(annot, "M", "D:" + DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture));

    private static string ReadString(IntPtr annot, string key)
    {
        var length = PdfiumNative.FPDFAnnot_GetStringValue(annot, key, null, 0);
        if (length <= 2)
        {
            return string.Empty;
        }

        var buffer = new byte[length];
        PdfiumNative.FPDFAnnot_GetStringValue(annot, key, buffer, length);
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)length - 2);
    }

    /// <summary>Undoes a half-built annotation; failure to do so is not worth masking the original error.</summary>
    private static void RemoveQuietly(PdfPage page, IntPtr annot)
    {
        var index = PdfiumNative.FPDFPage_GetAnnotIndex(page.Handle, annot);
        if (index >= 0)
        {
            PdfiumNative.FPDFPage_RemoveAnnot(page.Handle, index);
        }
    }
}

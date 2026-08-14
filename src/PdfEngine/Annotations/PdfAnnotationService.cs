using PdfEngine.Native;

namespace PdfEngine.Annotations;

public readonly record struct PdfColor(byte R, byte G, byte B, byte A = 255);

/// <summary>
/// Markup annotations (highlight, note, ink, stamp) via PDFium's flat fpdf_annot.h API.
/// Unlike AcroForm field access, these calls do not require a form-fill environment.
/// </summary>
public static class PdfAnnotationService
{
    /// <summary>
    /// Adds a highlight annotation covering <paramref name="rectInPoints"/> (PDF point
    /// coordinates, origin bottom-left) on <paramref name="page"/>.
    /// </summary>
    public static void AddHighlight(PdfPage page, (float Left, float Bottom, float Right, float Top) rectInPoints, PdfColor color)
    {
        ArgumentNullException.ThrowIfNull(page);

        lock (PdfiumRuntime.Gate)
        {
            var annot = PdfiumNative.FPDFPage_CreateAnnot(page.Handle, PdfiumNative.FPDF_ANNOTATION_SUBTYPE.HIGHLIGHT);
            if (annot == IntPtr.Zero)
            {
                throw PdfException.FromLastPdfiumError("Impossibile creare l'annotazione di evidenziazione.");
            }

            try
            {
                var rect = new PdfiumNative.FS_RECTF
                {
                    Left = rectInPoints.Left,
                    Bottom = rectInPoints.Bottom,
                    Right = rectInPoints.Right,
                    Top = rectInPoints.Top,
                };
                if (!PdfiumNative.FPDFAnnot_SetRect(annot, ref rect))
                {
                    throw PdfException.FromLastPdfiumError("Impossibile impostare l'area dell'evidenziazione.");
                }

                var quad = new PdfiumNative.FS_QUADPOINTSF
                {
                    X1 = rectInPoints.Left, Y1 = rectInPoints.Top,
                    X2 = rectInPoints.Right, Y2 = rectInPoints.Top,
                    X3 = rectInPoints.Left, Y3 = rectInPoints.Bottom,
                    X4 = rectInPoints.Right, Y4 = rectInPoints.Bottom,
                };
                if (!PdfiumNative.FPDFAnnot_AppendAttachmentPoints(annot, ref quad))
                {
                    throw PdfException.FromLastPdfiumError("Impossibile impostare l'area di testo evidenziata.");
                }

                if (!PdfiumNative.FPDFAnnot_SetColor(annot, PdfiumNative.FPDFANNOT_COLORTYPE.Color, color.R, color.G, color.B, color.A))
                {
                    throw PdfException.FromLastPdfiumError("Impossibile impostare il colore dell'evidenziazione.");
                }
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }
        }
    }

    // TODO(roadmap M2): note/free-text, ink (disegno a mano libera) e stamp seguono lo
    // stesso schema (FPDFPage_CreateAnnot con il subtype relativo); ink richiede in più
    // FPDFAnnot_AddInkStroke per ogni tratto disegnato dall'utente.
}

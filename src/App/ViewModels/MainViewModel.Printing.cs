using PdfEngine.Forms;
using Printing;

namespace Pdf26.ViewModels;

public partial class MainViewModel
{
    public async Task<PdfPrintSnapshot> CreatePrintSnapshotAsync()
    {
        // A click on Stampa first commits the focused form editor. Its work is queued here
        // with rendering, so wait for those edits before serializing the current document.
        await _renderTask;
        var document = _document ?? throw new InvalidOperationException("Nessun documento aperto.");
        var changes = _designFields.Select(field => (PdfFormChange)new AddFieldChange(field.PageIndex, field.ToSpec())).ToArray();
        // Copy while still on the UI thread: another OpenAsync cannot dispose document
        // between capturing its reference and the background worker reading its bytes.
        var bytes = document.SaveToBytes(removeSecurity: true);
        return await Task.Run(() => PdfPrintSnapshot.FromBytes(bytes, changes));
    }
}

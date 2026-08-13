# Architettura

## Progetti

```
26PDF.slnx
├── src/PdfEngine    Libreria .NET pura: binding PDFium + logica documento/pagina/organizzazione/annotazioni/moduli.
├── src/Printing     Stampa via System.Drawing.Printing (GDI classico). Separata dall'app WinUI3 (vedi sotto).
├── src/App          App WinUI3 (Windows App SDK), unpackaged. UI, comando riga di comando, risoluzione sorgenti.
└── tests/PdfEngine.Tests   Test xUnit su PdfEngine.
```

`App` referenzia `PdfEngine` e `Printing`. `Printing` referenzia `PdfEngine`. `PdfEngine` non ha dipendenze verso gli altri progetti.

## Perché PDFium

PDFium (https://pdfium.googlesource.com/pdfium/) è il motore di rendering PDF di Chromium: gratuito,
licenza permissiva (Apache-2.0 per i binari precompilati di `bblanchon/pdfium-binaries`, nessuna royalty),
supporta rendering, AcroForm, annotazioni e organizzazione pagine (merge/split) in un'unica libreria nativa.
Non supporta XFA (moduli dinamici Adobe) - per scelta del progetto (vedi risposta utente in fase di scoping),
questo non è un requisito.

I binari nativi arrivano dal pacchetto NuGet `bblanchon.PDFium.Win32`, che include `runtimes/{win-x64,win-x86,win-arm64}/native/pdfium.dll` risolti automaticamente da MSBuild in base al RID di pubblicazione.

## PdfEngine: binding nativi

`src/PdfEngine/Native/PdfiumNative.cs` dichiara la superficie P/Invoke usata. Punti importanti:

- **Calling convention**: PDFium usa `__stdcall` su Windows (`FPDF_CALLCONV`), non la convenzione di default di .NET - ogni `DllImport` specifica esplicitamente `CallingConvention.StdCall`.
- **Buffer del documento**: `FPDF_LoadMemDocument64` non copia i byte passati; PDFium continua a leggere dal buffer per tutta la vita del documento. `PdfDocument` mantiene il `byte[]` sorgente pinnato (`GCHandle`) fino alla `Dispose()`.
- **Annotazioni di markup** (evidenziazioni, note, timbri) usano l'API "flat" `fpdf_annot.h`, senza bisogno di un ambiente di form-fill.

## AcroForm filling: attenzione al layout dello struct

`FPDF_FORMFILLINFO` (usato da `PdfFormFiller` per leggere nome/tipo/valore dei campi) è uno struct C con ~30
puntatori a funzione, e PDFium usa questo struct per callback interattive (ridisegno, focus, ecc.).
**Il layout dichiarato in `PdfiumNative.FPDF_FORMFILLINFO` deve essere riverificato contro l'header
`public/fpdf_formfill.h` effettivamente incluso nella versione di `bblanchon.PDFium.Win32` referenziata**
prima di usare in produzione qualunque funzionalità interattiva (focus, scrittura campi) - un layout errato
non genera un errore di compilazione, ma corrompe lo stack nativo a runtime.

Il percorso di **sola lettura** dei campi (`PdfFormFiller.ListFields()`) è stato scritto per essere sicuro con
uno struct "quasi vuoto" (tutti i puntatori a funzione a `null`, solo `version = 1`), perché non attraversa mai
il percorso interattivo di PDFium. La **scrittura** dei valori (`PdfFormFiller.SetFieldValue`) è deliberatamente
non implementata in questa milestone - vedi [ROADMAP.md](ROADMAP.md) M2.

## Stampa: perché non l'API di stampa WinRT nativa

WinUI3 non espone ancora un equivalente diretto di `Windows.UI.Xaml.Printing.PrintDocument` (UWP): il tipo
WinRT `Windows.Graphics.Printing.PrintDocument.AddPage` richiede `Windows.UI.Xaml.UIElement`, non compatibile
con `Microsoft.UI.Xaml.UIElement` di WinUI3. Per questo `src/Printing` usa `System.Drawing.Printing`
(GDI classico via Windows Forms), stabile e maturo, disegnando un bitmap per pagina (via
`PdfPage.RenderToBgra`) sul contesto grafico di stampa. È in un progetto separato perché
`UseWinUI` e `UseWindowsForms` nello stesso `.csproj` mandano in conflitto il compilatore XAML.

## Nota sul nome dell'assembly

L'app si chiama **PDF26** (non 26PDF): il generatore di sorgenti CsWinRT (usato da WinUI3/Windows App SDK
per i binding WinRT) deriva nomi di tipo interni dal nome assembly e non gestisce correttamente un nome che
**inizia con una cifra** ("26PDF" → generava codice non compilabile, `PDFVtableClasses` non trovato, ecc. -
verificato scaffoldando questo progetto). "PDF26" inizia con una lettera e non è affetto dal problema;
`AssemblyName`, `<Product>`, `Window.Title` e la cartella di cache temporanea usano tutti "PDF26".

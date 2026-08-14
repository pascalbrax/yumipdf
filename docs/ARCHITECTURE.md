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

## Thread-safety: `PdfiumRuntime.Gate`

PDFium (compilato qui senza il supporto opzionale multi-thread) **non è sicuro da chiamare da più di un
thread alla volta** - nemmeno per pagine/documenti diversi. `PdfDocument` e `PdfPage` prendono il lock
statico `PdfiumRuntime.Gate` (`lock`/`Monitor`, rientrante sullo stesso thread) attorno a ogni chiamata
nativa, cosi' che i chiamanti non debbano occuparsene.

Questo non era vero nella prima versione del rendering "a due fasi" di `MainViewModel`
(`RenderAllPagesAsync`): un cambio di zoom mentre il rendering delle pagine dall'apertura era ancora in
corso avviava un **secondo** giro di rendering in parallelo al primo sullo stesso `PdfDocument`, da un
thread diverso - due chiamate PDFium concorrenti, che corrompevano lo stato nativo e mandavano in crash
il processo (riprodotto e confermato con un documento reale dalle pagine molto grandi, dove il rendering di
ogni pagina impiega abbastanza da lasciare la finestra di sovrapposizione facilmente raggiungibile
cliccando zoom subito dopo l'apertura). Oltre al lock, `MainViewModel` ora *accoda* esplicitamente i giri di
rendering successivi dietro quello in corso (invece di limitarsi ad annullarlo) cosi' da non bloccare il
thread UI in attesa del lock e da non sprecare lavoro renderizzando pagine per una collezione che la
richiesta piu' recente sta per svuotare.

## Zoom istantaneo ("fake zoom")

Su un cambio di zoom, `RenderAllPagesAsync`/`RunRenderAsync` non svuota piu' `Pages` e non riparte da
placeholder vuoti: riusa le istanze `PdfPageViewModel` gia' a schermo e ne ridimensiona subito
`PixelWidth`/`PixelHeight` (`PdfPageViewModel.Resize`, nessuna chiamata PDFium - usa le dimensioni in punti
gia' note). Il bitmap corrente resta quello vecchio finche' non arriva quello nuovo: `Image` con
`Stretch="Fill"` lo scala per riempire la nuova dimensione, dando l'illusione di uno zoom istantaneo (pixelato)
mentre il re-render vero e proprio procede in background e sostituisce il bitmap pagina per pagina senza
scatti. Questo path si attiva solo per un refresh zoom sullo stesso documento (`isNewDocument: false`);
l'apertura di un nuovo documento ricostruisce sempre `Pages` da zero, dato che non c'e' nulla da "fingere".

## Zoom massimo per documento

Lo stesso documento con pagine molto grandi (formato architettonico, ~40x24 pollici) espone un secondo
problema: a zoom elevato il bitmap per pagina puo' superare la dimensione massima di texture tipica
dell'hardware Direct3D (~16384px per lato) oltre a diventare enorme in memoria (oltre 1 GB per pagina).
`MainViewModel.ComputeMaxZoomForDocument` limita lo zoom massimo selezionabile in base alla pagina piu'
grande del documento aperto, restando sotto una soglia di sicurezza (`SafeMaxPixelDimension`, 8192px).

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

## Associazione file .pdf: perché solo HKCU e nessuna forzatura del default

`Services/FileAssociationService.cs` registra PDF26 come app candidata per aprire i `.pdf` **solo su richiesta
esplicita dell'utente** (toggle nel menu Opzioni, non in fase di installazione - vedi
[ROADMAP.md](ROADMAP.md) M1/M4). Scrive solo sotto `HKEY_CURRENT_USER` (nessun privilegio elevato richiesto,
reversibile disattivando il toggle) seguendo il pattern ufficiale "RegisteredApplications" +
`OpenWithProgids`. Non tenta di forzare PDF26 come app *predefinita*: dal Vista in poi Windows non permette a
un programma di scrivere l'hash `UserChoice` che determina il gestore predefinito per utente (misura
anti-hijacking) - anche volendo, non sarebbe possibile farlo in modo affidabile né appropriato. Il pulsante
"Apri le impostazioni predefinite di Windows" nello stesso dialogo apre `ms-settings:defaultapps`, dove
l'utente completa la scelta dalla UI di sistema.

## Nota sul nome dell'assembly

L'app si chiama **PDF26** (non 26PDF): il generatore di sorgenti CsWinRT (usato da WinUI3/Windows App SDK
per i binding WinRT) deriva nomi di tipo interni dal nome assembly e non gestisce correttamente un nome che
**inizia con una cifra** ("26PDF" → generava codice non compilabile, `PDFVtableClasses` non trovato, ecc. -
verificato scaffoldando questo progetto). "PDF26" inizia con una lettera e non è affetto dal problema;
`AssemblyName`, `<Product>`, `Window.Title` e la cartella di cache temporanea usano tutti "PDF26".

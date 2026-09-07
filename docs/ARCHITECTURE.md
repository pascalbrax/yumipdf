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

## `ConfigureAwait` nel ViewModel: mai `false`

`MainViewModel` (e in generale qualunque codice sotto `src/App`, a differenza di `PdfEngine`/`Printing`
che sono librerie pure) non deve mai usare `.ConfigureAwait(false)`: i suoi metodi `async` continuano a
toccare stato che deve restare sul thread UI - proprietà `[ObservableProperty]` che pilotano `x:Bind`, e
`RenderAllPagesAsync`/`RunRenderAsync` il cui stesso `ConfigureAwait(true)` funziona solo se esiste ancora
un `SynchronizationContext` della UI a cui tornare. Un `ConfigureAwait(false)` in `OpenAsync` (sulla
risoluzione della sorgente) era proprio questo bug: dopo quel punto tutto il resto del metodo - incluso
l'`await` sul rendering e l'assegnazione finale `StatusMessage = null` - girava fuori dal thread UI, e
l'aggiornamento della barra di stato a fine caricamento non arrivava in modo affidabile (il testo "Apertura
di ..." restava visibile anche a documento perfettamente caricato). `FileSourceResolver` invece usa
correttamente `ConfigureAwait(false)` al suo interno: è un servizio di libreria che non tocca mai la UI.

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

## AcroForm filling (M2): layout verificato, scrittura implementata

`FPDF_FORMFILLINFO` (lo struct C con ~35 campi/puntatori a funzione che PDFium usa per le callback
interattive - ridisegno, focus, timer, ecc.) è stato **verificato campo per campo** contro l'header reale
`public/fpdf_formfill.h`, scaricato dalla release GitHub di `bblanchon/pdfium-binaries` che corrisponde
esattamente alla versione referenziata (`bblanchon.PDFium.Win32` 153.0.7999 → tag `chromium/7999`).
La verifica ha trovato e corretto due bug reali nel binding scritto "a memoria" durante lo scaffolding:

1. Mancava il campo `xfa_disabled` (`FPDF_BOOL`, 4 byte) tra `m_pJsPlatform` e `FFI_DisplayCaret` - con
   `version = 1` (quello usato qui) PDFium non lo legge mai, quindi non causava problemi in pratica, ma è
   stato aggiunto comunque per un layout identico all'header a prescindere dalla versione usata.
2. `FORM_ReplaceSelection` è dichiarata `void` nell'header, non `FPDF_BOOL` - il binding dichiarava un
   valore di ritorno inesistente (ABI in pratica innocuo qui, ma comunque sbagliato: da correggere).

**Se la versione di `bblanchon.PDFium.Win32` in `PdfEngine.csproj` cambia, questa verifica va ripetuta**:
scaricare `https://github.com/bblanchon/pdfium-binaries/releases/download/chromium%2F<build>/pdfium-win-x64.tgz`
(stesso `<build>` della versione NuGet, es. `153.0.7999` → `chromium/7999`), estrarre `include/fpdf_formfill.h`
e confrontare campo per campo con `PdfiumNative.FPDF_FORMFILLINFO`.

### Scrittura: nessun "setter" diretto, bisogna simulare l'interazione

PDFium non espone un modo per impostare direttamente il valore di un campo: bisogna passare dallo stesso
percorso che userebbe un utente vero.

- **Campi di testo** (`PdfFormFiller.SetFieldValue`): `FORM_SetFocusedAnnot` (focus) → `FORM_SelectAllText`
  (seleziona tutto) → `FORM_ReplaceSelection` (sostituisce con il nuovo testo) → `FORM_ForceToKillFocus`
  (commit, come se l'utente avesse tabbato via dal campo).
- **Checkbox/radio** (`PdfFormFiller.SetChecked`): non esiste un `FPDFAnnot_SetChecked`. Si simula un click
  del mouse al centro del rettangolo del widget (`FPDFAnnot_GetRect` + `FORM_OnLButtonDown`/`FORM_OnLButtonUp`),
  lasciando che la gestione interna dei widget di PDFium se ne occupi (per i radio, deseleziona
  automaticamente gli altri pulsanti dello stesso gruppo). Se il campo è già nello stato richiesto
  l'operazione è un no-op, altrimenti un secondo click lo farebbe scattare di nuovo (spuntando/togliendo lo
  spunto per errore).

Entrambi i percorsi **richiedono `FORM_OnAfterLoadPage`** sia stato chiamato per quella pagina prima di
qualunque funzione `FORM_*` interattiva - non solo per il rendering come indicava un commento precedente in
questo file: senza, `FORM_SetFocusedAnnot` fallisce in modo pulito (ritorna `false`, nessun crash) ma
fallisce comunque. Scoperto testando dal vivo contro un PDF compilabile reale (vedi sotto).
`PdfFormFiller.RunOnFieldAnnot` chiama `FORM_OnAfterLoadPage`/`FORM_OnBeforeClosePage` attorno a ogni
operazione.

Per questo, a differenza della sola lettura (che può usare uno struct "quasi vuoto"), la scrittura richiede
callback native reali e non nulle: `FFI_Invalidate`, `FFI_SetCursor`, `FFI_SetTimer`/`FFI_KillTimer`,
`FFI_GetPage`, `FFI_ExecuteNamedAction` (gli unici marcati "Implementation Required: yes" per `version = 1`
nell'header). `PdfFormFiller` li implementa come no-op innocui (tranne `FFI_GetPage`, che deve rispondere con
l'handle di pagina corretto: tiene un singolo "slot attivo" - pagina/indice - impostato prima di ogni
operazione, sufficiente perché `SetFieldValue`/`SetChecked` toccano sempre una sola pagina alla volta) e li
tiene vivi come campi d'istanza per tutta la vita di `PdfFormFiller` (i delegate convertiti in puntatori a
funzione via `Marshal.GetFunctionPointerForDelegate` non devono essere raccolti dal GC finché PDFium può
ancora richiamarli).

### `Flatten`: rendere permanenti i valori compilati

`PdfPage.Flatten()` (`FPDFPage_Flatten`, `fpdf_flatten.h`) disegna le appearance stream di annotazioni e
widget direttamente nel contenuto della pagina e rimuove gli oggetti interattivi - tipicamente l'ultimo passo
prima di `PdfDocument.SaveAs` quando si consegna un modulo compilato. Non richiede un ambiente di form-fill
(opera direttamente sulle appearance stream della pagina), quindi vive su `PdfPage`, non su `PdfFormFiller`.

### Verifica end-to-end

L'intero percorso (apertura → `ListFields` → `SetFieldValue` su 33 campi di testo con caratteri accentati →
`SetChecked` su una checkbox → `Flatten` → `SaveAs` → riapertura del file salvato → rendering della pagina)
è stato verificato con un vero modulo compilabile comunale ("Notifica inizio lavori di costruzione"): tutti i
valori risultano scritti correttamente, la checkbox spuntata, e dopo il flatten `ListFields()` sul file
riaperto ritorna 0 campi (i widget sono stati sostituiti da contenuto di pagina statico, come atteso). I test
automatici in `tests/PdfEngine.Tests/PdfFormFillerTests.cs` coprono l'inizializzazione dell'ambiente form e i
percorsi di errore (tipo di campo sbagliato per `SetFieldValue`/`SetChecked`), ma non un documento AcroForm
reale - servirebbe un PDF compilabile di dimensioni ridotte da includere nel repository come fixture di test
(non fatto qui: il file usato per la verifica è un documento personale dell'utente, non incluso nel
repository).

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

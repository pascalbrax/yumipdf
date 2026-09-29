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

## "Adatta a larghezza"/"Adatta a pagina": solo la pagina corrente, non riadattano da soli scorrendo

`MainWindow.FitWidthToViewportAsync`/`OnFitPageClicked` calcolano lo zoom sulla larghezza/dimensioni
della pagina indicata da `MainViewModel.CurrentPageIndex` - mai la prima pagina del documento, mai la
più larga. In un documento con formati misti (una pagina orizzontale tra pagine verticali, per esempio)
adattare a una pagina diversa da quella che si sta guardando lascia comunque quella corrente sbagliata,
qualunque sia l'altra pagina scelta come riferimento - era già capitato con la pagina più larga usata in
un tentativo precedente di questa stessa funzione, per un motivo diverso (vedi sotto).

Scelta esplicita dell'utente, non un'assunzione: cambiando pagina (frecce, scorrimento, ricerca) mentre
"Adatta a larghezza" è attivo, lo zoom **non si ricalcola da solo** sulla nuova pagina - resta quello
calcolato per la pagina su cui era stato premuto il pulsante, finché non lo si preme di nuovo. Non è un
taglio per compromesso: era l'alternativa esplicitamente scartata (l'altra avrebbe fatto ricalcolare lo
zoom ad ogni pagina, come un vero "adatta sempre", cambiando continuamente ingrandimento scorrendo un
documento a formati misti).

Quello che invece resta un riadattamento automatico: se lo spazio disponibile per le pagine cambia
restando sulla stessa pagina (si apre/chiude la zona miniature o il pannello moduli, si ridimensiona la
finestra), `OnPagesViewportSizeChanged` fa ripartire il calcolo - ma sempre per la pagina corrente, mai
per un'altra. È lo stesso meccanismo introdotto per il bug precedente ("il documento non era più
centrato aprendo le miniature"): quel bug aveva due cause, lo zoom che non si aggiornava quando cambiava
lo spazio disponibile (`IsFitWidthActive` come modalità persistente, non un calcolo una tantum) e l'uso
della pagina più larga del documento invece di quella attuale - la prima parte del fix resta valida
(l'`IsFitWidthActive` continua a seguire i cambi di spazio), la seconda è quella corretta qui (ora è la
pagina corrente, non la più larga).

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

## Pannello "Compila modulo": tre bug trovati testando dal vivo la UI

Collegare `PdfFormFiller` a un pannello interattivo (`MainWindow`'s SplitView, `FormFieldEntryViewModel`) ha
esposto tre problemi che non emergevano dai test automatici né dalla verifica a riga di comando di M2 - tutti
trovati eseguendo davvero l'app (screenshot + click/digitazione simulati), non per ispezione del codice:

1. **Il valore digitato non appariva sulla pagina finché non si appiattiva.** Causa: `FPDF_RenderPageBitmap`
   disegna solo l'ultima appearance stream *generata* di un widget; una modifica interattiva del valore non la
   rigenera da sola. Serve `FPDF_FFLDraw` (chiamata subito dopo `FPDF_RenderPageBitmap`, stesso
   bitmap/pagina/rettangolo) per sovrapporre lo stato interattivo corrente. Aggiunta
   `PdfPage.RenderWithFormOverlay`/`PdfFormFiller.RenderPageToBgra`, usata da `MainViewModel` ogni volta che
   c'è un `PdfFormFiller` aperto (sia per il rendering iniziale sia per il refresh dopo un edit - altrimenti un
   valore già scritto "sparirebbe" al primo cambio di zoom, che ri-renderizza tutte le pagine). Anche
   `FPDF_FFLDraw` richiede `FORM_OnAfterLoadPage`/`FORM_OnBeforeClosePage` attorno a sé, come le funzioni
   `FORM_*` interattive.
2. **`SetFieldValue` scriveva il valore "vecchio" (quello prima dell'ultima modifica).** Causa: in WinUI3 il
   binding `TwoWay` di `TextBox.Text` si aggiorna sull'evento `LostFocus` - lo stesso evento a cui era
   agganciato il commit verso PDFium - e l'ordine fra "il framework scrive il testo modificato nella
   proprietà" e "il nostro handler legge quella proprietà" non è garantito. Soluzione: `TextChanged` aggiorna
   `Value` a ogni tasto (niente `TwoWay` su `Text`), così quando `LostFocus` scatta il commit legge un valore
   già aggiornato per costruzione, non per tempismo. Applicata la stessa cautela alla `CheckBox` (si legge
   `checkBox.IsChecked` direttamente nell'handler invece di fidarsi di un `TwoWay`).
3. **Crash nativo del processo** (nessuna eccezione gestita, nessuna riga in `crash.log`: un vero
   *access violation*) appiattendo il documento subito dopo averlo compilato. Causa più probabile: si
   chiamava `FPDFPage_Flatten` su ogni pagina mentre l'ambiente di form-fill interattivo
   (`FPDFDOC_InitFormFillEnvironment`) del `PdfFormFiller` era ancora attivo sullo stesso documento. Fix:
   `MainViewModel.SaveFilledFormAsync` ora chiude il `PdfFormFiller` (`FPDFDOC_ExitFormFillEnvironment`) e
   svuota il pannello **prima** di appiattire, non dopo - il che è anche coerente con l'avviso già mostrato
   all'utente ("non sarà più possibile modificare i campi in questa sessione").

Morale per chi tocca ancora quest'area: **il livello PdfEngine testato in isolamento (M2, riga di comando) non
basta a garantire che l'integrazione con un'app interattiva sia corretta** - i tre bug sopra emergono solo
quando c'è un vero ciclo UI (digitare → perdere il focus → renderizzare di nuovo → salvare) in sequenza reale,
non nell'ordine "pulito" con cui li si testerebbe uno alla volta da codice.

## Stampa: perché non l'API di stampa WinRT nativa

WinUI3 non espone ancora un equivalente diretto di `Windows.UI.Xaml.Printing.PrintDocument` (UWP): il tipo
WinRT `Windows.Graphics.Printing.PrintDocument.AddPage` richiede `Windows.UI.Xaml.UIElement`, non compatibile
con `Microsoft.UI.Xaml.UIElement` di WinUI3. Per questo `src/Printing` usa `System.Drawing.Printing`
(GDI classico via Windows Forms), stabile e maturo, disegnando un bitmap per pagina (via
`PdfPage.RenderToBgra`) sul contesto grafico di stampa. È in un progetto separato perché
`UseWinUI` e `UseWindowsForms` nello stesso `.csproj` mandano in conflitto il compilatore XAML.

## Miniature: Cover Flow verticale e thread

`Views/CoverFlowStrip` è la zona a scomparsa a sinistra (`SplitView` esterno in `MainWindow.xaml`). Mostra la pagina
centrale frontale e le altre inclinate in prospettiva (`PlaneProjection`), rimpicciolite, sovrapposte e sfumate. Le
pagine *dopo* quella centrale (sotto) hanno il lato in basso, il più vicino a chi guarda, più lungo, come fogli su un
tavolo; quelle *prima* si aprono a ventaglio specchiate.

- **Costo indipendente dalla lunghezza del documento**: esistono solo 15 "slot" riutilizzati per le pagine più vicine al
  centro. La posa di ogni slot è una funzione pura della distanza (frazionaria, animata) dal centro, quindi il movimento
  è continuo per costruzione. L'animazione è una molla criticamente smorzata avanzata a ogni fotogramma
  (`CompositionTarget.Rendering`), attiva solo mentre qualcosa si muove.
- **Hit test proprio**: XAML testerebbe un elemento inclinato contro il suo rettangolo di layout non trasformato, quindi
  gli slot non sono hit-test visibili e il clic viene tradotto in pagina dagli stessi numeri usati per disegnarli.
- **Sfogliare non muove il documento**: solo il clic (o Invio) sulla pagina, `PageActivated`. In senso inverso,
  scorrere il documento aggiorna `MainViewModel.CurrentPageIndex` (calcolato dalla pagina al centro del viewport) e la
  striscia la segue; durante lo scorrimento animato avviato dalla finestra stessa la lettura è sospesa 800 ms, perché le
  posizioni intermedie non sono una scelta dell'utente.
- **Accessibilità**: `CoverFlowStripAutomationPeer` la espone come cursore sui numeri di pagina (valore = pagina
  centrale, "Invoke" = apri la pagina centrale). Serve anche ai test con UI Automation.

**Generazione delle miniature** (`MainViewModel.BeginThumbnails` / `RunThumbnailLoopAsync`): parte solo quando la vista
principale è completamente popolata (ogni pagina ha il suo bitmap) e solo per documenti con 3+ pagine; è la stessa
condizione che rende visibili pulsante, maniglia e striscia. "Thread separato" significa fuori dal thread UI: un task del
thread pool renderizza una pagina alla volta. Non può significare un secondo thread PDFium, perché la libreria in questa
build non è thread-safe e ogni chiamata passa comunque dall'unico worker di `PdfiumRuntime`; una pagina per volta tiene
brevi quelle chiamate, così uno zoom o la modifica di un campo aspettano al massimo una miniatura. L'ordine è "la più
vicina alla pagina centrale della striscia per prima". Le miniature usano lo stesso rendering della vista (con i campi
compilati), si rigenerano per la pagina modificata dopo la modifica di un campo, e prima di appiattire e salvare il ciclo
viene fermato e atteso, perché sta per sparire il `PdfFormFiller` che usa.

**Impostazioni**: `AppSettings.ThumbnailPanelOpen` (chiusa alla prima esecuzione, poi come l'ha lasciata l'utente).
`MainViewModel` tiene un solo oggetto `AppSettings` e lo salva intero: salvare un valore con un `AppSettings` nuovo
azzererebbe tutti gli altri.

## Ricerca testo e OCR

La ricerca (barra a scomparsa in cima all'area pagine, `Icon="Find"`, Ctrl+F - all'apertura il focus
tastiera va nella casella, con un rinvio di un ciclo del dispatcher perché la barra deve prima passare
da `Collapsed` a `Visible` ed essere disposta prima che `Focus()` funzioni) è **per pagina, non per
occorrenza**: trova quali pagine contengono la parola cercata (confronto senza distinzione di
maiuscole/minuscole, `string.Contains(..., OrdinalIgnoreCase)`) e ci salta. Ogni occorrenza sulla pagina
attiva viene evidenziata (rettangolo semitrasparente, `PdfPageViewModel.Highlights`) - un overlay
esclusivamente a schermo, mai scritto nel PDF: per il testo nativo usa l'API di ricerca di PDFium
(`FPDFText_FindStart/FindNext` + `FPDFText_CountRects/GetRect`, in sola lettura, indipendente dalle API di
scrittura del "sandwich" più sotto), per una pagina OCR confronta il testo di ogni singola parola
riconosciuta con la query - un limite reale: una query di più parole divisa tra due parole OCR non
verrebbe evidenziata (la pagina risulta comunque trovata, tramite il testo unito). Niente conta le
occorrenze all'interno della pagina. Scelta deliberata, non un taglio per fretta: la
richiesta esplicita era "la priorità è la ricerca di parole chiave testuali", e la sessione di verifica
precedente aveva trovato un bug reale, mai isolato, nel posizionare per-parola un livello di testo
(si vedano i commit precedenti a questo per la cronaca completa) - niente di quel meccanismo entra qui.

**Dove il testo di ogni pagina viene letto** (`PdfPage.HasExtractableText`/`ExtractText`, in `PdfEngine`,
nessuna dipendenza da un form-fill environment): `FPDFText_LoadPage` + `FPDFText_CountChars` per sapere se
la pagina ha già del testo proprio (un PDF nato digitale, o uno scansionato la cui pagina scannerizzata
porti già un livello di testo), `FPDFText_GetText` per estrarlo. Una pagina scansionata pura risponde 0
caratteri.

**Indicizzazione** (`MainViewModel`, regione "Text search / OCR"): dopo l'apertura di un documento, un primo
passaggio veloce legge il testo nativo di ogni pagina (una alla volta, tramite `PdfiumRuntime`, come tutto
il resto). Le pagine senza testo proprio finiscono in coda per l'OCR. Il passaggio OCR **parte solo a
render principale completato** (stessa chiamata di `BeginThumbnails`), una pagina alla volta, in ordine,
per non competere con il rendering per l'unico thread PDFium.

**OCR**: `Services/OcrService.cs`, sopra `Windows.Media.Ocr` - l'API di sistema di Windows, non una
libreria open source (le alternative open source - RapidOCR/Tesseract - sono state confrontate prima di
scegliere questa; vedi la discussione in sessione). Non richiede alcun pacchetto oltre al SDK di Windows
già usato dal progetto (`net10.0-windows10.0.19041.0`): confermato con una prova che referenzia solo
`PdfEngine`, senza Windows App SDK. Limite reale, non teorico: dipende da un componente per lingua
installato in Windows (Impostazioni > Ora e lingua > Lingua e area geografica) - una lingua non installata
restituisce un motore nullo, non un motore lento. Se nessuna lingua OCR è disponibile, `EnsureOcrLoop`
lo segnala una sola volta (`InfoMessage`) e le pagine in coda restano semplicemente non ricercabili; le
pagine con testo nativo continuano a funzionare. `RecognizeAsync` restituisce sia `OcrResult.Text` (il
testo di pagina già unito da Windows, usato per trovare la pagina) sia l'elenco delle singole parole
riconosciute con il loro rettangolo in pixel (usato solo per l'evidenziazione a schermo, mai scritto nel
PDF - la stessa distinzione già fatta sopra per il "sandwich" abbandonato).

**Aggiornamento del risultato**: ogni pagina OCR completata richiama `RefreshSearchMatches`, quindi i
risultati di una ricerca già aperta crescono via via che l'OCR avanza; il testo di stato lo dice
("riconoscimento testo in corso: N pagine rimanenti"). L'appiattimento di un modulo (unisci) può far
comparire testo vero su una pagina che prima non ne aveva (un campo testo compilato viene disegnato nel
contenuto della pagina) - `SaveFilledFormAsync` rilancia l'indicizzazione dopo aver appiattito, per lo
stesso motivo per cui già rilancia le miniature.

**Verificato dal vivo** (UI Automation, incluso `AutomationElement.FocusedElement` per il focus tastiera -
non solo uno screenshot): parola presente solo nel testo nativo di una pagina trovata subito, con
l'esatto rettangolo evidenziato sulla parola; parola presente solo nell'immagine scansionata di un'altra
pagina trovata dopo l'OCR, con navigazione "Successivo" che salta esattamente alla pagina giusta e la
evidenzia; nessun risultato mostra "Nessun risultato"; chiudere la barra fa sparire l'evidenziazione,
riaprirla senza ridigitare la fa tornare (`OnIsSearchOpenChanged`, ramo apertura); l'apertura porta subito
il focus nella casella senza bisogno di un clic; zoom con un'evidenziazione attiva non l'ha rotta. Non
verificato dal vivo: la scorciatoia Ctrl+F in sé (il tentativo di invio tasti simulato è stato interrotto
dal controllo di sicurezza "la finestra in primo piano non è PDF26" prima di inviare nulla) - il
meccanismo (`KeyboardAccelerator` dichiarativo su `SearchToggle`) è lo stesso schema standard già usato
altrove in WinUI3, non testato live in questa sessione.

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

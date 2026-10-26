# Architettura

## Progetti

```
26PDF.slnx
├── src/PdfEngine    Libreria .NET pura: binding PDFium + logica documento/pagina/organizzazione/annotazioni/moduli.
├── src/Printing     Copia indipendente del PDF per la stampa, geometria e intervalli di pagine. Nessuna UI.
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

**Posizione di scorrimento attorno allo zoom.** Il `ScrollViewer` conserva l'offset verticale *in pixel*: se le pagine
si ingrandiscono, a pagina 30 di 100 lo stesso offset cade a pagina ~24, e `OnPagesViewChanged` leggeva quella
posizione sbagliata come "pagina corrente" (lo zoom successivo ripartiva da li', accumulando lo scarto).
Ora `MainViewModel` solleva `PagesResizing` (prima del `Resize`, dimensioni vecchie) e `PagesResized` (dopo);
`MainWindow` nel primo annota la pagina in cima alla vista e la *frazione* di pagina gia' scorsa, nel secondo
ricalcola l'offset dalle nuove altezze (24 px di margine, 16 fra le pagine) e lo applica con `UpdateLayout` +
`ChangeView` senza animazione. Durante lo spostamento la sincronizzazione scorrimento->pagina corrente e' sospesa
(`ZoomSettleMilliseconds`). Verificato dal vivo su un PDF di 60 pagine: da pagina 30, 3 zoom avanti (125%, 156%, 195%) restano sulla 30 (prima del fix a 125% si vedeva la 24). Non provati: zoom indietro e "Adatta a larghezza/pagina" con la versione corretta.

## Ombra sotto le pagine

Ogni pagina della vista principale ha una leggera ombra (`ThemeShadow` condiviso, risorsa `PageShadow` dell'`ItemsControl`,
con `Translation="0,0,16"` sul bordo della pagina per la profondita'): staccare la pagina bianca dallo sfondo. La
luce di `ThemeShadow` viene dall'alto, quindi l'ombra cade sotto e ai lati. **Non** aggiungere un receiver da codice
(`ThemeShadow.Receivers.Add(PagesScrollViewer)`): provato, l'app va in crash all'avvio (access violation, 0xC0000005,
nella proiezione WinRT); senza receiver l'ombra si vede comunque sullo sfondo dell'area pagine.

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

## Stampa con anteprima integrata

La [guida alla stampa](STAMPA.md) descrive il comportamento visibile all'utente. Le responsabilità del
codice sono suddivise così:

| Componente | Responsabilità |
| --- | --- |
| `App/ViewModels/MainViewModel.Printing.cs` | Attende gli aggiornamenti dei moduli e acquisisce lo stato da stampare. |
| `Printing/PdfPrintSnapshot.cs` | Possiede la copia PDF e il relativo ambiente moduli, indipendenti dal documento aperto. |
| `Printing/PrintLayout.cs` | Calcola proporzioni, area stampabile, limiti raster e selezione delle pagine. |
| `App/Services/NativePrintService.cs` | Gestisce pannello nativo, anteprima, invio al driver e durata della sessione. |
| `tests/PdfEngine.Tests/PrintingTests.cs` | Verifica geometria, intervalli e contenuto della copia di stampa. |

`App/Services/NativePrintService` apre il pannello nativo tramite `PrintManagerInterop.GetForWindow` e
`ShowPrintUIForWindowAsync`, anche nell'app non pacchettizzata. Il pannello riunisce anteprima, stampante,
pagine, copie, orientamento, carta, colore e fronte/retro (le opzioni disponibili dipendono dal driver).
Non ci sono finestre di anteprima aggiuntive o un secondo dialogo di stampa.

La sorgente è un `Microsoft.Graphics.Canvas.Printing.CanvasPrintDocument` (Win2D 1.4.0), invece di una
collezione di pagine XAML: l'evento `Preview` disegna soltanto la pagina richiesta; `Print` disegna in
sequenza le pagine selezionate. Il motore PDFium resta il renderer. Anteprima e stampa condividono la
stessa geometria (`Printing.PrintPlacement`): proporzioni conservate e centratura entro `ImageableRect`,
anche con margini asimmetrici e carta diversa dal formato originale. Le pagine miste conservano la loro
forma e vengono adattate al foglio scelto; non si cambia automaticamente orientamento per ogni pagina.

La stampa usa raster a 300 dpi, l'anteprima a 144 dpi; sono comunque limitati sia il numero di pixel sia
la dimensione massima della texture (20 milioni / 8192 px per la stampa, 3 milioni / 1800 px per
l'anteprima). Per carta eccezionalmente grande il limite può ridurre la risoluzione. I buffer non vengono
tenuti pinnati per l'intero lavoro, come avveniva nel percorso GDI precedente. La stampa resta raster,
non un'esportazione vettoriale del contenuto originale.

Prima dell'apertura del pannello, il view model attende gli aggiornamenti dei moduli e crea un
`PdfPrintSnapshot`: documento indipendente in memoria con valori, testo modificato e annotazioni
correnti. Anche i campi appena progettati vengono inclusi nella copia. Il documento aperto non viene
appiattito, salvato su disco o sostituito. La copia possiede il proprio `PdfFormFiller` per disegnare anche
le appearance dei moduli che PDFium non rigenera nel normale salvataggio.

La sessione resta referenziata fino a `PrintTask.Completed`. Il rendering usa deferral e annullamento,
e libera la copia soltanto quando tutte le operazioni pendenti sono terminate. Gli eventi tardivi dopo
la chiusura del pannello possono restituire `RO_E_CLOSED`/`RPC_E_DISCONNECTED`: sono gestiti anche quando
si completa un deferral o si scollegano gli eventi della sorgente che Windows ha già chiuso.
L'invio finale al driver avviene in background, compresa la creazione delle drawing session, così un
dialogo del driver ("Salva output di stampa" di Microsoft Print to PDF) non blocca il thread della finestra.
Le risorse della sorgente Win2D vengono mantenute fino alla successiva stampa
o alla chiusura della finestra, separatamente dalla copia PDF. Gli errori effettivi sono mostrati nella
barra di stato; il completamento indica l'invio allo spooler, non la consegna fisica dei fogli.

I test in `PrintingTests` coprono margini, proporzioni, limiti raster, intervalli sovrapposti, documenti
vuoti, indipendenza della copia, valori compilati e campi ancora da salvare. Le verifiche del pannello richiedono inoltre una
prova interattiva su Windows; stampanti fisiche e driver diversi possono avere comportamenti diversi.

Verificato su Windows il pannello con anteprima dei valori compilati, il cambio di stampante e di
orientamento, annullamento e riapertura. La stampa tramite Microsoft Print to PDF dell'intervallo `2,4`
di un documento sintetico di quattro pagine produce due pagine con i contenuti corrispondenti,
nell'ordine corretto; dopo l'invio l'app resta aperta e Stampa torna disponibile. Nessun foglio inviato
a una stampante fisica durante questo collaudo.

Il 24 ottobre 2026 la suite completa ha superato 120 test e la build Release win-x64 è stata compilata
senza errori o avvisi. Gli errori gestiti della stampa vengono registrati, se possibile, in
`%LocalAppData%\PDF26\logs\printing.log`; il fallimento della scrittura del log non interrompe la gestione
dell'errore originale. La dipendenza Windows Forms del vecchio progetto Printing è stata rimossa;
Win2D è referenziato dal progetto App.

Riferimenti: [stampa nelle app Windows](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/print-from-your-app),
[CanvasPrintDocument](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Printing_CanvasPrintDocument.htm).

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

La ricerca (barra nella barra laterale sinistra - si veda "Barra laterale sinistra" più sotto per dove
e perché - `Icon="Find"`, Ctrl+F - all'apertura il focus tastiera va nella casella, con un rinvio di un
ciclo del dispatcher perché la barra deve prima passare da `Collapsed` a `Visible` ed essere disposta
prima che `Focus()` funzioni) **naviga per pagina, non per occorrenza**: trova quali pagine contengono
la parola cercata (confronto senza distinzione di maiuscole/minuscole, `string.Contains(...,
OrdinalIgnoreCase)`) e ci salta una pagina alla volta (Precedente/Successivo). Ogni occorrenza sulla
pagina attiva viene evidenziata (rettangolo semitrasparente, `PdfPageViewModel.Highlights`) - un overlay
esclusivamente a schermo, mai scritto nel PDF: per il testo nativo usa l'API di ricerca di PDFium
(`FPDFText_FindStart/FindNext` + `FPDFText_CountRects/GetRect`, in sola lettura, indipendente dalle API di
scrittura del "sandwich" più sotto), per una pagina OCR confronta il testo di ogni singola parola
riconosciuta con la query - un limite reale: una query di più parole divisa tra due parole OCR non
verrebbe evidenziata (la pagina risulta comunque trovata, tramite il testo unito). La barra di stato
mostra anche il numero totale di ricorrenze (`MainViewModel.CountOccurrences`, un conteggio di substring
case-insensitive sul testo già indicizzato di ogni pagina trovata - `_pageText`, nativo o OCR che sia) -
un conteggio aggiuntivo mostrato all'utente, non usato per la navigazione: Precedente/Successivo restano
a livello di pagina, come sopra. Scelta deliberata, non un taglio per fretta: la richiesta esplicita era
"la priorità è la ricerca di parole chiave testuali", e la sessione di verifica precedente aveva trovato
un bug reale, mai isolato, nel posizionare per-parola un livello di testo (si vedano i commit precedenti
a questo per la cronaca completa) - niente di quel meccanismo entra qui.

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

**Indicazione nella barra di stato** (`MainViewModel.OcrStatusText`): quando il passaggio nativo trova pagine
senza testo e l'OCR parte, la barra mostra "OCR in corso: 3/12 pagine" e, a lavoro finito, "OCR eseguito su 12
pagine" (conta le pagine realmente riconosciute: una pagina che fallisce non viene dichiarata). Un documento
con testo proprio non mostra nulla; si azzera all'apertura di un altro documento. Se l'OCR non è disponibile
(nessuna lingua installata) resta il solo messaggio di `InfoMessage`. Provato dal vivo con un PDF di 3 pagine
scansionate (immagine JPEG con testo): "OCR in corso: 0/3" poi "OCR eseguito su 3 pagine"; un PDF di testo non mostra nulla.

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

## Barra laterale sinistra: Cerca, Compila modulo e Miniature condividono lo stesso pannello

> **Aggiornamento (successivo): un solo pannello alla volta.** Quanto segue sulle sovrapposizioni descrive la prima
> versione. Ora **tutti** i pannelli della barra - Miniature, Indice, Strumenti, Compila modulo, Cerca e Recenti - si
> escludono a vicenda, su richiesta esplicita: aprirne uno chiude gli altri (e quindi spegne i loro pulsanti, legati
> agli stessi flag). Vedi "Un solo pannello alla volta" più sotto.

Le tre funzioni vivevano prima in tre posti diversi (Cerca: fascia sopra l'area pagine; Compila modulo:
pannello a destra; Miniature: pannello a sinistra) - ora condividono lo stesso `SplitView.Pane` a
sinistra (`MainWindow.xaml`, `ThumbnailSplitView`), sovrapposte quando più di una è attiva insieme
invece di spartirsi lo spazio su lati diversi o spingersi a vicenda, su richiesta esplicita dell'utente.

**Apertura** (`MainViewModel.IsLeftPaneOpen`, bindato a `SplitView.IsPaneOpen`): `IsThumbnailPaneOpen ||
IsSearchOpen || IsFormPanelOpen` - il pannello si apre per uno qualsiasi dei tre, mai da solo. Resta
nascosto per impostazione predefinita, come già accadeva per le miniature: un documento appena aperto non
mostra nulla a sinistra finché l'utente non attiva uno dei tre (scelta esplicita - vedi la discussione in
sessione: i pulsanti Precedente/Successivo per cambiare **pagina** restano nella barra in alto, sempre
visibili; quelli spostati a sinistra sono solo Precedente/Successivo **risultato di ricerca**).

**Z-order** (`SplitView.Pane`, un `Grid` senza righe/colonne - stesso pattern già usato altrove in questo
file per sovrapporre overlay, es. i rettangoli di evidenziazione sopra il bitmap di pagina): dal basso
verso l'alto, `CoverFlowStrip` (visibile solo se `IsThumbnailPaneOpen`) → barra di ricerca (visibile solo
se `IsSearchOpen`, ora in verticale - casella, stato, Precedente/Successivo/Chiudi uno sotto l'altro,
non più su una riga sola: la barra laterale è molto più stretta di quanto non fosse l'area pagine sopra
cui si sovrapponeva prima) → pannello "Compila modulo" (visibile solo se `IsFormPanelOpen`, copre l'intero
pannello perché richiede più spazio delle miniature o di Cerca sotto). Quando più di uno è attivo insieme,
quello dichiarato dopo nel XAML copre quello prima - non ci sono regole di esclusione reciproca: aprire
Cerca non chiude Compila modulo, e viceversa.

**Larghezza** (`MainViewModel.LeftPaneWidth`, bindato a `SplitView.OpenPaneLength`): 340px quando Compila
modulo è la ragione per cui il pannello è aperto (l'elenco dei campi ha bisogno di più spazio), 300px per
l'Indice, 230px altrimenti (miniature/Cerca) - calcolata, non un singolo valore fisso scomodo per uno dei tre usi.

## Indice (bookmark): nella barra laterale, in alternativa alle miniature

**Engine** (`PdfDocument.GetBookmarks()` → albero di `PdfBookmark`): `FPDFBookmark_GetFirstChild/GetNextSibling/
GetTitle/GetCount/GetDest/GetAction` + `FPDFAction_GetType/GetDest` + `FPDFDest_GetDestPageIndex`, firme
verificate contro `fpdf_doc.h` del pacchetto NuGet (`build/native/include/pdfium/`). Lettura completa e
immediata dell'intero albero (non su espansione): anche l'indice di un libro è qualche migliaio di stringhe,
e uno snapshot resta valido senza tenere handle PDFium. Dettagli non ovvi:
- **Destinazione**: una voce porta a una pagina o con `/Dest` diretto o con un'azione `GoTo`; PDFium documenta
  che l'azione va chiesta solo se manca la destinazione. Altre azioni (URI, Launch, GoToR) non puntano dentro
  il documento: `PageIndex = -1`, la voce è mostrata ma non navigabile (più sbiadita).
- **Titolo**: UTF-16LE con terminatore incluso nella lunghezza riportata (due chiamate: lunghezza, poi dati).
- **Aperto/chiuso**: `FPDFBookmark_GetCount` positivo = voce aperta di default, negativo = chiusa.
- **Protezione da file malformati**: profondità massima 64, al massimo 100.000 voci, set degli handle già
  visitati (un outline può rimandare a se stesso) - il risultato è un albero troncato, mai un blocco.

**UI**: un `TreeView` nel `SplitView.Pane` sinistro (stesso pannello di miniature/Cerca/Compila modulo,
vedi sopra). Miniature e Indice **si escludono a vicenda** (`MainViewModel.IsThumbnailPanelOpen`/
`IsBookmarkPanelOpen`: attivarne uno disattiva l'altro), su richiesta esplicita dell'utente - diversamente da
Cerca/Compila modulo, che si sovrappongono. Il pulsante "Indice" compare solo se il documento ha un indice;
la scelta dell'utente è ricordata tra le sessioni (`AppSettings.BookmarkPanelOpen`) come per le miniature.
Larghezza 300px quando l'indice è il contenuto (i titoli sono più lunghi di una miniatura). Il clic su una
voce porta alla pagina (`GoToPage`, lo stesso percorso delle miniature); la freccia espande/comprime senza
navigare. (Il margine superiore che faceva scendere l'albero sotto la barra di Cerca - `OnSearchBarSizeChanged` - non serve più
e è stato tolto: con un solo pannello alla volta nulla si sovrappone.)

**Aspetto dell'indice**: la gerarchia si legge dal carattere, non solo dall'indentazione (`BookmarkNodeViewModel`:
capitoli di primo livello 14 pt semibold con un po' d'aria sopra e sotto, voci annidate 13 pt normali, dal
terzo livello leggermente più chiare, voci non navigabili più sbiadite). **La voce corrente è evidenziata**
(velo del colore d'accento con barretta a sinistra, grassetto): è l'ultima voce, in ordine di documento, che
inizia alla pagina corrente o prima (`MainViewModel.CurrentBookmark`, ricalcolata a ogni cambio di pagina,
anche scorrendo a mano). Se si trova dentro un genitore chiuso, i genitori vengono espansi (`IsExpanded`
bindato in due direzioni alla voce dell'albero) e la voce viene portata in vista (`RevealCurrentBookmark`).
Una scelta che segue lo scorrimento riapre un genitore che l'utente aveva chiuso a mano, ma solo quando la
voce corrente cambia, non a ogni pagina.

**Maniglia sul bordo della barra** (`SidebarHandle`, ex `ThumbnailHandle`): apre/chiude qualunque dei due
contenuti sia attivo - la freccia segue lo stato reale (`‹` con miniature **o** indice aperti, `›` da chiusa;
prima guardava solo le miniature e restava `›` con l'indice aperto) - ed è visibile se c'è almeno uno dei due.
Da chiusa riapre l'ultimo dei due che ha chiuso (`_handleReopensBookmarks`), altrimenti le miniature.

**`ScrollToPage` ora allinea la pagina in alto** (`VerticalAlignmentRatio = 0`). Il comportamento di
default di `StartBringIntoView` scorre solo il minimo per rendere visibile la pagina, cioè **niente** se è già
interamente sullo schermo: verificato dal vivo, cliccare "Sezione 1.1" non faceva nulla quando la pagina
era già visibile. Vale anche per Precedente/Successiva e le miniature, che condividono lo stesso percorso.

**Verifica**: test `PdfBookmarkTests` su `Fixtures/bookmarks.pdf` (generato a mano da
`Fixtures/make_bookmarks_pdf.py`: `/Dest` diretto, azione GoTo, azione URI, voci aperte/chiuse, titolo
non-ASCII); in app con UI Automation (albero, esclusione reciproca col pulsante Miniature, posizione di
scorrimento dopo l'invocazione di ogni voce, margine con Cerca aperto) e uno screenshot. Il clic reale del
mouse sulle voci non è stato provato (UI Automation `Invoke` è un percorso diverso dal clic), né un indice
molto grande.

## Annotazioni: note e timbri (M3)

Prima di M3 l'app non aveva alcuna interfaccia per le annotazioni (esisteva solo `AddHighlight` nel motore,
mai collegato). Ora: **note** (annotazione `/Text`) e **timbri** (`/Stamp`: testuali con data opzionale, o
un'immagine scelta dall'utente), come annotazioni PDF standard - le vedono anche gli altri lettori e restano
modificabili qui finché il documento non viene appiattito ("Unisci" nel pannello modulo cuoce nella pagina
anche note e timbri).

### Motore (`PdfEditableAnnotations`, `PdfAnnotationInfo`, `PdfRect`)

`List` (solo note e timbri - non link, widget, evidenziazioni, popup; salta le annotazioni nascoste),
`AddNote`, `AddTextStamp`, `AddImageStamp` (BGRA con alfa non premoltiplicato), `SetRect`, `SetContents`,
`Remove`. Tutte le coordinate in punti PDF, origine in basso a sinistra; gli indici sono quelli della lista
di annotazioni della pagina (cambiano dopo un'eliminazione: si rilegge la lista). I binding nuovi
(`PdfiumNative`) sono verificati contro `fpdf_annot.h`/`fpdf_edit.h` del pacchetto NuGet.

**Cosa ha mostrato la prova su PDFium 153** (parte dei dettagli non è documentata):
- **Nota**: bastano un rettangolo e `/Contents`; PDFium disegna da solo l'icona gialla. Sopravvive a
  salvataggio e riapertura.
- **Timbro**: PDFium non gli dà nessun aspetto; l'aspetto sono gli oggetti di pagina (testo, rettangolo,
  immagine) aggiunti con `FPDFAnnot_AppendObject` (supportata solo per ink e stamp). **Il rettangolo
  dell'annotazione va impostato PRIMA di aggiungere gli oggetti**: il `/BBox` dello stream di aspetto è
  preso dal rettangolo in quel momento; impostato dopo, il BBox resta 0x0 e non si vede nulla (0 pixel
  cambiati - il primo tentativo).
- **Anomalia sul ridimensionamento** (misurata con tentativi mirati): se il nuovo rettangolo *contiene* il
  BBox dell'aspetto, PDFium riscrive il BBox con il nuovo rettangolo invece di scalare: un timbro ingrandito
  resta della dimensione di prima dentro una cornice vuota più grande. Spostamenti, riduzioni e rettangoli
  disgiunti non ne sono colpiti. Letto dal file salvato: nei casi sovrapposti `/BBox` = nuovo `/Rect`.
- **Soluzione**: l'aspetto di un timbro si costruisce **fuori pagina** (`CanonicalOrigin` = -20000, -20000),
  dove nessun rettangolo sulla pagina può contenerlo, e solo dopo si sposta il rettangolo sulla pagina:
  da lì ogni spostamento/ridimensionamento è puro scaling dal BBox al rettangolo, a qualunque dimensione.
  Verificato ingrandendo 2x, 1,2x, rimpicciolendo 0,5x, spostando e ingrandendo insieme, anche dopo
  salvataggio e riapertura.
- **Timbri di altri software** hanno il BBox sulla pagina e non si ingrandiscono in modo affidabile: i nostri
  portano una voce privata `/PDF26` e solo quelli sono ridimensionabili (`PdfAnnotationInfo.CanResize`;
  gli altri si possono comunque spostare). Spostare un timbro non lo tocca mai (stessa dimensione = non
  contiene il vecchio rettangolo).
- Le note hanno dimensione fissa (24 pt): niente maniglia.
- La data della modifica (`/M`) viene aggiornata a ogni intervento; non si scrive nessun autore (`/T`):
  metterci il nome utente di Windows nel file sarebbe una scelta di privacy da prendere esplicitamente.

### Interfaccia

- **Pannello "Strumenti"** (pulsante "Strumenti" sulla barra, nella barra laterale sinistra): "Aggiungi una
  nota" (interruttore), sei timbri testuali colorati, "Aggiungi la data", "Immagine...". Prima erano pulsanti
  "Nota" e "Timbro" sulla barra: su richiesta sono stati spostati nel pannello, e in barra restano solo
  "Strumenti" e "Salva con nome..." (visibili solo con un documento aperto). Il pannello è un'altra sovrapposizione
  nello stesso `SplitView.Pane` (opaco, esclusivo con gli altri pannelli della barra), largo 270 px (`LeftPaneWidth`), e **non** è ricordato fra le sessioni (`IsToolsPanelOpen`: ha senso solo
  con un documento aperto). Il pannello resta aperto dopo un inserimento; lo strumento è comunque monouso.
  I pulsanti sono accessibili per nome (`AutomationProperties.Name`): senza, un `ToggleButton` con contenuto
  composto non ha nome per UI Automation.
- **Inserimento** (`MainViewModel.Annotations.cs`): scegliere lo strumento e fare clic sulla pagina; lo
  strumento è *monouso* e finisce col clic (Esc lo annulla; un suggerimento compare nella barra di stato).
  Un'immagine viene decodificata con `BitmapDecoder` (alfa non premoltiplicato, lato lungo al massimo 2000 px:
  un timbro è largo pochi centimetri), poi scalata a 160 pt sul lato lungo.
- **Livello interattivo per pagina** (`PdfAnnotationLayer`, un `Canvas` nel template della pagina, sopra i
  campi del modulo e sotto le evidenziazioni di ricerca): un'area di clic trasparente sopra ogni annotazione.
  Senza strumento attivo il livello non ha sfondo, quindi i clic fuori dalle annotazioni passano a selezione
  del testo e campi sotto; con uno strumento attivo l'intera pagina cattura il clic. Selezionata = cornice
  nel colore d'accento (+ maniglia d'angolo se ridimensionabile); trascinamento per spostare (soglia 3 px),
  maniglia per ridimensionare mantenendo le proporzioni, Canc per eliminare, Invio/F2/doppio clic per il
  testo di una nota, menu contestuale (Modifica testo / Elimina), clic altrove o Esc per deselezionare.
  Il testo di una nota si modifica in un flyout; chiuderlo salva; una nota lasciata vuota viene eliminata.
- **Modifiche al documento** (`EditPageAsync`): accodate dietro il rendering/le altre modifiche (PDFium lo
  tocca un lavoro alla volta, come per i campi modulo), eseguono la modifica, rileggono le annotazioni della
  pagina e la ridisegnano; la miniatura si aggiorna. Il livello viene ricostruito a ogni modifica (poche
  annotazioni per pagina) e restituisce il fuoco tastiera alla stessa annotazione, così Canc continua a
  funzionare dopo uno spostamento.
- **Modifiche non salvate** (`HasUnsavedChanges`, anche per i campi compilati): "● Modifiche non salvate"
  nella barra di stato; aprire un altro file o chiudere la finestra chiede di salvare, non salvare o
  annullare (`AppWindow.Closing` annullato e rilanciato dopo la scelta). Il file originale non viene mai
  sovrascritto: si salva sempre in un nuovo file.

### Verifica

18 test sul motore (`PdfEditableAnnotationsTests`: disegno, elenco, salvataggio e riapertura, scaling a 2x/1,2x/0,5x,
ridimensionamento dopo la riapertura, trasparenza dell'immagine, limiti di pagina, ripristino esatto dopo
l'eliminazione). Dal vivo con UI Automation, clic e trascinamenti reali del mouse e screenshot: inserire una
nota (e il suo testo), spostarla, inserire un timbro dal menu, ridimensionarlo dalla maniglia, eliminarlo con Canc,
la finestra "Modifiche non salvate" (Annulla/Non salvare), la riapertura di un file con una nota esistente, un
documento con campi modulo (ambiente form di PDFium attivo) più uno zoom dopo l'inserimento. **Non provato dal
vivo**: il salvataggio fino in fondo dal dialogo del sistema (il dialogo si apre col nome suggerito giusto, ma
non sono riuscito a pilotarlo - e un tentativo maldestro mi ha fatto invocare per errore una voce
dell'elenco, bloccata dalla richiesta di sovrascrittura del sistema e annullata chiudendo l'app: nessun file
toccato; il percorso di scrittura è però lo stesso `SaveFilledFormAsync` già usato e il salvataggio con
annotazioni è coperto dai test del motore), l'inserimento di un timbro-immagine dal selettore file (il
motore è testato con pixel sintetici), tooltip del testo della nota, schermi ad alta densità.

### Editing di testo e immagini già presenti nella pagina: prova di fattibilità

Richiesta esplicitamente prima di decidere l'ordine del lavoro. Con le API di PDFium
(`FPDFPage_GetObject`, `FPDFTextObj_GetText`, `FPDFText_SetText`, `FPDFPage_GenerateContent`):
- **Su un font standard non incorporato funziona del tutto**: "Ciao mondo" è diventato "Ciao Pascal, città è
  più" (accenti compresi), il testo riletto dopo salvataggio e riapertura coincide.
- **Su un documento reale con font incorporato a sottoinsieme no**: sul file da 104 pagine (doPDF) la riga
  "Comune di Paradiso" sostituita con "QZXJK modificato wyk" ha mostrato **riquadri vuoti** per ogni carattere
  assente dal sottoinsieme (Q, X, J, K, w, y...) e testo corretto solo per le lettere già presenti. Un
  sottoinsieme contiene solo i glifi usati dal documento originale: il limite è nel file, non in PDFium.
- **Nessun riflusso**: ogni oggetto testo è una riga; cambiare la lunghezza non sposta ciò che segue, e non c'è
  nozione di paragrafo.
- Sostituire un oggetto testo con uno nuovo in un font di ripiego (Helvetica o un font di Windows incorporato
  per intero) è fattibile ma cambia l'aspetto: il risultato non assomiglia a ciò che fa Acrobat.
Conclusione: una modifica "come Acrobat" richiederebbe una libreria di editing/layout vera; con PDFium da solo si
può offrire al massimo "sostituisci questa riga con un testo in un font a scelta" (e "elimina oggetto").
Decisione rimandata all'utente.

## Modifica del testo esistente

Richiesta dopo la prova di fattibilità qui sopra: modificare le righe di testo già nella pagina, con **font open source incorporati**
per i glifi che il PDF non ha. Solo righe (un oggetto testo PDF = una riga, nessun riflusso) e solo testo; le immagini restano
fuori (vedi ROADMAP).

### Motore (`PdfEngine.Editing`)

- `PdfTextEditing.List(page)` elenca le righe (testo, rettangolo, font, dimensione effettiva, incorporato sì/no), `Replace` e
  `Remove` le cambiano. Operano sugli oggetti pagina top-level (`FPDFPage_GetObject`) e finiscono con `FPDFPage_GenerateContent`:
  il file salvato contiene il contenuto rigenerato, e l'eliminazione toglie davvero il testo dal file (test: dopo salvataggio e
  riapertura la riga non è più estraibile - non è nascosta, perché ricerca e copia la troverebbero).
- **Quali lettere mancano nel font?** PDFium non ha una chiamata affidabile: `FPDFFont_GetGlyphWidth` restituisce una larghezza
  anche per glifi assenti da un sottoinsieme (la `/Widths` li elenca tutti), `GetGlyphPath` restituisce per un glifo assente il
  contorno di `.notdef` (10 segmenti, uguale a quello di una "i" in Liberation: ambiguo) e per i font CID i codici sono indici di
  glifo. La domanda che conta è "viene disegnato?", quindi `PdfEditingState.FontHasGlyph` la pone a PDFium stesso: crea un oggetto
  testo con quel font e quel carattere, ne fa il rendering (`FPDFTextObj_GetRenderedBitmap`) e lo confronta con il rendering di un
  carattere che nessun font ha (U+FFFF, che dà il riquadro `.notdef` del font). Uguale = assente. Provato sul fixture (Q, w, é, Ж,
  ω assenti, C, i, a, o presenti; Helvetica non incorporato: tutto il WinAnsi presente, cirillico e greco assenti). I caratteri già
  presenti nella riga originale non si controllano (si vedono già). Le risposte per i font di ripiego si ricordano per documento.
- Nessuna lettera mancante: `FPDFText_SetText` **sul posto** (stesso font, stessa posizione). Altrimenti la riga è **riscritta**:
  nuovo oggetto testo (`FPDFPageObj_CreateTextObj`) con il font di ripiego, stessa dimensione, stessa matrice (posizione), stesso
  colore di riempimento e modo di disegno, inserito alla stessa posizione nell'ordine di disegno. L'intera riga cambia font
  (scelta dell'utente: non si mescolano font dentro una riga); se nessun font ha un carattere, la riga viene scritta lo stesso e
  `PdfTextEditResult.MissingCharacters` lo riporta (l'interfaccia lo segnala).
- **Scelta del font** (`FallbackFonts.Classify`, in ordine di fiducia): (1) le tabelle del file del font incorporato
  (`SfntInfo`: `OS/2` panose/classe/peso, `head.macStyle`, `post.isFixedPitch`/angolo), (2) il nome, (3) i flag del descrittore.
  Il caso reale che l'ha imposto: nel PDF da 104 pagine il font è "CIDFont+F2", i flag dicono serif+simbolico (6), il peso
  3696 - e il file dice sans grassetto (la prima versione, basata sui flag, scriveva il titolo in un serif). Famiglia: Sans,
  Serif o Mono; stile: regolare/grassetto/corsivo/grassetto corsivo. Catena: `PDF26 <famiglia>` nello stesso stile, poi Noto Sans.
- **Font incorporati**: `src/PdfEngine/Fonts/`, 12 file `PDF26{Sans,Serif,Mono}-{Regular,Bold,Italic,BoldItalic}.ttf` (derivati da
  Liberation 2.1.5) e 4 `NotoSans-*.ttf`, tutti **SIL OFL 1.1**, risorse incorporate in `PdfEngine.dll` (e licenze in `Licenses/`
  accanto all'eseguibile). Ridotti con `fontTools` a: ASCII, Latin-1/Esteso A-B, greco, cirillico, latino esteso additivo (vietnamita),
  punteggiatura, valute, qualche simbolo - circa 85-100 KB l'uno (1,5 MB in tutto). **Liberation è rinominato**: la OFL riserva il
  nome "Liberation" alle versioni non modificate e un sottoinsieme è una versione modificata; nelle tabelle `name` (1, 3, 4, 6, 16,
  17...) "Liberation" è diventato "PDF26" (copyright e licenza restano). Noto Sans non ha nome riservato. Un font caricato con
  `FPDFText_LoadFont` entra **per intero** nel file: per questo i sottoinsiemi (90 KB per font usato, non 400-600).
- Un font di ripiego si carica **una volta per documento** (`PdfEditingState`) e si chiude prima del documento.

### Annulla e ripeti per le modifiche al testo

Una riga modificata sul posto si ripristina con `SetText` del testo vecchio. Una riga **riscritta** o **eliminata** ha tolto un
oggetto dalla pagina: l'oggetto viene **tenuto** (staccato, non distrutto) dal record di annullamento (`PdfEditRecord`) e rimesso
al suo posto da `Undo`; `Redo` rifà il percorso inverso. Gli oggetti staccati sono registrati in `PdfEditingState`, che li libera
quando il record viene scartato o, al più tardi, alla chiusura del documento (prima di `FPDF_CloseDocument`: un oggetto non può
sopravvivere al documento). Provato: annulla/ripeti ripetuti riportano il rendering **identico al pixel** all'originale; chiudere
il documento prima di scartare il record non va in crash.

### Interfaccia

Nel pannello "Strumenti" i timbri predefiniti sono **ristretti**: i primi due (APPROVATO, RIFIUTATO) sempre visibili, gli altri
(`MainViewModel.BasicTextStampPresets`/`MoreTextStampPresets`) dietro il pulsante "Mostra altri timbri" / "Mostra meno timbri"
(chevron). Lo stato si ricorda fra le sessioni (`AppSettings.MoreStampsExpanded`); al primo avvio è ristretto.

`PdfTextEditLayer` (un `Canvas` sopra il testo di ogni pagina, sotto quello delle annotazioni): con "Modifica testo" attivo mostra
una cornice tenue su ogni riga; clic = selezione (cornice accent), doppio clic/Invio/F2 = editor in un flyout (testo, font del
documento, "Applica", "Elimina riga"; un testo vuoto elimina), Canc = elimina, Esc = deseleziona. Esclusivo con gli strumenti di
inserimento (nota, timbro, campi). Le righe si leggono per pagina quando lo strumento si accende (in coda dietro i render, senza
barra di avanzamento) e si rileggono dopo ogni modifica; dopo una modifica del testo si aggiorna anche l'indice di ricerca della
pagina. Dopo una riscrittura la barra di stato dice "Riga riscritta con un font incorporato dall'app..." e, se serve, quali
caratteri non hanno glifo. **Verificato dal vivo** (fixture e PDF reale da 104 pagine): righe con cornice, Invio apre l'editor,
riscrittura con lettere assenti (disegnate correttamente, stesso colore e posizione), eliminazione, annulla/ripeti da tastiera.
Il doppio clic con il mouse simulato dallo script era troppo lento per essere riconosciuto come doppio clic (400 ms fra i
clic): provato con Invio, che apre lo stesso editor.

## Annulla e ripeti

`MainViewModel.Undo.cs`: due pile di `UndoEntry` (descrizione, `Undo`, `Redo`, risorsa da liberare, tag), massimo 100, azzerate
quando un altro documento sostituisce quello aperto. Pulsanti "Annulla"/"Ripeti" in barra (tooltip con la descrizione), **visibili solo con il pannello
"Strumenti" aperto** (`IsToolsPaneOpen`), e Ctrl+Z, Ctrl+Y, Ctrl+Maiusc+Z. Le scorciatoie sono `KeyboardAccelerator` della
griglia radice, **non** dei pulsanti: l'acceleratore di un pulsante nascosto non scatta (provato: con il pannello chiuso Ctrl+Z non
faceva nulla). Valgono in tutta la finestra, **ma dentro una casella di testo vince la casella** - provato con la nota: Ctrl+Z annulla
le lettere digitate, non il documento.
**Aspetto dei pulsanti riapparsi**: un pulsante della barra nascosto (`Collapsed`) e poi mostrato resta con l'aspetto *attivo* anche se
il comando lo disabilita (`IsEnabled` è giusto - lo conferma UI Automation - ma lo stato visivo "Disabled" non viene dipinto; si
vedeva solo senza che nulla toccasse l'interfaccia, perché leggere l'albero di UI Automation lo risolveva da solo). Non bastano né
un `IsEnabled` legato esplicitamente, né invertirlo e rimetterlo; `x:Load` non è disponibile sul radice di una `Window`. Funziona
`MainWindow.RefreshUndoRedoAppearance` all'apertura del pannello: `VisualStateManager.GoToState` ("Normal"/"Disabled") più
un'invalidazione forzata (Opacity 0,99 -> 1, `InvalidateArrange`, `UpdateLayout`). Verificato misurando i pixel del testo (grigio
163, attivo 27) dopo apertura, inserimento, annulla e ripeti. Le azioni si eseguono in coda
(`_undoGate`) e passano dalla stessa coda di render/modifiche di pagina (`EditPageAsync`), che segnala il fallimento: se un
annullamento fallisce la voce viene scartata e la barra di stato lo dice.
- **Note e timbri**: le annotazioni si indirizzano per **identità** (`/PDF26Id`, voce privata scritta alla prima modifica;
  `PdfEditableAnnotations.EnsureId/SetId/IndexOfId`), non per indice, perché eliminare e rimettere cambia la posizione delle
  altre. Inserimento: annulla = elimina, ripeti = ricrea dalla *ricetta* (nota con testo; timbro testuale con testo, colore e data;
  timbro immagine con i pixel). Spostamento/ridimensionamento e testo della nota: il valore prima/dopo. Eliminazione: annulla =
  ricrea dalla ricetta nel rettangolo di prima, con la stessa identità. Eliminare una nota vuota appena creata (editor chiuso senza
  scrivere) annulla la creazione invece di lasciare due voci.
- **Limite**: un timbro che era già nel file (non fatto da questa sessione) non ha ricetta, quindi la sua eliminazione **non si
  può annullare**; invece di lasciare voci che indirizzano le annotazioni per posizione la cronologia si azzera e la barra di
  stato lo dice. (Una nota estranea si ricrea dal testo, perdendo colore e autore.)
- **Campi modulo aggiunti** (in memoria fino al salvataggio): inserimento, spostamento, proprietà, eliminazione: lavoro in memoria,
  senza PDFium. **Non coperti**: i valori scritti nei campi di un modulo compilato (il pannello "Compila modulo", che ha già
  l'annulla delle sue caselle di testo) e il salvataggio.

## File recenti

Pulsante "Recenti" in barra (accanto ad "Apri...") che apre un **pannello della barra laterale** (all'inizio era un menu
a tendina; spostato su richiesta nella barra laterale, esclusivo con gli altri pannelli - vedi "Un solo pannello alla
volta"). Funziona anche senza documento aperto: è il modo di arrivare al primo. L'elenco (`AppSettings.RecentFiles`, al massimo 10, il più recente in cima) è nello stesso
`settings.json` del resto: si aggiorna **solo dopo un'apertura riuscita** (`MainViewModel.RegisterRecent`: una password
sbagliata o un file illeggibile non entrano) e vale anche per i file passati da riga di comando e per gli URL. Un
file già presente si sposta in cima invece di duplicarsi (confronto senza maiuscole/minuscole, come Windows).
Il pannello (`RecentPanel`, `ListView` su `MainViewModel.RecentEntries`, `RecentFileEntry`) mostra per ogni voce il
nome del file in grassetto e sotto la cartella (per un URL, l'host) - il che sostituisce la disambiguazione fra
omonimi del menu - più il percorso completo nel suggerimento, una croce per togliere la singola voce e "Svuota
elenco" in fondo; "Nessun file recente" se è vuoto. Un clic apre la voce (dopo l'avviso "modifiche non salvate") e
**chiude il pannello**: l'utente è andato avanti.

Una voce il cui file non esiste più viene tolta dall'elenco al primo tentativo (`File non trovato` da
`FileSourceResolver`): fallirebbe ogni volta. Nessun controllo di esistenza all'apertura del pannello, di proposito: su
una cartella di rete non raggiungibile ogni controllo può bloccarsi per secondi. Scegliere una voce passa per lo
stesso avviso "modifiche non salvate" di "Apri...". Non si usa il Jump List / "Documenti recenti" di Windows
(non richiesto). Verificato dal vivo con UI Automation (nella versione a menu): ordine, disambiguazione, apertura di
una voce, rimozione di una voce mancante con il messaggio di errore, "Svuota elenco"; nella versione a pannello: la
sua esclusività con gli altri pulsanti e il suo aspetto (screenshot).

## Un solo pannello alla volta

Richiesta esplicita: i sei pannelli della barra laterale (`SidebarPanel`: Thumbnails, Bookmarks, Tools, Form,
Search, Recent) si escludono a vicenda, **Cerca compreso** (prima Cerca si sovrapponeva agli altri). Meccanismo
(`MainViewModel.Sidebar.cs`): ogni pannello ha il suo flag (`IsThumbnailPanelOpen`, `IsBookmarkPanelOpen`,
`IsToolsPanelOpen`, `IsFormPanelOpen`, `IsSearchOpen`, `IsRecentPanelOpen`), legato in due direzioni al proprio
pulsante in barra; quando uno si accende il suo `On...Changed` chiama `CloseOtherPanels`, che spegne gli altri - e
siccome i pulsanti sono legati agli stessi flag, si spengono da soli. Ctrl+F passa dallo stesso flag di Cerca.
- **Preferenza di miniature/indice**: restano ricordate fra le sessioni, ma una chiusura *causata dall'apertura di un
  altro pannello* (`_excludingPanels`) non la cancella: aprire Cerca per un attimo non fa sparire le miniature dal
  prossimo avvio. Scegliere miniature spegne l'indice nelle impostazioni e viceversa.
- **Maniglia sul bordo**: ora chiude qualunque pannello sia aperto (`CloseSidebar`, che è una scelta dell'utente e
  quindi viene ricordata) e la freccia segue `IsLeftPaneOpen`; da chiusa riapre l'ultimo fra miniature e indice.
- **Larghezza** (`LeftPaneWidth`): Compila modulo 340, Indice e Recenti 300, Strumenti 270, il resto 230.
- Nota: i pulsanti dei pannelli che non servono (Compila modulo senza campi, Indice senza indice, Miniature sotto le
  3 pagine) restano nascosti, come prima. Un pulsante in overflow ("...") della barra non risponde a Ctrl+F
  finché la finestra è stretta: già così prima (l'acceleratore di un comando in overflow non scatta).
- Verificato dal vivo con UI Automation, premendo i pulsanti in sequenza e leggendo lo stato di tutti: ogni
  combinazione provata lascia acceso solo l'ultimo (con un documento a quattro pagine con indice: Miniature, Indice,
  Strumenti, Cerca, Recenti; con un modulo: Compila modulo, Strumenti, Recenti, Cerca), anche con Ctrl+F.

## Campi modulo creati dall'utente (iText)

Si aggiungono campi di testo e caselle di controllo dal pannello "Strumenti" > "Campi modulo": si sceglie lo
strumento (monouso, come note e timbri), si fa clic sulla pagina, si apre un editor delle proprietà. Sono
campi AcroForm veri: compilabili da qualunque lettore PDF.

### Perché iText

PDFium legge e compila i campi ma **non ha nessuna chiamata per crearne** (`FPDFPage_CreateAnnot` non supporta i
widget; le API dei dizionari non sono pubbliche). Serve una libreria che sappia scrivere la struttura. Valutate,
con una prova concreta su un PDF vero:
- **PDFsharp 6.5 (MIT)**: i costruttori di tutte le classi dei campi (`PdfTextField`, `PdfCheckBoxField`, ...) sono
  `internal`; senza forzature con la reflection non può creare campi. Scartata.
- **iText Core 9.8 (AGPL-3.0)**: `TextFormFieldBuilder`/`CheckBoxFormFieldBuilder` + `PdfAcroForm.AddField`, in append
  mode. Provato: PDFium rilegge i due campi creati con nome, tipo e rettangolo giusti. Scelta.
Dipendenza: ~5 MB di assembly (`itext` + `itext.commons`). **Licenza**: l'AGPL, se l'app viene distribuita, impone di
offrire i sorgenti dell'app a chi la riceve (o di acquistare la licenza commerciale di iText); accettata dall'utente
dopo che gliel'ho spiegata. Va nel README (licenze di terze parti) e nella finestra "Informazioni".

### Motore (`PdfFormDesigner`, `PdfDocument.SaveToBytes`)

`PdfFormDesigner.Apply(byte[] pdf, changes)` → nuovi byte. Cambiamenti: `AddFieldChange(pagina, PdfFieldSpec)`,
`ModifyFieldChange`/`RemoveFieldChange` (campo esistente trovato per pagina + posizione nella lista di annotazioni,
la stessa che riporta `PdfFormField`). Si parte dai byte che scrive **PDFium** (`SaveToBytes`, che include le
modifiche in memoria: annotazioni, valori compilati) e si riapre il risultato. Verificato su un file di
partenza con tabella xref classica, e su uno scritto con xref stream + object stream (e anche dopo il giro in
PDFium): l'append mode di iText gestisce entrambi, quindi non serve uno scrittore proprio. Dettagli:
- **Appearance stream**: dopo aver impostato le proprietà si chiama `RegenerateField()`, altrimenti il widget non ha
  un aspetto proprio e i lettori che non lo ricavano dalle proprietà non mostrerebbero niente. Bordo nero 1 pt e
  sfondo bianco.
- **Nomi**: non vuoti, senza punti (un punto è una gerarchia: "a.b" è il campo b del gruppo a), non già usati - due
  campi con lo stesso nome sono per un lettore un solo campo mostrato due volte. Controllo nel motore (eccezione) e
  nell'interfaccia (messaggio nell'editor).
- **Gli altri contenuti restano**: pagina, annotazioni esistenti (nota ritrovata dopo `Apply`), rendering invariato
  senza i campi.
- Osservazione: la proprietà "spuntata" di una casella è nel file (`/V /Yes`, `/AS /Yes`) e PDFium la **disegna**
  spuntata, ma il suo `FPDFAnnot_IsChecked` rispondeva falso: il test controlla il file e l'inchiostro disegnato, non
  quella chiamata.
- **iText scrive il proprio Producer** ("iText Core ... AGPL version") nei file in cui si sono aggiunti campi: è nel
  roadmap con i metadati.
- Test: `PdfFormDesignerTests` (aggiunta di testo/casella/multilinea, compilazione e spunta da parte di PDFium,
  contenuto e annotazioni invariati, xref stream, spostamento/rinomina/eliminazione, tooltip/obbligatorio/sola lettura/
  dimensione, nomi non validi o già usati, errori su campi inesistenti).

### Interfaccia (`MainViewModel.FieldDesign.cs`, `DesignField`)

Un campo appena piazzato **non è ancora nel documento**: vive nell'app come `DesignField` e il livello della pagina
(`PdfAnnotationLayer`, lo stesso di note e timbri) lo disegna come segnaposto (riquadro bianco con cornice sottile e,
per un campo di testo, il nome) con gli stessi gesti: selezione, trascinamento, maniglia d'angolo (un campo di
testo si allarga liberamente, una casella resta quadrata), Canc, menu contestuale, Invio/F2/doppio clic per le
proprietà. Editor delle proprietà in un flyout: nome (con messaggio se vuoto, con punti o già usato - anche rispetto
ai campi che il documento aveva già), suggerimento, obbligatorio, sola lettura; testo: su più righe e dimensione del
carattere (0 = automatica); casella: spuntata di default. OK applica; cliccare fuori applica ciò che è valido.
- **Al salvataggio** (`SaveWithPendingFieldsAsync`): `SaveToBytes` (PDFium) → `PdfFormDesigner.Apply` → file; poi
  **il file salvato viene aperto** (come "Salva con nome" di un editor di testo), così i campi sono subito veri:
  compaiono in "Compila modulo" e come caselle sulla pagina. "Unisci prima di salvare" non si applica in quel
  salvataggio (appiattirebbe i campi appena creati).
- **Perché non si scrivono subito nel documento in memoria**: avrebbe voluto dire rileggere il documento a ogni
  piazzamento (e fermare e riavviare miniature, indicizzazione del testo e OCR che tengono il riferimento al
  documento). Scrivere una volta sola al salvataggio è molto più semplice e robusto; il prezzo è che il campo si
  vede come segnaposto finché non si salva.
- **Limite dichiarato**: si modificano solo i campi creati nella sessione. Quelli che il documento aveva già non si
  toccano (roadmap): spostarne uno lascerebbe il vecchio disegno nel bitmap della pagina fino al salvataggio.
- Modifiche non salvate: i campi piazzati contano (`HasPendingFields` → `HasUnsavedChanges`): aprire un altro file o
  chiudere chiede conferma.
- Verifica dal vivo (UI Automation, clic e trascinamenti reali, screenshot): piazzare campo e casella, editor delle
  proprietà, messaggio "Esiste già un campo con questo nome", spostamento, ridimensionamento libero, "Salva con
  nome..." fino in fondo (dialogo di sistema compilato da tastiera: **il selettore file gira in un altro processo,
  `PickerHost`**, quindi è lui ad avere il primo piano, non l'app - un controllo sul PID dell'app lo scambia per
  assenza del dialogo), riapertura del file salvato con i due campi in "Compila modulo" e sulla pagina. Un difetto
  trovato lì e corretto: il pannello "Compila modulo" aveva uno sfondo semitrasparente (`LayerFill`) e lasciava
  vedere il pannello Strumenti sotto; ora i pannelli sovrapposti sono opachi (`SolidBackgroundFillColorBase`).
  Non provato: salvare un documento protetto da password con campi (iText ne chiede la password; il messaggio è
  quello di errore generico), lo stato "spuntata di default" dall'interfaccia, un documento molto grande.

## Selezione del testo (drag-to-select) - solo pagine con testo nativo

Trascinare il mouse su una pagina ne seleziona il testo (evidenziazione blu chiara, in
`PdfPageViewModel.Selection` - stessa forma di `Highlights` ma collezione separata, così una selezione
e un risultato di ricerca non si sovrascrivono) e al rilascio del mouse copia subito il testo negli
appunti, deseleziona (`PdfPageViewModel.ReportTextCopied`, chiamata da `PdfTextSelectionOverlay` appena
dopo `Clipboard.SetContent`) e conferma nella barra di stato ("Testo copiato negli appunti.", tramite
`MainViewModel.InfoMessage` - lo stesso meccanismo già usato per altre conferme come il salvataggio di
un modulo compilato). Nessuna UI di conferma propria (niente flyout/tooltip): la copia è automatica, non
un'azione separata dal trascinamento. **Attiva solo sulle pagine con testo nativo** (`PdfPageViewModel.HasNativeText`,
impostato da `MainViewModel` nello stesso punto in cui popola `_pageText` durante il passaggio nativo -
si veda "Ricerca testo e OCR" sopra): una pagina scansionata (OCR-only) non supporta la selezione, per
scelta esplicita del prodotto - la granularità per-parola dell'OCR non giustificava l'complessità di
un'API di selezione separata per quel caso.

**Livello di hit-test** (`PdfTextSelectionOverlay`, `Views/`): un `Panel` trasparente disegnato sopra il
bitmap della pagina, con `IsHitTestVisible` legato a `HasNativeText` (aggiornato anche a runtime, se
l'indicizzazione lo scopre dopo che il controllo esiste già) - una pagina senza testo nativo lascia quindi
passare i click a quel che c'è sotto. `Panel`, non `Border`: `Border` è `sealed` in WinUI3, non derivabile.

**Hit-testing carattere-per-carattere** (`PdfPage.GetCharIndexAt`/`HitTestAndSelect`, nuove binding
`FPDFText_GetCharIndexAtPos`): stesso approccio in punti PDF (bottom-up) già usato per i rettangoli di
ricerca, con la stessa trasformazione pixel→punto (inversa di quella in `RebuildHighlights`) usata da
`PdfPageViewModel.ToPagePoint`. `HitTestAndSelect` fa hit-test e calcola i rettangoli della selezione
nella stessa chiamata nativa, per un solo round-trip a movimento del puntatore.

**Coalescenza dei movimenti** (`PdfPageViewModel.RunExtendAsync`): un trascinamento reale genera più
`PointerMoved` di quanti round-trip nativi riescano a starci dietro - invece di accumulare chiamate,
mentre una è in corso il punto più recente viene solo annotato (`_pendingDragPoint`) e ripreso appena
quella finisce, così un movimento rapido non rischia mai che una risposta più vecchia sovrascriva una
più nuova.

**Un clic senza trascinamento non seleziona nulla** (`EndTextSelectionAsync`: se il carattere finale
coincide con quello iniziale, torna stringa vuota e pulisce l'evidenziazione) - lo stesso comportamento
di un editor di testo normale, dove il clic piazza solo il cursore.

**Verificato dal vivo, con una scoperta di metodo**: il primo giro di test (simulando il trascinamento
con `SetCursorPos` assoluto più `mouse_event` di down/up) sembrava non funzionare mai, su nessun file -
log aggiunti temporaneamente hanno mostrato che il hit-test *stesso* funzionava benissimo (carattere
giusto trovato, coordinate corrette), ma ogni chiamata di `PointerMoved` durante il trascinamento
riportava la stessa posizione del clic iniziale: `SetCursorPos` non genera movimento "vero" agli occhi
della pipeline puntatore di WinUI una volta che il pulsante è catturato (`CapturePointer`). Passando a
`mouse_event(MOUSEEVENTF_MOVE, dx, dy, ...)` con spostamenti relativi e piccoli step, il trascinamento ha
funzionato correttamente al primo tentativo utile: testo selezionato, evidenziazione visibile, testo
copiato negli appunti al rilascio coincidente con l'evidenziazione. Anche il primo
file di prova (`cento_pagine.pdf`, generato in una sessione precedente) si era rivelato **senza testo
nativo proprio** (il suo contenuto testuale in `_pageText` veniva solo dall'OCR) - la selezione che non
si attivava lì era quindi corretta, non un bug: l'individuazione del vero problema (la simulazione del
trascinamento) è arrivata solo dopo aver isolato e scartato questa causa con un file di controllo noto
per avere testo nativo vero (`tests/PdfEngine.Tests/Fixtures/text-page.pdf`, "Ciao mondo").

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

## Versionamento e finestra "Informazioni"

Un solo `<Version>` in `Directory.Build.props` (radice del repository, raccolto automaticamente da
MSBuild per ogni progetto della solution - App, PdfEngine, Printing, PdfEngine.Tests) invece di
duplicarlo in ogni `.csproj`: da lì derivano `AssemblyVersion`/`FileVersion`/`InformationalVersion` per
tutti, a meno che un progetto non li sovrascriva esplicitamente (nessuno lo fa oggi). Si parte da `0.1.0`
(nessun installer/M4 ancora, quindi non ancora una `1.0.0`). Lo stesso file imposta anche `Company`/
`Copyright`, letti da Esplora file nella scheda Dettagli dell'eseguibile.

**Nota**: l'SDK .NET, rilevando la cartella `.git`, appende automaticamente l'hash del commit
all'`InformationalVersion`/`ProductVersion` (es. `0.1.0+e448232...`) - utile in `FileVersionInfo` per
risalire alla build esatta, troppo lungo per una UI rivolta all'utente. `AboutDialog` (`Views/`,
`ContentDialog` come `SettingsDialog` - un'app WinUI3 unpackaged non ha bisogno di una finestra
separata, e una `ContentDialog` vive già dentro `MainWindow`, coerente con la nota su `WindowIcon`)
legge `ProductVersion` via `FileVersionInfo.GetVersionInfo` (non `Assembly.GetName().Version`, che
riporta solo `AssemblyVersion`, sempre a 4 cifre tipo `0.1.0.0`) e mostra solo la parte prima del `+`.

## Indicazione PDF/A nella barra di stato: autodichiarazione, non validazione

Alla riapertura di un documento la barra di stato mostra "PDF/A-2B" (o qualunque livello
dichiarato) accanto al nome del file - **solo se il documento lo dichiara**: per un PDF ordinario non compare nulla (prima
mostrava "Non PDF/A", tolto su richiesta: non esserlo non è un'informazione) (`MainViewModel.PdfAStatusText`,
calcolato in `OpenAsync` da `DetectPdfAConformance`). **Non è un validatore**: legge solo
l'autodichiarazione che il file stesso porta nei propri metadati XMP (`pdfaid:part`/
`pdfaid:conformance`, ISO 19005 Annex D) - esattamente quello che mostrano anche Adobe Acrobat,
Foxit e la maggior parte dei lettori PDF mainstream. Una vera validazione PDF/A (verificare font
incorporati, spazi colore, trasparenza, crittografia e decine di altre regole strutturali contro
il livello dichiarato) è un progetto a parte - è il motivo per cui esistono strumenti dedicati
come veraPDF; non è in scope qui.

**Perché sui byte grezzi del file, non su PDFium** (`PdfEngine.PdfAConformance.Detect`): l'API
pubblica di PDFium non espone in alcun modo il contenuto dello stream `/Metadata` del documento -
verificato contro gli header vendored (`FPDF_GetMetaText` legge solo il dizionario Info classico:
Title/Author/Subject/.../Producer, non l'XMP). La ricerca avviene quindi sui byte del file così
come arriva (`MainViewModel.DetectPdfAConformance` rilegge il file dal percorso locale già
risolto da `OpenAsync`): prima una ricerca a livello di byte del marcatore del namespace XMP
PDF/A (`www.aiim.org/pdfa/ns/id/`, stabile per tutte le parti 1/2/3) - non una conversione in
stringa dell'intero file, che per un PDF grande vorrebbe dire allocarne una seconda copia solo per
cercare un marcatore che quasi sempre sta vicino alla fine (lo stream dei metadati è tipicamente
uno degli ultimi oggetti scritti) - poi, solo sulla piccola finestra di byte attorno al marcatore
trovato, la decodifica in testo (Latin-1: ogni byte mappa esattamente un carattere, non lancia mai
eccezioni indipendentemente da cosa circondi il packet XMP) e l'estrazione di `pdfaid:part`/
`pdfaid:conformance` via regex, sia in forma attributo che elemento figlio.

**Limiti noti, non un bug**: non trova nulla se lo stream `/Metadata` è compresso (legale ma
inusuale - la maggior parte dei produttori PDF/A lo lascia non compresso apposta, per essere
trovabile così da strumenti esterni) o se usa un prefisso di namespace diverso dal convenzionale
`pdfaid`. In entrambi i casi non si mostra nulla anche per un documento che in realtà lo è - un falso negativo
accettabile per un'indicazione informativa, non per un validatore.

**Verificato**: due file PDF minimi creati a mano (uno con la dichiarazione PDF/A-2B nei metadati
XMP, uno senza) apribili correttamente da PDFium tramite `PdfDocument.OpenFromPath` e corretti
entrambi via `PdfAConformance.Detect` (vedi `tests/PdfEngine.Tests/PdfAConformanceTests.cs` per i
casi sulla sola logica di estrazione, in forma attributo e in forma elemento).

**Banner** (`MainViewModel.ApplyPdfAStatus`): oltre alla barra di stato, un documento che dichiara PDF/A
mostra un `InfoBar` informativo (blu) sopra le pagine - "Documento PDF/A-2B", testo volutamente breve: "Conformità
dichiarata dal file, non verificata". Sta nello stesso punto del banner delle firme digitali (due
`InfoBar` impilati in uno `StackPanel`, ciascuno chiudibile e presente solo se ha qualcosa da dire). Per "Non
PDF/A" **non** c'è banner: è solo l'assenza di una dichiarazione, resta una nota discreta nella barra di
stato. Provato dal vivo su un PDF/A-2B minimo creato a mano.

## Firme digitali: verifica dell'integrità, non dell'attendibilità

**Cosa si legge e cosa no.** PDFium (`fpdf_signature.h`, API sperimentale ma presente nella libreria) sa solo
*elencare* le firme: `FPDF_GetSignatureCount/Object`, `FPDFSignatureObj_GetContents/GetByteRange/GetSubFilter/
GetReason/GetTime` (`PdfDocument.GetSignatures()` → `PdfSignature`). Non verifica nulla. La verifica è nostra
(`PdfSignatureVerifier`), sui byte grezzi del file come `PdfAConformance`, con `SignedCms` di .NET (pacchetto
NuGet `System.Security.Cryptography.Pkcs` - non è nel framework condiviso di questo TFM, MIT, aggiunto alle
licenze di terze parti).

**Cosa dice, e soprattutto cosa NON dice.** Controlla che ciò che è stato firmato sia ancora ciò che c'è nel
file: l'impronta del contenuto coperto dal `/ByteRange` e la firma su di essa (`CheckSignature(verifySignatureOnly:
true)`). **Non** valida il certificato: niente catena fino a una radice attendibile, scadenza, revoca. È ciò che
Acrobat chiama "firma valida" *in più*, e richiede un archivio di fiducia e interrogazioni di rete. Per questo
ogni testo dell'interfaccia dice "non modificato dopo la firma", mai "firmatario attendibile", e il banner lo
scrive esplicitamente.

**Esiti** (`PdfSignatureStatus`, per firma e per documento - quello del documento è della firma che copre più file):
`Intact` (verde), `ChangedAfterSigning` (giallo), `Altered` (rosso), `Unverifiable` (giallo).

**Controlli di forma prima della crittografia.** Il `/ByteRange` deve essere ben formato: coppie in ordine,
dentro il file, senza sovrapposizioni, e il vuoto fra i primi due pezzi deve essere esattamente una stringa
`<...>` (la firma stessa) - un intervallo che non descrive "il file meno la firma" non dimostra nulla sul file
(`Unverifiable`). Il valore `/Contents` è riempito di zeri fino a una dimensione fissa: si taglia al primo valore
ASN.1 completo (`AsnDecoder.ReadEncodedValue`) prima di darlo a `SignedCms`. Formati supportati:
`adbe.pkcs7.detached` e `ETSI.CAdES.detached`; gli altri sono `Unverifiable`. Un certificato non incluso nella
firma o un contenitore illeggibile sono `Unverifiable`; un'impronta o una firma che non tornano sono `Altered`
(tecnicamente anche un algoritmo non supportato dalla piattaforma finirebbe qui: il testo dice "modificato o
danneggiato", e non si può distinguere meglio con l'API).

**Il caso che ha richiesto più cura: dati di validazione dopo l'ultima firma.** I servizi di firma qualificata
(PAdES-LT) aggiungono, *dopo* l'ultima firma, un aggiornamento incrementale con il Document Security Store
(`/DSS`: certificati, risposte OCSP, CRL) perché la firma sia verificabile anche a certificati scaduti. Il
file reale usato per sviluppare questa funzione (due firme CAdES) è fatto così: a un confronto ingenuo "la
firma arriva a fine file?" risulta **modificato dopo la firma** - un falso allarme su ogni documento di quel
tipo. `IsValidationDataOnly` analizza il codice aggiunto dopo la parte firmata e risponde sì solo se *ogni*
oggetto aggiunto è: il catalogo, **identico alla sua versione precedente tranne la voce `/DSS`** (se la
versione precedente non si trova in chiaro, ad esempio perché è in uno stream di oggetti, risponde no); un
oggetto raggiungibile dal dizionario `/DSS`; o un cross-reference stream. Qualunque altra cosa (una pagina, un
annotation, un campo, un oggetto non classificabile, un oggetto nascosto nei dati di un altro) dà no, cioè
`ChangedAfterSigning`. Conservativo di proposito: nel dubbio avvisa. Limite noto: una marca temporale di
documento (`/Type /DocTimeStamp`, PAdES-LTA) aggiunta dopo l'ultima firma non è riconosciuta e dà
`ChangedAfterSigning`. Una firma seguita da *un'altra firma* è per definizione "seguita da altro": lo stato per
firma lo dice, ma lo stato del documento guarda solo l'ultima.

**Interfaccia.** `MainViewModel.ApplySignatureReport`: un `InfoBar` chiudibile sopra le pagine (verde/giallo/
rosso, titolo, firmatario e data - dal certificato e dall'attributo di data firmata, altrimenti da `/M`) e una
voce nella barra di stato con icona colorata. Per un documento non firmato non compare nulla. Il file viene
letto una sola volta (`AnalyzeFile`, insieme al rilevamento PDF/A) e la verifica gira fuori dal thread UI.

**Verifica.** `PdfSignatureTests`: un PDF firmato davvero, costruito nel test con un certificato autofirmato
usa-e-getta (la firma deve corrispondere ai byte che copre, quindi non può essere una fixture statica) - integro,
byte alterato, pagina aggiunta dopo, solo dati di validazione dopo, aggiornamento DSS che tocca anche il
catalogo, formato non supportato - più l'analisi delle date PDF. In app, con un file firmato reale del
committente (non incluso nel repo) e due sue copie manomesse in cartella temporanea (un byte alterato, un
oggetto pagina aggiunto in coda): i tre stati hanno mostrato banner e barra di stato attesi. Non provato:
firme con certificati senza catena, ECDSA, firme PAdES con marca temporale di documento, file molto grandi
(la verifica copia i byte firmati in memoria: costa quanto il file).

## Licenza e componenti di terze parti

Il progetto è **AGPL-3.0 o successiva**: lo impone iText Core 9.8.0 (il suo `LICENSE.md` dice "AGPL versione 3 o, a tua scelta,
successiva", oppure licenza commerciale), usato solo da `PdfFormDesigner` per creare i campi modulo. Compatibilità, verificata dai
`.nuspec` dei pacchetti effettivamente risolti (`project.assets.json`) e dai file di licenza dentro i pacchetti: Apache-2.0 (PDFium) è
compatibile con la v3 (non con la v2); MIT, BSD, zlib, FreeType Project License, Unicode e SIL OFL (font, dati distribuiti
insieme) lo sono; i contratti Microsoft (Windows App SDK, Win2D) **non sono licenze libere** e restano il punto aperto
(`THIRD-PARTY-NOTICES.md`, "Punti aperti"). Le clausole che pesano: Windows App SDK §3.b (far accettare ai destinatari termini che
proteggano Microsoft e manlevarla) contro AGPL §10 (nessuna restrizione ulteriore); il pacchetto NuGet di Win2D rimanda a un EULA
del 2014 e non contiene un testo di licenza, mentre il repository è MIT. Rimedi: distribuzione framework-dependent, domanda a
Microsoft, o parere legale. Un'eventuale eccezione di collegamento (GPLv3 §7) coprirebbe solo il codice proprio, non quello di iText.
`LICENSE` è il testo ufficiale (gnu.org); gli avvisi di PDFium sono presi dalla release `chromium/7999` di `pdfium-binaries`
(il pacchetto NuGet non li contiene); FreeType chiede un riconoscimento esplicito, riportato in `THIRD-PARTY-NOTICES.md`.

## Nota sul nome dell'assembly

L'app si chiama **PDF26** (non 26PDF): il generatore di sorgenti CsWinRT (usato da WinUI3/Windows App SDK
per i binding WinRT) deriva nomi di tipo interni dal nome assembly e non gestisce correttamente un nome che
**inizia con una cifra** ("26PDF" → generava codice non compilabile, `PDFVtableClasses` non trovato, ecc. -
verificato scaffoldando questo progetto). "PDF26" inizia con una lettera e non è affetto dal problema;
`AssemblyName`, `<Product>`, `Window.Title` e la cartella di cache temporanea usavano tutti "PDF26" (poi l'app è stata
rinominata: vedi sotto - l'`AssemblyName` è ora "YumiPDF", che inizia con una lettera e non ha lo stesso problema).

**Nome mostrato all'utente: YumiPDF.** Cambiati solo i testi che l'utente vede: titolo della finestra e nome del lavoro di stampa,
finestra "Informazioni", finestra "Opzioni" (interruttore dell'associazione file), `<Product>` e `<AssemblyTitle>` (Esplora file >
Dettagli, Gestione attività) e il nome con cui l'associazione `.pdf` si presenta a Windows (`ApplicationName` in "Apri con").
L'`AssemblyName` è poi diventato **YumiPDF**, per richiesta esplicita: l'eseguibile si chiama `YumiPDF.exe` (e `YumiPDF.dll`, `YumiPDF.pri`;
il target di publish che aggiunge il `.pri` usa `$(AssemblyName)`, quindi segue da solo). **Restano "PDF26"** gli identificatori interni: namespace `Pdf26`, la cartella delle impostazioni
e dei log in `%LocalAppData%/PDF26` (cambiarla farebbe perdere le preferenze e l'elenco dei recenti), la cartella di cache temporanea,
`ProgId`/chiave di registrazione dell'associazione, le chiavi private scritte nei PDF (`/PDF26`, `/PDF26Id`) e i nomi dei font
incorporati ("PDF26 Sans/Serif/Mono": un'annotazione già salvata con quelle chiavi resta riconoscibile).
`FileAssociationService.RefreshRegisteredName` aggiorna all'avvio, in silenzio, il nome di un'associazione registrata da una
versione precedente (non registra mai da sola). Nota: l'immagine `about.png` contiene la scritta "PDF26" disegnata nell'immagine.

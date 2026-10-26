# Roadmap

## M0 - Scaffolding (questo commit)
- [x] Solution: PdfEngine, Printing, App (WinUI3), PdfEngine.Tests
- [x] PDFium: init libreria, apertura documento (memoria/percorso), rendering pagina a bitmap
- [x] Merge / split / riordino pagine (`PdfMergeSplitService`)
- [x] Annotazione evidenziazione (`PdfAnnotationService.AddHighlight`)
- [x] Lettura campi AcroForm (`PdfFormFiller.ListFields`)
- [x] Apertura da riga di comando: percorso locale, percorso UNC, URL HTTP/HTTPS pubblico
- [x] Stampa con anteprima nel pannello nativo di Windows (Win2D, intervalli di pagine)
- [x] Viewer minimo (apri, naviga pagine, stampa)

## M1 - Viewer completo
- [x] Zoom (+/-, percentuale in toolbar) e adatta a larghezza/pagina (`MainViewModel.SetZoomAsync/FitWidthAsync/FitPageAsync`)
- [x] Rendering progressivo: placeholder dimensionati per tutte le pagine subito (scrollbar/altezza corrette da subito), bitmap popolati in background senza bloccare la UI (`RenderPagesAsync`; oggi solo per le pagine della finestra, vedi sotto)
- [x] Gestione documenti protetti da password: dialogo password con retry su password errata (`PasswordPromptDialog`, `MainViewModel.OpenOutcome`)
- [x] Associazione file `.pdf` **opzionale**, attivabile/disattivabile dal menu Opzioni (non in fase di installazione - scelta esplicita dell'utente): registrazione in `HKCU` (nessun privilegio elevato richiesto), nessuna forzatura del default di Windows (non possibile/non desiderabile forgiare `UserChoice`), link diretto alle impostazioni predefinite di Windows per completare la scelta (`FileAssociationService`)
- [x] Ricerca testo per parola chiave, a livello di pagina (non di singola occorrenza):
  `FPDFText_LoadPage/CountChars/GetText` più, per le pagine senza testo (scansioni), un
  passaggio OCR in background - vedi docs/ARCHITECTURE.md "OCR" per l'architettura e i limiti
  noti (nessuna evidenziazione della parola, formattazione numerica svizzera debole in OCR)
- [x] Navigazione da miniature: zona a scomparsa a sinistra con un "Cover Flow" verticale (`CoverFlowStrip`), animato e interattivo (clic, trascinamento, rotella, tastiera). Solo per documenti con 3+ pagine; miniature generate in background dopo il caricamento completo (vedi docs/ARCHITECTURE.md "Miniature"). Verificato con UI Automation e screenshot; il clic reale del mouse sulle miniature non è stato provato (lo script di input simulato è stato bloccato dall'antivirus)
- [x] Navigazione dall'indice (bookmark, `FPDFBookmark_*`): albero nella barra laterale sinistra, al posto delle miniature (mai insieme) - vedi docs/ARCHITECTURE.md "Indice (bookmark)"
- [x] Virtualizzazione dei bitmap: solo la pagina corrente, le 3 precedenti e le 3 successive (piu' quelle che il viewport mostra, a zoom piccolo) hanno un bitmap; le altre restano segnaposto della dimensione giusta e il bitmap viene rilasciato quando escono dalla finestra. Lo zoom ridimensiona subito tutte le pagine e ripete il rendering solo di quelle della finestra: il tasto Ingrandisci non resta piu' disattivato finche' non e' stato rifatto tutto il documento (`PdfEngine.Viewing.PageWindow`, `MainViewModel.RenderPagesAsync` - vedi docs/ARCHITECTURE.md "Finestra di pagine renderizzate")
- [ ] Virtualizzazione degli elementi della UI: ogni pagina ha comunque il suo albero di elementi (`StackPanel` non virtualizzante); con migliaia di pagine servirebbe un `ItemsRepeater` con layout ad altezze note. Rendering a tessere per gli zoom molto alti (una sola pagina puo' superare i 48 MB)
- [x] Versionamento (`Directory.Build.props`, un solo `<Version>` per tutti i progetti) e finestra "Informazioni" (`AboutDialog`: versione, breve descrizione, licenze di terze parti)
- [x] "Recenti" (pannello della barra laterale; ultimi 10 documenti, anche URL; si aggiorna a ogni apertura riuscita, toglie da solo le voci il cui file non esiste più) - vedi docs/ARCHITECTURE.md "File recenti"
- [x] Cerca e Compila modulo spostati nella barra laterale sinistra, condivisa con le miniature (in origine sovrapposti; ora **un solo pannello alla volta**, tutti e sei: Miniature, Indice, Strumenti, Compila modulo, Cerca, Recenti) - Precedente/Successivo **pagina** restano nella barra in alto, solo Precedente/Successivo **risultato di ricerca** si sono spostati; la barra di stato della ricerca mostra ora anche il numero totale di ricorrenze, non solo le pagine - vedi docs/ARCHITECTURE.md "Barra laterale sinistra"
- [x] Indicazione PDF/A nella barra di stato ("PDF/A-2B", solo per i documenti che lo dichiarano) e in un banner informativo sopra le pagine - autodichiarazione letta dai metadati XMP del file (`PdfEngine.PdfAConformance`), non una validazione completa - vedi docs/ARCHITECTURE.md "Indicazione PDF/A nella barra di stato"
- [x] Firme digitali: banner sopra le pagine e indicazione nella barra di stato (integra / modificata dopo la firma / non valida / non verificabile) - controllo dell'**integrità** del contenuto firmato, non dell'attendibilità del certificato - vedi docs/ARCHITECTURE.md "Firme digitali"
- [ ] Firme digitali: validazione del certificato (catena fino a una radice attendibile, scadenza, revoca) e formati non ancora coperti (`adbe.pkcs7.sha1`, `adbe.x509.rsa_sha1`, marche temporali `DocTimeStamp`); elenco dettagliato delle firme (pannello)

## M2 - Compilazione moduli (scrittura)
- [x] Verificato il layout di `FPDF_FORMFILLINFO` contro l'header reale scaricato dalla release GitHub di `bblanchon/pdfium-binaries` corrispondente alla versione NuGet in uso - trovati e corretti due bug (campo `xfa_disabled` mancante, `FORM_ReplaceSelection` dichiarata con reso sbagliato). Vedi docs/ARCHITECTURE.md "AcroForm filling".
- [x] Wiring dei callback richiesti per il percorso interattivo (`FFI_Invalidate`, `FFI_SetCursor`, `FFI_SetTimer`/`KillTimer`, `FFI_GetPage`, `FFI_ExecuteNamedAction`) mantenendo i delegate vivi come campi d'istanza di `PdfFormFiller`
- [x] `PdfFormFiller.SetFieldValue` per campi testo (via `FORM_SetFocusedAnnot` + `FORM_SelectAllText` + `FORM_ReplaceSelection`); richiede anche `FORM_OnAfterLoadPage`/`FORM_OnBeforeClosePage` attorno all'operazione (non solo per il rendering - scoperto testando dal vivo)
- [x] Checkbox/radio (`PdfFormFiller.SetChecked`, via simulazione di click con `FORM_OnLButtonDown`/`Up` sul rettangolo del widget - nessun setter diretto esiste nell'API)
- [x] "Appiattimento" (flatten) del form compilato in output (`PdfPage.Flatten`, `FPDFPage_Flatten`)
- [x] Verificato end-to-end con un modulo compilabile reale (33 campi testo con caratteri accentati + 2 checkbox, salvataggio, riapertura, rendering) - vedi docs/ARCHITECTURE.md
- [x] UI applicativa per la compilazione moduli: pannello "Compila modulo" (`SplitView`), un controllo per campo (`TextBox`/`CheckBox`/sola lettura per gli altri tipi), rendering live del campo appena modificato (`FPDF_FFLDraw`), "Salva con nome..." con flatten opzionale. Tre bug reali trovati testando dal vivo con screenshot/click simulati (rendering che non si aggiornava, valore "vecchio" scritto per una race sul binding, crash nativo appiattendo con l'ambiente form-fill ancora attivo) - vedi docs/ARCHITECTURE.md "Pannello Compila modulo"
- [x] Combo box e list box: voci lette da PDFium e selezionabili dal pannello (`SetComboSelectionAndRender`/`SetListSelectionAndRender`, entrambi via `FORM_SetIndexSelected`). Il percorso testuale provato all'inizio funzionava solo per la prima scelta di un combo vuoto e ignorava ogni cambio successivo; `FORM_SetIndexSelected` da sola *aggiunge* alla selezione di una lista multipla, quindi le altre voci vengono deselezionate prima. Coperti da test con `Fixtures/form-choices.pdf`
- [ ] Salvataggio senza "Unisci": il valore è corretto riaprendo il file, ma l'aspetto dei campi non viene rigenerato (un render senza livello moduli mostra i campi vuoti, anche quelli di testo). Da decidere: unire sempre, oppure chiedere ai lettori di rigenerare l'aspetto (`NeedAppearances`)
- [ ] Metadati dei file salvati: oggi il salvataggio (PDFium, `FPDF_SaveAsCopy`) conserva quelli del documento originale (**salvo quando si sono aggiunti campi modulo: iText scrive il proprio Producer, "iText Core ... (AGPL version)"**), quindi un modulo prodotto da un altro software (es. GemBox.Pdf) resta con il suo Producer anche dopo essere stato compilato e salvato con PDF26. Da valutare: impostare un proprio Producer (es. "PDF26 <versione>") e la data di modifica - ma l'API pubblica di PDFium ha solo `FPDF_GetMetaText` (lettura, verificato in `fpdf_doc.h`) e nessun setter: servirebbe riscrivere il dizionario Info a livello di file, non con un semplice binding. Va deciso anche se sovrascrivere o preservare il Producer originale
- [x] Aggiungere campi di testo e caselle di controllo compilabili (pannello "Strumenti" > "Campi modulo"): si piazzano sulla pagina, si spostano, ridimensionano, rinominano, si eliminano, e si scrivono nel file al "Salva con nome..." (il file salvato si riapre con i campi già compilabili). Scritti con iText (AGPL) - vedi docs/ARCHITECTURE.md "Campi modulo creati dall'utente"
- [ ] Modificare i campi che il documento aveva già (spostare, ridimensionare, rinominare, eliminare): lo scrittore (`PdfFormDesigner.Apply`, `ModifyFieldChange`/`RemoveFieldChange`) lo sa già fare ed è testato; manca l'interfaccia, perché un campo esistente spostato continuerebbe a comparire nella vecchia posizione finché non si salva (il suo disegno è nel bitmap della pagina). Da risolvere nascondendolo temporaneamente, o ricaricando il documento a ogni modifica
- [ ] Altri tipi di campo (pulsanti di scelta, caselle combinate, elenchi) e proprietà avanzate dei campi di testo (valore predefinito, allineamento, bordo e sfondo)
- [ ] Fixture di test AcroForm per `PdfEngine.Tests`: c'è `Fixtures/form-choices.pdf` (combo e liste, generata a mano); mancano ancora campi di testo, checkbox e radio (oggi verificati a mano contro documenti reali non inclusi nel repo)

## Allineamento
- [x] "Aggancia alla griglia": griglia di punti (10 pt, dall'angolo in alto a sinistra) sulle pagine e aggancio di campi modulo, note e timbri in inserimento, spostamento e ridimensionamento; stato ricordato - vedi docs/ARCHITECTURE.md "Griglia di allineamento"
- [ ] Passo della griglia regolabile; guide di allineamento ai bordi degli altri campi (oggi solo la griglia)

## Stampa

- [x] I campi compilati e non ancora appiattiti vengono stampati tramite l'ambiente moduli della copia di stampa.
- [x] Anteprima integrata nel pannello nativo (`PrintManagerInterop` + Win2D `CanvasPrintDocument`), opzioni stampante e intervalli, proporzioni e margini corretti, rendering su richiesta e buffer di una pagina alla volta. Copia indipendente del documento per non alterare la sessione durante stampa/annullamento. Vedi ARCHITECTURE.md "Stampa con anteprima integrata".
- [x] Gestione dell'annullamento e delle chiamate tardive a risorse native già chiuse; invio al driver in background, compreso il dialogo Salva di Microsoft Print to PDF.
- [x] Collaudo interattivo del pannello, dei moduli in anteprima, del cambio stampante/orientamento e della riapertura dopo Annulla. Verificato l'output PDF delle sole pagine `2,4` nell'ordine corretto (24 ottobre 2026).
- [x] Test automatici su geometria, intervalli e copia del documento; suite completa: 120 test superati al collaudo del 24 ottobre 2026.
- [x] [Guida alla stampa](STAMPA.md) con flusso, impostazioni e limiti.
- [ ] Verifica su più stampanti fisiche/driver e documenti molto lunghi.
- [ ] Stampa vettoriale (oggi il percorso è raster a 300 dpi con limiti per pagina).

## M3 - Editor / annotazioni avanzate
- [x] Nota e timbro (testuale con data opzionale, o immagine dell'utente): pannello laterale "Strumenti" (pulsante in barra) + "Salva con nome...", selezione/spostamento/ridimensionamento/eliminazione sulla pagina, avviso di modifiche non salvate - vedi docs/ARCHITECTURE.md "Annotazioni: note e timbri"
- [x] Modifica del testo già presente (solo riga per riga, nessun riflusso): "Modifica testo" nel pannello "Strumenti"; una riga con lettere che il suo font non ha viene riscritta con un font open source incorporato (Liberation/Noto Sans, SIL OFL, ridotti a latino/greco/cirillico) scelto per somigliare all'originale - vedi docs/ARCHITECTURE.md "Modifica del testo esistente"
- [ ] Editing di **immagini** esistenti (sposta, ridimensiona, sostituisci, elimina): non richiesto per ora, lo stesso meccanismo (oggetti pagina staccati/reinseriti) dovrebbe bastare
- [ ] Testo dentro i Form XObject (alcuni produttori avvolgono tutta la pagina in uno): oggi non compare fra le righe modificabili
- [ ] Caratteri fuori da latino/greco/cirillico (CJK, arabo, ebraico, ...): nessun font incorporato li ha, appaiono come riquadri vuoti (l'app lo segnala)
- [x] Annulla/Ripeti (Ctrl+Z, Ctrl+Y, pulsanti in barra) per note, timbri, campi modulo aggiunti e modifiche al testo - vedi docs/ARCHITECTURE.md "Annulla e ripeti". Non copre: i valori scritti nei campi di un modulo compilato (il pannello "Compila modulo"), le modifiche di un timbro che era già nel file prima di eliminarlo (non annullabile: la cronologia si azzera)

## Licenze
- [x] Licenza del progetto: **AGPL-3.0 o successiva** (`LICENSE`), imposta da iText Core; elenco e testi dei componenti di terze parti in `THIRD-PARTY-NOTICES.md` e `third-party-licenses/`, copiati nel pacchetto in `Licenses/`
- [x] Pacchetti Windows App SDK ridotti ai componenti usati (via ONNX Runtime/DirectML/Windows AI dal pacchetto)
- [ ] Windows App SDK e Win2D (contratti Microsoft) rispetto all'AGPL: tensione aperta, vedi `THIRD-PARTY-NOTICES.md` "Punti aperti" (distribuzione framework-dependent, domanda a Microsoft su Win2D, o parere legale)
- [ ] Intestazioni SPDX (`AGPL-3.0-or-later`) nei file sorgente, se si vuole renderle esplicite file per file

## M4 - Distribuzione
- [ ] Installer MSI/EXE (WiX Toolset o Velopack), firma del codice
- [ ] Aggiornamenti automatici

Nota: l'associazione file `.pdf` è già implementata come opzione runtime (menu Opzioni, vedi M1),
non a livello di installer - scelta esplicita dell'utente invece di una registrazione automatica
silenziosa all'installazione.

## Backlog / non pianificato
- Testo libero (annotazione di testo scritta direttamente sulla pagina) - spostato da M3
- Disegno a mano libera (ink) - spostato da M3
- Supporto XFA (fuori scope per scelta di progetto)
- Cloud storage autenticato (OneDrive/SharePoint/Google Drive) per l'apertura da riga di comando - oggi si supportano solo URL HTTP/HTTPS pubblici
- Firma digitale con certificato (PKI)
- Redazione permanente del contenuto

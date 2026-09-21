# Roadmap

## M0 - Scaffolding (questo commit)
- [x] Solution: PdfEngine, Printing, App (WinUI3), PdfEngine.Tests
- [x] PDFium: init libreria, apertura documento (memoria/percorso), rendering pagina a bitmap
- [x] Merge / split / riordino pagine (`PdfMergeSplitService`)
- [x] Annotazione evidenziazione (`PdfAnnotationService.AddHighlight`)
- [x] Lettura campi AcroForm (`PdfFormFiller.ListFields`)
- [x] Apertura da riga di comando: percorso locale, percorso UNC, URL HTTP/HTTPS pubblico
- [x] Stampa via GDI classico (dialogo di stampa nativo, intervallo pagine)
- [x] Viewer minimo (apri, naviga pagine, stampa)

## M1 - Viewer completo
- [x] Zoom (+/-, percentuale in toolbar) e adatta a larghezza/pagina (`MainViewModel.SetZoomAsync/FitWidthAsync/FitPageAsync`)
- [x] Rendering progressivo: placeholder dimensionati per tutte le pagine subito (scrollbar/altezza corrette da subito), bitmap popolati in background senza bloccare la UI (`RenderAllPagesAsync`)
- [x] Gestione documenti protetti da password: dialogo password con retry su password errata (`PasswordPromptDialog`, `MainViewModel.OpenOutcome`)
- [x] Associazione file `.pdf` **opzionale**, attivabile/disattivabile dal menu Opzioni (non in fase di installazione - scelta esplicita dell'utente): registrazione in `HKCU` (nessun privilegio elevato richiesto), nessuna forzatura del default di Windows (non possibile/non desiderabile forgiare `UserChoice`), link diretto alle impostazioni predefinite di Windows per completare la scelta (`FileAssociationService`)
- [ ] Ricerca testo (`FPDFText_*`) - rimandato: richiede una nuova superficie di binding nativi (`FPDFText_LoadPage`, `FindStart/FindNext`, `GetCharIndex`, ecc.) più UI di evidenziazione risultati
- [x] Navigazione da miniature: zona a scomparsa a sinistra con un "Cover Flow" verticale (`CoverFlowStrip`), animato e interattivo (clic, trascinamento, rotella, tastiera). Solo per documenti con 3+ pagine; miniature generate in background dopo il caricamento completo (vedi docs/ARCHITECTURE.md "Miniature"). Verificato con UI Automation e screenshot; il clic reale del mouse sulle miniature non è stato provato (lo script di input simulato è stato bloccato dall'antivirus)
- [ ] Navigazione dall'indice (bookmark, `FPDFBookmark_*`) - rimandato: binding nativi aggiuntivi + pannello laterale
- [ ] Virtualizzazione reale del viewport (oggi ogni pagina viene comunque renderizzata e tenuta in memoria, solo in modo progressivo/asincrono - per documenti molto grandi serebbe da scaricare/saltare le pagine lontane dallo scroll corrente)

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
- [ ] Fixture di test AcroForm per `PdfEngine.Tests`: c'è `Fixtures/form-choices.pdf` (combo e liste, generata a mano); mancano ancora campi di testo, checkbox e radio (oggi verificati a mano contro documenti reali non inclusi nel repo)

## Stampa
- [x] I campi compilati e non ancora appiattiti vengono stampati (la stampa usa lo stesso render della vista)
- [ ] Anteprima di stampa nel dialogo - **posticipata**. Percorso individuato: stampa nativa Windows (`PrintManagerInterop` + `Microsoft.UI.Xaml.Printing.PrintDocument`, entrambi presenti in Windows App SDK 2.3.1 - la nota in ARCHITECTURE.md "Stampa: perché non l'API di stampa WinRT" è superata). Prima di iniziare: prova rapida (2-4 h) che il pannello si apra da app WinUI3 non pacchettizzata. Da sistemare comunque, con o senza anteprima: la stampa GDI tiene pinnati i buffer di tutte le pagine per l'intero lavoro (~139 MB/pagina A4 a 600 dpi) e stira la pagina sull'intero foglio ignorando margini e proporzioni

## M3 - Editor / annotazioni avanzate
- [ ] Nota/testo libero, disegno a mano libera (ink), timbro
- [ ] Editing di testo/immagini esistenti nella pagina (fuori scope M0-M2; da valutare se resti su PDFium o richieda una libreria aggiuntiva)
- [ ] Undo/redo sulle modifiche

## M4 - Distribuzione
- [ ] Installer MSI/EXE (WiX Toolset o Velopack), firma del codice
- [ ] Aggiornamenti automatici

Nota: l'associazione file `.pdf` è già implementata come opzione runtime (menu Opzioni, vedi M1),
non a livello di installer - scelta esplicita dell'utente invece di una registrazione automatica
silenziosa all'installazione.

## Backlog / non pianificato
- Supporto XFA (fuori scope per scelta di progetto)
- Cloud storage autenticato (OneDrive/SharePoint/Google Drive) per l'apertura da riga di comando - oggi si supportano solo URL HTTP/HTTPS pubblici
- Firma digitale con certificato (PKI)
- Redazione permanente del contenuto

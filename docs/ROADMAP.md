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
- [ ] Zoom/pan, adatta a larghezza/pagina, ricerca testo (`FPDFText_*`)
- [ ] Navigazione da miniature e da indice (bookmark, `FPDFBookmark_*`)
- [ ] Rendering asincrono/paginato per documenti grandi (attualmente ogni pagina è renderizzata in modo sincrono sul thread UI)
- [ ] Gestione documenti protetti da password (già supportata a livello di `PdfDocument.OpenFromBytes(data, password)`, manca l'UI)
- [ ] Registrazione file association (`.pdf`) + verbo "Apri con" in fase di installazione

## M2 - Compilazione moduli (scrittura)
- [ ] Verificare il layout di `FPDF_FORMFILLINFO` contro l'header vendorizzato nella versione esatta di `bblanchon.PDFium.Win32` in uso (vedi docs/ARCHITECTURE.md)
- [ ] Wiring dei callback minimi richiesti per il percorso interattivo (`FFI_Invalidate`, `FFI_SetTimer`/`KillTimer`, `FFI_GetPage`) mantenendo i delegate vivi (campi `static readonly`)
- [ ] `PdfFormFiller.SetFieldValue` per campi testo (via `FORM_SetFocusedAnnot` + `FORM_SelectAllText` + `FORM_ReplaceSelection`)
- [ ] Checkbox/radio/combo/listbox (percorso diverso da quello testuale)
- [ ] "Appiattimento" (flatten) del form compilato in output

## M3 - Editor / annotazioni avanzate
- [ ] Nota/testo libero, disegno a mano libera (ink), timbro
- [ ] Editing di testo/immagini esistenti nella pagina (fuori scope M0-M2; da valutare se resti su PDFium o richieda una libreria aggiuntiva)
- [ ] Undo/redo sulle modifiche

## M4 - Distribuzione
- [ ] Installer MSI/EXE (WiX Toolset o Velopack), firma del codice
- [ ] Associazione file `.pdf` e voce "Apri con" nel registro (installer, non runtime)
- [ ] Aggiornamenti automatici

## Backlog / non pianificato
- Supporto XFA (fuori scope per scelta di progetto)
- Cloud storage autenticato (OneDrive/SharePoint/Google Drive) per l'apertura da riga di comando - oggi si supportano solo URL HTTP/HTTPS pubblici
- Firma digitale con certificato (PKI)
- Redazione permanente del contenuto

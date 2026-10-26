# Stampa con anteprima

YumiPDF usa il pannello di stampa nativo di Windows: anteprima e impostazioni si trovano nella stessa
finestra, aperta dal pulsante **Stampa...** della barra principale.

## Come stampare

1. Apri il documento e completa le modifiche o la compilazione dei moduli.
2. Premi **Stampa...** e scegli la stampante nel pannello di Windows.
3. Imposta le pagine, il formato carta e l'orientamento. Controlla il risultato nell'anteprima;
   usa le frecce per sfogliare il documento.
4. Premi **Stampa** per inviare il lavoro oppure **Annulla** per tornare al PDF.

L'anteprima viene aggiornata quando cambiano stampante, carta o orientamento. Durante una sessione
di stampa il pulsante Stampa dell'app è disabilitato, per evitare l'apertura di lavori sovrapposti;
torna disponibile al termine o dopo l'annullamento.

## Impostazioni

| Opzione | Comportamento |
| --- | --- |
| Stampante | Mostra le stampanti disponibili in Windows, comprese quelle virtuali. |
| Pagine | Scegli tutte le pagine o un intervallo personalizzato, come `1-3,5` oppure `2,4`. I numeri partono da 1. |
| Copie | Disponibile se supportato dalla stampante. |
| Orientamento | Verticale o orizzontale. Il valore iniziale segue la prima pagina del PDF. |
| Formato carta | Usa i formati proposti dalla stampante selezionata. |
| Colore e fronte/retro | Le opzioni visibili dipendono dalle funzionalità del driver. |
| Altre impostazioni | Apre le ulteriori impostazioni offerte da Windows e dal driver. |

L'intervallo determina le pagine inviate alla stampante; l'anteprima consente di sfogliare il documento.
Gli intervalli sovrapposti non duplicano le pagine: ogni pagina selezionata viene inviata una sola volta,
nell'ordine del documento. Il numero di copie resta una scelta separata della stampante.

## Impaginazione e contenuti

Ogni pagina viene adattata all'area stampabile del foglio, mantenendo le proporzioni e rispettando anche
margini non simmetrici. Un PDF con pagine di dimensioni diverse viene adattato al formato carta scelto;
l'orientamento del foglio non cambia automaticamente passando da una pagina all'altra.
Attualmente l'adattamento è automatico: non è prevista una scelta di scala percentuale o di dimensioni reali.

La stampa include testo modificato, annotazioni, valori dei moduli e campi appena aggiunti. Non occorre
salvare o appiattire prima il documento. YumiPDF prepara una copia in memoria dello stato corrente per
il lavoro di stampa; il PDF aperto mantiene le sue modifiche e il suo stato di salvataggio.
Gli eventuali riconoscimenti OCR usati per la ricerca non aggiungono uno strato di testo al file stampato.

## Stampare su un file PDF

Seleziona **Microsoft Print to PDF**, controlla le impostazioni e premi **Stampa**. Windows apre il dialogo
**Salva output di stampa con nome**, nel quale scegli il percorso del nuovo PDF.

Questo file rappresenta l'output di stampa: le pagine vengono rasterizzate e i moduli non restano
compilabili. Per conservare gli elementi modificabili del documento usa **Salva con nome...** nell'app,
con le opzioni appropriate per i moduli.

## Qualità e messaggi

La stampa usa immagini a 300 dpi, mentre l'anteprima usa una risoluzione più leggera. Per fogli
eccezionalmente grandi la risoluzione può essere ridotta dai limiti di memoria per pagina.
Il percorso attuale non produce una stampa vettoriale del PDF originale.

**Documento inviato alla stampante.** conferma che il lavoro è stato consegnato al sistema di stampa;
non certifica che la stampante abbia già prodotto i fogli. Gli errori vengono mostrati nella barra di stato.
Se la stampa non è disponibile sul dispositivo, l'app lo segnala nello stesso punto.

## Verifiche eseguite

Il collaudo del 24 ottobre 2026 comprende apertura del pannello, anteprima dei moduli compilati,
cambio di stampante e orientamento, annullamento e riapertura. La stampa su Microsoft Print to PDF
delle pagine `2,4` di un documento sintetico di quattro pagine ha prodotto due pagine, con i contenuti
corrispondenti nell'ordine corretto. L'app è rimasta aperta e ha riabilitato Stampa.

Nello stesso collaudo la suite automatica ha superato **120 test**, inclusi i controlli specifici della
stampa. Restano da verificare stampanti fisiche, driver diversi e documenti molto lunghi.

Per i dettagli di implementazione vedi [Stampa con anteprima integrata](ARCHITECTURE.md#stampa-con-anteprima-integrata).

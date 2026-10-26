# Installer

Non ancora implementato (vedi [docs/ROADMAP.md](../docs/ROADMAP.md) M4) - oggi esiste solo il pacchetto
cartella/zip più sotto, non un vero installer MSI/EXE. Note per quando si affronta un installer vero:

## Opzioni valutate

- **WiX Toolset v4/v5** - pieno controllo su registro, componenti, upgrade in-place. Più verboso da
  configurare, nessuna UI di aggiornamento automatico integrata.
- **Velopack** - pensato per app .NET, aggiornamenti automatici pronti all'uso, meno controllo fine sul registro
  rispetto a WiX (va comunque bene per la maggior parte dei casi).

## Cosa deve fare l'installer

1. Pubblicare `src/App` come self-contained per il RID target (`win-x64` e/o `win-arm64`):
   ```
   dotnet publish src/App/App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
   ```
2. Firma del codice dell'eseguibile e del pacchetto installer (certificato EV o OV) prima della distribuzione,
   per evitare gli avvisi SmartScreen di Windows.

Nota: l'installer **non** deve registrare l'associazione con `.pdf` - è una scelta esplicita e reversibile
che l'utente attiva/disattiva dal menu Opzioni dell'app (`Services/FileAssociationService.cs`), non qualcosa
che va imposto in fase di installazione.

## Distribuzione della stampa con anteprima

La stampa usa `Microsoft.Graphics.Win2D` 1.4.0, referenziato da `src/App`. Distribuire l'intera cartella
prodotta da `dotnet publish`, con `Microsoft.Graphics.Canvas.dll`, `Microsoft.Graphics.Canvas.Interop.dll`
e le risorse WinUI: copiare soltanto `YumiPDF.exe` non è sufficiente.
La finestra Informazioni e il README riportano il componente Win2D e la licenza del pacchetto.

Dopo aver preparato il pacchetto, verificarlo anche dalla cartella estratta: aprire un PDF, richiamare
Stampa, controllare l'anteprima e annullare; riaprire Stampa e produrre un file con Microsoft Print to PDF.
Il collaudo della build non sostituisce questa verifica del pacchetto distribuito.

Il pacchetto `YumiPDF-0.1.1-win-x64-portable.zip` in `installer/out/` è stato ricreato con la nuova stampa (verificato
estraendolo in una cartella nuova: parte, il pannello di stampa si apre e si annulla, la finestra Informazioni è corretta).
I file ZIP non vengono aggiornati da `dotnet build`: dopo ogni modifica occorre pubblicare nuovamente l'app e ricreare il pacchetto. Per il flusso utente e i limiti
vedi la [guida alla stampa](../docs/STAMPA.md).

## Pacchetti Windows App SDK ridotti, licenze nel pacchetto

`src/App/App.csproj` non referenzia più l'ombrello `Microsoft.WindowsAppSDK`, ma solo i componenti che servono (`Base`,
`Foundation`, `InteractiveExperiences`, `WinUI`, `DWrite`). L'ombrello tirava dentro anche Windows AI, Windows ML (ONNX Runtime,
DirectML) e Widgets, che un visualizzatore PDF non usa e che una build self-contained ridistribuiva: la pubblicazione è passata da
496 a circa 400 file e da 278 a 192 MB, lo zip da 106 a 75 MB, e con loro sono spariti ONNX Runtime, DirectML e le DLL di Windows AI
dall'elenco delle licenze da portarsi dietro. WebView2 resta (dipendenza di WinUI). Verificato: build, pubblicazione, avvio dalla
cartella pubblicata e dallo zip estratto, interfaccia, miniature, finestre, pannello di stampa (Win2D).

`LICENSE`, `THIRD-PARTY-NOTICES.md` e tutta la cartella `third-party-licenses/` vengono copiate nell'output (e quindi nello zip) in
`Licenses/` (`LICENSE-AGPL-3.0.txt`, `THIRD-PARTY-NOTICES.md`, `Licenses/third-party/...`), insieme ai testi dei font già presenti.
Se si aggiorna un pacchetto (PDFium, .NET, Windows App SDK...) vanno aggiornati anche i testi in `third-party-licenses/` e le
versioni in `THIRD-PARTY-NOTICES.md`.

## Bug reale trovato pubblicando: crash immediato in Microsoft.UI.Xaml.dll

`dotnet publish` (a differenza di `dotnet build`) non copiava l'output proprio del compilatore XAML -
`YumiPDF.pri` (l'indice delle risorse; `PDF26.pri` finché l'app si chiamava così) e ogni `.xbf` (`App.xbf` alla radice, uno per `Views/*.xaml`) - nella
cartella di publish, pur essendo già presenti in `$(OutDir)` quando Publish parte (Publish dipende da
Build). Il risultato pubblicato si avviava e andava subito in crash dentro `Microsoft.UI.Xaml.dll` stesso
(verificato dal vivo: Visualizzatore eventi di Windows, "Application Error", codice eccezione `0xc000027b`,
modulo in errore `Microsoft.UI.Xaml.dll`) - senza quei file il runtime XAML non ha nulla da cui caricare la
finestra/le pagine dell'app. Non un problema di firma del codice o di SmartScreen: l'eseguibile proprio non
parte.

Risolto in `src/App/App.csproj` con un target che si aggancia a `ComputeFilesToPublish` (il punto di
estensione che `dotnet publish` stesso usa per decidere cosa copiare) e aggiunge esplicitamente
`$(OutDir)**\*.xbf` e `$(OutDir)$(AssemblyName).pri` a `@(ResolvedFileToPublish)`. Il pattern esclude la cartella `publish`
stessa (che sta sotto `$(OutDir)`): senza l'esclusione, dalla seconda pubblicazione in poi raccoglieva anche i `.xbf` della
pubblicazione precedente e li copiava in `publish/publish/...` (6 file inutili finiti anche nel primo zip portable). Verificato dal vivo dopo
la correzione: l'eseguibile pubblicato (`dotnet publish -c Release -r win-x64 --self-contained true`) si
avvia e resta in esecuzione normalmente.

## Pacchetto cartella/zip (oggi)

Senza un vero installer, la distribuzione più semplice è pubblicare e zippare l'output:
```
dotnet publish src/App/App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
```
L'output finisce in `src/App/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/` (~270 MB, self-
contained: include il runtime .NET e Windows App SDK, non richiede nulla preinstallato sulla macchina di
destinazione oltre a Windows 10 1809+/11). Zippato (`installer/out/`, ignorato da git) pesa circa 100 MB.
Nessuna voce nel menu Start, nessun disinstallatore: l'utente estrae la cartella e avvia `YumiPDF.exe`
direttamente. Senza certificato di firma del codice, Windows SmartScreen avviserà comunque al primo avvio -
non evitabile senza un certificato EV/OV (vedi sopra).

### Zip portable (come è stato fatto per la 0.1.1)

`installer/out/YumiPDF-<versione>-win-x64-portable.zip` (circa 107 MB; la cartella `installer/out/` è ignorata da git). Contiene una
cartella `YumiPDF/` con l'output del publish **senza i `.pdb`** (simboli di debug) più un `LEGGIMI.txt` (uso, dove stanno le
preferenze, nota su SmartScreen, licenze: iText è AGPL, quindi indica il repository con i sorgenti). Si crea con `ZipArchive` di
.NET dopo il `dotnet publish` qui sopra. Verificato estraendolo in una cartella nuova e avviando `YumiPDF.exe` da lì: parte,
apre un PDF, la finestra "Informazioni" mostra la versione 0.1.1.

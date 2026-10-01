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

## Bug reale trovato pubblicando: crash immediato in Microsoft.UI.Xaml.dll

`dotnet publish` (a differenza di `dotnet build`) non copiava l'output proprio del compilatore XAML -
`PDF26.pri` (l'indice delle risorse) e ogni `.xbf` (`App.xbf` alla radice, uno per `Views/*.xaml`) - nella
cartella di publish, pur essendo già presenti in `$(OutDir)` quando Publish parte (Publish dipende da
Build). Il risultato pubblicato si avviava e andava subito in crash dentro `Microsoft.UI.Xaml.dll` stesso
(verificato dal vivo: Visualizzatore eventi di Windows, "Application Error", codice eccezione `0xc000027b`,
modulo in errore `Microsoft.UI.Xaml.dll`) - senza quei file il runtime XAML non ha nulla da cui caricare la
finestra/le pagine dell'app. Non un problema di firma del codice o di SmartScreen: l'eseguibile proprio non
parte.

Risolto in `src/App/App.csproj` con un target che si aggancia a `ComputeFilesToPublish` (il punto di
estensione che `dotnet publish` stesso usa per decidere cosa copiare) e aggiunge esplicitamente
`$(OutDir)**\*.xbf` e `$(OutDir)$(AssemblyName).pri` a `@(ResolvedFileToPublish)`. Verificato dal vivo dopo
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
Nessuna voce nel menu Start, nessun disinstallatore: l'utente estrae la cartella e avvia `PDF26.exe`
direttamente. Senza certificato di firma del codice, Windows SmartScreen avviserà comunque al primo avvio -
non evitabile senza un certificato EV/OV (vedi sopra).

# PDF26

Visualizzatore, editor e compilatore di moduli PDF nativo per Windows 10 e 11.

## Funzionalità (stato attuale - vedi [docs/ROADMAP.md](docs/ROADMAP.md))

- Apertura da riga di comando di file locali, percorsi UNC di rete e URL HTTP/HTTPS pubblici
- Visualizzazione pagine (rendering PDFium)
- Unione, suddivisione e riordino di PDF
- Lettura dei campi AcroForm (nome/tipo/valore)
- Annotazione di evidenziazione
- Stampa (dialogo nativo, intervallo pagine)

La compilazione **in scrittura** dei campi modulo, le annotazioni avanzate (nota, disegno libero, timbro) e
l'installer sono pianificati nelle milestone successive - vedi la roadmap.

## Requisiti di sviluppo

- Windows 10/11
- Visual Studio 2022 (17.12+) con i workload **.NET Desktop Development** e **Windows App SDK C#**
  (necessario per compilare `src/App`, il progetto WinUI3 - la build da riga di comando richiede comunque
  gli stessi componenti del Windows SDK installati da questi workload)
- .NET SDK 10.0+ (già installato in questo ambiente)

## Struttura

```
26PDF.slnx
├── src/PdfEngine    Libreria PDFium (rendering, merge/split, annotazioni, moduli). Nessuna dipendenza UI.
├── src/Printing     Stampa (GDI classico), progetto separato dall'app WinUI3 per motivi di build.
├── src/App          App WinUI3 (Windows App SDK), unpackaged, installer MSI/EXE previsto.
├── tests/PdfEngine.Tests
├── docs/            Architettura e roadmap
└── installer/       Note per la fase di packaging (non ancora implementato)
```

Dettagli architetturali (perché PDFium, perché GDI per la stampa, l'unico punto delicato del codice nativo)
in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Build

```bash
dotnet build 26PDF.slnx -c Debug
```

Per compilare/pubblicare solo l'app per un RID specifico (necessario per `publish`, non per `build`;
fare prima una build della solution completa - il compilatore XAML di WinUI3 a volte non risolve i tipi
del progetto se viene invocato isolatamente prima che esista una build precedente):

```bash
dotnet build src/App/App.csproj -c Debug -r win-x64
```

Test:

```bash
dotnet test tests/PdfEngine.Tests/PdfEngine.Tests.csproj
```

Avvio con un documento passato da riga di comando (locale, UNC o URL):

```bash
dotnet run --project src/App/App.csproj -- "C:\percorso\file.pdf"
dotnet run --project src/App/App.csproj -- "\\server\condivisione\file.pdf"
dotnet run --project src/App/App.csproj -- "https://example.com/file.pdf"
```

## Licenze di terze parti

- PDFium (via `bblanchon.PDFium.Win32`): Apache-2.0
- CommunityToolkit.Mvvm: MIT

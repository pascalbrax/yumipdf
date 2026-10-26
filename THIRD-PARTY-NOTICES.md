# Componenti di terze parti e licenze

YumiPDF è distribuito sotto **GNU Affero General Public License v3 o successiva** (vedi [LICENSE](LICENSE)).
Questo file elenca i componenti di terze parti che l'app usa o distribuisce, con la loro licenza. I testi
completi sono nella cartella [`third-party-licenses/`](third-party-licenses/) e, nel pacchetto distribuito,
nella cartella `Licenses/` accanto a `YumiPDF.exe`.

Elenco compilato dai metadati dei pacchetti NuGet (`.nuspec`) e dai file di licenza che i pacchetti stessi contengono;
dove un pacchetto non dichiara nulla lo dice. Non è un parere legale.

## Compatibilità con l'AGPL-3.0

| Licenza | Componenti | Con l'AGPL-3.0-or-later |
| --- | --- | --- |
| AGPL-3.0-or-later | iText Core | È la licenza che determina quella dell'insieme |
| Apache-2.0 | PDFium, Abseil, LLVM libc | Compatibile (Apache-2.0 è compatibile con la v3, non con la v2) |
| MIT / BSD / zlib / libpng / IJG / Unicode / FreeType Project License | il resto di PDFium, .NET, CommunityToolkit.Mvvm, WebView2 | Compatibili (obblighi: conservare avvisi e testi; per FreeType anche il riconoscimento qui sotto) |
| SIL OFL 1.1 | font Liberation (derivati), Noto Sans | Compatibile: i font sono dati distribuiti insieme al programma, non codice collegato |
| Contratto Microsoft | Windows App SDK, Win2D (se vale l'EULA del pacchetto) | **Non è una licenza libera**: vedi "Punti aperti" |

## Componenti

| Componente | Versione | Licenza | Testi |
| --- | --- | --- | --- |
| iText Core (`itext`, `itext.commons`) | 9.8.0 | AGPL-3.0 o successiva, oppure licenza commerciale iText | `third-party-licenses/itext/` (include gli avvisi del codice di terzi che iText incorpora: woff2, libtiff, brotli, zlib...) |
| PDFium (`bblanchon.PDFium.Win32`, binari di `pdfium-binaries`) | 153.0.7999 | Apache-2.0 (PDFium); i binari di Benoit Blanchon: MIT | `third-party-licenses/pdfium/` |
| ...codice di terzi dentro `pdfium.dll` | | Abseil (Apache-2.0), AGG 2.3, fast_float (MIT), FreeType (FreeType Project License), ICU/Unicode (Unicode License v3), Little CMS (MIT), libjpeg-turbo (IJG/BSD), OpenJPEG (BSD-2), libpng, libtiff, LLVM libc (Apache-2.0 con eccezioni LLVM), simdutf (MIT), zlib | `third-party-licenses/pdfium/` |
| .NET runtime (incluso: build self-contained) | 10.0.10 | MIT, con avvisi di terzi | `third-party-licenses/dotnet-runtime/` |
| `Microsoft.Extensions.*`, `System.Security.Cryptography.Pkcs`/`.Xml`, `Microsoft.Extensions.DependencyModel` | 5.0.0 / 10.0.x / 6.0.2 | MIT | `third-party-licenses/dotnet-libraries/` |
| `Microsoft.DotNet.PlatformAbstractions` | 1.1.0 | Il pacchetto non dichiara la licenza; è codice Microsoft/.NET Foundation (repository `dotnet/core-setup`, MIT) | - |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | `third-party-licenses/communitytoolkit-mvvm/` |
| Windows App SDK (`Microsoft.WindowsAppSDK.Base`, `.Foundation`, `.InteractiveExperiences`, `.WinUI`, `.DWrite`) | 2.0.4 / 2.3.5 / 2.1.3 / 2.3.0 / 2.1.0 | Microsoft Software License Terms (contratto, non open source) | `third-party-licenses/windows-app-sdk/` |
| Microsoft Edge WebView2 (dipendenza di WinUI, non usato dall'app) | 1.0.3719.77 | BSD-3-Clause (Microsoft) | `third-party-licenses/webview2/` |
| Win2D (`Microsoft.Graphics.Win2D`), pannello di stampa | 1.4.0 | Il pacchetto NuGet rimanda a un EULA Microsoft e non contiene un file di licenza; il repository GitHub del progetto è, per quanto risulta, MIT | - (vedi "Punti aperti") |
| Font Liberation 2.1.5 (derivati, rinominati "PDF26 Sans/Serif/Mono"), Noto Sans | | SIL Open Font License 1.1 | `src/PdfEngine/Fonts/LICENSE-*.txt` (nel pacchetto: `Licenses/`) |

Solo in compilazione, non distribuiti: `Microsoft.Windows.SDK.BuildTools` e `.MSIX`.

### Riconoscimenti richiesti

- **FreeType** (dentro PDFium): *Portions of this software are copyright © 1996-2002, 2006 The FreeType Project
  (www.freetype.org). All rights reserved.*
- **PDFium**: © The PDFium Authors. **Little CMS**: © 1998-2026 Marti Maria Saguer. **libpng**: © The PNG Reference Library Authors.
  **zlib**: © Jean-loup Gailly e Mark Adler. I testi completi sono in `third-party-licenses/pdfium/`.

## Punti aperti (da risolvere prima di una distribuzione ampia)

1. **Windows App SDK e AGPL.** Il contratto Microsoft consente di ridistribuire le DLL che il pacchetto copia con l'app (anche in
   modalità self-contained), ma chiede (§3.b) di far accettare a chi le riceve termini che proteggano Microsoft almeno quanto il
   contratto e di manlevare Microsoft. L'AGPL vieta di imporre restrizioni ulteriori ai destinatari (§10). Non c'è una soluzione
   consolidata: molte applicazioni GPL per Windows convivono con questa tensione, sostenendo che il framework della piattaforma
   sia una "libreria di sistema" (GPLv3 §1). La distribuzione *framework-dependent* (l'utente installa il Windows App Runtime da
   Microsoft) la eviterebbe del tutto, al prezzo di un'installazione in più. Per una distribuzione commerciale, parere legale.
2. **Win2D.** Verificare la licenza del pacchetto (EULA nel NuGet, MIT nel repository), ad esempio chiedendo a Microsoft con una
   issue su GitHub, oppure compilando Win2D dai sorgenti.
3. **Obblighi dell'AGPL per chi distribuisce YumiPDF**: fornire il testo della licenza (`LICENSE`) e il codice sorgente corrispondente
   (https://github.com/pascalbrax/yumipdf), con le eventuali modifiche, sotto la stessa licenza.

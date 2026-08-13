# Installer

Non ancora implementato (vedi [docs/ROADMAP.md](../docs/ROADMAP.md) M4). Note per quando si affronta:

## Opzioni valutate

- **WiX Toolset v4/v5** - pieno controllo su registro (associazione `.pdf`, verbo "Apri con"), componenti,
  upgrade in-place. Più verboso da configurare, nessuna UI di aggiornamento automatico integrata.
- **Velopack** - pensato per app .NET, aggiornamenti automatici pronti all'uso, meno controllo fine sul registro
  rispetto a WiX (va comunque bene per la maggior parte dei casi).

## Cosa deve fare l'installer

1. Pubblicare `src/App` come self-contained per il RID target (`win-x64` e/o `win-arm64`):
   ```
   dotnet publish src/App/App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
   ```
2. Registrare l'associazione file `.pdf` e il verbo "Apri con" (chiavi in `HKCU\Software\Classes` per
   un'installazione per-utente senza privilegi elevati, oppure `HKLM\Software\Classes` per tutti gli utenti).
3. Firma del codice dell'eseguibile e del pacchetto installer (certificato EV o OV) prima della distribuzione,
   per evitare gli avvisi SmartScreen di Windows.

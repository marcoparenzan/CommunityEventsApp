# 1nn0va Events

Portale eventi della community 1nn0va, in **.NET 10 / Blazor Server** con database **SQL Server** (schema `community`, database `1nn0va`). Online su Azure (eventi.1nn0va.it) e utilizzabile in locale.

## Componenti

| Progetto | Ruolo | URL locale |
|---|---|---|
| `CommunityApp` | Sito pubblico e area partecipante (iscrizione, agenda, check-in con QR) | https://localhost:5003 (http 5000) |
| `SpeakerApp` | Area speaker e amministrazione (eventi, speaker, utenti, sponsor, import, mail, report, check-in, reset) | https://localhost:5002 (http 5004) |
| `EventsCommon` | Libreria Razor condivisa: modelli, accesso ai dati, email, orari, pagine pubbliche, asset statici, PWA, script SQL | – |

Le due app hanno cookie, manifest PWA e configurazione separati, ma usano lo stesso database.

## Avvio rapido (sviluppo)

1. Creare/aggiornare il database eseguendo **`EventsCommon/Database/Provision.sql`** (SSMS, Azure Data Studio o `sqlcmd -I -i Provision.sql`).
2. Creare la configurazione in `D:\Configurations\EventApp\<CommunityApp|SpeakerApp>\` (vedi [docs/CONFIGURAZIONE.md](docs/CONFIGURAZIONE.md)): `appsettings.json` e `secrets.json`.
3. Avviare con `D:\Configurations\EventApp\run-development.ps1` (apre due finestre) oppure `run-community.ps1` / `run-speaker.ps1`.
4. Accedere con l'email `marco.parenzan@1nn0va.it` (SuperUser creato da Provision.sql): il PIN arriva via email.

## Documentazione

- [docs/FUNZIONALITA.md](docs/FUNZIONALITA.md) – cosa fa l'applicazione, area per area
- [docs/USAGE.md](docs/USAGE.md) – guida operativa per gli organizzatori (prima, durante e dopo l'evento)
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) – struttura, autenticazione, sicurezza, orari, PWA
- [docs/CONFIGURAZIONE.md](docs/CONFIGURAZIONE.md) – tutte le chiavi di configurazione
- [docs/DATABASE.md](docs/DATABASE.md) – tabelle, indici, script unico, reset, backup
- [docs/DEPLOY-AZURE.md](docs/DEPLOY-AZURE.md) – pubblicazione e risoluzione problemi
- [docs/OPEN_POINTS.md](docs/OPEN_POINTS.md) – punti aperti e verifiche da fare
- [CHANGELOG.md](CHANGELOG.md)

## Tecnologie

Blazor Server (InteractiveServer), Microsoft.Data.SqlClient (SQL diretto, senza EF), MailKit (SMTP), ClosedXML (xlsx), QRCoder (QR), HtmlSanitizer, Quill 1.3.7 (editor rich text), Monaco 0.52.2 (editor CSV nell'import), jsQR (scanner, incluso nel repository). Monaco e Quill sono caricati da cdnjs.

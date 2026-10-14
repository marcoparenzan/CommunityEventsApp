# Architettura

## Struttura della soluzione

```
EventApp.sln
├── EventsCommon   libreria Razor (StaticWebAssetBasePath=/)
│   ├── Hosting/    EventAppHost: configurazione, fuso orario, AddEventsCommon
│   ├── Models/ Services/   accesso SQL, email, ItalyTime, SessionLifetime, DatabaseProblem, RichText
│   ├── Components/ pagine e componenti condivisi (evento, speaker, totem)
│   ├── Styles/ wwwroot/   CSS condiviso, js (pwa.js), sw.js, offline.html, jsQR
│   └── Database/Provision.sql   unico script SQL
├── CommunityApp   sito pubblico + area partecipante (cookie __Host-1nn0va-events)
└── SpeakerApp     area speaker + amministrazione (cookie __Host-1nn0va-speaker)
```

Gli asset di `EventsCommon` sono serviti dalla radice di entrambe le app. Le pagine pubbliche condivise usano `ISiteLinks` per generare link coerenti con l'app che le ospita.

## Accesso ai dati
SQL diretto con `Microsoft.Data.SqlClient` tramite `SqlConnectionFactory`; nessun Entity Framework. Il filtro sugli utenti cancellati (`IsDeleted = 0`) è applicato esplicitamente nelle query.

## Autenticazione e autorizzazione

- Login a PIN via email (nessuna password). PIN = HMAC con `Authentication:PinHashKey`; tabelle `EmailPinChallenges` e `SpeakerEmailPinChallenges`; rate limiter `pin` e `event-email-confirmation`.
- Cookie separati per app, `__Host-` (solo HTTPS, path `/`).
- Policy: **Participant** (CommunityApp), **Speaker**, **SuperUser**, **CanCheckIn** (SpeakerApp; i ruoli vengono da `Speakers.IsSuperUser`/`CanCheckIn`).
- Sessione: cookie a scadenza scorrevole di 24 ore (`Authentication:SessionHours`); se il login arriva da una PWA installata (flag `installed`) dura 90 giorni (`InstalledSessionDays`). Ogni `SessionCheckMinutes` (5) `OnValidatePrincipal` riverifica l'account e i permessi (cache in memoria): un utente cancellato o declassato perde l'accesso entro pochi minuti.

## Endpoint principali

CommunityApp: `/api/auth/{request-pin,verify-pin,sign-out}`, `/api/account` (GET, `/profile`, `PUT /notifications`, `DELETE`), `/api/events/{id}/register` (POST/DELETE), `PUT /api/events/{id}/preferences`, `/api/checkin/{eventToken}`, `/api/checkin/confirm`, `POST /api/checkin/events/{id}/self`, `/media/{id}`.

SpeakerApp: `/api/speaker-auth/*`, `/api/speaker/*` (account, sessions, attendees, people, sync, resources), `/api/speaker/events/{id}/slide-template`, `/api/admin/events/{id}/slide-template`, `/admin/reset-database`.

## Servizi di SpeakerApp
EventMailService, EventReportService, EventTemplateService, MaterialsImportService, SessionizeSyncService, SpeakerAuthenticationService, SpeakerImportService, SpeakerManagementService, SpeakerSelfService, SpeakerSessionCheck, SponsorAdminService, UserImportService, UserManagementService, XlsxExporter, CommunityLinks, DatabaseResetService.

## Copertine delle sessioni
`SessionCoverService` (SpeakerApp) disegna la copertina con **SkiaSharp** (le foto degli speaker sono scaricate solo da host HTTPS `sessionize.com`, con timeout e limite di dimensione), la salva in `MediaAssets` e la collega con `EventSessions.CoverAssetID`; l'immagine caricata a mano segue lo stesso percorso (controllo della firma del file). Download: `GET /api/speaker/events/{id}/sessions/{sessionId}/cover` (speaker della sessione o SuperUser). Il font è Segoe UI se presente sul server, altrimenti quello di sistema.

## Logging
Tutti i servizi usano `ILogger`: Information per le operazioni (salvataggi, import, sincronizzazioni, invii mail, login riusciti), Warning per i rifiuti (PIN errato, check-in rifiutato, cancellazioni, reset) ed Error con eccezione per i guasti. Gli indirizzi email non vengono scritti nei log.

## Orari
`ItalyTime` (EventsCommon) legge `Application:TimeZone` (default Europe/Rome) all'avvio; espone `Now`, `Today`, `NowOffset`, `ToItaly()`. All'avvio l'app scrive il fuso in `community.Settings`; le funzioni SQL `community.LocalNow()` (datetimeoffset) e `community.LocalNowDateTime()` (datetime) lo leggono e sono usate nei DEFAULT. Risultato: **tutte le date nel database sono ora locale** (i `datetimeoffset` con l'offset locale; `RegistrationDate` è ora locale senza fuso). Su Azure il server gira in UTC, quindi non si deve mai usare l'ora di sistema.

## Check-in firmato
`Events.CheckInToken` (GUID) viene ruotato a ogni "Genera QR code". Il QR include il token come firma; l'API di auto check-in lo confronta con quello corrente: un poster vecchio non funziona più.

## Import
xlsx/csv → testo CSV → editor Monaco → `UserImportService` / `SpeakerImportService` / `MaterialsImportService` (parse, analisi con anteprima, conferma). Il separatore è rilevato automaticamente.

## PWA
Manifest per app (`CommunityApp/wwwroot/manifest.webmanifest`, `SpeakerApp/wwwroot/manifest.webmanifest`), icone in `wwwroot/pwa/`, service worker `sw.js`, `offline.html` e registrazione in `js/pwa.js` (condivisi in EventsCommon). Due PWA distinte perché i due siti hanno scopo, utenti e cookie diversi.

## Sicurezza (riepilogo)
HTML sanificato con HtmlSanitizer (descrizioni evento); segreti fuori dal repository (secrets.json / App Settings); rate limiting su PIN e conferme; cookie `__Host-`; QR a token ruotabile; reset database protetto da segreto (≥ 16 caratteri) e disponibile solo con `Admin:ResetSecret` configurato.

# Database

SQL Server (LocalDB in sviluppo, Azure SQL online), database `1nn0va`, schema `community`.

## Script unico: `EventsCommon/Database/Provision.sql`

È l'**unico** script SQL. Crea o aggiorna tutto e verifica il risultato; non cancella mai dati; è ripetibile a ogni rilascio.

1. crea lo schema e le tabelle di base;
2. crea `community.Settings` e le funzioni `community.LocalNow()` / `LocalNowDateTime()` (solo se mancanti);
3. crea tabelle, colonne, indici e vincoli mancanti e applica le conversioni una tantum (default a ora locale, offset UTC → locale);
4. garantisce l'utente e lo speaker SuperUser `marco.parenzan@1nn0va.it`;
5. in fondo elenca gli oggetti ancora mancanti (nessuna riga = schema completo; stampa "Verification completed").

Esecuzione: SSMS / Azure Data Studio (contiene `GO`) oppure `sqlcmd -S <server> -d <db> -I -b -i Provision.sql` (`-I` serve per gli indici filtrati). **Ogni modifica allo schema si fa in questo file**, con istruzioni ripetibili (`IF NOT EXISTS`, `COL_LENGTH`).

Prima di eseguirlo su un database con dati reali fare un backup.

## Tabelle (18)

| Tabella | Contenuto / colonne principali |
|---|---|
| `Users` | Partecipanti: ID, FirstName, LastName, EMail (unica), AllowEventsNotifications, RequestCancellation, ToBeConfirmed, IsDeleted, DeletedAt (LastPin legacy) |
| `UserEventRegistration` | Iscrizioni: UserID, EventID, RegistrationDate (ora locale), Status (`Registered`/`AwaitingConfirmation`/`Pending`/`Cancelled`), CheckedInAt, IsEatingPizza, LactoseIntolerant |
| `UserConsentHistory` | Storico consensi (tipo, concesso, versione informativa, origine) |
| `UserLogs` | Log eventi utente (LogTypeId, es. 1001 = consenso) |
| `EmailPinChallenges` | PIN utenti (hash, tentativi, consumo) |
| `EventRegistrationConfirmations` | Token di conferma iscrizione (hash, scadenza) |
| `Events` | ID, Year, Name, Description (rich text), EventDate, Moniker, SessionizeMoniker/JsonUri, IsCurrent, IsPublished, CallForPapers*, CheckInToken, IsCancelled, IsLocked, SlideTemplateAssetID |
| `EventSessions` | Sessioni: titolo, descrizione, orari, Room, IsActive, LastSyncedAt, VideoUrl, PresentationUrl, RepositoryUrl, SpeakerDescription |
| `EventSessionSpeakers` | Legame sessione ↔ speaker dell'evento |
| `EventSpeakers` | Speaker per evento (dati Sessionize, cena, accompagnatori, AlbergoID, SpeakerID) |
| `Speakers` | Anagrafica speaker: EMail, SessionizeName, SessionizeSpeakerID, IsSuperUser, CanCheckIn, Cellulare |
| `SpeakerEmailPinChallenges` | PIN speaker |
| `SpeakerChangeLog` | Storico modifiche speaker |
| `Alberghi` | Elenco alberghi (Nome unico, Url) |
| `Sponsors` | SponsorKey (unica), Name, Url, Tier, MediaAssetID |
| `EventSponsors` | Sponsor per evento con SortOrder |
| `MediaAssets` | File binari (loghi, template PowerPoint) |
| `Settings` | Chiave/valore (es. `TimeZone`), scritto dall'app |

Indici univoci principali: `Users.EMail`; `Events(Year, Moniker)`; `Events.CheckInToken`; `Events.IsCurrent` (filtrato: un solo evento corrente); `Speakers.SessionizeSpeakerID` (filtrato); `UserEventRegistration(UserID, EventID)` (filtrato); `Sponsors.SponsorKey`; `EventSessions(EventID, SessionizeSessionID)`; `Alberghi.Nome`.

Chiavi esterne principali: iscrizioni → Users/Events; sessioni → Events; EventSpeakers → Events/Speakers/Alberghi; EventSessionSpeakers → EventSessions/EventSpeakers; EventSponsors → Events/Sponsors; Sponsors → MediaAssets; Events.SlideTemplateAssetID → MediaAssets; challenge e log → Users/Speakers.

## Orari nel database

Le date create dal database (DEFAULT) usano `community.LocalNow()`, che legge il fuso da `community.Settings`. I `datetimeoffset` sono salvati con l'offset locale. `RegistrationDate` è `datetime`: ora locale senza fuso. In fondo a `Provision.sql` c'è un blocco **commentato e opzionale** per convertire da UTC a locale le `RegistrationDate` scritte da un server in UTC (impostare `@since`, decommentare, eseguire una sola volta).

## Reset (solo sviluppo)

`GET https://<speakerapp>/admin/reset-database?secret=<Admin:ResetSecret>` svuota tutte le tabelle di dati (iscrizioni, eventi, sessioni, speaker per evento, sponsor, media, log, PIN) e lascia solo Marco Parenzan come utente e speaker SuperUser. Lo schema non viene toccato. **Non usarlo in produzione con dati reali**; lasciare `Admin:ResetSecret` vuoto per disattivarlo.

## Backup

Prima di ogni aggiornamento di schema o import massivo: `BACKUP DATABASE [1nn0va] TO DISK = N'...' WITH COPY_ONLY, INIT` (locale) o export bacpac / backup automatici di Azure SQL.

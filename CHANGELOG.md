# Changelog

## 2026-10-14

- Speaker per evento: nota "non vale come prenotazione" sull'albergo, voce **Altro** con campo "Se vuoi specifica", **taglia della maglia** (taglie europee, N/A) attiva solo se l'evento la raccoglie (flag, predefinito no), **link del gruppo WhatsApp speaker** per evento.
- **Copertine delle sessioni** generate con SkiaSharp (stile allegato) e collegate alla sessione, oppure caricate a mano.
- Copertine gestibili dai SuperUser per qualsiasi sessione (Eventi → Link sessioni); nuova tab report **Maglie speaker**; il calcolatore pizze mostra i numeri dell'evento.
- Logging esteso a servizi ed endpoint principali.
- Provision.sql: nuove colonne `EventSpeakers.AlbergoNote/TShirtSize`, `Events.SpeakerWhatsAppUrl/CollectsTShirtSizes`, `EventSessions.CoverAssetID`, albergo "Altro" (da eseguire anche sul database online).

## 2026-10-10

- Rimosso il progetto database `EventAppDb` (SSDT, obsoleto) dalla soluzione.
- Database: **un solo script**, `EventsCommon/Database/Provision.sql` (crea/aggiorna lo schema, orari locali, seed SuperUser, verifica finale). Rimossi Bootstrap, GlobalSetup, CheckSchema e FixRegistrationDatesFromUtc.
- Fuso orario configurabile (`Application:TimeZone`, default Europe/Rome); tutte le date create sono ora locale, anche nel database (`community.LocalNow()`); date esistenti corrette.
- Check-in con QR firmato a token ruotabile (generato solo da area speaker), scanner riscritto, redirect a `/account?checkin=ok`, ora del check-in locale.
- PWA installabili per CommunityApp e SpeakerApp; sessione 24 ore scorrevole (90 giorni da PWA installata) con riverifica periodica dell'account.
- Cancellazione account con eliminazione/anonimizzazione, flag notifiche e link di disiscrizione nelle email.
- Import unico (partecipanti, speaker, materiali) da xlsx/csv con editor Monaco; gestione utenti, sponsor per evento, track, template PowerPoint per evento; self-service speaker su `/my-events`.
- Endpoint di reset del database protetto da `Admin:ResetSecret`; configurazione da variabili d'ambiente su Azure.
- Documentazione completa riscritta (README e cartella `docs/`).


## 2026-10-07 (aggiornamento)

- Blocco delle modifiche per evento (`IsLocked`): pulsante "Blocca/Sblocca modifiche" in Gestione eventi (conferma al blocco); gli utenti non possono più iscriversi, annullare né cambiare le preferenze, con controllo anche sul server; nuova richiesta `PUT /api/events/{id}/preferences`.
- Area utente: le pagine evento attuale, prossimi eventi ed eventi passati hanno la stessa struttura (righe evento con agenda e materiali); iscrizione, modifica delle preferenze e annullamento con finestre di conferma per gli eventi non passati; eventi passati in sola lettura.
- Pagine pubbliche non autenticate: stessa barra fissa e stessi elenchi dell'area utente (Prossimi eventi, Eventi passati, Accedi), senza fascia blu iniziale, con un testo introduttivo; chi è autenticato viene portato all'area personale.
- Logo più grande e senza la scritta "Eventi" nella barra superiore di entrambe le app.

## 2026-10-07

### Struttura

- Divisione in tre progetti: `EventsCommon` (libreria Razor: modelli, accesso ai dati, email, script SQL, asset statici, pagine pubbliche condivise), `CommunityApp` (sito pubblico e area utente), `SpeakerApp` (area speaker e amministrazione, con navigazione dalla radice). Soluzione `EventApp.sln`.
- Cookie di accesso separati; configurazione per applicazione in `D:\Configurations\EventApp\CommunityApp` e `...\SpeakerApp` (copie dei file originali, che restano), con gli script `run-community.ps1`, `run-speaker.ps1` e `run-development.ps1`.
- Pagine pubbliche condivise (evento, relatori, totem, scheda relatore) servite da entrambe le app tramite `ISiteLinks`; in SpeakerApp usano la barra speaker e nascondono la registrazione. I link verso il sito pubblico usano `Application:PublicBaseUrl`.
- Asset statici condivisi serviti dalla radice di entrambe le app (`StaticWebAssetBasePath=/`).
- Documentazione aggiornata: nuovo [docs/FUNZIONALITA.md](docs/FUNZIONALITA.md).

### Area speaker (SpeakerApp)

- Barra fissa: Il mio profilo, I tuoi eventi, Le tue sessioni, Check-in evento e, per i superspeaker, Gestione eventi, Gestisci Speakers, Mail, Reports; voce attiva evidenziata; nome e cognome nel menu utente (altrimenti l'email) con la sola voce Esci.
- Il mio profilo mostra la scheda pubblica; eventi e sessioni in elenchi paginati a 5 righe, in ordine decrescente.
- Check-in evento: solo evento corrente; tre tab (Elenco partecipanti, Cerca in anagrafica, Aggiungi partecipante); iscrizione di persone già in anagrafica; aggiunta di partecipanti già confermata e senza email; pulsante "Invia email di conferma"; check-in consentito solo per l'evento corrente nel giorno dell'evento (anche sul server).
- Gestione eventi: tab per anno; conferme per sincronizzazione, cancellazione, pubblicazione e evento corrente; modulo evento a due colonne senza "Template"; link sessioni raggruppati per aula e ordinati per orario; calcolatore pizze in una finestra; link "Apri agenda" (nuova scheda) e "Versione totem".
- Gestisci Speakers: tab per iniziale del cognome, ordinamento per ultima partecipazione, finestra di modifica (nome, cognome, email, nome Sessionize).
- Mail: invio ai partecipanti di un evento per gruppo (registrati, non confermati, confermati, intolleranti, check-in, speaker), segnaposto `{nome}` ed `{evento}`, conferma, limite 500.
- Reports: riepilogo e tabelle (utenti registrati, non confermati, confermati, intolleranti, sessioni, speaker) in griglia ordinabile e paginata, esportazione XLSX e collegamento alla pagina Mail.

### Area utente (CommunityApp)

- Stesso stile dell'area speaker, senza titoli superflui; barra con evento attuale (se iscritto), prossimi eventi, eventi passati e Scansiona QR; agenda per aula con relatori cliccabili e link a video, slide e codice anche per gli eventi passati; nessun riferimento agli speaker; nome e cognome nel menu utente.

### Sincronizzazione Sessionize

- Collegamento degli speaker agli account per nome Sessionize, poi per nome e cognome; creazione dell'account con email `<notset>` se manca; moniker e URL Sessionize ricavati anche da altri eventi dello stesso relatore; la scheda pubblica raccoglie le sessioni di tutti gli eventi.

### Pagine pubbliche

- Intestazioni compatte e fluide per il mobile; menu in alto cliccabile (il velo sopra l'intestazione intercettava i clic); aula non più duplicata; rimossa "La giornata" quando manca la descrizione; rimosso il piè di pagina blu dalle pagine condivise; "Versione totem" spostata in Gestione eventi.

### Database

- Le tre migrazioni (cancellazione evento, URL Sessionize e moniker del relatore) sono state integrate in `GlobalSetup.sql` e la cartella `Migrations` è stata eliminata.
- Eliminata la colonna `community.Events.TemplateKey` (dal codice e, tramite `GlobalSetup.sql`, dallo schema).

## 2026-10-07

- Suddivisa l'applicazione in tre progetti: `EventsCommon` (libreria con modelli, accesso ai dati, email e asset statici), `CommunityApp` (sito pubblico e area utente) e `SpeakerApp` (area speaker e amministrazione, con navigazione dalla radice).
- Cookie di accesso separati per le due applicazioni; configurazione per applicazione in `D:\Configurations\EventApp\CommunityApp` e `...\SpeakerApp`.

## 2026-09-29

- Avviata la conversione da pagina singolo evento 1nn0vAI 2026 a catalogo multi-evento.
- Aggiunte route canoniche per anno e moniker, root basata sull'evento corrente e selezione corrente in admin Development.
- Spostato il record evento fuori dal progetto sotto `D:\\configurations\\EventsApp`.
- Introdotti record sponsor annuali referenziati per evento, campi Sessionize, session resources, deleghe check-in, date CfP e profili speaker/partecipante su filesystem.
- Aggiunta la pagina evento con layout responsive e mantenuta la pagina originale all'indirizzo `/legacy2026`.
- Pubblicato l'accesso al totem statico 2026 tramite la route evento.
- Documentati i flussi previsti e le funzionalità ancora da completare.

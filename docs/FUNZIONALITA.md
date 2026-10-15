# Funzionalità

## 1. Sito pubblico (senza accesso)

| Pagina | Contenuto |
|---|---|
| `/` | Prossimi eventi (il più vicino in evidenza), pulsante Accedi/registrati in alto a destra. Se l'utente è già autenticato viene portato a `/account`. |
| `/past` | Eventi passati |
| `/events/{anno}/{moniker}` | Scheda evento: descrizione (testo formattato), agenda, materiali, sponsor; sotto-pagine `/speakers` e `/totem` |
| `/speakers/{chiave}` | Scheda pubblica di uno speaker |
| `/totem` | Vista a schermo intero per il totem all'evento |
| `/legacy2026` | Pagina storica |
| `/media/{id}` | Immagini/file caricati (loghi sponsor, ecc.) |

Gli eventi non pubblicati (`IsPublished = 0`) non compaiono. Un evento annullato è segnalato come tale.

## 2. Accesso con PIN

Non esistono password. L'utente inserisce l'email, riceve un **PIN** (e un link che lo compila) e lo digita. Il PIN è un HMAC (chiave `Authentication:PinHashKey`), resta valido finché non se ne richiede un altro, con limite di tentativi e rate limiter. Se l'email non esiste l'utente viene registrato (nome, cognome, consenso notifiche). Le due app hanno accessi indipendenti: il partecipante usa CommunityApp; speaker e organizzatori usano SpeakerApp (email presente in `Speakers`).

## 3. Area partecipante (CommunityApp)

- `/account` – evento corrente con iscrizione, agenda e materiali; `/account/upcoming` prossimi eventi; `/account/past` eventi passati (stessa struttura a righe evento).
- **Iscrizione**: dopo la richiesta arriva un'email di conferma (stato `AwaitingConfirmation`, poi `Registered` alla conferma su `/account/confirm`). Preferenze: pizza (`IsEatingPizza`) e intolleranza al lattosio. Si può annullare. Se l'evento è **bloccato** (`IsLocked`) l'utente non può più iscriversi/modificare.
- `/account/profile` – nome, cognome, **flag "ricevi comunicazioni da 1nn0va"** (`AllowEventsNotifications`), **cancellazione dell'account**.
- **Check-in con QR** (sezione 9): `/checkin/scan` scanner, `/checkin/event/{id}` pagina di conferma, `/checkin/{token}` e `/checkin/confirm/{token}` per i link personali. A check-in riuscito si torna a `/account?checkin=ok`.
- Ogni email di notifica ha in fondo il link per non ricevere più comunicazioni.

### Cancellazione account

Il partecipante la chiede dal profilo. Se **non ha mai fatto check-in** viene eliminato; se ne ha fatti viene **anonimizzato** (`IsDeleted = 1`, email `deleted-<id>@deleted.invalid`, nomi nulli, iscrizioni future annullate) per conservare le statistiche. Gli utenti cancellati non compaiono in ricerche, mail e report.

## 4. Area speaker (SpeakerApp)

Barra fissa: Il mio profilo, I tuoi eventi, Le tue sessioni, Check-in evento e, per i SuperUser, Eventi, Speaker, Utenti, Sponsor, Track, Mail, Report, Import.

- `/` (profilo) – scheda pubblica dello speaker con modifica dei propri dati (anche cellulare, cena, albergo).
- `/my-events` – eventi a cui lo speaker partecipa; per ciascuno può modificare **le proprie sessioni** (descrizione, link slide `PresentationUrl`, repository `RepositoryUrl`, video `VideoUrl`) e scaricare il **template PowerPoint** dell'evento.
- **Cena, albergo e maglia** (per evento): partecipazione alla cena e accompagnatori; albergo scelto da elenco. L'albergo è **solo un'indicazione per l'organizzazione e non vale come prenotazione** (che resta a carico dello speaker). La voce **Altro** abilita il campo libero "Se vuoi specifica". Se l'evento raccoglie le taglie, lo speaker indica la **taglia europea** (XS, S, M, L, XL, XXL, 3XL, N/A).
- **Gruppo WhatsApp speaker**: se l'evento ha il link, compare un pulsante per entrare nel gruppo.
- **Copertine delle sessioni** (1280x720, stile 1nn0va: data ed evento in alto a sinistra, logo, titolo, foto tonda e nome degli speaker, barra blu con logo e sponsor): su ogni sessione il pulsante **Genera e allega** crea l'immagine con SkiaSharp e la collega alla sessione; in alternativa si **allega a mano** un'immagine PNG/JPEG/WebP (max 5 MB). Si può scaricare, rigenerare/sostituire o rimuovere. Le copertine non sono pubbliche: le vedono gli speaker della sessione e i SuperUser.
- `/my-sessions` – elenco delle sue sessioni.
- Le modifiche ai dati dello speaker sono tracciate in `SpeakerChangeLog` (campo, vecchio/nuovo valore, origine, autore).

## 5. Amministrazione (solo SuperUser)

### Eventi (`/events`)
Schede per anno. Per evento: nome, anno, moniker, data, **descrizione rich text** (Quill, sanificata), moniker/JSON Sessionize, call for papers (apertura, chiusura, URL), pubblicato, **corrente** (uno solo), annullato, **bloccato**. Azioni con conferma: sincronizza da Sessionize, elimina, pubblica, imposta corrente, blocca/sblocca. Campi **Link gruppo WhatsApp speaker** e **Raccogli la taglia della maglia degli speaker** (predefinito: no). Template PowerPoint per evento (carica/sostituisci/scarica), **copertine delle sessioni** di qualsiasi speaker (pannello "Link sessioni": genera e allega, allega a mano, scarica, rimuovi) e QR di check-in (sezione 9). I campi tagline, sede, immagine hero, URL/etichetta registrazione esterna non esistono più.

### Speaker (`/speakers`)
Tab per iniziale del cognome, ordinamento per ultima partecipazione, finestra di modifica: nome, cognome, email, nome Sessionize, ID Sessionize, cellulare, ruoli **SuperUser** e **CanCheckIn**. Per evento: cena speaker, accompagnatori, **albergo** (tabella `Alberghi`).

### Utenti (`/users`)
Ricerca e modifica dell'anagrafica e delle **iscrizioni per evento** (stato, check-in, pizza, lattosio), aggiunta di utenti, cancellazione/anonimizzazione, **invio dell'email di accesso**. Nei risultati si vedono gli eventi a cui l'utente ha partecipato.

### Sponsor (`/sponsors`)
Anagrafica (chiave, nome, URL, livello/tier, logo in `MediaAssets`) e assegnazione per evento/anno con ordinamento; pulsante per copiare gli sponsor da un altro evento.

### Track (`/tracks`)
Gestione delle track/aule (dato `Room` delle sessioni; "aula" e "track" sono sinonimi).

### Mail (`/mail`)
Invio ai partecipanti di un evento per gruppo: **Utenti registrati** (tutti i non annullati), **non confermati**, **confermati**, **intolleranti al lattosio**, **con check-in**, **confermati senza check-in**, più gli speaker. Segnaposto `{nome}` ed `{evento}`; massimo **500 destinatari** per invio, senza coda né retry. Il footer con il link di disiscrizione è aggiunto automaticamente.

### Report (`/reports`)
Riepilogo e tabelle (registrati, non confermati, confermati, intolleranti, check-in, sessioni, speaker) in griglia ordinabile e paginata, con **esportazione XLSX**. Per gli eventi che raccolgono le taglie c'è anche la tab **Maglie speaker** (quanti per taglia e chi, più chi non l'ha indicata).

### Import (`/import`)
Un'unica pagina per: **partecipanti** (anche con ordine cognome, nome, email), **speaker** (email e ID Sessionize, con traccia dei cambi email), **materiali** (slide e repository per speaker/evento). Si carica un file **xlsx o csv** che diventa testo CSV nell'**editor Monaco** con colonne colorate (si può anche incollare direttamente); il separatore `,` o `;` è riconosciuto; le righe vuote sono saltate. Passi: carica → analizza (anteprima nuovi/aggiornati/scartati) → conferma. Dove serve si sceglie l'evento.

### Check-in (`/checkin`)
Solo evento corrente. Tab: elenco partecipanti (ora locale del check-in), cerca in anagrafica, aggiungi partecipante. Check-in manuale per chi ha `CanCheckIn` o è SuperUser.

## 6. Sincronizzazione Sessionize
Dal JSON pubblico di ogni evento (`SessionizeJsonUri`) si importano sessioni, speaker e collegamenti; le sessioni scomparse vengono disattivate (`IsActive = 0`). Il collegamento a `Speakers` avviene per ID Sessionize o per `SessionizeName`. Solo SuperUser.

## 7. Orari
Gli orari sono nel fuso configurabile (`Application:TimeZone`, predefinito Europe/Rome): le date create dall'applicazione e dal database sono già ora locale. Vedi [ARCHITECTURE.md](ARCHITECTURE.md).

## 8. PWA
Entrambe le app sono installabili (manifest, icone, service worker, pagina offline). Da PWA installata la sessione dura 90 giorni (24 ore da browser).

## 9. Check-in con QR firmato

1. L'organizzatore apre `/events/{id}/checkin-poster` e preme **Genera QR code**: viene generato un nuovo token (`Events.CheckInToken`) che invalida i QR stampati prima.
2. Il QR contiene `https://<host>/checkin/event/{id}?s={token}`. Non è disponibile lato utente: si stampa solo dall'area speaker, quindi il poster stampato è sempre quello valido.
3. Il partecipante iscritto e confermato lo scansiona con la fotocamera del telefono o con `/checkin/scan` (BarcodeDetector con ripiego jsQR). L'API `POST /api/checkin/events/{id}/self` verifica la firma; se valida registra `CheckedInAt` (ora locale) e riporta a `/account?checkin=ok`.
4. In alternativa lo staff fa il check-in manuale dall'elenco.

## 10. Reset del database di sviluppo
`GET /admin/reset-database?secret=<Admin:ResetSecret>` su SpeakerApp svuota i dati lasciando solo Marco Parenzan (utente + speaker SuperUser). Non tocca lo schema. Vedi [DATABASE.md](DATABASE.md).

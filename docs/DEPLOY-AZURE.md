# Pubblicazione su Azure

Le due app sono due Web App separate (es. `eventi.1nn0va.it` per CommunityApp e un host dedicato per SpeakerApp) che condividono lo stesso Azure SQL.

## Ordine corretto

1. **Database**: eseguire `Provision.sql` sul database Azure (Azure Data Studio / SSMS). L'ultima verifica deve stampare "Verification completed". Farlo **prima** di pubblicare una nuova versione.
2. **App Settings** di ciascuna Web App (Configuration → Application settings, con `__` per i livelli):
   - `ConnectionStrings__Events`
   - `Application__PublicBaseUrl` (https)
   - `Application__TimeZone` (facoltativa)
   - `Email__Smtp__SMTPHostName`, `__SMTPPort`, `__Username`, `__Password`, `__FromAddress`
   - `Authentication__PinHashKey`
   - `Admin__ResetSecret` (solo SpeakerApp, facoltativa)
3. **Pubblicare** CommunityApp e SpeakerApp (Visual Studio/`dotnet publish`). Non esiste nessun `appsettings.json` obbligatorio: se la cartella `D:\Configurations\EventApp` manca l'app usa le sole variabili d'ambiente.
4. **Firewall SQL**: consentire l'accesso ai servizi Azure (o gli IP in uscita delle Web App).
5. Verificare con un accesso reale: PIN via email, iscrizione, check-in.

## Note

- Il server Azure gira in **UTC**: l'app usa comunque il fuso configurato (`Application:TimeZone`).
- `web.config` imposta lo stdout log e il passthrough degli errori IIS; abilitare il Log stream per leggere gli errori d'avvio.
- Dominio e HTTPS: i cookie `__Host-` richiedono HTTPS.

## Problemi noti e messaggi

| Sintomo | Causa / rimedio |
|---|---|
| HTTP 500.30 all'avvio | Configurazione mancante o non valida (es. stringa di connessione). Controllare App Settings e Log stream |
| 500.0 senza dettagli | Errore nascosto da IIS: guardare il log stdout |
| Pagina con messaggio "database vuoto/non aggiornato" o `Invalid object name 'community.…'` | Schema mancante o vecchio: eseguire `Provision.sql` |
| Msg 208 `community.Settings` eseguendo script parziali | Eseguire sempre l'intero `Provision.sql`, non frammenti |
| `Msg 1934` (SET options) in sqlcmd | Usare `-I` |
| Orari sbagliati (GMT) | Eseguito il Provision.sql e riavviato l'app? Il fuso è in `Settings`; vedere il blocco opzionale per le vecchie `RegistrationDate` |
| Nessuna email | Controllare `Email__Smtp__*` e che il mittente sia autorizzato |
| PIN non accettato dopo il deploy | `Authentication__PinHashKey` cambiata: richiedere un nuovo PIN |

L'app traduce gli errori SQL tipici (schema assente, login fallito, firewall) in messaggi comprensibili (`DatabaseProblem`).

## Chiavi di protezione dati

I cookie di accesso sono cifrati con le Data Protection keys. Su Azure App Service le chiavi sono mantenute nella cartella persistente dell'istanza; con più istanze o slot swap vanno condivise (vedi [OPEN_POINTS.md](OPEN_POINTS.md)).

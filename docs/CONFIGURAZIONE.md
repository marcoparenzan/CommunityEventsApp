# Configurazione

## Dove si legge

`EventAppHost.AddEventAppConfiguration` carica, nell'ordine (l'ultimo vince):

1. `D:\Configurations\EventApp\<CommunityApp|SpeakerApp>\appsettings.json`
2. `appsettings.{Environment}.json`
3. `secrets.json`

La cartella può essere cambiata con la variabile d'ambiente `EVENTAPP_CONFIG_PATH`. **Se la cartella non esiste** (caso Azure) l'app usa solo le variabili d'ambiente / App Settings, con `__` al posto di `:` (es. `Email__Smtp__Password`). All'avvio vengono loggati avvisi per le chiavi mancanti.

## Chiavi

| Chiave | App | Obbligatoria | Descrizione |
|---|---|---|---|
| `ConnectionStrings:Events` | entrambe | sì | Connessione SQL Server (database `1nn0va`) |
| `Application:PublicBaseUrl` | entrambe | sì | URL HTTPS pubblico di CommunityApp, usato in email e QR (es. `https://eventi.1nn0va.it`) |
| `Application:SpeakerBaseUrl` | – | no | Chiave legacy |
| `Application:TimeZone` | entrambe | no | Fuso orario IANA o Windows; default `Europe/Rome` |
| `Email:Smtp:SMTPHostName` / `SMTPPort` / `Username` / `Password` / `FromAddress` | entrambe | sì per le email | Server SMTP e mittente autorizzato |
| `Authentication:PinHashKey` | entrambe | sì | Chiave segreta dell'HMAC dei PIN; cambiarla invalida tutti i PIN |
| `Authentication:SessionHours` | entrambe | no | Durata sessione da browser, default 24 |
| `Authentication:InstalledSessionDays` | entrambe | no | Durata sessione da PWA installata, default 90 |
| `Authentication:SessionCheckMinutes` | entrambe | no | Ogni quanti minuti si riverifica l'account, default 5 (0 = disattivato) |
| `Admin:ResetSecret` | SpeakerApp | no | Abilita `/admin/reset-database`; minimo 16 caratteri. Se assente l'endpoint è disabilitato |

I segreti (`ConnectionStrings`, `Email:Smtp:Password`, `PinHashKey`, `ResetSecret`) vanno in `secrets.json` o negli App Settings, mai nel repository. I file `appsettings.template.json` in ogni app mostrano la forma.

## Esempio (`secrets.json`)

```json
{
  "ConnectionStrings": { "Events": "Server=(localdb)\\MSSQLLocalDB;Database=1nn0va;Trusted_Connection=True;TrustServerCertificate=True" },
  "Email": { "Smtp": { "SMTPHostName": "smtp.example.com", "SMTPPort": 587, "Username": "...", "Password": "...", "FromAddress": "eventi@1nn0va.it" } },
  "Authentication": { "PinHashKey": "stringa-lunga-casuale" },
  "Admin": { "ResetSecret": "almeno-16-caratteri" }
}
```

## Porte e script (sviluppo)

| App | https | http |
|---|---|---|
| CommunityApp | 5003 | 5000 |
| SpeakerApp | 5002 | 5004 |

`run-community.ps1`, `run-speaker.ps1`, `run-development.ps1` (entrambe) si trovano in `D:\Configurations\EventApp\`.

## Fuso orario

`Application:TimeZone` vale per codice e database: all'avvio viene scritto in `community.Settings`. Se lo si cambia, riavviare entrambe le app. Le date già salvate non vengono riconvertite.

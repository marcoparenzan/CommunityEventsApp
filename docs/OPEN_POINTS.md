# Punti aperti

## Da verificare in ambiente reale (non collaudati su browser/telefono/Azure)

- Installazione PWA (Android/iOS/desktop) di entrambe le app e comportamento offline.
- Scanner QR con la fotocamera (BarcodeDetector e ripiego jsQR) su più dispositivi.
- Rendering dell'editor Monaco nell'import e caricamento da cdnjs.
- Flussi completi su Azure: PIN via email, iscrizione/conferma, check-in, invio mail, sincronizzazione Sessionize.
- Eseguire `Provision.sql` sul database Azure prima del prossimo deploy.
- Mail: provare con un gruppo piccolo; limite 500 destinatari per invio, nessuna coda né retry né gestione bounce.
- Report: aprire l'XLSX in Excel e verificare le prestazioni con molti partecipanti (ordinamento e paginazione in memoria).

## Sicurezza e sessioni

- Sessione di 90 giorni da PWA installata: sulla SpeakerApp (ruoli amministrativi) valutare una durata più breve; la riverifica ogni 5 minuti revoca comunque l'accesso agli account rimossi/declassati.
- Data Protection keys su Azure: se si usano più istanze o slot, persisterle in uno storage condiviso, altrimenti i cookie si invalidano.
- PIN riutilizzabile fino a nuova richiesta e senza scadenza: richiede `PinHashKey` stabile e rate limiting; definire una procedura di invalidazione.
- Il reset del database è protetto solo dal segreto: disattivarlo (`Admin:ResetSecret` vuoto) in produzione.

## Prodotto e dati

- GDPR: informativa, retention, export dei dati, gestione richieste degli interessati, versionamento dei consensi (`UserConsentHistory`).
- Blocco evento (`IsLocked`): oggi non vale per la conferma email e per l'aggiunta partecipanti da parte dello staff; decidere se estenderlo o attivarlo in automatico.
- Evento corrente con data passata non compare in Check-in: decidere se deve restare selezionabile.
- Speaker creati dalla sincronizzazione con email `<notset>`: completarli da Speaker; verificare gli omonimi.
- Sponsor: import CSV non realizzato; copia del template PowerPoint tra eventi non realizzata.
- Limiti di dimensione/formato dei file binari in `MediaAssets` e policy di retention.
- Sync Sessionize: frequenza e gestione sessioni rimosse (oggi manuale, solo SuperUser).
- Branding, accessibilità, SEO, analytics.

## Manutenzione del repository

- Le colonne legacy `Users.LastPin` e `Speakers.LastPin` (`nvarchar(8)`) non sono usate dall'autenticazione attuale.
- Il codice `UsersLog` nel reset è un nome storico: la tabella reale è `UserLogs`.

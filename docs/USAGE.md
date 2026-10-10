# Guida operativa per gli organizzatori

Si usa SpeakerApp con un account SuperUser (e `CanCheckIn` per chi gestisce l'ingresso).

## Prima dell'evento

1. **Crea l'evento** (Eventi → nuovo): anno, nome, moniker (usato nell'URL `/events/{anno}/{moniker}`), data, descrizione, moniker e JSON di Sessionize, call for papers.
2. **Sincronizza Sessionize** dall'elenco eventi (conferma richiesta): arrivano sessioni e speaker. Ripeti la sincronizzazione quando l'agenda cambia.
3. **Speaker**: da Speaker verifica email, nome Sessionize, ruoli, cena, albergo. Per associare molte email/ID Sessionize usa Import → speaker.
4. **Partecipanti storici**: Import → partecipanti (CSV/xlsx, cognome, nome, email).
5. **Sponsor**: crea gli sponsor (logo incluso) e assegnali all'evento; usa "copia" per riportare quelli di un anno precedente.
6. **Template PowerPoint**: carica il file nell'evento; gli speaker lo scaricano da I tuoi eventi.
7. **Pubblica** l'evento e, quando è quello attivo, impostalo come **corrente**.
8. **Comunicazioni**: Mail → scegli evento e gruppo, usa `{nome}` ed `{evento}`; prova prima con un gruppo piccolo.

## Giorno dell'evento

1. Apri Eventi → l'evento → **poster check-in** e premi **Genera QR code**; stampa subito la pagina. Ogni nuova generazione invalida i QR precedenti: stampa sempre dopo l'ultima generazione.
2. I partecipanti iscritti e confermati inquadrano il QR (fotocamera o scanner in `/checkin/scan`) e vengono riportati alla loro area con l'esito.
3. Check-in manuale: Check-in evento → elenco partecipanti / cerca in anagrafica / aggiungi partecipante (per chi si presenta senza iscrizione).
4. Mail ai "confermati senza check-in" o al gruppo "intolleranti al lattosio" per la logistica; Report per i numeri (pizza, lattosio, presenze).
5. Facoltativo: **blocca le modifiche** dell'evento per impedire cambi di iscrizione.

## Dopo l'evento

1. Import → materiali: carica slide e repository degli speaker (o gli speaker li inseriscono in I tuoi eventi).
2. Report → esporta XLSX con presenze e partecipanti.
3. Rimuovi la marcatura "corrente" quando l'evento successivo è pronto (l'evento passato compare in `/past`).

## Richieste di cancellazione

Un utente può cancellarsi dal profilo (vedi [FUNZIONALITA.md](FUNZIONALITA.md)). Da Utenti si può anche cancellare/anonimizzare manualmente e inviare nuovamente il link di accesso.

## Manutenzione

- Aggiornamento schema dopo un rilascio: eseguire `Provision.sql` (vedi [DATABASE.md](DATABASE.md)).
- Ambiente di prova da svuotare: endpoint di reset (solo sviluppo).

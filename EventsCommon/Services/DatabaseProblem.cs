using Microsoft.Data.SqlClient;

namespace EventsCommon.Services;

/// <summary>Turns a database failure into a short hint for whoever manages the site (no server names, no secrets).</summary>
public static class DatabaseProblem
{
    public static string Describe(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return sql.Number switch
                {
                    // Invalid object / column name: the schema is missing or older than the application.
                    207 or 208 or 2812 =>
                        "Il database risponde ma non è inizializzato (mancano tabelle o colonne): esegui lo script Provision.sql.",
                    // Login failed, database not reachable with these credentials, Azure SQL firewall / unavailable.
                    18456 or 4060 or 40615 or 40613 or 40532 or 40914 =>
                        "Il database rifiuta l'accesso o non è disponibile: controlla utente, password, nome del database e il firewall di Azure SQL.",
                    // Network level: server not found, timeout.
                    -1 or 2 or 53 or 258 or 10053 or 10054 or 10060 or 11001 =>
                        "Non riesco a raggiungere il server del database: controlla il nome del server nella stringa di connessione e il firewall.",
                    _ => $"Errore del database (codice {sql.Number}): controlla la connessione e che lo script Provision.sql sia stato eseguito."
                };
            }

            if (current is InvalidOperationException && current.Message.Contains("ConnectionStrings", StringComparison.Ordinal))
            {
                return "Manca la stringa di connessione al database: imposta ConnectionStrings__Events nell'App Service.";
            }
        }

        return "Il database non è raggiungibile o non è inizializzato.";
    }
}

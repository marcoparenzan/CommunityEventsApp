using System.Data;
using Microsoft.Data.SqlClient;
using EventsCommon.Models;

namespace SpeakerApp.Services;

public sealed class SpeakerManagementService(SqlConnectionFactory connections)
{
    public async Task<IReadOnlyList<SpeakerPermissionResponse>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT s.ID, s.FirstName, s.LastName, s.EMail, s.IsSuperUser, s.CanCheckIn, s.SessionizeName,
                   last.LastEventDate, s.Cellulare
            FROM community.Speakers s
            OUTER APPLY (
                SELECT MAX(e.EventDate) AS LastEventDate
                FROM community.EventSpeakers es
                INNER JOIN community.Events e ON e.ID = es.EventID
                WHERE es.SpeakerID = s.ID
            ) last
            ORDER BY CASE WHEN last.LastEventDate IS NULL THEN 1 ELSE 0 END,
                     last.LastEventDate DESC, s.LastName, s.FirstName, s.EMail;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var speakers = new List<SpeakerPermissionResponse>();
        while (await reader.ReadAsync(cancellationToken))
        {
            speakers.Add(new SpeakerPermissionResponse(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                reader.IsDBNull(8) ? string.Empty : reader.GetString(8)));
        }

        return speakers;
    }

    public async Task<bool> SetCanCheckInAsync(
        int speakerId,
        bool canCheckIn,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE community.Speakers
            SET CanCheckIn = @canCheckIn
            WHERE ID = @speakerId;
            SELECT @@ROWCOUNT;
            """, connection);
        command.Parameters.Add("@speakerId", SqlDbType.Int).Value = speakerId;
        command.Parameters.Add("@canCheckIn", SqlDbType.Bit).Value = canCheckIn;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public const string UnsetEmail = "<notset>";

    /// <summary>Updates a speaker account; throws <see cref="InvalidOperationException"/> with a user-facing message on invalid input.</summary>
    public async Task<bool> UpdateAsync(
        int speakerId,
        UpdateSpeakerRequest request,
        CancellationToken cancellationToken = default)
    {
        var firstName = (request.FirstName ?? string.Empty).Trim();
        var lastName = (request.LastName ?? string.Empty).Trim();
        var email = (request.Email ?? string.Empty).Trim();
        var sessionizeName = (request.SessionizeName ?? string.Empty).Trim();
        var cellulare = (request.Cellulare ?? string.Empty).Trim();
        if (firstName.Length == 0 || lastName.Length == 0)
        {
            throw new InvalidOperationException("Nome e cognome sono obbligatori.");
        }

        if (firstName.Length > 64 || lastName.Length > 64 || email.Length > 64 || sessionizeName.Length > 64)
        {
            throw new InvalidOperationException("Nome, cognome, email e nome Sessionize possono avere al massimo 64 caratteri.");
        }

        if (cellulare.Length > 32 || cellulare.Any(c => !(char.IsDigit(c) || " +-().".Contains(c))))
        {
            throw new InvalidOperationException("Il cellulare può contenere solo cifre, spazi e i simboli + - ( ) . (massimo 32 caratteri).");
        }

        if (email.Length == 0)
        {
            email = UnsetEmail;
        }

        if (email != UnsetEmail &&
            !(System.Net.Mail.MailAddress.TryCreate(email, out var address) &&
              string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("L'indirizzo email non è valido (oppure lascia <notset>).");
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using (var duplicates = new SqlCommand("""
            SELECT
                CASE WHEN @email <> N'<notset>' AND EXISTS (
                    SELECT 1 FROM community.Speakers WHERE ID <> @id AND LOWER(LTRIM(RTRIM(EMail))) = LOWER(@email))
                    THEN 1 ELSE 0 END,
                CASE WHEN @sessionizeName <> N'' AND EXISTS (
                    SELECT 1 FROM community.Speakers WHERE ID <> @id AND LTRIM(RTRIM(SessionizeName)) = @sessionizeName)
                    THEN 1 ELSE 0 END;
            """, connection, transaction))
        {
            duplicates.Parameters.Add("@id", SqlDbType.Int).Value = speakerId;
            duplicates.Parameters.Add("@email", SqlDbType.NVarChar, 64).Value = email;
            duplicates.Parameters.Add("@sessionizeName", SqlDbType.NVarChar, 64).Value = sessionizeName;
            await using var reader = await duplicates.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (reader.GetInt32(0) == 1)
            {
                throw new InvalidOperationException("Esiste già un altro speaker con questa email.");
            }

            if (reader.GetInt32(1) == 1)
            {
                throw new InvalidOperationException("Esiste già un altro speaker con questo nome Sessionize.");
            }
        }

        int updated;
        await using (var command = new SqlCommand("""
            UPDATE community.Speakers
            SET FirstName = @firstName, LastName = @lastName, EMail = @email, SessionizeName = @sessionizeName,
                Cellulare = NULLIF(@cellulare, N'')
            WHERE ID = @id;
            SELECT @@ROWCOUNT;
            """, connection, transaction))
        {
            command.Parameters.Add("@id", SqlDbType.Int).Value = speakerId;
            command.Parameters.Add("@firstName", SqlDbType.NVarChar, 64).Value = firstName;
            command.Parameters.Add("@lastName", SqlDbType.NVarChar, 64).Value = lastName;
            command.Parameters.Add("@email", SqlDbType.NVarChar, 64).Value = email;
            command.Parameters.Add("@sessionizeName", SqlDbType.NVarChar, 64).Value = sessionizeName;
            command.Parameters.Add("@cellulare", SqlDbType.NVarChar, 32).Value = cellulare;
            updated = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return updated == 1;
    }
}

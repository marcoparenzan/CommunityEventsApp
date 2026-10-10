using Microsoft.Data.SqlClient;

namespace SpeakerApp.Services;

/// <summary>
/// Clears all application data and keeps only Marco Parenzan (user and SuperUser speaker).
/// The schema is not touched (it is created and updated by EventsCommon/Database/Provision.sql).
/// </summary>
public sealed class DatabaseResetService(SqlConnectionFactory connections)
{
    private const string OwnerEmail = "marco.parenzan@1nn0va.it";

    // Children first, so foreign keys are never violated.
    private static readonly string[] TablesToClear =
    [
        "EventRegistrationConfirmations",
        "UserEventRegistration",
        "UserConsentHistory",
        "EmailPinChallenges",
        "SpeakerEmailPinChallenges",
        "UserLogs",
        "UsersLog",
        "SpeakerChangeLog",
        "EventSessionSpeakers",
        "EventSessions",
        "EventSpeakers",
        "EventSponsors",
        "Events",
        "Sponsors",
        "MediaAssets"
    ];

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var table in TablesToClear)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"IF OBJECT_ID(N'community.{table}', N'U') IS NOT NULL DELETE FROM community.{table};",
                cancellationToken);
        }

        await ExecuteAsync(connection, transaction, """
            DELETE FROM community.Users
            WHERE EMail IS NULL OR LOWER(LTRIM(RTRIM(EMail))) <> @owner;

            ;WITH RankedUsers AS
            (
                SELECT ID, ROW_NUMBER() OVER (ORDER BY ID) AS RowNumber
                FROM community.Users
                WHERE LOWER(LTRIM(RTRIM(EMail))) = @owner
            )
            DELETE FROM RankedUsers WHERE RowNumber > 1;

            DELETE FROM community.Speakers
            WHERE EMail IS NULL OR LOWER(LTRIM(RTRIM(EMail))) <> @owner;

            ;WITH RankedSpeakers AS
            (
                SELECT ID, ROW_NUMBER() OVER (ORDER BY ID) AS RowNumber
                FROM community.Speakers
                WHERE LOWER(LTRIM(RTRIM(EMail))) = @owner
            )
            DELETE FROM RankedSpeakers WHERE RowNumber > 1;

            IF NOT EXISTS (SELECT 1 FROM community.Users WHERE LOWER(LTRIM(RTRIM(EMail))) = @owner)
                INSERT INTO community.Users
                    (FirstName, LastName, EMail, LastPin, AllowEventsNotifications, RequestCancellation, ToBeConfirmed)
                VALUES (N'Marco', N'Parenzan', @owner, NULL, 0, 0, 0);
            ELSE
                UPDATE community.Users
                SET FirstName = N'Marco', LastName = N'Parenzan', LastPin = NULL,
                    AllowEventsNotifications = 0, RequestCancellation = 0, ToBeConfirmed = 0
                WHERE LOWER(LTRIM(RTRIM(EMail))) = @owner;

            UPDATE community.Speakers SET IsSuperUser = 0, CanCheckIn = 0;

            IF NOT EXISTS (SELECT 1 FROM community.Speakers WHERE LOWER(LTRIM(RTRIM(EMail))) = @owner)
                INSERT INTO community.Speakers
                    (FirstName, LastName, EMail, LastPin, SessionizeName, IsSuperUser, CanCheckIn)
                VALUES (N'Marco', N'Parenzan', @owner, N'', N'Marco Parenzan', 1, 1);
            ELSE
                UPDATE community.Speakers
                SET FirstName = N'Marco', LastName = N'Parenzan', LastPin = N'',
                    IsSuperUser = 1, CanCheckIn = 1
                WHERE LOWER(LTRIM(RTRIM(EMail))) = @owner;
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        async Task ExecuteAsync(SqlConnection conn, SqlTransaction tx, string sql, CancellationToken token)
        {
            await using var command = new SqlCommand(sql, conn, tx);
            command.Parameters.AddWithValue("@owner", OwnerEmail);
            await command.ExecuteNonQueryAsync(token);
        }
    }
}

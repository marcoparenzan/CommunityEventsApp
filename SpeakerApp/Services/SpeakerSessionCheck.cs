using System.Data;
using Microsoft.Data.SqlClient;

namespace SpeakerApp.Services;

/// <summary>
/// A long sign-in carries the speaker's permissions in its cookie: this asks the database again, now and then, whether
/// the account still exists with the same permissions, so a revoked or changed account does not stay signed in.
/// </summary>
public sealed class SpeakerSessionCheck(SqlConnectionFactory connections)
{
    public async Task<bool> IsStillValidAsync(int speakerId, bool isSuperUser, bool canCheckIn, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT IsSuperUser, CanCheckIn FROM community.Speakers WHERE ID = @id;", connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = speakerId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
               && reader.GetBoolean(0) == isSuperUser
               && reader.GetBoolean(1) == canCheckIn;
    }
}

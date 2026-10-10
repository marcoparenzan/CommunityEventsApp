using System.Data;
using Microsoft.Data.SqlClient;

namespace EventsCommon.Services;

public sealed record MediaAsset(byte[] Content, string ContentType, string FileName);

public sealed class MediaAssetService(SqlConnectionFactory connections)
{
    public async Task<MediaAsset?> GetPublishedAssetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT TOP (1) m.Content, m.ContentType, m.FileName
            FROM community.MediaAssets m
            WHERE m.ID = @id
              AND (
                  EXISTS (
                      SELECT 1 FROM community.Sponsors s
                      INNER JOIN community.EventSponsors es ON es.SponsorID = s.ID
                      INNER JOIN community.Events e ON e.ID = es.EventID
                      WHERE e.IsPublished = 1 AND s.MediaAssetID = m.ID)
              );
            """, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MediaAsset((byte[])reader[0], reader.GetString(1), reader.GetString(2));
    }
}

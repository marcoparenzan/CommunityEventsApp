using System.Data;
using Microsoft.Data.SqlClient;

namespace SpeakerApp.Services;

public sealed record EventTemplateInfo(int AssetId, string FileName, long SizeBytes, DateTimeOffset UploadedAt);

public sealed record EventTemplateFile(byte[] Content, string ContentType, string FileName);

/// <summary>The PowerPoint template of an event: stored in MediaAssets, never served publicly, downloaded by the event's speakers.</summary>
public sealed class EventTemplateService(ILogger<EventTemplateService> logger, SqlConnectionFactory connections)
{
    public const int MaximumBytes = 25 * 1024 * 1024;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".potx"] = "application/vnd.openxmlformats-officedocument.presentationml.template",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pot"] = "application/vnd.ms-powerpoint"
    };

    public async Task<IReadOnlyDictionary<int, EventTemplateInfo>> GetInfosAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT e.ID, m.ID, m.FileName, DATALENGTH(m.Content), m.CreatedAt
            FROM community.Events e
            INNER JOIN community.MediaAssets m ON m.ID = e.SlideTemplateAssetID;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var infos = new Dictionary<int, EventTemplateInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            infos[reader.GetInt32(0)] = new EventTemplateInfo(reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3), reader.GetDateTimeOffset(4));
        }

        return infos;
    }

    /// <summary>Stores (or replaces) the template of the event. Throws <see cref="InvalidOperationException"/> with a user-facing message.</summary>
    public async Task SaveAsync(int eventId, string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Saving the PowerPoint template of event {EventId} ({FileName}, {Bytes} bytes).", eventId, fileName, content.Length);
        fileName = Path.GetFileName(fileName ?? string.Empty);
        var extension = Path.GetExtension(fileName);
        if (!ContentTypes.TryGetValue(extension, out var contentType))
        {
            throw new InvalidOperationException("Il template deve essere un file PowerPoint (.pptx, .potx, .ppt o .pot).");
        }

        if (content.Length == 0)
        {
            throw new InvalidOperationException("Il file è vuoto.");
        }

        if (content.Length > MaximumBytes)
        {
            throw new InvalidOperationException("Il file deve pesare al massimo 25 MB.");
        }

        // I file moderni sono archivi ZIP ("PK"), quelli storici documenti OLE (D0 CF 11 E0): non ci si fida dell'estensione.
        var isZip = content.Length > 4 && content[0] == 0x50 && content[1] == 0x4B;
        var isOle = content.Length > 4 && content[0] == 0xD0 && content[1] == 0xCF && content[2] == 0x11 && content[3] == 0xE0;
        var modern = extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".potx", StringComparison.OrdinalIgnoreCase);
        if (modern ? !isZip : !isOle)
        {
            throw new InvalidOperationException("Il file non sembra un documento PowerPoint valido.");
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        int? oldAssetId;
        string eventName;
        await using (var read = new SqlCommand(
            "SELECT SlideTemplateAssetID, Name FROM community.Events WITH (UPDLOCK, HOLDLOCK) WHERE ID = @id;", connection, transaction))
        {
            read.Parameters.Add("@id", SqlDbType.Int).Value = eventId;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("L'evento non esiste.");
            }

            oldAssetId = reader.IsDBNull(0) ? null : reader.GetInt32(0);
            eventName = reader.GetString(1);
        }

        int assetId;
        await using (var insert = new SqlCommand("""
            INSERT INTO community.MediaAssets (FileName, ContentType, Content, AltText)
            VALUES (@fileName, @contentType, @content, @alt);
            SELECT CONVERT(int, SCOPE_IDENTITY());
            """, connection, transaction))
        {
            insert.Parameters.Add("@fileName", SqlDbType.NVarChar, 255).Value = fileName.Length > 255 ? fileName[^255..] : fileName;
            insert.Parameters.Add("@contentType", SqlDbType.NVarChar, 100).Value = contentType;
            insert.Parameters.Add("@content", SqlDbType.VarBinary, -1).Value = content;
            insert.Parameters.Add("@alt", SqlDbType.NVarChar, 256).Value = $"Template PowerPoint {eventName}"[..Math.Min(256, $"Template PowerPoint {eventName}".Length)];
            assetId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
        }

        await using (var update = new SqlCommand("UPDATE community.Events SET SlideTemplateAssetID = @asset WHERE ID = @id;", connection, transaction))
        {
            update.Parameters.Add("@asset", SqlDbType.Int).Value = assetId;
            update.Parameters.Add("@id", SqlDbType.Int).Value = eventId;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (oldAssetId is int old)
        {
            await DeleteOrphanAsync(connection, transaction, old, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> RemoveAsync(int eventId, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Removing the PowerPoint template of event {EventId}.", eventId);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        int? assetId;
        await using (var read = new SqlCommand(
            "SELECT SlideTemplateAssetID FROM community.Events WITH (UPDLOCK, HOLDLOCK) WHERE ID = @id;", connection, transaction))
        {
            read.Parameters.Add("@id", SqlDbType.Int).Value = eventId;
            var value = await read.ExecuteScalarAsync(cancellationToken);
            if (value is null or DBNull)
            {
                return false;
            }

            assetId = Convert.ToInt32(value);
        }

        await using (var update = new SqlCommand("UPDATE community.Events SET SlideTemplateAssetID = NULL WHERE ID = @id;", connection, transaction))
        {
            update.Parameters.Add("@id", SqlDbType.Int).Value = eventId;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await DeleteOrphanAsync(connection, transaction, assetId.Value, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>The file, only for a speaker of that event (or a superuser). Null when there is no template or no access.</summary>
    public async Task<EventTemplateFile?> GetForSpeakerAsync(
        int eventId, int speakerId, bool isSuperUser, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT m.Content, m.ContentType, m.FileName
            FROM community.Events e
            INNER JOIN community.MediaAssets m ON m.ID = e.SlideTemplateAssetID
            WHERE e.ID = @eventId
              AND (@isSuperUser = 1 OR EXISTS (
                    SELECT 1 FROM community.EventSpeakers es WHERE es.EventID = e.ID AND es.SpeakerID = @speakerId));
            """, connection);
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        command.Parameters.Add("@speakerId", SqlDbType.Int).Value = speakerId;
        command.Parameters.Add("@isSuperUser", SqlDbType.Bit).Value = isSuperUser;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new EventTemplateFile((byte[])reader[0], reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static async Task DeleteOrphanAsync(SqlConnection connection, SqlTransaction transaction, int assetId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            DELETE FROM community.MediaAssets
            WHERE ID = @id
              AND NOT EXISTS (SELECT 1 FROM community.Sponsors WHERE MediaAssetID = @id)
              AND NOT EXISTS (SELECT 1 FROM community.Events WHERE SlideTemplateAssetID = @id)
              AND NOT EXISTS (SELECT 1 FROM community.EventSessions WHERE CoverAssetID = @id);
            """, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.Int).Value = assetId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

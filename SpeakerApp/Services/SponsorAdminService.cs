using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SpeakerApp.Services;

public sealed record SponsorEventOption(int Id, int Year, string Name, DateTime Date);

public sealed record SponsorRow(
    int Id, string Name, string Url, string Tier, int? MediaAssetId, string LogoDataUri,
    IReadOnlyList<int> EventIds);

public sealed class SponsorDraft
{
    public int? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Tier { get; set; } = "Partner";
    /// <summary>Events (of the displayed year) the sponsor is linked to.</summary>
    public HashSet<int> EventIds { get; set; } = [];
}

public sealed record SponsorLogo(byte[] Content, string ContentType, string FileName);

/// <summary>Sponsors are shared rows linked to events: "per year" means linked to the events of that year.</summary>
public sealed class SponsorAdminService(SqlConnectionFactory connections)
{
    public const int MaximumLogoBytes = 1024 * 1024;

    public async Task<IReadOnlyList<SponsorEventOption>> GetEventsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT ID, [Year], Name, EventDate FROM community.Events ORDER BY EventDate;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<SponsorEventOption>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new SponsorEventOption(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetDateTime(3)));
        }

        return list;
    }

    public async Task<IReadOnlyList<string>> GetTiersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT DISTINCT Tier FROM community.Sponsors ORDER BY Tier;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tiers = new List<string> { "Platinum", "Gold", "Silver", "Partner", "Media partner" };
        while (await reader.ReadAsync(cancellationToken))
        {
            var tier = reader.GetString(0);
            if (!tiers.Contains(tier, StringComparer.OrdinalIgnoreCase))
            {
                tiers.Add(tier);
            }
        }

        return tiers;
    }

    /// <summary>All sponsors with the events they are linked to (the page filters by year / event).</summary>
    public async Task<IReadOnlyList<SponsorRow>> GetSponsorsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var links = new Dictionary<int, List<int>>();
        await using (var command = new SqlCommand("SELECT SponsorID, EventID FROM community.EventSponsors;", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var sponsorId = reader.GetInt32(0);
                if (!links.TryGetValue(sponsorId, out var list))
                {
                    links[sponsorId] = list = [];
                }

                list.Add(reader.GetInt32(1));
            }
        }

        var sponsors = new List<SponsorRow>();
        await using (var command = new SqlCommand("""
            SELECT s.ID, s.Name, s.Url, s.Tier, s.MediaAssetID, m.ContentType, m.Content
            FROM community.Sponsors s
            LEFT JOIN community.MediaAssets m ON m.ID = s.MediaAssetID
            ORDER BY s.Name;
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt32(0);
                var dataUri = reader.IsDBNull(6)
                    ? string.Empty
                    : $"data:{reader.GetString(5)};base64,{Convert.ToBase64String((byte[])reader[6])}";
                sponsors.Add(new SponsorRow(
                    id, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4), dataUri,
                    links.TryGetValue(id, out var eventIds) ? eventIds : []));
            }
        }

        return sponsors;
    }

    /// <summary>
    /// Creates or updates a sponsor and sets its links for <paramref name="scopeEventIds"/> (the events of the displayed
    /// year): linked to the ones in the draft, unlinked from the others. Links to other years are left untouched.
    /// </summary>
    public async Task SaveAsync(
        SponsorDraft draft, SponsorLogo? logo, IReadOnlyCollection<int> scopeEventIds, CancellationToken cancellationToken = default)
    {
        var name = (draft.Name ?? string.Empty).Trim();
        var url = (draft.Url ?? string.Empty).Trim();
        var tier = (draft.Tier ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > 128)
        {
            throw new InvalidOperationException("Il nome è obbligatorio (massimo 128 caratteri).");
        }

        if (tier.Length == 0 || tier.Length > 50)
        {
            throw new InvalidOperationException("Il livello è obbligatorio (massimo 50 caratteri).");
        }

        if (url.Length > 512 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Il sito web deve essere un URL HTTPS valido (massimo 512 caratteri).");
        }

        if (logo is not null)
        {
            ValidateLogo(logo);
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        int sponsorId;
        int? oldAssetId = null;
        if (draft.Id is int existingId)
        {
            await using var read = new SqlCommand(
                "SELECT MediaAssetID FROM community.Sponsors WITH (UPDLOCK, HOLDLOCK) WHERE ID = @id;", connection, transaction);
            read.Parameters.Add("@id", SqlDbType.Int).Value = existingId;
            await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException("Lo sponsor non esiste più.");
                }

                oldAssetId = reader.IsDBNull(0) ? null : reader.GetInt32(0);
            }

            sponsorId = existingId;
        }
        else
        {
            sponsorId = await InsertSponsorAsync(connection, transaction, name, url, tier, cancellationToken);
        }

        int? assetId = oldAssetId;
        if (logo is not null)
        {
            await using var asset = new SqlCommand("""
                INSERT INTO community.MediaAssets (FileName, ContentType, Content, AltText)
                VALUES (@fileName, @contentType, @content, @alt);
                SELECT CONVERT(int, SCOPE_IDENTITY());
                """, connection, transaction);
            asset.Parameters.Add("@fileName", SqlDbType.NVarChar, 255).Value = logo.FileName.Length > 255 ? logo.FileName[..255] : logo.FileName;
            asset.Parameters.Add("@contentType", SqlDbType.NVarChar, 100).Value = logo.ContentType;
            asset.Parameters.Add("@content", SqlDbType.VarBinary, -1).Value = logo.Content;
            asset.Parameters.Add("@alt", SqlDbType.NVarChar, 256).Value = $"Logo {name}";
            assetId = Convert.ToInt32(await asset.ExecuteScalarAsync(cancellationToken));
        }

        await using (var update = new SqlCommand("""
            UPDATE community.Sponsors SET Name = @name, Url = @url, Tier = @tier, MediaAssetID = @asset WHERE ID = @id;
            """, connection, transaction))
        {
            update.Parameters.Add("@id", SqlDbType.Int).Value = sponsorId;
            update.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = name;
            update.Parameters.Add("@url", SqlDbType.NVarChar, 512).Value = url;
            update.Parameters.Add("@tier", SqlDbType.NVarChar, 50).Value = tier;
            update.Parameters.Add("@asset", SqlDbType.Int).Value = assetId is int a ? a : DBNull.Value;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        // Collegamenti: solo per gli eventi dell'anno mostrato.
        foreach (var eventId in scopeEventIds)
        {
            await using var link = new SqlCommand(draft.EventIds.Contains(eventId)
                ? """
                  IF NOT EXISTS (SELECT 1 FROM community.EventSponsors WHERE EventID = @eventId AND SponsorID = @sponsorId)
                      INSERT INTO community.EventSponsors (EventID, SponsorID, SortOrder) VALUES (@eventId, @sponsorId, 0);
                  """
                : "DELETE FROM community.EventSponsors WHERE EventID = @eventId AND SponsorID = @sponsorId;",
                connection, transaction);
            link.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            link.Parameters.Add("@sponsorId", SqlDbType.Int).Value = sponsorId;
            await link.ExecuteNonQueryAsync(cancellationToken);
        }

        if (logo is not null && oldAssetId is int old)
        {
            await DeleteOrphanAssetAsync(connection, transaction, old, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Unlinks the sponsor from the given events (it stays in the registry and in other events).</summary>
    public async Task RemoveFromEventsAsync(int sponsorId, IReadOnlyCollection<int> eventIds, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        foreach (var eventId in eventIds)
        {
            await using var command = new SqlCommand(
                "DELETE FROM community.EventSponsors WHERE EventID = @eventId AND SponsorID = @sponsorId;", connection);
            command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            command.Parameters.Add("@sponsorId", SqlDbType.Int).Value = sponsorId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>Deletes the sponsor everywhere (all links and its logo).</summary>
    public async Task<bool> DeleteAsync(int sponsorId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        int? assetId;
        await using (var read = new SqlCommand("SELECT MediaAssetID FROM community.Sponsors WITH (UPDLOCK, HOLDLOCK) WHERE ID = @id;", connection, transaction))
        {
            read.Parameters.Add("@id", SqlDbType.Int).Value = sponsorId;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return false;
            }

            assetId = reader.IsDBNull(0) ? null : reader.GetInt32(0);
        }

        await using (var delete = new SqlCommand("""
            DELETE FROM community.EventSponsors WHERE SponsorID = @id;
            DELETE FROM community.Sponsors WHERE ID = @id;
            """, connection, transaction))
        {
            delete.Parameters.Add("@id", SqlDbType.Int).Value = sponsorId;
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        if (assetId is int id)
        {
            await DeleteOrphanAssetAsync(connection, transaction, id, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Links to the target events every sponsor linked to any of the source events (existing links are kept).
    /// Returns the number of links created.
    /// </summary>
    public async Task<int> CopyAsync(
        IReadOnlyCollection<int> sourceEventIds, IReadOnlyCollection<int> targetEventIds, CancellationToken cancellationToken = default)
    {
        if (sourceEventIds.Count == 0 || targetEventIds.Count == 0)
        {
            throw new InvalidOperationException("Scegli gli eventi di origine e di destinazione.");
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var created = 0;
        foreach (var target in targetEventIds)
        {
            await using var command = new SqlCommand($"""
                INSERT INTO community.EventSponsors (EventID, SponsorID, SortOrder)
                SELECT @target, source.SponsorID, MIN(source.SortOrder)
                FROM community.EventSponsors source
                WHERE source.EventID IN ({string.Join(",", sourceEventIds.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)))})
                  AND NOT EXISTS (SELECT 1 FROM community.EventSponsors existing WHERE existing.EventID = @target AND existing.SponsorID = source.SponsorID)
                GROUP BY source.SponsorID;
                SELECT @@ROWCOUNT;
                """, connection, transaction);
            command.Parameters.Add("@target", SqlDbType.Int).Value = target;
            created += Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    private static async Task<int> InsertSponsorAsync(
        SqlConnection connection, SqlTransaction transaction, string name, string url, string tier, CancellationToken cancellationToken)
    {
        var baseKey = Slug(name);
        var key = baseKey;
        for (var suffix = 2; ; suffix++)
        {
            await using var exists = new SqlCommand("SELECT COUNT(*) FROM community.Sponsors WHERE SponsorKey = @key;", connection, transaction);
            exists.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = key;
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                break;
            }

            key = $"{(baseKey.Length > 74 ? baseKey[..74] : baseKey)}-{suffix}";
        }

        await using var insert = new SqlCommand("""
            INSERT INTO community.Sponsors (SponsorKey, Name, Url, Tier) VALUES (@key, @name, @url, @tier);
            SELECT CONVERT(int, SCOPE_IDENTITY());
            """, connection, transaction);
        insert.Parameters.Add("@key", SqlDbType.NVarChar, 80).Value = key;
        insert.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = name;
        insert.Parameters.Add("@url", SqlDbType.NVarChar, 512).Value = url;
        insert.Parameters.Add("@tier", SqlDbType.NVarChar, 50).Value = tier;
        return Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task DeleteOrphanAssetAsync(
        SqlConnection connection, SqlTransaction transaction, int assetId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            DELETE FROM community.MediaAssets
            WHERE ID = @id AND NOT EXISTS (SELECT 1 FROM community.Sponsors WHERE MediaAssetID = @id);
            """, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.Int).Value = assetId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Slug(string name)
    {
        var normalized = name.Normalize(NormalizationForm.FormD);
        var ascii = new string(normalized.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        var slug = Regex.Replace(ascii.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length == 0)
        {
            slug = "sponsor";
        }

        return slug.Length > 74 ? slug[..74] : slug;
    }

    /// <summary>Only raster images: SVG is refused because the logo is served from the site's own address.</summary>
    public static SponsorLogo CreateLogo(byte[] content, string fileName)
    {
        var contentType = Detect(content)
            ?? throw new InvalidOperationException("Il logo deve essere un'immagine PNG, JPEG, WebP o GIF.");
        var logo = new SponsorLogo(content, contentType, Path.GetFileName(fileName));
        ValidateLogo(logo);
        return logo;
    }

    private static void ValidateLogo(SponsorLogo logo)
    {
        if (logo.Content.Length == 0 || logo.Content.Length > MaximumLogoBytes)
        {
            throw new InvalidOperationException("Il logo deve pesare al massimo 1 MB.");
        }

        if (Detect(logo.Content) != logo.ContentType)
        {
            throw new InvalidOperationException("Il logo deve essere un'immagine PNG, JPEG, WebP o GIF.");
        }
    }

    private static string? Detect(byte[] b)
    {
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        if (b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return "image/gif";
        return null;
    }
}

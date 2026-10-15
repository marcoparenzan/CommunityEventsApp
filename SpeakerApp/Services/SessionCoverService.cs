using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.FileProviders;
using SkiaSharp;

namespace SpeakerApp.Services;

public sealed record SessionCoverFile(byte[] Content, string ContentType, string FileName);

/// <summary>
/// Cover image of a session of an event (1280x720): generated with SkiaSharp (date, title, speakers' photos and names,
/// sponsors) or uploaded by hand. It is stored in MediaAssets and linked by EventSessions.CoverAssetID; never public.
/// </summary>
public sealed class SessionCoverService(
    SqlConnectionFactory connections,
    IHttpClientFactory httpClientFactory,
    IWebHostEnvironment environment,
    ILogger<SessionCoverService> logger)
{
    public const int MaximumUploadBytes = 5 * 1024 * 1024;
    private const int Width = 1280;
    private const int Height = 720;
    private const int BarTop = 660;
    private const long MaximumPhotoBytes = 5 * 1024 * 1024;

    private static readonly SKColor Blue = new(0x2B, 0x7B, 0xB9);
    private static readonly SKColor LightBlue = new(0xC9, 0xDF, 0xF0);

    private sealed record Speaker(string Name, string PhotoUrl);

    private sealed record CoverData(
        string EventName, DateTime EventDate, string Title, IReadOnlyList<Speaker> Speakers, IReadOnlyList<int> SponsorAssetIds);

    /// <summary>Generates the cover of the session and attaches it. Throws <see cref="InvalidOperationException"/> with a user-facing message.</summary>
    public async Task GenerateAsync(
        int speakerId, bool isSuperUser, int eventId, string sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureAccessAsync(speakerId, isSuperUser, eventId, sessionId, cancellationToken);
        var data = await LoadAsync(eventId, sessionId, cancellationToken)
                   ?? throw new InvalidOperationException("La sessione non esiste.");

        var photos = new List<SKBitmap?>();
        var logos = new List<SKBitmap>();
        try
        {
            foreach (var speaker in data.Speakers)
            {
                photos.Add(await DownloadPhotoAsync(speaker.PhotoUrl, cancellationToken));
            }

            foreach (var assetId in data.SponsorAssetIds)
            {
                var content = await ReadAssetAsync(assetId, cancellationToken);
                var bitmap = content is null ? null : SKBitmap.Decode(content);
                if (bitmap is not null)
                {
                    logos.Add(bitmap);
                }
            }

            using var image = Render(data, photos, logos);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            await StoreAsync(eventId, sessionId, $"copertina-{eventId}-{sessionId}.png", "image/png", encoded.ToArray(), cancellationToken);
            logger.LogInformation("Cover generated for session {SessionId} of event {EventId} by speaker {SpeakerId}.", sessionId, eventId, speakerId);
        }
        finally
        {
            foreach (var photo in photos)
            {
                photo?.Dispose();
            }

            foreach (var logo in logos)
            {
                logo.Dispose();
            }
        }
    }

    /// <summary>Attaches an image uploaded by hand (PNG, JPEG or WebP).</summary>
    public async Task UploadAsync(
        int speakerId, bool isSuperUser, int eventId, string sessionId, string fileName, byte[] content,
        CancellationToken cancellationToken = default)
    {
        await EnsureAccessAsync(speakerId, isSuperUser, eventId, sessionId, cancellationToken);
        if (content.Length == 0 || content.Length > MaximumUploadBytes)
        {
            throw new InvalidOperationException("L'immagine deve pesare al massimo 5 MB.");
        }

        string contentType, extension;
        if (content.Length > 8 && content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47)
        {
            (contentType, extension) = ("image/png", ".png");
        }
        else if (content.Length > 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            (contentType, extension) = ("image/jpeg", ".jpg");
        }
        else if (content.Length > 12 && content[0] == 'R' && content[1] == 'I' && content[2] == 'F' && content[3] == 'F' && content[8] == 'W')
        {
            (contentType, extension) = ("image/webp", ".webp");
        }
        else
        {
            throw new InvalidOperationException("Il file deve essere un'immagine PNG, JPEG o WebP.");
        }

        await StoreAsync(eventId, sessionId, $"copertina-{eventId}-{sessionId}{extension}", contentType, content, cancellationToken);
        logger.LogInformation("Cover uploaded for session {SessionId} of event {EventId} by speaker {SpeakerId} ({Bytes} bytes, {FileName}).",
            sessionId, eventId, speakerId, content.Length, Path.GetFileName(fileName));
    }

    public async Task<bool> RemoveAsync(
        int speakerId, bool isSuperUser, int eventId, string sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureAccessAsync(speakerId, isSuperUser, eventId, sessionId, cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        int? assetId;
        await using (var read = new SqlCommand("""
            SELECT CoverAssetID FROM community.EventSessions WITH (UPDLOCK, HOLDLOCK)
            WHERE EventID = @eventId AND SessionizeSessionID = @sessionId;
            """, connection, transaction))
        {
            AddKey(read, eventId, sessionId);
            var value = await read.ExecuteScalarAsync(cancellationToken);
            if (value is null or DBNull)
            {
                return false;
            }

            assetId = Convert.ToInt32(value);
        }

        await using (var update = new SqlCommand(
            "UPDATE community.EventSessions SET CoverAssetID = NULL WHERE EventID = @eventId AND SessionizeSessionID = @sessionId;",
            connection, transaction))
        {
            AddKey(update, eventId, sessionId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await DeleteOrphanAsync(connection, transaction, assetId.Value, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Cover removed from session {SessionId} of event {EventId}.", sessionId, eventId);
        return true;
    }

    public async Task<bool> HasCoverAsync(int eventId, string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM community.EventSessions WHERE EventID = @eventId AND SessionizeSessionID = @sessionId AND CoverAssetID IS NOT NULL;",
            connection);
        AddKey(command, eventId, sessionId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    public async Task<SessionCoverFile?> GetAsync(
        int speakerId, bool isSuperUser, int eventId, string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT m.Content, m.ContentType, m.FileName
            FROM community.EventSessions s
            INNER JOIN community.MediaAssets m ON m.ID = s.CoverAssetID
            WHERE s.EventID = @eventId AND s.SessionizeSessionID = @sessionId
              AND (@isSuperUser = 1 OR EXISTS (
                    SELECT 1
                    FROM community.EventSessionSpeakers ss
                    INNER JOIN community.EventSpeakers es ON es.ID = ss.EventSpeakerID AND es.EventID = ss.EventID
                    WHERE ss.EventID = s.EventID AND ss.SessionizeSessionID = s.SessionizeSessionID AND es.SpeakerID = @speakerId));
            """, connection);
        AddKey(command, eventId, sessionId);
        command.Parameters.Add("@speakerId", SqlDbType.Int).Value = speakerId;
        command.Parameters.Add("@isSuperUser", SqlDbType.Bit).Value = isSuperUser;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SessionCoverFile((byte[])reader[0], reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static void AddKey(SqlCommand command, int eventId, string sessionId)
    {
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        command.Parameters.Add("@sessionId", SqlDbType.NVarChar, 80).Value = sessionId.Trim();
    }

    private async Task EnsureAccessAsync(int speakerId, bool isSuperUser, int eventId, string sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT COUNT(*)
            FROM community.EventSessions s
            WHERE s.EventID = @eventId AND s.SessionizeSessionID = @sessionId AND s.IsActive = 1
              AND (@isSuperUser = 1 OR EXISTS (
                    SELECT 1
                    FROM community.EventSessionSpeakers ss
                    INNER JOIN community.EventSpeakers es ON es.ID = ss.EventSpeakerID AND es.EventID = ss.EventID
                    WHERE ss.EventID = s.EventID AND ss.SessionizeSessionID = s.SessionizeSessionID AND es.SpeakerID = @speakerId));
            """, connection);
        AddKey(command, eventId, sessionId);
        command.Parameters.Add("@speakerId", SqlDbType.Int).Value = speakerId;
        command.Parameters.Add("@isSuperUser", SqlDbType.Bit).Value = isSuperUser;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 0)
        {
            logger.LogWarning("Cover refused: session {SessionId} of event {EventId} is not available to speaker {SpeakerId}.", sessionId, eventId, speakerId);
            throw new InvalidOperationException("La sessione non risulta assegnata al tuo profilo speaker.");
        }
    }

    private async Task<CoverData?> LoadAsync(int eventId, string sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        string eventName, title;
        DateTime eventDate;
        await using (var command = new SqlCommand("""
            SELECT e.Name, e.EventDate, s.Title
            FROM community.EventSessions s
            INNER JOIN community.Events e ON e.ID = s.EventID
            WHERE s.EventID = @eventId AND s.SessionizeSessionID = @sessionId;
            """, connection))
        {
            AddKey(command, eventId, sessionId);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            (eventName, eventDate, title) = (reader.GetString(0), reader.GetDateTime(1), reader.GetString(2));
        }

        var speakers = new List<Speaker>();
        await using (var command = new SqlCommand("""
            SELECT es.Name, es.ProfilePictureUrl
            FROM community.EventSessionSpeakers ss
            INNER JOIN community.EventSpeakers es ON es.ID = ss.EventSpeakerID AND es.EventID = ss.EventID
            WHERE ss.EventID = @eventId AND ss.SessionizeSessionID = @sessionId
            ORDER BY es.Name;
            """, connection))
        {
            AddKey(command, eventId, sessionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                speakers.Add(new Speaker(reader.GetString(0), reader.GetString(1)));
            }
        }

        var sponsors = new List<int>();
        await using (var command = new SqlCommand("""
            SELECT s.MediaAssetID
            FROM community.EventSponsors es
            INNER JOIN community.Sponsors s ON s.ID = es.SponsorID
            WHERE es.EventID = @eventId AND s.MediaAssetID IS NOT NULL
            ORDER BY es.SortOrder, s.Name;
            """, connection))
        {
            command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sponsors.Add(reader.GetInt32(0));
            }
        }

        return new CoverData(eventName, eventDate, title, speakers, sponsors);
    }

    private async Task<byte[]?> ReadAssetAsync(int assetId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT Content FROM community.MediaAssets WHERE ID = @id;", connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = assetId;
        return await command.ExecuteScalarAsync(cancellationToken) as byte[];
    }

    /// <summary>Downloads a speaker's photo (only HTTPS on Sessionize's hosts, short timeout, size cap). Null when it is not available.</summary>
    private async Task<SKBitmap?> DownloadPhotoAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.Equals("sessionize.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".sessionize.com", StringComparison.OrdinalIgnoreCase)))
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                logger.LogWarning("Speaker photo ignored because its address is not an HTTPS Sessionize address: {Url}", url);
            }

            return null;
        }

        try
        {
            using var client = httpClientFactory.CreateClient("Sessionize");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumPhotoBytes)
            {
                logger.LogWarning("Speaker photo skipped (too large): {Url}", url);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            return bytes.Length <= MaximumPhotoBytes ? SKBitmap.Decode(bytes) : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not download the speaker photo {Url}; the cover is generated without it.", url);
            return null;
        }
    }

    private async Task StoreAsync(
        int eventId, string sessionId, string fileName, string contentType, byte[] content, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        int? oldAssetId;
        await using (var read = new SqlCommand("""
            SELECT CoverAssetID FROM community.EventSessions WITH (UPDLOCK, HOLDLOCK)
            WHERE EventID = @eventId AND SessionizeSessionID = @sessionId;
            """, connection, transaction))
        {
            AddKey(read, eventId, sessionId);
            var value = await read.ExecuteScalarAsync(cancellationToken);
            if (value is null)
            {
                throw new InvalidOperationException("La sessione non esiste.");
            }

            oldAssetId = value is DBNull ? null : Convert.ToInt32(value);
        }

        int assetId;
        await using (var insert = new SqlCommand("""
            INSERT INTO community.MediaAssets (FileName, ContentType, Content, AltText)
            VALUES (@fileName, @contentType, @content, N'Copertina sessione');
            SELECT CONVERT(int, SCOPE_IDENTITY());
            """, connection, transaction))
        {
            insert.Parameters.Add("@fileName", SqlDbType.NVarChar, 255).Value = fileName.Length > 255 ? fileName[^255..] : fileName;
            insert.Parameters.Add("@contentType", SqlDbType.NVarChar, 100).Value = contentType;
            insert.Parameters.Add("@content", SqlDbType.VarBinary, -1).Value = content;
            assetId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
        }

        await using (var update = new SqlCommand(
            "UPDATE community.EventSessions SET CoverAssetID = @asset WHERE EventID = @eventId AND SessionizeSessionID = @sessionId;",
            connection, transaction))
        {
            AddKey(update, eventId, sessionId);
            update.Parameters.Add("@asset", SqlDbType.Int).Value = assetId;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (oldAssetId is int old)
        {
            await DeleteOrphanAsync(connection, transaction, old, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
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

    // ------------------------------------------------------------------ drawing

    private SKImage Render(CoverData data, IReadOnlyList<SKBitmap?> photos, IReadOnlyList<SKBitmap> logos)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        using var bold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var regular = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal) ?? SKTypeface.Default;

        // Header: date of the event and its name.
        var italian = System.Globalization.CultureInfo.GetCultureInfo("it-IT");
        var when = data.EventDate.TimeOfDay == TimeSpan.Zero
            ? data.EventDate.ToString("d MMMM yyyy", italian)
            : data.EventDate.ToString("d MMMM yyyy, 'ore' H", italian);
        using (var paint = new SKPaint { Color = Blue, IsAntialias = true })
        {
            using var small = new SKFont(bold, 27);
            canvas.DrawText(when, 14, 38, SKTextAlign.Left, small, paint);
            canvas.DrawText(data.EventName, 14, 76, SKTextAlign.Left, small, paint);
        }

        // 1nn0va logo, top right.
        using (var logo = LoadSiteLogo())
        {
            if (logo is not null)
            {
                DrawContained(canvas, logo, new SKRect(Width - 250, 8, Width - 14, 128), whiteOnBar: false);
            }
        }

        // Title, centered, up to three lines.
        using (var paint = new SKPaint { Color = Blue, IsAntialias = true })
        {
            var size = 64f;
            List<string> lines;
            while ((lines = Wrap(data.Title, regular, size, Width - 160)).Count > (size > 48 ? 2 : 3) && size > 34)
            {
                size -= 4;
            }

            using var font = new SKFont(regular, size);
            var y = 175f - (lines.Count - 1) * size * 0.4f;
            foreach (var line in lines)
            {
                canvas.DrawText(line, Width / 2f, y, SKTextAlign.Center, font, paint);
                y += size * 1.2f;
            }
        }

        // Speakers: round photos side by side, names below.
        var count = Math.Max(1, data.Speakers.Count);
        var diameter = count switch { 1 => 250f, 2 => 220f, _ => 170f };
        var gap = 40f;
        var totalWidth = count * diameter + (count - 1) * gap;
        var left = (Width - totalWidth) / 2f;
        var centerY = 440f;
        for (var index = 0; index < data.Speakers.Count; index++)
        {
            var centerX = left + index * (diameter + gap) + diameter / 2f;
            DrawRoundPhoto(canvas, photos[index], centerX, centerY, diameter);
        }

        var names = string.Join(" · ", data.Speakers.Select(speaker => speaker.Name));
        using (var paint = new SKPaint { Color = Blue, IsAntialias = true })
        {
            var size = 54f;
            while (size > 28 && Measure(regular, size, names) > Width - 100)
            {
                size -= 2;
            }

            using var font = new SKFont(regular, size);
            canvas.DrawText(names, Width / 2f, 640, SKTextAlign.Center, font, paint);
        }

        // Bottom bar with the community logo and the sponsors.
        using (var bar = new SKPaint { Color = Blue })
        {
            canvas.DrawRect(new SKRect(0, BarTop, Width, Height), bar);
        }

        var items = new List<SKBitmap>();
        using var siteLogo = LoadSiteLogo();
        if (siteLogo is not null)
        {
            DrawContained(canvas, siteLogo, new SKRect(8, BarTop + 6, 160, Height - 6), whiteOnBar: true);
        }

        items.AddRange(logos);
        if (items.Count > 0)
        {
            var area = new SKRect(190, BarTop + 6, Width - 20, Height - 6);
            var slot = area.Width / items.Count;
            for (var index = 0; index < items.Count; index++)
            {
                DrawContained(canvas, items[index], new SKRect(area.Left + index * slot + 8, area.Top, area.Left + (index + 1) * slot - 8, area.Bottom), whiteOnBar: true);
            }
        }

        return surface.Snapshot();
    }

    private static float Measure(SKTypeface typeface, float size, string text)
    {
        using var font = new SKFont(typeface, size);
        return font.MeasureText(text);
    }

    private static List<string> Wrap(string text, SKTypeface typeface, float size, float maxWidth)
    {
        using var font = new SKFont(typeface, size);
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && font.MeasureText(candidate) > maxWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return lines;
    }

    private static void DrawRoundPhoto(SKCanvas canvas, SKBitmap? photo, float centerX, float centerY, float diameter)
    {
        var radius = diameter / 2f;
        using (var ring = new SKPaint { Color = LightBlue, IsAntialias = true })
        {
            canvas.DrawCircle(centerX, centerY, radius + 10, ring);
        }

        using (var background = new SKPaint { Color = new SKColor(0xF1, 0xF5, 0xF9), IsAntialias = true })
        {
            canvas.DrawCircle(centerX, centerY, radius, background);
        }

        if (photo is null)
        {
            return;
        }

        canvas.Save();
        using (var clip = new SKPath())
        {
            clip.AddCircle(centerX, centerY, radius);
            canvas.ClipPath(clip, SKClipOperation.Intersect, true);
        }

        // "Cover" fit: the photo fills the circle, cropped in the middle (a little towards the top, where faces are).
        var scale = Math.Max(diameter / photo.Width, diameter / photo.Height);
        var drawWidth = photo.Width * scale;
        var drawHeight = photo.Height * scale;
        var target = new SKRect(centerX - drawWidth / 2f, centerY - radius - (drawHeight - diameter) * 0.25f,
            centerX + drawWidth / 2f, centerY - radius - (drawHeight - diameter) * 0.25f + drawHeight);
        using var image = SKImage.FromBitmap(photo);
        canvas.DrawImage(image, target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        canvas.Restore();
    }

    /// <summary>Draws a logo inside the box keeping its proportions. On the blue bar a transparent logo becomes white; an opaque one gets a white chip.</summary>
    private static void DrawContained(SKCanvas canvas, SKBitmap bitmap, SKRect box, bool whiteOnBar)
    {
        var scale = Math.Min(box.Width / bitmap.Width, box.Height / bitmap.Height);
        var width = bitmap.Width * scale;
        var height = bitmap.Height * scale;
        var target = SKRect.Create(box.MidX - width / 2f, box.MidY - height / 2f, width, height);
        using var image = SKImage.FromBitmap(bitmap);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        if (!whiteOnBar)
        {
            canvas.DrawImage(image, target, sampling);
            return;
        }

        if (HasTransparency(bitmap))
        {
            using var paint = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(SKColors.White, SKBlendMode.SrcIn), IsAntialias = true };
            canvas.DrawImage(image, target, sampling, paint);
        }
        else
        {
            using var chip = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawRoundRect(new SKRoundRect(SKRect.Inflate(target, 6, 4), 8), chip);
            canvas.DrawImage(image, target, sampling);
        }
    }

    private static bool HasTransparency(SKBitmap bitmap)
    {
        foreach (var (x, y) in new[] { (0, 0), (bitmap.Width - 1, 0), (0, bitmap.Height - 1), (bitmap.Width - 1, bitmap.Height - 1) })
        {
            if (bitmap.GetPixel(x, y).Alpha < 250)
            {
                return true;
            }
        }

        return false;
    }

    private SKBitmap? LoadSiteLogo()
    {
        try
        {
            var file = environment.WebRootFileProvider.GetFileInfo("img/1nn0va.png");
            if (!file.Exists)
            {
                logger.LogWarning("The community logo img/1nn0va.png was not found: the cover is drawn without it.");
                return null;
            }

            using var stream = file.CreateReadStream();
            return SKBitmap.Decode(stream);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the community logo.");
            return null;
        }
    }
}

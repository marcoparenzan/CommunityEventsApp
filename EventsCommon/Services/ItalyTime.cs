namespace EventsCommon.Services;

/// <summary>
/// The community is Italian: every date the application creates or shows is expressed in the community's time zone
/// (Application:TimeZone, "Europe/Rome" = CET/CEST when not configured), whatever the time zone of the server
/// (Azure runs in UTC). Stored instants are datetimeoffset values with the local offset (UTC + zone).
/// </summary>
public static class ItalyTime
{
    public const string DefaultTimeZoneId = "Europe/Rome";

    // Identificatori Windows di riserva (Windows senza ICU non conosce quelli IANA).
    private static readonly Dictionary<string, string> WindowsEquivalents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Europe/Rome"] = "W. Europe Standard Time",
        ["CET"] = "W. Europe Standard Time",
        ["CEST"] = "W. Europe Standard Time"
    };

    private static TimeZoneInfo zone = Resolve(DefaultTimeZoneId, out _);

    /// <summary>Windows time zone id (what SQL Server's AT TIME ZONE expects), e.g. "W. Europe Standard Time".</summary>
    public static string WindowsId { get; private set; } = WindowsIdOf(zone);

    public static string ZoneId => zone.Id;

    /// <summary>Applies Application:TimeZone. An unknown value falls back to the default and is reported through <paramref name="warning"/>.</summary>
    public static void Configure(string? timeZoneId, out string? warning)
    {
        warning = null;
        var requested = string.IsNullOrWhiteSpace(timeZoneId) ? DefaultTimeZoneId : timeZoneId.Trim();
        var resolved = Resolve(requested, out var found);
        if (!found)
        {
            warning = $"Application:TimeZone '{requested}' non è un fuso orario valido: uso {DefaultTimeZoneId}.";
            resolved = Resolve(DefaultTimeZoneId, out _);
        }

        zone = resolved;
        WindowsId = WindowsIdOf(resolved);
    }

    private static TimeZoneInfo Resolve(string id, out bool found)
    {
        var candidates = new List<string> { id };
        if (WindowsEquivalents.TryGetValue(id, out var windows))
        {
            candidates.Add(windows);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                found = true;
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // prova l'identificativo successivo
            }
        }

        found = false;
        return TimeZoneInfo.Utc;
    }

    private static string WindowsIdOf(TimeZoneInfo value)
    {
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(value.Id, out var windowsId) && windowsId is not null)
        {
            return windowsId;
        }

        return WindowsEquivalents.TryGetValue(value.Id, out var known) ? known : value.Id;
    }

    /// <summary>The same instant expressed in local time (with the right offset for summer / winter time).</summary>
    public static DateTimeOffset ToItaly(this DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, zone);

    /// <summary>Current date and time as a local-time instant (UTC + the time zone's offset).</summary>
    public static DateTimeOffset NowOffset => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);

    public static DateTime Now => NowOffset.DateTime;

    public static DateTime Today => Now.Date;
}

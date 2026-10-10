namespace EventsCommon.Services;

/// <summary>
/// How long a sign-in lasts. Sliding: every use renews it, so it ends only after that long without opening the site,
/// or at sign-out. A sign-in made from the installed app (PWA) lasts much longer.
/// Authentication:SessionHours (default 24), Authentication:InstalledSessionDays (default 90),
/// Authentication:SessionCheckMinutes (default 5, how often the account is checked again).
/// </summary>
public static class SessionLifetime
{
    public static TimeSpan Browser(IConfiguration configuration) =>
        TimeSpan.FromHours(Read(configuration["Authentication:SessionHours"], 24, 1, 24 * 30));

    public static TimeSpan Installed(IConfiguration configuration) =>
        TimeSpan.FromDays(Read(configuration["Authentication:InstalledSessionDays"], 90, 1, 365));

    public static TimeSpan For(IConfiguration configuration, bool installedApp) =>
        installedApp ? Installed(configuration) : Browser(configuration);

    /// <summary>How long a positive account check is trusted before the database is asked again.</summary>
    public static TimeSpan CheckInterval(IConfiguration configuration) =>
        TimeSpan.FromMinutes(Read(configuration["Authentication:SessionCheckMinutes"], 5, 0, 1440));

    private static int Read(string? value, int fallback, int minimum, int maximum) =>
        int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum ? parsed : fallback;
}

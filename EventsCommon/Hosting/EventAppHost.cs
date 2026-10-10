using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using EventsCommon.Services;

namespace EventsCommon.Hosting;

public static class EventAppHost
{
    private const string DefaultConfigurationRoot = @"D:\Configurations\EventApp";

    /// <summary>
    /// Loads appsettings.json, appsettings.{Environment}.json and secrets.json from
    /// EVENTAPP_CONFIG_PATH or, by default, from D:\Configurations\EventApp\{appName}.
    /// </summary>
    public static void AddEventAppConfiguration(this WebApplicationBuilder builder, string appName, string[] args)
    {
        var configurationDirectory = Environment.GetEnvironmentVariable("EVENTAPP_CONFIG_PATH");
        if (string.IsNullOrWhiteSpace(configurationDirectory))
        {
            configurationDirectory = Path.Combine(DefaultConfigurationRoot, appName);
        }

        configurationDirectory = Path.GetFullPath(configurationDirectory);

        // Fuori dalla macchina di sviluppo (es. Azure App Service) la cartella esterna non esiste:
        // la configurazione arriva dalle impostazioni dell'applicazione (variabili d'ambiente,
        // ConnectionStrings__Events, Email__Smtp__*, ...) e l'avvio non deve fallire.
        if (!File.Exists(Path.Combine(configurationDirectory, "appsettings.json")))
        {
            builder.Configuration.AddEnvironmentVariables();
            if (args.Length > 0)
            {
                builder.Configuration.AddCommandLine(args);
            }

            StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);
            return;
        }

        var appSettingsSources = builder.Configuration.Sources
            .OfType<JsonConfigurationSource>()
            .Where(source =>
                string.Equals(source.Path, "appsettings.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source.Path, $"appsettings.{builder.Environment.EnvironmentName}.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var source in appSettingsSources)
        {
            builder.Configuration.Sources.Remove(source);
        }

        builder.Configuration
            .AddJsonFile(Path.Combine(configurationDirectory, "appsettings.json"), optional: false, reloadOnChange: true)
            .AddJsonFile(
                Path.Combine(configurationDirectory, $"appsettings.{builder.Environment.EnvironmentName}.json"),
                optional: true,
                reloadOnChange: true);

        builder.Configuration.AddJsonFile(
            Path.Combine(configurationDirectory, "secrets.json"),
            optional: true,
            reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables();
        if (args.Length > 0)
        {
            builder.Configuration.AddCommandLine(args);
        }

        StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);
    }

    /// <summary>
    /// Applies Application:TimeZone ("Europe/Rome" = CET/CEST if missing) and tells the database which zone to use for
    /// the dates it creates (community.Settings, read by community.LocalNow()).
    /// </summary>
    public static void ConfigureTimeZone(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("EventAppConfiguration");
        ItalyTime.Configure(app.Configuration["Application:TimeZone"], out var warning);
        if (warning is not null)
        {
            logger.LogWarning("{Warning}", warning);
        }

        try
        {
            using var connection = app.Services.GetRequiredService<SqlConnectionFactory>().OpenAsync().GetAwaiter().GetResult();
            using var command = new Microsoft.Data.SqlClient.SqlCommand("""
                MERGE community.Settings AS target
                USING (SELECT N'TimeZone' AS [Key], @value AS [Value]) AS source ON target.[Key] = source.[Key]
                WHEN MATCHED AND target.[Value] <> source.[Value] THEN UPDATE SET [Value] = source.[Value]
                WHEN NOT MATCHED THEN INSERT ([Key], [Value]) VALUES (source.[Key], source.[Value]);
                """, connection);
            command.Parameters.Add("@value", System.Data.SqlDbType.NVarChar, 256).Value = ItalyTime.WindowsId;
            command.ExecuteNonQuery();
            logger.LogInformation("Time zone: {Zone} (database: {WindowsId}).", ItalyTime.ZoneId, ItalyTime.WindowsId);
        }
        catch (Exception exception)
        {
            // Database non raggiungibile o non ancora inizializzato (Provision.sql): l'app parte lo stesso.
            logger.LogWarning(exception, "Could not store the time zone in community.Settings: run Provision.sql.");
        }
    }

    /// <summary>Logs (once, at startup) the mandatory settings that are missing, without printing any secret.</summary>
    public static void LogConfigurationWarnings(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("EventAppConfiguration");
        var configuration = app.Configuration;
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Events")))
        {
            logger.LogError("Missing configuration: ConnectionStrings:Events (App Service: ConnectionStrings__Events).");
        }

        var pinKey = configuration["Authentication:PinHashKey"];
        if (string.IsNullOrWhiteSpace(pinKey) || System.Text.Encoding.UTF8.GetByteCount(pinKey) < 32)
        {
            logger.LogError("Missing or too short configuration: Authentication:PinHashKey (App Service: Authentication__PinHashKey, at least 32 characters).");
        }

        foreach (var name in new[] { "SMTPHostName", "SMTPPort", "Username", "Password", "FromAddress" })
        {
            if (string.IsNullOrWhiteSpace(configuration[$"Email:Smtp:{name}"]))
            {
                logger.LogWarning("Missing configuration: Email:Smtp:{Name} (App Service: Email__Smtp__{Name}).", name, name);
            }
        }
    }

    /// <summary>Registers the data access and messaging services shared by both applications.</summary>
    public static IServiceCollection AddEventsCommon(this IServiceCollection services)
    {
        services.AddSingleton<SqlConnectionFactory>();
        services.AddSingleton<EventCatalogService>();
        services.AddSingleton<SmtpEmailSender>();
        services.AddScoped<EventCheckInService>();
        services.AddSingleton<MediaAssetService>();
        return services;
    }
}

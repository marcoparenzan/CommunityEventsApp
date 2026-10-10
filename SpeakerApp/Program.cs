using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using EventsCommon.Hosting;
using EventsCommon.Models;
using EventsCommon.Services;
using SpeakerApp.Components;
using SpeakerApp.Services;

var builder = WebApplication.CreateBuilder(args);
builder.AddEventAppConfiguration("SpeakerApp", args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    // Le importazioni incollano interi file CSV: il limite predefinito di 32 KB li troncherebbe.
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 8 * 1024 * 1024);
builder.Services.AddOpenApi();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication("EventsCookie")
    .AddCookie("EventsCookie", options =>
    {
        options.Cookie.Name = "__Host-1nn0va-speaker";
        options.Cookie.Path = "/";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.LoginPath = "/";
        options.ExpireTimeSpan = SessionLifetime.Browser(builder.Configuration);
        options.SlidingExpiration = true;
        // Una sessione lunga non deve sopravvivere a un account tolto o a permessi cambiati: ogni pochi minuti si controlla.
        options.Events.OnValidatePrincipal = async context =>
        {
            if (!int.TryParse(context.Principal?.FindFirstValue("speaker_id"), out var speakerId))
            {
                return;
            }

            var isSuperUser = context.Principal!.HasClaim("is_super_user", "true");
            var canCheckIn = context.Principal.HasClaim("can_check_in", "true");
            var services = context.HttpContext.RequestServices;
            var cache = services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
            var key = $"session-speaker:{speakerId}:{isSuperUser}:{canCheckIn}";
            if (cache.TryGetValue(key, out _))
            {
                return;
            }

            try
            {
                if (!await services.GetRequiredService<SpeakerSessionCheck>().IsStillValidAsync(speakerId, isSuperUser, canCheckIn, context.HttpContext.RequestAborted))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync("EventsCookie");
                    return;
                }

                var interval = SessionLifetime.CheckInterval(services.GetRequiredService<IConfiguration>());
                if (interval > TimeSpan.Zero)
                {
                    cache.Set(key, true, interval);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                services.GetRequiredService<ILogger<Program>>().LogWarning(exception, "Could not re-check the signed-in speaker {SpeakerId}.", speakerId);
            }
        };
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddMemoryCache();
builder.Services.AddScoped<SpeakerSessionCheck>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Speaker", policy =>
        policy.RequireAuthenticatedUser().RequireClaim("account_type", "speaker").RequireClaim("is_speaker", "true"));
    options.AddPolicy("SuperUser", policy =>
        policy.RequireAuthenticatedUser().RequireClaim("is_super_user", "true"));
    options.AddPolicy("CanCheckIn", policy =>
        policy.RequireAuthenticatedUser().RequireClaim("can_check_in", "true"));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("pin", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(15),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});
builder.Services.AddEventsCommon();
builder.Services.AddSingleton<CommunityLinks>();
builder.Services.AddSingleton<ISiteLinks>(provider => provider.GetRequiredService<CommunityLinks>());
builder.Services.AddScoped<ISpeakerAuthenticationService, SpeakerAuthenticationService>();
builder.Services.AddScoped<SpeakerManagementService>();
builder.Services.AddScoped<EventReportService>();
builder.Services.AddScoped<DatabaseResetService>();
builder.Services.AddScoped<SpeakerImportService>();
builder.Services.AddScoped<UserImportService>();
builder.Services.AddScoped<MaterialsImportService>();
builder.Services.AddScoped<UserManagementService>();
builder.Services.AddScoped<SpeakerSelfService>();
builder.Services.AddScoped<SponsorAdminService>();
builder.Services.AddScoped<EventTemplateService>();
builder.Services.AddScoped<EventMailService>();
builder.Services.AddHttpClient("Sessionize", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("1nn0va-Events/1.0");
});
builder.Services.AddSingleton<SessionizeSyncService>();

var app = builder.Build();
app.LogConfigurationWarnings();
app.ConfigureTimeZone();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Template PowerPoint dell'evento: lo scaricano gli speaker di quell'evento e i superutenti (mai pubblico).
app.MapGet("/api/speaker/events/{eventId:int}/slide-template", async (
    int eventId,
    ClaimsPrincipal user,
    EventTemplateService templates,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue("speaker_id"), out var speakerId))
    {
        return Results.Unauthorized();
    }

    var file = await templates.GetForSpeakerAsync(eventId, speakerId, user.HasClaim("is_super_user", "true"), cancellationToken);
    return file is null
        ? Results.NotFound()
        : Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization("Speaker");

app.MapGet("/api/admin/events/{eventId:int}/slide-template", async (
    int eventId,
    EventTemplateService templates,
    CancellationToken cancellationToken) =>
{
    var file = await templates.GetForSpeakerAsync(eventId, 0, true, cancellationToken);
    return file is null
        ? Results.NotFound()
        : Results.File(file.Content, file.ContentType, file.FileName);
}).RequireAuthorization("SuperUser");

// Azzera il database lasciando solo Marco Parenzan. Funziona solo se Admin:ResetSecret è
// configurato (almeno 16 caratteri) e il segreto nella richiesta coincide; altrimenti 404.
app.MapGet("/admin/reset-database", async (
    string? secret,
    IConfiguration configuration,
    DatabaseResetService reset,
    ILogger<Program> logger,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    var expected = configuration["Admin:ResetSecret"];
    if (string.IsNullOrWhiteSpace(expected) || expected.Length < 16 || string.IsNullOrEmpty(secret) ||
        !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(secret),
            System.Text.Encoding.UTF8.GetBytes(expected)))
    {
        return Results.NotFound();
    }

    try
    {
        await reset.ResetAsync(cancellationToken);
        logger.LogWarning("Database reset requested from {RemoteIp}: all data cleared, only Marco Parenzan kept.", context.Connection.RemoteIpAddress);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { message = "Database azzerato: resta solo Marco Parenzan (marco.parenzan@1nn0va.it)." });
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        logger.LogError(exception, "Database reset failed.");
        return Results.Json(
            new { message = "Non è stato possibile azzerare il database. Controlla il log dell'applicazione." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireRateLimiting("pin");

app.MapPost("/api/auth/sign-out", async (HttpContext context) =>
{
    await context.SignOutAsync("EventsCookie");
    return Results.Ok(new { message = "Disconnessione completata." });
}).RequireAuthorization();

app.MapPost("/api/speaker-auth/request-pin", async Task<Results<
    Ok<SpeakerPinRequestResponse>,
    BadRequest<SpeakerPinRequestResponse>,
    JsonHttpResult<SpeakerPinRequestResponse>>> (
    RequestSpeakerPinRequest request,
    HttpContext context,
    ISpeakerAuthenticationService authentication,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    try
    {
        await authentication.RequestPinAsync(request.Email, $"{context.Request.Scheme}://{context.Request.Host}", cancellationToken);
        return TypedResults.Ok(new SpeakerPinRequestResponse(
            "Se l'indirizzo corrisponde a uno speaker sincronizzato, riceverai un PIN via email."));
    }
    catch (ArgumentException exception)
    {
        return TypedResults.BadRequest(new SpeakerPinRequestResponse(exception.Message));
    }
    catch (EmailDeliveryException exception)
    {
        logger.LogError(exception, "Failed to send speaker sign-in PIN.");
        return TypedResults.Json(
            new SpeakerPinRequestResponse("Non è stato possibile inviare il PIN. Il servizio email non è disponibile; riprova più tardi."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        logger.LogError(exception, "Speaker sign-in PIN request failed.");
        return TypedResults.Json(
            new SpeakerPinRequestResponse("Non è stato possibile elaborare la richiesta. Il server non è configurato correttamente o il database non è raggiungibile."),
            statusCode: StatusCodes.Status500InternalServerError);
    }
})
    .RequireRateLimiting("pin")
    .WithName("RequestSpeakerPin")
    .WithSummary("Richiede un PIN di accesso speaker")
    .WithDescription("Invia il PIN solo a un profilo speaker preesistente collegato a dati importati da Sessionize; non crea account.")
    .Produces<SpeakerPinRequestResponse>(StatusCodes.Status200OK)
    .Produces<SpeakerPinRequestResponse>(StatusCodes.Status400BadRequest)
    .Produces<SpeakerPinRequestResponse>(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/api/speaker-auth/verify-pin", async Task<Results<
    Ok<SpeakerLoginResponse>,
    UnauthorizedHttpResult,
    BadRequest<SpeakerPinRequestResponse>>> (
    VerifySpeakerPinRequest request,
    HttpContext context,
    ISpeakerAuthenticationService authentication,
    CancellationToken cancellationToken) =>
{
    try
    {
        var principal = await authentication.VerifyPinAsync(request.Email, request.Pin, cancellationToken);
        if (principal is null)
        {
            return TypedResults.Unauthorized();
        }

        await context.SignInAsync("EventsCookie", principal, new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.Add(SessionLifetime.For(context.RequestServices.GetRequiredService<IConfiguration>(), request.Installed))
        });
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new SpeakerLoginResponse("Accesso speaker effettuato."));
    }
    catch (ArgumentException exception)
    {
        return TypedResults.BadRequest(new SpeakerPinRequestResponse(exception.Message));
    }
})
    .RequireRateLimiting("pin")
    .WithName("VerifySpeakerPin")
    .WithSummary("Verifica il PIN speaker e avvia la sessione")
    .Produces<SpeakerLoginResponse>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces<SpeakerPinRequestResponse>(StatusCodes.Status400BadRequest);

app.MapGet("/api/speaker/account", async Task<Results<Ok<SpeakerAccountResponse>, UnauthorizedHttpResult>> (
    ClaimsPrincipal user,
    ISpeakerAuthenticationService authentication,
    CancellationToken cancellationToken) =>
{
    if (user.FindFirstValue("account_type") != "speaker" ||
        !int.TryParse(user.FindFirstValue("speaker_id"), out var speakerId))
    {
        return TypedResults.Unauthorized();
    }

    var account = await authentication.GetAccountAsync(speakerId, cancellationToken);
    return account is null ? TypedResults.Unauthorized() : TypedResults.Ok(account);
})
    .RequireAuthorization()
    .WithName("GetSpeakerAccount")
    .WithSummary("Restituisce l'area personale dello speaker autenticato")
    .Produces<SpeakerAccountResponse>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status401Unauthorized);

app.MapGet("/api/speaker/sessions", async Task<Results<
    Ok<IReadOnlyList<SpeakerSessionResponse>>,
    UnauthorizedHttpResult>> (
    ClaimsPrincipal user,
    ISpeakerAuthenticationService authentication,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue("speaker_id"), out var speakerId))
    {
        return TypedResults.Unauthorized();
    }

    return TypedResults.Ok(await authentication.GetSessionsAsync(speakerId, cancellationToken));
})
    .RequireAuthorization("Speaker")
    .WithName("GetSpeakerSessions")
    .WithSummary("Elenca le sessioni assegnate allo speaker")
    .Produces<IReadOnlyList<SpeakerSessionResponse>>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status401Unauthorized);

app.MapPost("/api/speaker/sessions/resources", async Task<Results<
    Ok<EventCheckInResponse>,
    NotFound<EventCheckInResponse>,
    UnauthorizedHttpResult,
    BadRequest<EventCheckInResponse>>> (
    UpdateSpeakerSessionLinksRequest request,
    ClaimsPrincipal user,
    ISpeakerAuthenticationService authentication,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue("speaker_id"), out var speakerId))
    {
        return TypedResults.Unauthorized();
    }

    try
    {
        var updated = await authentication.UpdateSessionLinksAsync(speakerId, request, cancellationToken);
        return updated
            ? TypedResults.Ok(new EventCheckInResponse(true, "Link della sessione aggiornati."))
            : TypedResults.NotFound(new EventCheckInResponse(false, "La sessione non risulta assegnata al tuo profilo speaker."));
    }
    catch (ArgumentException exception)
    {
        return TypedResults.BadRequest(new EventCheckInResponse(false, exception.Message));
    }
})
    .RequireAuthorization("Speaker")
    .WithName("UpdateSpeakerSessionLinks")
    .WithSummary("Aggiorna i link slide e GitHub di una sessione assegnata")
    .Produces<EventCheckInResponse>(StatusCodes.Status200OK)
    .Produces<EventCheckInResponse>(StatusCodes.Status400BadRequest)
    .Produces<EventCheckInResponse>(StatusCodes.Status404NotFound);

app.MapPost("/api/speaker/events/{eventId:int}/sessions/resources", async Task<Results<
    Ok<EventCheckInResponse>,
    NotFound<EventCheckInResponse>,
    BadRequest<EventCheckInResponse>>> (
    int eventId,
    UpdateSessionResourcesRequest request,
    EventCatalogService eventCatalog,
    CancellationToken cancellationToken) =>
{
    try
    {
        var updated = await eventCatalog.UpdateSessionResourcesAsync(
            eventId,
            request.SessionizeSessionId,
            request.VideoUrl,
            request.PresentationUrl,
            request.RepositoryUrl,
            cancellationToken);
        return updated
            ? TypedResults.Ok(new EventCheckInResponse(true, "Link della sessione aggiornati."))
            : TypedResults.NotFound(new EventCheckInResponse(false, "Sessione non trovata o non attiva."));
    }
    catch (ArgumentException exception)
    {
        return TypedResults.BadRequest(new EventCheckInResponse(false, exception.Message));
    }
})
    .RequireAuthorization("SuperUser")
    .WithName("UpdateSessionResources")
    .WithSummary("Aggiorna i link pubblici di una sessione")
    .WithDescription("Permette al superutente di associare video, slide e repository a una sessione sincronizzata.")
    .Produces<EventCheckInResponse>(StatusCodes.Status200OK)
    .Produces<EventCheckInResponse>(StatusCodes.Status400BadRequest)
    .Produces<EventCheckInResponse>(StatusCodes.Status404NotFound);

app.MapGet("/api/speaker/events/{eventId:int}/attendees", async (
    int eventId,
    EventCatalogService eventCatalog,
    EventCheckInService checkIn,
    CancellationToken cancellationToken) =>
{
    var eventExists = (await eventCatalog.GetUpcomingAsync(ItalyTime.Today, cancellationToken))
        .Any(item => item.Id == eventId);
    return eventExists
        ? Results.Ok(await checkIn.GetAttendeesAsync(eventId, cancellationToken))
        : Results.NotFound();
})
    .RequireAuthorization("CanCheckIn")
    .WithName("GetEventAttendees")
    .WithSummary("Elenca i partecipanti per il personale autorizzato")
    .Produces<IReadOnlyList<EventAttendeeResponse>>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status404NotFound);

app.MapPost("/api/speaker/events/{eventId:int}/attendees", async (
    int eventId,
    AddEventParticipantRequest request,
    EventCheckInService checkIn,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.FirstName) ||
        string.IsNullOrWhiteSpace(request.LastName) ||
        request.FirstName.Length > 64 ||
        request.LastName.Length > 64)
    {
        return Results.BadRequest(new EventCheckInResponse(
            false,
            "Nome e cognome sono obbligatori e non possono superare 64 caratteri."));
    }

    try
    {
        var result = await checkIn.AddConfirmedParticipantAsync(
            eventId,
            request.Email,
            request.IsEatingPizza,
            request.LactoseIntolerant,
            request.FirstName,
            request.LastName,
            cancellationToken);
        return result.Ok ? Results.Ok(result) : Results.Conflict(result);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new EventCheckInResponse(false, exception.Message));
    }
})
    .RequireAuthorization("CanCheckIn")
    .WithName("AddEventAttendee")
    .WithSummary("Aggiunge un partecipante già confermato, senza inviare email")
    .WithDescription("Riutilizza l'account esistente per email oppure ne crea uno nuovo.")
    .Produces<EventCheckInResponse>(StatusCodes.Status200OK)
    .Produces<EventCheckInResponse>(StatusCodes.Status400BadRequest)
    .Produces<EventCheckInResponse>(StatusCodes.Status409Conflict);

app.MapGet("/api/speaker/events/{eventId:int}/people", async (
    int eventId,
    string? q,
    EventCheckInService checkIn,
    CancellationToken cancellationToken) =>
    Results.Ok(await checkIn.SearchPeopleAsync(eventId, q, cancellationToken)))
    .RequireAuthorization("CanCheckIn")
    .WithName("SearchEventPeople")
    .WithSummary("Cerca persone in anagrafica per iscriverle a un evento")
    .Produces<IReadOnlyList<PersonSearchResponse>>(StatusCodes.Status200OK);

app.MapPost("/api/speaker/events/{eventId:int}/attendees/existing", async (
    int eventId,
    RegisterExistingParticipantRequest request,
    EventCheckInService checkIn,
    CancellationToken cancellationToken) =>
{
    var result = await checkIn.RegisterExistingAsync(
        eventId,
        request.UserId,
        request.IsEatingPizza,
        request.LactoseIntolerant,
        cancellationToken);
    return result.Ok ? Results.Ok(result) : Results.Conflict(result);
})
    .RequireAuthorization("CanCheckIn")
    .WithName("RegisterExistingAttendee")
    .WithSummary("Iscrive all'evento una persona già presente in anagrafica")
    .Produces<EventCheckInResponse>(StatusCodes.Status200OK)
    .Produces<EventCheckInResponse>(StatusCodes.Status409Conflict);

app.MapPost("/api/speaker/events/{eventId:int}/attendees/{registrationId:int}/send-confirmation", async (
    int eventId,
    int registrationId,
    EventCheckInService checkIn,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await checkIn.SendConfirmationAsync(eventId, registrationId, cancellationToken);
        return result.Ok ? Results.Ok(result) : Results.Conflict(result);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new EventCheckInResponse(false, exception.Message));
    }
})
    .RequireAuthorization("CanCheckIn")
    .WithName("SendAttendeeConfirmation")
    .WithSummary("Invia al partecipante l'email di conferma dell'iscrizione")
    .Produces<EventCheckInResponse>(StatusCodes.Status200OK)
    .Produces<EventCheckInResponse>(StatusCodes.Status409Conflict);

app.MapPost("/api/speaker/events/{eventId:int}/attendees/{registrationId:int}/check-in", async (
    int eventId,
    int registrationId,
    EventCheckInService checkIn,
    CancellationToken cancellationToken) =>
{
    var checkedIn = await checkIn.ManualCheckInAsync(eventId, registrationId, cancellationToken);
    return checkedIn
        ? Results.Ok(new EventCheckInResponse(true, "Check-in manuale completato."))
        : Results.Conflict(new EventCheckInResponse(false, "Partecipante non trovato, non confermato o già registrato al check-in."));
})
    .RequireAuthorization("CanCheckIn")
    .WithName("ManuallyCheckInAttendee")
    .WithSummary("Registra manualmente il check-in di un partecipante confermato")
    .Produces<EventCheckInResponse>(StatusCodes.Status200OK)
    .Produces<EventCheckInResponse>(StatusCodes.Status409Conflict);

app.MapPost("/api/speaker/events/{eventId:int}/sync", async (
    int eventId,
    SessionizeSyncService sync,
    CancellationToken cancellationToken) =>
{
    var count = await sync.SyncAsync(eventId, cancellationToken);
    return Results.Ok(new { synchronizedSessions = count });
})
    .RequireAuthorization("SuperUser")
    .WithName("SyncEventSessions")
    .WithSummary("Sincronizza l'agenda Sessionize per un evento")
    .Produces(StatusCodes.Status200OK);

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(EventAppHost).Assembly);

app.Run();

public partial class Program;

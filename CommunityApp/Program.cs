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
using CommunityApp.Components;
using CommunityApp.Services;

var builder = WebApplication.CreateBuilder(args);
builder.AddEventAppConfiguration("CommunityApp", args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddOpenApi();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication("EventsCookie")
    .AddCookie("EventsCookie", options =>
    {
        options.Cookie.Name = "__Host-1nn0va-events";
        options.Cookie.Path = "/";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.LoginPath = "/account";
        options.ExpireTimeSpan = SessionLifetime.Browser(builder.Configuration);
        options.SlidingExpiration = true;
        // Una sessione lunga non deve sopravvivere a un account cancellato: ogni pochi minuti si controlla.
        options.Events.OnValidatePrincipal = async context =>
        {
            if (!int.TryParse(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                return;
            }

            var services = context.HttpContext.RequestServices;
            var cache = services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
            var key = $"session-user:{userId}";
            if (cache.TryGetValue(key, out _))
            {
                return;
            }

            try
            {
                if (!await services.GetRequiredService<UserAuthenticationService>().IsActiveAsync(userId, context.HttpContext.RequestAborted))
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
                // Database momentaneamente non raggiungibile: non si disconnette nessuno per questo.
                services.GetRequiredService<ILogger<Program>>().LogWarning(exception, "Could not re-check the signed-in user {UserId}.", userId);
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
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Participant", policy =>
        policy.RequireAuthenticatedUser().RequireClaim("account_type", "participant"));
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
    options.AddPolicy("event-email-confirmation", context =>
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
builder.Services.AddScoped<UserAuthenticationService>();
builder.Services.AddSingleton<ISiteLinks, CommunitySiteLinks>();

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

// Chi è autenticato vede gli eventi dall'area personale: redirect prima del rendering
// (NavigateTo durante il prerender lancia una NavigationException).
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (HttpMethods.IsGet(context.Request.Method)
        && (path == "/" || path.StartsWithSegments("/past"))
        && context.User.Identity?.IsAuthenticated == true)
    {
        context.Response.Redirect("/account");
        return;
    }

    await next();
});

app.UseAntiforgery();
app.MapStaticAssets();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapPost("/api/auth/request-pin", async (
    RequestPinRequest request,
    HttpContext context,
    UserAuthenticationService authentication,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    try
    {
        await authentication.RequestPinAsync(
            request.FirstName,
            request.LastName,
            request.Email,
            request.AllowEventNotifications,
            $"{context.Request.Scheme}://{context.Request.Host}",
            cancellationToken);
        return Results.Ok(new { message = "Se l'indirizzo è valido, il codice è stato inviato via email." });
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
    catch (EmailDeliveryException exception)
    {
        logger.LogError(exception, "Failed to send the sign-in PIN.");
        return Results.Json(
            new { message = "Non è stato possibile inviare il PIN. Il servizio email non è disponibile; riprova più tardi." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        logger.LogError(exception, "Sign-in PIN request failed.");
        return Results.Json(
            new { message = "Non è stato possibile elaborare la richiesta. Il server non è configurato correttamente o il database non è raggiungibile." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
})
.RequireRateLimiting("pin");

app.MapPost("/api/auth/verify-pin", async (
    VerifyPinRequest request,
    HttpContext context,
    UserAuthenticationService authentication,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    try
    {
        var principal = await authentication.VerifyPinAsync(request.Email, request.Pin, cancellationToken);
        if (principal is null)
        {
            logger.LogWarning("Sign-in failed from {RemoteIp}.", context.Connection.RemoteIpAddress);
            return Results.Unauthorized();
        }

        logger.LogInformation("Sign-in from {RemoteIp}, installed PWA={Installed}.", context.Connection.RemoteIpAddress, request.Installed);

        await context.SignInAsync("EventsCookie", principal, new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.Add(SessionLifetime.For(context.RequestServices.GetRequiredService<IConfiguration>(), request.Installed))
        });
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { message = "Accesso effettuato." });
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
})
.RequireRateLimiting("pin");

app.MapPost("/api/auth/sign-out", async (HttpContext context) =>
{
    await context.SignOutAsync("EventsCookie");
    return Results.Ok(new { message = "Disconnessione completata." });
}).RequireAuthorization();

app.MapGet("/api/account", async (
    ClaimsPrincipal user,
    UserAuthenticationService authentication,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
    {
        return Results.Unauthorized();
    }

    var registrations = await authentication.GetRegistrationsAsync(userId, cancellationToken);
    return Results.Ok(new
    {
        email = user.FindFirstValue(ClaimTypes.Email),
        isSuperUser = user.HasClaim("is_super_user", "true"),
        canCheckIn = user.HasClaim("can_check_in", "true"),
        registrations
    });
}).RequireAuthorization("Participant");

app.MapPost("/api/events/{eventId:int}/register", async (
    int eventId,
    RegisterEventRequest request,
    ClaimsPrincipal user,
    EventCatalogService eventCatalog,
    EventCheckInService checkIn,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var email = user.FindFirstValue(ClaimTypes.Email);
    if (string.IsNullOrWhiteSpace(email))
    {
        return Results.Unauthorized();
    }

    if ((await eventCatalog.GetAllAsync(cancellationToken)).FirstOrDefault(item => item.Id == eventId)?.IsLocked == true)
    {
        logger.LogWarning("Registration to event {EventId} refused: the event is locked.", eventId);
        return Results.Conflict(new EventCheckInResponse(false, "Le modifiche alle iscrizioni di questo evento sono state bloccate dall'organizzazione."));
    }

    try
    {
        var result = await checkIn.AddParticipantAsync(
            eventId,
            email,
            request.IsEatingPizza,
            request.LactoseIntolerant,
            cancellationToken: cancellationToken);
        logger.LogInformation("Registration request for event {EventId}: ok={Ok}.", eventId, result.Ok);
        return result.Ok ? Results.Ok(result) : Results.Conflict(result);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new EventCheckInResponse(false, exception.Message));
    }
})
    .RequireAuthorization("Participant")
    .WithName("RequestEventRegistration")
    .WithSummary("Richiede la conferma email di un'iscrizione a un evento")
    .WithDescription("L'iscrizione diventa attiva solo dopo la conferma tramite il link inviato all'indirizzo email dell'account.")
    .Produces<EventCheckInResponse>(StatusCodes.Status200OK)
    .Produces<EventCheckInResponse>(StatusCodes.Status400BadRequest)
    .Produces<EventCheckInResponse>(StatusCodes.Status409Conflict)
    .RequireRateLimiting("event-email-confirmation");

app.MapPost("/api/checkin/events/{eventId:int}/self", async (
    int eventId,
    SelfCheckInRequest request,
    ClaimsPrincipal user,
    EventCheckInService checkIn,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
    {
        return Results.Unauthorized();
    }

    var checkedIn = await checkIn.CheckInAuthenticatedUserAsync(eventId, userId, request.Signature, cancellationToken);
    if (checkedIn)
    {
        logger.LogInformation("Self check-in completed: user {UserId}, event {EventId}.", userId, eventId);
    }
    else
    {
        logger.LogWarning("Self check-in refused: user {UserId}, event {EventId} (old QR signature, unconfirmed registration or check-in closed).", userId, eventId);
    }

    return checkedIn
        ? Results.Ok(new EventCheckInResponse(true, "Check-in completato. Benvenuto all’evento!"))
        : Results.Conflict(new EventCheckInResponse(
            false,
            "Check-in non eseguito: usa il QR code stampato più recente, serve una registrazione confermata e il check-in deve essere aperto per l’evento."));
})
    .RequireAuthorization("Participant")
    .WithName("AuthenticatedParticipantCheckIn")
    .WithSummary("Completa il check-in dell'utente autenticato per un evento")
    .WithDescription("Richiede la firma letta dal QR stampato (cambia a ogni stampa) e registra solo l'account autenticato con registrazione confermata.")
    .Produces<EventCheckInResponse>(StatusCodes.Status200OK)
    .Produces<EventCheckInResponse>(StatusCodes.Status401Unauthorized)
    .Produces<EventCheckInResponse>(StatusCodes.Status409Conflict);

app.MapPost("/api/checkin/{eventToken:guid}", async (
    Guid eventToken,
    EventCheckInRequest request,
    EventCheckInService checkIn,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await checkIn.RequestSelfCheckInAsync(
            eventToken,
            request.Email,
            request.IsEatingPizza,
            request.LactoseIntolerant,
            cancellationToken);
        return result.Ok ? Results.Ok(result) : Results.Conflict(result);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new EventCheckInResponse(false, exception.Message));
    }
})
    .WithName("RequestEventSelfCheckIn")
    .WithSummary("Richiede il check-in self-service di un partecipante")
    .WithDescription("Invia un link all'indirizzo email del partecipante. Il check-in si completa solo dopo la conferma esplicita.")
    .Produces<EventCheckInResponse>(StatusCodes.Status200OK)
    .Produces<EventCheckInResponse>(StatusCodes.Status400BadRequest)
    .Produces<EventCheckInResponse>(StatusCodes.Status409Conflict)
    .RequireRateLimiting("event-email-confirmation");

app.MapPost("/api/checkin/confirm", async (
    EventConfirmationRequest request,
    HttpContext context,
    EventCheckInService checkIn,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var result = await checkIn.ConfirmAsync(request.Token, cancellationToken);
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
})
    .WithName("ConfirmEventRegistrationOrCheckIn")
    .WithSummary("Conferma tramite token email un'iscrizione o un check-in")
    .WithDescription("È un'operazione POST esplicita: il semplice caricamento del link email non modifica i dati.")
    .Produces<EventCheckInResponse>(StatusCodes.Status200OK)
    .Produces<EventCheckInResponse>(StatusCodes.Status400BadRequest);

app.MapDelete("/api/events/{eventId:int}/register", async (
    int eventId,
    ClaimsPrincipal user,
    EventCatalogService eventCatalog,
    UserAuthenticationService authentication,
    CancellationToken cancellationToken) =>
{
    if ((await eventCatalog.GetAllAsync(cancellationToken)).FirstOrDefault(item => item.Id == eventId)?.IsLocked == true)
    {
        return Results.Conflict(new { message = "Le modifiche alle iscrizioni di questo evento sono state bloccate dall'organizzazione." });
    }

    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
    {
        return Results.Unauthorized();
    }

    var cancelled = await authentication.CancelEventRegistrationAsync(userId, eventId, cancellationToken);
    return cancelled
        ? Results.Ok(new { message = "Iscrizione annullata. Puoi registrarti nuovamente finché l'evento è aperto." })
        : Results.Conflict(new
        {
            message = "Non è possibile annullare questa iscrizione: potrebbe essere già annullata, l'evento è passato oppure la partecipazione è già stata confermata."
        });
}).RequireAuthorization("Participant");

app.MapGet("/api/account/profile", async (
    ClaimsPrincipal user,
    UserAuthenticationService authentication,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
    {
        return Results.Unauthorized();
    }

    var profile = await authentication.GetProfileAsync(userId, cancellationToken);
    return profile is null
        ? Results.Unauthorized()
        : Results.Ok(new
        {
            firstName = profile.FirstName,
            lastName = profile.LastName,
            email = profile.Email,
            allowEventNotifications = profile.AllowEventNotifications
        });
}).RequireAuthorization("Participant");

app.MapPut("/api/account/notifications", async (
    SetNotificationsRequest request,
    ClaimsPrincipal user,
    UserAuthenticationService authentication,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
    {
        return Results.Unauthorized();
    }

    return await authentication.SetEventNotificationsAsync(userId, request.Allow, cancellationToken)
        ? Results.Ok(new { message = request.Allow ? "Comunicazioni sugli eventi attivate." : "Non riceverai più comunicazioni sugli eventi." })
        : Results.NotFound(new { message = "Account non trovato." });
}).RequireAuthorization("Participant");

app.MapDelete("/api/account", async (
    HttpContext context,
    ClaimsPrincipal user,
    UserAuthenticationService authentication,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
    {
        return Results.Unauthorized();
    }

    var outcome = await authentication.DeleteAccountAsync(userId, cancellationToken);
    if (outcome == AccountDeletionOutcome.NotFound)
    {
        return Results.NotFound(new { message = "Account non trovato." });
    }

    logger.LogInformation("User account {UserId} deleted ({Outcome}).", userId, outcome);
    await context.SignOutAsync("EventsCookie");
    return Results.Ok(new
    {
        message = outcome == AccountDeletionOutcome.Deleted
            ? "Il tuo account e i tuoi dati sono stati cancellati."
            : "Il tuo account è stato cancellato: nome ed email sono stati rimossi; resta solo la tua partecipazione agli eventi, in forma anonima."
    });
}).RequireAuthorization("Participant");

app.MapPut("/api/events/{eventId:int}/preferences", async (
    int eventId,
    RegisterEventRequest request,
    ClaimsPrincipal user,
    UserAuthenticationService authentication,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
    {
        return Results.Unauthorized();
    }

    var updated = await authentication.UpdatePreferencesAsync(
        userId,
        eventId,
        request.IsEatingPizza,
        request.LactoseIntolerant,
        cancellationToken);
    return updated
        ? Results.Ok(new { message = "Preferenze aggiornate." })
        : Results.Conflict(new { message = "Non è possibile modificare le preferenze: l'evento potrebbe essere passato, cancellato o bloccato dall'organizzazione." });
}).RequireAuthorization("Participant");

app.MapGet("/media/{id:int}", async (
    int id,
    MediaAssetService media,
    CancellationToken cancellationToken) =>
{
    var asset = await media.GetPublishedAssetAsync(id, cancellationToken);
    if (asset is null || asset.Content.Length > 8 * 1024 * 1024 ||
        asset.ContentType is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif"))
    {
        return Results.NotFound();
    }

    return Results.File(asset.Content, asset.ContentType, asset.FileName);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(EventAppHost).Assembly);

app.Run();

public partial class Program;

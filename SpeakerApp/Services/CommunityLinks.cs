namespace SpeakerApp.Services;

/// <summary>Builds absolute links to the public community site (Application:PublicBaseUrl).</summary>
public sealed class CommunityLinks(IConfiguration configuration) : ISiteLinks
{
    private readonly Uri baseUri = new(
        configuration["Application:PublicBaseUrl"] is { Length: > 0 } value
            ? (value.EndsWith('/') ? value : value + "/")
            : "https://localhost:5003/");

    public string Url(string path) => new Uri(baseUri, path.TrimStart('/')).AbsoluteUri;

    /// <summary>Absolute link to an event page on the public site; <paramref name="suffix"/> is e.g. "/totem" or "#agenda".</summary>
    public string EventUrl(int year, string moniker, string suffix = "") =>
        new Uri(baseUri, $"events/{year}/{Uri.EscapeDataString(moniker)}").AbsoluteUri + suffix;

    public Type? TopbarComponent => typeof(SpeakerApp.Components.Layout.AccountTopbar);

    public bool ShowRegistration => false;

    public string Home => Url("");

    public string Account(int? eventId = null) => Url(eventId is int id ? $"account?eventId={id}" : "account");

    public string EventPage(int year, string moniker, string suffix = "") => EventUrl(year, moniker, suffix);

    // La scheda pubblica del relatore sta sul sito pubblico.
    public string SpeakerProfile(string publicProfileKey) =>
        new Uri(baseUri, "speakers/" + Uri.EscapeDataString(publicProfileKey)).AbsoluteUri;
}

namespace CommunityApp.Services;

/// <summary>Links as seen from the community site: its own pages are relative, speaker pages live in SpeakerApp.</summary>
public sealed class CommunitySiteLinks : ISiteLinks
{
    public Type? TopbarComponent => typeof(CommunityApp.Components.Layout.AccountTopbar);

    public bool ShowRegistration => true;

    public string Home => "/";

    public string Account(int? eventId = null) => eventId is int id ? $"/account?eventId={id}" : "/account";

    public string EventPage(int year, string moniker, string suffix = "") =>
        $"/events/{year}/{Uri.EscapeDataString(moniker)}{suffix}";

    public string SpeakerProfile(string publicProfileKey) => "/speakers/" + Uri.EscapeDataString(publicProfileKey);
}

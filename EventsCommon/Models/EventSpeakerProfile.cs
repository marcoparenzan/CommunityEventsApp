namespace EventsCommon.Models;

public sealed class EventSpeakerProfile
{
    public Guid Id { get; set; }
    public string SessionizeMoniker { get; set; } = string.Empty;
    public string PublicProfileKey => string.IsNullOrWhiteSpace(SessionizeMoniker)
        ? Id.ToString("D")
        : SessionizeMoniker;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Bio { get; set; } = string.Empty;
    public string TagLine { get; set; } = string.Empty;
    public string ProfilePictureUrl { get; set; } = string.Empty;
    public string LinkedInUrl { get; set; } = string.Empty;
    public string SessionizeUrl { get; set; } = string.Empty;
    public List<string> Sessions { get; set; } = [];
}

public sealed record PublicSpeakerSession(
    int Year,
    string EventMoniker,
    string EventName,
    string EventDate,
    string Title,
    string Room,
    DateTimeOffset? StartsAt);

public sealed record PublicSpeakerProfile(
    Guid Id,
    string FullName,
    string TagLine,
    string Bio,
    string ProfilePictureUrl,
    string LinkedInUrl,
    string SessionizeUrl,
    IReadOnlyList<PublicSpeakerSession> Sessions);

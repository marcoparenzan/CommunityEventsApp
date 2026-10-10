namespace EventsCommon.Models;

/// <summary>Requests an email PIN for an existing speaker account.</summary>
public sealed record RequestSpeakerPinRequest(string Email);

/// <summary>Verifies the email PIN for an existing speaker account.</summary>
public sealed record VerifySpeakerPinRequest(string Email, string Pin, bool Installed = false);

/// <summary>Confirms that the speaker PIN request was accepted.</summary>
public sealed record SpeakerPinRequestResponse(string Message);

/// <summary>Confirms that a speaker has signed in.</summary>
public sealed record SpeakerLoginResponse(string Message);

/// <summary>Returns public event details linked to a speaker account.</summary>
public sealed record SpeakerEventResponse(int Id, int Year, string Moniker, string Name, bool IsPublished);

/// <summary>Returns the authenticated speaker's profile and synchronized events.</summary>
public sealed record SpeakerAccountResponse(
    string FirstName,
    string LastName,
    string Email,
    IReadOnlyList<SpeakerEventResponse> Events,
    string? PublicProfileKey = null);

/// <summary>Returns a session assigned to the authenticated speaker.</summary>
public sealed record SpeakerSessionResponse(
    int EventId,
    string EventName,
    string EventDate,
    string SessionizeSessionId,
    string Title,
    string Room,
    DateTimeOffset StartsAt,
    string PresentationUrl,
    string RepositoryUrl);

/// <summary>Updates slide and repository links for one of the speaker's sessions.</summary>
public sealed record UpdateSpeakerSessionLinksRequest(
    int EventId,
    string SessionizeSessionId,
    string PresentationUrl,
    string RepositoryUrl);

/// <summary>Updates resource links for a session as a superuser.</summary>
public sealed record UpdateSessionResourcesRequest(
    string SessionizeSessionId,
    string VideoUrl,
    string PresentationUrl,
    string RepositoryUrl);

/// <summary>Represents a speaker account and its administrative permissions.</summary>
public sealed record SpeakerPermissionResponse(
    int Id,
    string FirstName,
    string LastName,
    string Email,
    bool IsSuperUser,
    bool CanCheckIn,
    string SessionizeName,
    DateTime? LastEventDate = null,
    string Cellulare = "");

/// <summary>Edits the identity fields of a speaker account.</summary>
public sealed record UpdateSpeakerRequest(
    string FirstName,
    string LastName,
    string Email,
    string SessionizeName,
    string Cellulare = "");

/// <summary>Changes whether a speaker can manage participant check-in.</summary>
public sealed record SetSpeakerCheckInPermissionRequest(bool CanCheckIn);

namespace EventsCommon.Models;

/// <summary>Participant details submitted from the public event QR check-in page.</summary>
public sealed record EventCheckInRequest(string Email, bool IsEatingPizza, bool LactoseIntolerant);

/// <summary>Participant details submitted by an authorized event organizer.</summary>
public sealed record AddEventParticipantRequest(
    string FirstName,
    string LastName,
    string Email,
    bool IsEatingPizza,
    bool LactoseIntolerant);

/// <summary>Meal preferences submitted with an authenticated event registration.</summary>
public sealed record RegisterEventRequest(bool IsEatingPizza, bool LactoseIntolerant);

/// <summary>One-time email token used to confirm an event registration or check-in.</summary>
public sealed record EventConfirmationRequest(string Token);

/// <summary>Identifies a registered participant for organizer-assisted check-in.</summary>
public sealed record ManualCheckInRequest(int RegistrationId);

/// <summary>Public outcome of requesting or completing an email-confirmed event action.</summary>
public sealed record EventCheckInResponse(bool Ok, string Message, bool Warning = false);

/// <summary>Registration details visible to authorized check-in staff.</summary>
public sealed record EventAttendeeResponse(
    int RegistrationId,
    string Email,
    string? FirstName,
    string? LastName,
    string Status,
    bool IsEatingPizza,
    bool LactoseIntolerant,
    DateTimeOffset? CheckedInAt);

/// <summary>A person in the registry, with whether they are already registered for an event.</summary>
public sealed record PersonSearchResponse(
    int UserId,
    string Email,
    string? FirstName,
    string? LastName,
    bool IsConfirmed,
    bool IsRegistered);

/// <summary>Registers an existing registry person for an event.</summary>
public sealed record RegisterExistingParticipantRequest(
    int UserId,
    bool IsEatingPizza,
    bool LactoseIntolerant);

/// <summary>Self check-in of the signed-in participant, with the signature read from the printed QR code.</summary>
public sealed record SelfCheckInRequest(Guid Signature);

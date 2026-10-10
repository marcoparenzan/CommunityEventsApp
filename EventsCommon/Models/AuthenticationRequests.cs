namespace EventsCommon.Models;

public sealed record RequestPinRequest(
    string FirstName,
    string LastName,
    string Email,
    bool AllowEventNotifications);

public sealed record VerifyPinRequest(string Email, string Pin, bool Installed = false);

public sealed record SetNotificationsRequest(bool Allow);

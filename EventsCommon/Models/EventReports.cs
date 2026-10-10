namespace EventsCommon.Models;

/// <summary>Headline counters for one event.</summary>
public sealed record EventSummaryReport(
    int Registered,
    int AwaitingConfirmation,
    int CheckedIn,
    int Cancelled,
    int EatingPizza,
    int LactoseIntolerant,
    int Speakers,
    int SpeakersLinked,
    int Sessions,
    int SessionsWithVideo,
    int SessionsWithSlides,
    int SessionsWithRepository);

/// <summary>One active session of an event, with its resource links status.</summary>
public sealed record ReportSessionRow(
    string Title,
    string Room,
    DateTimeOffset StartsAt,
    string Speakers,
    bool HasVideo,
    bool HasSlides,
    bool HasRepository);

/// <summary>One synchronized speaker of an event.</summary>
public sealed record ReportSpeakerRow(
    string Name,
    string AccountEmail,
    int Sessions);

/// <summary>A person who can receive a message.</summary>
public sealed record MailRecipient(string Email, string Name);

/// <summary>Outcome of a bulk send.</summary>
public sealed record MailSendResult(int Sent, int Failed);

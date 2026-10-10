namespace EventsCommon.Services;

/// <summary>
/// Links that depend on which application hosts a shared page: the community site and the
/// speaker site are separate applications, so some destinations are relative and others absolute.
/// </summary>
public interface ISiteLinks
{
    /// <summary>Home of the public community site.</summary>
    string Home { get; }

    /// <summary>Personal area of the community site, optionally preselecting an event.</summary>
    string Account(int? eventId = null);

    /// <summary>Public page of an event (optionally with a suffix such as "/totem" or "#agenda").</summary>
    string EventPage(int year, string moniker, string suffix = "");

    /// <summary>Public page of a speaker.</summary>
    string SpeakerProfile(string publicProfileKey);

    /// <summary>
    /// Component rendered at the top of shared pages in place of the public header navigation
    /// (null: the page shows its own public navigation).
    /// </summary>
    Type? TopbarComponent { get; }

    /// <summary>Whether shared pages offer the public registration button.</summary>
    bool ShowRegistration { get; }
}

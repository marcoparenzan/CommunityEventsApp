namespace EventsCommon.Models;

/// <summary>Headline numbers of an event (participants not cancelled, confirmed, awaiting, checked in, with pizza, lactose intolerant; speakers and speaker dinner).</summary>
public sealed record EventNumbers(
    int Registered, int Confirmed, int AwaitingConfirmation, int CheckedIn, int WithPizza, int LactoseIntolerant,
    int Speakers, int SpeakersAtDinner, int SpeakerDinnerGuests);

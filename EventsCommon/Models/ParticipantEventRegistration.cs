namespace EventsCommon.Models;

public sealed class ParticipantEventRegistration
{
    public int EventId { get; set; }
    public int EventYear { get; set; }
    public string EventMoniker { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public DateOnly EventDate { get; set; }
    public string Status { get; set; } = "Registered";
    public bool IsEatingPizza { get; set; }
    public bool LactoseIntolerant { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? CheckedInAt { get; set; }
}

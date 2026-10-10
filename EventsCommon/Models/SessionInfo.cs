using System.Text.Json.Serialization;

namespace EventsCommon.Models;

public partial class RoomDescription
{
    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("rooms")]
    public List<RoomDescriptionRoom> Rooms { get; set; }

    [JsonPropertyName("timeSlots")]
    public List<TimeSlot> TimeSlots { get; set; }
}

public partial class RoomDescriptionRoom
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("sessions")]
    public List<Session> Sessions { get; set; }

    [JsonPropertyName("hasOnlyPlenumSessions")]
    public bool HasOnlyPlenumSessions { get; set; }
}

public partial class Session
{
    [JsonPropertyName("id")]
    public object Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("startsAt")]
    public DateTimeOffset StartsAt { get; set; }

    [JsonPropertyName("endsAt")]
    public DateTimeOffset EndsAt { get; set; }

    [JsonPropertyName("isServiceSession")]
    public bool IsServiceSession { get; set; }

    [JsonPropertyName("isPlenumSession")]
    public bool IsPlenumSession { get; set; }

    [JsonPropertyName("speakers")]
    public List<Speaker> Speakers { get; set; }

    [JsonPropertyName("categories")]
    public List<object> Categories { get; set; }

    [JsonPropertyName("roomId")]
    public long RoomId { get; set; }

    [JsonPropertyName("room")]
    public string Room { get; set; }

    [JsonPropertyName("liveUrl")]
    public object LiveUrl { get; set; }

    [JsonPropertyName("recordingUrl")]
    public object RecordingUrl { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("videoUrl")]
    public string VideoUrl { get; set; }

    [JsonPropertyName("presentationUrl")]
    public string PresentationUrl { get; set; }

    [JsonPropertyName("repositoryUrl")]
    public string RepositoryUrl { get; set; }

    [JsonPropertyName("isInformed")]
    public bool IsInformed { get; set; }

    [JsonPropertyName("isConfirmed")]
    public bool IsConfirmed { get; set; }
}

public partial class Speaker
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }
}

public partial class TimeSlot
{
    [JsonPropertyName("slotStart")]
    public string SlotStart { get; set; }

    [JsonPropertyName("rooms")]
    public List<TimeSlotRoom> Rooms { get; set; }
}

public partial class TimeSlotRoom
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("session")]
    public Session Session { get; set; }

    [JsonPropertyName("index")]
    public long Index { get; set; }
}

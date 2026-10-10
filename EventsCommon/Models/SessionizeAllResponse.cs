using System.Text.Json.Serialization;

namespace EventsCommon.Models;

public sealed class SessionizeAllResponse
{
    [JsonPropertyName("sessions")]
    public List<SessionizeAllSession> Sessions { get; set; } = [];

    [JsonPropertyName("speakers")]
    public List<SessionizeAllSpeaker> Speakers { get; set; } = [];

    [JsonPropertyName("rooms")]
    public List<SessionizeAllRoom> Rooms { get; set; } = [];
}

public sealed class SessionizeAllSession
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("startsAt")]
    public DateTimeOffset StartsAt { get; set; }

    [JsonPropertyName("endsAt")]
    public DateTimeOffset EndsAt { get; set; }

    [JsonPropertyName("isServiceSession")]
    public bool IsServiceSession { get; set; }

    [JsonPropertyName("speakers")]
    public List<string> Speakers { get; set; } = [];

    [JsonPropertyName("roomId")]
    public long RoomId { get; set; }
}

public sealed class SessionizeAllSpeaker
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;
}

public sealed class SessionizeAllRoom
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

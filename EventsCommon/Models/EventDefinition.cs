namespace EventsCommon.Models;

public sealed class EventDefinition
{
    public int Id { get; set; }
    public Guid CheckInToken { get; set; }
    public int Year { get; set; }
    public string Moniker { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }
    public bool IsPublished { get; set; }
    public bool IsCancelled { get; set; }

    /// <summary>When true participants cannot register, cancel or change their preferences.</summary>
    public bool IsLocked { get; set; }
    /// <summary>Invite link of the speakers' WhatsApp group for the event (empty when there is none).</summary>
    public string SpeakerWhatsAppUrl { get; set; } = string.Empty;

    /// <summary>When true speakers are asked for their T-shirt size.</summary>
    public bool CollectsTShirtSizes { get; set; }
    public string Name { get; set; } = string.Empty;
    public string EventDate { get; set; } = string.Empty;
    public string CallForPapersOpenAt { get; set; } = string.Empty;
    public string CallForPapersCloseAt { get; set; } = string.Empty;
    public string CallForPapersUrl { get; set; } = string.Empty;
    public string SessionizeUrl { get; set; } = string.Empty;
    public string SessionizeJsonUri { get; set; } = string.Empty;
    /// <summary>Rich text (HTML) description of the event.</summary>
    public string Description { get; set; } = string.Empty;
}

public sealed class EventSponsor
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string LogoUrl { get; set; } = string.Empty;
    public string Tier { get; set; } = "Partner";
    public int MediaAssetId { get; set; }
}

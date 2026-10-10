using System.Text.Json.Serialization;

namespace EventsCommon.Models;

public partial class SpeakerInfo
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("firstName")]
    public string FirstName { get; set; }

    [JsonPropertyName("lastName")]
    public string LastName { get; set; }

    [JsonPropertyName("fullName")]
    public string FullName { get; set; }

    [JsonPropertyName("bio")]
    public string Bio { get; set; }

    [JsonPropertyName("tagLine")]
    public string TagLine { get; set; }

    [JsonPropertyName("profilePicture")]
    public Uri ProfilePicture { get; set; }

    [JsonPropertyName("sessions")]
    public List<SpeakerSession> Sessions { get; set; }

    [JsonPropertyName("isTopSpeaker")]
    public bool IsTopSpeaker { get; set; }

    [JsonPropertyName("links")]
    public List<Link> Links { get; set; }

    [JsonPropertyName("questionAnswers")]
    public List<object> QuestionAnswers { get; set; }

    [JsonPropertyName("categories")]
    public List<object> Categories { get; set; }

    public string SpeakerUrl()
    {
        try
        {
            if (this.Links is null || !this.Links.Any())
                return string.Empty;

            // Priorità assoluta a Sessionize
            var link = this.Links.FirstOrDefault(xx => xx.LinkType == "Sessionize");
            if (link is null) link = this.Links.FirstOrDefault(xx => xx.LinkType == "LinkedIn");
            if (link is null) link = this.Links.FirstOrDefault(xx => xx.LinkType == "Twitter");
            if (link is null) link = this.Links.FirstOrDefault(xx => xx.LinkType == "XTwitter");
            if (link is null) link = this.Links.FirstOrDefault();

            if (link is null || string.IsNullOrWhiteSpace(link.Url))
                return string.Empty;

            return link.Url;
        }
        catch
        {
            return string.Empty;
        }
    }

    public bool HasValidUrl()
    {
        return !string.IsNullOrWhiteSpace(SpeakerUrl());
    }

}

public partial class Link
{
    [JsonPropertyName("title")]
    public string Title { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; }

    [JsonPropertyName("linkType")]
    public string LinkType { get; set; }
}

public enum LinkType { Blog, Facebook, Instagram, LinkedIn, Sessionize, Twitter, XTwitter };

public partial class SpeakerSession
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }
}
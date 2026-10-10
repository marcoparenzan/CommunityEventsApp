using System.Net;
using Ganss.Xss;

namespace EventsCommon.Services;

/// <summary>Sanitizes and renders the rich text (HTML) descriptions written in the admin editor.</summary>
public static class RichText
{
    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        foreach (var tag in new[] { "p", "br", "strong", "b", "em", "i", "u", "s", "a", "ul", "ol", "li", "h2", "h3", "h4", "blockquote" })
        {
            sanitizer.AllowedTags.Add(tag);
        }

        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.Add("href");
        sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto" })
        {
            sanitizer.AllowedSchemes.Add(scheme);
        }

        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Html.Dom.IHtmlAnchorElement anchor)
            {
                anchor.SetAttribute("target", "_blank");
                anchor.SetAttribute("rel", "noopener noreferrer");
            }
        };
        return sanitizer;
    }

    /// <summary>Safe HTML for a stored description (plain text from older events is turned into paragraphs).</summary>
    public static string ToHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (!value.TrimStart().StartsWith('<'))
        {
            value = string.Concat(value
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => $"<p>{WebUtility.HtmlEncode(line)}</p>"));
        }

        return Sanitizer.Sanitize(value);
    }
}

using System.Text.Json.Serialization;

namespace Tuvima.WikidataReconciliation.Internal.Json;

internal sealed class WbGetEntitiesResponse
{
    [JsonPropertyName("entities")]
    public Dictionary<string, WikidataEntity>? Entities { get; set; }

    [JsonPropertyName("success")]
    public int Success { get; set; }
}

internal sealed class WikidataEntity
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("labels")]
    public Dictionary<string, LanguageValue>? Labels { get; set; }

    [JsonPropertyName("descriptions")]
    public Dictionary<string, LanguageValue>? Descriptions { get; set; }

    [JsonPropertyName("aliases")]
    public Dictionary<string, List<LanguageValue>>? Aliases { get; set; }

    [JsonPropertyName("claims")]
    public Dictionary<string, List<Claim>>? Claims { get; set; }

    [JsonPropertyName("sitelinks")]
    public Dictionary<string, SiteLink>? Sitelinks { get; set; }

    [JsonPropertyName("lastrevid")]
    public long LastRevId { get; set; }

    [JsonPropertyName("modified")]
    public string? Modified { get; set; }
}

internal sealed class LanguageValue
{
    [JsonPropertyName("language")]
    public string Language { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

internal sealed class Claim
{
    [JsonPropertyName("mainsnak")]
    public Snak? MainSnak { get; set; }

    [JsonPropertyName("rank")]
    public string? Rank { get; set; }

    [JsonPropertyName("qualifiers")]
    public Dictionary<string, List<Snak>>? Qualifiers { get; set; }

    [JsonPropertyName("qualifiers-order")]
    public List<string>? QualifiersOrder { get; set; }
}

internal sealed class SiteLink
{
    [JsonPropertyName("site")]
    public string Site { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";
}

internal sealed class Snak
{
    [JsonPropertyName("snaktype")]
    public string? SnakType { get; set; }

    [JsonPropertyName("property")]
    public string? Property { get; set; }

    [JsonPropertyName("datavalue")]
    public DataValue? DataValue { get; set; }

    [JsonPropertyName("datatype")]
    public string? DataType { get; set; }
}

internal sealed class DataValue
{
    [JsonPropertyName("value")]
    public object? Value { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

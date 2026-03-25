using System.Globalization;
using System.Text.Json;
using Tuvima.WikidataReconciliation.Internal.Json;

namespace Tuvima.WikidataReconciliation.Internal;

/// <summary>
/// Maps internal JSON DTOs to public models.
/// </summary>
internal static class EntityMapper
{
    public static WikidataEntityInfo MapEntity(WikidataEntity entity, string language)
    {
        LanguageFallback.TryGetValue(entity.Labels, language, out var label);
        LanguageFallback.TryGetValue(entity.Descriptions, language, out var description);

        var aliases = new List<string>();
        if (entity.Aliases != null)
        {
            // Try requested language first, then fallback languages
            var langChain = LanguageFallback.GetFallbackChain(language);
            foreach (var lang in langChain)
            {
                if (entity.Aliases.TryGetValue(lang, out var aliasList))
                {
                    foreach (var a in aliasList)
                    {
                        if (!string.IsNullOrEmpty(a.Value))
                            aliases.Add(a.Value);
                    }
                    break; // Use first language that has aliases
                }
            }
        }

        DateTimeOffset? modified = null;
        if (!string.IsNullOrEmpty(entity.Modified) && DateTimeOffset.TryParse(entity.Modified, out var parsedModified))
            modified = parsedModified;

        return new WikidataEntityInfo
        {
            Id = entity.Id,
            Label = string.IsNullOrEmpty(label) ? null : label,
            Description = string.IsNullOrEmpty(description) ? null : description,
            Aliases = aliases,
            Claims = MapClaims(entity.Claims),
            LastRevisionId = entity.LastRevId,
            Modified = modified
        };
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>> MapClaims(
        Dictionary<string, List<Claim>>? claims)
    {
        if (claims is null or { Count: 0 })
            return new Dictionary<string, IReadOnlyList<WikidataClaim>>();

        var result = new Dictionary<string, IReadOnlyList<WikidataClaim>>(claims.Count);

        foreach (var (propertyId, claimList) in claims)
        {
            var mapped = new List<WikidataClaim>(claimList.Count);
            foreach (var claim in claimList)
            {
                mapped.Add(new WikidataClaim
                {
                    PropertyId = propertyId,
                    Rank = claim.Rank ?? "normal",
                    Value = claim.MainSnak?.SnakType == "value" && claim.MainSnak.DataValue != null
                        ? MapDataValue(claim.MainSnak.DataValue, claim.MainSnak.DataType)
                        : null,
                    Qualifiers = MapQualifiers(claim.Qualifiers),
                    QualifierOrder = claim.QualifiersOrder ?? []
                });
            }
            result[propertyId] = mapped;
        }

        return result;
    }

    public static WikidataValue MapDataValue(DataValue dataValue, string? dataType)
    {
        if (dataValue.Value is not JsonElement element)
        {
            return new WikidataValue
            {
                Kind = WikidataValueKind.Unknown,
                RawValue = dataValue.Value?.ToString() ?? ""
            };
        }

        return dataType switch
        {
            "wikibase-item" or "wikibase-property" => MapEntityIdValue(element),
            "time" => MapTimeValue(element),
            "quantity" => MapQuantityValue(element),
            "globe-coordinate" => MapCoordinateValue(element),
            "monolingualtext" => MapMonolingualTextValue(element),
            "string" or "external-id" or "url" or "commonsMedia" or "math" or "musical-notation"
                => MapStringValue(element, dataType),
            _ => MapStringValue(element, dataType)
        };
    }

    private static WikidataValue MapEntityIdValue(JsonElement element)
    {
        var id = "";
        if (element.TryGetProperty("id", out var idProp))
            id = idProp.GetString() ?? "";

        return new WikidataValue
        {
            Kind = WikidataValueKind.EntityId,
            RawValue = id,
            EntityId = id
        };
    }

    private static WikidataValue MapTimeValue(JsonElement element)
    {
        var time = "";
        int? precision = null;

        if (element.TryGetProperty("time", out var timeProp))
            time = timeProp.GetString() ?? "";
        if (element.TryGetProperty("precision", out var precProp))
            precision = precProp.GetInt32();

        return new WikidataValue
        {
            Kind = WikidataValueKind.Time,
            RawValue = time,
            TimePrecision = precision
        };
    }

    private static WikidataValue MapQuantityValue(JsonElement element)
    {
        var amountStr = "";
        decimal? amount = null;
        string? unit = null;

        if (element.TryGetProperty("amount", out var amountProp))
        {
            amountStr = amountProp.GetString() ?? "";
            if (decimal.TryParse(amountStr.TrimStart('+'), CultureInfo.InvariantCulture, out var parsed))
                amount = parsed;
        }

        if (element.TryGetProperty("unit", out var unitProp))
        {
            var unitStr = unitProp.GetString();
            if (unitStr != "1") // "1" means dimensionless
                unit = unitStr;
        }

        return new WikidataValue
        {
            Kind = WikidataValueKind.Quantity,
            RawValue = amountStr.TrimStart('+'),
            Amount = amount,
            Unit = unit
        };
    }

    private static WikidataValue MapCoordinateValue(JsonElement element)
    {
        double? lat = null, lon = null;

        if (element.TryGetProperty("latitude", out var latProp))
            lat = latProp.GetDouble();
        if (element.TryGetProperty("longitude", out var lonProp))
            lon = lonProp.GetDouble();

        var raw = lat.HasValue && lon.HasValue
            ? $"{lat.Value.ToString(CultureInfo.InvariantCulture)},{lon.Value.ToString(CultureInfo.InvariantCulture)}"
            : "";

        return new WikidataValue
        {
            Kind = WikidataValueKind.GlobeCoordinate,
            RawValue = raw,
            Latitude = lat,
            Longitude = lon
        };
    }

    private static WikidataValue MapMonolingualTextValue(JsonElement element)
    {
        var text = "";
        string? language = null;

        if (element.TryGetProperty("text", out var textProp))
            text = textProp.GetString() ?? "";
        if (element.TryGetProperty("language", out var langProp))
            language = langProp.GetString();

        return new WikidataValue
        {
            Kind = WikidataValueKind.MonolingualText,
            RawValue = text,
            Language = language
        };
    }

    private static WikidataValue MapStringValue(JsonElement element, string? dataType)
    {
        var value = element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? ""
            : element.ToString();

        return new WikidataValue
        {
            Kind = WikidataValueKind.String,
            RawValue = value
        };
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<WikidataValue>> MapQualifiers(
        Dictionary<string, List<Snak>>? qualifiers)
    {
        if (qualifiers is null or { Count: 0 })
            return new Dictionary<string, IReadOnlyList<WikidataValue>>();

        var result = new Dictionary<string, IReadOnlyList<WikidataValue>>(qualifiers.Count);

        foreach (var (propertyId, snaks) in qualifiers)
        {
            var values = new List<WikidataValue>();
            foreach (var snak in snaks)
            {
                if (snak.SnakType == "value" && snak.DataValue != null)
                    values.Add(MapDataValue(snak.DataValue, snak.DataType));
            }
            if (values.Count > 0)
                result[propertyId] = values;
        }

        return result;
    }
}

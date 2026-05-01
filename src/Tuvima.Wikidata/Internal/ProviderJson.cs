using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Tuvima.Wikidata.Internal;

internal static class ProviderJson
{
    public static T? Deserialize<T>(string json, JsonTypeInfo<T> jsonTypeInfo, string endpoint)
    {
        try
        {
            return JsonSerializer.Deserialize(json, jsonTypeInfo);
        }
        catch (JsonException ex)
        {
            throw new WikidataProviderException(
                WikidataFailureKind.MalformedResponse,
                $"The provider returned malformed JSON for {endpoint}.",
                innerException: ex);
        }
    }
}

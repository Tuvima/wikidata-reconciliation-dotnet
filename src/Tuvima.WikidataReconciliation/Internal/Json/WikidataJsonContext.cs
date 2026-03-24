using System.Text.Json.Serialization;

namespace Tuvima.WikidataReconciliation.Internal.Json;

[JsonSerializable(typeof(WbSearchEntitiesResponse))]
[JsonSerializable(typeof(QuerySearchResponse))]
[JsonSerializable(typeof(WbGetEntitiesResponse))]
[JsonSerializable(typeof(RecentChangesResponse))]
[JsonSerializable(typeof(WikipediaSummaryResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class WikidataJsonContext : JsonSerializerContext;

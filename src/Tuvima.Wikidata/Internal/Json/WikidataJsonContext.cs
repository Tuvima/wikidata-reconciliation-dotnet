using System.Text.Json.Serialization;

namespace Tuvima.Wikidata.Internal.Json;

[JsonSerializable(typeof(WbSearchEntitiesResponse))]
[JsonSerializable(typeof(QuerySearchResponse))]
[JsonSerializable(typeof(WbGetEntitiesResponse))]
[JsonSerializable(typeof(RecentChangesResponse))]
[JsonSerializable(typeof(WikipediaSummaryResponse))]
[JsonSerializable(typeof(WikipediaSummaryBatchResponse))]
[JsonSerializable(typeof(RevisionQueryResponse))]
[JsonSerializable(typeof(ParseResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class WikidataJsonContext : JsonSerializerContext;

using System.Text.Json;

namespace Tuvima.Wikidata.Tests;

internal static class TestPayloads
{
    public static WikidataReconciler CreateReconciler(TestHttpMessageHandler handler)
    {
        return new WikidataReconciler(CreateHttpClient(handler), new WikidataReconcilerOptions
        {
            UserAgent = "Tuvima.Wikidata.Tests/2.5 (https://github.com/Tuvima/wikidata)"
        });
    }

    public static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Tuvima.Wikidata.Tests/2.5 (https://github.com/Tuvima/wikidata)");
        return client;
    }

    public static string SearchResponse(params (string Id, string Label)[] results)
    {
        return JsonSerializer.Serialize(new
        {
            success = 1,
            search = results.Select(r => new { id = r.Id, label = r.Label }).ToList()
        });
    }

    public static string QueryResponse(params string[] titles)
    {
        return JsonSerializer.Serialize(new
        {
            query = new
            {
                search = titles.Select(title => new { title }).ToList()
            }
        });
    }

    public static string RecentChangesResponse(
        IEnumerable<object> entries,
        string? continueToken = null)
    {
        return JsonSerializer.Serialize(new
        {
            query = new
            {
                recentchanges = entries.ToList()
            },
            @continue = continueToken is null ? null : new
            {
                rccontinue = continueToken
            }
        });
    }

    public static string EntityResponse(params Dictionary<string, object?>[] entities)
    {
        return JsonSerializer.Serialize(new
        {
            success = 1,
            entities = entities.ToDictionary(
                entity => (string)entity["id"]!,
                entity => entity)
        });
    }

    public static Dictionary<string, object?> Entity(
        string id,
        string label,
        Dictionary<string, object>? claims = null,
        Dictionary<string, object>? sitelinks = null)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["type"] = "item",
            ["labels"] = Labels(("en", label)),
            ["claims"] = claims,
            ["sitelinks"] = sitelinks
        };
    }

    public static Dictionary<string, object> Labels(params (string Language, string Value)[] values)
    {
        return values.ToDictionary(
            value => value.Language,
            value => (object)new
            {
                language = value.Language,
                value = value.Value
            });
    }

    public static Dictionary<string, object> Sitelinks(params (string Site, string Title)[] values)
    {
        return values.ToDictionary(
            value => value.Site,
            value => (object)new
            {
                site = value.Site,
                title = value.Title
            });
    }

    public static Dictionary<string, object> Claims(
        params (string PropertyId, string DataType, object DataValue, string Rank)[] values)
    {
        return values
            .GroupBy(value => value.PropertyId)
            .ToDictionary(
                group => group.Key,
                group => (object)group.Select(value => new
                {
                    mainsnak = new
                    {
                        snaktype = "value",
                        property = value.PropertyId,
                        datavalue = value.DataValue,
                        datatype = value.DataType
                    },
                    rank = value.Rank
                }).ToList());
    }

    public static object ItemDataValue(string qid)
        => new
        {
            value = new { id = qid },
            type = "wikibase-entityid"
        };

    public static object StringDataValue(string value)
        => new
        {
            value,
            type = "string"
        };
}

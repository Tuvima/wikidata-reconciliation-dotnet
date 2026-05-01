namespace Tuvima.Wikidata;

/// <summary>
/// Controls whether bridge resolution should return the matched entity directly,
/// the canonical work behind an edition/release, or both.
/// </summary>
public enum BridgeRollupTarget
{
    ResolvedEntity = 0,
    PreferCanonicalWork,
    PreferEdition,
    ReturnWorkAndEdition
}

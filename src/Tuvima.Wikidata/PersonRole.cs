namespace Tuvima.Wikidata;

/// <summary>
/// The role a person plays relative to a work, used by <see cref="Services.PersonsService.SearchAsync"/>
/// to pick the right occupation filter and to decide whether musical groups should be included.
/// </summary>
public enum PersonRole
{
    /// <summary>Unspecified role. No occupation filter is applied.</summary>
    Unknown = 0,

    /// <summary>Author / writer of a literary work. Maps to Q36180 (writer), Q4853732 (author).</summary>
    Author = 1,

    /// <summary>Narrator (typically for audiobooks). Maps to Q1622272 (voice actor), Q10800557 (audiobook narrator).</summary>
    Narrator = 2,

    /// <summary>Film / TV director. Maps to Q2526255 (film director), Q2722764 (TV director).</summary>
    Director = 3,

    /// <summary>Screen actor. Maps to Q33999 (actor), Q10800557 (voice actor).</summary>
    Actor = 4,

    /// <summary>Voice actor specifically. Maps to Q1622272 (voice actor).</summary>
    VoiceActor = 5,

    /// <summary>Composer. Maps to Q36834 (composer), Q486748 (film composer).</summary>
    Composer = 6,

    /// <summary>
    /// Performer (music, theatre). Maps to Q488205 (performing artist), Q639669 (musician), Q177220 (singer).
    /// Defaults <see cref="PersonSearchRequest.IncludeMusicalGroups"/> to true.
    /// </summary>
    Performer = 7,

    /// <summary>
    /// Visual / musical artist. Maps to Q483501 (artist), Q639669 (musician).
    /// Defaults <see cref="PersonSearchRequest.IncludeMusicalGroups"/> to true.
    /// </summary>
    Artist = 8,

    /// <summary>Screenwriter. Maps to Q28389 (screenwriter).</summary>
    Screenwriter = 9
}

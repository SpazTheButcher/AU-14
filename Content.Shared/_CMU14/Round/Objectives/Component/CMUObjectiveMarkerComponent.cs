namespace Content.Shared._CMU14.Round.Objectives.Component;

/// <summary>
///     Marks a position for objective entities to spawn at. Used by FindMarkers/ResolveMarkers
///     in the shared ObjectiveSystem base class. Mappers place these on maps.
/// </summary>
[RegisterComponent]
public sealed partial class CMUObjectiveMarkerComponent : Robust.Shared.GameObjects.Component
{
    /// <summary>
    ///     Optional identifier; markers with a matching FetchId are preferred over generic ones.
    /// </summary>
    [DataField("fetchId")]
    public string FetchId = string.Empty;

    /// <summary>
    ///     If true, this marker can be used as a fallback when no specific marker is found.
    /// </summary>
    [DataField]
    public bool Generic;

    /// <summary>
    ///     Set to true once a spawn has consumed this marker.
    /// </summary>
    public bool Used;
}

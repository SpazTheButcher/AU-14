namespace Content.Shared._CMU14.Round.Objectives.Type;

[RegisterComponent]
public sealed partial class FetchObjectiveComponent : Robust.Shared.GameObjects.Component
{
    [DataField]
    public bool UseMarkers = true;

    [DataField]
    public bool UseAnyEntity;

    [DataField]
    public string TargetPrototype = string.Empty;

    [DataField]
    public string SpawnMarkerId = string.Empty;

    [DataField]
    public int SpawnCount = 1;

    [DataField]
    public int FetchCount = 1;

    [DataField("spawnOther")]
    public string? SpawnOther;

    [DataField("customReturnPointId")]
    public string? CustomReturnPointId;

    [DataField]
    public bool RespawnOnRepeat;

    public bool HasSpawned;
    public Dictionary<string, int> AmountFetchedPerFaction = new();
}

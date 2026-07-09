using Robust.Shared.GameStates;

namespace Content.Shared._CMU14.Round.Objectives;

[RegisterComponent, NetworkedComponent]
public sealed partial class DestroyObjectiveComponent : Component
{
    [DataField]
    public bool UseAnyEntity { get; private set; } = false;

    [DataField("spawnMarker")]
    public string SpawnMarkerId { get; private set; } = string.Empty;

    [DataField("entityToDestroy")]
    public string TargetPrototype { get; private set; } = string.Empty;

    [DataField("amountToSpawn")]
    public int SpawnCount { get; private set; } = 1;

    [DataField("amountToDestroy")]
    public int DestroyCount { get; private set; } = 1;

    public int DestroyedCount = 0; // AmountDestroyed
    public bool HasSpawned = false; // EntitiesSpawned
}

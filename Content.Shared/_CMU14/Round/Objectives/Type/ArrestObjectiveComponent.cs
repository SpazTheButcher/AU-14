namespace Content.Shared._CMU14.Round.Objectives.Type;

[RegisterComponent]
public sealed partial class ArrestObjectiveComponent : Robust.Shared.GameObjects.Component
{
    [DataField("factiontoarrest")] public string FactionToArrest = string.Empty;
    [DataField("specificjob")] public string? SpecificJob;
    [DataField] public bool SynthOnly;
    [DataField("mobtoarrest")] public string? TargetPrototype;
    [DataField] public bool SpawnMob;
    [DataField("spawnmarker")] public string SpawnMarkerId = string.Empty;
    [DataField("amounttospawn")] public int SpawnCount = 1;
    [DataField("amounttoarrest")] public int ArrestCount = 1;
    [DataField] public bool RespawnOnRepeat;
    [DataField("removekillmark")] public bool RemoveKillMark = true;

    public bool HasSpawned;
    public Dictionary<string, int> AmountArrestedPerFaction = new();
}

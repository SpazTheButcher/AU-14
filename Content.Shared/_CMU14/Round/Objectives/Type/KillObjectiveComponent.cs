namespace Content.Shared._CMU14.Round.Objectives.Type;

[RegisterComponent]
public sealed partial class KillObjectiveComponent : Robust.Shared.GameObjects.Component
{
    [DataField("mobtokill")] public string? TargetPrototype;
    [DataField("specificjob")] public string? SpecificJob;
    [DataField] public bool SynthOnly;
    [DataField("spawnmob")] public bool SpawnMob;
    [DataField("spawnmarker")] public string SpawnMarkerId = string.Empty;
    [DataField("amounttospawn")] public int SpawnCount = 1;
    [DataField("factiontokill")] public string FactionToKill = string.Empty;
    [DataField("amounttokill")] public int KillCount = 1;
    [DataField] public bool RespawnOnRepeat;
    [DataField("countarrest")] public bool CountArrest = true;

    public bool HasSpawned;
    public Dictionary<string, int> AmountKilledPerFaction = new();
}

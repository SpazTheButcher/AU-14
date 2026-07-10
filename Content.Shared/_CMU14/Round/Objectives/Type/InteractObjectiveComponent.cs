namespace Content.Shared._CMU14.Round.Objectives.Type;

[RegisterComponent]
public sealed partial class InteractObjectiveComponent : Component
{
    [DataField] public List<string> Interactables { get; private set; } = new();
    [DataField] public string DoAfterMessageBegin { get; private set; } = "You begin working...";
    [DataField("doAfterMessagecomplete")] public string DoAfterMessageComplete { get; private set; } = "You finish working.";
    [DataField("spawnmarker")] public string SpawnMarkerId { get; private set; } = string.Empty;
    [DataField("amounttospawn")] public int SpawnCount { get; private set; } = 1;
    [DataField] public bool Spawn { get; private set; }
    [DataField("interactionsneeded")] public int InteractionsNeeded { get; private set; } = 1;
    [DataField("completionsperent")] public int CompletionsPerEnt { get; private set; } = 1;
    [DataField] public List<string> Skills { get; private set; } = new();
    [DataField] public List<string> Access { get; private set; } = new();
    [DataField] public List<string>? Tools { get; private set; }
    [DataField("destroyoncomplete")] public bool DestroyOnComplete { get; private set; }
    [DataField("interacttime")] public float InteractTime { get; private set; } = 4f;
    [DataField("totalcompletionsneeded")] public int TotalCompletionsNeeded { get; private set; }

    public bool HasSpawned;
    public Dictionary<string, int> CompletionsPerFaction { get; set; } = new();
}

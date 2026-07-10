namespace Content.Shared._CMU14.Round.Objectives.Type;

[RegisterComponent]
public sealed partial class InteractTrackerComponent : Component
{
    public EntityUid ObjectiveUid;
    public int CurrentInteractions;
    public Dictionary<string, int> CompletionsPerFaction { get; set; } = new();
    public Dictionary<string, int> InteractionsPerFaction { get; set; } = new();
}

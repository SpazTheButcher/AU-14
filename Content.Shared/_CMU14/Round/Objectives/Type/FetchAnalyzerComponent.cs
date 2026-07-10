namespace Content.Shared._CMU14.Round.Objectives.Type;

[RegisterComponent]
public sealed partial class FetchAnalyzerComponent : Component
{
    [DataField("faction")]
    public string Faction { get; set; } = string.Empty;

    public int CashStored;
}

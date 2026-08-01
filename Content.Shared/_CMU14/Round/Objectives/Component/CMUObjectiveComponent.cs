using Content.Shared._CMU14.Round.Objectives;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._CMU14.Round.Objectives.Component;

public sealed class ObjectiveActivatedEvent : EntityEventArgs;

public sealed class ObjectiveResetEvent : EntityEventArgs;

[RegisterComponent, NetworkedComponent]
public sealed partial class CMUObjectiveComponent : Robust.Shared.GameObjects.Component
{
    public enum ObjectiveStatus
    {
        Incomplete,
        Completed,
        Failed
    }

    public enum FinalObjectiveType
    {
        InstantWin,
        Boon
    }

    [DataField(required: true)]
    public string Id { get; private set; } = string.Empty;

    [DataField("ObjectiveDescription", required: true)]
    public string ObjectiveDescription { get; private set; } = string.Empty;

    [DataField("applicableModes", required: true)]
    public List<string> AllowedPresets { get; private set; } = new();

    [DataField("possibleFactions", required: true)]
    public List<string> Factions { get; private set; } = new();

    [DataField]
    public int MaxPlayers { get; private set; } = 200;

    [DataField]
    public int MinPlayers { get; private set; } = 0;

    [DataField]
    public List<string> BlacklistedPlatoons { get; private set; } = new();

    [DataField]
    public List<string> WhitelistedPlatoons { get; private set; } = new();

    [DataField("rollanyway")]
    public bool RollAnyway { get; private set; } = false;

    [DataField]
    public bool Repeating { get; private set; } = false;

    [DataField("maxrepeatable")]
    public int? MaxRepeatable { get; private set; } = null;

    [DataField]
    public bool FactionNeutral { get; private set; } = false;

    [DataField("objectivelevel", required: true)]
    public int ObjectiveLevel { get; private set; } = 1;

    [DataField]
    public int ObjectiveWeight { get; private set; } = 1;

    [DataField]
    public FinalObjectiveType FinalType { get; private set; } = FinalObjectiveType.InstantWin;

    [DataField("custompoints")]
    public int CustomPoints { get; private set; } = 0;

    [DataField]
    public List<string> TechUnlocks { get; private set; } = new();

    [DataField("intelTiers")]
    public List<ProtoId<ObjectiveIntelTierPrototype>> IntelTiersProtos { get; private set; } = new();

    [DataField("nextTier")]
    public EntProtoId<CMUObjectiveComponent>? NextTierObjective { get; private set; } = null;

    [DataField("intelTierPerFaction")]
    public Dictionary<string, int> DefaultIntelTiers { get; set; } = new()
    {
        { "govfor", 1 }, { "opfor", 1 }, { "clf", 1 }, { "scientist", 1 }
    };

    [DataField]
    public string? RoundEndMessage { get; private set; } = null;

    public bool Active { get; set; } = false;
    public string Faction { get; set; } = string.Empty;
    public Dictionary<string, ObjectiveStatus> StatusesPerFaction { get; set; } = new(); // factionStatuses
    public int TimesCompleted { get; set; } = 0;
}

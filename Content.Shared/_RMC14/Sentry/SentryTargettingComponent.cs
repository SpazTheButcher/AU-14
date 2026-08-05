using Content.Shared.NPC.Prototypes;
using Content.Shared.AU14.AllianceConsole;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._RMC14.Sentry;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(SharedSentryTargetingSystem))]
public sealed partial class SentryTargetingComponent : Component
{
    [DataField, AutoNetworkedField]
    public HashSet<string> FriendlyFactions = new();

    /// <summary>
    /// If populated, this sentry always uses exactly these friendly factions.
    /// Deployer assignment, multitools, and sentry laptops cannot replace this whitelist.
    /// </summary>
    [DataField, AutoNetworkedField]
    public HashSet<string> LockedFriendlyFactions = new();

    [DataField, AutoNetworkedField]
    public HashSet<string> DeployedFriendlyFactions = new();

    [DataField, AutoNetworkedField]
    public string OriginalFaction = "GOVFOR";

    [DataField, AutoNetworkedField]
    public HashSet<string> TargetedFactions = new();

    [DataField, AutoNetworkedField]
    public HashSet<string> HumanoidAdded = new();

    /// <summary>
    /// NPC factions set as friendly by the alliance console.
    /// Entities in these factions are never treated as valid targets, regardless of IFF.
    /// </summary>
    [DataField, AutoNetworkedField]
    public HashSet<ProtoId<NpcFactionPrototype>> AllianceFriendlyNpcFactions = new();

    /// <summary>
    /// Standing selected for targets that have no recognized IFF and do not belong to a faction
    /// represented on the alliance console.
    /// </summary>
    [DataField, AutoNetworkedField]
    public AllianceStatus AllianceUnknownStatus = AllianceStatus.Neutral;
}

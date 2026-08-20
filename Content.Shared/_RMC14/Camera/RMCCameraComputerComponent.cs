using Robust.Shared.GameStates;
using Content.Shared.Camera;
using Robust.Shared.Prototypes;

namespace Content.Shared._RMC14.Camera;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
[Access(typeof(SharedRMCCameraSystem))]
public sealed partial class RMCCameraComputerComponent : Component
{
    [DataField(required: true), AutoNetworkedField]
    public HashSet<EntProtoId> ProtoIds = new ();

    [AutoNetworkedField]
    public EntityUid? CurrentCamera;

    [AutoNetworkedField]
    public ProtoId<CameraNetworkPrototype>? ActiveNetwork;

    [AutoNetworkedField]
    public List<NetEntity> CameraIds = new();

    [AutoNetworkedField]
    public List<string> CameraNames = new();

    [AutoNetworkedField]
    public List<EntityUid> Watchers = new();

    [DataField, AutoNetworkedField]
    public LocId? Title;
}

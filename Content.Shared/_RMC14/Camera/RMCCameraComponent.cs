using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._RMC14.Camera;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(SharedRMCCameraSystem))]
public sealed partial class RMCCameraComponent : Component
{
    [DataField(required: true), AutoNetworkedField]
    public EntProtoId? Id;

    [DataField, AutoNetworkedField]
    public bool Rename = true;

    [DataField, AutoNetworkedField]
    public string? NameOverride;
}

[ByRefEvent]
public record struct RMCLegacyCameraIdChangedEvent(
    EntityUid Camera,
    EntProtoId? OldId,
    EntProtoId? NewId);

[ByRefEvent]
public record struct RMCLegacyCameraMapInitEvent(EntityUid Camera);

[ByRefEvent]
public record struct RMCLegacyCameraComputerMapInitEvent(EntityUid Computer);

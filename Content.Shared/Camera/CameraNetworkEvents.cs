using Robust.Shared.Prototypes;

namespace Content.Shared.Camera;

public enum CameraReceiverChangeKind : byte
{
    Authorization,
    MemberList,
    Marker,
}

[ByRefEvent]
public record struct CameraReceiverChangedEvent(CameraReceiverChangeKind Kind, EntityUid? Camera = null);

[ByRefEvent]
public record struct CameraNetworkGrantRequestEvent(
    ProtoId<CameraNetworkPrototype> Network,
    EntityUid Source,
    bool Grant);

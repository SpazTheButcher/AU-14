using Content.Shared.Camera;
using Robust.Shared.Prototypes;

namespace Content.Server.SurveillanceCamera;

[RegisterComponent]
[Access(typeof(SurveillanceCameraMonitorSystem))]
public sealed partial class SurveillanceCameraMonitorComponent : Component
{
    // Currently active camera viewed by this monitor.
    [ViewVariables]
    public EntityUid? ActiveCamera { get; set; }

    [ViewVariables]
    // Set of viewers currently looking at this monitor.
    public HashSet<EntityUid> Viewers { get; } = new();

    [ViewVariables]
    public ProtoId<CameraNetworkPrototype>? ActiveNetwork { get; set; }
}

using Robust.Shared.Prototypes;

namespace Content.Shared.Camera;

[Flags]
public enum CameraSourceKinds : byte
{
    None = 0,
    Standard = 1 << 0,
    Rmc = 1 << 1,
    All = Standard | Rmc,
}

[RegisterComponent]
public sealed partial class CameraNetworkMemberComponent : Component
{
    [DataField(required: true)] public HashSet<ProtoId<CameraNetworkPrototype>> Networks = [];
    [DataField(required: true)] public CameraSourceKinds SourceKinds = CameraSourceKinds.None;
}

[RegisterComponent]
public sealed partial class CameraNetworkReceiverComponent : Component
{
    [DataField] public HashSet<ProtoId<CameraNetworkPrototype>> Networks = [];
    [DataField(required: true)] public CameraSourceKinds SupportedSources = CameraSourceKinds.None;
}

[RegisterComponent]
public sealed partial class CameraMapMarkerComponent : Component
{
    [DataField] public bool Visible = true;
    [DataField] public bool Mobile;
    [DataField] public TimeSpan UpdateInterval = TimeSpan.FromSeconds(0.25);
}

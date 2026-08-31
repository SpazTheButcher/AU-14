using Content.Shared.Camera;
using Content.Shared.SurveillanceCamera;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._RMC14.Camera;

[Serializable, NetSerializable]
public enum RMCCameraUiKey
{
    Key,
}

[Serializable, NetSerializable]
public sealed class RMCCameraBuiState(
    CameraMapUiState map,
    List<CameraNetworkUiData>? networks = null,
    ProtoId<CameraNetworkPrototype>? activeNetwork = null,
    RMCCameraNetworkEditorUiState? editor = null) : BoundUserInterfaceState
{
    public CameraMapUiState Map { get; } = map;
    public List<CameraNetworkUiData> Networks { get; } = networks ?? [];
    public ProtoId<CameraNetworkPrototype>? ActiveNetwork { get; } = activeNetwork;
    public RMCCameraNetworkEditorUiState Editor { get; } = editor ?? new(0, [], []);
}

[Serializable, NetSerializable]
public sealed class RMCCameraWatchBuiMsg(NetEntity camera) : BoundUserInterfaceMessage
{
    public readonly NetEntity Camera = camera;
}

[Serializable, NetSerializable]
public sealed class RMCCameraPreviousBuiMsg : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class RMCCameraNextBuiMsg : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class RMCCameraRefreshSubnetsBuiMsg : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class RMCCameraNetworkBuiMsg(ProtoId<CameraNetworkPrototype> network) : BoundUserInterfaceMessage
{
    public ProtoId<CameraNetworkPrototype> Network { get; } = network;
}

[Serializable, NetSerializable]
public sealed class RMCCameraDisconnectBuiMsg : BoundUserInterfaceMessage;

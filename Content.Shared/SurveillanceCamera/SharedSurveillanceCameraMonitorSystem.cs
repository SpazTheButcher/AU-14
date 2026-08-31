using System.Linq;
using Content.Shared.Camera;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.SurveillanceCamera;

[Serializable, NetSerializable]
public sealed class CameraNetworkUiData(ProtoId<CameraNetworkPrototype> id, string name)
{
    public ProtoId<CameraNetworkPrototype> Id { get; } = id;
    public string Name { get; } = name;
}

[Serializable, NetSerializable]
public sealed class CameraListUiData(NetEntity camera, string name, bool active,
    HashSet<ProtoId<CameraNetworkPrototype>> networks)
{
    public NetEntity Camera { get; } = camera;
    public string Name { get; } = name;
    public bool Active { get; } = active;
    public HashSet<ProtoId<CameraNetworkPrototype>> Networks { get; } = networks;
}

[Serializable, NetSerializable]
public sealed class SurveillanceCameraMonitorUiState(
    NetEntity? activeCamera,
    string? activeCameraName,
    List<CameraNetworkUiData> networks,
    ProtoId<CameraNetworkPrototype>? activeNetwork,
    List<CameraListUiData> cameras,
    CameraMapUiState cameraMap) : BoundUserInterfaceState
{
    public NetEntity? ActiveCamera { get; } = activeCamera;
    public string? ActiveCameraName { get; } = activeCameraName;
    public List<CameraNetworkUiData> Networks { get; } = networks;
    public ProtoId<CameraNetworkPrototype>? ActiveNetwork { get; } = activeNetwork;
    public List<CameraListUiData> CameraList { get; } = cameras;
    public CameraMapUiState CameraMap { get; } = cameraMap;

    // TODO: Remove when the client UI switches to logical camera networks.
    [Obsolete("Use Networks.")]
    public HashSet<string> Subnets { get; } = networks.Select(network => network.Id.ToString()).ToHashSet();

    [Obsolete("Use ActiveCamera.")]
    public string ActiveAddress { get; } = activeCamera?.ToString() ?? string.Empty;

    [Obsolete("Use ActiveNetwork.")]
    public string ActiveSubnet { get; } = activeNetwork?.ToString() ?? string.Empty;

    [Obsolete("Use CameraList.")]
    public Dictionary<string, string> Cameras { get; } = cameras.ToDictionary(camera => camera.Camera.ToString(), camera => camera.Name);
}

[Serializable, NetSerializable]
public sealed class SurveillanceCameraMonitorSwitchMessage(NetEntity camera) : BoundUserInterfaceMessage
{
    public NetEntity Camera { get; } = camera;

    // TODO: Remove with the address-based client selection path.
    [Obsolete("Use the NetEntity constructor.")]
    public SurveillanceCameraMonitorSwitchMessage(string _) : this(default(NetEntity)) { }
}

[Serializable, NetSerializable]
public sealed class SurveillanceCameraMonitorSubnetRequestMessage(ProtoId<CameraNetworkPrototype> network) : BoundUserInterfaceMessage
{
    public ProtoId<CameraNetworkPrototype> Network { get; } = network;

    // TODO: Remove with the address-based client subnet selector.
    [Obsolete("Use the ProtoId constructor.")]
    public SurveillanceCameraMonitorSubnetRequestMessage(string network) : this((ProtoId<CameraNetworkPrototype>) network) { }

    [Obsolete("Use Network.")]
    public string Subnet => Network.ToString();
}

// Sent when the user requests that the cameras on the current subnet be refreshed.
[Serializable, NetSerializable]
public sealed class SurveillanceCameraRefreshCamerasMessage : BoundUserInterfaceMessage
{}

// Sent when the user requests that the subnets known by the monitor be refreshed.
[Serializable, NetSerializable]
public sealed class SurveillanceCameraRefreshSubnetsMessage : BoundUserInterfaceMessage
{}

// Sent when the user wants to disconnect the monitor from the camera.
[Serializable, NetSerializable]
public sealed class SurveillanceCameraDisconnectMessage : BoundUserInterfaceMessage
{}

[Serializable, NetSerializable]
public enum SurveillanceCameraMonitorUiKey : byte
{
    Key
}

// SETUP

[Serializable, NetSerializable]
public sealed class SurveillanceCameraSetupBoundUiState : BoundUserInterfaceState
{
    public string Name { get; }
    public uint Network { get; }
    public List<string> Networks { get; }
    public bool NameDisabled { get; }
    public bool NetworkDisabled { get; }

    public SurveillanceCameraSetupBoundUiState(string name, uint network, List<string> networks, bool nameDisabled, bool networkDisabled)
    {
        Name = name;
        Network = network;
        Networks = networks;
        NameDisabled = nameDisabled;
        NetworkDisabled = networkDisabled;
    }
}

[Serializable, NetSerializable]
public sealed class SurveillanceCameraLogicalNetworkSetupBoundUiState(
    string name,
    ProtoId<CameraNetworkPrototype>? network,
    List<ProtoId<CameraNetworkPrototype>> networks,
    bool nameDisabled,
    bool networkDisabled) : BoundUserInterfaceState
{
    public string Name { get; } = name;
    public ProtoId<CameraNetworkPrototype>? Network { get; } = network;
    public List<ProtoId<CameraNetworkPrototype>> Networks { get; } = networks;
    public bool NameDisabled { get; } = nameDisabled;
    public bool NetworkDisabled { get; } = networkDisabled;
}

[Serializable, NetSerializable]
public sealed class SurveillanceCameraSetupSetName : BoundUserInterfaceMessage
{
    public string Name { get; }

    public SurveillanceCameraSetupSetName(string name)
    {
        Name = name;
    }
}

[Serializable, NetSerializable]
public sealed class SurveillanceCameraSetupSetNetwork : BoundUserInterfaceMessage
{
    public int Network { get; }

    public SurveillanceCameraSetupSetNetwork(int network)
    {
        Network = network;
    }
}


[Serializable, NetSerializable]
public enum SurveillanceCameraSetupUiKey : byte
{
    Camera,
    Router
}

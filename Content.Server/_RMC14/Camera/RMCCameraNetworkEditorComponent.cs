using Content.Shared.Camera;
using Robust.Shared.Prototypes;

namespace Content.Server._RMC14.Camera;

[RegisterComponent, Access(typeof(RMCCameraSystem))]
public sealed partial class RMCCameraNetworkEditorComponent : Component
{
    public readonly HashSet<ProtoId<CameraNetworkPrototype>> SeededNetworks = [];
    public readonly Dictionary<ProtoId<CameraNetworkPrototype>, string> OwnedNetworks = [];
    public readonly Dictionary<ProtoId<CameraNetworkPrototype>, string> Aliases = [];
    public readonly HashSet<ProtoId<CameraNetworkPrototype>> HiddenSeededNetworks = [];
    public uint Revision;
    public uint NextOwnedNetworkId = 1;
}

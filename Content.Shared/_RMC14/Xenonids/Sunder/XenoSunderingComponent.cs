using Content.Shared.FixedPoint;
using Robust.Shared.GameStates;

namespace Content.Shared._RMC14.Xenonids.Sunder;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(XenoSunderSystem))]
public sealed partial class XenoSunderingComponent : Component
{
    [DataField, AutoNetworkedField]
    public FixedPoint2 Amount;
}

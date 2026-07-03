using Content.Shared._RMC14.Armor;
using Content.Shared.FixedPoint;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._RMC14.Xenonids.Sunder;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(fieldDeltas: true), AutoGenerateComponentPause]
[Access(typeof(CMArmorSystem), typeof(XenoSunderSystem))]
public sealed partial class XenoSunderComponent : Component
{
    [DataField, AutoNetworkedField]
    public FixedPoint2 Amount;

    [DataField, AutoNetworkedField]
    public FixedPoint2 Max = 100;

    [DataField, AutoNetworkedField]
    public FixedPoint2 Recover = 0.15;

    [DataField, AutoNetworkedField]
    public FixedPoint2 IncomingMultiplier = 1;

    [DataField, AutoNetworkedField]
    public TimeSpan RegenCooldown = TimeSpan.FromSeconds(1);

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan NextRegenTime;
}

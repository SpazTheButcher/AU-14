using Content.Shared.Antag;
using Content.Shared.Chat.Prototypes;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Damage;
using Content.Shared.Humanoid;
using Content.Shared.Roles;
using Content.Shared.StatusIcon;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype;

namespace Content.Shared.AU14.BlackGoo;

[RegisterComponent, NetworkedComponent]
public sealed partial class AlphaGooZombieComponent : Component
{
    /// Originally was inherited from standardgoo but component inhertiance is nobueno, used in transform system

}

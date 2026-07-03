using Robust.Shared.GameStates;

namespace Content.Shared._RMC14.Xenonids.Charge.ChargerJockey;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class XenoChargerJockeyComponent : Component
{
    // Max drones that can ride at once.
    [DataField, AutoNetworkedField] public int MaxRiders = 4;

    // Tracks current riders.
    [DataField, AutoNetworkedField] public HashSet<EntityUid> Riders = new();

    [DataField] public TimeSpan MountDoAfter = TimeSpan.FromSeconds(1.5f);
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class XenoChargerRidingComponent : Component
{
    [DataField, AutoNetworkedField] public EntityUid Charger;
}

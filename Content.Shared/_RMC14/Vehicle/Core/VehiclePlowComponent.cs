using Robust.Shared.GameStates;

namespace Content.Shared._RMC14.Vehicle;

/// <summary>
/// Marks a hardpoint as a structure-clearing plow. Its damage bonus only
/// applies when the obstruction contacts the front face of the vehicle.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class VehiclePlowComponent : Component
{
    /// <summary>
    /// Multiplier applied to the vehicle's impact damage against structures.
    /// The bonus above 1 scales with the hardpoint's current performance.
    /// </summary>
    [DataField]
    public float StructureDamageMultiplier = 1.5f;
}

/// <summary>
/// Optional vehicle-side multiplier for chassis designed specifically around a
/// plow, such as the AEV. This combines with the installed plow's multiplier.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class VehiclePlowChassisComponent : Component
{
    [DataField]
    public float StructureDamageMultiplier = 1f;
}

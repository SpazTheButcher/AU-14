namespace Content.Server._CMU14.Explosion;

/// <summary>
/// CMU-owned selectable detonation behavior for compatible grenades.
/// Timed mode leaves the upstream timer path unchanged.
/// Impact mode arms the grenade but removes the active countdown.
/// </summary>
public enum CMUGrenadeDetonationMode : byte
{
    Timed,
    Impact,
}

[RegisterComponent]
[Access(typeof(CMUGrenadeDetonationSystem))]
public sealed partial class CMUGrenadeDetonationModeComponent : Component
{
    /// <summary>
    /// Selected detonation mode. Existing grenades default to their normal timed behavior.
    /// </summary>
    [DataField]
    public CMUGrenadeDetonationMode Mode = CMUGrenadeDetonationMode.Timed;

    /// <summary>
    /// True after an Impact-mode grenade has been primed.
    /// </summary>
    [ViewVariables(VVAccess.ReadOnly)]
    public bool Armed;

    /// <summary>
    /// Best available attribution for the entity that armed/threw/fired the grenade.
    /// </summary>
    [ViewVariables(VVAccess.ReadOnly)]
    public EntityUid? ArmedBy;
}

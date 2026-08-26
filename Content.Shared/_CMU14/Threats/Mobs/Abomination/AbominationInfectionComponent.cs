using Content.Shared.Damage;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._CMU14.Threats.Mobs.Abomination;

/// <summary>
///     A silent marker applied to a humanoid hit by an abomination (or injected
///     with abomination venom). There are no visible symptoms — no cough, no
///     jitter, no vomit, no drunkenness, no scream — so neither the host nor
///     anyone around them can tell they carry it. The infection still works
///     quietly: a flat poison tick drains the host until they die, and any
///     death while infected reclaims the body as an abomination (seeding flesh
///     kudzu at the corpse). Survive long enough and the infection burns out
///     on its own. The horror is the not knowing — the colony falls to its own
///     paranoia.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, AutoGenerateComponentPause]
public sealed partial class AbominationInfectionComponent : Component
{
    /// <summary>How long until the infection is automatically cured if the host is still alive.</summary>
    [DataField, AutoNetworkedField]
    public TimeSpan CureAfter = TimeSpan.FromMinutes(15);

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan InfectedAt;

    /// <summary>Next scheduled silent poison tick.</summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan NextTickAt;

    /// <summary>Damage applied on each silent poison tick. Flat — not scaled by severity.</summary>
    [DataField, AutoNetworkedField]
    public DamageSpecifier TickDamage = new();

    /// <summary>How often the silent poison tick fires.</summary>
    [DataField, AutoNetworkedField]
    public TimeSpan TickInterval = TimeSpan.FromSeconds(6);
}

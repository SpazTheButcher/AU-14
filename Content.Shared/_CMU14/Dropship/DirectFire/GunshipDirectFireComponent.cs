using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._CMU14.Dropship.DirectFire;

/// <summary>
/// A dropship weapon point reserved for pilot-controlled, forward-firing weapons.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class GunshipDirectFirePointComponent : Component
{
    [DataField, AutoNetworkedField]
    public float AimOffsetDegrees;
}

/// <summary>
/// Defines a weapon that is fired directly from a tactical-hovering gunship.
/// GimbalDegrees is the weapon's complete firing arc, centered on ship-forward.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, AutoGenerateComponentPause]
public sealed partial class GunshipDirectFireWeaponComponent : Component
{
    [DataField]
    public EntProtoId Projectile = "RMCFlareCASBullet";

    [DataField(required: true)]
    public EntProtoId AmmoPrototype;

    [DataField, AutoNetworkedField]
    public float GimbalDegrees = 30f;

    [DataField, AutoNetworkedField]
    public float ProjectileSpeed = 20f;

    [DataField, AutoNetworkedField]
    public TimeSpan FireDelay = TimeSpan.FromSeconds(1.5);

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan? NextFireAt;

    [DataField]
    public SoundSpecifier FireSound = new SoundPathSpecifier("/Audio/Weapons/Guns/Gunshots/flaregun.ogg");
}

[Serializable, NetSerializable]
public enum GunshipDirectFireVisuals
{
    AimOffsetDegrees,
}

[Serializable, NetSerializable]
public sealed class GunshipDirectFireAimEvent(NetCoordinates coordinates) : EntityEventArgs
{
    public NetCoordinates Coordinates = coordinates;
}

[Serializable, NetSerializable]
public sealed class GunshipDirectFireEvent(NetCoordinates coordinates) : EntityEventArgs
{
    public NetCoordinates Coordinates = coordinates;
}

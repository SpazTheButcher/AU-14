using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.Shared._CMU14.Dropship.TacticalLand;

[RegisterComponent]
public sealed partial class DropshipTacticalHoverComponent : Component
{
    [DataField]
    public EntityUid? ReturnDestination;

    [DataField]
    public EntityUid? HoverDestination;

    [DataField]
    public EntityUid? GroundMap;

    [DataField]
    public TimeSpan ReturnAt;

    [DataField]
    public TimeSpan NextReturnAttempt;

    [DataField]
    public Vector2i Footprint = new(9, 17);

    [DataField]
    public int GroundMapOffset = -1;

    /// <summary>
    /// World-space velocity accumulated by gunship flight controls. Tactical
    /// hover deliberately applies no passive damping, giving it weightless momentum.
    /// </summary>
    public Vector2 GunshipLinearVelocity;

    /// <summary>
    /// Angular velocity accumulated by gunship flight controls, in degrees per second.
    /// Like linear velocity, tactical hover applies no passive damping.
    /// </summary>
    public float GunshipAngularVelocityDegrees;

    /// <summary>
    /// Pending five-second vertical movement. The dropship remains on its
    /// current level until this time is reached.
    /// </summary>
    public TimeSpan? AltitudeTransitionAt;

    public EntityUid? AltitudeTargetMap;

    public int AltitudeOffset;

    public bool AltitudeLanding;

    public EntityUid? AltitudePilot;

    [DataField]
    public EntityUid? Shadow;

    [DataField]
    public List<EntityUid> Downwashes = new();

    [DataField]
    public EntProtoId ShadowPrototype = "CMUDropshipTacticalHoverShadow";

    [DataField]
    public EntProtoId DownwashPrototype = "CMUDropshipTacticalHoverDownwash";
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class DropshipTacticalHoverShadowComponent : Component
{
    [DataField, AutoNetworkedField]
    public EntityUid? Dropship;

    [DataField, AutoNetworkedField]
    public Vector2i Footprint = new(9, 17);

    [DataField, AutoNetworkedField]
    public int ProjectedMapOffset = -1;
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class DropshipTacticalHoverDownwashComponent : Component
{
    [DataField, AutoNetworkedField]
    public EntityUid? Dropship;

    [DataField, AutoNetworkedField]
    public Vector2 Offset;

    [DataField, AutoNetworkedField]
    public int ProjectedMapOffset = -1;
}

/// <summary>
/// Raised after a dropship leaves tactical hover so hover-only equipment can clean itself up.
/// </summary>
public sealed class DropshipTacticalHoverEndedEvent(EntityUid dropship) : EntityEventArgs
{
    public readonly EntityUid Dropship = dropship;
}

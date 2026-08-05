using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Shared._CMU14.Dropship.DirectFire;
using Content.Shared._CMU14.Dropship.Integrity;
using Content.Shared._CMU14.Dropship.TacticalLand;
using Content.Shared._CMU14.ZLevels.Core;
using Content.Shared._RMC14.Dropship;
using Content.Shared._RMC14.Dropship.AttachmentPoint;
using Content.Shared._RMC14.Dropship.Weapon;
using Content.Shared._RMC14.NightVision;
using Content.Shared._RMC14.PowerLoader;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Actions;
using Content.Shared.Buckle.Components;
using Content.Shared.Damage;
using Content.Shared.Eye;
using Content.Shared.Inventory;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Server.Movement.Components;
using Content.Server.Destructible;
using Robust.Shared.Audio;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Physics;

namespace Content.Server._CMU14.Dropship.TacticalLand;

public sealed partial class DropshipTacticalLandSystem
{
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private InventorySystem _pilotInventory = default!;
    [Dependency] private SharedNightVisionSystem _nightVision = default!;
    [Dependency] private SharedContainerSystem _gunshipContainers = default!;
    [Dependency] private SharedGunSystem _gunshipGun = default!;
    [Dependency] private PowerLoaderSystem _gunshipPowerLoader = default!;
    [Dependency] private SharedAppearanceSystem _directFireAppearance = default!;
    [Dependency] private SharedActionsSystem _gunshipActions = default!;

    private static readonly TimeSpan GunshipBlockedPopupCooldown = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GunshipAlarmUpdateInterval = TimeSpan.FromMilliseconds(250);
    private static readonly AudioParams GunshipAlarmAudioParams = AudioParams.Default
        .WithLoop(true)
        .WithMaxDistance(24f);
    private static readonly Vector2i[] GunshipProximityOffsets =
    [
        new(-1, -1), new(0, -1), new(1, -1),
        new(-1, 0),               new(1, 0),
        new(-1, 1),  new(0, 1),  new(1, 1),
    ];
    private const float GunshipThrustStep = 5f;
    private const float GunshipCursorMaxOffset = 24f;
    private const float GunshipCursorPvsIncrease = 2.4f;
    private const float GunshipPilotPvsScale = 1f + GunshipCursorPvsIncrease;
    private TimeSpan _nextGunshipAlarmUpdate;

    private void InitializeGunshipPilot()
    {
        SubscribeLocalEvent<GunshipPilotSeatComponent, StrapAttemptEvent>(OnGunshipSeatStrapAttempt);
        SubscribeLocalEvent<GunshipPilotSeatComponent, StrappedEvent>(OnGunshipSeatStrapped);
        SubscribeLocalEvent<GunshipPilotSeatComponent, UnstrappedEvent>(OnGunshipSeatUnstrapped);
        SubscribeLocalEvent<GunshipPilotSeatComponent, ComponentShutdown>(OnGunshipSeatShutdown);
        SubscribeLocalEvent<GunshipPilotSeatComponent, GunshipMasterAlarmToggleActionEvent>(OnMasterAlarmToggle);
        SubscribeLocalEvent<DropshipIntegrityComponent, ComponentShutdown>(OnDropshipIntegrityShutdown);
        SubscribeNetworkEvent<GunshipControlInputEvent>(OnGunshipControlInput);
        SubscribeNetworkEvent<GunshipThrustAdjustEvent>(OnGunshipThrustAdjust);
        SubscribeNetworkEvent<GunshipDirectFireAimEvent>(OnGunshipDirectFireAim);
        SubscribeNetworkEvent<GunshipDirectFireEvent>(OnGunshipDirectFire);
    }

    private void OnGunshipDirectFireAim(GunshipDirectFireAimEvent ev, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { } pilot ||
            !TryGetPilotDirectFireMount(pilot, out var grid, out _, out var point, out _, out _, out _, out _))
        {
            return;
        }

        TryAimDirectFireMount(grid, point, GetCoordinates(ev.Coordinates), out _);
    }

    private void OnGunshipDirectFire(GunshipDirectFireEvent ev, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { } pilot ||
            !TryGetPilotDirectFireMount(pilot,
                out var grid,
                out var hover,
                out var point,
                out var weaponUid,
                out var weapon,
                out var ammoUid,
                out var ammo))
        {
            return;
        }

        if (TryComp(grid, out DropshipIntegrityComponent? weaponIntegrity) &&
            weaponIntegrity.ActiveMalfunctions.Contains(DropshipMalfunction.WeaponShort))
        {
            _popup.PopupEntity("Weapon short detected. The direct-fire system is offline.", point.Owner, pilot, PopupType.SmallCaution);
            return;
        }

        if (!TryAimDirectFireMount(grid, point, GetCoordinates(ev.Coordinates), out var direction))
            return;

        if (weapon.NextFireAt is { } nextFire && _timing.CurTime < nextFire)
            return;

        if (ammoUid is not { } loadedAmmo || ammo == null || ammo.Rounds < 1)
        {
            _popup.PopupEntity("The M4332 Signal Flare Launcher is out of ammunition.", point.Owner, pilot, PopupType.SmallCaution);
            return;
        }

        var origin = _transform.GetWorldPosition(point.Owner) + direction * 1.25f;
        var mapId = Transform(grid).MapID;
        var projectile = Spawn(weapon.Projectile, new MapCoordinates(origin, mapId));
        _gunshipGun.ShootProjectile(projectile,
            direction,
            hover.GunshipLinearVelocity,
            weaponUid,
            pilot,
            weapon.ProjectileSpeed);

        _audio.PlayPvs(weapon.FireSound, point.Owner);
        ammo.Rounds--;
        _directFireAppearance.SetData(loadedAmmo, DropshipAmmoVisuals.Fill, ammo.Rounds);
        Dirty(loadedAmmo, ammo);
        _gunshipPowerLoader.SyncAppearance(point.Owner);

        weapon.NextFireAt = _timing.CurTime + weapon.FireDelay;
        Dirty(weaponUid, weapon);
    }

    private bool TryGetPilotDirectFireMount(
        EntityUid pilot,
        out EntityUid grid,
        out DropshipTacticalHoverComponent hover,
        out Entity<GunshipDirectFirePointComponent> point,
        out EntityUid weaponUid,
        out GunshipDirectFireWeaponComponent weapon,
        out EntityUid? ammoUid,
        out DropshipAmmoComponent? ammo)
    {
        grid = default;
        hover = default!;
        point = default;
        weaponUid = default;
        weapon = default!;
        ammoUid = null;
        ammo = null;

        if (!TryGetControlledGunshipSeat(pilot, out var seat) ||
            seat.Comp.ViewOffset != 0 ||
            seat.Comp.RearView ||
            Transform(seat).GridUid is not { } seatGrid ||
            !TryComp(seatGrid, out DropshipTacticalHoverComponent? foundHover) ||
            foundHover.AltitudeTransitionAt != null ||
            !TryComp(pilot, out GunshipPilotHudComponent? hud) ||
            hud.Dropship != seatGrid ||
            TryComp(seatGrid, out DropshipComponent? dropship) && dropship.Crashed ||
            !TryGetDirectFireMount(seatGrid, out point, out weaponUid, out weapon, out ammoUid, out ammo))
        {
            return false;
        }

        grid = seatGrid;
        hover = foundHover;
        return true;
    }

    private bool TryGetDirectFireMount(
        EntityUid grid,
        out Entity<GunshipDirectFirePointComponent> point,
        out EntityUid weaponUid,
        out GunshipDirectFireWeaponComponent weapon,
        out EntityUid? ammoUid,
        out DropshipAmmoComponent? ammo)
    {
        point = default;
        weaponUid = default;
        weapon = default!;
        ammoUid = null;
        ammo = null;

        if (!TryComp(grid, out DropshipComponent? dropship))
            return false;

        foreach (var pointUid in dropship.AttachmentPoints)
        {
            if (!TryComp(pointUid, out GunshipDirectFirePointComponent? directPoint) ||
                !TryComp(pointUid, out DropshipWeaponPointComponent? weaponPoint) ||
                !_gunshipContainers.TryGetContainer(pointUid, weaponPoint.WeaponContainerSlotId, out var weaponContainer) ||
                weaponContainer.ContainedEntities.Count == 0)
            {
                continue;
            }

            var containedWeapon = weaponContainer.ContainedEntities[0];
            if (!TryComp(containedWeapon, out GunshipDirectFireWeaponComponent? directWeapon))
                continue;

            point = (pointUid, directPoint);
            weaponUid = containedWeapon;
            weapon = directWeapon;

            if (!_gunshipContainers.TryGetContainer(pointUid, weaponPoint.AmmoContainerSlotId, out var ammoContainer))
                return true;

            foreach (var containedAmmo in ammoContainer.ContainedEntities)
            {
                if (!TryComp(containedAmmo, out DropshipAmmoComponent? foundAmmo) ||
                    MetaData(containedAmmo).EntityPrototype?.ID != directWeapon.AmmoPrototype.Id)
                {
                    continue;
                }

                ammoUid = containedAmmo;
                ammo = foundAmmo;
                break;
            }

            return true;
        }

        return false;
    }

    private bool TryAimDirectFireMount(
        EntityUid grid,
        Entity<GunshipDirectFirePointComponent> point,
        EntityCoordinates targetCoordinates,
        out Vector2 direction)
    {
        direction = default;
        var target = _transform.ToMapCoordinates(targetCoordinates);
        if (target.MapId != Transform(grid).MapID)
            return false;

        var origin = _transform.GetWorldPosition(point.Owner);
        var desired = target.Position - origin;
        if (desired.LengthSquared() <= 0.0001f ||
            !TryGetDirectFireMount(grid, out _, out _, out var weapon, out _, out _))
        {
            return false;
        }

        desired = Vector2.Normalize(desired);
        var shipRotation = _transform.GetWorldRotation(grid);
        var forward = shipRotation.RotateVec(Vector2.UnitY);
        var signedRadians = MathF.Atan2(
            forward.X * desired.Y - forward.Y * desired.X,
            Vector2.Dot(forward, desired));
        var halfGimbal = MathHelper.DegreesToRadians(MathF.Max(0f, weapon.GimbalDegrees) * 0.5f);
        var clampedRadians = Math.Clamp(signedRadians, -halfGimbal, halfGimbal);
        var aimOffset = new Angle(clampedRadians);
        direction = shipRotation.RotateVec(aimOffset.RotateVec(Vector2.UnitY));

        var aimDegrees = (float) aimOffset.Degrees;
        if (!MathHelper.CloseToPercent(point.Comp.AimOffsetDegrees, aimDegrees))
        {
            point.Comp.AimOffsetDegrees = aimDegrees;
            Dirty(point);
            _directFireAppearance.SetData(point.Owner, GunshipDirectFireVisuals.AimOffsetDegrees, aimDegrees);
        }

        return true;
    }

    private void OnGunshipSeatStrapAttempt(Entity<GunshipPilotSeatComponent> ent, ref StrapAttemptEvent args)
    {
        if (Transform(ent).GridUid is not { } grid || !HasComp<DropshipComponent>(grid))
        {
            args.Cancelled = true;
            if (args.Popup)
                _popup.PopupEntity("The pilot seat is not installed on a dropship.", ent, args.User ?? args.Buckle.Owner, PopupType.MediumCaution);
            return;
        }

        if (TryComp(ent, out AccessReaderComponent? access) && !_access.IsAllowed(args.Buckle, ent, access))
        {
            args.Cancelled = true;
            if (args.Popup)
                _popup.PopupEntity("Access denied.", ent, args.User ?? args.Buckle.Owner, PopupType.MediumCaution);
        }
    }

    private void OnGunshipSeatStrapped(Entity<GunshipPilotSeatComponent> ent, ref StrappedEvent args)
    {
        StopGunshipControl(ent, restorePilot: true);
        ent.Comp.Pilot = args.Buckle;
        ent.Comp.HeldInputs = GunshipControlInput.None;
        ent.Comp.PressedActions = 0;
        ent.Comp.ViewOffset = 0;
        ent.Comp.RearView = false;
        Dirty(ent);

        EnablePilotAlarmAction(args.Buckle, ent);

        if (Transform(ent).GridUid is { } grid && TryComp(grid, out DropshipTacticalHoverComponent? hover))
            EnsureGunshipPilotEye(ent, (grid, hover));
        else
            _popup.PopupEntity("Gunship flight controls will engage during tactical hover.", ent, args.Buckle, PopupType.Medium);
    }

    private void OnGunshipSeatUnstrapped(Entity<GunshipPilotSeatComponent> ent, ref UnstrappedEvent args)
    {
        if (ent.Comp.Pilot != args.Buckle)
            return;

        StopGunshipControl(ent, restorePilot: true);
    }

    private void OnGunshipSeatShutdown(Entity<GunshipPilotSeatComponent> ent, ref ComponentShutdown args)
    {
        StopGunshipControl(ent, restorePilot: true);
    }

    private void EnablePilotAlarmAction(EntityUid pilot, Entity<GunshipPilotSeatComponent> seat)
    {
        if (Transform(seat).GridUid is not { } dropship ||
            !TryComp(dropship, out DropshipIntegrityComponent? integrity))
        {
            return;
        }

        seat.Comp.MasterAlarmAction ??= _gunshipActions.AddAction(pilot,
            seat.Comp.MasterAlarmActionId,
            seat.Owner);
        _gunshipActions.SetToggled(seat.Comp.MasterAlarmAction, integrity.MasterAlarmSilenced);
    }

    private void DisablePilotAlarmAction(EntityUid pilot, Entity<GunshipPilotSeatComponent> seat)
    {
        if (seat.Comp.MasterAlarmAction is not { } action)
            return;

        _gunshipActions.RemoveAction(pilot, action);
        seat.Comp.MasterAlarmAction = null;
    }

    private void OnMasterAlarmToggle(
        Entity<GunshipPilotSeatComponent> ent,
        ref GunshipMasterAlarmToggleActionEvent args)
    {
        if (ent.Comp.Pilot != args.Performer ||
            !TryGetControlledGunshipSeat(args.Performer, out var seat) ||
            seat.Owner != ent.Owner ||
            Transform(ent).GridUid is not { } dropship ||
            !TryComp(dropship, out DropshipIntegrityComponent? integrity))
        {
            return;
        }

        args.Handled = true;
        integrity.MasterAlarmSilenced = !integrity.MasterAlarmSilenced;
        Dirty(dropship, integrity);
        _gunshipActions.SetToggled(ent.Comp.MasterAlarmAction, integrity.MasterAlarmSilenced);
        UpdateGunshipAlarmAudio((dropship, integrity));
        _popup.PopupEntity(integrity.MasterAlarmSilenced ? "Master alarm silenced." : "Master alarm restored.",
            seat,
            args.Performer,
            PopupType.Small);
    }

    private void OnGunshipControlInput(GunshipControlInputEvent ev, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { } pilot ||
            !TryGetControlledGunshipSeat(pilot, out var seat))
        {
            return;
        }

        var actionMask = (ushort) (1 << (int) ev.Action);
        if (ev.Pressed)
        {
            if ((seat.Comp.PressedActions & actionMask) != 0)
                return;

            seat.Comp.PressedActions |= actionMask;
        }
        else
        {
            seat.Comp.PressedActions &= (ushort) ~actionMask;
        }

        if (ev.Action is GunshipControlAction.Ascend or GunshipControlAction.Descend or
            GunshipControlAction.ViewUp or GunshipControlAction.ViewDown or GunshipControlAction.RearView)
        {
            if (!ev.Pressed)
                return;

            if ((ev.Action is GunshipControlAction.ViewUp or GunshipControlAction.ViewDown or GunshipControlAction.RearView) &&
                Transform(seat).GridUid is { } sensorGrid &&
                TryComp(sensorGrid, out DropshipIntegrityComponent? sensorIntegrity) &&
                sensorIntegrity.ActiveMalfunctions.Contains(DropshipMalfunction.SensorArrayFault))
            {
                _popup.PopupEntity("Sensor array fault detected. External cameras are unavailable.", seat, pilot, PopupType.SmallCaution);
                return;
            }

            if (ev.Action == GunshipControlAction.ViewUp)
                SetGunshipViewOffset(seat, 1);
            else if (ev.Action == GunshipControlAction.ViewDown)
                SetGunshipViewOffset(seat, -1);
            else if (ev.Action == GunshipControlAction.RearView)
                SetGunshipRearView(seat);
            else
                TryChangeGunshipAltitude(seat, ev.Action == GunshipControlAction.Ascend ? 1 : -1);

            return;
        }

        var input = ev.Action switch
        {
            GunshipControlAction.Forward => GunshipControlInput.Forward,
            GunshipControlAction.Back => GunshipControlInput.Back,
            GunshipControlAction.Left => GunshipControlInput.Left,
            GunshipControlAction.Right => GunshipControlInput.Right,
            GunshipControlAction.RotateLeft => GunshipControlInput.RotateLeft,
            GunshipControlAction.RotateRight => GunshipControlInput.RotateRight,
            _ => GunshipControlInput.None,
        };

        if (ev.Pressed)
            seat.Comp.HeldInputs |= input;
        else
            seat.Comp.HeldInputs &= ~input;
    }

    private void OnGunshipThrustAdjust(GunshipThrustAdjustEvent ev, EntitySessionEventArgs args)
    {
        if (ev.Steps == 0 ||
            args.SenderSession.AttachedEntity is not { } pilot ||
            !TryGetControlledGunshipSeat(pilot, out var seat) ||
            !TryComp(pilot, out GunshipPilotHudComponent? hud) ||
            hud.Dropship == null)
        {
            return;
        }

        var thrust = Math.Clamp(seat.Comp.ThrustPercent + Math.Sign(ev.Steps) * GunshipThrustStep, 0f, 100f);
        if (MathHelper.CloseToPercent(seat.Comp.ThrustPercent, thrust))
            return;

        seat.Comp.ThrustPercent = thrust;
        Dirty(seat);
    }

    private bool TryGetControlledGunshipSeat(EntityUid pilot, out Entity<GunshipPilotSeatComponent> seat)
    {
        seat = default;
        if (!TryComp(pilot, out BuckleComponent? buckle) ||
            buckle.BuckledTo is not { } seatUid ||
            !TryComp(seatUid, out GunshipPilotSeatComponent? seatComp) ||
            seatComp.Pilot != pilot)
        {
            return false;
        }

        seat = (seatUid, seatComp);
        return true;
    }

    private void UpdateGunshipPilots(float frameTime)
    {
        UpdateGunshipPilotHuds();

        var query = EntityQueryEnumerator<GunshipPilotSeatComponent>();
        while (query.MoveNext(out var seatUid, out var seat))
        {
            if (seat.Pilot is not { } pilot)
                continue;

            if (!TryComp(pilot, out BuckleComponent? buckle) || buckle.BuckledTo != seatUid)
            {
                StopGunshipControl((seatUid, seat), restorePilot: true);
                continue;
            }

            if (Transform(seatUid).GridUid is not { } grid ||
                !TryComp(grid, out DropshipTacticalHoverComponent? hover))
            {
                seat.HeldInputs = GunshipControlInput.None;
                TeardownGunshipPilotEye((seatUid, seat));
                continue;
            }

            if (TryComp(grid, out DropshipIntegrityComponent? integrity) &&
                integrity.ActiveMalfunctions.Contains(DropshipMalfunction.SensorArrayFault) &&
                (seat.ViewOffset != 0 || seat.RearView))
            {
                seat.ViewOffset = 0;
                seat.RearView = false;
                Dirty(seatUid, seat);
            }

            EnsureGunshipPilotEye((seatUid, seat), (grid, hover));
            ApplyGunshipFlightInput((seatUid, seat), (grid, hover), frameTime);
            UpdateGunshipPilotEye((seatUid, seat), (grid, hover));
        }
    }

    private void ApplyGunshipFlightInput(
        Entity<GunshipPilotSeatComponent> seat,
        Entity<DropshipTacticalHoverComponent> hover,
        float frameTime)
    {
        if (hover.Comp.AltitudeTransitionAt != null)
        {
            seat.Comp.HeldInputs = GunshipControlInput.None;
            return;
        }

        if (TryComp(hover.Owner, out DropshipComponent? dropship) && dropship.Crashed)
        {
            seat.Comp.HeldInputs = GunshipControlInput.None;
            return;
        }

        if (!TryComp(hover.Owner, out MapGridComponent? dropshipGrid))
        {
            return;
        }

        var propulsionFault = TryComp(hover.Owner, out DropshipIntegrityComponent? integrity) &&
            integrity.ActiveMalfunctions.Contains(DropshipMalfunction.PropulsionFault);
        var maneuveringFault = integrity != null &&
            integrity.ActiveMalfunctions.Contains(DropshipMalfunction.ManeuveringThrusterFault);
        var propulsionAccelerationMultiplier = propulsionFault ? 0.55f : 1f;
        var maximumSpeedMultiplier = propulsionFault ? 0.70f : 1f;
        var maneuveringAccelerationMultiplier = maneuveringFault ? 0.45f : 1f;
        var maximumTurnSpeedMultiplier = maneuveringFault ? 0.60f : 1f;
        var thrustMultiplier = Math.Clamp(seat.Comp.ThrustPercent / 100f, 0f, 1f);

        var position = _transform.GetWorldPosition(hover.Owner);
        var rotation = _transform.GetWorldRotation(hover.Owner);
        var map = Transform(hover.Owner).MapUid;
        if (map is null)
            return;

        var turn = 0f;
        if (seat.Comp.HeldInputs.HasFlag(GunshipControlInput.RotateLeft))
            turn += 1f;
        if (seat.Comp.HeldInputs.HasFlag(GunshipControlInput.RotateRight))
            turn -= 1f;

        if (turn != 0f)
        {
            var previousAngularSpeed = MathF.Abs(hover.Comp.GunshipAngularVelocityDegrees);
            hover.Comp.GunshipAngularVelocityDegrees += turn *
                seat.Comp.RotationAccelerationDegrees * maneuveringAccelerationMultiplier * thrustMultiplier * frameTime;
            var angularSpeed = MathF.Abs(hover.Comp.GunshipAngularVelocityDegrees);
            var maximumAngularSpeed = seat.Comp.MaxRotationSpeedDegrees * maximumTurnSpeedMultiplier * thrustMultiplier;
            if (angularSpeed > maximumAngularSpeed && angularSpeed > previousAngularSpeed)
            {
                hover.Comp.GunshipAngularVelocityDegrees = MathF.CopySign(
                    MathF.Max(previousAngularSpeed, maximumAngularSpeed),
                    hover.Comp.GunshipAngularVelocityDegrees);
            }
        }

        if (hover.Comp.GunshipAngularVelocityDegrees != 0f)
        {
            var proposedRotation = rotation +
                Angle.FromDegrees(hover.Comp.GunshipAngularVelocityDegrees * frameTime);
            if (IsGunshipFootprintClear((hover.Owner, dropshipGrid), map.Value, position, proposedRotation, out var rotationBlockers))
            {
                rotation = proposedRotation;
                _transform.SetWorldRotation(hover.Owner, rotation);
            }
            else
            {
                var radius = MathF.Max(dropshipGrid.LocalAABB.Width, dropshipGrid.LocalAABB.Height) * 0.5f;
                var impactSpeed = MathF.Abs(MathHelper.DegreesToRadians(hover.Comp.GunshipAngularVelocityDegrees)) * radius;
                _integrity.ApplyFlightImpact(hover.Owner, rotationBlockers, impactSpeed);
                hover.Comp.GunshipAngularVelocityDegrees = 0f;
                PopupGunshipBlocked(seat, "The gunship cannot rotate into an obstruction.");
            }
        }

        var localMovement = Vector2.Zero;
        if (seat.Comp.HeldInputs.HasFlag(GunshipControlInput.Forward))
            localMovement += Vector2.UnitY;
        if (seat.Comp.HeldInputs.HasFlag(GunshipControlInput.Back))
            localMovement -= Vector2.UnitY;
        if (seat.Comp.HeldInputs.HasFlag(GunshipControlInput.Right))
            localMovement += Vector2.UnitX;
        if (seat.Comp.HeldInputs.HasFlag(GunshipControlInput.Left))
            localMovement -= Vector2.UnitX;

        if (localMovement != Vector2.Zero)
        {
            var previousSpeed = hover.Comp.GunshipLinearVelocity.Length();
            localMovement = Vector2.Normalize(localMovement);
            localMovement.X *= maneuveringAccelerationMultiplier;
            localMovement.Y *= propulsionAccelerationMultiplier;
            hover.Comp.GunshipLinearVelocity += rotation.RotateVec(localMovement) *
                seat.Comp.TranslationAcceleration * thrustMultiplier * frameTime;

            var speed = hover.Comp.GunshipLinearVelocity.Length();
            var maximumSpeed = seat.Comp.MaxTranslationSpeed * maximumSpeedMultiplier * thrustMultiplier;
            if (speed > maximumSpeed && speed > previousSpeed)
            {
                hover.Comp.GunshipLinearVelocity = hover.Comp.GunshipLinearVelocity / speed *
                    MathF.Max(previousSpeed, maximumSpeed);
            }
        }

        if (hover.Comp.GunshipLinearVelocity == Vector2.Zero)
            return;

        var proposedPosition = position + hover.Comp.GunshipLinearVelocity * frameTime;
        if (IsGunshipFootprintClear((hover.Owner, dropshipGrid), map.Value, proposedPosition, rotation, out var translationBlockers))
            _transform.SetWorldPosition(hover.Owner, proposedPosition);
        else
        {
            _integrity.ApplyFlightImpact(hover.Owner, translationBlockers, hover.Comp.GunshipLinearVelocity.Length());
            hover.Comp.GunshipLinearVelocity = Vector2.Zero;
            PopupGunshipBlocked(seat, "The gunship cannot move into an obstruction.");
        }
    }

    private bool IsGunshipFootprintClear(
        Entity<MapGridComponent> dropship,
        EntityUid targetMap,
        Vector2 targetPosition,
        Angle targetRotation)
    {
        return IsGunshipFootprintClear(dropship, targetMap, targetPosition, targetRotation, out _);
    }

    private bool IsGunshipFootprintClear(
        Entity<MapGridComponent> dropship,
        EntityUid targetMap,
        Vector2 targetPosition,
        Angle targetRotation,
        out HashSet<EntityUid> blockers)
    {
        blockers = new HashSet<EntityUid>();
        if (!TryComp(targetMap, out MapGridComponent? targetGrid))
            return false;

        const CollisionGroup blockMask =
            CollisionGroup.Impassable | CollisionGroup.MidImpassable | CollisionGroup.HighImpassable;

        var blocked = false;
        foreach (var tile in _map.GetAllTiles(dropship.Owner, dropship.Comp))
        {
            var localCenter = _map.TileCenterToVector(dropship.Owner, dropship.Comp, tile.GridIndices);
            var sample = targetPosition + targetRotation.RotateVec(localCenter);
            if (!_map.TryGetTileRef(targetMap, targetGrid, sample, out var targetTile))
                return false;

            var opening = CMUZLevelOpeningCache.IsOpeningTile(targetTile.Tile, _tile);
            if (targetTile.Tile.IsEmpty && !opening)
                return false;

            if (!opening && _turf.IsTileBlocked(targetTile, blockMask))
            {
                blocked = true;
                var foundPhysicalBlocker = false;
                foreach (var anchored in _map.GetAnchoredEntities(targetMap, targetGrid, targetTile.GridIndices))
                {
                    if (!HasComp<FixturesComponent>(anchored))
                        continue;

                    blockers.Add(anchored);
                    foundPhysicalBlocker = true;
                }

                // A blocking tile with no physical anchored entity is terrain
                // supplied by the target grid itself and cannot be rammed away.
                if (!foundPhysicalBlocker)
                    blockers.Add(targetMap);
            }
        }

        return !blocked;
    }

    private void TryChangeGunshipAltitude(Entity<GunshipPilotSeatComponent> seat, int offset)
    {
        if (seat.Comp.Pilot is not { } pilot)
            return;

        if (Transform(seat).GridUid is not { } grid ||
            !TryComp(grid, out DropshipTacticalHoverComponent? hover) ||
            !TryComp(grid, out MapGridComponent? dropshipGrid) ||
            Transform(grid).MapUid is not { } currentMap)
        {
            _popup.PopupEntity("The gunship is not in tactical hover.", seat, pilot, PopupType.MediumCaution);
            return;
        }

        if (hover.AltitudeTransitionAt != null)
        {
            _popup.PopupEntity("The gunship is already changing flight levels.", seat, pilot, PopupType.MediumCaution);
            return;
        }

        if (!_zLevels.TryMapOffset(currentMap, offset, out var targetMap))
        {
            _popup.PopupEntity(offset > 0 ? "No higher flight level is available." : "No lower flight level is available.",
                seat,
                pilot,
                PopupType.MediumCaution);
            return;
        }

        var position = _transform.GetWorldPosition(grid);
        var currentRotation = _transform.GetWorldRotation(grid);
        var snappedDegrees = Math.Round(currentRotation.Degrees / 90d) * 90d;
        var snappedRotation = Angle.FromDegrees(snappedDegrees);

        var clear = IsGunshipFootprintClear((grid, dropshipGrid), targetMap.Value.Owner, position, snappedRotation, out var blockers);
        if (!clear && !CanGunshipCrashThrough(blockers))
        {
            _popup.PopupEntity("The target flight level contains an indestructible obstruction.", seat, pilot, PopupType.MediumCaution);
            return;
        }

        _transform.SetWorldRotation(grid, snappedRotation);
        hover.GunshipLinearVelocity = Vector2.Zero;
        hover.GunshipAngularVelocityDegrees = 0f;
        hover.AltitudeTargetMap = targetMap.Value.Owner;
        hover.AltitudeOffset = offset;
        hover.AltitudeLanding = offset < 0 &&
            (hover.GroundMap == targetMap.Value.Owner || hover.GroundMap is null && hover.GroundMapOffset == -1);
        hover.AltitudePilot = pilot;
        hover.AltitudeTransitionAt = _timing.CurTime + GunshipAltitudeTransitionTime;

        var warningCoords = new EntityCoordinates(targetMap.Value.Owner, position);
        SpawnWarningBorder(warningCoords,
            hover.Footprint,
            snappedRotation,
            (float)GunshipAltitudeTransitionTime.TotalSeconds + 1f);
        _audio.PlayPvs(WarningSound, warningCoords, AudioParams.Default.WithVolume(2f));

        _popup.PopupEntity(hover.AltitudeLanding
                ? "Landing approach committed. Touchdown in five seconds."
                : offset > 0
                    ? "Ascending one flight level in five seconds."
                    : "Descending one flight level in five seconds.",
            seat,
            pilot,
            PopupType.Medium);
    }

    private bool CanGunshipCrashThrough(IReadOnlyCollection<EntityUid> blockers)
    {
        if (blockers.Count == 0)
            return false;

        foreach (var blocker in blockers)
        {
            if (TerminatingOrDeleted(blocker) ||
                !HasComp<DamageableComponent>(blocker) ||
                !HasComp<DestructibleComponent>(blocker))
                return false;
        }

        return true;
    }

    private void ProcessGunshipAltitudeTransitions(TimeSpan now)
    {
        var query = EntityQueryEnumerator<DropshipTacticalHoverComponent, MapGridComponent>();
        while (query.MoveNext(out var grid, out var hover, out var dropshipGrid))
        {
            if (hover.AltitudeTransitionAt is not { } transitionAt || now < transitionAt)
                continue;

            FinishGunshipAltitudeTransition((grid, hover), dropshipGrid);
        }
    }

    private void FinishGunshipAltitudeTransition(
        Entity<DropshipTacticalHoverComponent> hover,
        MapGridComponent dropshipGrid)
    {
        var pilot = hover.Comp.AltitudePilot;
        var targetMap = hover.Comp.AltitudeTargetMap;
        var offset = hover.Comp.AltitudeOffset;
        var landing = hover.Comp.AltitudeLanding;
        ClearGunshipAltitudeTransition(hover.Comp);

        if (targetMap is not { } map ||
            !TryComp(map, out MapComponent? targetMapComp))
        {
            PopupAltitudeTransitionFailure(hover.Owner, pilot, "The target flight level is no longer available.");
            return;
        }

        var position = _transform.GetWorldPosition(hover.Owner);
        var rotation = _transform.GetWorldRotation(hover.Owner);
        var clear = IsGunshipFootprintClear((hover.Owner, dropshipGrid), map, position, rotation, out var blockers);
        if (!clear)
        {
            if (!CanGunshipCrashThrough(blockers))
            {
                PopupAltitudeTransitionFailure(hover.Owner, pilot, "The flight-level change was aborted by an indestructible obstruction.");
                return;
            }

            // A vertical maneuver has no useful linear velocity to derive an
            // impact from, so use a substantial fixed collision speed.
            _integrity.ApplyFlightImpact(hover.Owner, blockers, 5.5f);

            // A lethal impact has handed control to the crash sequence. Do not
            // finish this as an ordinary altitude change as well.
            if (TryComp(hover.Owner, out DropshipIntegrityComponent? integrity) && integrity.Crashing)
                return;
        }

        var targetCoords = new MapCoordinates(position, targetMapComp.MapId);
        _transform.SetMapCoordinates(hover.Owner, targetCoords);
        _transform.SetWorldRotation(hover.Owner, rotation);
        _zLevels.EnsureZLevelViewer(hover.Owner);

        if (hover.Comp.HoverDestination is { } destination && !TerminatingOrDeleted(destination))
        {
            _transform.SetMapCoordinates(destination, targetCoords);
            _transform.SetWorldRotation(destination, rotation);
        }

        if (landing)
        {
            if (hover.Comp.HoverDestination is { } hoverDestination &&
                TryComp(hoverDestination, out EphemeralDropshipDestinationComponent? ephemeral))
            {
                ephemeral.TacticalHover = false;
                ephemeral.ReturnDestination = null;
            }

            RemComp<DropshipTacticalHoverComponent>(hover.Owner);
            if (pilot is { } landingPilot)
                _popup.PopupEntity("The gunship has tactically landed.", hover.Owner, landingPilot, PopupType.Medium);
            return;
        }

        hover.Comp.GroundMapOffset -= offset;
        if (pilot is { } altitudePilot)
        {
            _popup.PopupEntity(offset > 0 ? "The gunship ascends one flight level." : "The gunship descends one flight level.",
                hover.Owner,
                altitudePilot,
                PopupType.Medium);
        }

        if (TryGetControlledGunshipSeat(pilot ?? EntityUid.Invalid, out var seat))
            UpdateGunshipPilotEye(seat, hover);
    }

    private static void ClearGunshipAltitudeTransition(DropshipTacticalHoverComponent hover)
    {
        hover.AltitudeTransitionAt = null;
        hover.AltitudeTargetMap = null;
        hover.AltitudeOffset = 0;
        hover.AltitudeLanding = false;
        hover.AltitudePilot = null;
    }

    private void PopupAltitudeTransitionFailure(EntityUid dropship, EntityUid? pilot, string message)
    {
        if (pilot is { } user && !TerminatingOrDeleted(user))
            _popup.PopupEntity(message, dropship, user, PopupType.MediumCaution);
    }

    private void SetGunshipViewOffset(Entity<GunshipPilotSeatComponent> seat, int requestedOffset)
    {
        if (seat.Comp.Pilot is not { } pilot)
            return;

        if (Transform(seat).GridUid is not { } grid ||
            !TryComp(grid, out DropshipTacticalHoverComponent? hover) ||
            Transform(grid).MapUid is not { } currentMap)
        {
            return;
        }

        var offset = seat.Comp.ViewOffset == requestedOffset ? 0 : requestedOffset;
        if (offset != 0 && !_zLevels.TryMapOffset(currentMap, offset, out _))
        {
            _popup.PopupEntity(offset > 0 ? "No level is available above the gunship." : "No level is available below the gunship.",
                seat,
                pilot,
                PopupType.MediumCaution);
            return;
        }

        seat.Comp.ViewOffset = offset;
        seat.Comp.RearView = false;
        Dirty(seat);
        UpdateGunshipPilotEye(seat, (grid, hover));
    }

    private void SetGunshipRearView(Entity<GunshipPilotSeatComponent> seat)
    {
        if (seat.Comp.Pilot is not { } pilot ||
            Transform(seat).GridUid is not { } grid ||
            !TryComp(grid, out DropshipTacticalHoverComponent? hover))
        {
            return;
        }

        seat.Comp.RearView = !seat.Comp.RearView;
        seat.Comp.ViewOffset = 0;
        Dirty(seat);
        UpdateGunshipPilotEye(seat, (grid, hover));

        _popup.PopupEntity(seat.Comp.RearView ? "Rear camera active." : "Rear camera disengaged.",
            seat,
            pilot,
            PopupType.Small);
    }

    private void EnsureGunshipPilotEye(
        Entity<GunshipPilotSeatComponent> seat,
        Entity<DropshipTacticalHoverComponent> hover)
    {
        if (seat.Comp.Pilot is not { } pilot || TerminatingOrDeleted(pilot))
            return;

        if (seat.Comp.Eye is { } existing && !TerminatingOrDeleted(existing))
            return;

        var gridXform = Transform(hover.Owner);
        var eye = Spawn(seat.Comp.EyePrototype, new MapCoordinates(_transform.GetWorldPosition(hover.Owner), gridXform.MapID));
        var eyeComp = EnsureComp<GunshipPilotEyeComponent>(eye);
        eyeComp.Dropship = hover.Owner;
        eyeComp.Footprint = hover.Comp.Footprint;
        eyeComp.RotationDegrees = (float)_transform.GetWorldRotation(hover.Owner).Degrees;
        eyeComp.ViewOffset = seat.Comp.ViewOffset;
        eyeComp.RearView = seat.Comp.RearView;
        Dirty(eye, eyeComp);
        _zLevels.EnsureZLevelViewer(eye);

        seat.Comp.Eye = eye;
        if (TryComp(pilot, out EyeComponent? pilotEye))
        {
            seat.Comp.OriginalZoom = pilotEye.Zoom;
            seat.Comp.OriginalPvsScale = pilotEye.PvsScale;
        }

        Dirty(seat);
        UpdateGunshipPilotEye(seat, hover);
        _popup.PopupEntity("Gunship flight controls engaged.", seat, pilot, PopupType.Medium);
    }

    private void UpdateGunshipPilotEye(
        Entity<GunshipPilotSeatComponent> seat,
        Entity<DropshipTacticalHoverComponent> hover)
    {
        if (seat.Comp.Eye is not { } eye ||
            TerminatingOrDeleted(eye) ||
            Transform(hover.Owner).MapUid is not { } currentMap)
        {
            return;
        }

        EntityUid viewMap = currentMap;
        if (seat.Comp.ViewOffset != 0 &&
            _zLevels.TryMapOffset(currentMap, seat.Comp.ViewOffset, out var offsetMap))
        {
            viewMap = offsetMap.Value.Owner;
        }

        if (!TryComp(viewMap, out MapComponent? mapComp))
            return;

        var position = _transform.GetWorldPosition(hover.Owner);
        if (seat.Comp.RearView && TryComp(hover.Owner, out MapGridComponent? dropshipGrid))
        {
            var rearCamera = new Vector2(0f, dropshipGrid.LocalAABB.Bottom - 1.5f);
            position += _transform.GetWorldRotation(hover.Owner).RotateVec(rearCamera);
        }

        _transform.SetMapCoordinates(eye, new MapCoordinates(position, mapComp.MapId));
        _zLevels.EnsureZLevelViewer(eye);

        UpdateGunshipCameraMode(seat, hover.Owner);

        var eyeComp = EnsureComp<GunshipPilotEyeComponent>(eye);
        var rotationDegrees = (float)_transform.GetWorldRotation(hover.Owner).Degrees;
        if (eyeComp.Dropship == hover.Owner &&
            eyeComp.Footprint == hover.Comp.Footprint &&
            MathHelper.CloseToPercent(eyeComp.RotationDegrees, rotationDegrees) &&
            eyeComp.ViewOffset == seat.Comp.ViewOffset &&
            eyeComp.RearView == seat.Comp.RearView)
        {
            return;
        }

        eyeComp.Dropship = hover.Owner;
        eyeComp.Footprint = hover.Comp.Footprint;
        eyeComp.RotationDegrees = rotationDegrees;
        eyeComp.ViewOffset = seat.Comp.ViewOffset;
        eyeComp.RearView = seat.Comp.RearView;
        Dirty(eye, eyeComp);
    }

    private void UpdateGunshipCameraMode(Entity<GunshipPilotSeatComponent> seat, EntityUid dropship)
    {
        if (seat.Comp.Pilot is not { } pilot ||
            seat.Comp.Eye is not { } eye ||
            !TryComp(pilot, out EyeComponent? pilotEye))
        {
            return;
        }

        var linked = TryComp(pilot, out GunshipPilotHudComponent? hud) && hud.Dropship == dropship;
        var remote = linked && (seat.Comp.ViewOffset != 0 || seat.Comp.RearView);

        _eye.SetTarget(pilot, remote ? eye : null, pilotEye);
        _eye.SetDrawFov(pilot, !remote, pilotEye);
        _eye.SetPvsScale((pilot, pilotEye), linked ? GunshipPilotPvsScale : seat.Comp.OriginalPvsScale);

        if (linked && seat.Comp.ViewOffset == 0 && !seat.Comp.RearView)
        {
            var cursor = EnsureComp<EyeCursorOffsetComponent>(pilot);
            cursor.MaxOffset = GunshipCursorMaxOffset;
            cursor.OffsetSpeed = 0.35f;
            cursor.PvsIncrease = GunshipCursorPvsIncrease;
            seat.Comp.AddedCursorOffset = true;
        }
        else if (seat.Comp.AddedCursorOffset)
        {
            RemComp<EyeCursorOffsetComponent>(pilot);
            seat.Comp.AddedCursorOffset = false;
        }
    }

    private void TeardownGunshipPilotEye(Entity<GunshipPilotSeatComponent> seat)
    {
        var gunshipEye = seat.Comp.Eye;
        if (gunshipEye is { } controlledEye &&
            seat.Comp.Pilot is { } pilot &&
            !TerminatingOrDeleted(pilot) &&
            TryComp(pilot, out EyeComponent? pilotEye))
        {
            if (pilotEye.Target == controlledEye)
                _eye.SetTarget(pilot, null, pilotEye);
            _eye.SetDrawFov(pilot, true, pilotEye);
            _eye.SetPvsScale((pilot, pilotEye), seat.Comp.OriginalPvsScale);
        }

        if (seat.Comp.Pilot is { } cursorPilot && seat.Comp.AddedCursorOffset && !TerminatingOrDeleted(cursorPilot))
            RemComp<EyeCursorOffsetComponent>(cursorPilot);

        if (gunshipEye is { } eye && !TerminatingOrDeleted(eye))
            QueueDel(eye);

        seat.Comp.Eye = null;
        seat.Comp.ViewOffset = 0;
        seat.Comp.RearView = false;
        seat.Comp.AddedCursorOffset = false;
        seat.Comp.HeldInputs = GunshipControlInput.None;
        seat.Comp.PressedActions = 0;
        Dirty(seat);
    }

    private void StopGunshipControl(Entity<GunshipPilotSeatComponent> seat, bool restorePilot)
    {
        if (seat.Comp.Pilot is { } pilot)
            DisablePilotAlarmAction(pilot, seat);

        TeardownGunshipPilotEye(seat);
        if (restorePilot)
            seat.Comp.Pilot = null;
        Dirty(seat);
    }

    private void PopupGunshipBlocked(Entity<GunshipPilotSeatComponent> seat, string message)
    {
        if (seat.Comp.Pilot is not { } pilot || _timing.CurTime < seat.Comp.NextBlockedPopup)
            return;

        seat.Comp.NextBlockedPopup = _timing.CurTime + GunshipBlockedPopupCooldown;
        _popup.PopupEntity(message, seat, pilot, PopupType.SmallCaution);
    }

    private void UpdateGunshipPilotHuds()
    {
        UpdateGunshipAlarms();

        var validWearers = new HashSet<EntityUid>();
        var visorQuery = EntityQueryEnumerator<GunshipPilotVisorComponent, TransformComponent>();
        while (visorQuery.MoveNext(out var visor, out _, out var visorXform))
        {
            var wearer = visorXform.ParentUid;
            if (wearer == visor ||
                !_pilotInventory.TryGetSlotEntity(wearer, "head", out var wornHead) ||
                wornHead != visor)
            {
                continue;
            }

            validWearers.Add(wearer);
            var hud = EnsureComp<GunshipPilotHudComponent>(wearer);
            var visorChanged = hud.Visor != visor;
            if (visorChanged && hud.Visor != EntityUid.Invalid)
                CleanupGunshipNightVision((wearer, hud));
            hud.Visor = visor;

            EntityUid? dropship = null;
            Vector2 velocity = Vector2.Zero;
            var rotationDegrees = 0f;
            var integrity = 0f;
            var maxIntegrity = 0f;
            var thrustPercent = 0f;
            var hasDirectFireWeapon = false;
            var directFireAmmo = -1;
            var malfunctions = new List<DropshipMalfunction>();
            var alarms = new List<DropshipAlarm>();
            var masterAlarmSilenced = false;
            var viewOffset = 0;
            var rearView = false;

            if (TryGetControlledGunshipSeat(wearer, out var seat) &&
                Transform(seat).GridUid is { } grid &&
                HasComp<DropshipComponent>(grid))
            {
                dropship = grid;
                rotationDegrees = (float)_transform.GetWorldRotation(grid).Degrees;
                viewOffset = seat.Comp.ViewOffset;
                rearView = seat.Comp.RearView;
                thrustPercent = seat.Comp.ThrustPercent;

                if (TryComp(grid, out DropshipTacticalHoverComponent? hover))
                    velocity = hover.GunshipLinearVelocity;

                if (TryComp(grid, out DropshipIntegrityComponent? dropshipIntegrity))
                {
                    integrity = dropshipIntegrity.Integrity;
                    maxIntegrity = dropshipIntegrity.MaxIntegrity;
                    malfunctions.AddRange(dropshipIntegrity.ActiveMalfunctions);
                    masterAlarmSilenced = dropshipIntegrity.MasterAlarmSilenced;
                    if (dropshipIntegrity.ProximityAlarmActive)
                        alarms.Add(DropshipAlarm.Proximity);
                    if (dropshipIntegrity.LowIntegrityAlarmActive)
                        alarms.Add(DropshipAlarm.LowIntegrity);
                }

                hasDirectFireWeapon = TryGetDirectFireMount(grid, out _, out _, out _, out _, out var directAmmo);
                if (hasDirectFireWeapon)
                    directFireAmmo = directAmmo?.Rounds ?? 0;
            }

            if (visorChanged ||
                hud.Dropship != dropship ||
                hud.LinearVelocity != velocity ||
                !MathHelper.CloseToPercent(hud.ShipRotationDegrees, rotationDegrees) ||
                !MathHelper.CloseToPercent(hud.Integrity, integrity) ||
                !MathHelper.CloseToPercent(hud.MaxIntegrity, maxIntegrity) ||
                !MathHelper.CloseToPercent(hud.ThrustPercent, thrustPercent) ||
                hud.HasDirectFireWeapon != hasDirectFireWeapon ||
                hud.DirectFireAmmo != directFireAmmo ||
                !hud.Malfunctions.SequenceEqual(malfunctions) ||
                !hud.Alarms.SequenceEqual(alarms) ||
                hud.MasterAlarmSilenced != masterAlarmSilenced ||
                hud.ViewOffset != viewOffset ||
                hud.RearView != rearView)
            {
                hud.Dropship = dropship;
                hud.LinearVelocity = velocity;
                hud.ShipRotationDegrees = rotationDegrees;
                hud.Integrity = integrity;
                hud.MaxIntegrity = maxIntegrity;
                hud.ThrustPercent = thrustPercent;
                hud.HasDirectFireWeapon = hasDirectFireWeapon;
                hud.DirectFireAmmo = directFireAmmo;
                hud.Malfunctions = malfunctions;
                hud.Alarms = alarms;
                hud.MasterAlarmSilenced = masterAlarmSilenced;
                hud.ViewOffset = viewOffset;
                hud.RearView = rearView;
                Dirty(wearer, hud);
            }

            UpdateGunshipNightVision((wearer, hud), visor, dropship != null);
        }

        var hudQuery = EntityQueryEnumerator<GunshipPilotHudComponent>();
        while (hudQuery.MoveNext(out var wearer, out var hud))
        {
            if (validWearers.Contains(wearer))
                continue;

            CleanupGunshipNightVision((wearer, hud));
            RemCompDeferred<GunshipPilotHudComponent>(wearer);
        }
    }

    private void UpdateGunshipAlarms()
    {
        var now = _timing.CurTime;
        if (now < _nextGunshipAlarmUpdate)
            return;

        _nextGunshipAlarmUpdate = now + GunshipAlarmUpdateInterval;
        var query = EntityQueryEnumerator<DropshipIntegrityComponent>();
        while (query.MoveNext(out var dropship, out var integrity))
        {
            var proximityHazards = new List<Vector2>();
            var lowIntegrity = !integrity.Wrecked &&
                integrity.MaxIntegrity > 0f &&
                integrity.Integrity > 0f &&
                integrity.Integrity / integrity.MaxIntegrity <= 0.25f;
            var proximity = !integrity.Wrecked &&
                HasComp<DropshipTacticalHoverComponent>(dropship) &&
                TryComp(dropship, out MapGridComponent? dropshipGrid) &&
                IsGunshipNearObstruction((dropship, dropshipGrid), proximityHazards);

            if (integrity.LowIntegrityAlarmActive != lowIntegrity ||
                integrity.ProximityAlarmActive != proximity ||
                !integrity.ProximityHazards.SequenceEqual(proximityHazards))
            {
                integrity.LowIntegrityAlarmActive = lowIntegrity;
                integrity.ProximityAlarmActive = proximity;
                integrity.ProximityHazards = proximityHazards;
                Dirty(dropship, integrity);
            }

            UpdateGunshipAlarmAudio((dropship, integrity));
        }
    }

    private bool IsGunshipNearObstruction(Entity<MapGridComponent> dropship, List<Vector2> hazards)
    {
        var xform = Transform(dropship);
        if (xform.MapUid is not { } targetMap ||
            !TryComp(targetMap, out MapGridComponent? targetGrid))
        {
            return false;
        }

        const CollisionGroup blockMask =
            CollisionGroup.Impassable | CollisionGroup.MidImpassable | CollisionGroup.HighImpassable;

        var position = _transform.GetWorldPosition(xform);
        var rotation = _transform.GetWorldRotation(xform);
        var occupied = new HashSet<Vector2i>();
        foreach (var tile in _map.GetAllTiles(dropship.Owner, dropship.Comp))
        {
            var localCenter = _map.TileCenterToVector(dropship.Owner, dropship.Comp, tile.GridIndices);
            var sample = position + rotation.RotateVec(localCenter);
            if (_map.TryGetTileRef(targetMap, targetGrid, sample, out var targetTile))
                occupied.Add(targetTile.GridIndices);
        }

        var hazardousTiles = new HashSet<Vector2i>();
        foreach (var tile in occupied)
        {
            foreach (var offset in GunshipProximityOffsets)
            {
                var nearby = tile + offset;
                if (occupied.Contains(nearby) ||
                    !_map.TryGetTileRef(targetMap, targetGrid, nearby, out var nearbyTile))
                {
                    continue;
                }

                if (_turf.IsTileBlocked(nearbyTile, blockMask))
                    hazardousTiles.Add(nearby);
            }
        }

        foreach (var tile in hazardousTiles.OrderBy(tile => tile.X).ThenBy(tile => tile.Y))
        {
            var coordinates = _map.GridTileToLocal(targetMap, targetGrid, tile);
            hazards.Add(_transform.ToMapCoordinates(coordinates).Position);
        }

        return hazards.Count > 0;
    }

    private void UpdateGunshipAlarmAudio(Entity<DropshipIntegrityComponent> dropship)
    {
        UpdateGunshipAlarmStream(dropship.Owner,
            dropship.Comp.ProximityAlarmActive && !dropship.Comp.MasterAlarmSilenced,
            dropship.Comp.ProximityAlarmSound,
            ref dropship.Comp.ProximityAlarmStream);
        UpdateGunshipAlarmStream(dropship.Owner,
            dropship.Comp.LowIntegrityAlarmActive && !dropship.Comp.MasterAlarmSilenced,
            dropship.Comp.LowIntegrityAlarmSound,
            ref dropship.Comp.LowIntegrityAlarmStream);
    }

    private void UpdateGunshipAlarmStream(
        EntityUid dropship,
        bool active,
        SoundSpecifier sound,
        ref EntityUid? stream)
    {
        if (!active)
        {
            stream = _audio.Stop(stream);
            return;
        }

        if (stream is { } playing && !TerminatingOrDeleted(playing))
            return;

        stream = _audio.PlayPvs(sound, dropship, GunshipAlarmAudioParams)?.Entity;
    }

    private void OnDropshipIntegrityShutdown(Entity<DropshipIntegrityComponent> ent, ref ComponentShutdown args)
    {
        ent.Comp.ProximityAlarmStream = _audio.Stop(ent.Comp.ProximityAlarmStream);
        ent.Comp.LowIntegrityAlarmStream = _audio.Stop(ent.Comp.LowIntegrityAlarmStream);
    }

    private void UpdateGunshipNightVision(Entity<GunshipPilotHudComponent> wearer, EntityUid visor, bool linked)
    {
        if (!linked)
        {
            CleanupGunshipNightVision(wearer);
            return;
        }

        if (!wearer.Comp.AddedNightVisionItem)
        {
            wearer.Comp.AddedNightVisionItem = true;
            var pilotVision = Comp<GunshipPilotVisorComponent>(visor);
            _nightVision.EnableLinkedHeadNightVision(visor,
                wearer.Owner,
                pilotVision.NightVisionTint,
                pilotVision.NightVisionNoiseStrength,
                pilotVision.NightVisionVignetteStrength);
            return;
        }

        if (TryComp(visor, out NightVisionItemComponent? item) && item.User != wearer.Owner)
            _nightVision.EnableNightVisionItem((visor, item), wearer.Owner);
    }

    private void CleanupGunshipNightVision(Entity<GunshipPilotHudComponent> wearer)
    {
        if (!wearer.Comp.AddedNightVisionItem ||
            wearer.Comp.Visor == EntityUid.Invalid ||
            TerminatingOrDeleted(wearer.Comp.Visor))
        {
            wearer.Comp.AddedNightVisionItem = false;
            return;
        }

        if (TryComp(wearer.Comp.Visor, out NightVisionItemComponent? item))
        {
            _nightVision.DisableNightVisionItem((wearer.Comp.Visor, item), wearer.Owner);
            RemComp<NightVisionItemComponent>(wearer.Comp.Visor);
        }

        wearer.Comp.AddedNightVisionItem = false;
    }
}

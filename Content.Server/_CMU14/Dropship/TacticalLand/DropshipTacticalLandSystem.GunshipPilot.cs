using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Shared._CMU14.Dropship.DirectFire;
using Content.Shared._CMU14.Dropship.GunshipControls;
using Content.Shared._CMU14.Dropship.Integrity;
using Content.Shared._CMU14.Dropship.TacticalLand;
using Content.Shared._CMU14.ZLevels.Core;
using Content.Shared._RMC14.Dropship;
using Content.Shared._RMC14.Dropship.AttachmentPoint;
using Content.Shared._RMC14.Dropship.Weapon;
using Content.Shared._RMC14.Camera;
using Content.Shared._RMC14.NightVision;
using Content.Shared._RMC14.PowerLoader;
using Content.Shared._RMC14.Vehicle;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Actions;
using Content.Shared.Buckle.Components;
using Content.Shared.Damage;
using Content.Shared.Eye;
using Content.Shared.Inventory;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Components;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.UserInterface;
using Content.Shared.Weapons.Ranged.Components;
using Content.Server.Movement.Components;
using Content.Server.Destructible;
using Robust.Shared.Audio;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Physics;
using Robust.Server.GameObjects;

namespace Content.Server._CMU14.Dropship.TacticalLand;

public sealed partial class DropshipTacticalLandSystem
{
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private InventorySystem _pilotInventory = default!;
    [Dependency] private SharedNightVisionSystem _nightVision = default!;
    [Dependency] private SharedContainerSystem _gunshipContainers = default!;
    [Dependency] private SharedAppearanceSystem _directFireAppearance = default!;
    [Dependency] private SharedActionsSystem _gunshipActions = default!;
    [Dependency] private SharedHandsSystem _gunshipHands = default!;
    [Dependency] private SharedVirtualItemSystem _gunshipVirtualItems = default!;
    [Dependency] private ViewSubscriberSystem _gunshipViewSubscriber = default!;

    private static readonly TimeSpan GunshipBlockedPopupCooldown = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GunshipAlarmUpdateInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan GunshipHudUpdateInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan GunshipCameraUpdateInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StationaryProximityRefreshInterval = TimeSpan.FromSeconds(1);
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
    // EyeCursorOffsetSystem caps movement by this many tiles per rendered
    // update. This exceeds the maximum possible 48-tile opposite-edge delta,
    // making the pilot camera track the cursor without easing.
    private const float GunshipCursorPanSpeed = 64f;
    private const float GunshipCursorPvsIncrease = 2.4f;
    private const float GunshipPilotPvsScale = 1f + GunshipCursorPvsIncrease;
    private TimeSpan _nextGunshipAlarmUpdate;
    private TimeSpan _nextGunshipHudUpdate;
    private readonly HashSet<EntityUid> _validGunshipHudWearers = new();
    private readonly HashSet<Vector2i> _proximityOccupiedTiles = new();
    private readonly HashSet<Vector2i> _proximityHazardTiles = new();
    private readonly List<Vector2> _proximityHazardsScratch = new();

    private void InitializeGunshipPilot()
    {
        SubscribeLocalEvent<GunshipPilotSeatComponent, StrapAttemptEvent>(OnGunshipSeatStrapAttempt);
        SubscribeLocalEvent<GunshipPilotSeatComponent, StrappedEvent>(OnGunshipSeatStrapped);
        SubscribeLocalEvent<GunshipPilotSeatComponent, UnstrappedEvent>(OnGunshipSeatUnstrapped);
        SubscribeLocalEvent<GunshipPilotSeatComponent, ComponentShutdown>(OnGunshipSeatShutdown);
        SubscribeLocalEvent<GunshipPilotSeatComponent, GunshipMasterAlarmToggleActionEvent>(OnMasterAlarmToggle);
        SubscribeLocalEvent<GunshipPilotSeatComponent, GunshipCycleCameraActionEvent>(OnCycleCamera);
        SubscribeLocalEvent<GunshipPilotSeatComponent, GunshipDropshipOutlineToggleActionEvent>(OnDropshipOutlineToggle);
        SubscribeLocalEvent<GunshipPilotSeatComponent, GunshipPilotPanningToggleActionEvent>(OnPilotPanningToggle);
        SubscribeLocalEvent<GunshipPilotSeatComponent, GunshipPilotZoomToggleActionEvent>(OnPilotZoomToggle);
        SubscribeLocalEvent<DropshipTacticalHoverComponent, GunshipCrashStartedEvent>(OnGunshipCrashStarted);
        SubscribeLocalEvent<DropshipIntegrityComponent, ComponentShutdown>(OnDropshipIntegrityShutdown);
        SubscribeNetworkEvent<GunshipControlInputEvent>(OnGunshipControlInput);
        SubscribeNetworkEvent<GunshipThrustAdjustEvent>(OnGunshipThrustAdjust);
        SubscribeNetworkEvent<GunshipCycleCameraInputEvent>(OnGunshipCycleCameraInput);
        SubscribeNetworkEvent<GunshipPilotPanningInputEvent>(OnGunshipPilotPanningInput);
        SubscribeNetworkEvent<GunshipOpenNavigationInputEvent>(OnGunshipOpenNavigationInput);
        SubscribeNetworkEvent<GunshipDirectFireAimEvent>(OnGunshipDirectFireAim);
    }

    private void OnGunshipOpenNavigationInput(GunshipOpenNavigationInputEvent ev, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { } pilot ||
            !TryGetControlledGunshipSeat(pilot, out var seat) ||
            Transform(seat).GridUid is not { } dropship)
        {
            return;
        }

        EntityUid? navigationConsole = null;
        var query = EntityQueryEnumerator<DropshipNavigationComputerComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid != dropship)
                continue;

            navigationConsole ??= uid;
            if (HasComp<GunshipControlsComponent>(uid))
            {
                navigationConsole = uid;
                break;
            }
        }

        if (navigationConsole is not { } console)
            return;

        var before = new BeforeActivatableUIOpenEvent(pilot);
        RaiseLocalEvent(console, before);

        if (!_ui.TryOpenUi(console, DropshipNavigationUiKey.Key, pilot))
            return;

        var after = new AfterActivatableUIOpenEvent(pilot, pilot);
        RaiseLocalEvent(console, after);
    }

    private void OnGunshipDirectFireAim(GunshipDirectFireAimEvent ev, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { } pilot ||
            !TryGetPilotDirectFireMount(pilot, out var grid, out _, out var point, out _, out var weapon, out _, out _))
        {
            return;
        }

        TryAimDirectFireMount(grid, point, weapon, GetCoordinates(ev.Coordinates), out _);
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
        GunshipDirectFireWeaponComponent weapon,
        EntityCoordinates targetCoordinates,
        out Vector2 direction)
    {
        direction = default;
        var target = _transform.ToMapCoordinates(targetCoordinates);
        if (target.MapId != Transform(grid).MapID)
            return false;

        var shipRotation = _transform.GetWorldRotation(grid);
        if (!GunshipDirectFireSystem.TryGetClampedAim(
                shipRotation,
                _transform.GetWorldPosition(grid),
                target.Position,
                weapon.GimbalDegrees,
                out direction,
                out var aimDegrees))
        {
            return false;
        }

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
        if (!_gunshipOverhaulEnabled)
        {
            args.Cancelled = true;
            if (args.Popup)
                _popup.PopupEntity("Gunship flight controls are currently disabled.",
                    ent,
                    args.User ?? args.Buckle.Owner,
                    PopupType.MediumCaution);
            return;
        }

        if (_gunshipHands.CountFreeHands(args.Buckle.Owner) < _gunshipHands.GetHandCount(args.Buckle.Owner))
        {
            args.Cancelled = true;
            if (args.Popup)
                _popup.PopupEntity(Loc.GetString("emplacement-mount-need-hands-free"), ent, args.User ?? args.Buckle.Owner, PopupType.MediumCaution);
            return;
        }

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
        ent.Comp.ManeuveringCamera = GunshipManeuveringCamera.None;
        ent.Comp.ShowDropshipOutline = true;
        ent.Comp.PilotPanning = true;
        ent.Comp.PilotZoom = false;
        Dirty(ent);

        EnablePilotHudActions(args.Buckle, ent);
        OccupyPilotHands(args.Buckle, ent);
        EnsurePilotControlComponents(args.Buckle, ent.Comp);

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

    private void OccupyPilotHands(EntityUid pilot, EntityUid seat)
    {
        var handsToOccupy = Math.Min(2, _gunshipHands.GetHandCount(pilot));
        for (var i = 0; i < handsToOccupy; i++)
        {
            if (_gunshipVirtualItems.TrySpawnVirtualItemInHand(seat, pilot, out var virtualItem))
                EnsureComp<UnremoveableComponent>(virtualItem.Value);
        }
    }

    private void EnsurePilotControlComponents(EntityUid pilot, GunshipPilotSeatComponent seat)
    {
        // Keep these networked components stable for the complete seated
        // session. Adding or removing them exactly when hover starts can race
        // client movement prediction rollback on the player entity.
        if (!HasComp<EyeCursorOffsetComponent>(pilot))
        {
            EnsureComp<EyeCursorOffsetComponent>(pilot);
            seat.AddedCursorOffset = true;
        }

        // EyeCursorOffset defaults to a small amount of panning. Keep the
        // component inert until the lowered pilot HUD is actually linked.
        if (TryComp(pilot, out EyeCursorOffsetComponent? cursor))
        {
            cursor.MaxOffset = 0f;
            cursor.OffsetSpeed = GunshipCursorPanSpeed;
            cursor.PvsIncrease = 0f;
        }

        EnsureComp<RemoteWeaponOperatorComponent>(pilot);
    }

    private void OnGunshipCrashStarted(
        Entity<DropshipTacticalHoverComponent> ent,
        ref GunshipCrashStartedEvent args)
    {
        var query = EntityQueryEnumerator<GunshipPilotSeatComponent>();
        while (query.MoveNext(out var seatUid, out var seat))
        {
            if (Transform(seatUid).GridUid != ent.Owner)
                continue;

            seat.HeldInputs = GunshipControlInput.None;
            seat.PressedActions = 0;
            seat.ManeuveringCamera = GunshipManeuveringCamera.None;
            _gunshipActions.SetToggled(seat.CameraCycleAction, false);
            TeardownGunshipPilotEye((seatUid, seat));
            Dirty(seatUid, seat);
        }
    }

    private void EnablePilotHudActions(EntityUid pilot, Entity<GunshipPilotSeatComponent> seat)
    {
        if (Transform(seat).GridUid is not { } dropship ||
            !TryComp(dropship, out DropshipIntegrityComponent? integrity))
        {
            return;
        }

        seat.Comp.MasterAlarmAction ??= _gunshipActions.AddAction(pilot,
            seat.Comp.MasterAlarmActionId,
            seat.Owner);
        seat.Comp.CameraCycleAction ??= _gunshipActions.AddAction(pilot,
            seat.Comp.CameraCycleActionId,
            seat.Owner);
        seat.Comp.DropshipOutlineAction ??= _gunshipActions.AddAction(pilot,
            seat.Comp.DropshipOutlineActionId,
            seat.Owner);
        seat.Comp.PilotPanningAction ??= _gunshipActions.AddAction(pilot,
            seat.Comp.PilotPanningActionId,
            seat.Owner);
        seat.Comp.PilotZoomAction ??= _gunshipActions.AddAction(pilot,
            seat.Comp.PilotZoomActionId,
            seat.Owner);
        _gunshipActions.SetToggled(seat.Comp.MasterAlarmAction, integrity.MasterAlarmSilenced);
        _gunshipActions.SetToggled(seat.Comp.CameraCycleAction,
            seat.Comp.ManeuveringCamera != GunshipManeuveringCamera.None);
        _gunshipActions.SetToggled(seat.Comp.DropshipOutlineAction, seat.Comp.ShowDropshipOutline);
        _gunshipActions.SetToggled(seat.Comp.PilotPanningAction, seat.Comp.PilotPanning);
        _gunshipActions.SetToggled(seat.Comp.PilotZoomAction, seat.Comp.PilotZoom);
    }

    private void DisablePilotHudActions(EntityUid pilot, Entity<GunshipPilotSeatComponent> seat)
    {
        _gunshipActions.RemoveAction(pilot, seat.Comp.MasterAlarmAction);
        _gunshipActions.RemoveAction(pilot, seat.Comp.CameraCycleAction);
        _gunshipActions.RemoveAction(pilot, seat.Comp.DropshipOutlineAction);
        _gunshipActions.RemoveAction(pilot, seat.Comp.PilotPanningAction);
        _gunshipActions.RemoveAction(pilot, seat.Comp.PilotZoomAction);
        seat.Comp.MasterAlarmAction = null;
        seat.Comp.CameraCycleAction = null;
        seat.Comp.DropshipOutlineAction = null;
        seat.Comp.PilotPanningAction = null;
        seat.Comp.PilotZoomAction = null;
    }

    private void OnCycleCamera(
        Entity<GunshipPilotSeatComponent> ent,
        ref GunshipCycleCameraActionEvent args)
    {
        if (!CanToggleManeuveringCamera(ent, args.Performer))
            return;

        args.Handled = true;
        CycleManeuveringCamera(ent);
    }

    private void OnGunshipCycleCameraInput(GunshipCycleCameraInputEvent args, EntitySessionEventArgs session)
    {
        if (session.SenderSession.AttachedEntity is not { } pilot ||
            !TryGetControlledGunshipSeat(pilot, out var seat) ||
            !CanToggleManeuveringCamera(seat, pilot))
        {
            return;
        }

        CycleManeuveringCamera(seat);
    }

    private void CycleManeuveringCamera(Entity<GunshipPilotSeatComponent> seat)
    {
        seat.Comp.ManeuveringCamera = seat.Comp.ManeuveringCamera switch
        {
            GunshipManeuveringCamera.None => GunshipManeuveringCamera.Rear,
            GunshipManeuveringCamera.Rear => GunshipManeuveringCamera.Lower,
            GunshipManeuveringCamera.Lower => GunshipManeuveringCamera.Upper,
            _ => GunshipManeuveringCamera.None,
        };
        FinishManeuveringCameraToggle(seat);
    }

    private bool CanToggleManeuveringCamera(Entity<GunshipPilotSeatComponent> ent, EntityUid performer)
    {
        if (ent.Comp.Pilot != performer ||
            !TryGetControlledGunshipSeat(performer, out var seat) ||
            seat.Owner != ent.Owner ||
            Transform(ent).GridUid is not { } dropship)
        {
            return false;
        }

        if (!TryComp(dropship, out DropshipIntegrityComponent? integrity) ||
            !integrity.ActiveMalfunctions.Contains(DropshipMalfunction.SensorArrayFault))
        {
            return true;
        }

        _popup.PopupEntity("Sensor array fault detected. Maneuvering cameras are unavailable.",
            ent,
            performer,
            PopupType.SmallCaution);
        return false;
    }

    private void FinishManeuveringCameraToggle(Entity<GunshipPilotSeatComponent> seat)
    {
        seat.Comp.NextCameraUpdate = TimeSpan.Zero;
        UpdateGunshipCameraSubscription(seat);
        Dirty(seat);
        _gunshipActions.SetToggled(seat.Comp.CameraCycleAction,
            seat.Comp.ManeuveringCamera != GunshipManeuveringCamera.None);
    }

    private void OnDropshipOutlineToggle(
        Entity<GunshipPilotSeatComponent> ent,
        ref GunshipDropshipOutlineToggleActionEvent args)
    {
        if (ent.Comp.Pilot != args.Performer ||
            !TryGetControlledGunshipSeat(args.Performer, out var seat) ||
            seat.Owner != ent.Owner)
        {
            return;
        }

        args.Handled = true;
        ent.Comp.ShowDropshipOutline = !ent.Comp.ShowDropshipOutline;
        Dirty(ent);
        _gunshipActions.SetToggled(ent.Comp.DropshipOutlineAction, ent.Comp.ShowDropshipOutline);
    }

    private void OnPilotPanningToggle(
        Entity<GunshipPilotSeatComponent> ent,
        ref GunshipPilotPanningToggleActionEvent args)
    {
        if (ent.Comp.Pilot != args.Performer ||
            !TryGetControlledGunshipSeat(args.Performer, out var seat) ||
            seat.Owner != ent.Owner ||
            Transform(ent).GridUid is not { } grid)
        {
            return;
        }

        args.Handled = true;
        TogglePilotPanning(ent, grid);
    }

    private void OnGunshipPilotPanningInput(GunshipPilotPanningInputEvent args, EntitySessionEventArgs session)
    {
        if (session.SenderSession.AttachedEntity is not { } pilot ||
            !TryGetControlledGunshipSeat(pilot, out var seat) ||
            Transform(seat).GridUid is not { } grid)
        {
            return;
        }

        TogglePilotPanning(seat, grid);
    }

    private void TogglePilotPanning(Entity<GunshipPilotSeatComponent> ent, EntityUid grid)
    {
        if (ent.Comp.Pilot is not { } pilot ||
            !TryComp(pilot, out GunshipPilotHudComponent? hud) ||
            hud.Dropship != grid)
        {
            return;
        }

        ent.Comp.PilotPanning = !ent.Comp.PilotPanning;
        if (ent.Comp.PilotPanning && ent.Comp.PilotZoom)
        {
            ent.Comp.PilotZoom = false;
            _gunshipActions.SetToggled(ent.Comp.PilotZoomAction, false);
        }

        Dirty(ent);
        _gunshipActions.SetToggled(ent.Comp.PilotPanningAction, ent.Comp.PilotPanning);
        UpdateGunshipCameraMode(ent, grid);
    }

    private void OnPilotZoomToggle(
        Entity<GunshipPilotSeatComponent> ent,
        ref GunshipPilotZoomToggleActionEvent args)
    {
        if (ent.Comp.Pilot != args.Performer ||
            !TryGetControlledGunshipSeat(args.Performer, out var seat) ||
            seat.Owner != ent.Owner)
        {
            return;
        }

        args.Handled = true;
        ent.Comp.PilotZoom = !ent.Comp.PilotZoom;
        if (ent.Comp.PilotZoom && ent.Comp.PilotPanning)
        {
            ent.Comp.PilotPanning = false;
            _gunshipActions.SetToggled(ent.Comp.PilotPanningAction, false);
        }

        Dirty(ent);
        _gunshipActions.SetToggled(ent.Comp.PilotZoomAction, ent.Comp.PilotZoom);

        if (Transform(ent).GridUid is { } grid)
            UpdateGunshipCameraMode(ent, grid);
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

        if (Transform(seat).GridUid is not { } grid ||
            !HasComp<DropshipTacticalHoverComponent>(grid))
        {
            seat.Comp.HeldInputs = GunshipControlInput.None;
            seat.Comp.PressedActions = 0;
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

        if (ev.Action is GunshipControlAction.Ascend or GunshipControlAction.Descend)
        {
            if (!ev.Pressed)
                return;

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
            Transform(seat).GridUid is not { } grid ||
            !HasComp<DropshipTacticalHoverComponent>(grid) ||
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
        if (!_gunshipOverhaulEnabled)
            return;

        if (_timing.CurTime >= _nextGunshipHudUpdate)
        {
            _nextGunshipHudUpdate = _timing.CurTime + GunshipHudUpdateInterval;
            UpdateGunshipPilotHuds();
        }

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

            if (TryComp(grid, out DropshipComponent? dropship) && dropship.Crashed ||
                TryComp(grid, out DropshipIntegrityComponent? crashIntegrity) &&
                (crashIntegrity.Crashing || crashIntegrity.Wrecked))
            {
                seat.HeldInputs = GunshipControlInput.None;
                TeardownGunshipPilotEye((seatUid, seat));
                continue;
            }

            if (TryComp(grid, out DropshipIntegrityComponent? integrity) &&
                integrity.ActiveMalfunctions.Contains(DropshipMalfunction.SensorArrayFault) &&
                (seat.ViewOffset != 0 || seat.RearView || HasAnyManeuveringCamera(seat)))
            {
                seat.ViewOffset = 0;
                seat.RearView = false;
                seat.ManeuveringCamera = GunshipManeuveringCamera.None;
                _gunshipActions.SetToggled(seat.CameraCycleAction, false);
                UpdateGunshipCameraSubscription((seatUid, seat));
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

        if (!hover.Comp.FlightGridChildrenInitialized)
        {
            var children = Transform(hover.Owner).ChildEnumerator;
            while (children.MoveNext(out var child))
                hover.Comp.FlightGridChildren.Add(child);

            hover.Comp.FlightGridChildrenInitialized = true;
        }

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
            hover.Comp.FlightTerrainAnchors.Clear();
            var proposedRotation = rotation +
                Angle.FromDegrees(hover.Comp.GunshipAngularVelocityDegrees * frameTime);
            if (IsGunshipFootprintClear((hover.Owner, dropshipGrid), map.Value, position, proposedRotation,
                    boundaryOnly: false, out var rotationBlockers))
            {
                rotation = proposedRotation;
                _transform.SetWorldRotation(hover.Owner, rotation);
                _integrity.GuardFlightAdoptions(hover.Owner, map.Value, hover.Comp.FlightGridChildren,
                    hover.Comp.FlightTerrainAnchors);
            }
            else
            {
                var radius = MathF.Max(dropshipGrid.LocalAABB.Width, dropshipGrid.LocalAABB.Height) * 0.5f;
                var impactSpeed = MathF.Abs(MathHelper.DegreesToRadians(hover.Comp.GunshipAngularVelocityDegrees)) * radius;
                var remainingImpactSpeed = _integrity.ApplyFlightImpact(
                    hover.Owner,
                    rotationBlockers,
                    impactSpeed,
                    map.Value);
                if (remainingImpactSpeed > 0f &&
                    IsGunshipFootprintClearAfterImpact((hover.Owner, dropshipGrid), map.Value, position, proposedRotation))
                {
                    var remainingRadians = remainingImpactSpeed / MathF.Max(radius, 0.001f);
                    hover.Comp.GunshipAngularVelocityDegrees = MathF.CopySign(
                        MathHelper.RadiansToDegrees(remainingRadians),
                        hover.Comp.GunshipAngularVelocityDegrees);
                    rotation = proposedRotation;
                    _transform.SetWorldRotation(hover.Owner, rotation);
                    _integrity.GuardFlightAdoptions(hover.Owner, map.Value, hover.Comp.FlightGridChildren,
                        hover.Comp.FlightTerrainAnchors);
                }
                else
                {
                    hover.Comp.GunshipAngularVelocityDegrees = 0f;
                    PopupGunshipBlocked(seat, "The gunship cannot rotate into an obstruction.");
                }
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

        hover.Comp.FlightTerrainAnchors.Clear();
        var proposedPosition = position + hover.Comp.GunshipLinearVelocity * frameTime;
        if (IsGunshipFlightPathClear((hover.Owner, dropshipGrid), map.Value, position, proposedPosition, rotation,
                out var translationBlockers))
        {
            _transform.SetWorldPosition(hover.Owner, proposedPosition);
            _integrity.GuardFlightAdoptions(hover.Owner, map.Value, hover.Comp.FlightGridChildren,
                hover.Comp.FlightTerrainAnchors);
        }
        else
        {
            var remainingSpeed = _integrity.ApplyFlightImpact(
                hover.Owner,
                translationBlockers,
                hover.Comp.GunshipLinearVelocity.Length(),
                map.Value);
            if (remainingSpeed > 0f &&
                IsGunshipFootprintClearAfterImpact((hover.Owner, dropshipGrid), map.Value, proposedPosition, rotation))
            {
                hover.Comp.GunshipLinearVelocity = Vector2.Normalize(hover.Comp.GunshipLinearVelocity) * remainingSpeed;
                _transform.SetWorldPosition(hover.Owner, proposedPosition);
                _integrity.GuardFlightAdoptions(hover.Owner, map.Value, hover.Comp.FlightGridChildren,
                    hover.Comp.FlightTerrainAnchors);
            }
            else
            {
                hover.Comp.GunshipLinearVelocity = Vector2.Zero;
                PopupGunshipBlocked(seat, "The gunship cannot move into an obstruction.");
            }
        }

    }

    private bool IsGunshipFootprintClearAfterImpact(
        Entity<MapGridComponent> dropship,
        EntityUid targetMap,
        Vector2 targetPosition,
        Angle targetRotation)
    {
        if (IsGunshipFootprintClear(dropship, targetMap, targetPosition, targetRotation,
                boundaryOnly: false, out var remainingBlockers))
            return true;

        // Destructible deletion is queued until the end of the tick, so its
        // fixture can still be returned by the map query which immediately
        // follows the damage event. It is safe to continue when every remaining
        // blocker is already terminating; anything durable still stops us.
        return remainingBlockers.Count > 0 && remainingBlockers.All(uid => TerminatingOrDeleted(uid));
    }

    private bool IsGunshipFootprintClear(
        Entity<MapGridComponent> dropship,
        EntityUid targetMap,
        Vector2 targetPosition,
        Angle targetRotation)
    {
        return IsGunshipFootprintClear(dropship, targetMap, targetPosition, targetRotation, out _);
    }

    private bool IsGunshipFlightPathClear(
        Entity<MapGridComponent> dropship,
        EntityUid targetMap,
        Vector2 startPosition,
        Vector2 targetPosition,
        Angle targetRotation,
        out HashSet<EntityUid> blockers)
    {
        var distance = Vector2.Distance(startPosition, targetPosition);
        var steps = Math.Max(1, (int) MathF.Ceiling(distance / 0.5f));
        for (var step = 1; step <= steps; step++)
        {
            var position = Vector2.Lerp(startPosition, targetPosition, step / (float) steps);
            if (!IsGunshipFootprintClear(dropship, targetMap, position, targetRotation,
                    boundaryOnly: true, out blockers))
            {
                return false;
            }
        }

        // Boundary sampling is sufficient for the swept path, but a thin
        // fixture can already be inside the hull after a prior missed sample
        // or an initial overlap. Validate the complete footprint once at the
        // final pose so those structures enter the impact/destruction path.
        if (!IsGunshipFootprintClear(dropship, targetMap, targetPosition, targetRotation,
                boundaryOnly: false, out blockers))
        {
            return false;
        }

        if (TryComp(dropship.Owner, out DropshipTacticalHoverComponent? hover))
        {
            blockers = hover.CollisionBlockers;
            blockers.Clear();
        }
        else
        {
            blockers = new HashSet<EntityUid>();
        }

        return true;
    }

    private bool IsGunshipFootprintClear(
        Entity<MapGridComponent> dropship,
        EntityUid targetMap,
        Vector2 targetPosition,
        Angle targetRotation,
        out HashSet<EntityUid> blockers)
    {
        return IsGunshipFootprintClear(dropship, targetMap, targetPosition, targetRotation,
            boundaryOnly: false, out blockers);
    }

    private bool IsGunshipFootprintClear(
        Entity<MapGridComponent> dropship,
        EntityUid targetMap,
        Vector2 targetPosition,
        Angle targetRotation,
        bool boundaryOnly,
        out HashSet<EntityUid> blockers)
    {
        if (TryComp(dropship.Owner, out DropshipTacticalHoverComponent? hover))
        {
            blockers = hover.CollisionBlockers;
            blockers.Clear();
        }
        else
        {
            // Placement checks outside tactical hover are rare and have no
            // component lifetime in which to retain a scratch collection.
            blockers = new HashSet<EntityUid>();
        }

        if (!TryComp(targetMap, out MapGridComponent? targetGrid))
            return false;

        const CollisionGroup blockMask =
            CollisionGroup.Impassable |
            CollisionGroup.LowImpassable |
            CollisionGroup.MidImpassable |
            CollisionGroup.HighImpassable;

        var blocked = false;
        var groundRotation = _transform.GetWorldRotation(targetMap);
        foreach (var rotatedCenter in GetRotatedGunshipFootprintCenters(dropship, targetRotation, boundaryOnly))
        {
            var sample = targetPosition + rotatedCenter;
            if (!_map.TryGetTileRef(targetMap, targetGrid, sample, out var targetTile))
                return false;

            var opening = CMUZLevelOpeningCache.IsOpeningTile(targetTile.Tile, _tile);
            if (targetTile.Tile.IsEmpty && !opening)
                return false;

            var tileBlocked = !opening &&
                _turf.IsTileBlocked(targetTile, blockMask, DropshipMinimumBlockingArea);
            var foundPhysicalBlocker = false;
            if (TryComp(dropship.Owner, out DropshipTacticalHoverComponent? flightHover))
            {
                var candidates = flightHover.FlightTerrainCandidates;
                candidates.Clear();
                var sampleBounds = Box2.UnitCentered.Scale(0.98f).Translated(sample);
                var sampleWorldBounds = new Box2Rotated(sampleBounds, targetRotation, sample);
                _entityLookup.GetEntitiesIntersecting(
                    targetMap,
                    sampleWorldBounds,
                    candidates,
                    LookupFlags.Static | LookupFlags.Dynamic);

                // Raised walkways and their edge pieces may belong to a
                // support grid rather than the primary terrain grid. Query
                // every broadphase on this map as well or those fixtures can
                // pass through the hull and collide with its occupants.
                _entityLookup.GetEntitiesIntersecting(
                    Transform(targetMap).MapID,
                    sampleWorldBounds.CalcBoundingBox(),
                    candidates,
                    LookupFlags.Static | LookupFlags.Dynamic);

                // Thin anchored fixtures such as raised platform edges can be
                // omitted by the swept overlap when their anchor tile is an
                // opening. Opening tiles deliberately skip the turf blocker
                // check, so retain the anchored structures on the sampled
                // hull tile as collision candidates as well. They still have
                // to pass the hard blocking-fixture test below.
                foreach (var anchored in _map.GetAnchoredEntities(
                             targetMap,
                             targetGrid,
                             targetTile.GridIndices))
                {
                    candidates.Add(anchored);
                }

                foreach (var candidate in candidates)
                {
                    if (!TryComp(candidate, out TransformComponent? candidateXform) ||
                        candidateXform.GridUid == dropship.Owner)
                    {
                        continue;
                    }

                    // Several multi-z maps serialize raised platform edges as
                    // explicitly unanchored even though their prototype is an
                    // anchored VehicleSmashable. They remain hard structures
                    // which can collide with occupants, so they must enter the
                    // dropship impact path. Do not broaden this to ordinary
                    // loose entities.
                    var anchored = candidateXform.Anchored;
                    if (!anchored && !HasComp<VehicleSmashableComponent>(candidate))
                        continue;

                    var worldPosition = _transform.GetWorldPosition(candidateXform);
                    if (candidateXform.GridUid != targetMap &&
                        !sampleWorldBounds.Contains(worldPosition))
                    {
                        continue;
                    }

                    if (anchored &&
                        candidateXform.GridUid == targetMap &&
                        !flightHover.FlightTerrainAnchors.ContainsKey(candidate))
                    {
                        var localPosition = Vector2.Transform(worldPosition, Transform(targetMap).InvLocalMatrix);
                        var localRotation = _transform.GetWorldRotation(candidateXform) - groundRotation;
                        flightHover.FlightTerrainAnchors.Add(candidate,
                            new DropshipTerrainAnchorPose(localPosition, localRotation));
                    }

                    if (!TryComp(candidate, out FixturesComponent? fixtures) ||
                        !fixtures.Fixtures.Values.Any(fixture =>
                            fixture.Hard && (fixture.CollisionLayer & (int) blockMask) != 0))
                    {
                        continue;
                    }

                    blockers.Add(candidate);
                    foundPhysicalBlocker = true;
                    blocked = true;
                }
            }
            else
            {
                // Placement checks without a hover component do not need the
                // terrain-adoption cache, but still retain the tile blocker.
                foreach (var anchored in _map.GetAnchoredEntities(targetMap, targetGrid, targetTile.GridIndices))
                {
                    if (!TryComp(anchored, out FixturesComponent? fixtures) ||
                        !fixtures.Fixtures.Values.Any(fixture =>
                            fixture.Hard && (fixture.CollisionLayer & (int) blockMask) != 0))
                    {
                        continue;
                    }

                    blockers.Add(anchored);
                    foundPhysicalBlocker = true;
                }
            }

            if (tileBlocked)
            {
                blocked = true;
                // A blocking tile with no matching physical entity is terrain
                // supplied by the target grid itself and cannot be rammed away.
                if (!foundPhysicalBlocker)
                    blockers.Add(targetMap);
            }
        }

        if (!boundaryOnly &&
            TryComp(dropship.Owner, out DropshipTacticalHoverComponent? overlapHover))
        {
            CollectOverlappingFlightSmashables(
                dropship,
                targetMap,
                targetPosition,
                targetRotation,
                groundRotation,
                blockMask,
                overlapHover,
                blockers,
                ref blocked);
        }

        return !blocked;
    }

    private void CollectOverlappingFlightSmashables(
        Entity<MapGridComponent> dropship,
        EntityUid targetMap,
        Vector2 targetPosition,
        Angle targetRotation,
        Angle groundRotation,
        CollisionGroup blockMask,
        DropshipTacticalHoverComponent hover,
        HashSet<EntityUid> blockers,
        ref bool blocked)
    {
        var candidates = hover.FlightTerrainCandidates;
        candidates.Clear();

        var radius = dropship.Comp.LocalAABB.Size.Length() * 0.5f + 1f;
        _entityLookup.GetEntitiesInRange(
            Transform(targetMap).MapID,
            targetPosition,
            radius,
            candidates,
            LookupFlags.Static | LookupFlags.Dynamic);

        if (hover.CachedFootprintCenters.Count == 0)
            CacheGunshipFootprint((dropship.Owner, hover), dropship.Comp);

        foreach (var candidate in candidates)
        {
            if (!HasComp<VehicleSmashableComponent>(candidate) ||
                !TryComp(candidate, out TransformComponent? candidateXform) ||
                candidateXform.GridUid == dropship.Owner ||
                !TryComp(candidate, out FixturesComponent? fixtures) ||
                !fixtures.Fixtures.Values.Any(fixture =>
                    fixture.Hard && (fixture.CollisionLayer & (int) blockMask) != 0))
            {
                continue;
            }

            var worldPosition = _transform.GetWorldPosition(candidateXform);
            var localPosition = (-targetRotation).RotateVec(worldPosition - targetPosition);
            if (!IsWithinGunshipFootprint(dropship, hover, localPosition))
            {
                continue;
            }

            if (candidateXform.Anchored &&
                candidateXform.GridUid == targetMap &&
                !hover.FlightTerrainAnchors.ContainsKey(candidate))
            {
                var groundLocalPosition = Vector2.Transform(worldPosition, Transform(targetMap).InvLocalMatrix);
                var localRotation = _transform.GetWorldRotation(candidateXform) - groundRotation;
                hover.FlightTerrainAnchors.Add(candidate,
                    new DropshipTerrainAnchorPose(groundLocalPosition, localRotation));
            }

            blockers.Add(candidate);
            blocked = true;
        }
    }

    private bool IsWithinGunshipFootprint(
        Entity<MapGridComponent> dropship,
        DropshipTacticalHoverComponent hover,
        Vector2 localPosition)
    {
        var localCoordinates = new EntityCoordinates(dropship.Owner, localPosition);
        var localTile = _map.TileIndicesFor(dropship.Owner, dropship.Comp, localCoordinates);

        // Preserve the old inclusive half-tile test at exact tile boundaries,
        // but cap it to the point's immediate neighborhood instead of walking
        // every occupied dropship tile.
        for (var x = -1; x <= 1; x++)
        {
            for (var y = -1; y <= 1; y++)
            {
                var tile = localTile + new Vector2i(x, y);
                if (!hover.CachedFootprintTiles.Contains(tile))
                    continue;

                var center = _map.TileCenterToVector(dropship.Owner, dropship.Comp, tile);
                if (MathF.Abs(center.X - localPosition.X) <= 0.5f &&
                    MathF.Abs(center.Y - localPosition.Y) <= 0.5f)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private IReadOnlyList<Vector2> GetRotatedGunshipFootprintCenters(
        Entity<MapGridComponent> dropship,
        Angle rotation,
        bool boundaryOnly)
    {
        if (!TryComp(dropship.Owner, out DropshipTacticalHoverComponent? hover))
        {
            var rotated = new List<Vector2>();
            foreach (var center in GetGunshipFootprintCenters(dropship, boundaryOnly))
                rotated.Add(rotation.RotateVec(center));
            return rotated;
        }

        if (boundaryOnly)
        {
            if (hover.HasCachedFootprintBoundaryRotation && hover.CachedFootprintBoundaryRotation.Equals(rotation))
                return hover.CachedRotatedFootprintBoundaryCenters;

            hover.CachedRotatedFootprintBoundaryCenters.Clear();
            foreach (var center in GetGunshipFootprintCenters(dropship, boundaryOnly: true))
                hover.CachedRotatedFootprintBoundaryCenters.Add(rotation.RotateVec(center));

            hover.CachedFootprintBoundaryRotation = rotation;
            hover.HasCachedFootprintBoundaryRotation = true;
            return hover.CachedRotatedFootprintBoundaryCenters;
        }

        if (hover.HasCachedFootprintRotation && hover.CachedFootprintRotation.Equals(rotation))
            return hover.CachedRotatedFootprintCenters;

        hover.CachedRotatedFootprintCenters.Clear();
        foreach (var center in GetGunshipFootprintCenters(dropship, boundaryOnly: false))
            hover.CachedRotatedFootprintCenters.Add(rotation.RotateVec(center));

        hover.CachedFootprintRotation = rotation;
        hover.HasCachedFootprintRotation = true;
        return hover.CachedRotatedFootprintCenters;
    }

    private IReadOnlyList<Vector2> GetGunshipFootprintCenters(
        Entity<MapGridComponent> dropship,
        bool boundaryOnly)
    {
        if (!TryComp(dropship.Owner, out DropshipTacticalHoverComponent? hover))
        {
            var uncached = new List<Vector2>();
            foreach (var tile in _map.GetAllTiles(dropship.Owner, dropship.Comp))
                uncached.Add(_map.TileCenterToVector(dropship.Owner, dropship.Comp, tile.GridIndices));
            return uncached;
        }

        if (hover.CachedFootprintCenters.Count == 0)
            CacheGunshipFootprint((dropship.Owner, hover), dropship.Comp);

        return boundaryOnly ? hover.CachedFootprintBoundaryCenters : hover.CachedFootprintCenters;
    }

    private void CacheGunshipFootprint(
        Entity<DropshipTacticalHoverComponent> hover,
        MapGridComponent grid)
    {
        hover.Comp.CachedFootprintCenters.Clear();
        hover.Comp.CachedFootprintTiles.Clear();
        hover.Comp.CachedFootprintBoundaryCenters.Clear();
        hover.Comp.CachedRotatedFootprintCenters.Clear();
        hover.Comp.CachedRotatedFootprintBoundaryCenters.Clear();
        hover.Comp.HasCachedFootprintRotation = false;
        hover.Comp.HasCachedFootprintBoundaryRotation = false;

        var tiles = new HashSet<Vector2i>();
        foreach (var tile in _map.GetAllTiles(hover.Owner, grid))
            tiles.Add(tile.GridIndices);

        foreach (var tile in tiles)
        {
            var center = _map.TileCenterToVector(hover.Owner, grid, tile);
            hover.Comp.CachedFootprintCenters.Add(center);
            hover.Comp.CachedFootprintTiles.Add(tile);
            if (!tiles.Contains(tile + Vector2i.Left) ||
                !tiles.Contains(tile + Vector2i.Right) ||
                !tiles.Contains(tile + Vector2i.Down) ||
                !tiles.Contains(tile + Vector2i.Up))
            {
                hover.Comp.CachedFootprintBoundaryCenters.Add(center);
            }
        }
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
        SpawnGunshipFootprintWarning(warningCoords,
            (grid, dropshipGrid),
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

    private void SpawnGunshipFootprintWarning(
        EntityCoordinates center,
        Entity<MapGridComponent> dropship,
        Angle rotation,
        float lifetime)
    {
        foreach (var offset in GetGunshipFootprintCenters(dropship, boundaryOnly: true))
            SpawnTimed(center.Offset(rotation.RotateVec(offset)), lifetime);
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
            _integrity.ApplyFlightImpact(hover.Owner, blockers, 5.5f, map);

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
        {
            UpdateGunshipCameraSubscription(seat);
            return;
        }

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
        UpdateGunshipCameraSubscription(seat);
        if (TryComp(pilot, out EyeComponent? pilotEye))
        {
            seat.Comp.OriginalZoom = pilotEye.Zoom;
            seat.Comp.OriginalPvsScale = pilotEye.PvsScale;
        }

        Dirty(seat);
        UpdateGunshipPilotEye(seat, hover);
        _popup.PopupEntity("Gunship flight controls engaged.", seat, pilot, PopupType.Medium);
    }

    private void UpdateGunshipCameraSubscription(Entity<GunshipPilotSeatComponent> seat)
    {
        if (seat.Comp.Pilot is not { } pilot ||
            seat.Comp.Eye is not { } eye ||
            TerminatingOrDeleted(eye) ||
            !TryComp(pilot, out ActorComponent? actor))
        {
            return;
        }

        if (HasAnyManeuveringCamera(seat.Comp))
        {
            if (seat.Comp.ManeuveringCamera == GunshipManeuveringCamera.Rear)
                _gunshipViewSubscriber.AddViewSubscriber(eye, actor.PlayerSession);
            else
                _gunshipViewSubscriber.RemoveViewSubscriber(eye, actor.PlayerSession);

            UpdateManeuveringCameraEyes(seat, actor.PlayerSession);
        }
        else
        {
            _gunshipViewSubscriber.RemoveViewSubscriber(eye, actor.PlayerSession);
            ClearManeuveringCameraEyes(seat, actor.PlayerSession);
        }
    }

    private void UpdateManeuveringCameraEyes(
        Entity<GunshipPilotSeatComponent> seat,
        ICommonSession session)
    {
        if (Transform(seat).GridUid is not { } dropship ||
            Transform(dropship).MapUid is not { } currentMap)
        {
            ClearManeuveringCameraEyes(seat, session);
            return;
        }

        var position = _transform.GetWorldPosition(dropship);
        if (seat.Comp.ManeuveringCamera == GunshipManeuveringCamera.Upper)
            UpdateManeuveringCameraEye(seat, ref seat.Comp.UpperCameraEye, currentMap, 1, position, session);
        else
            ClearManeuveringCameraEye(ref seat.Comp.UpperCameraEye, session);

        if (seat.Comp.ManeuveringCamera == GunshipManeuveringCamera.Lower)
            UpdateManeuveringCameraEye(seat, ref seat.Comp.LowerCameraEye, currentMap, -1, position, session);
        else
            ClearManeuveringCameraEye(ref seat.Comp.LowerCameraEye, session);
    }

    private void UpdateManeuveringCameraEye(
        Entity<GunshipPilotSeatComponent> seat,
        ref EntityUid? cameraEye,
        EntityUid currentMap,
        int offset,
        Vector2 position,
        ICommonSession session)
    {
        if (!_zLevels.TryMapOffset(currentMap, offset, out var targetMap) ||
            !TryComp(targetMap.Value.Owner, out MapComponent? map))
        {
            ClearManeuveringCameraEye(ref cameraEye, session);
            return;
        }

        if (cameraEye is not { } eye || TerminatingOrDeleted(eye))
        {
            eye = Spawn(seat.Comp.EyePrototype, new MapCoordinates(position, map.MapId));
            cameraEye = eye;
        }
        else
        {
            _transform.SetMapCoordinates(eye, new MapCoordinates(position, map.MapId));
        }

        _gunshipViewSubscriber.AddViewSubscriber(eye, session);
    }

    private void ClearManeuveringCameraEyes(
        Entity<GunshipPilotSeatComponent> seat,
        ICommonSession? session = null)
    {
        ClearManeuveringCameraEye(ref seat.Comp.UpperCameraEye, session);
        ClearManeuveringCameraEye(ref seat.Comp.LowerCameraEye, session);
    }

    private void ClearManeuveringCameraEye(ref EntityUid? cameraEye, ICommonSession? session)
    {
        if (cameraEye is not { } eye)
            return;

        cameraEye = null;
        if (TerminatingOrDeleted(eye))
            return;

        if (session != null)
            _gunshipViewSubscriber.RemoveViewSubscriber(eye, session);
        QueueDel(eye);
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

        if (_timing.CurTime < seat.Comp.NextCameraUpdate)
            return;

        seat.Comp.NextCameraUpdate = _timing.CurTime + GunshipCameraUpdateInterval;

        EntityUid viewMap = currentMap;
        if (seat.Comp.ViewOffset != 0 &&
            _zLevels.TryMapOffset(currentMap, seat.Comp.ViewOffset, out var offsetMap))
        {
            viewMap = offsetMap.Value.Owner;
        }

        if (!TryComp(viewMap, out MapComponent? mapComp))
            return;

        var needsMainCameraEye = seat.Comp.ManeuveringCamera == GunshipManeuveringCamera.Rear ||
                                 seat.Comp.ViewOffset != 0 ||
                                 seat.Comp.RearView;
        if (needsMainCameraEye)
        {
            var position = _transform.GetWorldPosition(hover.Owner);
            // Keep the entity-backed rear camera at the stern so its viewport
            // can use the normal below-level compositor.
            if (TryComp(hover.Owner, out MapGridComponent? dropshipGrid))
            {
                var rearCamera = new Vector2(0f, dropshipGrid.LocalAABB.Bottom - 1.5f);
                position += _transform.GetWorldRotation(hover.Owner).RotateVec(rearCamera);
            }

            _transform.SetMapCoordinates(eye, new MapCoordinates(position, mapComp.MapId));
            _zLevels.EnsureZLevelViewer(eye);
        }

        if (seat.Comp.ManeuveringCamera is GunshipManeuveringCamera.Upper or GunshipManeuveringCamera.Lower &&
            seat.Comp.Pilot is { } cameraPilot &&
            TryComp(cameraPilot, out ActorComponent? cameraActor))
        {
            UpdateManeuveringCameraEyes(seat, cameraActor.PlayerSession);
        }

        UpdateGunshipCameraMode(seat, hover.Owner);

        if (!needsMainCameraEye)
            return;

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

    private static bool HasAnyManeuveringCamera(GunshipPilotSeatComponent seat)
    {
        return seat.ManeuveringCamera != GunshipManeuveringCamera.None;
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

        if (linked && seat.Comp.ViewOffset == 0 && !seat.Comp.RearView && seat.Comp.PilotPanning)
        {
            if (TryComp(pilot, out EyeCursorOffsetComponent? cursor))
            {
                cursor.MaxOffset = GunshipCursorMaxOffset;
                cursor.OffsetSpeed = GunshipCursorPanSpeed;
                cursor.PvsIncrease = GunshipCursorPvsIncrease;
            }
        }
        else if (TryComp(pilot, out EyeCursorOffsetComponent? cursor))
        {
            cursor.MaxOffset = 0f;
            cursor.OffsetSpeed = GunshipCursorPanSpeed;
            cursor.PvsIncrease = 0f;
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

        if (seat.Comp.Pilot is { } cursorPilot &&
            !TerminatingOrDeleted(cursorPilot) &&
            TryComp(cursorPilot, out EyeCursorOffsetComponent? cursor))
        {
            cursor.MaxOffset = 0f;
            cursor.OffsetSpeed = GunshipCursorPanSpeed;
            cursor.PvsIncrease = 0f;
        }

        if (gunshipEye is { } eye && !TerminatingOrDeleted(eye))
        {
            if (seat.Comp.Pilot is { } subscribedPilot &&
                TryComp(subscribedPilot, out ActorComponent? actor))
            {
                _gunshipViewSubscriber.RemoveViewSubscriber(eye, actor.PlayerSession);
            }

            QueueDel(eye);
        }

        ICommonSession? cameraSession = null;
        if (seat.Comp.Pilot is { } cameraPilot && TryComp(cameraPilot, out ActorComponent? cameraActor))
            cameraSession = cameraActor.PlayerSession;
        ClearManeuveringCameraEyes(seat, cameraSession);

        seat.Comp.Eye = null;
        seat.Comp.ViewOffset = 0;
        seat.Comp.RearView = false;
        seat.Comp.HeldInputs = GunshipControlInput.None;
        seat.Comp.PressedActions = 0;
        Dirty(seat);
    }

    private void StopGunshipControl(Entity<GunshipPilotSeatComponent> seat, bool restorePilot)
    {
        if (seat.Comp.Pilot is { } pilot)
        {
            DisablePilotHudActions(pilot, seat);
            _gunshipVirtualItems.DeleteInHandsMatching(pilot, seat.Owner);
            RemCompDeferred<RemoteWeaponOperatorComponent>(pilot);
            if (seat.Comp.AddedCursorOffset)
                RemCompDeferred<EyeCursorOffsetComponent>(pilot);
        }

        TeardownGunshipPilotEye(seat);
        seat.Comp.AddedCursorOffset = false;
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

        var validWearers = _validGunshipHudWearers;
        validWearers.Clear();
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
            var flightControlsAvailable = false;
            Vector2 velocity = Vector2.Zero;
            var rotationDegrees = 0f;
            var integrity = 0f;
            var maxIntegrity = 0f;
            var thrustPercent = 0f;
            var hasDirectFireWeapon = false;
            EntityUid? directFireWeapon = null;
            var directFireAmmo = -1;
            IReadOnlyList<DropshipMalfunction> malfunctions = Array.Empty<DropshipMalfunction>();
            var proximityAlarm = false;
            var lowIntegrityAlarm = false;
            var masterAlarmSilenced = false;
            var viewOffset = 0;
            var rearView = false;
            var maneuveringCamera = GunshipManeuveringCamera.None;
            var showDropshipOutline = true;
            var pilotPanning = true;
            var pilotZoom = false;

            if (TryGetControlledGunshipSeat(wearer, out var seat) &&
                Transform(seat).GridUid is { } grid &&
                HasComp<DropshipComponent>(grid))
            {
                dropship = grid;
                rotationDegrees = (float)_transform.GetWorldRotation(grid).Degrees;
                viewOffset = seat.Comp.ViewOffset;
                rearView = seat.Comp.RearView;
                maneuveringCamera = seat.Comp.ManeuveringCamera;
                showDropshipOutline = seat.Comp.ShowDropshipOutline;
                pilotPanning = seat.Comp.PilotPanning;
                pilotZoom = seat.Comp.PilotZoom;
                thrustPercent = seat.Comp.ThrustPercent;

                if (TryComp(grid, out DropshipTacticalHoverComponent? hover))
                {
                    flightControlsAvailable = true;
                    velocity = hover.GunshipLinearVelocity;
                }

                if (TryComp(grid, out DropshipIntegrityComponent? dropshipIntegrity))
                {
                    integrity = dropshipIntegrity.Integrity;
                    maxIntegrity = dropshipIntegrity.MaxIntegrity;
                    malfunctions = dropshipIntegrity.ActiveMalfunctions;
                    masterAlarmSilenced = dropshipIntegrity.MasterAlarmSilenced;
                    proximityAlarm = dropshipIntegrity.ProximityAlarmActive;
                    lowIntegrityAlarm = dropshipIntegrity.LowIntegrityAlarmActive;
                }

                hasDirectFireWeapon = TryGetDirectFireMount(grid, out _, out var foundWeapon, out _, out _, out var directAmmo);
                if (hasDirectFireWeapon)
                {
                    directFireWeapon = foundWeapon;
                    directFireAmmo = directAmmo?.Rounds ?? 0;
                }
            }

            UpdateRemoteDirectFireWeapon(wearer,
                flightControlsAvailable ? dropship : null,
                flightControlsAvailable ? directFireWeapon : null);

            var malfunctionsChanged = !hud.Malfunctions.SequenceEqual(malfunctions);
            var alarmsChanged = !AlarmStateMatches(hud.Alarms, proximityAlarm, lowIntegrityAlarm);
            if (visorChanged ||
                hud.Dropship != dropship ||
                hud.FlightControlsAvailable != flightControlsAvailable ||
                hud.LinearVelocity != velocity ||
                !MathHelper.CloseToPercent(hud.ShipRotationDegrees, rotationDegrees) ||
                !MathHelper.CloseToPercent(hud.Integrity, integrity) ||
                !MathHelper.CloseToPercent(hud.MaxIntegrity, maxIntegrity) ||
                !MathHelper.CloseToPercent(hud.ThrustPercent, thrustPercent) ||
                hud.HasDirectFireWeapon != hasDirectFireWeapon ||
                hud.DirectFireAmmo != directFireAmmo ||
                malfunctionsChanged ||
                alarmsChanged ||
                hud.MasterAlarmSilenced != masterAlarmSilenced ||
                hud.ViewOffset != viewOffset ||
                hud.RearView != rearView ||
                hud.ManeuveringCamera != maneuveringCamera ||
                hud.ShowDropshipOutline != showDropshipOutline ||
                hud.PilotPanning != pilotPanning ||
                hud.PilotZoom != pilotZoom)
            {
                hud.Dropship = dropship;
                hud.FlightControlsAvailable = flightControlsAvailable;
                hud.LinearVelocity = velocity;
                hud.ShipRotationDegrees = rotationDegrees;
                hud.Integrity = integrity;
                hud.MaxIntegrity = maxIntegrity;
                hud.ThrustPercent = thrustPercent;
                hud.HasDirectFireWeapon = hasDirectFireWeapon;
                hud.DirectFireAmmo = directFireAmmo;
                if (malfunctionsChanged)
                    hud.Malfunctions = new List<DropshipMalfunction>(malfunctions);
                if (alarmsChanged)
                {
                    hud.Alarms = new List<DropshipAlarm>(2);
                    if (proximityAlarm)
                        hud.Alarms.Add(DropshipAlarm.Proximity);
                    if (lowIntegrityAlarm)
                        hud.Alarms.Add(DropshipAlarm.LowIntegrity);
                }
                hud.MasterAlarmSilenced = masterAlarmSilenced;
                hud.ViewOffset = viewOffset;
                hud.RearView = rearView;
                hud.ManeuveringCamera = maneuveringCamera;
                hud.ShowDropshipOutline = showDropshipOutline;
                hud.PilotPanning = pilotPanning;
                hud.PilotZoom = pilotZoom;
                Dirty(wearer, hud);
            }

            UpdateGunshipNightVision((wearer, hud), visor, dropship != null);
            UpdateGunshipStaticZoom((wearer, hud), dropship != null);
        }

        var hudQuery = EntityQueryEnumerator<GunshipPilotHudComponent>();
        while (hudQuery.MoveNext(out var wearer, out var hud))
        {
            if (validWearers.Contains(wearer))
                continue;

            CleanupGunshipNightVision((wearer, hud));
            CleanupGunshipStaticZoom((wearer, hud));
            if (TryGetControlledGunshipSeat(wearer, out _))
                UpdateRemoteDirectFireWeapon(wearer, null, null);
            else
                RemCompDeferred<RemoteWeaponOperatorComponent>(wearer);
            RemCompDeferred<GunshipPilotHudComponent>(wearer);
        }
    }

    private static bool AlarmStateMatches(
        IReadOnlyList<DropshipAlarm> alarms,
        bool proximity,
        bool lowIntegrity)
    {
        var expectedCount = (proximity ? 1 : 0) + (lowIntegrity ? 1 : 0);
        if (alarms.Count != expectedCount)
            return false;

        var index = 0;
        if (proximity && alarms[index++] != DropshipAlarm.Proximity)
            return false;

        return !lowIntegrity || alarms[index] == DropshipAlarm.LowIntegrity;
    }

    private void UpdateRemoteDirectFireWeapon(EntityUid pilot, EntityUid? dropship, EntityUid? weapon)
    {
        if (dropship == null || weapon == null)
        {
            if (TryComp(pilot, out RemoteWeaponOperatorComponent? inactive) &&
                (inactive.Platform != null || inactive.SelectedWeapon != null))
            {
                inactive.Platform = null;
                inactive.SelectedWeapon = null;
                Dirty(pilot, inactive);
            }
            return;
        }

        var remote = EnsureComp<RemoteWeaponOperatorComponent>(pilot);
        if (remote.Platform == dropship && remote.SelectedWeapon == weapon)
            return;

        remote.Platform = dropship;
        remote.SelectedWeapon = weapon;
        Dirty(pilot, remote);
    }

    private void UpdateGunshipStaticZoom(Entity<GunshipPilotHudComponent> wearer, bool linked)
    {
        if (linked)
        {
            if (!HasComp<RMCStaticZoomLevelComponent>(wearer))
            {
                EnsureComp<RMCStaticZoomLevelComponent>(wearer);
                wearer.Comp.AddedStaticZoomLevel = true;
            }

            return;
        }

        CleanupGunshipStaticZoom(wearer);
    }

    private void CleanupGunshipStaticZoom(Entity<GunshipPilotHudComponent> wearer)
    {
        if (!wearer.Comp.AddedStaticZoomLevel)
            return;

        wearer.Comp.AddedStaticZoomLevel = false;
        RemCompDeferred<RMCStaticZoomLevelComponent>(wearer);
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
            var lowIntegrity = !integrity.Wrecked &&
                integrity.MaxIntegrity > 0f &&
                integrity.Integrity > 0f &&
                integrity.Integrity / integrity.MaxIntegrity <= 0.25f;
            IReadOnlyList<Vector2> proximityHazards = integrity.ProximityHazards;
            var proximity = integrity.ProximityAlarmActive;

            if (!integrity.Wrecked &&
                TryComp(dropship, out DropshipTacticalHoverComponent? hover) &&
                TryComp(dropship, out MapGridComponent? dropshipGrid))
            {
                var xform = Transform(dropship);
                var position = _transform.GetWorldPosition(xform);
                var rotation = _transform.GetWorldRotation(xform);
                var moving = hover.GunshipLinearVelocity != Vector2.Zero ||
                    !MathHelper.CloseToPercent(hover.GunshipAngularVelocityDegrees, 0f);
                var poseChanged = !integrity.HasLastProximityPose ||
                    integrity.LastProximityMap != xform.MapUid ||
                    Vector2.DistanceSquared(integrity.LastProximityPosition, position) > 0.0025f ||
                    MathF.Abs((float)(integrity.LastProximityRotation - rotation).Theta) > 0.01f;

                if (moving || poseChanged || now >= integrity.NextStationaryProximityScan)
                {
                    _proximityHazardsScratch.Clear();
                    proximity = IsGunshipNearObstruction((dropship, dropshipGrid), _proximityHazardsScratch);
                    proximityHazards = _proximityHazardsScratch;
                    integrity.HasLastProximityPose = true;
                    integrity.LastProximityMap = xform.MapUid;
                    integrity.LastProximityPosition = position;
                    integrity.LastProximityRotation = rotation;
                    integrity.NextStationaryProximityScan = now + StationaryProximityRefreshInterval;
                }
            }
            else if (proximity || proximityHazards.Count != 0)
            {
                proximity = false;
                proximityHazards = Array.Empty<Vector2>();
                integrity.HasLastProximityPose = false;
            }

            if (integrity.LowIntegrityAlarmActive != lowIntegrity ||
                integrity.ProximityAlarmActive != proximity ||
                !integrity.ProximityHazards.SequenceEqual(proximityHazards))
            {
                integrity.LowIntegrityAlarmActive = lowIntegrity;
                integrity.ProximityAlarmActive = proximity;
                integrity.ProximityHazards = new List<Vector2>(proximityHazards);
                Dirty(dropship, integrity);
            }

            UpdateGunshipAlarmAudio((dropship, integrity));
        }
    }

    private bool IsGunshipNearObstruction(Entity<MapGridComponent> dropship, ICollection<Vector2> hazards)
    {
        var xform = Transform(dropship);
        if (xform.MapUid is not { } targetMap ||
            !TryComp(targetMap, out MapGridComponent? targetGrid))
        {
            return false;
        }

        const CollisionGroup blockMask =
            CollisionGroup.Impassable |
            CollisionGroup.LowImpassable |
            CollisionGroup.MidImpassable |
            CollisionGroup.HighImpassable;

        var position = _transform.GetWorldPosition(xform);
        var rotation = _transform.GetWorldRotation(xform);
        var occupied = _proximityOccupiedTiles;
        occupied.Clear();
        foreach (var localCenter in GetGunshipFootprintCenters(dropship, boundaryOnly: true))
        {
            var sample = position + rotation.RotateVec(localCenter);
            if (_map.TryGetTileRef(targetMap, targetGrid, sample, out var targetTile))
                occupied.Add(targetTile.GridIndices);
        }

        var hazardousTiles = _proximityHazardTiles;
        hazardousTiles.Clear();
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

                if (_turf.IsTileBlocked(nearbyTile, blockMask, DropshipMinimumBlockingArea))
                    hazardousTiles.Add(nearby);
            }
        }

        foreach (var tile in hazardousTiles)
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

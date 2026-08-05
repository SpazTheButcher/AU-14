using System.Linq;
using System.Numerics;
using Content.Server._RMC14.Explosion;
using Content.Server.Explosion.Components;
using Content.Server.Explosion.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Components;
using Content.Shared._CMU14.Dropship.Integrity;
using Content.Shared._CMU14.Dropship.GunshipControls;
using Content.Shared._CMU14.Dropship.TacticalLand;
using Content.Shared._CMU14.ZLevels.Core.EntitySystems;
using Content.Shared._RMC14.Dropship;
using Content.Shared._RMC14.Repairable;
using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.Popups;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Station.Components;
using Content.Shared.Tools;
using Content.Shared.Tools.Systems;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;
using Robust.Shared.Prototypes;
using Robust.Shared.Spawners;
using Robust.Shared.Timing;

namespace Content.Server._CMU14.Dropship.Integrity;

public sealed partial class DropshipIntegritySystem : EntitySystem
{
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedToolSystem _tool = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private RMCExplosionSystem _explosion = default!;
    [Dependency] private RMCRepairableSystem _repairable = default!;
    [Dependency] private SharedDropshipSystem _dropships = default!;
    [Dependency] private ProjectileGrenadeSystem _projectileGrenades = default!;
    [Dependency] private TriggerSystem _trigger = default!;
    [Dependency] private CMUSharedZLevelsSystem _zLevels = default!;

    private static readonly ProtoId<ToolQualityPrototype> WeldingQuality = "Welding";
    private const string WarningSignPrototype = "CMUHolographicWarningSign";
    private static readonly EntProtoId CrashExplosion = "CMUDropshipCrashM15Explosion";

    public override void Initialize()
    {
        SubscribeLocalEvent<DropshipComponent, ComponentStartup>(OnDropshipStartup);
        SubscribeLocalEvent<StationMemberComponent, ComponentStartup>(OnStationMemberStartup);
        SubscribeLocalEvent<DropshipHullComponent, DamageChangedEvent>(OnStructuralDamageChanged);
        SubscribeLocalEvent<DropshipHullComponent, InteractUsingEvent>(OnHullInteractUsing);
        SubscribeLocalEvent<DropshipHullComponent, DropshipIntegrityRepairDoAfterEvent>(OnRepairDoAfter);
        SubscribeLocalEvent<DropshipHullComponent, DropshipMalfunctionRepairDoAfterEvent>(OnMalfunctionRepairDoAfter);
        SubscribeLocalEvent<DropshipHullComponent, ExaminedEvent>(OnHullExamined);
    }

    private void OnDropshipStartup(Entity<DropshipComponent> ent, ref ComponentStartup args)
    {
        // Navigation computers can cause DropshipComponent to be ensured on their
        // parent grid. Some maps place those computers directly on a station grid;
        // only real shuttle grids should receive hull integrity or crash behavior.
        if (!IsDropshipIntegrityGrid(ent.Owner))
        {
            RemoveDropshipIntegrity(ent.Owner);
            return;
        }

        var integrity = EnsureComp<DropshipIntegrityComponent>(ent);
        integrity.Integrity = Math.Clamp(integrity.Integrity, 0f, integrity.MaxIntegrity);
        integrity.Wrecked |= ent.Comp.Crashed;
        Dirty(ent.Owner, integrity);
        RefreshHullMarkers(ent.Owner, integrity);
    }

    private void OnStationMemberStartup(Entity<StationMemberComponent> ent, ref ComponentStartup args)
    {
        // Station membership is often assigned after other grid components have
        // started. Clean up any false-positive integrity pool created earlier in
        // that startup sequence.
        RemoveDropshipIntegrity(ent.Owner);
    }

    private bool IsDropshipIntegrityGrid(EntityUid grid)
    {
        return HasComp<ShuttleComponent>(grid) &&
               HasComp<MapGridComponent>(grid) &&
               !HasComp<BecomesStationComponent>(grid) &&
               !HasComp<StationMemberComponent>(grid);
    }

    private void RemoveDropshipIntegrity(EntityUid grid)
    {
        if (!HasComp<DropshipIntegrityComponent>(grid))
            return;

        var children = Transform(grid).ChildEnumerator;
        while (children.MoveNext(out var child))
            RemComp<DropshipHullComponent>(child);

        RemComp<DropshipIntegrityComponent>(grid);
    }

    private void OnStructuralDamageChanged(Entity<DropshipHullComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || args.DamageDelta is null || !Transform(ent).Anchored)
            return;

        if (!TryGetDropship(ent.Owner, out var dropship))
            return;

        var amount = args.DamageDelta.GetTotal().Float();
        if (amount > 0f)
            DamageIntegrity(dropship, amount);
    }

    public void ApplyFlightImpact(EntityUid dropship, IReadOnlyCollection<EntityUid> obstructions, float speed)
    {
        if (!TryComp(dropship, out DropshipIntegrityComponent? integrity) ||
            integrity.Crashing || integrity.Wrecked || speed < integrity.MinimumDamagingImpactSpeed)
        {
            return;
        }

        var shipDamage = speed * speed * integrity.ImpactDamageMultiplier;
        DamageIntegrity((dropship, integrity), shipDamage);

        if (_timing.CurTime >= integrity.NextImpactSound)
        {
            _audio.PlayPvs(integrity.ImpactSound, dropship);
            integrity.NextImpactSound = _timing.CurTime + integrity.ImpactSoundCooldown;
        }

        var obstacleDamage = speed * speed * integrity.ObstacleDamageMultiplier;
        if (obstacleDamage <= 0f)
            return;

        // Destructible debris is spawned from map coordinates. When the terrain
        // grid and dropship grid overlap, Robust may choose the dropship as its
        // parent. Remember the pre-impact ship contents and the obstruction's
        // actual grid so anything adopted by this damage event can be restored.
        var originalChildren = new HashSet<EntityUid>();
        var children = Transform(dropship).ChildEnumerator;
        while (children.MoveNext(out var child))
            originalChildren.Add(child);

        Entity<MapGridComponent>? obstructionGrid = null;
        foreach (var obstruction in obstructions)
        {
            if (!TryComp(obstruction, out TransformComponent? obstructionXform) ||
                obstructionXform.GridUid is not { } grid ||
                grid == dropship ||
                !TryComp(grid, out MapGridComponent? gridComp))
            {
                continue;
            }

            obstructionGrid = (grid, gridComp);
            break;
        }

        var damage = new DamageSpecifier();
        damage.DamageDict["Blunt"] = FixedPoint2.New(obstacleDamage);
        foreach (var obstruction in obstructions)
        {
            if (TerminatingOrDeleted(obstruction) || !HasComp<DamageableComponent>(obstruction))
                continue;

            _damageable.TryChangeDamage(obstruction, damage, origin: dropship);
        }

        if (obstructionGrid is { } ground)
            RestoreImpactAdoptions(dropship, ground, originalChildren);
    }

    private void RestoreImpactAdoptions(
        EntityUid dropship,
        Entity<MapGridComponent> ground,
        HashSet<EntityUid> originalChildren)
    {
        var adopted = new List<EntityUid>();
        var children = Transform(dropship).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (!originalChildren.Contains(child))
                adopted.Add(child);
        }

        var groundXform = Transform(ground);
        var groundRotation = _transform.GetWorldRotation(ground);
        foreach (var child in adopted)
        {
            if (TerminatingOrDeleted(child) || !TryComp(child, out TransformComponent? childXform))
                continue;

            var worldPosition = _transform.GetWorldPosition(childXform);
            var worldRotation = _transform.GetWorldRotation(childXform);
            var localPosition = Vector2.Transform(worldPosition, groundXform.InvLocalMatrix);
            var wasAnchored = childXform.Anchored;

            _transform.SetCoordinates(
                child,
                childXform,
                new EntityCoordinates(ground, localPosition),
                worldRotation - groundRotation);

            if (!wasAnchored || TerminatingOrDeleted(child))
                continue;

            var tile = _map.TileIndicesFor(ground, ground.Comp, new EntityCoordinates(ground, localPosition));
            _transform.AnchorEntity((child, childXform), ground, tile);
        }
    }

    public void DamageIntegrity(Entity<DropshipIntegrityComponent> dropship, float amount)
    {
        if (amount <= 0f || dropship.Comp.Crashing || dropship.Comp.Wrecked)
            return;

        var previousIntegrity = dropship.Comp.Integrity;
        dropship.Comp.Integrity = Math.Max(0f, dropship.Comp.Integrity - amount);
        TryTriggerMalfunctions(dropship, previousIntegrity);
        Dirty(dropship);

        if (dropship.Comp.Integrity <= 0f)
            BeginCrash(dropship);
    }

    private void BeginCrash(Entity<DropshipIntegrityComponent> integrity)
    {
        if (!TryComp(integrity.Owner, out DropshipComponent? dropship) || integrity.Comp.Crashing || integrity.Comp.Wrecked)
            return;

        integrity.Comp.Crashing = true;
        integrity.Comp.CrashAt = _timing.CurTime + integrity.Comp.CrashWarningTime;
        _dropships.SetDropshipCrashed((integrity.Owner, dropship), true);
        Dirty(integrity);

        var xform = Transform(integrity.Owner);
        integrity.Comp.CrashMap = xform.MapUid;
        if (TryComp(integrity.Owner, out DropshipTacticalHoverComponent? hover))
        {
            if (xform.MapUid is { } currentMap &&
                _zLevels.TryMapOffset(currentMap, hover.GroundMapOffset, out var groundMap))
            {
                integrity.Comp.CrashMap = groundMap.Value.Owner;
            }
            else if (hover.GroundMap is { } fallbackGround)
            {
                integrity.Comp.CrashMap = fallbackGround;
            }

            hover.ReturnAt = TimeSpan.MaxValue;
            hover.NextReturnAttempt = TimeSpan.MaxValue;
            hover.GunshipLinearVelocity = Vector2.Zero;
        }

        if (integrity.Comp.CrashMap is { } warningMap && TryComp(warningMap, out MapComponent? map))
        {
            SpawnCrashWarning(warningMap,
                _transform.GetWorldPosition(integrity.Owner),
                integrity.Owner,
                (float) integrity.Comp.CrashWarningTime.TotalSeconds);
        }

        _audio.PlayPvs(dropship.CrashWarningSound, integrity.Owner);
        _popup.PopupEntity("CRITICAL HULL FAILURE! IMPACT IN THREE SECONDS!", integrity.Owner, PopupType.LargeCaution);
    }

    private void SpawnCrashWarning(EntityUid map, Vector2 position, EntityUid dropship, float lifetime)
    {
        if (!TryComp(dropship, out MapGridComponent? grid))
            return;

        var tiles = _map.GetAllTiles(dropship, grid).Select(tile => tile.GridIndices).ToHashSet();
        var rotation = _transform.GetWorldRotation(dropship);
        foreach (var tile in tiles)
        {
            if (tiles.Contains(tile + Vector2i.Up) &&
                tiles.Contains(tile + Vector2i.Down) &&
                tiles.Contains(tile + Vector2i.Left) &&
                tiles.Contains(tile + Vector2i.Right))
            {
                continue;
            }

            var localCenter = _map.TileCenterToVector(dropship, grid, tile);
            var warning = Spawn(WarningSignPrototype,
                new EntityCoordinates(map, position + rotation.RotateVec(localCenter)));
            EnsureComp<TimedDespawnComponent>(warning).Lifetime = lifetime;
        }
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<DropshipIntegrityComponent>();
        while (query.MoveNext(out var uid, out var integrity))
        {
            if (_timing.CurTime >= integrity.NextHullScan)
                RefreshHullMarkers(uid, integrity);

            if (!integrity.Crashing)
                continue;

            if (TryComp(uid, out DropshipTacticalHoverComponent? hover))
            {
                hover.GunshipAngularVelocityDegrees += integrity.CrashSpinAccelerationDegrees * frameTime;
                _transform.SetWorldRotation(uid,
                    _transform.GetWorldRotation(uid) + Angle.FromDegrees(hover.GunshipAngularVelocityDegrees * frameTime));
            }

            if (_timing.CurTime >= integrity.CrashAt)
                FinishCrash((uid, integrity));
        }
    }

    private void RefreshHullMarkers(EntityUid dropship, DropshipIntegrityComponent integrity)
    {
        integrity.NextHullScan = _timing.CurTime + TimeSpan.FromSeconds(1);

        var children = Transform(dropship).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (!HasComp<DamageableComponent>(child))
                continue;

            var childTransform = Transform(child);
            if (!childTransform.Anchored || childTransform.GridUid != dropship)
                continue;

            EnsureComp<DropshipHullComponent>(child);
        }
    }

    private void FinishCrash(Entity<DropshipIntegrityComponent> integrity)
    {
        if (!TryComp(integrity.Owner, out MapGridComponent? shipGrid))
            return;

        var position = _transform.GetWorldPosition(integrity.Owner);
        var rotation = _transform.GetWorldRotation(integrity.Owner);
        if (integrity.Comp.CrashMap is { } targetMap &&
            targetMap != integrity.Owner &&
            TryComp(targetMap, out MapComponent? mapComp) &&
            TryComp(targetMap, out MapGridComponent? groundGrid))
        {
            ClearCrashFootprint((integrity.Owner, shipGrid), (targetMap, groundGrid), position, rotation);
            _transform.SetMapCoordinates(integrity.Owner, new MapCoordinates(position, mapComp.MapId));
            _transform.SetWorldRotation(integrity.Owner, rotation);
        }

        if (TryComp(integrity.Owner, out DropshipTacticalHoverComponent? hover))
        {
            hover.GunshipLinearVelocity = Vector2.Zero;
            hover.GunshipAngularVelocityDegrees = 0f;
            RemComp<DropshipTacticalHoverComponent>(integrity.Owner);
        }

        integrity.Comp.Crashing = false;
        integrity.Comp.Wrecked = true;
        integrity.Comp.Integrity = 0f;
        Dirty(integrity);

        if (TryComp(integrity.Owner, out DropshipComponent? dropship))
            _audio.PlayPvs(dropship.CrashSound, integrity.Owner);

        ConvertCrashWalls((integrity.Owner, shipGrid));
        SpawnCrashExplosions((integrity.Owner, shipGrid));
        SpawnConsoleShrapnel((integrity.Owner, shipGrid));
    }

    private void ConvertCrashWalls(Entity<MapGridComponent> ship)
    {
        var children = Transform(ship.Owner).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (!TryComp(child, out DropshipCrashDestructibleWallComponent? wreckWall))
                continue;

            EntityManager.AddComponents(child, wreckWall.Components);
            EnsureComp<DropshipHullComponent>(child);
        }
    }

    private void ClearCrashFootprint(
        Entity<MapGridComponent> ship,
        Entity<MapGridComponent> ground,
        Vector2 position,
        Angle rotation)
    {
        var delete = new HashSet<EntityUid>();
        foreach (var tile in _map.GetAllTiles(ship.Owner, ship.Comp))
        {
            var localCenter = _map.TileCenterToVector(ship.Owner, ship.Comp, tile.GridIndices);
            var sample = position + rotation.RotateVec(localCenter);
            if (!_map.TryGetTileRef(ground.Owner, ground.Comp, sample, out var targetTile))
                continue;

            foreach (var anchored in _map.GetAnchoredEntities(ground.Owner, ground.Comp, targetTile.GridIndices))
            {
                // Tactical destinations are control markers, not physical
                // obstructions. Deleting one leaves navigation state holding a
                // stale entity reference and can break later dropship updates.
                if (anchored != ship.Owner &&
                    !HasComp<MapComponent>(anchored) &&
                    !HasComp<MapGridComponent>(anchored) &&
                    !HasComp<DropshipDestinationComponent>(anchored))
                {
                    delete.Add(anchored);
                }
            }
        }

        foreach (var uid in delete)
        {
            if (!TerminatingOrDeleted(uid))
                QueueDel(uid);
        }
    }

    private void SpawnCrashExplosions(Entity<MapGridComponent> ship)
    {
        var tiles = _map.GetAllTiles(ship.Owner, ship.Comp).ToList();
        if (tiles.Count == 0)
            return;

        var mapId = Transform(ship.Owner).MapID;
        for (var i = 0; i < 2; i++)
        {
            var tile = _random.Pick(tiles);
            var local = _map.TileCenterToVector(ship.Owner, ship.Comp, tile.GridIndices);
            var world = _transform.GetWorldPosition(ship.Owner) + _transform.GetWorldRotation(ship.Owner).RotateVec(local);
            QueueCrashExplosion(new MapCoordinates(world, mapId), 650f, 35f, 120f, ship.Owner);
        }

        var radius = MathF.Max(ship.Comp.LocalAABB.Width, ship.Comp.LocalAABB.Height) * 0.65f;
        for (var i = 0; i < 2; i++)
        {
            var angle = _random.NextAngle();
            var world = _transform.GetWorldPosition(ship.Owner) + angle.ToVec() * radius;
            QueueCrashExplosion(new MapCoordinates(world, mapId), 450f, 30f, 100f, ship.Owner);
        }
    }

    private void QueueCrashExplosion(
        MapCoordinates coordinates,
        float totalIntensity,
        float slope,
        float maxTileIntensity,
        EntityUid cause)
    {
        _explosion.QueueExplosion(
            coordinates,
            "RMC",
            totalIntensity,
            slope,
            maxTileIntensity,
            cause,
            tileBreakScale: 3f,
            canCreateVacuum: false);

        // This invisible effect uses the M15's exact RMC blast and shrapnel
        // components without creating a grenade item in the wreck.
        var effect = Spawn(CrashExplosion, coordinates);
        _trigger.Trigger(effect, cause);
    }

    private void SpawnConsoleShrapnel(Entity<MapGridComponent> ship)
    {
        var consoles = new List<EntityUid>();
        var children = Transform(ship.Owner).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (HasComp<GunshipControlsComponent>(child))
                consoles.Add(child);
        }

        foreach (var console in consoles)
        {
            if (TerminatingOrDeleted(console))
                continue;

            var effect = Spawn(CrashExplosion, _transform.GetMapCoordinates(console));
            if (TryComp(effect, out ProjectileGrenadeComponent? projectileGrenade))
                _projectileGrenades.SetPayloadCount((effect, projectileGrenade), _random.Next(18, 46));

            _trigger.Trigger(effect, ship.Owner);
        }
    }

    private void OnHullInteractUsing(Entity<DropshipHullComponent> target, ref InteractUsingEvent args)
    {
        if (args.Handled || !TryGetDropship(target.Owner, out var integrity))
        {
            return;
        }

        if (TryStartMalfunctionRepair(target, integrity, ref args))
            return;

        if (!_tool.HasQuality(args.Used, WeldingQuality))
            return;

        args.Handled = true;
        if (integrity.Comp.Wrecked || integrity.Comp.Crashing)
        {
            _popup.PopupEntity("This dropship is wrecked beyond repair.", target, args.User, PopupType.SmallCaution);
            return;
        }

        if (HasComp<DropshipTacticalHoverComponent>(integrity.Owner) ||
            TryComp(integrity.Owner, out FTLComponent? ftl) && ftl.State is FTLState.Starting or FTLState.Travelling or FTLState.Arriving)
        {
            _popup.PopupEntity("The dropship must be landed before its hull can be repaired.", target, args.User, PopupType.SmallCaution);
            return;
        }

        if (integrity.Comp.Integrity >= integrity.Comp.MaxIntegrity)
        {
            _popup.PopupEntity("The dropship hull is already fully repaired.", target, args.User, PopupType.SmallCaution);
            return;
        }

        if (!_repairable.UseFuel(args.Used, args.User, integrity.Comp.RepairFuel, true))
            return;

        var ev = new DropshipIntegrityRepairDoAfterEvent();
        var doAfter = new DoAfterArgs(EntityManager, args.User, integrity.Comp.RepairTime, ev, target, target, used: args.Used)
        {
            NeedHand = true,
            BreakOnMove = true,
            BlockDuplicate = true,
            DuplicateCondition = DuplicateConditions.SameEvent,
        };

        if (_doAfter.TryStartDoAfter(doAfter))
            _popup.PopupEntity("You begin welding the dropship hull.", target, args.User);
    }

    private void OnHullExamined(Entity<DropshipHullComponent> target, ref ExaminedEvent args)
    {
        if (!TryGetDropship(target.Owner, out var integrity))
            return;

        var status = integrity.Comp.Wrecked
            ? "[color=red]WRECKED[/color]"
            : $"{MathF.Ceiling(integrity.Comp.Integrity)}/{MathF.Ceiling(integrity.Comp.MaxIntegrity)}";
        args.PushMarkup($"Dropship hull integrity: {status}");
        PushMalfunctionDiagnostics(integrity, args);
    }

    private void OnRepairDoAfter(Entity<DropshipHullComponent> target, ref DropshipIntegrityRepairDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled || args.Used is not { } tool ||
            !TryGetDropship(target.Owner, out var integrity) || integrity.Comp.Wrecked || integrity.Comp.Crashing ||
            HasComp<DropshipTacticalHoverComponent>(integrity.Owner) ||
            !_repairable.UseFuel(tool, args.User, integrity.Comp.RepairFuel))
        {
            return;
        }

        args.Handled = true;
        integrity.Comp.Integrity = Math.Min(integrity.Comp.MaxIntegrity,
            integrity.Comp.Integrity + integrity.Comp.RepairAmount);
        Dirty(integrity);
        _audio.PlayPvs(integrity.Comp.RepairSound, target);
        _popup.PopupEntity($"Dropship hull integrity restored to {MathF.Ceiling(integrity.Comp.Integrity)}/{MathF.Ceiling(integrity.Comp.MaxIntegrity)}.",
            target,
            args.User);
    }

    private bool TryGetDropship(EntityUid structure, out Entity<DropshipIntegrityComponent> dropship)
    {
        dropship = default;
        var xform = Transform(structure);
        if (!xform.Anchored || xform.GridUid is not { } grid ||
            !TryComp(grid, out DropshipIntegrityComponent? integrity))
        {
            return false;
        }

        dropship = (grid, integrity);
        return true;
    }
}

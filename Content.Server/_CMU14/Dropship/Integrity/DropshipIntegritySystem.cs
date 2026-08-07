using System.Linq;
using System.Numerics;
using Content.Server._RMC14.Explosion;
using Content.Server.Explosion.Components;
using Content.Server.Explosion.EntitySystems;
using Content.Server.Destructible;
using Content.Server.Destructible.Thresholds;
using Content.Server.Destructible.Thresholds.Behaviors;
using Content.Server.Destructible.Thresholds.Triggers;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Components;
using Content.Shared._CMU14.Dropship.Integrity;
using Content.Shared._CMU14.Dropship.GunshipControls;
using Content.Shared._CMU14.Dropship.TacticalLand;
using Content.Shared._CMU14.ZLevels.Core.EntitySystems;
using Content.Shared._RMC14.Dropship;
using Content.Shared._RMC14.Repairable;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.Popups;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Station.Components;
using Content.Shared.Tag;
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
    [Dependency] private TagSystem _tag = default!;
    [Dependency] private ProjectileGrenadeSystem _projectileGrenades = default!;
    [Dependency] private TriggerSystem _trigger = default!;
    [Dependency] private CMUSharedZLevelsSystem _zLevels = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private static readonly ProtoId<ToolQualityPrototype> WeldingQuality = "Welding";
    private static readonly ProtoId<TagPrototype> WallTag = "Wall";
    private const string WarningSignPrototype = "CMUHolographicWarningSign";
    private static readonly EntProtoId CrashExplosion = "CMUDropshipCrashM15Explosion";
    private static readonly TimeSpan ImpactAdoptionGuardTime = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ImpactAdoptionCheckInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HullInitializationScanInterval = TimeSpan.FromMilliseconds(250);
    private const byte HullInitializationFollowupScans = 4;
    private readonly Dictionary<EntityUid, PendingImpactAdoption> _pendingImpactAdoptions = new();
    private readonly HashSet<EntityUid> _emptyFlightObstructions = new();

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
        MarkInitialHull(ent.Owner);
        integrity.HullInitializationScansRemaining = HullInitializationFollowupScans;
        integrity.NextHullInitializationScan = _timing.CurTime + HullInitializationScanInterval;
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

    /// <summary>
    /// Applies an impact and returns the impact-speed budget left after breaking
    /// every obstruction. A zero result means the ship must stop.
    /// </summary>
    public float ApplyFlightImpact(
        EntityUid dropship,
        IReadOnlyCollection<EntityUid> obstructions,
        float speed,
        EntityUid? terrainGrid = null)
    {
        // Grid traversal can adopt an overlapping anchored obstruction before
        // damage is applied. Snapshot the ship first and prefer the terrain
        // grid supplied by the collision query over the obstruction's current
        // (possibly already changed) parent.
        HashSet<EntityUid> originalChildren;
        if (TryComp(dropship, out DropshipTacticalHoverComponent? hover) &&
            hover.FlightGridChildrenInitialized)
        {
            originalChildren = hover.FlightGridChildren;
        }
        else
        {
            originalChildren = new HashSet<EntityUid>();
            var children = Transform(dropship).ChildEnumerator;
            while (children.MoveNext(out var child))
                originalChildren.Add(child);
        }

        Entity<MapGridComponent>? obstructionGrid = null;
        if (terrainGrid is { } explicitGround &&
            explicitGround != dropship &&
            TryComp(explicitGround, out MapGridComponent? explicitGroundGrid))
        {
            obstructionGrid = (explicitGround, explicitGroundGrid);
        }

        if (obstructionGrid == null)
        {
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
        }

        // The Z-level map entity is also its terrain grid and remains the
        // fallback for callers which cannot provide an authoritative grid.
        if (obstructionGrid == null &&
            Transform(dropship).MapUid is { } map &&
            map != dropship &&
            TryComp(map, out MapGridComponent? mapGrid))
        {
            obstructionGrid = (map, mapGrid);
        }

        var impactTiles = obstructionGrid is { } impactGround
            ? GetImpactTiles(impactGround, obstructions)
            : new HashSet<Vector2i>();

        if (!TryComp(dropship, out DropshipIntegrityComponent? integrity) ||
            integrity.Crashing || integrity.Wrecked || speed < integrity.MinimumDamagingImpactSpeed)
        {
            GuardImpactAdoptions(dropship, obstructionGrid, originalChildren, obstructions, impactTiles);
            return 0f;
        }

        var shipDamage = speed * speed * integrity.ImpactDamageMultiplier;
        DamageIntegrity((dropship, integrity), shipDamage);

        if (_timing.CurTime >= integrity.NextImpactSound)
        {
            _audio.PlayPvs(integrity.ImpactSound, dropship);
            integrity.NextImpactSound = _timing.CurTime + integrity.ImpactSoundCooldown;
        }

        if (integrity.Crashing || integrity.Wrecked)
        {
            GuardImpactAdoptions(dropship, obstructionGrid, originalChildren, obstructions, impactTiles);
            return 0f;
        }

        var obstacleDamage = speed * speed * integrity.ObstacleDamageMultiplier;
        if (obstacleDamage <= 0f)
        {
            GuardImpactAdoptions(dropship, obstructionGrid, originalChildren, obstructions, impactTiles);
            return 0f;
        }

        var remainingSpeed = speed;
        var removedEveryObstruction = true;
        var removedAnyObstruction = false;
        foreach (var obstruction in obstructions.Order())
        {
            if (TerminatingOrDeleted(obstruction))
                continue;

            if (remainingSpeed <= 0f)
            {
                removedEveryObstruction = false;
                break;
            }

            if (!TryGetObstacleBreakCost(obstruction,
                    remainingSpeed,
                    integrity.ObstacleDamageMultiplier,
                    out var breakCost))
            {
                // Spend whatever remains against the obstruction that stopped
                // the ship. Do not also give every other blocker the original
                // full-speed impact for free.
                var remainingDamage = remainingSpeed * remainingSpeed * integrity.ObstacleDamageMultiplier;
                ApplyObstacleDamage(obstruction, remainingDamage, dropship);
                remainingSpeed = 0f;
                removedEveryObstruction = false;
                break;
            }

            var rawDamage = breakCost * breakCost * integrity.ObstacleDamageMultiplier;
            ApplyObstacleDamage(obstruction, rawDamage, dropship);
            remainingSpeed = MathF.Max(0f, remainingSpeed - breakCost);
            removedAnyObstruction = true;
        }

        GuardImpactAdoptions(dropship, obstructionGrid, originalChildren, obstructions, impactTiles);

        return removedEveryObstruction && removedAnyObstruction
            ? remainingSpeed
            : 0f;
    }

    private void GuardImpactAdoptions(
        EntityUid dropship,
        Entity<MapGridComponent>? ground,
        HashSet<EntityUid> originalChildren,
        IReadOnlyCollection<EntityUid> obstructions,
        HashSet<Vector2i> impactTiles)
    {
        if (ground is not { } impactGround)
            return;

        var forcedGround = obstructions.ToHashSet();
        RestoreImpactAdoptions(dropship, impactGround, originalChildren, forcedGround);
        _pendingImpactAdoptions[dropship] = new PendingImpactAdoption(
            impactGround.Owner,
            originalChildren,
            forcedGround,
            impactTiles,
            _timing.CurTime + _timing.TickPeriod,
            _timing.CurTime + ImpactAdoptionGuardTime);
    }

    /// <summary>
    /// Guards ordinary free-flight movement against adopting anchored terrain
    /// whose fixtures are not considered a blocking impact.
    /// </summary>
    public void GuardFlightAdoptions(
        EntityUid dropship,
        EntityUid terrainGrid,
        HashSet<EntityUid> originalChildren)
    {
        if (terrainGrid == dropship ||
            !TryComp(terrainGrid, out MapGridComponent? groundGrid))
        {
            return;
        }

        // A blocking impact in the same movement tick may already have
        // scheduled a stricter guard with explicit obstruction entities and
        // impact tiles. Do not replace that information with the general
        // free-flight guard.
        if (_pendingImpactAdoptions.TryGetValue(dropship, out var pending) &&
            pending.Ground == terrainGrid &&
            _timing.CurTime < pending.Expires)
        {
            return;
        }

        _pendingImpactAdoptions[dropship] = new PendingImpactAdoption(
            terrainGrid,
            originalChildren,
            _emptyFlightObstructions,
            null,
            _timing.CurTime + TimeSpan.FromMilliseconds(100),
            _timing.CurTime + ImpactAdoptionGuardTime);
    }

    private bool TryGetObstacleBreakCost(
        EntityUid obstruction,
        float availableSpeed,
        float damageMultiplier,
        out float requiredSpeed)
    {
        requiredSpeed = 0f;
        if (!TryComp(obstruction, out DamageableComponent? damageable) ||
            !TryComp(obstruction, out DestructibleComponent? destructible))
        {
            return false;
        }

        var destroyedAt = GetObstacleRemovalThreshold(destructible);
        if (destroyedAt == FixedPoint2.MaxValue)
            return false;

        var remainingDamage = destroyedAt.Float() - damageable.TotalDamage.Float();
        if (remainingDamage <= 0f)
            return true;

        if (GetEffectiveObstacleDamage(damageable,
                availableSpeed * availableSpeed * damageMultiplier) < remainingDamage)
        {
            return false;
        }

        // Find the smallest speed whose post-modifier damage reaches the
        // remaining destruction threshold. Twenty iterations are well beyond
        // the precision useful to movement or FixedPoint2 damage.
        var low = 0f;
        var high = availableSpeed;
        for (var i = 0; i < 12; i++)
        {
            var middle = (low + high) * 0.5f;
            var rawDamage = middle * middle * damageMultiplier;
            if (GetEffectiveObstacleDamage(damageable, rawDamage) >= remainingDamage)
                high = middle;
            else
                low = middle;
        }

        requiredSpeed = high;
        return true;
    }

    /// <summary>
    /// Finds the damage threshold that actually removes an obstruction. The
    /// generic DestructibleSystem helper intentionally treats Breakage as
    /// destruction, but RMC walls use Breakage to turn into a still-solid
    /// girder before their later Destruction threshold.
    /// </summary>
    private static FixedPoint2 GetObstacleRemovalThreshold(DestructibleComponent destructible)
    {
        var destructionAt = FixedPoint2.MaxValue;
        var breakageAt = FixedPoint2.MaxValue;

        foreach (var threshold in destructible.Thresholds)
        {
            if (threshold.Trigger is not DamageTrigger trigger)
                continue;

            foreach (var behavior in threshold.Behaviors)
            {
                if (behavior is not DoActsBehavior acts)
                    continue;

                if (acts.HasAct(ThresholdActs.Destruction))
                    destructionAt = FixedPoint2.Min(destructionAt, FixedPoint2.New(trigger.Damage));
                else if (acts.HasAct(ThresholdActs.Breakage))
                    breakageAt = FixedPoint2.Min(breakageAt, FixedPoint2.New(trigger.Damage));
            }
        }

        return destructionAt != FixedPoint2.MaxValue ? destructionAt : breakageAt;
    }

    private float GetEffectiveObstacleDamage(DamageableComponent damageable, float rawDamage)
    {
        var damage = new DamageSpecifier();
        damage.DamageDict["Blunt"] = FixedPoint2.New(rawDamage);
        if (damageable.DamageModifierSetId != null &&
            _prototypes.TryIndex<DamageModifierSetPrototype>(damageable.DamageModifierSetId, out var modifierSet))
        {
            damage = DamageSpecifier.ApplyModifierSet(damage, modifierSet);
        }

        damage = _damageable.ApplyUniversalAllModifiers(damage);
        return MathF.Max(0f, damage.GetTotal().Float());
    }

    private void ApplyObstacleDamage(EntityUid obstruction, float rawDamage, EntityUid dropship)
    {
        if (TerminatingOrDeleted(obstruction) || rawDamage <= 0f || !HasComp<DamageableComponent>(obstruction))
            return;

        var damage = new DamageSpecifier();
        damage.DamageDict["Blunt"] = FixedPoint2.New(rawDamage);
        _damageable.TryChangeDamage(obstruction, damage, origin: dropship);
    }

    private HashSet<Vector2i> GetImpactTiles(
        Entity<MapGridComponent> ground,
        IReadOnlyCollection<EntityUid> obstructions)
    {
        var tiles = new HashSet<Vector2i>();
        var groundXform = Transform(ground);
        foreach (var obstruction in obstructions)
        {
            if (!TryComp(obstruction, out TransformComponent? xform))
                continue;

            var worldPosition = _transform.GetWorldPosition(xform);
            var localPosition = Vector2.Transform(worldPosition, groundXform.InvLocalMatrix);
            tiles.Add(_map.TileIndicesFor(
                ground,
                ground.Comp,
                new EntityCoordinates(ground, localPosition)));
        }

        return tiles;
    }

    private void RestoreImpactAdoptions(
        EntityUid dropship,
        Entity<MapGridComponent> ground,
        HashSet<EntityUid> originalChildren,
        HashSet<EntityUid> forcedGround,
        bool anchoredOnly = false,
        HashSet<Vector2i>? allowedGroundTiles = null)
    {
        var adopted = new List<EntityUid>();
        var children = Transform(dropship).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (originalChildren.Contains(child) && !forcedGround.Contains(child))
                continue;

            if (anchoredOnly &&
                (!TryComp(child, out TransformComponent? adoptedXform) || !adoptedXform.Anchored))
            {
                continue;
            }

            if (anchoredOnly &&
                !forcedGround.Contains(child) &&
                allowedGroundTiles != null &&
                TryComp(child, out TransformComponent? tileXform))
            {
                var worldPosition = _transform.GetWorldPosition(tileXform);
                var groundPosition = Vector2.Transform(worldPosition, Transform(ground).InvLocalMatrix);
                var groundTile = _map.TileIndicesFor(
                    ground,
                    ground.Comp,
                    new EntityCoordinates(ground, groundPosition));
                if (!allowedGroundTiles.Contains(groundTile))
                    continue;
            }

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
            var crashStarted = new GunshipCrashStartedEvent();
            RaiseLocalEvent(integrity.Owner, ref crashStarted);

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
        ProcessPendingImpactAdoptions();

        var query = EntityQueryEnumerator<DropshipIntegrityComponent>();
        while (query.MoveNext(out var uid, out var integrity))
        {
            if (integrity.HullInitializationScansRemaining > 0 &&
                _timing.CurTime >= integrity.NextHullInitializationScan)
            {
                MarkInitialHull(uid);
                integrity.HullInitializationScansRemaining--;
                integrity.NextHullInitializationScan = _timing.CurTime + HullInitializationScanInterval;
            }

            if (integrity.CrashAftermathAt is { } aftermathAt && _timing.CurTime >= aftermathAt)
            {
                integrity.CrashAftermathAt = null;
                if (TryComp(uid, out MapGridComponent? wreckGrid))
                {
                    ConvertCrashWalls((uid, wreckGrid));
                    SpawnCrashExplosions((uid, wreckGrid));
                    SpawnConsoleShrapnel((uid, wreckGrid));
                }
            }

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

    private void ProcessPendingImpactAdoptions()
    {
        if (_pendingImpactAdoptions.Count == 0)
            return;

        var finished = new List<EntityUid>();
        foreach (var (dropship, pending) in _pendingImpactAdoptions)
        {
            if (TerminatingOrDeleted(dropship) ||
                _timing.CurTime >= pending.Expires ||
                !TryComp(pending.Ground, out MapGridComponent? groundGrid))
            {
                finished.Add(dropship);
                continue;
            }

            if (_timing.CurTime < pending.NextCheck)
                continue;

            RestoreImpactAdoptions(
                dropship,
                (pending.Ground, groundGrid),
                pending.OriginalChildren,
                pending.ForcedGround,
                anchoredOnly: true,
                allowedGroundTiles: pending.ImpactTiles);
            pending.NextCheck = _timing.CurTime + ImpactAdoptionCheckInterval;
        }

        foreach (var dropship in finished)
            _pendingImpactAdoptions.Remove(dropship);
    }

    private sealed class PendingImpactAdoption(
        EntityUid ground,
        HashSet<EntityUid> originalChildren,
        HashSet<EntityUid> forcedGround,
        HashSet<Vector2i>? impactTiles,
        TimeSpan nextCheck,
        TimeSpan expires)
    {
        public readonly EntityUid Ground = ground;
        public readonly HashSet<EntityUid> OriginalChildren = originalChildren;
        public readonly HashSet<EntityUid> ForcedGround = forcedGround;
        public readonly HashSet<Vector2i>? ImpactTiles = impactTiles;
        public TimeSpan NextCheck = nextCheck;
        public readonly TimeSpan Expires = expires;
    }

    private void MarkInitialHull(EntityUid dropship)
    {
        var children = Transform(dropship).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            // Intact dropship walls are deliberately invincible and only gain
            // Damageable when the ship becomes a wreck. Still expose the
            // shared hull status and repair interaction on every wall.
            if (!HasComp<DamageableComponent>(child) && !_tag.HasTag(child, WallTag))
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

        // Keep the recursive map transfer and tactical-hover component removal
        // in a separate game state from the large batch of wreck components,
        // triggered explosions, and shrapnel entities.
        integrity.Comp.CrashAftermathAt = _timing.CurTime + _timing.TickPeriod;
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
            QueueCrashExplosion(new MapCoordinates(world, mapId), ship.Owner);
        }

        var radius = MathF.Max(ship.Comp.LocalAABB.Width, ship.Comp.LocalAABB.Height) * 0.65f;
        for (var i = 0; i < 2; i++)
        {
            var angle = _random.NextAngle();
            var world = _transform.GetWorldPosition(ship.Owner) + angle.ToVec() * radius;
            QueueCrashExplosion(new MapCoordinates(world, mapId), ship.Owner);
        }
    }

    private void QueueCrashExplosion(
        MapCoordinates coordinates,
        EntityUid cause)
    {
        // Match the M15's RMC blast once. The spawned helper below supplies
        // only its fragmentation payload, avoiding the previous duplicate
        // explosion at every crash site.
        _explosion.QueueExplosion(
            coordinates,
            "RMC",
            240f,
            6f,
            20f,
            cause,
            tileBreakScale: 3f,
            canCreateVacuum: false);

        var effect = Spawn(CrashExplosion, coordinates);
        _trigger.Trigger(effect, cause);
        QueueDel(effect);
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
            QueueDel(effect);
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

        if (IsRepairerInsideDropship(args.User, integrity.Owner))
        {
            _popup.PopupEntity("You must be outside the dropship to repair it.", target, args.User, PopupType.SmallCaution);
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
            IsRepairerInsideDropship(args.User, integrity.Owner) ||
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

    private bool IsRepairerInsideDropship(EntityUid user, EntityUid dropship)
    {
        return TryComp(user, out TransformComponent? xform) && xform.GridUid == dropship;
    }
}

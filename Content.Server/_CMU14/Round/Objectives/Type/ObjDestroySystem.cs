using Content.Shared._CMU14.Round.Objectives.Component;
using Content.Server._CMU14.Round.Objectives.Component;
using Content.Shared._CMU14.Round.Objectives.Type;
using Robust.Shared.Map;

namespace Content.Server._CMU14.Round.Objectives.Type;

public sealed partial class ObjDestroySystem : ObjectiveSystem
{
    [Dependency] private ObjectiveControlSystem _objCtrl = default!;
    [Dependency] private ObjectiveInterestSystem _objInt = default!;
    private ISawmill _logs = default!;

    public override void Initialize()
    {
        base.Initialize();
        _logs = Logger.GetSawmill("obj-destroy");
        SubscribeLocalEvent<CMUObjectiveComponent, ObjectiveActivatedEvent>(OnActivated);
        SubscribeLocalEvent<DestroyObjectiveComponent, ObjectiveResetEvent>(OnReset);
        SubscribeLocalEvent<MetaDataComponent, ComponentStartup>(OnEntityMetaStartup);
        SubscribeLocalEvent<DestroyMarkedForComponent, EntityTerminatingEvent>(OnMarkedEntityDestroyed);
    }

    private void OnActivated(EntityUid uid, CMUObjectiveComponent comp, ref ObjectiveActivatedEvent _)
    {
        if (!HasComp<DestroyObjectiveComponent>(uid) || !comp.Active)
            return;

        ActivateDestroyObjective(uid, comp);
    }

    private void OnReset(EntityUid uid, DestroyObjectiveComponent comp, ref ObjectiveResetEvent args)
    {
        var query = EntityQueryEnumerator<ObjSpawnedByComponent>();
        while (query.MoveNext(out var ent, out var spawnedBy))
        {
            if (spawnedBy.ObjectiveUid != uid)
                continue;

            if (TryComp(ent, out DestroyMarkedForComponent? marked))
            {
                marked.AssociatedObjectives.Remove(uid);
                if (marked.AssociatedObjectives.Count == 0)
                    RemComp<DestroyMarkedForComponent>(ent);
            }

            if (Exists(ent))
                QueueDel(ent);
        }

        comp.DestroyedCount = 0;
        comp.HasSpawned = false;
    }

    private void ActivateDestroyObjective(EntityUid uid, CMUObjectiveComponent comp)
    {
        var destroyComp = Comp<DestroyObjectiveComponent>(uid);
        if (destroyComp.HasSpawned)
            return;

        var objMap = Transform(uid).MapID;
        var markers = ResolveMarkers(objMap, destroyComp.SpawnMarkerId);
        var spawned = SpawnEntitiesAtMarkers(destroyComp.TargetPrototype, destroyComp.SpawnCount, markers);
        destroyComp.HasSpawned = true;

        foreach (var ent in spawned)
            EnsureComp<ObjSpawnedByComponent>(ent).ObjectiveUid = uid;

        _objInt.RegisterInterest(uid, objMap,
            keys: string.IsNullOrEmpty(destroyComp.TargetPrototype) ? null : new[] { destroyComp.TargetPrototype },
            wildcard: destroyComp.UseAnyEntity);

        MarkExistingEntities(uid, destroyComp, comp, objMap);
    }

    private void MarkExistingEntities(EntityUid uid, DestroyObjectiveComponent comp, CMUObjectiveComponent auComp, MapId objMap)
    {
        var query = AllEntityQuery<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out var ent, out var meta, out var xform))
        {
            if (ent == uid || xform.MapID != objMap)
                continue;

            var proto = meta.EntityPrototype?.ID ?? string.Empty;
            if (comp.UseAnyEntity)
            {
                if (!string.IsNullOrEmpty(comp.TargetPrototype)
                        && !string.Equals(comp.TargetPrototype, proto, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            else if (!string.Equals(comp.TargetPrototype, proto, StringComparison.OrdinalIgnoreCase))
                continue;

            var mark = EnsureComp<DestroyMarkedForComponent>(ent);
            mark.AssociatedObjectives[uid] = auComp.Faction.ToLowerInvariant();
        }
    }

    private void OnEntityMetaStartup(EntityUid uid, MetaDataComponent meta, ref ComponentStartup args)
    {
        var proto = meta.EntityPrototype?.ID;
        if (string.IsNullOrEmpty(proto))
            return;

        var map = Transform(uid).MapID;
        var interested = _objInt.GetInterestedObjectives(map, [proto]);
        foreach (var objUid in interested)
        {
            if (!TryComp(objUid, out CMUObjectiveComponent? auComp) || !auComp.Active)
                continue;

            if (TryComp(objUid, out DestroyObjectiveComponent? destroyComp)
                    && !string.IsNullOrEmpty(destroyComp.TargetPrototype)
                    && !string.Equals(destroyComp.TargetPrototype, proto, StringComparison.OrdinalIgnoreCase))
                continue;

            if (TryComp(uid, out DestroyMarkedForComponent? existing) && existing.AssociatedObjectives.ContainsKey(objUid))
                continue;

            var mark = EnsureComp<DestroyMarkedForComponent>(uid);
            mark.AssociatedObjectives[objUid] = auComp.Faction.ToLowerInvariant();
        }
    }

    private void OnMarkedEntityDestroyed(EntityUid uid, DestroyMarkedForComponent comp, ref EntityTerminatingEvent args)
    {
        var objectivesToRemove = new List<EntityUid>();
        foreach (var (objectiveUid, factionToCredit) in comp.AssociatedObjectives)
        {
            if (!TryComp(objectiveUid, out DestroyObjectiveComponent? destroyComp)
                    || !TryComp(objectiveUid, out CMUObjectiveComponent? auComp))
                continue;

            destroyComp.DestroyedCount++;
            if (destroyComp.DestroyedCount < destroyComp.DestroyCount)
                continue;

            _objInt.UnregisterInterest(objectiveUid);
            _objCtrl.CompleteObjectiveForFaction(objectiveUid, auComp, factionToCredit);
            objectivesToRemove.Add(objectiveUid);
        }
        foreach (var o in objectivesToRemove)
            comp.AssociatedObjectives.Remove(o);
    }

    // _objCtrl.AwardPointsToFaction()
    // ResetObjectiveComponents()
}

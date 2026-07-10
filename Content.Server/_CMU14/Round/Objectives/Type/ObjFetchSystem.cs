using Content.Shared._CMU14.Round.Objectives.Component;
using Content.Shared._CMU14.Round.Objectives.Type;
using Content.Shared.AU14;
using Content.Shared.DragDrop;
using Content.Shared.Interaction.Events;
using Content.Shared.Movement.Pulling.Events;

namespace Content.Server._CMU14.Round.Objectives.Type;

// TODO: Move over the YAML (markers etc.) to new System

public sealed partial class ObjFetchSystem : ObjectiveSystem
{
    [Dependency] private ObjectiveControlSystem _objCtrl = default!;
    [Dependency] private ObjectiveInterestSystem _objInt = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _xformSys = default!;
    private ISawmill _logs = default!;

    public override void Initialize()
    {
        base.Initialize();
        _logs = Logger.GetSawmill("obj-fetch");
        SubscribeLocalEvent<CMUObjectiveComponent, ObjectiveActivatedEvent>(OnActivated);
        SubscribeLocalEvent<FetchObjectiveComponent, ObjectiveResetEvent>(OnReset);
        SubscribeLocalEvent<MetaDataComponent, ComponentStartup>(OnEntityMetaStartup);
        SubscribeLocalEvent<FetchItemComponent, DroppedEvent>(OnDropped);
        SubscribeLocalEvent<FetchItemComponent, PullStoppedMessage>(OnUndragged);
        SubscribeLocalEvent<FetchReturnPointComponent, DragDropTargetEvent>(OnReturnPointDragDrop);
        SubscribeLocalEvent<FetchItemComponent, EntityTerminatingEvent>(OnFetchItemDestroyed);
    }

    private void OnActivated(EntityUid uid, CMUObjectiveComponent comp, ref ObjectiveActivatedEvent _)
    {
        if (!TryComp(uid, out FetchObjectiveComponent? fetchComp) || !comp.Active || fetchComp.HasSpawned)
            return;

        if (!fetchComp.UseMarkers)
            return;

        var objMap = Transform(uid).MapID;

        _objInt.RegisterInterest(uid, objMap,
            keys: string.IsNullOrEmpty(fetchComp.TargetPrototype) ? null : new[] { fetchComp.TargetPrototype },
            wildcard: fetchComp.UseAnyEntity);

        if (fetchComp.UseAnyEntity && !string.IsNullOrEmpty(fetchComp.TargetPrototype)
            && RegisterPreplacedFetchEntities(uid, fetchComp) > 0)
        {
            fetchComp.HasSpawned = true;
            return;
        }

        var markers = ResolveMarkers(objMap, fetchComp.SpawnMarkerId);
        var spawned = SpawnEntitiesAtMarkers(fetchComp.TargetPrototype, fetchComp.SpawnCount, markers, shuffle: true);

        foreach (var ent in spawned)
        {
            EnsureComp<FetchItemComponent>(ent).ObjectiveUid = uid;
            if (!string.IsNullOrEmpty(fetchComp.SpawnOther))
                Spawn(fetchComp.SpawnOther, Transform(ent).Coordinates);
        }
        fetchComp.HasSpawned = spawned.Count > 0;
    }

    private void OnReset(EntityUid uid, FetchObjectiveComponent comp, ref ObjectiveResetEvent args)
    {
        comp.AmountFetchedPerFaction.Clear();
        if (!comp.RespawnOnRepeat)
            return;

        var query = EntityQueryEnumerator<FetchItemComponent>();
        while (query.MoveNext(out var ent, out var item))
        {
            if (item.ObjectiveUid == uid && !item.Fetched && Exists(ent))
                QueueDel(ent);
        }

        comp.HasSpawned = false;
    }

    private int RegisterPreplacedFetchEntities(EntityUid objectiveUid, FetchObjectiveComponent comp, float radius = 48f)
    {
        if (!TryComp(objectiveUid, out TransformComponent? xform))
            return 0;

        int registered = 0;
        foreach (var ent in _lookup.GetEntitiesInRange(xform.Coordinates, radius))
        {
            if (ent == objectiveUid || HasComp<FetchItemComponent>(ent))
                continue;

            if (!TryComp(ent, out MetaDataComponent? meta) || meta.EntityPrototype?.ID != comp.TargetPrototype)
                continue;

            EnsureComp<FetchItemComponent>(ent).ObjectiveUid = objectiveUid;
            registered++;
        }
        return registered;
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

            if (TryComp(objUid, out FetchObjectiveComponent? fetchComp)
                    && !string.IsNullOrEmpty(fetchComp.TargetPrototype)
                    && !string.Equals(fetchComp.TargetPrototype, proto, StringComparison.OrdinalIgnoreCase))
                continue;

            if (HasComp<FetchItemComponent>(uid))
                continue;

            EnsureComp<FetchItemComponent>(uid).ObjectiveUid = objUid;
        }
    }

    private void OnDropped(EntityUid uid, FetchItemComponent item, ref DroppedEvent _) => TryCompleteFetch(uid, item);
    private void OnUndragged(EntityUid uid, FetchItemComponent item, ref PullStoppedMessage _) => TryCompleteFetch(uid, item);

    private void OnReturnPointDragDrop(EntityUid uid, FetchReturnPointComponent comp, ref DragDropTargetEvent args)
    {
        if (TryComp(args.Dragged, out FetchItemComponent? item))
            TryCompleteFetch(args.Dragged, item);
    }

    private void TryCompleteFetch(EntityUid itemUid, FetchItemComponent item)
    {
        if (item.Fetched
                || !TryComp(item.ObjectiveUid, out FetchObjectiveComponent? fetchComp)
                || !TryComp(item.ObjectiveUid, out CMUObjectiveComponent? auComp))
            return;

        var xform = Transform(itemUid);
        var coords = xform.Coordinates;
        var gridId = _xformSys.GetGrid(coords);
        var pos = _xformSys.GetWorldPosition(xform);

        FetchReturnPointComponent? matched = null;
        foreach (var ent in _lookup.GetEntitiesInRange(coords, 10f))
        {
            if (!TryComp(ent, out FetchReturnPointComponent? rp))
                continue;

            var rpXform = Transform(ent);
            if (_xformSys.GetGrid(rpXform.Coordinates) != gridId)
                continue;

            var rpPos = _xformSys.GetWorldPosition(rpXform);
            if ((int)pos.X != (int)rpPos.X || (int)pos.Y != (int)rpPos.Y)
                continue;

            var returnId = fetchComp.CustomReturnPointId;
            if (!string.IsNullOrEmpty(returnId))
            {
                if (rp.FetchId == returnId || (string.IsNullOrEmpty(rp.FetchId) && rp.Generic))
                { matched = rp; break; }
            }
            else if (rp.Generic)
            { matched = rp; break; }
        }

        if (matched is not { } rpComp || string.IsNullOrEmpty(rpComp.ReturnPointFaction))
            return;

        var faction = rpComp.ReturnPointFaction.ToLowerInvariant();
        fetchComp.AmountFetchedPerFaction.TryAdd(faction, 0);
        fetchComp.AmountFetchedPerFaction[faction]++;
        item.Fetched = true;

        if (ShouldCompleteForFaction(auComp, faction, fetchComp.AmountFetchedPerFaction[faction], fetchComp.FetchCount))
        {
            _objInt.UnregisterInterest(item.ObjectiveUid);
            _objCtrl.CompleteObjectiveForFaction(item.ObjectiveUid, auComp, faction);
        }
    }

    public int ScanForFetchItems(EntityUid analyzerUid)
    {
        if (!TryComp(analyzerUid, out TransformComponent? analyzerXform))
            return 0;

        var analyzerFaction = TryComp(analyzerUid, out AnalyzerComponent? a) ? a.Faction.ToLowerInvariant() : string.Empty;
        int totalFetched = 0;

        var query = EntityQueryEnumerator<FetchObjectiveComponent, CMUObjectiveComponent>();
        while (query.MoveNext(out var objUid, out var fetchComp, out var auComp))
        {
            if (!auComp.Active || fetchComp.UseMarkers || string.IsNullOrEmpty(fetchComp.TargetPrototype))
                continue;

            if (!string.IsNullOrEmpty(analyzerFaction) && !auComp.FactionNeutral && auComp.Faction.ToLowerInvariant() != analyzerFaction)
                continue;

            var creditFaction = string.IsNullOrEmpty(analyzerFaction) ? auComp.Faction.ToLowerInvariant() : analyzerFaction;

            foreach (var ent in _lookup.GetEntitiesInRange(analyzerXform.Coordinates, 5f))
            {
                if (ent == analyzerUid || ent == objUid) continue;
                if (!TryComp(ent, out MetaDataComponent? meta) || meta.EntityPrototype?.ID != fetchComp.TargetPrototype) continue;

                var item = EnsureComp<FetchItemComponent>(ent);
                if (item.Fetched) continue;

                item.ObjectiveUid = objUid;
                fetchComp.AmountFetchedPerFaction.TryAdd(creditFaction, 0);
                fetchComp.AmountFetchedPerFaction[creditFaction]++;
                item.Fetched = true;
                totalFetched++;
            }

            if (ShouldCompleteForFaction(auComp, creditFaction, fetchComp.AmountFetchedPerFaction.GetValueOrDefault(creditFaction), fetchComp.FetchCount))
            {
                _objInt.UnregisterInterest(objUid);
                _objCtrl.CompleteObjectiveForFaction(objUid, auComp, creditFaction);
            }
        }
        return totalFetched;
    }

    private void OnFetchItemDestroyed(EntityUid uid, FetchItemComponent comp, ref EntityTerminatingEvent args)
    {
        if (comp.Fetched || comp.ObjectiveUid == EntityUid.Invalid || TerminatingOrDeleted(comp.ObjectiveUid))
            return;
        if (!TryComp(comp.ObjectiveUid, out FetchObjectiveComponent? fetchComp)
                || !TryComp(comp.ObjectiveUid, out CMUObjectiveComponent? auComp))
            return;

        int unfetched = 0;
        var q = EntityQueryEnumerator<FetchItemComponent>();
        while (q.MoveNext(out var _, out var other))
        {
            if (other.ObjectiveUid == comp.ObjectiveUid && !other.Fetched)
                unfetched++;
        }

        var factions = auComp.FactionNeutral ? auComp.Factions : [auComp.Faction];
        foreach (var faction in factions)
        {
            var key = faction.ToLowerInvariant();
            var possible = fetchComp.AmountFetchedPerFaction.GetValueOrDefault(key) + unfetched;
            if (possible >= fetchComp.FetchCount) continue;
            if (auComp.StatusesPerFaction.TryGetValue(key, out var s) && s != CMUObjectiveComponent.ObjectiveStatus.Incomplete)
                continue;

            auComp.StatusesPerFaction[key] = CMUObjectiveComponent.ObjectiveStatus.Failed;
            _objCtrl.AwardPointsToFaction(key, auComp);
        }
    }
}

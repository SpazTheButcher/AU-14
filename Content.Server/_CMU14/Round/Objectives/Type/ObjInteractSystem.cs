using Content.Server.Popups;
using Content.Shared._CMU14.Round.Objectives;
using Content.Shared._CMU14.Round.Objectives.Type;
using Content.Shared.Popups;

namespace Content.Server._CMU14.Round.Objectives.Type;

public sealed class ObjInteractSystem : ObjectiveSystem
{
    [Dependency] private ObjectiveControlSystem _objCtrl = default!;
    [Dependency] private ObjectiveInterestSystem _objInt = default!;
    [Dependency] private PopupSystem _popup = default!;
    private ISawmill _logs = default!;

    public override void Initialize()
    {
        base.Initialize();
        _logs = Logger.GetSawmill("obj-interact");
        SubscribeLocalEvent<CMUObjectiveComponent, ObjectiveActivatedEvent>(OnActivated);
        SubscribeLocalEvent<InteractObjectiveComponent, ObjectiveResetEvent>(OnReset);
        SubscribeLocalEvent<InteractTrackerComponent, InteractObjectiveDoAfterEvent>(OnDoAfterComplete);
        SubscribeLocalEvent<MetaDataComponent, ComponentStartup>(OnEntityMetaStartup);
    }

    private void OnActivated(EntityUid uid, CMUObjectiveComponent comp, ref ObjectiveActivatedEvent _)
    {
        if (!TryComp(uid, out InteractObjectiveComponent? interactComp)
                || !comp.Active
                || interactComp.HasSpawned)
            return;

        var objMap = Transform(uid).MapID;

        if (interactComp.Interactables.Count > 0)
        {
            _objInt.RegisterInterest(uid, objMap,
                keys: interactComp.Interactables,
                wildcard: false);
        }

        if (!interactComp.Spawn)
        {
            var registered = RegisterPreplacedEntities(uid, interactComp);
            interactComp.HasSpawned = registered > 0;
            if (registered <= 0) return;
        }
        else
        {
            var entityToSpawn = interactComp.Interactables.FirstOrDefault();
            if (string.IsNullOrEmpty(entityToSpawn))
            {
                _logs.Warning($"[INTERACT] Objective {uid} has no Interactables defined!");
                return;
            }

            var markers = ResolveMarkers(objMap, interactComp.SpawnMarkerId);
            var spawned = SpawnEntitiesAtMarkers(entityToSpawn, interactComp.SpawnCount, markers, shuffle: true);
            foreach (var ent in spawned)
                EnsureComp<InteractTrackerComponent>(ent).ObjectiveUid = uid;

            interactComp.HasSpawned = spawned.Count > 0;
        }
    }

    private void OnReset(EntityUid uid, InteractObjectiveComponent comp, ref ObjectiveResetEvent args)
    {
        comp.CompletionsPerFaction.Clear();
        comp.HasSpawned = false;

        var query = EntityQueryEnumerator<InteractTrackerComponent, TransformComponent>();
        while (query.MoveNext(out var ent, out var tracker, out var xform))
        {
            if (tracker.ObjectiveUid != uid || xform.MapID != Transform(uid).MapID)
                continue;

            tracker.CurrentInteractions = 0;
            tracker.CompletionsPerFaction.Clear();
            tracker.InteractionsPerFaction.Clear();
        }
    }

    private int RegisterPreplacedEntities(EntityUid objectiveUid, InteractObjectiveComponent comp)
    {
        var interactableSet = comp.Interactables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int registered = 0;
        var objMap = Transform(objectiveUid).MapID;

        var query = AllEntityQuery<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out var ent, out var meta, out var xform))
        {
            if (xform.MapID != objMap || ent == objectiveUid || HasComp<InteractTrackerComponent>(ent))
                continue;
            if (meta.EntityPrototype?.ID is not { } proto || !interactableSet.Contains(proto))
                continue;

            EnsureComp<InteractTrackerComponent>(ent).ObjectiveUid = objectiveUid;
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
        var interested = _objInt.GetInterestedObjectives(map, new[] { proto });

        foreach (var objUid in interested)
        {
            if (!TryComp(objUid, out InteractObjectiveComponent? interactComp)
                    || !TryComp(objUid, out CMUObjectiveComponent? auComp) || !auComp.Active)
                continue;

            if (HasComp<InteractTrackerComponent>(uid))
                continue;

            EnsureComp<InteractTrackerComponent>(uid).ObjectiveUid = objUid;
        }
    }

    private void OnDoAfterComplete(EntityUid uid, InteractTrackerComponent tracker, InteractObjectiveDoAfterEvent args)
    {
        if (args.Cancelled || !TryComp(tracker.ObjectiveUid, out InteractObjectiveComponent? interactComp)
                || !TryComp(tracker.ObjectiveUid, out CMUObjectiveComponent? auComp))
            return;

        var faction = args.Faction.ToLowerInvariant();
        if (string.IsNullOrEmpty(faction)) return;

        if (!auComp.FactionNeutral && auComp.Faction.ToLowerInvariant() != faction
                && auComp.Factions.All(f => f.ToLowerInvariant() != faction))
            return;

        if (auComp.StatusesPerFaction.TryGetValue(faction, out var s) && s == CMUObjectiveComponent.ObjectiveStatus.Completed)
            return;

        var entityCompletions = tracker.CompletionsPerFaction.GetValueOrDefault(faction, 0);
        if (entityCompletions >= interactComp.CompletionsPerEnt)
            return;

        tracker.InteractionsPerFaction.TryAdd(faction, 0);
        tracker.InteractionsPerFaction[faction]++;

        var current = tracker.InteractionsPerFaction[faction];
        _popup.PopupEntity(interactComp.DoAfterMessageComplete, uid, args.User != EntityUid.Invalid ? args.User : uid, PopupType.Medium);

        if (current < interactComp.InteractionsNeeded)
            return;

        tracker.InteractionsPerFaction[faction] = 0;
        tracker.CompletionsPerFaction.TryAdd(faction, 0);
        tracker.CompletionsPerFaction[faction]++;

        interactComp.CompletionsPerFaction.TryAdd(faction, 0);
        interactComp.CompletionsPerFaction[faction]++;

        var totalNeeded = interactComp.TotalCompletionsNeeded > 0
            ? interactComp.TotalCompletionsNeeded
            : interactComp.SpawnCount;

        _objCtrl.AwardPointsToFaction(faction, auComp);

        if (interactComp.DestroyOnComplete
                && tracker.CompletionsPerFaction[faction] >= interactComp.CompletionsPerEnt
                && Exists(uid))
            QueueDel(uid);

        if (interactComp.CompletionsPerFaction[faction] >= totalNeeded)
        {
            _objInt.UnregisterInterest(tracker.ObjectiveUid);
            _objCtrl.CompleteObjectiveForFaction(tracker.ObjectiveUid, auComp, faction);
        }
    }
}

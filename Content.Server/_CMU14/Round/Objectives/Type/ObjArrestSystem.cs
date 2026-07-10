using System.Linq;
using Content.Server.GameTicking;
using Content.Server.Roles.Jobs;
using Content.Shared._CMU14.Round.Objectives;
using Content.Shared._CMU14.Round.Objectives.Type;
using Content.Shared._RMC14.Synth;
using Content.Shared.Cuffs;
using Content.Shared.Cuffs.Components;
using Content.Shared.NPC.Components;
using Content.Shared.Mind.Components;
using Robust.Shared.Timing;

namespace Content.Server._CMU14.Round.Objectives.Type;

public sealed class ObjArrestSystem : ObjectiveSystem
{
    [Dependency] private ObjectiveControlSystem _objCtrl = default!;
    [Dependency] private ObjectiveInterestSystem _objInt = default!;
    [Dependency] private GameTicker _gameTicker = default!;
    [Dependency] private JobSystem _jobSystem = default!;
    [Dependency] private SharedCuffableSystem _cuffableSystem = default!;
    private ISawmill _logs = default!;
    private bool _shuttingDown;

    public override void Initialize()
    {
        base.Initialize();
        _logs = Logger.GetSawmill("obj-arrest");
        _shuttingDown = false;
        SubscribeLocalEvent<CMUObjectiveComponent, ObjectiveActivatedEvent>(OnActivated);
        SubscribeLocalEvent<ArrestObjectiveComponent, ObjectiveResetEvent>(OnReset);
        SubscribeLocalEvent<MetaDataComponent, ComponentStartup>(OnEntityMetaStartup);
        SubscribeLocalEvent<ArrestMarkedForComponent, CuffedStateChangeEvent>(OnCuffStateChanged);
    }

    public override void Shutdown()
    {
        _shuttingDown = true;
        base.Shutdown();
    }

    private void OnActivated(EntityUid uid, CMUObjectiveComponent comp, ref ObjectiveActivatedEvent _)
    {
        if (!TryComp(uid, out ArrestObjectiveComponent? arrestComp) || !comp.Active || arrestComp.HasSpawned)
            return;

        if (arrestComp.SpawnMob && !string.IsNullOrEmpty(arrestComp.TargetPrototype))
            ActivateArrestObjective(uid, arrestComp);

        var objMap = Transform(uid).MapID;
        _objInt.RegisterInterest(uid, objMap,
            keys: string.IsNullOrEmpty(arrestComp.FactionToArrest) ? null : new[] { arrestComp.FactionToArrest.ToLowerInvariant() },
            wildcard: comp.FactionNeutral);

        MarkExistingEntities(uid, arrestComp, comp, objMap);
    }

    private void OnReset(EntityUid uid, ArrestObjectiveComponent comp, ref ObjectiveResetEvent args)
    {
        comp.AmountArrestedPerFaction.Clear();
        comp.HasSpawned = false;
    }

    private void ActivateArrestObjective(EntityUid uid, ArrestObjectiveComponent arrestComp)
    {
        var objMap = Transform(uid).MapID;
        var (specific, generic) = FindMarkers(objMap, arrestComp.SpawnMarkerId);
        List<EntityUid> markers;
        if (!string.IsNullOrEmpty(arrestComp.SpawnMarkerId))
            markers = specific;
        else
            markers = specific.Count > 0 ? specific : generic;

        for (var i = 0; i < arrestComp.SpawnCount; i++)
        {
            if (markers.Count == 0) break;
            var markerUid = markers[i % markers.Count];
            var xform = Comp<TransformComponent>(markerUid);
            Spawn(arrestComp.TargetPrototype, xform.Coordinates);
        }
        arrestComp.HasSpawned = true;
    }

    private void OnEntityMetaStartup(EntityUid uid, MetaDataComponent meta, ref ComponentStartup args)
    {
        if (_shuttingDown) return;
        if (HasComp<ArrestMarkedForComponent>(uid)) return;

        var protoId = meta.EntityPrototype?.ID ?? string.Empty;
        var factions = new List<string>();
        if (TryComp<FactionComponent>(uid, out var factionComp))
            factions.AddRange(factionComp.Factions.Select(f => f.ToLowerInvariant()));

        var map = Transform(uid).MapID;
        var interested = _objInt.GetInterestedObjectives(map, factions);

        foreach (var objUid in interested)
        {
            if (!TryComp(objUid, out ArrestObjectiveComponent? arrestComp) ||
                !TryComp(objUid, out CMUObjectiveComponent? auComp) || !auComp.Active)
                continue;

            string? creditFaction = GetCreditFaction(auComp, factions, arrestComp.FactionToArrest, _gameTicker.Preset?.ID, _objCtrl);
            if (creditFaction == null) continue;

            if (!string.IsNullOrEmpty(arrestComp.SpecificJob))
            {
                string? jobId = null;
                if (TryComp<MindComponent>(uid, out var mindCont) && mindCont.Mind != null)
                    _jobSystem.MindTryGetJob(mindCont.Mind.Value, out var jobProto);
                if (jobId == null || !jobId.Equals(arrestComp.SpecificJob, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (arrestComp.SynthOnly && !HasComp<SynthComponent>(uid)) continue;
            if (!string.IsNullOrEmpty(arrestComp.TargetPrototype) && !protoId.Equals(arrestComp.TargetPrototype, StringComparison.OrdinalIgnoreCase))
                continue;

            var mark = EnsureComp<ArrestMarkedForComponent>(uid);
            mark.AssociatedObjectives[objUid] = creditFaction;
            if (!string.IsNullOrEmpty(arrestComp.SpecificJob))
            {
                string? jobId = null;
                if (TryComp<MindComponent>(uid, out var mindCont) && mindCont.Mind != null)
                    _jobSystem.MindTryGetJob(mindCont.Mind.Value, out var jobProto);
                mark.AssociatedObjectiveJobs[objUid] = jobId;
            }
        }
    }

    private void MarkExistingEntities(EntityUid uid, ArrestObjectiveComponent comp, CMUObjectiveComponent auComp, MapId objMap)
    {
        var query = AllEntityQuery<MetaDataComponent, TransformComponent, FactionComponent>();
        while (query.MoveNext(out var ent, out var meta, out var xform, out var factionComp))
        {
            if (ent == uid || xform.MapID != objMap)
                continue;

            var factions = factionComp.Factions.Select(f => f.ToLowerInvariant()).ToList();
            if (factions.Count == 0) continue;

            if (!string.IsNullOrEmpty(comp.TargetPrototype) && meta.EntityPrototype?.ID != comp.TargetPrototype)
                continue;

            string? creditFaction = GetCreditFaction(auComp, factions, comp.FactionToArrest, _gameTicker.Preset?.ID, _objCtrl);
            if (creditFaction == null) continue;

            var mark = EnsureComp<ArrestMarkedForComponent>(ent);
            mark.AssociatedObjectives[uid] = creditFaction;
            // Job caching?
        }
    }

    private void OnCuffStateChanged(EntityUid uid, ArrestMarkedForComponent comp, ref CuffedStateChangeEvent args)
    {
        if (!TryComp<CuffableComponent>(uid, out var cuffable) || !_cuffableSystem.IsCuffed((uid, cuffable), requireFullyCuffed: false))
            return;

        var objectivesToRemove = new List<EntityUid>();
        foreach (var (objectiveUid, factionToCredit) in comp.AssociatedObjectives)
        {
            if (!TryComp(objectiveUid, out ArrestObjectiveComponent? arrestComp) ||
                !TryComp(objectiveUid, out CMUObjectiveComponent? auComp))
                continue;

            var factionKey = factionToCredit.ToLowerInvariant();
            arrestComp.AmountArrestedPerFaction.TryAdd(factionKey, 0);
            arrestComp.AmountArrestedPerFaction[factionKey]++;

            if (arrestComp.AmountArrestedPerFaction[factionKey] >= arrestComp.ArrestCount)
            {
                _objInt.UnregisterInterest(objectiveUid);
                _objCtrl.CompleteObjectiveForFaction(objectiveUid, auComp, factionToCredit);
                objectivesToRemove.Add(objectiveUid);
            }
        }

        foreach (var o in objectivesToRemove)
            comp.AssociatedObjectives.Remove(o);

        if (HasComp<KillMarkedForComponent>(uid) && objectivesToRemove.Any(o => TryComp(o, out ArrestObjectiveComponent? a) && a.RemoveKillMark))
            RemComp<KillMarkedForComponent>(uid);
    }
}

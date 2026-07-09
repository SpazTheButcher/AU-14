namespace Content.Server._CMU14.Round.Objectives.Type;

public sealed class ObjDestroySystem : ObjectiveSystem
{
    [Dependency] private ObjectiveControlSystem _objCtrl = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CMUObjectiveComponent, ObjectiveActivatedEvent>(OnActivated);
    }

    private void OnActivated(EntityUid uid, CMUObjectiveComponent comp, ref ObjectiveActivatedEvent args)
    {
        if (HasComp<DestroyObjectiveComponent>(uid))
            ActivateDestroyObjectiveIfNeeded(uid, comp);
    }


    private void ActivateDestroyObjectiveIfNeeded(EntityUid uid, CMUObjectiveComponent comp)
    {
        if (!TryComp(uid, out DestroyObjectiveComponent? destroyComp) || !comp.Active || destroyComp.EntitiesSpawned)
            return;

        // var found = FindMarkers(Transform(uid).MapID, destroyComp.SpawnMarker);
        // var markers = ResolveMarkers(found);
        // var spawned = SpawnEntitiesAtMarkers(destroyComp.EntityToDestroy, destroyComp.AmountToSpawn, markers);
        // attach tracker, register interest, mark existing entities, etc.
    }

    // _objCtrl.CompleteObjectiveForFaction
    // _objCtrl.AwardPointsToFaction

    // ResetObjectiveComponents
}

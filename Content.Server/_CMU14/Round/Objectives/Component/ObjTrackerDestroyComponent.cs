namespace Content.Server._CMU14.Round.Objectives.Component;

[RegisterComponent]
public sealed partial class ObjTrackerDestroyComponent : Robust.Shared.GameObjects.Component
{
    // Link back to the objective entity that cares about this target
    public EntityUid ObjectiveUid;
}

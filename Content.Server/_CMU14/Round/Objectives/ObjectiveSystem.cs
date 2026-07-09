namespace Content.Server._CMU14.Round.Objectives;

public abstract partial class ObjectiveSystem : EntitySystem
{
    protected virtual void ResetObjectiveComponents(EntityUid uid) { }
}

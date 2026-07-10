using Content.Shared._CMU14.Round.Objectives.Component;

namespace Content.Server._CMU14.Round.Objectives;

public abstract partial class ObjectiveSystem : EntitySystem
{
    protected static bool ShouldCompleteForFaction(
        CMUObjectiveComponent auComp,
        string faction,
        int currentAmount,
        int requiredAmount)
        => currentAmount >= requiredAmount && (auComp.FactionNeutral || faction == auComp.Faction.ToLowerInvariant());
}

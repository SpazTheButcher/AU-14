using Content.Server.Zombies;
using Content.Shared.AU14.BlackGoo;
using Content.Shared.EntityEffects;
using Robust.Shared.Prototypes;
using Content.Shared.Zombies;

namespace Content.Server.AU14.BlackGoo;

public sealed partial class CauseGooInfection : EntityEffect
{
    protected override string? ReagentEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys)
        => Loc.GetString("reagent-effect-guidebook-cause-zombie-infection", ("chance", Probability));

    // Adds the Zombie Infection Components
    public override void Effect(EntityEffectBaseArgs args)
    {
        var entityManager = args.EntityManager;
        entityManager.EnsureComponent<GooifyOnDeathComponent>(args.TargetEntity);
        entityManager.EnsureComponent<PendingGooZombieComponent>(args.TargetEntity);
    }
}

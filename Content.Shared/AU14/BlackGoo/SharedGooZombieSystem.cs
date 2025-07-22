using Content.Shared.Movement.Systems;
using Content.Shared.NameModifier.EntitySystems;
using Content.Shared.Zombies;

namespace Content.Shared.AU14.BlackGoo;

public abstract class SharedGooZombieSystem : EntitySystem
{
    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GooZombieComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshSpeed);
        SubscribeLocalEvent<GooZombieComponent, RefreshNameModifiersEvent>(OnRefreshNameModifiers);
    }

    private void OnRefreshSpeed(EntityUid uid, GooZombieComponent component, RefreshMovementSpeedModifiersEvent args)
    {
        var mod = component.ZombieMovementSpeedBuff;
        if (HasComp<AlphaGooZombieComponent>(uid))
        {
            args.ModifySpeed(mod*1.8f, mod*1.5f);

            return;
        }
        args.ModifySpeed(mod, mod);
    }

    private void OnRefreshNameModifiers(Entity<GooZombieComponent> entity, ref RefreshNameModifiersEvent args)
    {
        args.AddModifier("goo-name-prefix");
    }
}

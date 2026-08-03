using Content.Shared._CMU14.Dropship.GunshipControls;
using Content.Shared._RMC14.Dropship;
using Content.Shared._RMC14.Dropship.Weapon;
using Content.Shared.UserInterface;

namespace Content.Server._CMU14.Dropship.GunshipControls;

public sealed class GunshipControlsSystem : EntitySystem
{
    [Dependency] private SharedUserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        base.Initialize();

        Subs.BuiEvents<GunshipControlsComponent>(GunshipControlsUiKey.Key, subs =>
        {
            subs.Event<GunshipControlsOpenUiMsg>(OnOpenUi);
        });
    }

    private void OnOpenUi(Entity<GunshipControlsComponent> ent, ref GunshipControlsOpenUiMsg args)
    {
        Enum destinationKey;
        switch (args.Destination)
        {
            case GunshipControlsDestination.Navigation when HasComp<DropshipNavigationComputerComponent>(ent):
                destinationKey = DropshipNavigationUiKey.Key;
                break;
            case GunshipControlsDestination.Weapons when HasComp<DropshipTerminalWeaponsComponent>(ent):
                destinationKey = DropshipTerminalWeaponsUi.Key;
                break;
            default:
                return;
        }

        _ui.CloseUi(ent.Owner, GunshipControlsUiKey.Key, args.Actor);

        if (args.Destination == GunshipControlsDestination.Navigation)
        {
            var before = new BeforeActivatableUIOpenEvent(args.Actor);
            RaiseLocalEvent(ent.Owner, before);
        }

        if (!_ui.TryOpenUi(ent.Owner, destinationKey, args.Actor))
            return;

        if (args.Destination == GunshipControlsDestination.Navigation)
        {
            // Navigation prepares its destination list and tactical controls from
            // this event when opened through a normal ActivatableUI interaction.
            var after = new AfterActivatableUIOpenEvent(args.Actor, args.Actor);
            RaiseLocalEvent(ent.Owner, after);
        }
    }
}

using Content.Shared._RMC14.Marines.Squads;
using Content.Shared.Inventory.Events;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Content.Shared.Radio.EntitySystems;
using Content.Shared.Verbs;
using Robust.Shared.Containers;

namespace Content.Shared._RMC14.Radio;

public sealed partial class RMCRadioSystem : EntitySystem
{
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private EncryptionKeySystem _encryptionKey = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;

    private readonly HashSet<Entity<RMCHeadsetComponent, EncryptionKeyHolderComponent>> _toUpdate = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RMCHeadsetComponent, EncryptionChannelsChangedEvent>(OnHeadsetEncryptionChannelsChanged, before: new[] { typeof(SharedHeadsetSystem) });
        SubscribeLocalEvent<RMCRadioFilterComponent, GetVerbsEvent<AlternativeVerb>>(OnGetAltVerbs);

        SubscribeLocalEvent<HeadsetAutoSquadComponent, MapInitEvent>(OnHeadsetAutoSquadMapInit);
        SubscribeLocalEvent<HeadsetAutoSquadComponent, GotEquippedEvent>(OnHeadsetAutoSquadEquipped);
        SubscribeLocalEvent<HeadsetAutoSquadComponent, EncryptionChannelsChangedEvent>(OnHeadsetAutoSquadEncryptionChannelsChanged, before: new[] { typeof(SharedHeadsetSystem) });

        Subs.BuiEvents<RMCRadioFilterComponent>(RMCRadioFilterUI.Key,
            subs =>
            {
                subs.Event<RMCRadioFilterBuiMsg>(OnRadioFilterBuiMsg);
            });
    }

    private void OnHeadsetEncryptionChannelsChanged(Entity<RMCHeadsetComponent> ent, ref EncryptionChannelsChangedEvent args)
    {
        _toUpdate.Add((ent.Owner, ent.Comp, args.Component));
    }

    private void OnGetAltVerbs(Entity<RMCRadioFilterComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.Hands == null)
            return;

        var user = args.User;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = "Tune Radio",
            IconEntity = GetNetEntity(ent.Owner),
            Act = () =>
            {
                _ui.OpenUi(ent.Owner, RMCRadioFilterUI.Key, user);
            },
        });
    }

    private void OnHeadsetAutoSquadMapInit(Entity<HeadsetAutoSquadComponent> ent, ref MapInitEvent args)
    {
        RefreshHeadsetAutoSquad(ent);
    }

    private void OnHeadsetAutoSquadEquipped(Entity<HeadsetAutoSquadComponent> ent, ref GotEquippedEvent args)
    {
        RefreshHeadsetAutoSquad(ent);
    }

    private void RefreshHeadsetAutoSquad(Entity<HeadsetAutoSquadComponent> ent)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (TryComp(ent.Owner, out EncryptionKeyHolderComponent? holder) && holder.KeyContainer != null)
            _encryptionKey.UpdateChannels(ent.Owner, holder);
    }

    private void OnHeadsetAutoSquadEncryptionChannelsChanged(Entity<HeadsetAutoSquadComponent> ent, ref EncryptionChannelsChangedEvent args)
    {
        if (!_container.TryGetContainingContainer(ent.Owner, out var container) ||
            !TryComp(container.Owner, out SquadMemberComponent? member) ||
            !TryComp(member.Squad, out SquadTeamComponent? team) ||
            team.Radio is not { } radio)
        {
            return;
        }

        args.Component.Channels.Add(radio);
    }

    private void OnRadioFilterBuiMsg(Entity<RMCRadioFilterComponent> ent, ref RMCRadioFilterBuiMsg args)
    {
        if (args.Toggle)
        {
            ent.Comp.DisabledChannels.Remove(args.Channel);
        }
        else
        {
            ent.Comp.DisabledChannels.Add(args.Channel);
        }

        Dirty(ent.Owner, ent.Comp);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        try
        {
            foreach (var ent in _toUpdate)
            {
                if (TerminatingOrDeleted(ent.Owner) ||
                    !ent.Comp1.Running ||
                    !ent.Comp2.Running)
                {
                    continue;
                }

                foreach (var channel in ent.Comp1.Channels)
                {
                    ent.Comp2.Channels.Add(channel);
                    Dirty(ent.Owner, ent.Comp2);
                }
            }
        }
        finally
        {
            _toUpdate.Clear();
        }
    }
}

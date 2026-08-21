using Content.Server._RMC14.Emote;
using Content.Server.Actions;
using Content.Server.Chat.Systems;
using Content.Shared._CMU14.Vocal;
using Content.Shared.CCVar;
using Content.Shared.Chat.Prototypes;
using Content.Shared.Humanoid;
using Content.Shared.Speech;
using Content.Shared.Speech.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server.Speech.EntitySystems;

public sealed partial class VocalSystem : EntitySystem
{
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private ActionsSystem _actions = default!;
    [Dependency] private RMCEmoteSystem _rmcEmote = default!;
    [Dependency] private INetConfigurationManager _netConfig = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<VocalComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<VocalComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<VocalComponent, SexChangedEvent>(OnSexChanged);
        SubscribeLocalEvent<VocalComponent, EmoteEvent>(OnEmote);
        SubscribeLocalEvent<VocalComponent, ScreamActionEvent>(OnScreamAction);
        SubscribeLocalEvent<VocalComponent, PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeNetworkEvent<CMUScreamOnHotbarPreferenceMessage>(OnScreamOnHotbarPreference);
    }

    private void OnShutdown(EntityUid uid, VocalComponent component, ComponentShutdown args)
    {
        // remove scream action when component removed
        if (component.ScreamActionEntity != null)
        {
            _actions.RemoveAction(uid, component.ScreamActionEntity);
        }
    }

    private void OnPlayerAttached(EntityUid uid, VocalComponent component, PlayerAttachedEvent args)
    {
        // scream is off the hotbar by default; players can opt back in under CMU settings
        var enabled = _netConfig.GetClientCVar(args.Player.Channel, CCVars.CMUScreamOnHotbarEnabled);
        SetScreamOnHotbar(uid, component, enabled);
    }

    private void OnScreamOnHotbarPreference(CMUScreamOnHotbarPreferenceMessage msg, EntitySessionEventArgs args)
    {
        // lets the toggle take effect immediately, instead of waiting for the player's next spawn
        if (args.SenderSession.AttachedEntity is not { } uid || !TryComp<VocalComponent>(uid, out var component))
            return;

        SetScreamOnHotbar(uid, component, msg.Enabled);
    }

    private void SetScreamOnHotbar(EntityUid uid, VocalComponent component, bool enabled)
    {
        if (enabled)
        {
            _actions.AddAction(uid, ref component.ScreamActionEntity, component.ScreamAction);
        }
        else if (component.ScreamActionEntity != null)
        {
            _actions.RemoveAction(uid, component.ScreamActionEntity);
        }
    }

    private void OnSexChanged(EntityUid uid, VocalComponent component, SexChangedEvent args)
    {
        LoadSounds(uid, component, args.NewSex);
    }

    private void OnEmote(EntityUid uid, VocalComponent component, ref EmoteEvent args)
    {
        if (args.Handled || !args.Emote.Category.HasFlag(EmoteCategory.Vocal))
            return;

        // snowflake case for wilhelm scream easter egg
        if (args.Emote.ID == component.ScreamId)
        {
            args.Handled = TryPlayScreamSound(uid, component);
            return;
        }

        if (component.EmoteSounds is not { } sounds)
            return;

        // just play regular sound based on emote proto
        args.Handled = _chat.TryPlayEmoteSound(uid, _proto.Index(sounds), args.Emote);
    }

    private void OnMapInit(EntityUid uid, VocalComponent component, MapInitEvent args)
    {
        LoadSounds(uid, component);
    }

    private void OnScreamAction(EntityUid uid, VocalComponent component, ScreamActionEvent args)
    {
        if (args.Handled)
            return;

        _chat.TryEmoteWithChat(uid, component.ScreamId);
        args.Handled = true;
    }

    private bool TryPlayScreamSound(EntityUid uid, VocalComponent component)
    {
        if (_random.Prob(component.WilhelmProbability))
        {
            _audio.PlayPvs(component.Wilhelm, uid, component.Wilhelm.Params);
            return true;
        }

        if (component.EmoteSounds is not { } sounds)
            return false;

        return _chat.TryPlayEmoteSound(uid, _proto.Index(sounds), component.ScreamId);
    }

    private void LoadSounds(EntityUid uid, VocalComponent component, Sex? sex = null)
    {
        if (component.Sounds == null)
            return;

        sex ??= CompOrNull<HumanoidAppearanceComponent>(uid)?.Sex ?? Sex.Unsexed;

        if (!component.Sounds.TryGetValue(sex.Value, out var protoId))
            return;

        if (!_proto.HasIndex(protoId))
            return;

        component.EmoteSounds = protoId;
    }
}

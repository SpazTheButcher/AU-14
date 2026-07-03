using Content.Server._RMC14.Announce;
using Content.Server._RMC14.Rules;
using Content.Server.Administration.Logs;
using Content.Server.Radio.EntitySystems;
using Content.Shared._RMC14.AlertLevel;
using Content.Shared._RMC14.Announce;
using Content.Shared._RMC14.ARES;
using Content.Shared._RMC14.ARES.Logs;
using Content.Shared._RMC14.Dropship;
using Content.Shared._RMC14.Intel;
using Content.Shared._RMC14.Marines;
using Content.Shared._RMC14.Marines.Announce;
using Content.Shared._RMC14.Marines.Squads;
using Content.Shared._RMC14.Rules;
using Content.Shared._RMC14.Survivor;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.Ghost;
using Content.Shared.Radio;
using Content.Shared.UserInterface;
using Robust.Shared.Audio;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server._RMC14.Marines;

public sealed partial class MarineAnnounceSystem : SharedMarineAnnounceSystem
{
    [Dependency] private IAdminLogManager _adminLogs = default!;
    [Dependency] private AnnouncementRouterSystem _announcementRouter = default!;
    [Dependency] private ARESCoreSystem _core = default!;
    [Dependency] private CMDistressSignalRuleSystem _distressSignal = default!;
    [Dependency] private SharedDropshipSystem _dropship = default!;
    [Dependency] private RadioSystem _radio = default!;
    [Dependency] private SquadSystem _squad = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;

    private static readonly EntProtoId<ARESLogTypeComponent> LogCat = "ARESTabAnnouncementLogs";
    private static readonly ProtoId<AnnouncementPresetPrototype> PresetMarineOverwatch = "MarineOverwatch";
    private static readonly ProtoId<AnnouncementPresetPrototype> PresetMarineAlertGreen = "MarineAlertGreen";
    private static readonly ProtoId<AnnouncementPresetPrototype> PresetMarineAlertBlue = "MarineAlertBlue";
    private static readonly ProtoId<AnnouncementPresetPrototype> PresetMarineAlertRed = "MarineAlertRed";
    private static readonly ProtoId<AnnouncementPresetPrototype> PresetMarineAlertDelta = "MarineAlertDelta";
    private static readonly ProtoId<AnnouncementPresetPrototype> PresetMarineAlertLevel = "MarineAlertLevel";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MarineCommunicationsComputerComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<MarineCommunicationsComputerComponent, BoundUIOpenedEvent>(OnBUIOpened);

        SubscribeLocalEvent<RMCPlanetComponent, RMCPlanetAddedEvent>(OnPlanetAdded);

        Subs.BuiEvents<MarineCommunicationsComputerComponent>(MarineCommunicationsComputerUI.Key,
            subs =>
            {
                subs.Event<MarineCommunicationsDesignatePrimaryLZMsg>(OnMarineCommunicationsDesignatePrimaryLZMsg);
            });
    }

    private void OnMapInit(Entity<MarineCommunicationsComputerComponent> computer, ref MapInitEvent args)
    {
        UpdatePlanetMap(computer);
    }

    private void OnBUIOpened(Entity<MarineCommunicationsComputerComponent> computer, ref BoundUIOpenedEvent args)
    {
        UpdatePlanetMap(computer);
    }

    private void OnPlanetAdded(Entity<RMCPlanetComponent> ent, ref RMCPlanetAddedEvent args)
    {
        var computers = EntityQueryEnumerator<MarineCommunicationsComputerComponent>();
        while (computers.MoveNext(out var uid, out var computer))
        {
            UpdatePlanetMap((uid, computer));
        }
    }

    private void OnMarineCommunicationsDesignatePrimaryLZMsg(
        Entity<MarineCommunicationsComputerComponent> computer,
        ref MarineCommunicationsDesignatePrimaryLZMsg args)
    {
        var user = args.Actor;
        if (!TryGetEntity(args.LZ, out var lz))
        {
            Log.Warning($"{ToPrettyString(user)} tried to designate invalid entity {args.LZ} as primary LZ!");
            return;
        }

        if (!string.IsNullOrWhiteSpace(computer.Comp.Faction))
            _dropship.SetFactionController(lz.Value, computer.Comp.Faction.ToLowerInvariant());

        _dropship.TryDesignatePrimaryLZ(user, lz.Value);
        _core.CreateARESLog(computer, LogCat, $"{Name(args.Actor)} designated Primary LZ as: {Name(lz.Value)}");
    }

    private void UpdatePlanetMap(Entity<MarineCommunicationsComputerComponent> computer)
    {
        var planet = _distressSignal.SelectedPlanetMapName ?? string.Empty;
        var operation = _distressSignal.OperationName ?? string.Empty;
        var landingZones = new List<LandingZone>();

        var compFaction = string.IsNullOrWhiteSpace(computer.Comp.Faction)
            ? null
            : computer.Comp.Faction.ToLowerInvariant();

        foreach (var (id, metaData) in _dropship.GetPrimaryLZCandidates(compFaction))
        {
            if (!string.IsNullOrWhiteSpace(computer.Comp.Faction) &&
                TryComp<DropshipDestinationComponent>(id, out var dest) &&
                !string.IsNullOrWhiteSpace(dest.FactionController))
            {
                continue;
            }

            landingZones.Add(new LandingZone(GetNetEntity(id), metaData.EntityName));
        }

        landingZones.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

        var state = new MarineCommunicationsComputerBuiState(planet, operation, landingZones);
        _ui.SetUiState(computer.Owner, MarineCommunicationsComputerUI.Key, state);
    }

    public override void AnnounceToMarines(
        string message,
        SoundSpecifier? sound = null,
        Filter? filter = null,
        bool excludeSurvivors = true,
        string? faction = null)
    {
        filter ??= string.IsNullOrWhiteSpace(faction)
            ? BuildAllMarineAnnouncementFilter()
            : BuildMarineAnnouncementFilter(faction);

        if (excludeSurvivors)
            filter.RemoveWhereAttachedEntity(HasComp<RMCSurvivorComponent>);

        filter.RemoveWhereAttachedEntity(HasComp<IntelRescueSurvivorObjectiveComponent>);

        _announcementRouter.Announce(new AnnouncementRequest
        {
            Message = message,
            Route = new AnnouncementRoute
            {
                Target = AnnouncementTarget.Marines,
                Channels = AnnouncementChannels.Chat | AnnouncementChannels.Sound,
            },
            Chat = new AnnouncementChatOptions
            {
                Message = message,
                WrappedMessage = message,
                Channel = ChatChannel.Radio,
            },
            Sound = CreateSoundOptions(sound ?? DefaultAnnouncementSound),
        }, filter);
    }

    public override void AnnounceHighCommand(
        string message,
        string? author = null,
        SoundSpecifier? sound = null)
    {
        var wrappedMessage = FormatHighCommand(author, message);
        AnnounceToMarines(wrappedMessage, sound);
    }

    public override void AnnounceRadio(
        EntityUid sender,
        string message,
        ProtoId<RadioChannelPrototype> channel)
    {
        base.AnnounceRadio(sender, message, channel);

        _adminLogs.Add(LogType.RMCMarineAnnounce, $"{ToPrettyString(sender):source} marine announced radio message: {message}");
        _radio.SendRadioMessage(sender, message, channel, sender);
    }

    public override void AnnounceARESStaging(
        EntityUid? source,
        string message,
        SoundSpecifier? sound = null,
        LocId? announcement = null,
        string? faction = null)
    {
        base.AnnounceARESStaging(source, message, sound, announcement, faction);

        message = FormatARESStaging(announcement, message);

        var filter = string.IsNullOrWhiteSpace(faction)
            ? BuildAllMarineAnnouncementFilter()
            : BuildMarineAnnouncementFilter(faction);

        AnnounceToMarines(message, sound, filter);
        _adminLogs.Add(LogType.RMCMarineAnnounce, $"{ToPrettyString(source):player} APOLLO announced message: {message}");
    }

    public override void AnnounceSquad(string message, EntProtoId<SquadTeamComponent> squad, SoundSpecifier? sound = null)
    {
        base.AnnounceSquad(message, squad, sound);

        var filter = Filter.Empty().AddWhereAttachedEntity(e => _squad.IsInSquad(e, squad));
        AnnounceToFilter(message, filter, sound ?? DefaultSquadSound);
    }

    public override void AnnounceSquad(string message, EntityUid squad, SoundSpecifier? sound = null)
    {
        base.AnnounceSquad(message, squad, sound);

        var filter = Filter.Empty().AddWhereAttachedEntity(e => _squad.IsInSquad(e, squad));
        AnnounceToFilter(message, filter, sound ?? DefaultSquadSound);
    }

    public override void AnnounceSingle(string message, EntityUid receiver, SoundSpecifier? sound = null)
    {
        base.AnnounceSingle(message, receiver, sound);

        if (!TryComp(receiver, out ActorComponent? actor))
            return;

        var filter = Filter.Empty().AddPlayer(actor.PlayerSession);
        AnnounceToFilter(message, filter, sound);
    }

    protected override void AnnounceSignedUi(
        EntityUid sender,
        string message,
        string author,
        string name,
        SoundSpecifier? sound,
        Filter? filter,
        bool excludeSurvivors,
        string? faction)
    {
        var overlayFilter = filter == null
            ? BuildMarineAnnouncementFilter(faction)
            : Filter.Empty().AddPlayers(filter.Recipients);

        if (excludeSurvivors)
            overlayFilter.RemoveWhereAttachedEntity(HasComp<RMCSurvivorComponent>);

        overlayFilter.RemoveWhereAttachedEntity(HasComp<IntelRescueSurvivorObjectiveComponent>);

        _announcementRouter.Announce(new AnnouncementRequest
        {
            Message = message,
            Preset = AnnouncementRouterSystem.PresetMarineCommand,
            Route = new AnnouncementRoute
            {
                Target = AnnouncementTarget.Marines,
                Speaker = sender,
                Source = sender,
                Channels = AnnouncementChannels.Overlay,
            },
        }, overlayFilter);
    }

    private Filter BuildMarineAnnouncementFilter(string? faction)
    {
        var targetFaction = ResolveAnnouncementFaction(faction);
        return Filter.Empty().AddWhereAttachedEntity(e =>
        {
            if (TryComp<MarineComponent>(e, out var marine))
                return IsMarineAnnouncementRecipient(marine.Faction, targetFaction);

            return HasComp<GhostComponent>(e);
        });
    }

    private Filter BuildAllMarineAnnouncementFilter()
    {
        return Filter.Empty().AddWhereAttachedEntity(e =>
            HasComp<MarineComponent>(e) ||
            HasComp<GhostComponent>(e));
    }

    public override void AnnounceOverwatchSquad(
        EntityUid sender,
        string message,
        EntityUid squad,
        Color squadColor,
        string squadName,
        SoundSpecifier? sound = null)
    {
        var color = squadColor;
        ProtoId<AnnouncementPresetPrototype> preset = PresetMarineOverwatch;

        if (TryComp(squad, out SquadTeamComponent? squadComp))
        {
            color = squadComp.AccessibleColor ?? squadComp.Color;
            preset = squadComp.OverwatchAnnouncementPreset;
        }

        var colorHex = color.ToHex();
        var chatMessage =
            $"[color={colorHex}][bold]Overwatch:[/bold] transmits: [font size=16][bold]{message}[/bold][/font][/color]";

        var filter = Filter.Empty().AddWhereAttachedEntity(e => _squad.IsInSquad(e, squad));
        _announcementRouter.Announce(new AnnouncementRequest
        {
            Message = $"Overwatch transmits: {message}",
            Preset = preset,
            Route = new AnnouncementRoute
            {
                Target = AnnouncementTarget.Marines,
                Channels = AnnouncementChannels.Chat | AnnouncementChannels.Overlay | AnnouncementChannels.Sound,
            },
            Chat = new AnnouncementChatOptions
            {
                Message = chatMessage,
                WrappedMessage = chatMessage,
                Channel = ChatChannel.Radio,
            },
            Sound = CreateSoundOptions(sound),
        }, filter);
    }

    public override void AnnounceAlertLevel(RMCAlertLevels level, string message, Filter? filter = null)
    {
        var preset = level switch
        {
            RMCAlertLevels.Green => PresetMarineAlertGreen,
            RMCAlertLevels.Blue => PresetMarineAlertBlue,
            RMCAlertLevels.Red => PresetMarineAlertRed,
            RMCAlertLevels.Delta => PresetMarineAlertDelta,
            _ => PresetMarineAlertLevel,
        };

        var request = new AnnouncementRequest
        {
            Message = message,
            Preset = preset,
            Route = new AnnouncementRoute
            {
                Target = AnnouncementTarget.Marines,
                Channels = AnnouncementChannels.Overlay,
            },
        };

        if (filter != null)
            _announcementRouter.Announce(request, filter);
        else
            _announcementRouter.Announce(request);
    }

    private void AnnounceToFilter(string message, Filter filter, SoundSpecifier? sound)
    {
        _announcementRouter.Announce(new AnnouncementRequest
        {
            Message = message,
            Route = new AnnouncementRoute
            {
                Target = AnnouncementTarget.Marines,
                Channels = AnnouncementChannels.Chat | AnnouncementChannels.Sound,
            },
            Chat = new AnnouncementChatOptions
            {
                Message = message,
                WrappedMessage = message,
                Channel = ChatChannel.Radio,
            },
            Sound = CreateSoundOptions(sound),
        }, filter);
    }

    private static AnnouncementSoundOptions? CreateSoundOptions(SoundSpecifier? sound)
    {
        if (sound == null)
            return null;

        return new AnnouncementSoundOptions
        {
            Sound = sound,
        };
    }
}

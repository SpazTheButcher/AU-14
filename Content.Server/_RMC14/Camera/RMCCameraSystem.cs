using System.Linq;
using Content.Server.Camera;
using Content.Server.SurveillanceCamera;
using Content.Shared._RMC14.Camera;
using Content.Shared.Camera;
using Content.Shared.CCVar;
using Content.Shared.SurveillanceCamera;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._RMC14.Camera;

public sealed partial class RMCCameraSystem : SharedRMCCameraSystem
{
    private static readonly TimeSpan ViewerValidationInterval = TimeSpan.FromSeconds(0.5);

    [Dependency] private CameraNetworkSystem _cameraNetworks = default!;
    [Dependency] private CameraSessionSystem _cameraSessions = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private IGameTiming _serverTiming = default!;
    [Dependency] private UserInterfaceSystem _userInterface = default!;

    private EntityQuery<ActorComponent> _actorQuery;
    private TimeSpan _nextViewerValidation;

    public override void Initialize()
    {
        base.Initialize();

        _actorQuery = GetEntityQuery<ActorComponent>();

        SubscribeLocalEvent<RMCCameraComputerComponent, CameraReceiverChangedEvent>(OnCameraReceiverChanged);
        SubscribeLocalEvent<RMCCameraComputerComponent, CameraSessionChangedEvent>(OnCameraSessionChanged);
        SubscribeLocalEvent<RMCCameraNetworkEditorComponent, ComponentShutdown>(OnCameraEditorShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_serverTiming.CurTime < _nextViewerValidation)
            return;

        _nextViewerValidation = _serverTiming.CurTime + ViewerValidationInterval;
        var query = EntityQueryEnumerator<RMCCameraComputerComponent>();
        while (query.MoveNext(out var uid, out var computer))
        {
            foreach (var watcher in computer.Watchers.ToArray())
            {
                if (!CanUseComputer((uid, computer), watcher))
                    RemoveComputerWatcher((uid, computer), watcher);
            }
        }
    }

    private void OnCameraReceiverChanged(
        Entity<RMCCameraComputerComponent> ent,
        ref CameraReceiverChangedEvent args)
    {
        var old = ent.Comp.CurrentCamera;
        RebuildComputerCameras(ent.Owner, ent.Comp);

        if (old is { } camera && !CanSelectCamera(ent, camera))
        {
            ent.Comp.CurrentCamera = null;
            Refresh(ent, camera);
            return;
        }

        Dirty(ent);
        UpdateUserInterface(ent);
    }

    private void OnCameraSessionChanged(
        Entity<RMCCameraComputerComponent> computer,
        ref CameraSessionChangedEvent args)
    {
        if (!_actorQuery.TryComp(args.Actor, out var actor)
            || !_cameraSessions.TryGetSession(actor.PlayerSession, computer.Owner, out var session)
            || !_userInterface.IsUiOpen(computer.Owner, RMCCameraUiKey.Key, args.Actor))
        {
            return;
        }

        SendSessionDelta(computer, args.Actor, session);
    }

    public override void RebuildComputerCameras(EntityUid computerUid, RMCCameraComputerComponent? computer = null)
    {
        if (!Resolve(computerUid, ref computer, false)
            || !TryComp(computerUid, out CameraNetworkReceiverComponent? receiver))
        {
            return;
        }

        EnsureActiveNetwork((computerUid, computer), BuildAvailableNetworks(computerUid));

        var effectiveNetworks = _cameraNetworks.GetEffectiveNetworks(computerUid);
        var cameras = _cameraNetworks.GetAccessibleCameras((computerUid, receiver))
            .Where(camera => TryComp(camera, out RMCCameraComponent? _))
            .Where(camera => computer.ActiveNetwork is { } selectedNetwork &&
                             effectiveNetworks.Contains(selectedNetwork) &&
                             TryComp(camera, out CameraNetworkMemberComponent? member) &&
                             member.Networks.Contains(selectedNetwork))
            .Select(camera => (Camera: camera, Component: Comp<RMCCameraComponent>(camera)))
            .OrderBy(camera => GetCameraName(camera.Camera, camera.Component), StringComparer.Ordinal)
            .ThenBy(camera => camera.Camera.Id)
            .ToList();

        computer.CameraIds = cameras.Select(camera => GetNetEntity(camera.Camera)).ToList();
        computer.CameraNames = cameras.Select(camera => GetCameraName(camera.Camera, camera.Component)).ToList();

        if (computer.CurrentCamera is { } current &&
            !computer.CameraIds.Contains(GetNetEntity(current)))
        {
            computer.CurrentCamera = null;
        }

        Dirty(computerUid, computer);
    }

    public RMCCameraBuiState BuildBuiState(Entity<RMCCameraComputerComponent> computer)
    {
        var mapEnabled = _configuration.GetCVar(CCVars.CMUCameraMapEnabled);
        var editorEnabled = _configuration.GetCVar(CCVars.CMUCameraEditorEnabled);
        var networks = BuildAvailableNetworks(computer.Owner);
        EnsureActiveNetwork(computer, networks);
        return new RMCCameraBuiState(
            mapEnabled
                ? BuildSelectedMapState(computer.Owner, computer.Comp.ActiveNetwork)
                : new CameraMapUiState(default, []),
            networks,
            computer.Comp.ActiveNetwork,
            editorEnabled ? BuildEditorState(computer) : null,
            mapEnabled,
            editorEnabled);
    }

    public List<CameraNetworkUiData> BuildAvailableNetworks(EntityUid computer)
    {
        if (!TryComp(computer, out CameraNetworkReceiverComponent? receiver))
            return [];

        var hiddenNetworks = TryComp(computer, out RMCCameraNetworkEditorComponent? editor)
            ? editor.HiddenSeededNetworks
            : [];
        return _cameraNetworks.GetEffectiveNetworks(computer)
            .Where(network => !hiddenNetworks.Contains(_cameraNetworks.ResolveNetwork(network)))
            .Select(network => TryResolveNetworkName(computer, _cameraNetworks.ResolveNetwork(network), out var name)
                ? new CameraNetworkUiData(network, name)
                : null)
            .Where(network => network != null)
            .Select(network => network!)
            .OrderBy(network => network.Name, StringComparer.Ordinal)
            .ThenBy(network => network.Id.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    public bool TrySelectNetwork(
        Entity<RMCCameraComputerComponent> computer,
        ProtoId<CameraNetworkPrototype> network)
    {
        if (!BuildAvailableNetworks(computer.Owner).Any(available => available.Id == network))
            return false;

        var old = computer.Comp.CurrentCamera;
        computer.Comp.ActiveNetwork = network;
        RebuildComputerCameras(computer.Owner, computer.Comp);
        if (old is { } camera && !computer.Comp.CameraIds.Contains(GetNetEntity(camera)))
        {
            computer.Comp.CurrentCamera = null;
            Refresh(computer, old);
        }
        else
        {
            Dirty(computer);
            UpdateUserInterface(computer);
        }
        return true;
    }

    protected override void RefreshRejectedSelection(Entity<RMCCameraComputerComponent> computer)
    {
        UpdateUserInterface(computer);
    }

    public override bool TrySelectCameraFor(
        Entity<RMCCameraComputerComponent> computer,
        EntityUid actor,
        EntityUid camera)
    {
        return TryGetSession(computer, actor, out var session)
            && CanSelectCamera(computer, camera, session.ActiveNetwork)
            && _cameraSessions.SelectCamera(session.Id, camera);
    }

    public override bool TrySelectCamera(Entity<RMCCameraComputerComponent> computer, EntityUid camera)
    {
        if (!CanSelectCamera(computer, camera))
            return false;

        var old = computer.Comp.CurrentCamera;
        computer.Comp.CurrentCamera = camera;
        Refresh(computer, old);
        return true;
    }

    private bool CanSelectCamera(
        Entity<RMCCameraComputerComponent> computer,
        EntityUid camera,
        EntityUid? activeNetwork = null)
    {
        return !TerminatingOrDeleted(camera)
               && !Paused(camera)
               && _cameraNetworks.CanAccess(computer.Owner, camera)
               && TryComp(camera, out CameraNetworkMemberComponent? member)
               && (member.SourceKinds & CameraSourceKinds.Rmc) != CameraSourceKinds.None
               && (activeNetwork is { } selectedNetwork
                   ? _cameraNetworks.IsMemberOfNetwork(camera, selectedNetwork)
                   : computer.Comp.ActiveNetwork is { } legacyNetwork && member.Networks.Contains(legacyNetwork))
               && (!TryComp(camera, out SurveillanceCameraComponent? surveillance) || surveillance.Active);
    }

    protected override void Refresh(Entity<RMCCameraComputerComponent> ent, EntityUid? old)
    {
        base.Refresh(ent, old);
        UpdateUserInterface(ent);
    }

    private void UpdateUserInterface(Entity<RMCCameraComputerComponent> computer)
    {
        if (!_userInterface.IsUiOpen(computer.Owner, RMCCameraUiKey.Key))
            return;

        foreach (var session in _cameraSessions.GetSessions(computer.Owner))
        {
            if (_userInterface.IsUiOpen(computer.Owner, RMCCameraUiKey.Key, session.Actor))
                SendSessionDelta(computer, session.Actor, session);
        }
    }

    protected override void OnNetworkBuiMsg(Entity<RMCCameraComputerComponent> computer, RMCCameraNetworkBuiMsg args)
    {
        if (!TryGetSession(computer, args.Actor, out var session))
            return;

        _cameraSessions.SelectNetwork(session.Id, _cameraNetworks.ResolveNetwork(args.Network));
    }

    protected override void OnSessionNetworkBuiMsg(
        Entity<RMCCameraComputerComponent> computer,
        RMCCameraSessionNetworkBuiMsg args)
    {
        if (TryGetSession(computer, args.Actor, out var session)
            && TryGetEntity(args.Network, out var network)
            && network is { } networkUid)
        {
            _cameraSessions.SelectNetwork(session.Id, networkUid);
        }
    }

    protected override void OnSessionResyncBuiMsg(
        Entity<RMCCameraComputerComponent> computer,
        CameraSessionResyncMessage args)
    {
        if (TryGetSession(computer, args.Actor, out var session) && session.Id == args.SessionId)
            SendSessionSnapshot(computer, args.Actor, session);
    }

    private void EnsureActiveNetwork(
        Entity<RMCCameraComputerComponent> computer,
        List<CameraNetworkUiData> networks)
    {
        if (computer.Comp.ActiveNetwork is { } active && networks.Any(network => network.Id == active))
            return;

        if (networks.Count == 0)
            computer.Comp.ActiveNetwork = default;
        else
            computer.Comp.ActiveNetwork = networks[0].Id;
        Dirty(computer);
    }

    private CameraMapUiState BuildSelectedMapState(
        EntityUid computer,
        ProtoId<CameraNetworkPrototype>? activeNetwork)
    {
        var map = _cameraNetworks.BuildMapState(computer);
        if (activeNetwork is not { } selectedNetwork)
            return new CameraMapUiState(map.ConsoleGrid, []);

        if (!_cameraNetworks.GetEffectiveNetworks(computer).Contains(selectedNetwork))
            return new CameraMapUiState(map.ConsoleGrid, []);

        var grids = map.Grids
            .Select(grid => new CameraMapGridUiData(
                grid.Grid,
                grid.Name,
                grid.Markers
                    .Where(marker => TryGetEntity(marker.Camera, out var camera) &&
                                     TryComp(camera, out CameraNetworkMemberComponent? member) &&
                                     member.Networks.Contains(selectedNetwork))
                    .ToList()))
            .Where(grid => grid.Markers.Count > 0)
            .ToList();

        return new CameraMapUiState(map.ConsoleGrid, grids);
    }

    private List<EntityUid> GetVisibleNetworkEntities(EntityUid computer)
    {
        var hidden = TryComp(computer, out RMCCameraNetworkEditorComponent? editor)
            ? editor.HiddenSeededNetworks
            : [];
        return _cameraNetworks.GetEffectiveNetworkEntities(computer)
            .Where(network => !hidden.Contains(network))
            .Where(network => HasComp<CameraNetworkIdentityComponent>(network))
            .OrderBy(network => ResolveSessionNetworkName(computer, network), StringComparer.Ordinal)
            .ThenBy(network => network.Id)
            .ToList();
    }

    private CameraSessionDirectoryUiData BuildSessionDirectory(CameraViewerSession session)
    {
        var networks = GetVisibleNetworkEntities(session.Receiver)
            .Select(network => new CameraSessionNetworkUiData(
                GetNetEntity(network),
                ResolveSessionNetworkName(session.Receiver, network)))
            .ToList();
        var cameras = session.ActiveNetwork is { } activeNetwork
            ? session.AuthorizedCameras
                .Where(camera => _cameraNetworks.IsMemberOfNetwork(camera, activeNetwork))
                .Where(camera => TryComp(camera, out RMCCameraComponent? _))
                .OrderBy(camera => GetCameraName(camera, Comp<RMCCameraComponent>(camera)), StringComparer.Ordinal)
                .ThenBy(camera => camera.Id)
                .Select(camera => new CameraSessionCameraUiData(
                    GetNetEntity(camera),
                    GetCameraName(camera, Comp<RMCCameraComponent>(camera)),
                    !TryComp(camera, out SurveillanceCameraComponent? surveillance) || surveillance.Active))
                .ToList()
            : [];
        var selectedName = session.SelectedCamera is { } selected && TryComp(selected, out RMCCameraComponent? rmc)
            ? GetCameraName(selected, rmc)
            : null;
        return new CameraSessionDirectoryUiData(
            GetNetEntity(session.SelectedCamera),
            selectedName,
            networks,
            GetNetEntity(session.ActiveNetwork),
            cameras,
            (session.Capabilities & CameraSessionCapabilities.Map) != 0);
    }

    private string ResolveSessionNetworkName(EntityUid computer, EntityUid network)
    {
        return TryResolveNetworkName(computer, network, out var name)
            ? name
            : Comp<CameraNetworkIdentityComponent>(network).DisplayName;
    }

    private CameraMapUiState BuildSessionGeometry(CameraViewerSession session)
    {
        if ((session.Capabilities & CameraSessionCapabilities.Map) == 0
            || session.ActiveNetwork is not { } activeNetwork)
        {
            return new CameraMapUiState(default, []);
        }

        var map = _cameraNetworks.BuildMapState(session.Receiver);
        var grids = map.Grids
            .Select(grid => new CameraMapGridUiData(
                grid.Grid,
                grid.Name,
                grid.Markers
                    .Where(marker => TryGetEntity(marker.Camera, out var camera)
                        && camera is { } cameraUid
                        && _cameraNetworks.IsMemberOfNetwork(cameraUid, activeNetwork))
                    .ToList()))
            .Where(grid => grid.Markers.Count > 0)
            .ToList();
        return new CameraMapUiState(map.ConsoleGrid, grids);
    }

    private void SendSessionSnapshot(
        Entity<RMCCameraComputerComponent> computer,
        EntityUid actor,
        CameraViewerSession session)
    {
        _userInterface.ServerSendUiMessage(
            computer.Owner,
            RMCCameraUiKey.Key,
            new CameraSessionSnapshotMessage(session.Id, session.Revision, BuildSessionDirectory(session)),
            actor);
        session.LastSentRevision = session.Revision;
        SendSessionGeometry(computer.Owner, actor, session);
        SendEditorState(computer, actor);
    }

    private void SendSessionDelta(
        Entity<RMCCameraComputerComponent> computer,
        EntityUid actor,
        CameraViewerSession session)
    {
        if (session.LastSentRevision == 0 || session.LastSentRevision > session.Revision)
        {
            SendSessionSnapshot(computer, actor, session);
            return;
        }

        _userInterface.ServerSendUiMessage(
            computer.Owner,
            RMCCameraUiKey.Key,
            new CameraSessionDeltaMessage(
                session.Id,
                session.LastSentRevision,
                session.Revision,
                BuildSessionDirectory(session)),
            actor);
        session.LastSentRevision = session.Revision;
        SendSessionGeometry(computer.Owner, actor, session);
        SendEditorState(computer, actor);
    }

    private void SendSessionGeometry(EntityUid computer, EntityUid actor, CameraViewerSession session)
    {
        if ((session.Capabilities & CameraSessionCapabilities.Map) == 0
            || session.LastSentMarkerRevision == _cameraNetworks.MarkerRevision
            && session.LastSentGeometryNetwork == session.ActiveNetwork)
        {
            return;
        }

        _userInterface.ServerSendUiMessage(
            computer,
            RMCCameraUiKey.Key,
            new CameraSessionGeometryMessage(
                session.Id,
                _cameraNetworks.MarkerRevision,
                BuildSessionGeometry(session)),
            actor);
        session.LastSentMarkerRevision = _cameraNetworks.MarkerRevision;
        session.LastSentGeometryNetwork = session.ActiveNetwork;
    }

    private void SendEditorState(Entity<RMCCameraComputerComponent> computer, EntityUid actor)
    {
        var enabled = _configuration.GetCVar(CCVars.CMUCameraEditorEnabled);
        _userInterface.ServerSendUiMessage(
            computer.Owner,
            RMCCameraUiKey.Key,
            new RMCCameraEditorStateBuiMsg(
                enabled,
                enabled ? BuildEditorState(computer) : new RMCCameraNetworkEditorUiState(0, [], [])),
            actor);
    }

    protected override void OnComputerUiClosed(Entity<RMCCameraComputerComponent> computer, EntityUid actor)
    {
        if (_actorQuery.TryComp(actor, out var actorComponent))
            _cameraSessions.CloseSession(actorComponent.PlayerSession, computer.Owner);
    }

    protected override void OnComputerUiOpened(Entity<RMCCameraComputerComponent> computer, EntityUid actor)
    {
        if (!_actorQuery.TryComp(actor, out var actorComponent))
            return;

        var capabilities = CameraSessionCapabilities.Browse | CameraSessionCapabilities.LiveView;
        if (_configuration.GetCVar(CCVars.CMUCameraMapEnabled))
            capabilities |= CameraSessionCapabilities.Map;
        if (_configuration.GetCVar(CCVars.CMUCameraEditorEnabled))
            capabilities |= CameraSessionCapabilities.Edit;

        var session = _cameraSessions.OpenSession(
            actorComponent.PlayerSession,
            actor,
            computer.Owner,
            capabilities,
            shadow: false);
        if (session == null)
            return;

        var visibleNetworks = GetVisibleNetworkEntities(computer.Owner);
        if (session.ActiveNetwork is not { } active || !visibleNetworks.Contains(active))
        {
            var first = visibleNetworks.FirstOrDefault();
            if (first != EntityUid.Invalid)
                _cameraSessions.SelectNetwork(session.Id, first);
        }

        SendSessionSnapshot(computer, actor, session);
    }

    protected override bool CanUseComputer(Entity<RMCCameraComputerComponent> computer, EntityUid actor)
    {
        return !TerminatingOrDeleted(actor) &&
            _actorQuery.TryComp(actor, out var actorComponent) &&
            actorComponent.PlayerSession.AttachedEntity == actor &&
            _userInterface.IsUiOpen(computer.Owner, RMCCameraUiKey.Key, actor) &&
            _accessReader.IsAllowed(actor, computer.Owner);
    }

    protected override void RefreshFor(Entity<RMCCameraComputerComponent> computer, EntityUid actor)
    {
        if (!TryGetSession(computer, actor, out var session))
            return;

        SendSessionSnapshot(computer, actor, session);
    }

    protected override void DisconnectFor(Entity<RMCCameraComputerComponent> computer, EntityUid actor)
    {
        if (TryGetSession(computer, actor, out var session))
            _cameraSessions.SelectCamera(session.Id, null);
    }

    protected override void SelectRelativeCamera(
        Entity<RMCCameraComputerComponent> computer,
        EntityUid actor,
        int offset)
    {
        if (!TryGetSession(computer, actor, out var session)
            || session.ActiveNetwork is not { } activeNetwork)
        {
            return;
        }

        var cameras = session.AuthorizedCameras
            .Where(camera => _cameraNetworks.IsMemberOfNetwork(camera, activeNetwork))
            .Where(camera => TryComp(camera, out RMCCameraComponent? _))
            .OrderBy(camera => GetCameraName(camera, Comp<RMCCameraComponent>(camera)), StringComparer.Ordinal)
            .ThenBy(camera => camera.Id)
            .ToList();
        if (cameras.Count == 0)
            return;

        var index = session.SelectedCamera is { } selected ? cameras.IndexOf(selected) + offset : 0;
        if (index < 0)
            index = cameras.Count - 1;
        else if (index >= cameras.Count)
            index = 0;

        _cameraSessions.SelectCamera(session.Id, cameras[index]);
    }

    private bool TryGetSession(
        Entity<RMCCameraComputerComponent> computer,
        EntityUid actor,
        out CameraViewerSession session)
    {
        if (CanUseComputer(computer, actor)
            && _actorQuery.TryComp(actor, out var actorComponent)
            && _cameraSessions.TryGetSession(actorComponent.PlayerSession, computer.Owner, out session))
        {
            return true;
        }

        session = default!;
        return false;
    }
}

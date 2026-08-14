using System.Linq;
using Content.Server.Camera;
using Content.Server.SurveillanceCamera;
using Content.Shared._RMC14.Camera;
using Content.Shared.Camera;
using Content.Shared.SurveillanceCamera;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._RMC14.Camera;

public sealed partial class RMCCameraSystem : SharedRMCCameraSystem
{
    [Dependency] private ViewSubscriberSystem _viewSubscriber = default!;
    [Dependency] private UserInterfaceSystem _userInterface = default!;
    [Dependency] private CameraNetworkSystem _cameraNetworks = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;

    private EntityQuery<ActorComponent> _actorQuery;

    public override void Initialize()
    {
        base.Initialize();

        _actorQuery = GetEntityQuery<ActorComponent>();

        SubscribeLocalEvent<RMCCameraWatcherComponent, PlayerAttachedEvent>(OnWatcherPlayerAttached);
        SubscribeLocalEvent<RMCCameraWatcherComponent, PlayerDetachedEvent>(OnWatcherPlayerDetached);
        SubscribeLocalEvent<RMCCameraComputerComponent, CameraReceiverChangedEvent>(OnCameraReceiverChanged);
        SubscribeLocalEvent<RMCCameraNetworkEditorComponent, ComponentShutdown>(OnCameraEditorShutdown);
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
        var networks = BuildAvailableNetworks(computer.Owner);
        EnsureActiveNetwork(computer, networks);
        return new RMCCameraBuiState(
            BuildSelectedMapState(computer.Owner, computer.Comp.ActiveNetwork),
            networks,
            computer.Comp.ActiveNetwork,
            BuildEditorState(computer));
    }

    public List<CameraNetworkUiData> BuildAvailableNetworks(EntityUid computer)
    {
        if (!TryComp(computer, out CameraNetworkReceiverComponent? receiver))
            return [];

        var editor = EnsureEditorState((computer, Comp<RMCCameraComputerComponent>(computer)));
        return _cameraNetworks.GetEffectiveNetworks(computer)
            .Where(network => !editor.HiddenSeededNetworks.Contains(network))
            .Select(network => TryResolveNetworkName(computer, network, out var name)
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

    public override bool TrySelectCamera(Entity<RMCCameraComputerComponent> computer, EntityUid camera)
    {
        if (!CanSelectCamera(computer, camera))
            return false;

        var old = computer.Comp.CurrentCamera;
        computer.Comp.CurrentCamera = camera;
        Refresh(computer, old);
        return true;
    }

    private bool CanSelectCamera(Entity<RMCCameraComputerComponent> computer, EntityUid camera)
    {
        return !TerminatingOrDeleted(camera)
               && !Paused(camera)
               && computer.Comp.CameraIds.Contains(GetNetEntity(camera))
               && _cameraNetworks.CanAccess(computer.Owner, camera)
               && TryComp(camera, out CameraNetworkMemberComponent? member)
               && (member.SourceKinds & CameraSourceKinds.Rmc) != CameraSourceKinds.None
               && (!TryComp(camera, out SurveillanceCameraComponent? surveillance) || surveillance.Active);
    }

    private void OnWatcherPlayerAttached(Entity<RMCCameraWatcherComponent> ent, ref PlayerAttachedEvent args)
    {
        foreach (var netOverride in ent.Comp.Overrides)
        {
            if (TryGetEntity(netOverride, out var over))
                _viewSubscriber.AddViewSubscriber(over.Value, args.Player);
        }
    }

    private void OnWatcherPlayerDetached(Entity<RMCCameraWatcherComponent> ent, ref PlayerDetachedEvent args)
    {
        _cameraNetworks.ClearMapViewSubscriptionsForViewer(ent.Owner);
        foreach (var netOverride in ent.Comp.Overrides)
        {
            if (TryGetEntity(netOverride, out var over))
                _viewSubscriber.RemoveViewSubscriber(over.Value, args.Player);
        }
    }

    protected override void Refresh(Entity<RMCCameraComputerComponent> ent, EntityUid? old)
    {
        base.Refresh(ent, old);

        for (var i = ent.Comp.Watchers.Count - 1; i >= 0; i--)
        {
            var watcher = ent.Comp.Watchers[i];
            if (TerminatingOrDeleted(watcher))
            {
                ent.Comp.Watchers.RemoveAt(i);
                continue;
            }

            if (!_actorQuery.TryComp(watcher, out var actor))
                continue;

            RMCCameraWatcherComponent? watcherComp = null;
            if (old != null && TryComp(watcher, out watcherComp))
                RemoveOverrides((watcher, watcherComp, actor));

            if (ent.Comp.CurrentCamera is not { } current)
                continue;

            _viewSubscriber.AddViewSubscriber(current, actor.PlayerSession);

            watcherComp ??= EnsureComp<RMCCameraWatcherComponent>(watcher);
            watcherComp.Overrides.Add(GetNetEntity(current));
            Dirty(watcher, watcherComp);
        }

        SyncMapViewSubscriptions(ent);

        UpdateUserInterface(ent);
    }

    private void SyncMapViewSubscriptions(Entity<RMCCameraComputerComponent> computer)
    {
        var map = BuildSelectedMapState(computer.Owner, computer.Comp.ActiveNetwork);
        foreach (var watcher in computer.Comp.Watchers)
            _cameraNetworks.SyncMapViewSubscriptions(computer.Owner, watcher, map);
    }

    private void UpdateUserInterface(Entity<RMCCameraComputerComponent> computer)
    {
        if (!_userInterface.IsUiOpen(computer.Owner, RMCCameraUiKey.Key))
            return;

        var state = BuildBuiState(computer);
        foreach (var watcher in computer.Comp.Watchers)
            _cameraNetworks.SyncMapViewSubscriptions(computer.Owner, watcher, state.Map);

        _userInterface.SetUiState(computer.Owner, RMCCameraUiKey.Key, state);
    }

    protected override void OnNetworkBuiMsg(Entity<RMCCameraComputerComponent> computer, RMCCameraNetworkBuiMsg args)
    {
        if (!_userInterface.IsUiOpen(computer.Owner, RMCCameraUiKey.Key))
            return;

        TrySelectNetwork(computer, args.Network);
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

    protected override void OnWatcherRemoved(Entity<RMCCameraWatcherComponent> watcher)
    {
        base.OnWatcherRemoved(watcher);
        RemoveOverrides(watcher);
    }

    protected override void OnComputerUiClosed(Entity<RMCCameraComputerComponent> computer, EntityUid actor)
    {
        _cameraNetworks.ClearMapViewSubscriptions(computer.Owner, actor);
    }

    private void RemoveOverrides(Entity<RMCCameraWatcherComponent, ActorComponent?> watcher)
    {
        if (!_actorQuery.Resolve(watcher, ref watcher.Comp2, false))
        {
            watcher.Comp1.Overrides.Clear();
            return;
        }

        foreach (var compOverride in watcher.Comp1.Overrides)
        {
            if (!TryGetEntity(compOverride, out var over))
                continue;

            _viewSubscriber.RemoveViewSubscriber(over.Value, watcher.Comp2.PlayerSession);
        }

        watcher.Comp1.Overrides.Clear();
        Dirty(watcher, watcher.Comp1);
    }
}

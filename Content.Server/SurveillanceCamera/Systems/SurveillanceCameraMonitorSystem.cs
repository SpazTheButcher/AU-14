using System.Linq;
using Content.Server.Camera;
using Content.Server.Power.Components;
using Content.Shared.Camera;
using Content.Shared.Power;
using Content.Shared.SurveillanceCamera;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.SurveillanceCamera;

public sealed partial class SurveillanceCameraMonitorSystem : EntitySystem
{
    [Dependency] private SurveillanceCameraSystem _surveillanceCameras = default!;
    [Dependency] private UserInterfaceSystem _userInterface = default!;
    [Dependency] private CameraNetworkSystem _cameraNetworks = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<SurveillanceCameraMonitorComponent, SurveillanceCameraDeactivateEvent>(OnSurveillanceCameraDeactivate);
        SubscribeLocalEvent<SurveillanceCameraMonitorComponent, PowerChangedEvent>(OnPowerChanged);
        SubscribeLocalEvent<SurveillanceCameraMonitorComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<SurveillanceCameraMonitorComponent, CameraReceiverChangedEvent>(OnCameraReceiverChanged);
        SubscribeLocalEvent<SurveillanceCameraMonitorComponent, AfterActivatableUIOpenEvent>(OnToggleInterface);
        Subs.BuiEvents<SurveillanceCameraMonitorComponent>(SurveillanceCameraMonitorUiKey.Key, subs =>
        {
            subs.Event<SurveillanceCameraRefreshCamerasMessage>(OnRefreshCamerasMessage);
            subs.Event<SurveillanceCameraRefreshSubnetsMessage>(OnRefreshSubnetsMessage);
            subs.Event<SurveillanceCameraDisconnectMessage>(OnDisconnectMessage);
            subs.Event<SurveillanceCameraMonitorSubnetRequestMessage>(OnSubnetRequest);
            subs.Event<SurveillanceCameraMonitorSwitchMessage>(OnSwitchMessage);
            subs.Event<BoundUIClosedEvent>(OnBoundUiClose);
        });
    }

    private void OnSubnetRequest(EntityUid uid, SurveillanceCameraMonitorComponent component,
        SurveillanceCameraMonitorSubnetRequestMessage args)
    {
        if (_cameraNetworks.GetEffectiveNetworks(uid).Contains(args.Network))
            component.ActiveNetwork = args.Network;

        UpdateUserInterface(uid, component);
    }

    private void OnDisconnectMessage(EntityUid uid, SurveillanceCameraMonitorComponent component,
        SurveillanceCameraDisconnectMessage message)
    {
        DisconnectCamera(uid, true, component);
    }

    private void OnRefreshCamerasMessage(EntityUid uid, SurveillanceCameraMonitorComponent component,
        SurveillanceCameraRefreshCamerasMessage message)
    {
        UpdateUserInterface(uid, component);
    }

    private void OnRefreshSubnetsMessage(EntityUid uid, SurveillanceCameraMonitorComponent component,
        SurveillanceCameraRefreshSubnetsMessage message)
    {
        UpdateUserInterface(uid, component);
    }

    private void OnSwitchMessage(EntityUid uid, SurveillanceCameraMonitorComponent component,
        SurveillanceCameraMonitorSwitchMessage message)
    {
        if (TryGetEntity(message.Camera, out var camera) && camera is { } cameraUid)
            TrySelectCamera((uid, component), cameraUid);

        UpdateUserInterface(uid, component);
    }

    private void OnPowerChanged(EntityUid uid, SurveillanceCameraMonitorComponent component, ref PowerChangedEvent args)
    {
        if (!args.Powered)
        {
            RemoveActiveCamera(uid, component);
            component.ActiveNetwork = null;
            UpdateUserInterface(uid, component);
        }
    }

    private void OnShutdown(EntityUid uid, SurveillanceCameraMonitorComponent component, ComponentShutdown args)
    {
        RemoveActiveCamera(uid, component);
        _cameraNetworks.ClearMapViewSubscriptions(uid);
    }

    private void OnToggleInterface(EntityUid uid, SurveillanceCameraMonitorComponent component,
        AfterActivatableUIOpenEvent args)
    {
        AfterOpenUserInterface(uid, args.User, component);
    }

    private void OnSurveillanceCameraDeactivate(EntityUid uid, SurveillanceCameraMonitorComponent monitor,
        SurveillanceCameraDeactivateEvent args)
    {
        DisconnectCamera(uid, false, monitor);
    }

    private void OnCameraReceiverChanged(EntityUid uid, SurveillanceCameraMonitorComponent monitor,
        ref CameraReceiverChangedEvent args)
    {
        if (monitor.ActiveCamera is { } camera && !_cameraNetworks.CanAccess(uid, camera))
        {
            DisconnectCamera(uid, true, monitor);
            return;
        }

        UpdateUserInterface(uid, monitor);
    }

    private void OnBoundUiClose(EntityUid uid, SurveillanceCameraMonitorComponent component, BoundUIClosedEvent args)
    {
        RemoveViewer(uid, args.Actor, component);
    }

    private void DisconnectCamera(EntityUid uid, bool removeViewers, SurveillanceCameraMonitorComponent? monitor = null)
    {
        if (!Resolve(uid, ref monitor))
            return;

        if (removeViewers)
            RemoveActiveCamera(uid, monitor);

        monitor.ActiveCamera = null;
        RemComp<ActiveSurveillanceCameraMonitorComponent>(uid);
        UpdateUserInterface(uid, monitor);
    }

    private void AddViewer(EntityUid uid, EntityUid player, SurveillanceCameraMonitorComponent? monitor = null)
    {
        if (!Resolve(uid, ref monitor))
            return;

        monitor.Viewers.Add(player);
        if (monitor.ActiveCamera is { } camera)
            _surveillanceCameras.AddActiveViewer(camera, player, uid);

        _cameraNetworks.SyncMapViewSubscriptions(uid, player, _cameraNetworks.BuildMapState(uid));

        UpdateUserInterface(uid, monitor);
    }

    private void RemoveViewer(EntityUid uid, EntityUid player, SurveillanceCameraMonitorComponent? monitor = null)
    {
        if (!Resolve(uid, ref monitor))
            return;

        monitor.Viewers.Remove(player);
        _cameraNetworks.ClearMapViewSubscriptions(uid, player);
        if (monitor.ActiveCamera is { } camera)
            _surveillanceCameras.RemoveActiveViewer(camera, player);
    }

    private void RemoveActiveCamera(EntityUid uid, SurveillanceCameraMonitorComponent? monitor = null)
    {
        if (!Resolve(uid, ref monitor) || monitor.ActiveCamera is not { } camera)
            return;

        _surveillanceCameras.RemoveActiveViewers(camera, monitor.Viewers, uid);
        monitor.ActiveCamera = null;
        RemComp<ActiveSurveillanceCameraMonitorComponent>(uid);
        UpdateUserInterface(uid, monitor);
    }

    public bool TrySelectCamera(Entity<SurveillanceCameraMonitorComponent> monitor, EntityUid camera)
    {
        if (TerminatingOrDeleted(camera) ||
            Paused(camera) ||
            !_cameraNetworks.CanAccess(monitor.Owner, camera) ||
            !TryComp(camera, out SurveillanceCameraComponent? source) ||
            !source.Active)
        {
            return false;
        }

        if (monitor.Comp.ActiveCamera is { } oldCamera)
            _surveillanceCameras.SwitchActiveViewers(oldCamera, camera, monitor.Comp.Viewers, monitor.Owner);
        else
            _surveillanceCameras.AddActiveViewers(camera, monitor.Comp.Viewers, monitor.Owner);

        monitor.Comp.ActiveCamera = camera;
        EnsureComp<ActiveSurveillanceCameraMonitorComponent>(monitor.Owner);
        return true;
    }

    public void AfterOpenUserInterface(EntityUid uid, EntityUid player,
        SurveillanceCameraMonitorComponent? monitor = null, ActorComponent? actor = null)
    {
        if (!Resolve(uid, ref monitor) || !Resolve(player, ref actor))
            return;

        AddViewer(uid, player, monitor);
    }

    public SurveillanceCameraMonitorUiState BuildUiState(Entity<SurveillanceCameraMonitorComponent> monitor)
    {
        var effectiveNetworks = _cameraNetworks.GetEffectiveNetworks(monitor.Owner);
        if (monitor.Comp.ActiveNetwork is not { } activeNetwork || !effectiveNetworks.Contains(activeNetwork))
        {
            if (effectiveNetworks.Count == 0)
            {
                monitor.Comp.ActiveNetwork = default;
            }
            else
            {
                monitor.Comp.ActiveNetwork = effectiveNetworks
                    .OrderBy(network => Loc.GetString(_prototypeManager.Index<CameraNetworkPrototype>(network).Name), StringComparer.Ordinal)
                    .ThenBy(network => network.ToString(), StringComparer.Ordinal)
                    .First();
            }
        }

        var networks = effectiveNetworks
            .Select(network => new CameraNetworkUiData(
                network,
                Loc.GetString(_prototypeManager.Index<CameraNetworkPrototype>(network).Name)))
            .OrderBy(network => network.Name, StringComparer.Ordinal)
            .ThenBy(network => network.Id.ToString(), StringComparer.Ordinal)
            .ToList();

        var cameras = new List<CameraListUiData>();
        if (monitor.Comp.ActiveNetwork is { } selectedNetwork &&
            TryComp(monitor.Owner, out CameraNetworkReceiverComponent? receiver))
        {
            cameras = _cameraNetworks.GetAccessibleCameras((monitor.Owner, receiver))
                .Where(camera => TryComp(camera, out CameraNetworkMemberComponent? member) &&
                                 member.Networks.Contains(selectedNetwork))
                .OrderBy(camera => Name(camera), StringComparer.Ordinal)
                .ThenBy(camera => camera.Id)
                .Select(camera =>
                {
                    var member = Comp<CameraNetworkMemberComponent>(camera);
                    var source = CompOrNull<SurveillanceCameraComponent>(camera);
                    return new CameraListUiData(
                        GetNetEntity(camera),
                        Name(camera),
                        source?.Active ?? false,
                        new HashSet<ProtoId<CameraNetworkPrototype>>(member.Networks));
                })
                .ToList();
        }

        var activeCamera = monitor.Comp.ActiveCamera;
        return new SurveillanceCameraMonitorUiState(
            GetNetEntity(activeCamera),
            activeCamera == null ? null : Name(activeCamera.Value),
            networks,
            monitor.Comp.ActiveNetwork,
            cameras,
            _cameraNetworks.BuildMapState(monitor.Owner));
    }

    private void UpdateUserInterface(EntityUid uid, SurveillanceCameraMonitorComponent? monitor = null)
    {
        if (!Resolve(uid, ref monitor) || !_userInterface.IsUiOpen(uid, SurveillanceCameraMonitorUiKey.Key))
            return;

        var state = BuildUiState((uid, monitor));
        foreach (var viewer in monitor.Viewers)
            _cameraNetworks.SyncMapViewSubscriptions(uid, viewer, state.CameraMap);

        _userInterface.SetUiState(uid, SurveillanceCameraMonitorUiKey.Key, state);
    }
}

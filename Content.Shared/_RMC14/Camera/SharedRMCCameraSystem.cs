using System.Diagnostics.CodeAnalysis;
using Content.Shared._RMC14.Areas;
using Content.Shared._RMC14.Dropship.Weapon;
using Content.Shared.GameTicking;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Shared._RMC14.Camera;

// we would be using the upstream system for cameras IF IT WAS NOT ABOMINABLE DOGSHIT
public abstract partial class SharedRMCCameraSystem : EntitySystem
{
    [Dependency] private AreaSystem _area = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly Dictionary<string, int> _cameraNames = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);

        SubscribeLocalEvent<RMCCameraComponent, MapInitEvent>(OnCameraMapInit, after: new [] { typeof(AreaSystem), typeof(SharedDropshipWeaponSystem) });
        SubscribeLocalEvent<RMCCameraComponent, ComponentRemove>(OnCameraRemove);
        SubscribeLocalEvent<RMCCameraComponent, EntityTerminatingEvent>(OnCameraTerminating);

        SubscribeLocalEvent<RMCCameraComputerComponent, MapInitEvent>(OnComputerMapInit, after: new [] { typeof(AreaSystem), typeof(SharedDropshipWeaponSystem) });

        SubscribeLocalEvent<RMCCameraWatcherComponent, ComponentRemove>(OnWatcherRemove);
        SubscribeLocalEvent<RMCCameraWatcherComponent, EntityTerminatingEvent>(OnWatcherTerminating);

        Subs.BuiEvents<RMCCameraComputerComponent>(RMCCameraUiKey.Key,
            subs =>
            {
                subs.Event<BoundUIOpenedEvent>(OnComputerBuiOpened);
                subs.Event<BoundUIClosedEvent>(OnComputerBuiClosed);
                subs.Event<RMCCameraWatchBuiMsg>(OnComputerWatchBuiMsg);
                subs.Event<RMCCameraPreviousBuiMsg>(OnComputerPreviousBuiMsg);
                subs.Event<RMCCameraNextBuiMsg>(OnComputerNextBuiMsg);
                subs.Event<RMCCameraRefreshSubnetsBuiMsg>(OnComputerRefreshSubnetsBuiMsg);
                subs.Event<RMCCameraNetworkBuiMsg>(OnComputerNetworkBuiMsg);
                subs.Event<RMCCameraDisconnectBuiMsg>(OnComputerDisconnectBuiMsg);
                subs.Event<RMCCameraNetworkEditorCreateBuiMsg>(OnEditorCreateBuiMsg);
                subs.Event<RMCCameraNetworkEditorRenameBuiMsg>(OnEditorRenameBuiMsg);
                subs.Event<RMCCameraNetworkEditorDeleteBuiMsg>(OnEditorDeleteBuiMsg);
                subs.Event<RMCCameraNetworkEditorSetHiddenBuiMsg>(OnEditorSetHiddenBuiMsg);
                subs.Event<RMCCameraNetworkEditorSaveCameraBuiMsg>(OnEditorSaveCameraBuiMsg);
            });
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        _cameraNames.Clear();
        OnCameraEditorRoundRestartCleanup();
    }

    protected virtual void OnCameraEditorRoundRestartCleanup()
    {
    }

    private void OnCameraMapInit(Entity<RMCCameraComponent> ent, ref MapInitEvent args)
    {
        var ev = new RMCLegacyCameraMapInitEvent(ent.Owner);
        RaiseLocalEvent(ent, ref ev);

        if (ent.Comp.Rename)
        {
            if (!_area.TryGetArea(ent, out _, out var areaProto))
                return;

            var areaName = areaProto.Name;
            var count = _cameraNames.GetValueOrDefault(areaName);
            _metaData.SetEntityName(ent, $"{areaName} #{++count}");
            _cameraNames[areaName] = count;
        }
        else
        {
            var name = Name(ent);
            if (ent.Comp.NameOverride != null)
                name = ent.Comp.NameOverride;

            var count = _cameraNames.GetValueOrDefault(name);
            _cameraNames[name] = count;
        }

    }

    private void OnCameraRemove(Entity<RMCCameraComponent> ent, ref ComponentRemove args)
    {
        OnCameraRemoved(ent);
    }

    private void OnCameraTerminating(Entity<RMCCameraComponent> ent, ref EntityTerminatingEvent args)
    {
        OnCameraRemoved(ent);
    }

    private void OnComputerMapInit(Entity<RMCCameraComputerComponent> ent, ref MapInitEvent args)
    {
        var ev = new RMCLegacyCameraComputerMapInitEvent(ent.Owner);
        RaiseLocalEvent(ent, ref ev);
        RebuildComputerCameras(ent.Owner, ent.Comp);
    }

    private void OnWatcherRemove(Entity<RMCCameraWatcherComponent> ent, ref ComponentRemove args)
    {
        OnWatcherRemoved(ent);
    }

    private void OnWatcherTerminating(Entity<RMCCameraWatcherComponent> ent, ref EntityTerminatingEvent args)
    {
        OnWatcherRemoved(ent);
    }

    private void OnComputerBuiOpened(Entity<RMCCameraComputerComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (_timing.ApplyingState)
            return;

        var actor = args.Actor;
        ent.Comp.Watchers.Add(actor);
        Dirty(ent);

        var watcher = EnsureComp<RMCCameraWatcherComponent>(actor);
        watcher.Computer = null;
        Dirty(actor, watcher);

        Refresh(ent, null);
    }

    private void OnComputerBuiClosed(Entity<RMCCameraComputerComponent> ent, ref BoundUIClosedEvent args)
    {
        if (_timing.ApplyingState)
            return;

        var actor = args.Actor;
        ent.Comp.Watchers.Remove(actor);
        Dirty(ent);

        OnComputerUiClosed(ent, actor);

        RemCompDeferred<RMCCameraWatcherComponent>(actor);
    }

    protected virtual void OnComputerUiClosed(Entity<RMCCameraComputerComponent> computer, EntityUid actor)
    {
    }

    private void OnComputerWatchBuiMsg(Entity<RMCCameraComputerComponent> ent, ref RMCCameraWatchBuiMsg args)
    {
        if (_timing.ApplyingState)
            return;

        if (!TryGetEntity(args.Camera, out var camera) || camera is not { } cameraUid)
        {
            RefreshRejectedSelection(ent);
            return;
        }

        if (!TrySelectCamera(ent, cameraUid))
            RefreshRejectedSelection(ent);
    }

    private void OnComputerPreviousBuiMsg(Entity<RMCCameraComputerComponent> ent, ref RMCCameraPreviousBuiMsg args)
    {
        var index = 0;
        if (ent.Comp.CurrentCamera is { } old &&
            TryGetNetEntity(old, out var netCamera))
        {
            index = ent.Comp.CameraIds.IndexOf(netCamera.Value) - 1;
            if (index < 0 || index >= ent.Comp.CameraIds.Count)
                index = ent.Comp.CameraIds.Count - 1;
        }

        if (index >= 0 &&
            index < ent.Comp.CameraIds.Count &&
            TryGetEntity(ent.Comp.CameraIds[index], out var camera) && camera is { } cameraUid)
            TrySelectCamera(ent, cameraUid);
    }

    private void OnComputerNextBuiMsg(Entity<RMCCameraComputerComponent> ent, ref RMCCameraNextBuiMsg args)
    {
        var index = 0;
        if (ent.Comp.CurrentCamera is { } old &&
            TryGetNetEntity(old, out var netCamera))
        {
            index = ent.Comp.CameraIds.IndexOf(netCamera.Value) + 1;
            if (index < 0 || index >= ent.Comp.CameraIds.Count)
                index = 0;
        }

        if (index >= 0 &&
            index < ent.Comp.CameraIds.Count &&
            TryGetEntity(ent.Comp.CameraIds[index], out var camera) && camera is { } cameraUid)
            TrySelectCamera(ent, cameraUid);
    }

    private void OnComputerRefreshSubnetsBuiMsg(Entity<RMCCameraComputerComponent> ent, ref RMCCameraRefreshSubnetsBuiMsg args)
    {
        if (_timing.ApplyingState)
            return;

        var old = ent.Comp.CurrentCamera;
        RebuildComputerCameras(ent.Owner, ent.Comp);
        if (old is { } current && !ent.Comp.CameraIds.Contains(GetNetEntity(current)))
            ent.Comp.CurrentCamera = null;

        Refresh(ent, old);
    }

    private void OnComputerNetworkBuiMsg(Entity<RMCCameraComputerComponent> ent, ref RMCCameraNetworkBuiMsg args)
    {
        if (_timing.ApplyingState)
            return;

        OnNetworkBuiMsg(ent, args);
    }

    protected virtual void OnNetworkBuiMsg(Entity<RMCCameraComputerComponent> computer, RMCCameraNetworkBuiMsg args)
    {
    }

    private void OnComputerDisconnectBuiMsg(Entity<RMCCameraComputerComponent> ent, ref RMCCameraDisconnectBuiMsg args)
    {
        if (_timing.ApplyingState)
            return;

        var old = ent.Comp.CurrentCamera;
        ent.Comp.CurrentCamera = null;
        Refresh(ent, old);
    }

    private void OnEditorCreateBuiMsg(
        Entity<RMCCameraComputerComponent> computer,
        ref RMCCameraNetworkEditorCreateBuiMsg args)
    {
        if (!_timing.ApplyingState)
            OnEditorCreate(computer, args);
    }

    private void OnEditorRenameBuiMsg(
        Entity<RMCCameraComputerComponent> computer,
        ref RMCCameraNetworkEditorRenameBuiMsg args)
    {
        if (!_timing.ApplyingState)
            OnEditorRename(computer, args);
    }

    private void OnEditorDeleteBuiMsg(
        Entity<RMCCameraComputerComponent> computer,
        ref RMCCameraNetworkEditorDeleteBuiMsg args)
    {
        if (!_timing.ApplyingState)
            OnEditorDelete(computer, args);
    }

    private void OnEditorSetHiddenBuiMsg(
        Entity<RMCCameraComputerComponent> computer,
        ref RMCCameraNetworkEditorSetHiddenBuiMsg args)
    {
        if (!_timing.ApplyingState)
            OnEditorSetHidden(computer, args);
    }

    private void OnEditorSaveCameraBuiMsg(
        Entity<RMCCameraComputerComponent> computer,
        ref RMCCameraNetworkEditorSaveCameraBuiMsg args)
    {
        if (!_timing.ApplyingState)
            OnEditorSaveCamera(computer, args);
    }

    protected virtual void OnEditorCreate(
        Entity<RMCCameraComputerComponent> computer,
        RMCCameraNetworkEditorCreateBuiMsg args)
    {
    }

    protected virtual void OnEditorRename(
        Entity<RMCCameraComputerComponent> computer,
        RMCCameraNetworkEditorRenameBuiMsg args)
    {
    }

    protected virtual void OnEditorDelete(
        Entity<RMCCameraComputerComponent> computer,
        RMCCameraNetworkEditorDeleteBuiMsg args)
    {
    }

    protected virtual void OnEditorSetHidden(
        Entity<RMCCameraComputerComponent> computer,
        RMCCameraNetworkEditorSetHiddenBuiMsg args)
    {
    }

    protected virtual void OnEditorSaveCamera(
        Entity<RMCCameraComputerComponent> computer,
        RMCCameraNetworkEditorSaveCameraBuiMsg args)
    {
    }

    protected virtual void Refresh(Entity<RMCCameraComputerComponent> ent, EntityUid? old)
    {
        Dirty(ent);
    }

    protected virtual void RefreshRejectedSelection(Entity<RMCCameraComputerComponent> computer)
    {
    }

    public virtual bool TrySelectCamera(Entity<RMCCameraComputerComponent> computer, EntityUid camera)
    {
        if (!computer.Comp.CameraIds.Contains(GetNetEntity(camera)))
            return false;

        var old = computer.Comp.CurrentCamera;
        computer.Comp.CurrentCamera = camera;
        Refresh(computer, old);
        return true;
    }

    protected virtual void OnWatcherRemoved(Entity<RMCCameraWatcherComponent> watcher)
    {
        if (TryComp(watcher.Comp.Computer, out RMCCameraComputerComponent? computer))
        {
            computer.Watchers.Remove(watcher);
            Dirty(watcher.Comp.Computer.Value, computer);
        }
    }

    public bool GetComputerCameraName(Entity<RMCCameraComputerComponent> computer, EntityUid camera, [NotNullWhen(true)] out string? name)
    {
        var index = computer.Comp.CameraIds.IndexOf(GetNetEntity(camera));
        if (index < 0 || index >= computer.Comp.CameraNames.Count)
        {
            name = default;
            return false;
        }

        if (index >= computer.Comp.CameraNames.Count)
        {
            name = default;
            return false;
        }

        name = computer.Comp.CameraNames[index];
        return true;
    }

    protected virtual void OnCameraRemoved(Entity<RMCCameraComponent> camera)
    {
    }

    public void AddProtoId(RMCCameraComputerComponent computer, EntProtoId protoId)
    {
        computer.ProtoIds.Add(protoId);
    }

    public void RemoveProtoId(RMCCameraComputerComponent computer, EntProtoId protoId)
    {
        computer.ProtoIds.Remove(protoId);
    }

    public void RefreshCameras(EntProtoId protoId)
    {
    }

    public virtual void RebuildComputerCameras(EntityUid computerUid, RMCCameraComputerComponent? computer = null)
    {
    }

    public void SetCameraId(EntityUid camera, EntProtoId? protoId, RMCCameraComponent? cameraComponent)
    {
        if (!Resolve(camera, ref cameraComponent, false))
            return;

        var oldId = cameraComponent.Id;
        cameraComponent.Id = protoId;
        Dirty(camera, cameraComponent);

        var ev = new RMCLegacyCameraIdChangedEvent(camera, oldId, protoId);
        RaiseLocalEvent(camera, ref ev);
    }

    public void SetCameraName(EntityUid camera,  string name, RMCCameraComponent? cameraComponent)
    {
        if (!Resolve(camera, ref cameraComponent, false))
            return;

        cameraComponent.NameOverride = name;
        Dirty(camera, cameraComponent);
    }

    public void SetCameraRename(EntityUid camera, bool rename, RMCCameraComponent? cameraComponent)
    {
        if (!Resolve(camera, ref cameraComponent, false))
            return;

        cameraComponent.Rename = rename;
        Dirty(camera, cameraComponent);
    }

    protected string GetCameraName(EntityUid uid, RMCCameraComponent camera)
    {
        return camera.NameOverride ?? Name(uid);
    }
}

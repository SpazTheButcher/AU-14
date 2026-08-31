using System.Numerics;
using Content.Server.Power.Components;
using Content.Server.SurveillanceCamera;
using Content.Shared.Camera;
using Content.Shared._RMC14.Camera;
using Content.Shared.GameTicking;
using Content.Shared.Power;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using System.Linq;

namespace Content.Server.Camera;

public sealed class CameraNetworkSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;

    private readonly Dictionary<EntityUid, HashSet<EntityUid>> _members = [];
    private readonly Dictionary<EntityUid, HashSet<EntityUid>> _receivers = [];
    private readonly Dictionary<EntityUid,
        Dictionary<EntityUid, HashSet<EntityUid>>> _runtimeGrants = [];
    private readonly Dictionary<EntityUid, HashSet<(EntityUid Receiver, EntityUid Network)>>
        _grantsBySource = [];
    private readonly Dictionary<ProtoId<CameraNetworkPrototype>, EntityUid> _seedNetworks = [];
    private readonly Dictionary<EntityUid, EntityUid> _pendingMarkerReceivers = [];
    private readonly Dictionary<EntityUid, TimeSpan> _mobileMarkerUpdates = [];
    private readonly HashSet<EntityUid> _legacyMembers = [];
    private readonly HashSet<EntityUid> _legacyReceivers = [];

    public ulong AuthorizationRevision { get; private set; }
    public ulong DirectoryRevision { get; private set; }
    public ulong MarkerRevision { get; private set; }

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CameraNetworkMemberComponent, ComponentStartup>(OnMemberStartup);
        SubscribeLocalEvent<CameraNetworkMemberComponent, ComponentShutdown>(OnMemberShutdown);
        SubscribeLocalEvent<CameraNetworkReceiverComponent, ComponentStartup>(OnReceiverStartup);
        SubscribeLocalEvent<CameraNetworkReceiverComponent, ComponentShutdown>(OnReceiverShutdown);
        SubscribeLocalEvent<CameraNetworkReceiverComponent, CameraNetworkGrantRequestEvent>(OnGrantRequest);
        SubscribeLocalEvent<CameraNetworkIdentityComponent, ComponentShutdown>(OnNetworkShutdown);
        SubscribeLocalEvent<RMCCameraComponent, RMCLegacyCameraMapInitEvent>(OnLegacyCameraMapInit);
        SubscribeLocalEvent<RMCCameraComputerComponent, RMCLegacyCameraComputerMapInitEvent>(OnLegacyComputerMapInit);
        SubscribeLocalEvent<RMCCameraComponent, RMCLegacyCameraIdChangedEvent>(OnLegacyCameraIdChanged);
        SubscribeLocalEvent<RMCCameraComponent, RMCLegacyCameraRemovedEvent>(OnLegacyCameraRemoved);
        SubscribeLocalEvent<RMCCameraComputerComponent, RMCLegacyCameraComputerRemovedEvent>(OnLegacyComputerRemoved);
        SubscribeLocalEvent<CameraMapMarkerComponent, ComponentStartup>(OnMarkerStartup);
        SubscribeLocalEvent<CameraMapMarkerComponent, ComponentShutdown>(OnMarkerShutdown);
        SubscribeLocalEvent<CameraMapMarkerComponent, MoveEvent>(OnMarkerMove);
        SubscribeLocalEvent<CameraMapMarkerComponent, EntityRenamedEvent>(OnMarkerRenamed);
        SubscribeLocalEvent<CameraMapMarkerComponent, PowerChangedEvent>(OnMarkerPowerChanged);
        SubscribeLocalEvent<CameraMapMarkerComponent, EntityPausedEvent>(OnMarkerPaused);
        SubscribeLocalEvent<CameraMapMarkerComponent, EntityUnpausedEvent>(OnMarkerUnpaused);
        SubscribeLocalEvent<EntityTerminatingEvent>(OnEntityTerminating);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnMemberStartup(Entity<CameraNetworkMemberComponent> ent, ref ComponentStartup args)
    {
        var networks = GetMemberNetworkEntities(ent.Comp);
        IndexMember(ent.Owner, networks);
        NotifyReceivers(networks, ent.Owner);
    }

    private void OnMemberShutdown(Entity<CameraNetworkMemberComponent> ent, ref ComponentShutdown args)
    {
        var networks = GetMemberNetworkEntities(ent.Comp);
        UnindexMember(ent.Owner, networks);
        NotifyReceivers(networks, ent.Owner);
    }

    private void OnReceiverStartup(Entity<CameraNetworkReceiverComponent> ent, ref ComponentStartup args)
    {
        IndexReceiver(ent.Owner, GetEffectiveNetworkEntities(ent.Owner, ent.Comp));
    }

    private void OnReceiverShutdown(Entity<CameraNetworkReceiverComponent> ent, ref ComponentShutdown args)
    {
        _pendingMarkerReceivers.Remove(ent.Owner);
        UnindexReceiver(ent.Owner, GetEffectiveNetworkEntities(ent.Owner, ent.Comp));
        CleanupReceiverGrants(ent.Owner);
    }

    private void OnNetworkShutdown(Entity<CameraNetworkIdentityComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.Seed is { } seed && _seedNetworks.GetValueOrDefault(seed) == ent.Owner)
            _seedNetworks.Remove(seed);

        RemoveNetworkIdentity(ent.Owner);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _members.Clear();
        _receivers.Clear();
        _runtimeGrants.Clear();
        _grantsBySource.Clear();
        _seedNetworks.Clear();
        _pendingMarkerReceivers.Clear();
        _mobileMarkerUpdates.Clear();
        _legacyMembers.Clear();
        _legacyReceivers.Clear();
        AuthorizationRevision = 0;
        DirectoryRevision = 0;
        MarkerRevision = 0;
    }

    private void OnGrantRequest(Entity<CameraNetworkReceiverComponent> ent, ref CameraNetworkGrantRequestEvent args)
    {
        if (args.Grant)
            GrantNetwork(ent.Owner, args.Network, args.Source);
        else
            RevokeNetwork(ent.Owner, args.Network, args.Source);
    }

    private void OnLegacyCameraMapInit(Entity<RMCCameraComponent> ent, ref RMCLegacyCameraMapInitEvent args)
    {
        if (HasComp<CameraNetworkMemberComponent>(ent))
            return;

        var member = EnsureComp<CameraNetworkMemberComponent>(ent);
        member.SourceKinds = CameraSourceKinds.Rmc;
        SetMemberNetworks(ent, ValidateLegacyNetworks(ent.Owner, ent.Comp.Id));
        _legacyMembers.Add(ent.Owner);
    }

    private void OnLegacyComputerMapInit(Entity<RMCCameraComputerComponent> ent, ref RMCLegacyCameraComputerMapInitEvent args)
    {
        if (HasComp<CameraNetworkReceiverComponent>(ent))
            return;

        var receiver = EnsureComp<CameraNetworkReceiverComponent>(ent);
        receiver.SupportedSources = CameraSourceKinds.Rmc;
        SetReceiverNetworks(ent, ValidateLegacyNetworks(ent.Owner, ent.Comp.ProtoIds));
        _legacyReceivers.Add(ent.Owner);
    }

    private void OnLegacyCameraIdChanged(Entity<RMCCameraComponent> ent, ref RMCLegacyCameraIdChangedEvent args)
    {
        if (!_legacyMembers.Contains(ent.Owner) || !TryComp(ent, out CameraNetworkMemberComponent? member))
            return;

        SetMemberNetworks(ent, ValidateLegacyNetworks(ent.Owner, args.NewId));
    }

    private void OnLegacyCameraRemoved(Entity<RMCCameraComponent> ent, ref RMCLegacyCameraRemovedEvent args)
    {
        if (_legacyMembers.Remove(ent.Owner))
            RemCompDeferred<CameraNetworkMemberComponent>(ent.Owner);
    }

    private void OnLegacyComputerRemoved(
        Entity<RMCCameraComputerComponent> ent,
        ref RMCLegacyCameraComputerRemovedEvent args)
    {
        if (_legacyReceivers.Remove(ent.Owner))
            RemCompDeferred<CameraNetworkReceiverComponent>(ent.Owner);
    }

    private HashSet<ProtoId<CameraNetworkPrototype>> ValidateLegacyNetworks(
        EntityUid entity,
        IEnumerable<EntProtoId> legacyIds)
    {
        var networks = new HashSet<ProtoId<CameraNetworkPrototype>>();
        foreach (var legacyId in legacyIds)
        {
            if (_prototypeManager.HasIndex<CameraNetworkPrototype>(legacyId))
            {
                networks.Add(new ProtoId<CameraNetworkPrototype>(legacyId.Id));
                continue;
            }

            Log.Warning($"RMC camera prototype '{MetaData(entity).EntityPrototype?.ID ?? "unknown"}' has invalid legacy camera network '{legacyId}'.");
        }

        return networks;
    }

    private HashSet<ProtoId<CameraNetworkPrototype>> ValidateLegacyNetworks(EntityUid entity, EntProtoId? legacyId)
    {
        return legacyId == null ? [] : ValidateLegacyNetworks(entity, [legacyId.Value]);
    }

    private void OnMarkerStartup(Entity<CameraMapMarkerComponent> ent, ref ComponentStartup args)
    {
        QueueMarkerChange(ent);
    }

    private void OnMarkerShutdown(Entity<CameraMapMarkerComponent> ent, ref ComponentShutdown args)
    {
        _mobileMarkerUpdates.Remove(ent.Owner);
        QueueMarkerChange(ent);
    }

    private void OnMarkerMove(Entity<CameraMapMarkerComponent> ent, ref MoveEvent args)
    {
        QueueMarkerChange(ent);
    }

    private void OnMarkerRenamed(Entity<CameraMapMarkerComponent> ent, ref EntityRenamedEvent args)
    {
        QueueMarkerChange(ent);
    }

    private void OnMarkerPowerChanged(Entity<CameraMapMarkerComponent> ent, ref PowerChangedEvent args)
    {
        QueueMarkerChange(ent);
    }

    private void OnMarkerPaused(Entity<CameraMapMarkerComponent> ent, ref EntityPausedEvent args)
    {
        QueueMarkerChange(ent);
    }

    private void OnMarkerUnpaused(Entity<CameraMapMarkerComponent> ent, ref EntityUnpausedEvent args)
    {
        QueueMarkerChange(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        foreach (var (marker, due) in _mobileMarkerUpdates.ToArray())
        {
            if (due > _timing.CurTime)
                continue;

            _mobileMarkerUpdates.Remove(marker);
            QueueAffectedReceivers(marker);
        }

        var pendingMarkerReceivers = _pendingMarkerReceivers.ToArray();
        // Preserve marker changes queued synchronously by receiver event handlers for the next update.
        _pendingMarkerReceivers.Clear();

        foreach (var (receiver, marker) in pendingMarkerReceivers)
            NotifyReceiver(receiver, marker, CameraReceiverChangeKind.Marker);
    }

    private void OnEntityTerminating(ref EntityTerminatingEvent args)
    {
        var entity = args.Entity.Owner;
        _legacyMembers.Remove(entity);
        _legacyReceivers.Remove(entity);
        _pendingMarkerReceivers.Remove(entity);
        CleanupSourceGrants(entity);

        if (TryComp(entity, out CameraNetworkReceiverComponent? receiver))
        {
            UnindexReceiver(entity, GetEffectiveNetworkEntities(entity, receiver));
            CleanupReceiverGrants(entity);
        }
    }

    /// <summary>
    /// Resolves a static YAML seed to the single logical network entity for this round.
    /// </summary>
    public EntityUid ResolveNetwork(ProtoId<CameraNetworkPrototype> seed)
    {
        if (_seedNetworks.TryGetValue(seed, out var existing) && !TerminatingOrDeleted(existing))
            return existing;

        var prototype = _prototypeManager.Index<CameraNetworkPrototype>(seed);
        var network = Spawn(null, MapCoordinates.Nullspace);
        var identity = AddComp<CameraNetworkIdentityComponent>(network);
        identity.Seed = seed;
        identity.DisplayName = Loc.GetString(prototype.Name);
        identity.Runtime = false;
        _seedNetworks[seed] = network;
        return network;
    }

    public EntityUid CreateNetwork(string displayName, EntityUid? owner = null)
    {
        var network = Spawn(null, MapCoordinates.Nullspace);
        var identity = AddComp<CameraNetworkIdentityComponent>(network);
        identity.DisplayName = displayName.Trim();
        identity.CreatedBy = owner;
        identity.Runtime = true;
        return network;
    }

    public bool DeleteNetwork(EntityUid network)
    {
        if (!TryComp(network, out CameraNetworkIdentityComponent? identity) || !identity.Runtime)
            return false;

        QueueDel(network);
        return true;
    }

    public bool TryGetNetworkSeed(EntityUid network, out ProtoId<CameraNetworkPrototype> seed)
    {
        if (TryComp(network, out CameraNetworkIdentityComponent? identity) && identity.Seed is { } value)
        {
            seed = value;
            return true;
        }

        seed = default;
        return false;
    }

    public HashSet<EntityUid> GetEffectiveNetworkEntities(EntityUid receiver)
    {
        return TryComp(receiver, out CameraNetworkReceiverComponent? component)
            ? GetEffectiveNetworkEntities(receiver, component)
            : [];
    }

    private HashSet<EntityUid> GetMemberNetworkEntities(CameraNetworkMemberComponent component)
    {
        var networks = component.Networks.Select(ResolveNetwork).ToHashSet();
        networks.UnionWith(component.RuntimeNetworks.Where(Exists));
        return networks;
    }

    private HashSet<EntityUid> GetReceiverBaseNetworkEntities(CameraNetworkReceiverComponent component)
    {
        var networks = component.Networks.Select(ResolveNetwork).ToHashSet();
        networks.UnionWith(component.RuntimeNetworks.Where(Exists));
        return networks;
    }

    public HashSet<EntityUid> GetAccessibleCameras(Entity<CameraNetworkReceiverComponent> receiver)
    {
        var cameras = new HashSet<EntityUid>();
        var effectiveNetworks = GetEffectiveNetworkEntities(receiver.Owner, receiver.Comp);

        foreach (var network in effectiveNetworks)
        {
            if (!_members.TryGetValue(network, out var members))
                continue;

            foreach (var member in members)
            {
                if (!TryComp(member, out CameraNetworkMemberComponent? memberComponent) ||
                    (receiver.Comp.SupportedSources & memberComponent.SourceKinds) == CameraSourceKinds.None)
                {
                    continue;
                }

                cameras.Add(member);
            }
        }

        return cameras;
    }

    public CameraMapUiState BuildMapState(EntityUid receiver)
    {
        EntityUid? consoleGrid = null;
        if (TryComp(receiver, out TransformComponent? receiverTransform))
            consoleGrid = receiverTransform.GridUid ?? receiverTransform.MapUid;

        if (!TryComp(receiver, out CameraNetworkReceiverComponent? receiverComponent))
            return new CameraMapUiState(GetNetEntity(consoleGrid), []);

        var grouped = new Dictionary<EntityUid, List<(EntityUid Camera, Vector2 Position, string Name, CameraMapMarkerStatus Status)>>();
        foreach (var camera in GetAccessibleCameras((receiver, receiverComponent)))
        {
            if (TerminatingOrDeleted(camera)
                || Paused(camera)
                || !TryComp(camera, out CameraMapMarkerComponent? marker)
                || !marker.Visible
                || !TryComp(camera, out TransformComponent? xform))
            {
                continue;
            }

            var grid = xform.GridUid ?? xform.MapUid;
            if (grid == null || TerminatingOrDeleted(grid.Value))
                continue;

            if (!grouped.TryGetValue(grid.Value, out var markers))
            {
                markers = [];
                grouped.Add(grid.Value, markers);
            }

            var worldPosition = _transform.GetWorldPosition(xform);
            var position = Vector2.Transform(worldPosition, _transform.GetInvWorldMatrix(grid.Value));
            var status = IsAvailable(camera)
                    ? CameraMapMarkerStatus.Active
                    : CameraMapMarkerStatus.Inactive;
            markers.Add((camera, position, GetCameraDisplayName(camera), status));
        }

        var grids = grouped
            .OrderBy(pair => pair.Key != consoleGrid)
            .ThenBy(pair => Name(pair.Key), StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Id)
            .Select(pair => new CameraMapGridUiData(
                GetNetEntity(pair.Key),
                Name(pair.Key),
                pair.Value
                    .OrderBy(marker => marker.Name, StringComparer.Ordinal)
                    .ThenBy(marker => marker.Camera.Id)
                    .Select(marker => new CameraMapMarkerUiData(
                        GetNetEntity(marker.Camera), marker.Position, marker.Name, marker.Status))
                    .ToList()))
            .ToList();

        return new CameraMapUiState(GetNetEntity(consoleGrid), grids);
    }

    private string GetCameraDisplayName(EntityUid camera)
    {
        return TryComp(camera, out RMCCameraComponent? rmc)
            ? rmc.NameOverride ?? Name(camera)
            : Name(camera);
    }

    public void SyncMapViewSubscriptions(EntityUid receiver, EntityUid viewer, CameraMapUiState state)
    {
        // Camera maps must never grant remote-grid PVS. Geometry will be delivered by a dedicated protocol.
    }

    public void ClearMapViewSubscriptions(EntityUid receiver, EntityUid viewer)
    {
    }

    public void ClearMapViewSubscriptions(EntityUid receiver)
    {
    }

    public void ClearMapViewSubscriptionsForViewer(EntityUid viewer)
    {
    }

    public bool CanAccess(EntityUid receiver, EntityUid camera)
    {
        if (!TryComp(receiver, out CameraNetworkReceiverComponent? receiverComponent)
            || !TryComp(camera, out CameraNetworkMemberComponent? memberComponent)
            || (receiverComponent.SupportedSources & memberComponent.SourceKinds) == CameraSourceKinds.None)
        {
            return false;
        }

        foreach (var network in GetEffectiveNetworkEntities(receiver, receiverComponent))
        {
            // ComponentShutdown leaves the component readable while its lifecycle
            // callback runs. The index is therefore the authoritative indicator
            // that this source is still a live member of the logical network.
            if (_members.TryGetValue(network, out var members)
                && members.Contains(camera))
                return true;
        }

        return false;
    }

    public bool SetMapVisibility(EntityUid camera, bool visible)
    {
        if (!TryComp(camera, out CameraMapMarkerComponent? marker)
            || marker.Visible == visible)
        {
            return false;
        }

        marker.Visible = visible;
        QueueMarkerChange((camera, marker));
        return true;
    }

    public bool IsMapVisible(EntityUid camera)
    {
        return TryComp(camera, out CameraMapMarkerComponent? marker) && marker.Visible;
    }

    public bool AddNetwork(EntityUid member, ProtoId<CameraNetworkPrototype> network)
    {
        if (!TryComp(member, out CameraNetworkMemberComponent? component)
            || !component.Networks.Add(network))
        {
            return false;
        }

        var identity = ResolveNetwork(network);
        IndexMember(member, [identity]);
        NotifyReceivers([identity], member);
        return true;
    }

    public bool RemoveNetwork(EntityUid member, ProtoId<CameraNetworkPrototype> network)
    {
        if (!TryComp(member, out CameraNetworkMemberComponent? component)
            || !component.Networks.Remove(network))
        {
            return false;
        }

        var identity = ResolveNetwork(network);
        UnindexMember(member, [identity]);
        NotifyReceivers([identity], member);
        return true;
    }

    public bool AddNetwork(EntityUid member, EntityUid network)
    {
        if (!HasComp<CameraNetworkIdentityComponent>(network)
            || !TryComp(member, out CameraNetworkMemberComponent? component)
            || !component.RuntimeNetworks.Add(network))
        {
            return false;
        }

        IndexMember(member, [network]);
        NotifyReceivers([network], member);
        return true;
    }

    public bool RemoveNetwork(EntityUid member, EntityUid network)
    {
        if (!TryComp(member, out CameraNetworkMemberComponent? component)
            || !component.RuntimeNetworks.Remove(network))
        {
            return false;
        }

        UnindexMember(member, [network]);
        NotifyReceivers([network], member);
        return true;
    }

    public bool SetMemberNetworks(EntityUid member, IEnumerable<ProtoId<CameraNetworkPrototype>> networks)
    {
        return SetMemberNetworksBatch(
            new Dictionary<EntityUid, IReadOnlyCollection<ProtoId<CameraNetworkPrototype>>>
            {
                [member] = networks.ToHashSet(),
            });
    }

    public bool SetMemberNetworksBatch(
        IReadOnlyDictionary<EntityUid, IReadOnlyCollection<ProtoId<CameraNetworkPrototype>>> updates)
    {
        var changed = new List<(
            EntityUid Member,
            CameraNetworkMemberComponent Component,
            HashSet<ProtoId<CameraNetworkPrototype>> Updated)>();
        var affected = new HashSet<EntityUid>();

        foreach (var (member, networks) in updates)
        {
            if (!TryComp(member, out CameraNetworkMemberComponent? component))
                return false;

            var updated = networks.ToHashSet();
            if (component.Networks.SetEquals(updated))
                continue;

            affected.UnionWith(component.Networks.Select(ResolveNetwork));
            affected.UnionWith(updated.Select(ResolveNetwork));
            changed.Add((member, component, updated));
        }

        if (changed.Count == 0)
            return false;

        foreach (var (member, component, _) in changed)
            UnindexMember(member, GetMemberNetworkEntities(component));

        foreach (var (member, component, updated) in changed)
        {
            component.Networks = updated;
            IndexMember(member, GetMemberNetworkEntities(component));
        }

        NotifyReceivers(affected, changed.Count == 1 ? changed[0].Member : null);
        return true;
    }

    public IReadOnlyCollection<EntityUid> GetNetworkMembers(ProtoId<CameraNetworkPrototype> network)
    {
        return _members.TryGetValue(ResolveNetwork(network), out var members)
            ? members.ToArray()
            : Array.Empty<EntityUid>();
    }

    public IReadOnlyCollection<EntityUid> GetNetworkMembers(EntityUid network)
    {
        return _members.TryGetValue(network, out var members)
            ? members.ToArray()
            : Array.Empty<EntityUid>();
    }

    public bool SetReceiverNetworks(EntityUid receiver, IEnumerable<ProtoId<CameraNetworkPrototype>> networks)
    {
        if (!TryComp(receiver, out CameraNetworkReceiverComponent? component))
            return false;

        var updated = networks.ToHashSet();
        if (component.Networks.SetEquals(updated))
            return false;

        var oldEffective = GetEffectiveNetworkEntities(receiver, component);
        component.Networks = updated;
        var newEffective = GetEffectiveNetworkEntities(receiver, component);
        UpdateReceiverIndex(receiver, oldEffective, newEffective);
        if (!oldEffective.SetEquals(newEffective))
            NotifyAuthorizationChanged(receiver);
        return true;
    }

    public bool AddReceiverNetwork(EntityUid receiver, EntityUid network)
    {
        if (!HasComp<CameraNetworkIdentityComponent>(network)
            || !TryComp(receiver, out CameraNetworkReceiverComponent? component)
            || !component.RuntimeNetworks.Add(network))
        {
            return false;
        }

        IndexReceiver(receiver, [network]);
        NotifyAuthorizationChanged(receiver);
        return true;
    }

    public bool RemoveReceiverNetwork(EntityUid receiver, EntityUid network)
    {
        if (!TryComp(receiver, out CameraNetworkReceiverComponent? component)
            || !component.RuntimeNetworks.Remove(network))
        {
            return false;
        }

        if (!_runtimeGrants.TryGetValue(receiver, out var grants) || !grants.ContainsKey(network))
            UnindexReceiver(receiver, [network]);
        NotifyAuthorizationChanged(receiver);
        return true;
    }

    public HashSet<ProtoId<CameraNetworkPrototype>> GetEffectiveNetworks(EntityUid receiver)
    {
        var networks = new HashSet<ProtoId<CameraNetworkPrototype>>();
        foreach (var network in GetEffectiveNetworkEntities(receiver))
        {
            if (TryGetNetworkSeed(network, out var seed))
                networks.Add(seed);
        }

        return networks;
    }

    public bool GrantNetwork(EntityUid receiver, ProtoId<CameraNetworkPrototype> network, EntityUid source)
    {
        return GrantNetwork(receiver, ResolveNetwork(network), source);
    }

    public bool GrantNetwork(EntityUid receiver, EntityUid network, EntityUid source)
    {
        if (!HasComp<CameraNetworkIdentityComponent>(network)
            || !TryComp(receiver, out CameraNetworkReceiverComponent? component))
            return false;

        var effectiveNetworks = GetEffectiveNetworkEntities(receiver, component);
        if (!_runtimeGrants.TryGetValue(receiver, out var grants))
        {
            grants = [];
            _runtimeGrants.Add(receiver, grants);
        }

        if (!grants.TryGetValue(network, out var sources))
        {
            sources = [];
            grants.Add(network, sources);
        }

        if (!sources.Add(source))
            return false;

        AddSourceGrant(source, receiver, network);
        if (effectiveNetworks.Add(network))
        {
            IndexReceiver(receiver, [network]);
            NotifyAuthorizationChanged(receiver);
        }

        return true;
    }

    public bool RevokeNetwork(EntityUid receiver, ProtoId<CameraNetworkPrototype> network, EntityUid source)
    {
        return RevokeNetwork(receiver, ResolveNetwork(network), source);
    }

    public bool RevokeNetwork(EntityUid receiver, EntityUid network, EntityUid source)
    {
        if (!TryComp(receiver, out CameraNetworkReceiverComponent? component))
            return false;

        return RevokeNetwork(receiver, component, network, source);
    }

    private bool RevokeNetwork(
        EntityUid receiver,
        CameraNetworkReceiverComponent component,
        EntityUid network,
        EntityUid source)
    {
        if (!_runtimeGrants.TryGetValue(receiver, out var grants)
            || !grants.TryGetValue(network, out var sources)
            || !sources.Remove(source))
        {
            return false;
        }

        RemoveSourceGrant(source, receiver, network);
        if (sources.Count != 0)
            return true;

        grants.Remove(network);
        if (grants.Count == 0)
            _runtimeGrants.Remove(receiver);

        if (GetReceiverBaseNetworkEntities(component).Contains(network))
            return true;

        UnindexReceiver(receiver, [network]);
        NotifyAuthorizationChanged(receiver);
        return true;
    }

    private void IndexMember(EntityUid member, IEnumerable<EntityUid> networks)
    {
        foreach (var network in networks)
        {
            if (!_members.TryGetValue(network, out var members))
            {
                members = [];
                _members.Add(network, members);
            }

            members.Add(member);
        }
    }

    private void UnindexMember(EntityUid member, IEnumerable<EntityUid> networks)
    {
        foreach (var network in networks)
        {
            if (!_members.TryGetValue(network, out var members))
                continue;

            members.Remove(member);
            if (members.Count == 0)
                _members.Remove(network);
        }
    }

    private void IndexReceiver(EntityUid receiver, IEnumerable<EntityUid> networks)
    {
        foreach (var network in networks)
        {
            if (!_receivers.TryGetValue(network, out var receivers))
            {
                receivers = [];
                _receivers.Add(network, receivers);
            }

            receivers.Add(receiver);
        }
    }

    private void UnindexReceiver(EntityUid receiver, IEnumerable<EntityUid> networks)
    {
        foreach (var network in networks)
        {
            if (!_receivers.TryGetValue(network, out var receivers))
                continue;

            receivers.Remove(receiver);
            if (receivers.Count == 0)
                _receivers.Remove(network);
        }
    }

    private void UpdateReceiverIndex(
        EntityUid receiver,
        IEnumerable<EntityUid> oldNetworks,
        IEnumerable<EntityUid> newNetworks)
    {
        UnindexReceiver(receiver, oldNetworks);
        IndexReceiver(receiver, newNetworks);
    }

    private HashSet<EntityUid> GetEffectiveNetworkEntities(
        EntityUid receiver,
        CameraNetworkReceiverComponent component)
    {
        var networks = GetReceiverBaseNetworkEntities(component);
        if (_runtimeGrants.TryGetValue(receiver, out var grants))
            networks.UnionWith(grants.Keys);

        return networks;
    }

    private void AddSourceGrant(EntityUid source, EntityUid receiver, EntityUid network)
    {
        if (!_grantsBySource.TryGetValue(source, out var grants))
        {
            grants = [];
            _grantsBySource.Add(source, grants);
        }

        grants.Add((receiver, network));
    }

    private void RemoveSourceGrant(EntityUid source, EntityUid receiver, EntityUid network)
    {
        if (!_grantsBySource.TryGetValue(source, out var grants))
            return;

        grants.Remove((receiver, network));
        if (grants.Count == 0)
            _grantsBySource.Remove(source);
    }

    private void CleanupSourceGrants(EntityUid source)
    {
        if (!_grantsBySource.Remove(source, out var grants))
            return;

        foreach (var (receiver, network) in grants)
        {
            if (TryComp(receiver, out CameraNetworkReceiverComponent? component))
                RevokeNetwork(receiver, component, network, source);
            else
                RemoveRuntimeGrant(receiver, network, source);
        }
    }

    private void CleanupReceiverGrants(EntityUid receiver)
    {
        if (!_runtimeGrants.Remove(receiver, out var grants))
            return;

        foreach (var (network, sources) in grants)
        {
            foreach (var source in sources)
                RemoveSourceGrant(source, receiver, network);
        }
    }

    private void RemoveRuntimeGrant(EntityUid receiver, EntityUid network, EntityUid source)
    {
        if (!_runtimeGrants.TryGetValue(receiver, out var grants)
            || !grants.TryGetValue(network, out var sources)
            || !sources.Remove(source))
        {
            return;
        }

        if (sources.Count == 0)
            grants.Remove(network);
        if (grants.Count == 0)
            _runtimeGrants.Remove(receiver);
    }

    private void RemoveNetworkIdentity(EntityUid network)
    {
        if (_members.Remove(network, out var members))
        {
            foreach (var member in members)
            {
                if (TryComp(member, out CameraNetworkMemberComponent? component))
                    component.RuntimeNetworks.Remove(network);
            }
        }

        if (_receivers.Remove(network, out var receivers))
        {
            foreach (var receiver in receivers)
            {
                if (TryComp(receiver, out CameraNetworkReceiverComponent? component))
                    component.RuntimeNetworks.Remove(network);

                if (!_runtimeGrants.TryGetValue(receiver, out var grants)
                    || !grants.Remove(network, out var sources))
                    continue;

                foreach (var source in sources)
                    RemoveSourceGrant(source, receiver, network);
                if (grants.Count == 0)
                    _runtimeGrants.Remove(receiver);

                NotifyAuthorizationChanged(receiver);
            }
        }
    }

    private void NotifyReceivers(
        IEnumerable<EntityUid> networks,
        EntityUid? member,
        CameraReceiverChangeKind kind = CameraReceiverChangeKind.MemberList)
    {
        if (kind == CameraReceiverChangeKind.MemberList)
            DirectoryRevision++;

        var notified = new HashSet<EntityUid>();
        foreach (var network in networks)
        {
            if (!_receivers.TryGetValue(network, out var receivers))
                continue;

            foreach (var receiver in receivers)
            {
                if (notified.Add(receiver))
                    NotifyReceiver(receiver, member, kind);
            }
        }
    }

    private bool IsAvailable(EntityUid camera)
    {
        if (TerminatingOrDeleted(camera) || MetaData(camera).EntityPaused)
            return false;

        if (TryComp(camera, out SurveillanceCameraComponent? surveillance) && !surveillance.Active)
            return false;

        return !TryComp(camera, out ApcPowerReceiverComponent? power) || power.Powered;
    }

    private void QueueMarkerChange(Entity<CameraMapMarkerComponent> marker)
    {
        if (marker.Comp.Mobile)
        {
            _mobileMarkerUpdates.TryAdd(marker.Owner, _timing.CurTime + marker.Comp.UpdateInterval);
            return;
        }

        QueueAffectedReceivers(marker.Owner);
    }

    private void QueueAffectedReceivers(EntityUid marker)
    {
        if (!TryComp(marker, out CameraNetworkMemberComponent? member))
            return;

        MarkerRevision++;

        foreach (var network in GetMemberNetworkEntities(member))
        {
            if (!_receivers.TryGetValue(network, out var receivers))
                continue;

            foreach (var receiver in receivers)
                _pendingMarkerReceivers.TryAdd(receiver, marker);
        }
    }

    private void NotifyAuthorizationChanged(EntityUid receiver)
    {
        AuthorizationRevision++;
        var ev = new CameraReceiverChangedEvent(CameraReceiverChangeKind.Authorization);
        RaiseLocalEvent(receiver, ref ev);
    }

    private void NotifyReceiver(EntityUid receiver, EntityUid? member, CameraReceiverChangeKind kind)
    {
        var ev = new CameraReceiverChangedEvent(kind, member);
        RaiseLocalEvent(receiver, ref ev);
    }
}

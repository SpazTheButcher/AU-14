using System.Linq;
using System.Numerics;
using Content.Server.AU14.Round;
using Content.Server.GameTicking;
using Content.Server.Maps;
using Content.Shared._CMU14.Round.Objectives.Component;
using Content.Shared._RMC14.Rules;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._CMU14.Round.Objectives;

public sealed partial class ObjectiveControlSystem : EntitySystem
{
    [Dependency] private AuRoundSystem _auRoundSystem = default!;
    [Dependency] private SharedMapSystem _mapSystem = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private GameTicker _gameTicker = default!;

    private EntityUid _objectiveMasterUid = EntityUid.Invalid;
    private MapId _planetMapId = MapId.Nullspace;
    private ISawmill _logs = default!;

    public override void Initialize()
    {
        base.Initialize();
        _logs = Logger.GetSawmill("objectives");
        SubscribeLocalEvent<PostGameMapLoad>(OnPostGameMapLoad);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _planetMapId = MapId.Nullspace;
        _objectiveMasterUid = EntityUid.Invalid;
    }

    private void OnPostGameMapLoad(PostGameMapLoad ev)
    {
        var gameMap = ev.GameMap;
        var map = ev.Map;
        var grids = ev.Grids.ToArray();

        Timer.Spawn(0, () => SetupPostGameMapLoad(gameMap, map, grids));
    }

    private void SetupPostGameMapLoad(GameMapPrototype gameMap, MapId mapId, IReadOnlyList<EntityUid> grids)
    {
        var presetId = _gameTicker.Preset?.ID;
        if (string.IsNullOrWhiteSpace(presetId))
            return;

        var selectedPlanet = _auRoundSystem.GetSelectedPlanet();
        if (selectedPlanet == null
            || !gameMap.ID.Equals(selectedPlanet.MapId, StringComparison.OrdinalIgnoreCase))
        {
            _logs.Debug($"[OBJ-CTRL] OnPostGameMapLoad: map '{gameMap.ID}' is not the voted planet '{selectedPlanet?.MapId}', skipping.");
            return;
        }

        EntityUid? bestPlanetGrid = null;
        float bestArea = -1f;
        foreach (var grid in grids)
        {
            if (!TryComp<MapGridComponent>(grid, out var gridComp))
                continue;

            var area = gridComp.LocalAABB.Width * gridComp.LocalAABB.Height;
            if (!(area > bestArea))
                continue;

            bestArea = area;
            bestPlanetGrid = grid;
        }

        if (bestPlanetGrid == null)
        {
            _logs.Warning($"[OBJ-CTRL] OnPostGameMapLoad: planet map has no valid grids!");
            return;
        }

        _planetMapId = mapId;
        EnsureComp<RMCPlanetComponent>(_mapSystem.GetMap(mapId));

        bool hasPlanetMaster = false;
        var masterScan = EntityQueryEnumerator<CMUObjectiveMasterComponent, TransformComponent>();
        while (masterScan.MoveNext(out var mUid, out _, out var mXform))
        {
            if (mXform.MapID != mapId)
                continue;

            hasPlanetMaster = true;
            _objectiveMasterUid = mUid;
            break;
        }

        if (hasPlanetMaster)
        {
            _logs.Debug($"[OBJ-CTRL] SetupPostGameMapLoad: ObjectiveMaster loaded from planet, running Main()");
            Timer.Spawn(0, Main);
            return;
        }

        bool spawnedIn = false;
        var compFactory = EntityManager.ComponentFactory;
        foreach (var proto in _proto.EnumeratePrototypes<EntityPrototype>())
        {
            if (!proto.TryComp<CMUObjectiveMasterComponent>(out var masterComp, compFactory)
                    || !string.Equals(masterComp.GamePreset, presetId, StringComparison.OrdinalIgnoreCase))
                continue;

            _objectiveMasterUid = Spawn(proto.ID, new EntityCoordinates(bestPlanetGrid.Value, Vector2.Zero));
            spawnedIn = true;
            _logs.Warning($"[OBJ-CTRL] SetupPostGameMapLoad: auto-spawned missing ObjectiveMaster '{proto.ID}' for preset '{presetId}'");
            break;
        }

        if (!spawnedIn)
        {
            _objectiveMasterUid = Spawn("ObjectiveMasterBaseDistress", new EntityCoordinates(bestPlanetGrid.Value, Vector2.Zero));
            _logs.Warning($"[OBJ-CTRL] SetupPostGameMapLoad: no master found for preset '{presetId}', spawned fallback 'ObjectiveMasterBaseDistress'");
        }

        Timer.Spawn(0, Main);
    }


    private CMUObjectiveMasterComponent? GetOrReselectObjMaster() // Stub
    {
        if (_objectiveMasterUid.IsValid() && TryComp(_objectiveMasterUid, out CMUObjectiveMasterComponent? master))
            return master;

        var query = EntityQueryEnumerator<CMUObjectiveMasterComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            if (xform.MapID != _planetMapId)
                continue;

            _objectiveMasterUid = uid;
            return comp;
        }

        _logs.Warning("[OBJ-CTRL] GetOrReselectObjMaster: no master found.");
        return null;
    }

    private void DirtyObjectiveMaster()
    {
        if (_objectiveMasterUid.IsValid() && TryComp(_objectiveMasterUid, out CMUObjectiveMasterComponent? master))
            Dirty(_objectiveMasterUid, master);
    }

    private void Main() // Stub
    {
        _logs.Debug("[OBJ-CTRL] Main() called, but wasn't ported.");
        // TODO: implement GetInactiveObjectives & SelectObjectives etc.
    }
}

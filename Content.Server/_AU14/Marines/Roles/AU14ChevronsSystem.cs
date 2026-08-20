using System.Linq;
using Content.Server.Players.PlayTimeTracking;
using Content.Shared.GameTicking;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;
using Content.Shared._RMC14.UniformAccessories;
using Content.Shared.Hands.EntitySystems;
using Content.Shared._AU14.Marines.Roles.Chevrons;
using Content.Server.Ghost.Roles.Components;
using Robust.Shared.Utility;
using Robust.Server.Player;
using Robust.Shared.Player;
using Content.Shared.AU14.util;
using Content.Shared._RMC14.Marines.Roles.Ranks;
using Content.Shared.Preferences;
using Content.Server.AU14.Round;
using Content.Shared.NPC.Components;

namespace Content.Server._AU14.Marines.Roles.Chevrons;

public sealed partial class ChevronSystem : EntitySystem
{
    [Dependency] private PlayTimeTrackingManager _tracking = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private SharedUniformAccessorySystem _uniformAccessory = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private IPlayerManager _playerManager = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);
    }

    private void OnPlayerAttached(PlayerAttachedEvent ev)
    {
        if (HasComp<ChevronSpawnedComponent>(ev.Entity))
            return;

        if (!TryComp<GhostRoleComponent>(ev.Entity, out var ghostRole) || ghostRole.JobProto == null)
            return;

        if (!_prototypes.TryIndex<JobPrototype>(ghostRole.JobProto, out var jobPrototype))
            return;

        if (jobPrototype.Chevrons == null || jobPrototype.Chevrons.Count == 0)
            return;

        if (!_tracking.TryGetTrackerTimes(ev.Player, out var playTimes))
        {
            Log.Warning($"Playtimes not ready for ghost role takeover by {ev.Player}");
            playTimes ??= new Dictionary<string, TimeSpan>();
        }

        // Ghost roles: resolve platoon from the mob's faction the same way
        var platoon = ResolvePlatoonFromMobFaction(ev.Entity);
        var chevronMap = ResolveChevronMapForPlatoon(jobPrototype, platoon);
        TrySpawnFirstValidChevron(chevronMap, null, playTimes, ev.Entity);
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        if (ev.JobId == null || ev.Player == null)
            return;

        if (!_prototypes.TryIndex<JobPrototype>(ev.JobId, out var jobPrototype))
            return;

        if (jobPrototype.Chevrons == null || jobPrototype.Chevrons.Count == 0)
            return;

        if (!_tracking.TryGetTrackerTimes(ev.Player, out var playTimes))
        {
            Log.Error($"Playtimes weren't ready yet for {ev.Player} on roundstart!");
            playTimes ??= new Dictionary<string, TimeSpan>();
        }

        var platoon = ResolvePlatoonFromMobFaction(ev.Mob);
        var chevronMap = ResolveChevronMapForPlatoon(jobPrototype, platoon);
        TrySpawnFirstValidChevron(chevronMap, ev.Profile, playTimes, ev.Mob);
    }

    /// <summary>
    /// Checks the mob's NpcFactionMemberComponent to determine if they are govfor or opfor,
    /// then returns the corresponding platoon from PlatoonSpawnRuleSystem.
    /// </summary>
    private PlatoonPrototype? ResolvePlatoonFromMobFaction(EntityUid mob)
    {
        if (!TryComp<NpcFactionMemberComponent>(mob, out var factionMember))
            return null;

        var platoonRule = EntitySystem.Get<PlatoonSpawnRuleSystem>();

        foreach (var faction in factionMember.Factions)
        {
            var factionId = faction.Id;
            if (factionId.Equals("govfor", StringComparison.OrdinalIgnoreCase) ||
                factionId.Contains("govfor", StringComparison.OrdinalIgnoreCase))
            {
                return platoonRule.SelectedGovforPlatoon;
            }

            if (factionId.Equals("opfor", StringComparison.OrdinalIgnoreCase) ||
                factionId.Contains("opfor", StringComparison.OrdinalIgnoreCase))
            {
                return platoonRule.SelectedOpforPlatoon;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the platoon's chevron override map for this job (walking the inheritance
    /// chain), or falls back to the job's base chevrons if no override matches.
    /// </summary>
    private Dictionary<string, ChevronDefinition>? ResolveChevronMapForPlatoon(
        JobPrototype job,
        PlatoonPrototype? platoon)
    {
        if (platoon?.ChevronOverrides != null)
        {
            foreach (var (overrideJobId, overrideChevrons) in platoon.ChevronOverrides)
            {
                if (JobInheritsFrom(job.ID, overrideJobId.Id))
                {
                    return overrideChevrons.ToDictionary(
                        kvp => kvp.Key.Id,
                        kvp => kvp.Value);
                }
            }
        }

        return job.Chevrons;
    }

    private bool JobInheritsFrom(string jobId, string ancestorId)
    {
        if (jobId == ancestorId)
            return true;

        if (!_prototypes.TryIndex<JobPrototype>(jobId, out var job) || job.Parents == null)
            return false;

        foreach (var parent in job.Parents)
        {
            if (JobInheritsFrom(parent, ancestorId))
                return true;
        }

        return false;
    }

    private void TrySpawnFirstValidChevron(
        Dictionary<string, ChevronDefinition>? chevronMap,
        HumanoidCharacterProfile? profile,
        Dictionary<string, TimeSpan> playTimes,
        EntityUid mob)
    {
        if (chevronMap == null)
            return;

        foreach (var (_, chevronDef) in chevronMap)
        {
            var failed = false;

            if (chevronDef.Requirements != null)
            {
                foreach (var req in chevronDef.Requirements)
                {
                    if (!req.Check(_entityManager, _prototypes, profile, playTimes, out FormattedMessage? _))
                    {
                        failed = true;
                        break;
                    }
                }
            }

            if (!failed)
            {
                SpawnChevron(chevronDef, mob);
                return;
            }
        }
    }

    private void SpawnChevron(ChevronDefinition chevronDef, EntityUid mob)
    {
        var coords = _entityManager.GetComponent<TransformComponent>(mob).Coordinates;
        var chevron = _entityManager.SpawnEntity(chevronDef.Entity, coords);

        if (!_uniformAccessory.TryInsertToValidSlot(chevron, mob))
            _hands.TryPickupAnyHand(mob, chevron, false);

        EnsureComp<ChevronSpawnedComponent>(mob);
    }
}
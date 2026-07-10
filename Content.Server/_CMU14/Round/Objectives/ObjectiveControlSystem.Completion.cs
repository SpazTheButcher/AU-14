using System.Linq;
using Content.Server._CMU14.RoundStatistics;
using Content.Shared._CMU14.Round.Objectives.Component;
using Content.Shared._RMC14.Vendors;
using Robust.Shared.Map;

namespace Content.Server._CMU14.Round.Objectives;

public sealed partial class ObjectiveControlSystem
{
    [Dependency] private SharedCMAutomatedVendorSystem _vendorSystem = default!;
    [Dependency] private CMURoundStatisticsSystem _roundStats = default!;
    [Dependency] private ObjectiveConsoleSystem _objConsole = default!;

    public void CompleteObjectiveForFaction(EntityUid uid, CMUObjectiveComponent objective, string completingFaction)
    {
        if (_planetMapId == MapId.Nullspace || Transform(uid).MapID != _planetMapId)
            return;

        if (objective.StatusesPerFaction.ContainsValue(CMUObjectiveComponent.ObjectiveStatus.Completed))
            return;

        var factionKey = completingFaction.ToLowerInvariant();
        MarkFactionCompleted(objective, factionKey);
        AwardAndRefresh(objective, completingFaction);

        if (objective.ObjectiveLevel == 3)
        {
            if (objective.FinalType == CMUObjectiveComponent.FinalObjectiveType.InstantWin)
                EndRound(completingFaction, objective.RoundEndMessage);
            else
                _logs.Info($"[OBJ-FINAL] Final objective '{objective.ObjectiveDescription}' completed for faction '{completingFaction}' as Boon (not ending the round).");
        }

        TryUnlockOrSpawnNextTier(uid, objective, completingFaction);

        if (!objective.Repeating)
        {
            Dirty(uid, objective);
            return;
        }

        if (objective.MaxRepeatable is { } maxRepeat && objective.TimesCompleted + 1 >= maxRepeat)
        {
            objective.TimesCompleted = maxRepeat;
            objective.Active = false;
            MarkAllFactionsCompleted(objective, factionKey);
            Dirty(uid, objective);
            _logs.Debug($"[OBJ-REPEAT] Objective '{objective.ObjectiveDescription}' reached max repeats ({maxRepeat}), marking as completed.");
            _objConsole.RefreshConsolesForFaction(completingFaction);
            return;
        }

        objective.TimesCompleted++;
        ResetObjectiveStatuses(objective);

        RaiseLocalEvent(uid, new ObjectiveResetEvent());

        objective.Active = true;
        Dirty(uid, objective);
        RaiseLocalEvent(uid, new ObjectiveActivatedEvent());
        _logs.Debug($"[OBJ-REPEAT] Restarted repeating objective '{objective.ObjectiveDescription}'...");

        if (objective.FactionNeutral)
            foreach (var faction in objective.Factions)
                _objConsole.RefreshConsolesForFaction(faction);
        else
            _objConsole.RefreshConsolesForFaction(objective.Faction);
    }

    private void MarkFactionCompleted(CMUObjectiveComponent objective, string factionKey)
    {
        if (objective.FactionNeutral)
        {
            if (!objective.StatusesPerFaction.TryGetValue(factionKey, out var status)
                    || status != CMUObjectiveComponent.ObjectiveStatus.Incomplete)
                return;

            objective.StatusesPerFaction[factionKey] = CMUObjectiveComponent.ObjectiveStatus.Completed;

            if (objective.Repeating)
                return;

            foreach (var key in objective.StatusesPerFaction.Keys.ToList())
            {
                if (key == factionKey || objective.StatusesPerFaction[key] != CMUObjectiveComponent.ObjectiveStatus.Incomplete)
                    continue;
                objective.StatusesPerFaction[key] = CMUObjectiveComponent.ObjectiveStatus.Failed;
            }
        }
        else
            objective.StatusesPerFaction[factionKey] = CMUObjectiveComponent.ObjectiveStatus.Completed;
    }

    private void MarkAllFactionsCompleted(CMUObjectiveComponent objective, string factionKey)
    {
        if (objective.FactionNeutral)
            foreach (var key in objective.StatusesPerFaction.Keys.ToList())
                objective.StatusesPerFaction[key] = CMUObjectiveComponent.ObjectiveStatus.Completed;
        else
            objective.StatusesPerFaction[factionKey] = CMUObjectiveComponent.ObjectiveStatus.Completed;
    }

    private void AwardAndRefresh(CMUObjectiveComponent objective, string completingFaction)
    {
        AwardPointsToFaction(completingFaction, objective);
        if (objective.FactionNeutral)
            foreach (var f in objective.Factions)
                _objConsole.RefreshConsolesForFaction(f);
        else
            _objConsole.RefreshConsolesForFaction(completingFaction);
    }

    public void AwardPointsToFaction(string faction, CMUObjectiveComponent objective)
        => ApplyWinPoints(faction, objective.CustomPoints == 0
            ? (objective.ObjectiveLevel == 1 ? 5 : 20)
            : objective.CustomPoints);

    public void AwardRawPointsToFaction(string faction, int points) => ApplyWinPoints(faction, points);

    private void ApplyWinPoints(string faction, int points)
    {
        if (GetOrReselectObjMaster() is not { } master)
            return;

        var key = faction.ToLowerInvariant();
        var data = master.GetOrCreateFactionData(key);
        data.CurrentWinPoints += points;
        DirtyObjectiveMaster();
        _vendorSystem.UpdateVendorFactionPointsCache(key, data.CurrentWinPoints);

        if (!master.FactionsGivenFinalObjective.Contains(key) && data.CurrentWinPoints >= data.RequiredWinPoints)
            TryActivateFinalObjective(key);
    }

    private void TryActivateFinalObjective(string factionKey)
    {
        _logs.Warning($"[OBJ-FINAL] TryActivateFinalObjective not ported, final objective for '{factionKey}' not activated.");
    }

    // IsWinActive()

    private void TryUnlockOrSpawnNextTier(EntityUid completedUid, CMUObjectiveComponent completedObjective, string completingFaction)
    {
        var nextTier = completedObjective.NextTierObjective;
        if (!nextTier.HasValue)
            return;

        var protoIdStr = nextTier.Value.Id;
        if (string.IsNullOrEmpty(protoIdStr))
            return;

        if (!TryComp(completedUid, out TransformComponent? completedXform))
            return;

        if (!nextTier.Value.TryGet(out CMUObjectiveComponent? _, _proto, EntityManager.ComponentFactory))
        {
            _logs.Warning($"[OBJ-TIER] Next tier prototype '{protoIdStr}' does not contain a CMUObjectiveComponent or is missing!");
            return;
        }

        var newEnt = Spawn(protoIdStr, completedXform.Coordinates);
        if (TryComp(newEnt, out CMUObjectiveComponent? newObjComp))
        {
            newObjComp.StatusesPerFaction.Clear();
            newObjComp.Faction = newObjComp.FactionNeutral ? string.Empty : completingFaction.ToLowerInvariant();
            newObjComp.Active = true;
            InitializeObjectiveStatuses(newObjComp);
            Dirty(newEnt, newObjComp);
            RaiseLocalEvent(newEnt, new ObjectiveActivatedEvent());

            if (newObjComp.FactionNeutral)
                foreach (var f in newObjComp.Factions)
                    _objConsole.RefreshConsolesForFaction(f);
            else
                _objConsole.RefreshConsolesForFaction(newObjComp.Faction);
        }
        else
            _logs.Warning($"[OBJ-TIER] Spawned prototype {protoIdStr} but it does not contain a CMUObjectiveComponent!");
    }

    private void EndRound(string faction, string? roundEndMessage)
    {
        var message = roundEndMessage ?? string.Empty;
        var roundEndText = Loc.GetString("objectives-system-round-end",
            ("faction", faction.ToUpperInvariant()),
            ("message", message));

        _roundStats.RecordObjectiveVictory(faction);
        _gameTicker.EndRound(roundEndText);
    }

    // GetWinPoints()

    public void InitializeObjectiveStatuses(CMUObjectiveComponent obj)
    {
        if (obj.FactionNeutral)
            foreach (var faction in obj.Factions)
                obj.StatusesPerFaction.TryAdd(faction.ToLowerInvariant(), CMUObjectiveComponent.ObjectiveStatus.Incomplete);
        else if (!string.IsNullOrEmpty(obj.Faction))
            obj.StatusesPerFaction.TryAdd(obj.Faction.ToLowerInvariant(), CMUObjectiveComponent.ObjectiveStatus.Incomplete);
    }

    private void ResetObjectiveStatuses(CMUObjectiveComponent objective)
    {
        foreach (var key in objective.StatusesPerFaction.Keys.ToList())
            objective.StatusesPerFaction[key] = CMUObjectiveComponent.ObjectiveStatus.Incomplete;
    }
}

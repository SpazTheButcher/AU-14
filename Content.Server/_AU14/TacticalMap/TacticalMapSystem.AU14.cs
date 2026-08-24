using Content.Shared._CMU14.TacticalMap;
using Content.Shared._RMC14.TacticalMap;
using Content.Shared.GameTicking;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server._RMC14.TacticalMap;

public sealed partial class TacticalMapSystem // CMU14 Class
{
    [Dependency] private IPrototypeManager _weyuProtoMan = default!;
    private int _nextIntelBlipKey = -1;

    private static readonly Dictionary<string, SpriteSpecifier.Rsi> FactionSignalIcon = new()
    {
        ["govfor"] = new SpriteSpecifier.Rsi(
            new ResPath("/Textures/_CMU14/Interface/cmugovforblips.rsi"),
            "rifleman"),

        ["opfor"] = new SpriteSpecifier.Rsi(
            new ResPath("/Textures/_CMU14/Interface/cmuopforblips.rsi"),
            "rifleman"),

        ["clf"] = new SpriteSpecifier.Rsi(
            new ResPath("/Textures/_CMU14/Interface/cmucolonyblips.rsi"),
            "colonist")
    };

    public (EntityUid GridId, int Key)? CreateFactionIntelBlip(
        EntityUid source,
        string sourceFactionLower,
        string viewerFactionUpper)
    {
        if (!_transformQuery.TryComp(source, out var xform) ||
            xform.GridUid is not { } gridId ||
            !_mapGridQuery.TryComp(gridId, out var gridComp) ||
            !_tacticalMapQuery.TryComp(gridId, out var tacticalMap) ||
            !_transform.TryGetGridTilePosition((source, xform), out var indices, gridComp))
        {
            return null;
        }

        FactionSignalIcon.TryGetValue(sourceFactionLower, out var icon);

        var blip = new TacticalMapBlip(
            indices,
            icon,
            Color.FromHex("#FF6B6B"),
            TacticalMapBlipStatus.Alive,
            null,
            false);

        var key = _nextIntelBlipKey--;

        if (!TryGetBlipDicts(tacticalMap, viewerFactionUpper, out var live, out _))
            return null;

        // live dict only: DF fixes are realtime SIGINT for the ops consoles (tacmap
        // computers and overwatch poll the live blips), not part of the snapshot map
        // updates handed to every rifleman. a manual update pulled while the fix is
        // up still captures it, which is fine - that update reflects current intel
        live[key] = blip;

        tacticalMap.MapDirty = true;
        return (gridId, key);
    }

    public void EraseFactionIntelBlip(EntityUid gridId, int key, string viewerFactionUpper)
    {
        if (!TryComp(gridId, out TacticalMapComponent? tacticalMap))
            return;

        if (!TryGetBlipDicts(tacticalMap, viewerFactionUpper, out var live, out var snapshot))
            return;

        var removed = live.Remove(key);
        removed |= snapshot.Remove(key);

        if (removed)
            tacticalMap.MapDirty = true;
    }

    private static bool TryGetBlipDicts(
        TacticalMapComponent map,
        string factionUpper,
        out Dictionary<int, TacticalMapBlip> live,
        out Dictionary<int, TacticalMapBlip> snapshot)
    {
        switch (factionUpper)
        {
            case "OPFOR":
                live = map.OpforBlips;
                snapshot = map.LastUpdateOpforBlips;
                return true;

            case "GOVFOR":
                live = map.GovforBlips;
                snapshot = map.LastUpdateGovforBlips;
                return true;

            case "CLF":
                live = map.ClfBlips;
                snapshot = map.LastUpdateClfBlips;
                return true;

            case "WEYU":
                live = map.WeYuBlips;
                snapshot = map.LastUpdateWeYuBlips;
                return true;

            default:
                live = null!;
                snapshot = null!;
                return false;
        }
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        if (ev.JobId == null || !_weyuProtoMan.TryIndex<JobPrototype>(ev.JobId, out var job))
            return;

        if (job.RoundForce is not "WY")
            return;

        EnsureComp<WeYuMapTrackedComponent>(ev.Mob);

        if (TryComp<TacticalMapUserComponent>(ev.Mob, out var user))
        {
            SyncUserFactionFlags((ev.Mob, user));
            SyncTrackedFaction(ev.Mob);
        }
    }
}

using Content.Server.Popups;
using Content.Shared._CMU14.Round.Objectives.Type;
using Content.Shared.Stacks;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.Utility;

namespace Content.Server._CMU14.Round.Objectives.Type;

/// <summary>
/// Handles the Analyzer Machine.
/// - All factions: "Scan" context‑menu action that credits nearby items to active fetch objectives.
/// - CLF only: cash can be inserted; every 15 credits awards 1 win point directly to CLF.
/// </summary>
public sealed class ObjFetchAnalyzerSystem : EntitySystem
{
    [Dependency] private ObjFetchSystem _fetchSystem = default!;
    [Dependency] private ObjectiveControlSystem _objCtrl = default!;
    [Dependency] private PopupSystem _popupSystem = default!;

    private const string ClfFaction = "clf";
    private const int CashPerPoint = 15;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<FetchAnalyzerComponent, GetVerbsEvent<InteractionVerb>>(OnGetVerbs);
        SubscribeLocalEvent<FetchAnalyzerComponent, EntInsertedIntoContainerMessage>(OnCashInserted);
    }

    private void OnGetVerbs(EntityUid uid, FetchAnalyzerComponent component, GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;

        var verb = new InteractionVerb
        {
            Act = () => PerformScan(uid, args.User),
            Text = "Scan",
            Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/examine.svg.192dpi.png"))
        };
        args.Verbs.Add(verb);
    }

    private void PerformScan(EntityUid analyzerUid, EntityUid user)
    {
        var count = _fetchSystem.ScanForFetchItems(analyzerUid);
        var message = count > 0
            ? $"Analyzer detected {count} item(s) of interest in the vicinity."
            : "No items of interest detected nearby.";
        _popupSystem.PopupEntity(message, analyzerUid, user);
    }

    private void OnCashInserted(EntityUid uid, FetchAnalyzerComponent component, EntInsertedIntoContainerMessage args)
    {
        if (component.Faction.ToLowerInvariant() != ClfFaction)
            return;

        int credits = 1;
        if (TryComp(args.Entity, out StackComponent? stack))
            credits = stack.Count;

        component.CashStored += credits;
        int points = component.CashStored / CashPerPoint;
        component.CashStored -= points * CashPerPoint;

        QueueDel(args.Entity);

        if (points > 0)
            _objCtrl.AwardRawPointsToFaction(ClfFaction, points);

        var banked = component.CashStored > 0
            ? $" ({component.CashStored}/{CashPerPoint} cr. banked)"
            : string.Empty;

        var msg = points > 0
            ? $"Analyzer credited {points} point(s) to CLF.{banked}"
            : $"Analyzer banked {credits} cr. ({component.CashStored}/{CashPerPoint} cr. until next point).";

        _popupSystem.PopupEntity(msg, uid);
    }
}

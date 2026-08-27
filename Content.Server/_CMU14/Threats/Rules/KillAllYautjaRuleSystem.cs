using Content.Server._CMU14.RoundStatistics;
using Content.Server.AU14.Round;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules;
using Content.Shared._CMU14.Threats;
using Content.Shared._CMU14.Yautja;
using Content.Shared._RMC14.Evacuation;
using Content.Shared.GameTicking.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.Prototypes;
using KillAllYautjaRuleComponent = Content.Shared._CMU14.Threats.Rules.KillAllYautjaRuleComponent;

namespace Content.Server._CMU14.Threats.Rules;

public sealed partial class KillAllYautjaRuleSystem : GameRuleSystem<KillAllYautjaRuleComponent>
{
    [Dependency] private AuRoundSystem _auRoundSystem = default!;
    [Dependency] private IEntityManager _entMan = default!;
    [Dependency] private GameTicker _gameTicker = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private CMURoundStatisticsSystem _roundStats = default!;
    [Dependency] private ThreatRuleHelper _threatRuleHelper = default!;
    private const string DefaultWinMsg = "The Bad Blood Clan has been eliminated.";

    private static readonly HashSet<string> OwnRules =
        new(StringComparer.OrdinalIgnoreCase) { "KillAllYautjaRule", "KillAllGovforRule" };

    private HashSet<string>? _threatRules;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<EvacuationLaunchedEvent>(OnEvacuationLaunched);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs ev)
        => _threatRules = null;

    private void OnEvacuationLaunched(ref EvacuationLaunchedEvent ev)
    {
        if (_gameTicker.IsGameRuleActive<KillAllYautjaRuleComponent>())
            CheckVictoryCondition();
    }

    private void OnMobStateChanged(MobStateChangedEvent ev)
    {
        if (!_gameTicker.IsGameRuleActive<KillAllYautjaRuleComponent>())
            return;
        if (ev.NewMobState != MobState.Dead)
            return;

        CheckVictoryCondition();
    }

    private void CheckVictoryCondition()
    {
        EntityQueryEnumerator<ActiveGameRuleComponent, KillAllYautjaRuleComponent, GameRuleComponent> queryRule
            = QueryActiveRules();
        if (!ThreatRuleHelper.TryGetActiveRule(ref queryRule, out KillAllYautjaRuleComponent ruleComp, out _))
            return;

        int requiredPercent = Math.Clamp(ruleComp.Percent, 1, 100);
        int eliminated = 0, total = 0;

        EntityQueryEnumerator<MobStateComponent, YautjaComponent> query = _entMan
            .EntityQueryEnumerator<MobStateComponent, YautjaComponent>();
        while (query.MoveNext(out EntityUid uid, out MobStateComponent? mobState, out _))
        {
            total++;
            if (mobState.CurrentState == MobState.Dead || _threatRuleHelper.IsEvacuated(uid))
                eliminated++;
        }

        if (total == 0)
            return;
        if (_gameTicker.RunLevel != GameRunLevel.InRound)
            return;
        if (!ThreatRuleHelper.MeetsRequiredPercent(eliminated, total, requiredPercent))
            return;
        if (AnyOtherThreatRuleActive())
            return;

        string? winMessage = _auRoundSystem.SelectedThreat?.WinMessage;
        _roundStats.RecordThreatDefeatedRule("KillAllYautjaRule");
        _gameTicker.EndRound(!string.IsNullOrEmpty(winMessage) ? winMessage : DefaultWinMsg);
    }

    private bool AnyOtherThreatRuleActive()
    {
        var threatRules = GetThreatRules();

        var rules = _entMan.EntityQueryEnumerator<ActiveGameRuleComponent, GameRuleComponent>();
        while (rules.MoveNext(out var uid, out _, out _))
        {
            if (Prototype(uid) is { } proto && threatRules.Contains(proto.ID))
                return true;
        }

        return false;
    }

    private HashSet<string> GetThreatRules()
    {
        if (_threatRules != null)
            return _threatRules;

        var rules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var threat in _prototypes.EnumeratePrototypes<ThreatPrototype>())
        {
            rules.UnionWith(threat.WinConditions);
            rules.UnionWith(threat.AddGameRules);
        }

        rules.ExceptWith(OwnRules);
        return _threatRules = rules;
    }
}

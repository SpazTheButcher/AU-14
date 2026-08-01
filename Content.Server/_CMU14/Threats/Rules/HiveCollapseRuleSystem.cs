using Content.Server._CMU14.RoundStatistics;
using Content.Server._RMC14.Xenonids.Hive;
using Content.Server.AU14.Round;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules;
using Content.Shared._RMC14.Xenonids.Hive;
using Content.Shared.GameTicking.Components;
using Robust.Shared.Timing;
using HiveCollapseRuleComponent = Content.Shared._CMU14.Threats.Rules.HiveCollapseRuleComponent;

namespace Content.Server._CMU14.Threats.Rules;

public sealed partial class HiveCollapseRuleSystem : GameRuleSystem<HiveCollapseRuleComponent>
{
    [Dependency] private GameTicker _gameTicker = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private AuRoundSystem _auRoundSystem = default!;
    [Dependency] private CMURoundStatisticsSystem _roundStats = default!;

    private const string DefaultWinMsg = "The hive has collapsed!";

    private TimeSpan? _hiveCollapseTime;
    

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<XenoHiveQueenChangedEvent>(OnQueenChanged);
    }

    private void OnQueenChanged(XenoHiveQueenChangedEvent ev)
    {
        if (!_gameTicker.IsGameRuleActive<HiveCollapseRuleComponent>())
        {
            return;
        }
        EntityQueryEnumerator<ActiveGameRuleComponent, HiveCollapseRuleComponent, GameRuleComponent> queryRule
            = QueryActiveRules();
        if (!ThreatRuleHelper.TryGetActiveRule(ref queryRule, out HiveCollapseRuleComponent ruleComp, out _))
            return;

        if (ev.NewQueen == null)
        {
            _hiveCollapseTime = _timing.CurTime + ruleComp.HiveCollapseDuration;
        }
        else
        {
            _hiveCollapseTime = null;
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_gameTicker.IsGameRuleActive<HiveCollapseRuleComponent>() ||
            _hiveCollapseTime == null)
            return;

        
        if (_timing.CurTime > _hiveCollapseTime)
        {
            string? winMessage = _auRoundSystem.SelectedThreat?.WinMessage;
            _roundStats.RecordThreatDefeatedRule("HiveCollapseRule");
            _gameTicker.EndRound(string.IsNullOrEmpty(winMessage) ? DefaultWinMsg : winMessage);
        }
    }
}
using Content.Shared._RMC14.Armor;
using Content.Shared._RMC14.Atmos;
using Content.Shared._RMC14.Projectiles.Penetration;
using Content.Shared._RMC14.Xenonids.Evolution;
using Content.Shared._RMC14.Xenonids.Pheromones;
using Content.Shared._RMC14.Xenonids.Rest;
using Content.Shared._RMC14.Xenonids.Weeds;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs.Systems;
using Content.Shared.Standing;
using Robust.Shared.Network;
using Robust.Shared.Timing;

namespace Content.Shared._RMC14.Xenonids.Sunder;

public sealed partial class XenoSunderSystem : EntitySystem
{
    private static readonly FixedPoint2 BaseRecoverMultiplier = 0.5;
    private static readonly FixedPoint2 RestingMultiplier = 2;
    private static readonly FixedPoint2 WeedsMultiplier = 2;
    private static readonly FixedPoint2 RecoveryPheromoneMultiplier = 0.1;

    [Dependency] private CMArmorSystem _armor = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private SharedRMCFlammableSystem _rmcFlammable = default!;
    [Dependency] private StandingStateSystem _standing = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedXenoWeedsSystem _weeds = default!;

    private EntityQuery<AffectableByWeedsComponent> _affectableQuery;
    private EntityQuery<XenoRecoveryPheromonesComponent> _xenoRecoveryQuery;
    private EntityQuery<XenoRegenComponent> _xenoRegenQuery;

    private readonly HashSet<EntityUid> _activeSundered = new();
    private readonly List<EntityUid> _activeSunderedBuffer = new();

    public override void Initialize()
    {
        base.Initialize();

        _affectableQuery = GetEntityQuery<AffectableByWeedsComponent>();
        _xenoRecoveryQuery = GetEntityQuery<XenoRecoveryPheromonesComponent>();
        _xenoRegenQuery = GetEntityQuery<XenoRegenComponent>();

        SubscribeLocalEvent<XenoSunderComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<XenoSunderComponent, NewXenoEvolvedEvent>(OnNewXenoEvolved);
        SubscribeLocalEvent<XenoSunderComponent, XenoDevolvedEvent>(OnXenoDevolved);
        SubscribeLocalEvent<XenoSunderComponent, ComponentShutdown>(OnSunderShutdown);
        SubscribeLocalEvent<XenoSunderingComponent, AfterProjectileHitEvent>(OnProjectileHit);

        UpdatesAfter.Add(typeof(SharedXenoPheromonesSystem));
    }

    private void OnMapInit(Entity<XenoSunderComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.NextRegenTime = _timing.CurTime + ent.Comp.RegenCooldown;
        DirtyField(ent, ent.Comp, nameof(XenoSunderComponent.NextRegenTime));
        UpdateActiveSundered(ent);
    }

    private void OnNewXenoEvolved(Entity<XenoSunderComponent> newXeno, ref NewXenoEvolvedEvent args)
    {
        TransferSunder(args.OldXeno, newXeno);
    }

    private void OnXenoDevolved(Entity<XenoSunderComponent> newXeno, ref XenoDevolvedEvent args)
    {
        TransferSunder(args.OldXeno, newXeno);
    }

    private void OnSunderShutdown(Entity<XenoSunderComponent> ent, ref ComponentShutdown args)
    {
        _activeSundered.Remove(ent.Owner);
    }

    private void OnProjectileHit(Entity<XenoSunderingComponent> ent, ref AfterProjectileHitEvent args)
    {
        if (_net.IsClient ||
            ent.Comp.Amount <= FixedPoint2.Zero)
        {
            return;
        }

        AdjustSunder(args.Target, ent.Comp.Amount);
    }

    public override void Update(float frameTime)
    {
        if (_net.IsClient ||
            _activeSundered.Count == 0)
        {
            return;
        }

        var time = _timing.CurTime;
        _activeSunderedBuffer.Clear();
        _activeSunderedBuffer.AddRange(_activeSundered);

        foreach (var uid in _activeSunderedBuffer)
        {
            if (!TryComp<XenoSunderComponent>(uid, out var sunder))
            {
                _activeSundered.Remove(uid);
                continue;
            }

            if (sunder.Amount <= FixedPoint2.Zero ||
                time < sunder.NextRegenTime)
            {
                continue;
            }

            sunder.NextRegenTime = time + sunder.RegenCooldown;
            DirtyField(uid, sunder, nameof(XenoSunderComponent.NextRegenTime));

            if (_mobState.IsDead(uid) ||
                _rmcFlammable.IsOnFire(uid) ||
                !CanRegenerateSunder(uid, out var onFriendlyWeeds))
            {
                continue;
            }

            var recovery = GetPassiveRecovery(uid, sunder, onFriendlyWeeds);
            if (recovery > FixedPoint2.Zero)
                AdjustSunder((uid, sunder), -recovery);
        }

        _activeSunderedBuffer.Clear();
    }

    public bool HasSunder(EntityUid uid)
    {
        return TryComp<XenoSunderComponent>(uid, out var sunder) &&
               sunder.Amount > FixedPoint2.Zero;
    }

    public FixedPoint2 HealSunder(Entity<XenoSunderComponent?> ent, FixedPoint2 amount)
    {
        if (amount <= FixedPoint2.Zero)
            return FixedPoint2.Zero;

        return AdjustSunder(ent, -amount);
    }

    public FixedPoint2 AdjustSunder(Entity<XenoSunderComponent?> ent, FixedPoint2 adjustment)
    {
        if (!Resolve(ent, ref ent.Comp, false) ||
            ent.Comp.Max <= FixedPoint2.Zero ||
            adjustment == FixedPoint2.Zero)
        {
            return FixedPoint2.Zero;
        }

        if (adjustment > FixedPoint2.Zero)
            adjustment *= ent.Comp.IncomingMultiplier;

        var old = ent.Comp.Amount;
        ent.Comp.Amount = FixedPoint2.Min(ent.Comp.Max, FixedPoint2.Max(FixedPoint2.Zero, old + adjustment));

        var delta = ent.Comp.Amount - old;
        if (delta == FixedPoint2.Zero)
            return FixedPoint2.Zero;

        Dirty(ent);
        UpdateActiveSundered((ent.Owner, ent.Comp));
        _armor.UpdateArmorValue(ent.Owner);
        return delta;
    }

    private void TransferSunder(EntityUid oldXeno, Entity<XenoSunderComponent> newXeno)
    {
        if (!TryComp<XenoSunderComponent>(oldXeno, out var oldSunder) ||
            oldSunder.Amount <= FixedPoint2.Zero)
        {
            return;
        }

        newXeno.Comp.Amount = FixedPoint2.Min(oldSunder.Amount, newXeno.Comp.Max);
        Dirty(newXeno);
        UpdateActiveSundered(newXeno);
        _armor.UpdateArmorValue(newXeno.Owner);
    }

    private void UpdateActiveSundered(Entity<XenoSunderComponent> ent)
    {
        if (ent.Comp.Amount > FixedPoint2.Zero)
            _activeSundered.Add(ent.Owner);
        else
            _activeSundered.Remove(ent.Owner);
    }

    private bool CanRegenerateSunder(EntityUid uid, out bool onFriendlyWeeds)
    {
        onFriendlyWeeds = IsOnFriendlyWeeds(uid);

        if (!_xenoRegenQuery.TryComp(uid, out var regen))
            return false;

        return regen.HealOffWeeds || onFriendlyWeeds;
    }

    private bool IsOnFriendlyWeeds(EntityUid uid)
    {
        if (Transform(uid).Anchored)
            _weeds.UpdateQueued(uid);

        return _affectableQuery.CompOrNull(uid) is { OnXenoWeeds: true, OnFriendlyWeeds: true };
    }

    private FixedPoint2 GetPassiveRecovery(EntityUid uid, XenoSunderComponent sunder, bool onFriendlyWeeds)
    {
        var recovery = sunder.Recover * BaseRecoverMultiplier * FixedPoint2.New(sunder.RegenCooldown.TotalSeconds);

        if (_standing.IsDown(uid) ||
            HasComp<XenoRestingComponent>(uid))
        {
            recovery *= RestingMultiplier;
        }

        if (onFriendlyWeeds)
            recovery *= WeedsMultiplier;

        var pheromones = _xenoRecoveryQuery.CompOrNull(uid)?.Multiplier ?? FixedPoint2.Zero;
        if (pheromones > FixedPoint2.Zero)
            recovery *= 1 + pheromones * RecoveryPheromoneMultiplier;

        return recovery;
    }
}

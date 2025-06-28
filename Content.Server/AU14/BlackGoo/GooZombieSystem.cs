using System.Linq;
using Content.Server.Actions;
using Content.Server.Body.Systems;
using Content.Server.Chat;
using Content.Server.Chat.Systems;
using Content.Server.Emoting.Systems;
using Content.Server.Speech.EntitySystems;
using Content.Server.Roles;using Content.Shared.Anomaly.Components;
using Content.Shared.Bed.Sleep;
using Content.Shared.Cloning.Events;
using Content.Shared.Damage;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.Roles;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.AU14.BlackGoo;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.AU14.BlackGoo
{
    public sealed partial class GooZombieSystem : SharedGooZombieSystem
    {
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly IPrototypeManager _protoManager = default!;
        [Dependency] private readonly IRobustRandom _random = default!;
        [Dependency] private readonly BloodstreamSystem _bloodstream = default!;
        [Dependency] private readonly DamageableSystem _damageable = default!;
        [Dependency] private readonly ChatSystem _chat = default!;
        [Dependency] private readonly ActionsSystem _actions = default!;
        [Dependency] private readonly AutoEmoteSystem _autoEmote = default!;
        [Dependency] private readonly EmoteOnDamageSystem _emoteOnDamage = default!;
        [Dependency] private readonly MobStateSystem _mobState = default!;
        [Dependency] private readonly SharedPopupSystem _popup = default!;
        [Dependency] private readonly SharedRoleSystem _role = default!;

        public const SlotFlags ProtectiveSlots =
            SlotFlags.FEET |
            SlotFlags.HEAD |
            SlotFlags.EYES |
            SlotFlags.GLOVES |
            SlotFlags.MASK |
            SlotFlags.NECK |
            SlotFlags.INNERCLOTHING |
            SlotFlags.OUTERCLOTHING;

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<GooZombieComponent, ComponentStartup>(OnStartup);
            SubscribeLocalEvent<GooZombieComponent, EmoteEvent>(OnEmote,
                before:
                new[] { typeof(VocalSystem), typeof(BodyEmotesSystem) });

            SubscribeLocalEvent<GooZombieComponent, MeleeHitEvent>(OnMeleeHit);
            SubscribeLocalEvent<GooZombieComponent, MobStateChangedEvent>(OnMobState);
            SubscribeLocalEvent<GooZombieComponent, CloningEvent>(OnGooZombieCloning);
            SubscribeLocalEvent<GooZombieComponent, TryingToSleepEvent>(OnSleepAttempt);
            SubscribeLocalEvent<GooZombieComponent, GetCharactedDeadIcEvent>(OnGetCharacterDeadIC);
            SubscribeLocalEvent<PendingGooZombieComponent, MapInitEvent>(OnPendingMapInit);
            SubscribeLocalEvent<PendingGooZombieComponent, BeforeRemoveAnomalyOnDeathEvent>(
                OnBeforeRemoveAnomalyOnDeath);

            SubscribeLocalEvent<IncurableGooZombieComponent, MapInitEvent>(OnPendingMapInit);

            SubscribeLocalEvent<GooifyOnDeathComponent, MobStateChangedEvent>(OnDamageChanged);

        }

        private void OnBeforeRemoveAnomalyOnDeath(Entity<PendingGooZombieComponent> ent,
            ref BeforeRemoveAnomalyOnDeathEvent args)
        {
            // Pending zombies (e.g. infected non-zombies) do not remove their hosted anomaly on death.
            // Current zombies DO remove the anomaly on death.
            args.Cancelled = true;
        }

        private void OnPendingMapInit(EntityUid uid, IncurableGooZombieComponent component, MapInitEvent args)
        {
            _actions.AddAction(uid, ref component.Action, component.ZombifySelfActionPrototype);
        }

        private void OnPendingMapInit(EntityUid uid, PendingGooZombieComponent component, MapInitEvent args)
        {
            if (_mobState.IsDead(uid))
            {
                GooifyEntity(uid);
                return;
            }

            component.NextTick = _timing.CurTime + TimeSpan.FromSeconds(1f);
            component.GracePeriod = _random.Next(component.MinInitialInfectedGrace, component.MaxInitialInfectedGrace);
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);
            var curTime = _timing.CurTime;

            // Hurt the living infected
            var query = EntityQueryEnumerator<PendingGooZombieComponent, DamageableComponent, MobStateComponent>();
            while (query.MoveNext(out var uid, out var comp, out var damage, out var mobState))
            {
                // Process only once per second
                if (comp.NextTick > curTime)
                    continue;

                comp.NextTick = curTime + TimeSpan.FromSeconds(1f);

                comp.GracePeriod -= TimeSpan.FromSeconds(1f);
                if (comp.GracePeriod > TimeSpan.Zero)
                    continue;

                if (_random.Prob(comp.InfectionWarningChance))
                    _popup.PopupEntity(Loc.GetString(_random.Pick(comp.InfectionWarnings)), uid, uid);

                var multiplier = _mobState.IsCritical(uid, mobState)
                    ? comp.CritDamageMultiplier
                    : 1f;

                _damageable.TryChangeDamage(uid, comp.Damage * multiplier, true, false, damage);
            }

            // Heal the zombified
            var zombQuery = EntityQueryEnumerator<GooZombieComponent, DamageableComponent, MobStateComponent>();
            while (zombQuery.MoveNext(out var uid, out var comp, out var damage, out var mobState))
            {
                // Process only once per second
                if (comp.NextTick + TimeSpan.FromSeconds(1) > curTime)
                    continue;

                comp.NextTick = curTime;

                if (_mobState.IsDead(uid, mobState))
                    continue;

                var multiplier = _mobState.IsCritical(uid, mobState)
                    ? comp.PassiveHealingCritMultiplier
                    : 1f;

                // Gradual healing for living zombies.
                _damageable.TryChangeDamage(uid, comp.PassiveHealing * multiplier, true, false, damage);
            }
        }

        private void OnSleepAttempt(EntityUid uid, GooZombieComponent component, ref TryingToSleepEvent args)
        {
            args.Cancelled = true;
        }

        private void OnGetCharacterDeadIC(EntityUid uid, GooZombieComponent component, ref GetCharactedDeadIcEvent args)
        {
            args.Dead = true;
        }

        private void OnStartup(EntityUid uid, GooZombieComponent component, ComponentStartup args)
        {
            if (component.EmoteSoundsId == null)
                return;
            _protoManager.TryIndex(component.EmoteSoundsId, out component.EmoteSounds);
        }

        private void OnEmote(EntityUid uid, GooZombieComponent component, ref EmoteEvent args)
        {
            // always play zombie emote sounds and ignore others
            if (args.Handled)
                return;
            args.Handled = _chat.TryPlayEmoteSound(uid, component.EmoteSounds, args.Emote);
        }

        private void OnMobState(EntityUid uid, GooZombieComponent component, MobStateChangedEvent args)
        {
            if (args.NewMobState == MobState.Alive)
            {
                // Groaning when damaged
                EnsureComp<EmoteOnDamageComponent>(uid);
                _emoteOnDamage.AddEmote(uid, "Scream");

                // Random groaning
                EnsureComp<AutoEmoteComponent>(uid);
                _autoEmote.AddEmote(uid, "ZombieGroan");
            }
            else
            {
                // Stop groaning when damaged
                _emoteOnDamage.RemoveEmote(uid, "Scream");

                // Stop random groaning
                _autoEmote.RemoveEmote(uid, "ZombieGroan");
            }
        }

        private float GetGooZombieInfectionChance(EntityUid uid, GooZombieComponent component)
        {
            var max = component.MaxZombieInfectionChance;

            if (!_inventory.TryGetContainerSlotEnumerator(uid, out var enumerator, ProtectiveSlots))
                return max;

            var items = 0f;
            var total = 0f;
            while (enumerator.MoveNext(out var con))
            {
                total++;
                if (con.ContainedEntity != null)
                    items++;
            }

            if (total == 0)
                return max;

            // Everyone knows that when it comes to zombies, socks & sandals provide just as much protection as an
            // armored vest. Maybe these should be weighted per-item. I.e. some kind of coverage/protection component.
            // Or at the very least different weights per slot.

            var min = component.MinZombieInfectionChance;
            //gets a value between the max and min based on how many items the entity is wearing
            var chance = (max - min) * ((total - items) / total) + min;
            return chance;
        }

        private void OnMeleeHit(EntityUid uid, GooZombieComponent component, MeleeHitEvent args)
        {
            if (!TryComp<GooZombieComponent>(args.User, out _))
                return;

            if (!args.HitEntities.Any())
                return;

            foreach (var entity in args.HitEntities)
            {
                if (args.User == entity)
                    continue;

                if (!TryComp<MobStateComponent>(entity, out var mobState))
                    continue;

                if (HasComp<GooZombieComponent>(entity))
                {
                    args.BonusDamage = -args.BaseDamage;
                }
                else
                {
                    if (!HasComp<GooZombieImmuneComponent>(entity)  &&
                        _random.Prob(GetGooZombieInfectionChance(entity, component)))
                    {
                        EnsureComp<PendingGooZombieComponent>(entity);
                        EnsureComp<GooifyOnDeathComponent>(entity);
                    }
                }

                if (_mobState.IsIncapacitated(entity, mobState) && !HasComp<GooZombieComponent>(entity) &&
                    !HasComp<GooZombieImmuneComponent>(entity))
                {
                    GooifyEntity(entity);
                    args.BonusDamage = -args.BaseDamage;
                }
                else if (mobState.CurrentState == MobState.Alive) //heals when zombies bite live entities
                {
                    _damageable.TryChangeDamage(uid, component.HealingOnBite, true, false);
                }
            }
        }

        /// <summary>
        ///     This is the function to call if you want to unzombify an entity.
        /// </summary>
        /// <param name="source">the entity having the GooZombieComponent</param>
        /// <param name="target">the entity you want to unzombify (different from source in case of cloning, for example)</param>
        /// <param name="zombiecomp"></param>

        ///     this currently only restore the skin/eye color from before zombified

        public bool UnZombify(EntityUid source, EntityUid target, GooZombieComponent? zombiecomp)
        {
            if (!Resolve(source, ref zombiecomp))
                return false;

            foreach (var (layer, info) in zombiecomp.BeforeZombifiedCustomBaseLayers)
            {
                _humanoidAppearance.SetBaseLayerColor(target, layer, info.Color);
                _humanoidAppearance.SetBaseLayerId(target, layer, info.Id);
            }

            if (TryComp<HumanoidAppearanceComponent>(target, out var appcomp))
            {
                appcomp.EyeColor = zombiecomp.BeforeZombifiedEyeColor;
            }

            _humanoidAppearance.SetSkinColor(target, zombiecomp.BeforeZombifiedSkinColor, false);
            _bloodstream.ChangeBloodReagent(target, zombiecomp.BeforeZombifiedBloodReagent);

            return true;
        }

        private void OnGooZombieCloning(Entity<GooZombieComponent> ent, ref CloningEvent args)
        {
            UnZombify(ent.Owner, args.CloneUid, ent.Comp);

        }
    }
}

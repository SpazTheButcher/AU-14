using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.Chat;
using Content.Server.Chat.Managers;
using Content.Server.Ghost.Roles.Components;
using Content.Server.Humanoid;
using Content.Server.IdentityManagement;
using Content.Server.Inventory;
using Content.Server.Mind;
using Content.Server.Mind.Commands;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server.Speech.Components;
using Content.Server.Temperature.Components;
using Content.Shared.CombatMode;
using Content.Shared.CombatMode.Pacification;
using Content.Shared.Damage;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Interaction.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.NameModifier.EntitySystems;
using Content.Shared.NPC.Systems;
using Content.Shared.Nutrition.AnimalHusbandry;
using Content.Shared.Nutrition.Components;
using Content.Shared.Popups;
using Content.Shared.Weapons.Melee;
using Content.Shared.AU14.BlackGoo;
using Content.Shared.Prying.Components;
using Content.Shared.Traits.Assorted;
using Robust.Shared.Audio.Systems;
using Content.Shared.Ghost.Roles.Components;
using Content.Shared.Tag;
using Robust.Shared.Prototypes;
using Robust.Shared.Toolshed.Commands.Math;

namespace Content.Server.AU14.BlackGoo;

/// <summary>
///     Handles zombie propagation and inherent zombie traits
/// </summary>
/// <remarks>
///     Don't Shitcode Open Inside
/// </remarks>
public sealed partial class GooZombieSystem
{
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly IChatManager _chatMan = default!;
    [Dependency] private readonly SharedCombatModeSystem _combat = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly HumanoidAppearanceSystem _humanoidAppearance = default!;
    [Dependency] private readonly IdentitySystem _identity = default!;
    [Dependency] private readonly ServerInventorySystem _inventory = default!;
    [Dependency] private readonly MindSystem _mind = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movementSpeedModifier = default!;
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly NameModifierSystem _nameMod = default!;

    private static readonly ProtoId<TagPrototype> InvalidForGlobalSpawnSpellTag = "InvalidForGlobalSpawnSpell";

    /// <summary>
    /// Handles an entity turning into a zombie when they die or go into crit
    /// </summary>
    private void OnDamageChanged(EntityUid uid, GooifyOnDeathComponent component, MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Dead)
        {
            GooifyEntity(uid, args.Component);
        }
    }

    /// <summary>
    ///     This is the general purpose function to call if you want to zombify an entity.
    ///     It handles both humanoid and nonhumanoid transformation and everything should be called through it.
    /// </summary>
    /// <param name="target">the entity being zombified</param>
    /// <param name="mobState"></param>
    /// <remarks>
    ///     ALRIGHT BIG BOYS, GIRLS AND ANYONE ELSE. YOU'VE COME TO THE LAYER OF THE BEAST. THIS IS YOUR WARNING.
    ///     This function is the god function for zombie stuff, and it is cursed. I have
    ///     attempted to label everything thouroughly for your sanity. I have attempted to
    ///     rewrite this, but this is how it shall lie eternal. Turn back now.
    ///     -emo
    /// </remarks>
    ///




    public void GooifyEntity(EntityUid target, MobStateComponent? mobState = null)
    {
       // var alpha = false;




// I know this code is bad but im a bad coder -eg


        //Don't zombfiy zombies
        if (HasComp<GooZombieComponent>(target) || HasComp<GooZombieImmuneComponent>(target) ||
            HasComp<AlphaGooZombieComponent>(target))
            return;

        if (!Resolve(target, ref mobState, logMissing: false))
            return;

        if (HasComp<PendingAlphaGooComponent>(target))
        {
            AddComp<AlphaGooZombieComponent>(target);
           // alpha = true;
        }

        //you're a real zombie now, son.


      var  goozombiecomp = AddComp<GooZombieComponent>(target);


    //we need to basically remove all of these because zombies shouldn't
        //get diseases, breath, be thirst, be hungry, die in space, have offspring or be paraplegic.
        RemComp<RespiratorComponent>(target);
        RemComp<BarotraumaComponent>(target);
        RemComp<HungerComponent>(target);
        RemComp<ThirstComponent>(target);
        RemComp<ReproductiveComponent>(target);
        RemComp<ReproductivePartnerComponent>(target);
        RemComp<LegsParalyzedComponent>(target);
        RemComp<ComplexInteractionComponent>(target);


        //This is needed for stupid entities that fuck up combat mode component
        //in an attempt to make an entity not attack. This is the easiest way to do it.
        var combat = EnsureComp<CombatModeComponent>(target);
        RemComp<PacifiedComponent>(target);
        _combat.SetCanDisarm(target, false, combat);
        _combat.SetInCombatMode(target, true, combat);

        //This is the actual damage of the zombie. We assign the visual appearance
        //and range here because of stuff we'll find out later
        var melee = EnsureComp<MeleeWeaponComponent>(target);
        melee.Animation =goozombiecomp.AttackAnimation;
        melee.WideAnimation =goozombiecomp.AttackAnimation;
        melee.AltDisarm = false;
        melee.Range = 1.2f;
        melee.Angle = 0.0f;
        melee.HitSound =goozombiecomp.BiteSound;

        DirtyFields(target, melee, null, fields:
        [
            nameof(MeleeWeaponComponent.Animation),
            nameof(MeleeWeaponComponent.WideAnimation),
            nameof(MeleeWeaponComponent.AltDisarm),
            nameof(MeleeWeaponComponent.Range),
            nameof(MeleeWeaponComponent.Angle),
            nameof(MeleeWeaponComponent.HitSound),
        ]);

        if (mobState.CurrentState == MobState.Alive)
        {
            // Groaning when damaged
            EnsureComp<EmoteOnDamageComponent>(target);
            _emoteOnDamage.AddEmote(target, "Scream");

            // Random groaning
            EnsureComp<AutoEmoteComponent>(target);
            _autoEmote.AddEmote(target, "ZombieGroan");
        }

        //We have specific stuff for humanoid zombies because they matter more
        if (TryComp<HumanoidAppearanceComponent>(target, out var huApComp)) //huapcomp
        {
            if (HasComp<AlphaGooZombieComponent>(target))
            {
                //hardcoding for now -eg
                _humanoidAppearance.SetSkinColor(target,new Color(0.15f,0.15f,0.15f), verify: false, humanoid: huApComp);

            }

            else
            {
                _humanoidAppearance.SetSkinColor(target,goozombiecomp.SkinColor, verify: false, humanoid: huApComp);

            }
            Log.Debug( goozombiecomp.ZombieMovementSpeedBuff.ToString());

            // Messing with the eye layer made it vanish upon cloning, and also it didn't even appear right
            huApComp.EyeColor =goozombiecomp.EyeColor;


            // this might not resync on clone?
            _humanoidAppearance.SetBaseLayerId(target, HumanoidVisualLayers.Tail,goozombiecomp.BaseLayerExternal, humanoid: huApComp);
            _humanoidAppearance.SetBaseLayerId(target, HumanoidVisualLayers.HeadSide,goozombiecomp.BaseLayerExternal, humanoid: huApComp);
            _humanoidAppearance.SetBaseLayerId(target, HumanoidVisualLayers.HeadTop,goozombiecomp.BaseLayerExternal, humanoid: huApComp);
            _humanoidAppearance.SetBaseLayerId(target, HumanoidVisualLayers.Snout,goozombiecomp.BaseLayerExternal, humanoid: huApComp);

            //This is done here because non-humanoids shouldn't get baller damage
            //lord forgive me for the hardcoded damage
            DamageSpecifier dspec = new()
            {
                DamageDict = new()
                {
                    { "Slash", 13 },
                    { "Piercing", 7 },
                    { "Structural", 10 }
                }
            };
            melee.Damage = dspec;

            // humanoid zombies get to pry open doors and shit
            var pryComp = EnsureComp<PryingComponent>(target);
            pryComp.SpeedModifier = 0.75f;
            pryComp.PryPowered = true;
            pryComp.Force = true;

            Dirty(target, pryComp);
        }

        Dirty(target, melee);

        //The zombie gets the assigned damage weaknesses and strengths
        _damageable.SetDamageModifierSetId(target, "Zombie");

        //This makes it so the zombie doesn't take bloodloss damage.
        //NOTE: they are supposed to bleed, just not take damage
        _bloodstream.SetBloodLossThreshold(target, 0f);
        //Give them zombie blood
        _bloodstream.ChangeBloodReagent(target,goozombiecomp.NewBloodReagent);

        //This is specifically here to combat insuls, because frying zombies on grilles is funny as shit.
        _inventory.TryUnequip(target, "gloves", true, true);
        //Should prevent instances of zombies using comms for information they shouldnt be able to have.
        _inventory.TryUnequip(target, "ears", true, true);

        //popup
        _popup.PopupEntity(Loc.GetString("goo-transform", ("target", target)), target, PopupType.LargeCaution);

        //Make it sentient if it's an animal or something
        MakeSentientCommand.MakeSentient(target, EntityManager);

        //Make the zombie not die in the cold. Good for space zombies
        if (TryComp<TemperatureComponent>(target, out var tempComp))
            tempComp.ColdDamage.ClampMax(0);

        //Heals the zombie from all the damage it took while human
        if (TryComp<DamageableComponent>(target, out var damageablecomp))
            _damageable.SetAllDamage(target, damageablecomp, 0);
        _mobState.ChangeMobState(target, MobState.Alive);

        _faction.ClearFactions(target, dirty: false);
       // _faction.AddFaction(target, "Zombie");

        //gives it the funny "Zombie ___" name.
        _nameMod.RefreshNameModifiers(target);

        _identity.QueueIdentityUpdate(target);

        var htn = EnsureComp<HTNComponent>(target);
        htn.RootTask = new HTNCompoundTask() { Task = "SimpleHostileCompound" };
        htn.Blackboard.SetValue(NPCBlackboard.Owner, target);
        _npc.SleepNPC(target, htn);

        //He's gotta have a mind
        var hasMind = _mind.TryGetMind(target, out var mindId, out _);
        if (hasMind && _mind.TryGetSession(mindId, out var session))
        {

            //Greeting message for new bebe zombers
            _chatMan.DispatchServerMessage(session, Loc.GetString("goo-infection-greeting"));

            // Notificate player about new role assignment
            _audio.PlayGlobal(goozombiecomp.GreetSoundNotification, session);
        }
        else
        {
            _npc.WakeNPC(target, htn);
        }


        if (TryComp<HandsComponent>(target, out var handsComp))
        {
            _hands.RemoveHands(target);
            RemComp(target, handsComp);
        }

        // Sloth: What the fuck?
        // How long until compregistry lmao.
        RemComp<PullerComponent>(target);

        // No longer waiting to become a zombie:
        // Requires deferral because this is (probably) the event which called GooifyEntity in the first place.
        RemCompDeferred<PendingGooZombieComponent>(target);

        //zombie gamemode stuff
     //   var ev = new EntityZombifiedEvent(target);
     //   RaiseLocalEvent(target, ref ev, true);
        _movementSpeedModifier.RefreshMovementSpeedModifiers(target);

        //Need to prevent them from getting an item, they have no hands.
        // Also prevents them from becoming a Survivor. They're undead.
        _tag.AddTag(target, InvalidForGlobalSpawnSpellTag);
    }
}

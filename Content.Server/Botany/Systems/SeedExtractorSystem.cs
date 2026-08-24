using System.Linq;
using Content.Server._CMU14.Botany;
using Content.Server.Botany.Components;
using Content.Server.Popups;
using Content.Server.Power.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Storage;
using Content.Shared.Verbs;
using Robust.Shared.Random;

namespace Content.Server.Botany.Systems;

public sealed partial class SeedExtractorSystem : EntitySystem
{
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private PopupSystem _popupSystem = default!;
    [Dependency] private BotanySystem _botanySystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SeedExtractorComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<SeedExtractorComponent, GetVerbsEvent<AlternativeVerb>>(OnGetAlternativeVerbs);
    }

    private void OnInteractUsing(EntityUid uid, SeedExtractorComponent seedExtractor, InteractUsingEvent args)
    {
        if (!this.IsPowered(uid, EntityManager))
            return;

        if (!TryComp(args.Used, out ProduceComponent? produce))
            return;
        if (!_botanySystem.TryGetSeed(produce, out var seed) || seed.Seedless)
        {
            _popupSystem.PopupCursor(Loc.GetString("seed-extractor-component-no-seeds", ("name", args.Used)),
                args.User, PopupType.MediumCaution);
            return;
        }

        _popupSystem.PopupCursor(Loc.GetString("seed-extractor-component-interact-message", ("name", args.Used)),
            args.User, PopupType.Medium);

        args.Handled = true;

        ExtractProduce(uid, args.Used, seed, seedExtractor, args.User);
    }

    private void OnGetAlternativeVerbs(Entity<SeedExtractorComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess ||
            !args.CanInteract ||
            !this.IsPowered(ent, EntityManager) ||
            args.Using is not { } plantBag ||
            !HasComp<CMUPlantBagComponent>(plantBag) ||
            !HasComp<StorageComponent>(plantBag))
        {
            return;
        }

        var user = args.User;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("seed-extractor-component-convert-plant-bag"),
            IconEntity = GetNetEntity(plantBag),
            Act = () => ConvertPlantBag(ent, plantBag, user),
        });
    }

    private void ConvertPlantBag(
        Entity<SeedExtractorComponent> ent,
        EntityUid plantBag,
        EntityUid user)
    {
        if (!Exists(plantBag) ||
            !this.IsPowered(ent, EntityManager) ||
            !TryComp(plantBag, out CMUPlantBagComponent? _) ||
            !TryComp(plantBag, out StorageComponent? storage))
        {
            return;
        }

        var produceConverted = 0;
        var seedsExtracted = 0;
        foreach (var item in storage.Container.ContainedEntities.ToList())
        {
            if (!TryComp(item, out ProduceComponent? produce) ||
                !_botanySystem.TryGetSeed(produce, out var seed) ||
                seed.Seedless)
            {
                continue;
            }

            seedsExtracted += ExtractProduce(ent, item, seed, ent.Comp, user);
            produceConverted++;
        }

        if (produceConverted == 0)
        {
            _popupSystem.PopupEntity(
                Loc.GetString("seed-extractor-component-plant-bag-no-seeds"),
                ent,
                user,
                PopupType.MediumCaution);
            return;
        }

        _popupSystem.PopupEntity(
            Loc.GetString("seed-extractor-component-plant-bag-converted",
                ("produce", produceConverted),
                ("seeds", seedsExtracted)),
            ent,
            user,
            PopupType.Medium);
    }

    private int ExtractProduce(
        EntityUid extractor,
        EntityUid produce,
        SeedData seed,
        SeedExtractorComponent seedExtractor,
        EntityUid user)
    {
        QueueDel(produce);

        var amount = _random.Next(seedExtractor.BaseMinSeeds, seedExtractor.BaseMaxSeeds + 1);
        var coords = Transform(extractor).Coordinates;

        var packetSeed = seed;
        if (amount > 1)
            packetSeed.Unique = false;

        for (var i = 0; i < amount; i++)
        {
            _botanySystem.SpawnSeedPacket(packetSeed, coords, user);
        }

        return amount;
    }
}

using Content.Shared._RMC14.Attachable.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Storage.Components;
using Content.Shared.Tag;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests._CMU14.Weapons;

[TestFixture]
public sealed class MP5A5LoadoutPrototypeTest
{
    [TestCase("CMUWeaponSMGMP5AltStandard", "CMMagazineSMGMP5", 30, "CMCartridge9mmSMG")]
    [TestCase("CMUWeaponSMGMP5AltDrum", "CMUMagazineSMGMP5Drum", 60, "CMCartridge9mmSMG")]
    [TestCase("CMUWeaponSMGMP5AltAP", "CMUMagazineSMGMP5AP", 30, "CMCartridgePistolM77AP")]
    [TestCase("CMUWeaponSMGMP5AltAPDrum", "CMUMagazineSMGMP5APDrum", 60, "CMCartridgePistolM77AP")]
    [TestCase("CMUWeaponSMGMP5AltSquashHead", "CMUMagazineSMGMP5SquashHead", 30, "RMCCartridgeSMG9mmSquashHead")]
    [TestCase("CMUWeaponSMGMP5AltSquashHeadDrum", "CMUMagazineSMGMP5SquashHeadDrum", 60, "RMCCartridgeSMG9mmSquashHead")]
    public async Task MP5A5LoadoutHasRequestedAttachmentsAndMagazine(
        string weaponId,
        string magazineId,
        int capacity,
        string cartridgeId)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var factory = server.EntMan.ComponentFactory;

            Assert.That(prototypes.TryIndex<EntityPrototype>(weaponId, out var weapon), Is.True);
            Assert.That(weapon!.TryComp<ItemSlotsComponent>(out var itemSlots, factory), Is.True);
            var magazineSlot = itemSlots!.Slots["gun_magazine"];
            Assert.Multiple(() =>
            {
                Assert.That(magazineSlot.StartingItem, Is.EqualTo(magazineId));
                Assert.That(magazineSlot.Whitelist, Is.Not.Null,
                    "The magazine slot must not become unrestricted when its starting magazine is overridden.");
                Assert.That(magazineSlot.Whitelist?.Tags, Has.Count.EqualTo(1));
                Assert.That(magazineSlot.Whitelist?.Tags?[0].ToString(), Is.EqualTo("CMMagazineSMGMP5"));
            });

            Assert.That(weapon.TryComp<AttachableHolderComponent>(out var holder, factory), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(holder!.RandomAttachmentChance, Is.Zero);
                Assert.That(holder.Slots["rmc-aslot-rail"].StartingAttachable?.ToString(),
                    Is.EqualTo("AU14AttachmentTwoPointSling"));
                Assert.That(holder.Slots["rmc-aslot-underbarrel"].StartingAttachable?.ToString(),
                    Is.EqualTo("RMCAttachmentLaserSight"));
                Assert.That(holder.Slots["rmc-aslot-stock"].StartingAttachable?.ToString(),
                    Is.EqualTo("RMCAttachmentMP5AltStockCollapsible"));
                Assert.That(holder.Slots["rmc-aslot-stock"].Locked, Is.True);
            });

            Assert.That(prototypes.TryIndex<EntityPrototype>(magazineId, out var magazine), Is.True);
            Assert.That(magazine!.TryComp<BallisticAmmoProviderComponent>(out var ammo, factory), Is.True);
            Assert.That(magazine.TryComp<TagComponent>(out var tags, factory), Is.True);
            var isDrum = magazineId.EndsWith("Drum", StringComparison.Ordinal);
            Assert.Multiple(() =>
            {
                Assert.That(ammo!.Capacity, Is.EqualTo(capacity));
                Assert.That(ammo.Proto, Is.EqualTo((EntProtoId) cartridgeId));
                Assert.That(tags!.Tags, Contains.Item("CMMagazineSMGMP5"));
                Assert.That(tags.Tags, isDrum
                    ? Contains.Item("CMUMagazineSMGMP5Drum")
                    : Contains.Item("CMUMagazineSMGMP5Stick"));
                Assert.That(tags.Tags, isDrum
                    ? Does.Not.Contains("CMUMagazineSMGMP5Stick")
                    : Does.Not.Contains("CMUMagazineSMGMP5Drum"));
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MP5WeaponsMapStickAndDrumMagazineSprites()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var factory = server.EntMan.ComponentFactory;

            foreach (var weaponId in new[] { "WeaponSMGMP5", "RMCWeaponSMGMP5Alt" })
            {
                Assert.That(prototypes.TryIndex<EntityPrototype>(weaponId, out var weapon), Is.True);
                Assert.That(weapon!.TryComp<ItemMapperComponent>(out var mapper, factory), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(mapper!.ContainerWhitelist, Contains.Item("gun_magazine"));
                    Assert.That(mapper.MapLayers, Contains.Key("mag-0"));
                    Assert.That(mapper.MapLayers, Contains.Key("mag-1"));
                    Assert.That(mapper.MapLayers["mag-0"].Whitelist?.Tags,
                        Contains.Item("CMUMagazineSMGMP5Stick"));
                    Assert.That(mapper.MapLayers["mag-1"].Whitelist?.Tags,
                        Contains.Item("CMUMagazineSMGMP5Drum"));
                });
            }
        });

        await pair.CleanReturnAsync();
    }
}

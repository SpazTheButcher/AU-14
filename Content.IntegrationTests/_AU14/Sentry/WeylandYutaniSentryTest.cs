using System.Collections.Generic;
using System.Linq;
using Content.Server._RMC14.Sentry;
using Content.Shared._RMC14.Dropship.Utility.Components;
using Content.Shared._RMC14.Sentry;
using Content.Shared._RMC14.Weapons.Ranged.IFF;
using Content.Shared.Inventory;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests._AU14.Sentry;

[TestFixture]
public sealed class WeylandYutaniSentryTest
{
    private static readonly EntProtoId CorporateSentry = "AU14SentryOmniWeYu";
    private static readonly EntProtoId CorporateSentryDeployer = "AU14DeployerSentryWeYu";
    private static readonly EntProtoId CorporateAlmayerTurret = "AU14TurretAlmayerWeYu";
    private static readonly EntProtoId BaseSentryDeployer = "RMCDeployerSentry";
    private static readonly EntProtoId CorporateNpc = "AU14JobWYPMCPartyContractor";
    private static readonly EntProtoId Human = "CMMobHuman";
    private static readonly EntProtoId CorporateId = "CMIDCardLiaison";
    private static readonly EntProtoId Xenomorph = "CMXenoDrone";
    private static readonly EntProtoId Neomorph = "CMU14XenoNeomorph";
    private static readonly EntProtoId Abomination = "AU14AbominationSpider";
    private static readonly EntProtoId XenoVines = "XenoWeeds";
    private static readonly EntProtoId NeomorphVines = "CMU14XenoMyceliumWeeds";
    private static readonly EntProtoId NeomorphStructure = "CMU14PathogenOvermindCore";
    private static readonly EntProtoId AbominationVines = "AU14AbominationFleshKudzu";
    private static readonly EntProtoId AbominationStructure = "AU14AbominationFleshNest";
    private static readonly EntProtoId AbominationWall = "AU14AbominationFleshWall";
    private static readonly EntProtoId<IFFFactionComponent> CorporateIff = "FactionWEYU";
    private static readonly EntProtoId<IFFFactionComponent> GovforIff = "GOVFOR";

    [Test]
    public async Task CorporateSentryWhitelistIsLockedAndRecognizesCorporateAffiliates()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var targetingSystem = server.System<SentryTargetingSystem>();
            var iffSystem = server.System<GunIFFSystem>();
            var inventorySystem = server.System<InventorySystem>();
            var sentry = entities.SpawnEntity(CorporateSentry, testMap.GridCoords);
            var corporateNpc = entities.SpawnEntity(CorporateNpc, testMap.GridCoords);
            var corporateIff = entities.SpawnEntity(null, testMap.GridCoords);
            var corporateCardHolder = entities.SpawnEntity(Human, testMap.GridCoords);
            var corporateCard = entities.SpawnEntity(CorporateId, testMap.GridCoords);
            var outsider = entities.SpawnEntity(null, testMap.GridCoords);

            try
            {
                iffSystem.SetUserFaction(corporateIff, CorporateIff);
                iffSystem.SetUserFaction(outsider, GovforIff);
                Assert.That(inventorySystem.TryEquip(corporateCardHolder, corporateCard, "id", force: true), Is.True);

                var targeting = entities.GetComponent<SentryTargetingComponent>(sentry);
                var expectedCorporateFactions = new[] { "AUWeYu", "WeYa" };
                var nearbyTargets = targetingSystem.GetNearbyIffHostiles((sentry, targeting), 1).ToHashSet();

                Assert.Multiple(() =>
                {
                    Assert.That(targeting.LockedFriendlyFactions, Is.EquivalentTo(expectedCorporateFactions));
                    Assert.That(targeting.FriendlyFactions, Is.EquivalentTo(expectedCorporateFactions));
                    Assert.That(targetingSystem.IsValidTarget((sentry, targeting), corporateNpc), Is.False);
                    Assert.That(targetingSystem.IsValidTarget((sentry, targeting), corporateIff), Is.False);
                    Assert.That(targetingSystem.IsValidTarget((sentry, targeting), corporateCardHolder), Is.False);
                    Assert.That(targetingSystem.IsValidTarget((sentry, targeting), outsider), Is.True);
                    Assert.That(nearbyTargets, Does.Not.Contain(corporateNpc));
                    Assert.That(nearbyTargets, Does.Not.Contain(corporateIff));
                    Assert.That(nearbyTargets, Does.Not.Contain(corporateCardHolder));
                    Assert.That(nearbyTargets, Does.Contain(outsider));
                });

                targetingSystem.SetFriendlyFactions((sentry, targeting), new HashSet<string> { "GOVFOR" });
                targetingSystem.ToggleFaction((sentry, targeting), "AUWeYu", false);
                targetingSystem.ClearFactionAssignment((sentry, targeting));
                targetingSystem.ApplyDeployerFactions(sentry, outsider);
                targetingSystem.TryApplyDefaultFaction(sentry, "GOVFOR");

                Assert.Multiple(() =>
                {
                    Assert.That(targeting.FriendlyFactions, Is.EquivalentTo(expectedCorporateFactions));
                    Assert.That(targeting.DeployedFriendlyFactions, Is.EquivalentTo(expectedCorporateFactions));
                    Assert.That(targeting.AllianceFriendlyNpcFactions, Is.Empty);
                    var sentryIff = entities.GetComponent<UserIFFComponent>(sentry);
                    Assert.That(sentryIff.Factions, Does.Contain(CorporateIff));
                    Assert.That(sentryIff.Factions, Does.Not.Contain(GovforIff));
                });
            }
            finally
            {
                entities.DeleteEntity(sentry);
                entities.DeleteEntity(corporateNpc);
                entities.DeleteEntity(corporateIff);
                entities.DeleteEntity(corporateCardHolder);
                entities.DeleteEntity(outsider);
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CorporateRmcDeployerSentryIsSeparateFromBaseDeployer()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var deployer = entities.SpawnEntity(CorporateSentryDeployer, testMap.GridCoords);
            var baseDeployer = entities.SpawnEntity(BaseSentryDeployer, testMap.GridCoords);

            try
            {
                var deployerComponent = entities.GetComponent<RMCEquipmentDeployerComponent>(deployer);
                var baseDeployerComponent = entities.GetComponent<RMCEquipmentDeployerComponent>(baseDeployer);
                Assert.That(deployerComponent.DeployEntity, Is.Not.Null);
                Assert.That(baseDeployerComponent.DeployEntity, Is.Not.Null);

                var sentry = entities.GetEntity(deployerComponent.DeployEntity!.Value);
                var targeting = entities.GetComponent<SentryTargetingComponent>(sentry);
                var baseSentry = entities.GetEntity(baseDeployerComponent.DeployEntity!.Value);
                var baseTargeting = entities.GetComponent<SentryTargetingComponent>(baseSentry);
                var expectedCorporateFactions = new[] { "AUWeYu", "WeYa" };

                Assert.Multiple(() =>
                {
                    Assert.That(deployerComponent.DeployPrototype, Is.EqualTo(CorporateAlmayerTurret));
                    Assert.That(targeting.LockedFriendlyFactions, Is.EquivalentTo(expectedCorporateFactions));
                    Assert.That(targeting.FriendlyFactions, Is.EquivalentTo(expectedCorporateFactions));
                    Assert.That(baseDeployerComponent.DeployPrototype, Is.EqualTo((EntProtoId) "RMCTurretAlmayer"));
                    Assert.That(baseTargeting.LockedFriendlyFactions, Is.Empty);
                });
            }
            finally
            {
                entities.DeleteEntity(deployer);
                entities.DeleteEntity(baseDeployer);
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CorporateSentryTargetsAlienMobsStructuresAndVines()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var targetingSystem = server.System<SentryTargetingSystem>();
            var sentry = entities.SpawnEntity(CorporateSentry, testMap.GridCoords);
            var targets = new[]
            {
                entities.SpawnEntity(Xenomorph, testMap.GridCoords),
                entities.SpawnEntity(Neomorph, testMap.GridCoords),
                entities.SpawnEntity(Abomination, testMap.GridCoords),
                entities.SpawnEntity(XenoVines, testMap.GridCoords),
                entities.SpawnEntity(NeomorphVines, testMap.GridCoords),
                entities.SpawnEntity(NeomorphStructure, testMap.GridCoords),
                entities.SpawnEntity(AbominationVines, testMap.GridCoords),
                entities.SpawnEntity(AbominationStructure, testMap.GridCoords),
                entities.SpawnEntity(AbominationWall, testMap.GridCoords),
            };

            try
            {
                var targeting = entities.GetComponent<SentryTargetingComponent>(sentry);
                var nearbyTargets = targetingSystem.GetNearbyIffHostiles((sentry, targeting), 1).ToHashSet();

                Assert.Multiple(() =>
                {
                    foreach (var target in targets)
                    {
                        Assert.That(targetingSystem.IsValidTarget((sentry, targeting), target), Is.True,
                            $"{entities.GetComponent<MetaDataComponent>(target).EntityPrototype?.ID} should be a valid target");
                        Assert.That(nearbyTargets, Does.Contain(target),
                            $"{entities.GetComponent<MetaDataComponent>(target).EntityPrototype?.ID} should be discovered by the sentry scan");
                    }
                });
            }
            finally
            {
                entities.DeleteEntity(sentry);
                foreach (var target in targets)
                    entities.DeleteEntity(target);
            }
        });

        await pair.CleanReturnAsync();
    }
}

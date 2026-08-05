using System.Linq;
using Content.Server._RMC14.Sentry;
using Content.Shared.AU14.AllianceConsole;
using Content.Shared._RMC14.Sentry;
using Content.Shared._RMC14.Weapons.Ranged.IFF;
using Content.Shared.Physics;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests._AU14.Sentry;

[TestFixture]
public sealed class DropshipSentryRegressionTest
{
    private static readonly EntProtoId DropshipSentry = "RMCSentryDropship";
    private static readonly EntProtoId GovforAllianceConsole = "AU14AllianceConsoleGovfor";

    [Test]
    public async Task DropshipSentryIsShootableAndInheritsStoredAllianceState()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entities = server.EntMan;
            var targetingSystem = server.System<SentryTargetingSystem>();
            var sentry = entities.SpawnEntity(DropshipSentry, testMap.GridCoords);
            var console = entities.SpawnEntity(GovforAllianceConsole, testMap.GridCoords);
            var unknownTarget = entities.SpawnEntity(null, testMap.GridCoords);

            try
            {
                var fixtures = entities.GetComponent<FixturesComponent>(sentry);
                Assert.That(fixtures.Fixtures.Values.Any(fixture =>
                    (fixture.CollisionLayer & (int) CollisionGroup.BulletImpassable) != 0), Is.True);

                entities.EnsureComponent<UserIFFComponent>(unknownTarget);

                entities.EventBus.RaiseLocalEvent(console,
                    new AllianceConsoleSetFactionStatusMsg("CLF", AllianceStatus.Friendly));
                entities.EventBus.RaiseLocalEvent(console,
                    new AllianceConsoleSetFactionStatusMsg(AllianceConsoleComponent.UnknownFaction,
                        AllianceStatus.Friendly));

                Assert.That(targetingSystem.TryApplyDefaultFaction(sentry, "GOVFOR"), Is.True);
                var targeting = entities.GetComponent<SentryTargetingComponent>(sentry);

                Assert.Multiple(() =>
                {
                    Assert.That(targeting.AllianceFriendlyNpcFactions.Select(faction => faction.Id),
                        Does.Contain("CLF"));
                    Assert.That(targeting.AllianceUnknownStatus, Is.EqualTo(AllianceStatus.Friendly));
                    Assert.That(targetingSystem.IsValidTarget((sentry, targeting), unknownTarget), Is.False);
                });

                entities.EventBus.RaiseLocalEvent(console,
                    new AllianceConsoleSetFactionStatusMsg(AllianceConsoleComponent.UnknownFaction,
                        AllianceStatus.Hostile));

                Assert.That(targeting.AllianceUnknownStatus, Is.EqualTo(AllianceStatus.Hostile));
                Assert.That(targetingSystem.IsValidTarget((sentry, targeting), unknownTarget), Is.True);
            }
            finally
            {
                entities.DeleteEntity(sentry);
                entities.DeleteEntity(console);
                entities.DeleteEntity(unknownTarget);
            }
        });

        await pair.CleanReturnAsync();
    }
}

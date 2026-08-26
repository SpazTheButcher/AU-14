using Content.Server._CMU14.Explosion;
using Content.Shared.Explosion.Components;
using Robust.Shared.Map;

namespace Content.IntegrationTests._CMU14.Explosion;

[TestFixture]
[TestOf(typeof(CMUGrenadeDetonationSystem))]
public sealed class CMUGrenadeDetonationTest
{
    [TestCase("CMGrenadeHighExplosive", 4f)]
    [TestCase("CMGrenadeFrag", 4f)]
    [TestCase("CMGrenadeSmoke", 2.5f)]
    public async Task TargetGrenadesHaveCMUModeAndKeepExistingFuse(
        string prototype,
        float expectedDelay)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var grenade = entMan.SpawnEntity(prototype, MapCoordinates.Nullspace);

            try
            {
                var mode = entMan.GetComponent<CMUGrenadeDetonationModeComponent>(grenade);
                var timer = entMan.GetComponent<OnUseTimerTriggerComponent>(grenade);

                Assert.Multiple(() =>
                {
                    Assert.That(mode.Mode, Is.EqualTo(CMUGrenadeDetonationMode.Timed));
                    Assert.That(mode.Armed, Is.False);
                    Assert.That(timer.Delay, Is.EqualTo(expectedDelay).Within(0.001f));
                });
            }
            finally
            {
                entMan.DeleteEntity(grenade);
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ModeCanChangeBeforeArming()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var system = entMan.System<CMUGrenadeDetonationSystem>();
            var grenade = entMan.SpawnEntity("CMGrenadeHighExplosive", MapCoordinates.Nullspace);

            try
            {
                var component = entMan.GetComponent<CMUGrenadeDetonationModeComponent>(grenade);

                Assert.That(
                    system.TrySetMode(grenade, CMUGrenadeDetonationMode.Impact),
                    Is.True);
                Assert.That(component.Mode, Is.EqualTo(CMUGrenadeDetonationMode.Impact));

                Assert.That(
                    system.TrySetMode(grenade, CMUGrenadeDetonationMode.Timed),
                    Is.True);
                Assert.That(component.Mode, Is.EqualTo(CMUGrenadeDetonationMode.Timed));
            }
            finally
            {
                entMan.DeleteEntity(grenade);
            }
        });

        await pair.CleanReturnAsync();
    }
}

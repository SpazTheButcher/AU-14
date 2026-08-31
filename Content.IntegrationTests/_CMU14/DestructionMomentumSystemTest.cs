using Content.Server._CMU14.Destruction;
using Content.Shared._CMU14.Destruction;
using Content.Shared.Damage;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests._CMU14;

[TestFixture]
[TestOf(typeof(DestructionMomentumSystem))]
public sealed class DestructionMomentumSystemTest
{
    private const string TestObstacle = "CMUTestDestructionMomentumObstacle";

    [TestPrototypes]
    private const string Prototypes = $@"
- type: damageModifierSet
  id: CMUTestDestructionMomentumModifier
  coefficients:
    Blunt: 0.5

- type: entity
  id: {TestObstacle}
  components:
  - type: Damageable
    damageContainer: StructuralInorganic
    damageModifierSet: CMUTestDestructionMomentumModifier
  - type: Destructible
    thresholds:
    - trigger:
        !type:DamageTrigger
        damage: 50
      behaviors:
      - !type:DoActsBehavior
        acts: [Breakage]
    - trigger:
        !type:DamageTrigger
        damage: 100
      behaviors:
      - !type:DoActsBehavior
        acts: [Destruction]
";

    [Test]
    public async Task BreakCostUsesRemainingDurability()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var target = entMan.SpawnEntity(TestObstacle, MapCoordinates.Nullspace);
            var momentum = entMan.System<DestructionMomentumSystem>();
            var damageable = entMan.System<DamageableSystem>();

            Assert.That(momentum.TryGetBreakCost(target, 2f, 100f, out var fullCost), Is.True);
            Assert.That(fullCost, Is.EqualTo(MathF.Sqrt(2f)).Within(0.001f));

            damageable.TryChangeDamage(target, new DamageSpecifier { DamageDict = { ["Blunt"] = 75 } }, true);

            Assert.That(momentum.TryGetBreakCost(target, 2f, 100f, out var damagedCost), Is.True);
            Assert.That(damagedCost, Is.EqualTo(MathF.Sqrt(0.5f)).Within(0.001f));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task InsufficientImpactCannotClearObstacle()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var target = entMan.SpawnEntity(TestObstacle, MapCoordinates.Nullspace);
            var momentum = entMan.System<DestructionMomentumSystem>();

            Assert.That(momentum.TryGetBreakCost(target, 0.5f, 100f, out _), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RequiredBreakSpeedCanBeResolvedWithoutAnAvailableBudget()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var target = entMan.SpawnEntity(TestObstacle, MapCoordinates.Nullspace);
            var momentum = entMan.System<DestructionMomentumSystem>();

            Assert.That(momentum.TryGetRequiredBreakSpeed(target, 100f, out var required), Is.True);
            Assert.That(required, Is.EqualTo(MathF.Sqrt(2f)).Within(0.001f));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public void RemainingSpeedConservesSquaredSpeedBudget()
    {
        var remaining = ImpactEnergySolver.GetRemainingSpeed(10f, 4f);

        Assert.That(remaining, Is.EqualTo(MathF.Sqrt(84f)).Within(0.001f));
        Assert.That(remaining * remaining + 4f * 4f, Is.EqualTo(100f).Within(0.001f));
    }

    [Test]
    public void RemainingSpeedCannotGoBelowZero()
    {
        Assert.That(ImpactEnergySolver.GetRemainingSpeed(3f, 5f), Is.Zero.Within(0.001f));
    }

    [Test]
    public void SimultaneousContactsShareEnergyProportionally()
    {
        var allocation = ImpactEnergySolver.AllocateBatch(10f, [6f, 8f]);

        Assert.Multiple(() =>
        {
            Assert.That(allocation.CanClearAll, Is.True);
            Assert.That(allocation.AppliedFraction, Is.EqualTo(1f));
            Assert.That(allocation.RemainingSpeed, Is.Zero.Within(0.001f));
        });
    }

    [Test]
    public void UnderpoweredBatchAppliesSameFractionToEveryContact()
    {
        var allocation = ImpactEnergySolver.AllocateBatch(5f, [6f, 8f]);

        Assert.Multiple(() =>
        {
            Assert.That(allocation.CanClearAll, Is.False);
            Assert.That(allocation.AppliedFraction, Is.EqualTo(0.25f).Within(0.001f));
            Assert.That(allocation.RemainingSpeed, Is.Zero);
        });
    }
}

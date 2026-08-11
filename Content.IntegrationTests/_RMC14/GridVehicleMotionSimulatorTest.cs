using Content.Shared.Vehicle;
using System.Numerics;
using Robust.Shared.Maths;

namespace Content.IntegrationTests._RMC14;

[TestFixture]
public sealed class GridVehicleMotionSimulatorTest
{
    [TestCase(1, 2, 1, 1)]
    [TestCase(-1, 2, 1, -1)]
    [TestCase(1, -2, -1, -1)]
    [TestCase(-1, -2, -1, 1)]
    [TestCase(1, 2, -1, 1)]
    [TestCase(1, -2, 1, -1)]
    [TestCase(1, 0, -1, -1)]
    [TestCase(-1, 0, -1, 1)]
    [TestCase(1, 0, 0, 1)]
    public void SteeringFollowsTravelDirection(
        float steering,
        float currentSpeed,
        float throttle,
        float expected)
    {
        var actual = GridVehicleMotionSimulator.GetEffectiveSteering(steering, currentSpeed, throttle);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(true, 0, true)]
    [TestCase(true, 2, true)]
    [TestCase(true, -2, true)]
    [TestCase(false, 0, false)]
    [TestCase(false, 0.01f, false)]
    [TestCase(false, -0.01f, false)]
    [TestCase(false, 0.011f, true)]
    [TestCase(false, -0.011f, true)]
    public void StationarySteeringRequiresTurnInPlace(
        bool turnInPlace,
        float currentSpeed,
        bool expected)
    {
        var actual = GridVehicleMotionSimulator.CanSteer(turnInPlace, currentSpeed);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(2f, 0f, 0f, true)]
    [TestCase(-2f, 0f, 0f, false)]
    [TestCase(0f, 2f, 0f, false)]
    [TestCase(0f, -2f, 0f, false)]
    [TestCase(0f, 2f, 90f, true)]
    [TestCase(2f, 0f, 90f, false)]
    [TestCase(2f, 1f, 0f, true)]
    [TestCase(1f, 2f, 0f, false)]
    public void PlowBonusRequiresFrontImpact(float obstacleX, float obstacleY, float rotationDegrees, bool expected)
    {
        var vehicleBounds = new Box2(-1f, -2f, 1f, 2f);
        var obstacleBounds = Box2.CenteredAround(new Vector2(obstacleX, obstacleY), new Vector2(0.5f));
        var rotation = Angle.FromDegrees(rotationDegrees);

        Assert.That(
            GridVehicleMotionSimulator.IsFrontImpact(Vector2.Zero, rotation, vehicleBounds, obstacleBounds),
            Is.EqualTo(expected));
    }
}

using System;
using System.Numerics;

namespace Content.Shared._CMU14.Destruction;

/// <summary>
/// Shared squared-speed energy accounting for vehicle and dropship impacts.
/// </summary>
public static class ImpactEnergySolver
{
    public readonly record struct BatchAllocation(
        bool CanClearAll,
        float AppliedFraction,
        float RemainingSpeed);

    public static float GetRemainingSpeed(float availableSpeed, float requiredSpeed)
    {
        var available = MathF.Abs(availableSpeed);
        var required = MathF.Max(0f, requiredSpeed);
        return MathF.Sqrt(MathF.Max(0f, available * available - required * required));
    }

    /// <summary>
    /// Returns a stable geometric key for resolving swept contacts from the
    /// start of a motion toward its target. Stationary probes use radial
    /// distance so rotation-only checks still resolve the nearest contact first.
    /// </summary>
    public static float GetContactOrder(Vector2 start, Vector2 target, Vector2 contact)
    {
        var delta = target - start;
        if (delta == Vector2.Zero)
            return Vector2.DistanceSquared(start, contact);

        return Vector2.Dot(contact - start, Vector2.Normalize(delta));
    }

    /// <summary>
    /// Allocates one impact's energy across simultaneous contacts. When there
    /// is not enough energy to clear the whole batch, every contact receives
    /// the same proportion of its required destruction energy.
    /// </summary>
    public static BatchAllocation AllocateBatch(float availableSpeed, ReadOnlySpan<float> requiredSpeeds)
    {
        var availableEnergy = MathF.Abs(availableSpeed);
        availableEnergy *= availableEnergy;

        var requiredEnergy = 0f;
        foreach (var requiredSpeed in requiredSpeeds)
        {
            var required = MathF.Max(0f, requiredSpeed);
            requiredEnergy += required * required;
        }

        if (requiredEnergy <= 0f)
            return new BatchAllocation(true, 1f, MathF.Sqrt(availableEnergy));

        var appliedFraction = Math.Clamp(availableEnergy / requiredEnergy, 0f, 1f);
        var canClearAll = appliedFraction >= 1f;
        var remainingEnergy = canClearAll ? MathF.Max(0f, availableEnergy - requiredEnergy) : 0f;
        return new BatchAllocation(canClearAll, appliedFraction, MathF.Sqrt(remainingEnergy));
    }
}

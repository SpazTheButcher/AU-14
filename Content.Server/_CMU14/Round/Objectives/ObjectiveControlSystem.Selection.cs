namespace Content.Server._CMU14.Round.Objectives;

public sealed partial class ObjectiveControlSystem
{
    // GetInactiveObjectives
    // SelectObjectives
    // WeightedRandomPick
    // GetRandomObjectiveCount (extract from Main)

    // ActivateFactionObjectives
    //
    public string GetOppositeFaction(string faction, string? mode)
    {
        return (mode?.ToLowerInvariant(), faction.ToLowerInvariant()) switch
        {
            ("forceonforce", "govfor") => "opfor",
            ("forceonforce", "opfor") => "govfor",
            ("distresssignal", "clf") => "govfor",
            ("distresssignal", "govfor") => "clf",
            ("insurgency", "clf") => "govfor",
            ("insurgency", "govfor") => "clf",
            _ => string.Empty,
        };
    }

    // GetPlanetMapId
    // IsKillObjectiveCompletable
}

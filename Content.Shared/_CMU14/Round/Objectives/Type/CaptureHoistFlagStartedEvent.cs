namespace Content.Shared._CMU14.Round.Objectives;

public sealed class CaptureHoistFlagStartedEvent : EntityEventArgs // Stub
{
    public EntityUid User;
    public string Faction;

    public FlagHoistStartedEvent(EntityUid user, string faction)
    {
        User = user;
        Faction = faction;
    }
}

// DEPRECATED: Replaced by _CMU14/Round/Objectives.
#if false

using Robust.Shared.GameObjects;

namespace Content.Shared.AU14.Objectives.Capture;

public sealed class FlagHoistStartedEvent : EntityEventArgs
{
    public EntityUid User { get; }
    public string Faction { get; }
    public FlagHoistStartedEvent(EntityUid user, string faction)
    {
        User = user;
        Faction = faction;
    }
}

public sealed class FlagHoistedEvent : EntityEventArgs
{
    public EntityUid User { get; }
    public string Faction { get; }
    public FlagHoistedEvent(EntityUid user, string faction)
    {
        User = user;
        Faction = faction;
    }
}


#endif

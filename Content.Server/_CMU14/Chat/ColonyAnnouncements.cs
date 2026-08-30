using Content.Shared._RMC14.Xenonids;
using Robust.Shared.Player;

namespace Content.Server._CMU14.Chat;

// Colony-wide announcements are human-side comms; the hivemind must never receive them.
public static class ColonyAnnouncements
{
    public static Filter Recipients(IEntityManager entities) =>
        Filter.Empty().AddWhereAttachedEntity(e => !entities.HasComponent<XenoComponent>(e));
}

using Robust.Shared.Serialization;

namespace Content.Shared._RMC14.Medical.HUD;

[Serializable, NetSerializable]
public enum HolocardStatus : byte
{
    None,
    Urgent,
    Emergency,
    Xeno,
    Permadead,
    // Append-only: existing values must not shift (wire compatibility).
    // AutoHolocardSystem maps these values through an explicit clinical priority
    // and clears Trauma / OrganFailure after their qualifying injuries resolve.
    Stable,
    Trauma,
    OrganFailure,
}

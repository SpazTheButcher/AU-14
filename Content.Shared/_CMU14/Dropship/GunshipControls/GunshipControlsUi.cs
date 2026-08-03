using Robust.Shared.Serialization;

namespace Content.Shared._CMU14.Dropship.GunshipControls;

[Serializable, NetSerializable]
public enum GunshipControlsUiKey
{
    Key,
}

[Serializable, NetSerializable]
public enum GunshipControlsDestination : byte
{
    Navigation,
    Weapons,
}

[Serializable, NetSerializable]
public sealed class GunshipControlsOpenUiMsg(GunshipControlsDestination destination) : BoundUserInterfaceMessage
{
    public GunshipControlsDestination Destination = destination;
}

using Robust.Shared.Serialization;

namespace Content.Shared._RMC14.Dropship;

[Serializable, NetSerializable]
public sealed class DropshipHijackerBuiState(
    List<(NetEntity Id, string Name)> destinations,
    bool canDeclineHijack) : BoundUserInterfaceState
{
    public List<(NetEntity Id, string Name)> Destinations = destinations;
    public bool CanDeclineHijack = canDeclineHijack;
}

[Serializable, NetSerializable]
public sealed class DropshipHijackerDestinationChosenBuiMsg(NetEntity destination) : BoundUserInterfaceMessage
{
    public NetEntity Destination = destination;
}

[Serializable, NetSerializable]
public sealed class DropshipHijackerDeclineBuiMsg : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public enum DropshipHijackerUiKey
{
    Key,
}

using Robust.Shared.Serialization;

namespace Content.Shared._CMU14.Xenomorphs.Pathogen.Walker;

[Serializable, NetSerializable]
public enum CMUPathogenWalkerUiKey : byte
{
    Key
}

[Serializable, NetSerializable]
public sealed class CMUPathogenWalkerBuiState : BoundUserInterfaceState
{
    public double TimeoutSeconds;
    public CMUPathogenWalkerBuiState(double timeoutSeconds) => TimeoutSeconds = timeoutSeconds;
}

[Serializable, NetSerializable]
public sealed class CMUPathogenWalkerAcceptMsg : BoundUserInterfaceMessage { }

[Serializable, NetSerializable]
public sealed class CMUPathogenWalkerDeclineMsg : BoundUserInterfaceMessage { }
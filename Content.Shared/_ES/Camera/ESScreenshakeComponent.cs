using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._ES.Camera;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ESScreenshakeComponent : Component
{
    [DataField, AutoNetworkedField]
    public HashSet<ESScreenshakeCommand> Commands = new();

    public override bool SendOnlyToOwner => true;
}

[DataRecord, Serializable, NetSerializable]
public partial record ESScreenshakeCommand(ESScreenshakeParameters? Translational, ESScreenshakeParameters? Rotational, TimeSpan Start, TimeSpan CalculatedEnd);

[DataDefinition, Serializable, NetSerializable]
public partial record ESScreenshakeParameters()
{
    [DataField(required: true)]
    public float Trauma = 0f;

    [DataField]
    public float DecayRate = 1.2f;

    [DataField]
    public float Frequency = 0.01f;
};

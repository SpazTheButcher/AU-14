using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._CMU14.Dropship.Integrity;

[Serializable, NetSerializable]
public enum DropshipMalfunction : byte
{
    WeaponShort,
    PropulsionFault,
    ManeuveringThrusterFault,
    SensorArrayFault,
}

public static class DropshipMalfunctionData
{
    public static string GetAlertName(DropshipMalfunction malfunction)
    {
        return malfunction switch
        {
            DropshipMalfunction.WeaponShort => "Weapon short",
            DropshipMalfunction.PropulsionFault => "Propulsion fault",
            DropshipMalfunction.ManeuveringThrusterFault => "Maneuvering thruster fault",
            DropshipMalfunction.SensorArrayFault => "Sensor array fault",
            _ => "Unknown malfunction",
        };
    }
}

[Serializable, NetSerializable]
public sealed partial class DropshipMalfunctionRepairDoAfterEvent : DoAfterEvent
{
    [DataField(required: true)]
    public DropshipMalfunction Malfunction;

    [DataField]
    public int Step;

    public DropshipMalfunctionRepairDoAfterEvent()
    {
    }

    public DropshipMalfunctionRepairDoAfterEvent(DropshipMalfunction malfunction, int step)
    {
        Malfunction = malfunction;
        Step = step;
    }

    public override DoAfterEvent Clone()
    {
        return new DropshipMalfunctionRepairDoAfterEvent(Malfunction, Step);
    }
}

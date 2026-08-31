namespace Content.Shared._CMU14.Dropship.Integrity;

public enum DropshipAlarm : byte
{
    Proximity,
    LowIntegrity,
}

public static class DropshipAlarmData
{
    public static string GetAlertName(DropshipAlarm alarm)
    {
        return alarm switch
        {
            DropshipAlarm.Proximity => "COLLISION PROXIMITY WARNING",
            DropshipAlarm.LowIntegrity => "LOW HULL INTEGRITY",
            _ => "UNKNOWN ALARM",
        };
    }
}

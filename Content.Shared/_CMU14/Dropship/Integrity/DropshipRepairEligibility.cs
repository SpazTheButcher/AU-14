namespace Content.Shared._CMU14.Dropship.Integrity;

public static class DropshipRepairEligibility
{
    public static bool CanRepair(bool hovering, bool ftlActive)
    {
        return !hovering && !ftlActive;
    }
}

using Content.Shared._CMU14.Dropship.Integrity;

namespace Content.IntegrationTests._CMU14.Dropship;

[TestFixture]
public sealed class DropshipRepairEligibilityTest
{
    [TestCase(false, false, true)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, true, false)]
    public void RepairRequiresLandedAndNotInFtl(bool hovering, bool ftlActive, bool expected)
    {
        Assert.That(DropshipRepairEligibility.CanRepair(hovering, ftlActive), Is.EqualTo(expected));
    }
}

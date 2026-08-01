namespace Content.Shared._CMU14.Threats.Rules;

[RegisterComponent]
public sealed partial class HiveCollapseRuleComponent : Component
{
    /// <summary>
    ///     Duration in seconds for the hive to collapse once the Queen has fallen.
    ///     Default 5 Minutes.
    /// </summary>
    [DataField("hiveCollapseDuration")]
    public TimeSpan HiveCollapseDuration = TimeSpan.FromSeconds(300);

}

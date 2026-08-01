// DEPRECATED: Replaced by _CMU14/Round/Objectives.
#if false

using Robust.Shared.GameStates;

namespace Content.Shared.AU14.Objectives;

[RegisterComponent, NetworkedComponent]
public sealed partial class ObjectivesConsoleComponent : Component
{
    [DataField("faction", required: true)]
    public string Faction { get; private set; } = string.Empty;
}


#endif

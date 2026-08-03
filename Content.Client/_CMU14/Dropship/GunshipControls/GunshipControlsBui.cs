using Content.Shared._CMU14.Dropship.GunshipControls;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._CMU14.Dropship.GunshipControls;

[UsedImplicitly]
public sealed class GunshipControlsBui(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private GunshipControlsWindow? _window;

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<GunshipControlsWindow>();
        _window.NavigationPressed += () =>
            SendMessage(new GunshipControlsOpenUiMsg(GunshipControlsDestination.Navigation));
        _window.WeaponsPressed += () =>
            SendMessage(new GunshipControlsOpenUiMsg(GunshipControlsDestination.Weapons));
    }
}

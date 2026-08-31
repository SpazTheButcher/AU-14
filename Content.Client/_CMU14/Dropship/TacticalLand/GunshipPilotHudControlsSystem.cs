using System.Numerics;
using Content.Shared._CMU14.Dropship.TacticalLand;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client._CMU14.Dropship.TacticalLand;

/// <summary>
/// Interactive portion of the pilot HUD. The remaining indicators stay in an
/// overlay, while this control provides a real button that consumes UI clicks.
/// </summary>
public sealed partial class GunshipPilotHudControlsSystem : EntitySystem
{
    [Dependency] private IUserInterfaceManager _ui = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IGameTiming _timing = default!;

    private GunshipPilotTopBar? _bar;
    private LayoutContainer? _parent;
    private TimeSpan _nextStatusUpdate;

    public override void Shutdown()
    {
        HideBar();
        base.Shutdown();
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (_player.LocalEntity is not { } pilot ||
            !TryComp(pilot, out GunshipPilotHudComponent? hud) ||
            hud.Dropship is not { } dropship)
        {
            HideBar();
            return;
        }

        if (!EnsureBar())
            return;

        if (_timing.CurTime < _nextStatusUpdate)
            return;

        _nextStatusUpdate = _timing.CurTime + TimeSpan.FromMilliseconds(100);

        _bar!.SetViewMode(hud.RearView ? "REAR CAMERA" : hud.ViewOffset switch
        {
            > 0 => "UPPER CAMERA",
            < 0 => "LOWER CAMERA",
            _ => "PILOT VIEW",
        });

        if (hud.FlightControlsAvailable)
        {
            _bar.SetFlightStatus("--", "TACTICAL HOVER");
            return;
        }

        if (!TryComp(dropship, out FTLComponent? ftl))
        {
            _bar.SetFlightStatus("--", "READY");
            return;
        }

        var stage = ftl.State switch
        {
            FTLState.Starting => "LAUNCHING",
            FTLState.Travelling => "TRANSIT",
            FTLState.Arriving => "FINAL APPROACH",
            FTLState.Cooldown => "REFUELING",
            _ => "READY",
        };
        var phaseRemaining = Math.Max(0, (ftl.StateTime.End - _timing.CurTime).TotalSeconds);
        var destinationRemaining = ftl.State switch
        {
            // TravelTime contains both transit and final approach.
            FTLState.Starting => phaseRemaining + ftl.TravelTime,
            FTLState.Travelling => phaseRemaining +
                                   Math.Max(0, ftl.TravelTime - ftl.StateTime.Length.TotalSeconds),
            FTLState.Arriving => phaseRemaining,
            _ => -1,
        };
        var eta = destinationRemaining < 0
            ? "--"
            : $"T-{Math.Ceiling(destinationRemaining):0}s";
        _bar.SetFlightStatus(eta, stage);
    }

    private bool EnsureBar()
    {
        var screen = _ui.ActiveScreen;
        if (screen == null)
            return false;

        var parent = screen.FindControl<LayoutContainer>("ViewportContainer");
        if (_parent != parent)
        {
            HideBar();
            _parent = parent;
        }

        if (_bar != null)
            return true;

        _bar = new GunshipPilotTopBar(() => RaiseNetworkEvent(new GunshipOpenNavigationInputEvent()));
        parent.AddChild(_bar);
        LayoutContainer.SetAnchorPreset(_bar, LayoutContainer.LayoutPreset.CenterTop);
        LayoutContainer.SetMarginLeft(_bar, -340f);
        LayoutContainer.SetMarginRight(_bar, 340f);
        LayoutContainer.SetMarginTop(_bar, 8f);
        LayoutContainer.SetMarginBottom(_bar, 70f);
        return true;
    }

    private void HideBar()
    {
        _bar?.Orphan();
        _bar = null;
        _parent = null;
        _nextStatusUpdate = TimeSpan.Zero;
    }
}

public sealed class GunshipPilotTopBar : PanelContainer
{
    private static readonly Color HudColor = new(0.25f, 0.88f, 1f, 0.98f);
    private readonly Label _viewMode;
    private readonly Label _eta;
    private readonly Label _stage;
    private string? _lastEta;
    private string? _lastStage;

    public GunshipPilotTopBar(Action openNavigation)
    {
        MinSize = new Vector2(680f, 62f);
        PanelOverride = new StyleBoxFlat(new Color(0.01f, 0.035f, 0.045f, 0.90f))
        {
            BorderColor = new Color(0.25f, 0.88f, 1f, 0.75f),
            BorderThickness = new Thickness(1f),
            ContentMarginLeftOverride = 8f,
            ContentMarginRightOverride = 8f,
            ContentMarginTopOverride = 6f,
            ContentMarginBottomOverride = 6f,
        };

        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            SeparationOverride = 12,
            HorizontalExpand = true,
            VerticalExpand = true,
        };
        AddChild(row);

        var navigation = new Button
        {
            Text = "Open Navigation Controls",
            MinSize = new Vector2(210f, 40f),
            VerticalAlignment = VAlignment.Center,
        };
        navigation.OnPressed += _ => openNavigation();
        row.AddChild(navigation);

        _viewMode = new Label
        {
            Text = "PILOT VIEW",
            MinSize = new Vector2(190f, 0f),
            FontColorOverride = HudColor,
            HorizontalAlignment = HAlignment.Center,
            VerticalAlignment = VAlignment.Center,
        };
        row.AddChild(_viewMode);

        var status = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            MinSize = new Vector2(230f, 0f),
            VerticalAlignment = VAlignment.Center,
        };
        row.AddChild(status);

        _eta = new Label
        {
            Text = "ETA --",
            FontColorOverride = HudColor,
            HorizontalAlignment = HAlignment.Left,
        };
        status.AddChild(_eta);

        _stage = new Label
        {
            Text = "STAGE READY",
            FontColorOverride = HudColor,
            HorizontalAlignment = HAlignment.Left,
        };
        status.AddChild(_stage);
    }

    public void SetViewMode(string mode)
    {
        if (_viewMode.Text != mode)
            _viewMode.Text = mode;
    }

    public void SetFlightStatus(string eta, string stage)
    {
        if (_lastEta != eta)
        {
            _lastEta = eta;
            _eta.Text = $"ETA {eta}";
        }

        if (_lastStage != stage)
        {
            _lastStage = stage;
            _stage.Text = $"STAGE {stage}";
        }
    }
}

using Content.Shared._CMU14.Dropship.TacticalLand;
using Content.Shared._RMC14.Weapons.Ranged.IFF;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC.Components;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._CMU14.Dropship.TacticalLand;

/// <summary>
/// Applies a client-only IFF silhouette to living creatures while the local
/// player has a linked gunship pilot HUD.
/// </summary>
public sealed partial class GunshipPilotIffOutlineSystem : EntitySystem
{
    private static readonly ProtoId<ShaderPrototype> OutlineShader = "RMCAuraOutline";
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(250);
    private static readonly Color FriendlyColor = new(0.14f, 1f, 0.25f, 0.95f);
    private static readonly Color NeutralColor = new(1f, 0.58f, 0.08f, 0.95f);
    private static readonly Color HostileColor = new(1f, 0.08f, 0.08f, 0.98f);

    [Dependency] private IEyeManager _eye = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private ShaderInstance _friendlyShader = default!;
    private ShaderInstance _neutralShader = default!;
    private ShaderInstance _hostileShader = default!;
    private readonly Dictionary<EntityUid, HighlightState> _highlighted = new();
    private readonly HashSet<EntityUid> _seen = new();
    private readonly List<EntityUid> _remove = new();
    private readonly HashSet<EntProtoId<IFFFactionComponent>> _pilotIff = new();
    private readonly HashSet<EntProtoId<IFFFactionComponent>> _targetIff = new();
    private readonly HashSet<Entity<MobStateComponent>> _viewportMobs = new();
    private TimeSpan _nextUpdate;

    public override void Initialize()
    {
        base.Initialize();

        _friendlyShader = CreateShader(FriendlyColor);
        _neutralShader = CreateShader(NeutralColor);
        _hostileShader = CreateShader(HostileColor);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        ClearHighlights();
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (_player.LocalEntity is not { } pilot ||
            !TryComp(pilot, out GunshipPilotHudComponent? hud) ||
            hud.Dropship == null)
        {
            ClearHighlights();
            return;
        }

        if (_timing.CurTime < _nextUpdate)
            return;

        _nextUpdate = _timing.CurTime + UpdateInterval;
        RefreshHighlights(pilot, hud.Dropship.Value);
    }

    private ShaderInstance CreateShader(Color color)
    {
        var shader = _prototypes.Index(OutlineShader).InstanceUnique();
        shader.SetParameter("outline_color", color);
        shader.SetParameter("outline_width", 1.5f);
        return shader;
    }

    private void RefreshHighlights(EntityUid pilot, EntityUid dropship)
    {
        var eye = _eye.CurrentEye;
        var viewBounds = _eye.GetWorldViewbounds();
        _viewportMobs.Clear();
        _lookup.GetEntitiesIntersecting(eye.Position.MapId, viewBounds, _viewportMobs);

        _seen.Clear();
        if (_viewportMobs.Count == 0)
        {
            RemoveUnseenHighlights();
            return;
        }

        GetIffFactions(pilot, _pilotIff);

        foreach (var (uid, _) in _viewportMobs)
        {
            if (!TryComp(uid, out SpriteComponent? sprite) ||
                !sprite.Visible ||
                !TryComp(uid, out TransformComponent? xform) ||
                xform.GridUid == dropship ||
                !viewBounds.Contains(_transform.GetWorldPosition(xform)))
            {
                continue;
            }

            var shader = GetRelationshipShader(pilot, uid);
            ApplyHighlight(uid, sprite, shader);
            _seen.Add(uid);
        }

        RemoveUnseenHighlights();
    }

    private void RemoveUnseenHighlights()
    {
        _remove.Clear();
        foreach (var uid in _highlighted.Keys)
        {
            if (!_seen.Contains(uid))
                _remove.Add(uid);
        }

        foreach (var uid in _remove)
            RestoreHighlight(uid);
    }

    private ShaderInstance GetRelationshipShader(EntityUid pilot, EntityUid target)
    {
        GetIffFactions(target, _targetIff);
        if (_pilotIff.Overlaps(_targetIff))
            return _friendlyShader;

        if (TryComp(pilot, out NpcFactionMemberComponent? pilotFaction) &&
            TryComp(target, out NpcFactionMemberComponent? targetFaction))
        {
            if (pilotFaction.Factions.Overlaps(targetFaction.Factions) ||
                pilotFaction.FriendlyFactions.Overlaps(targetFaction.Factions) ||
                targetFaction.FriendlyFactions.Overlaps(pilotFaction.Factions))
            {
                return _friendlyShader;
            }

            if (pilotFaction.HostileFactions.Overlaps(targetFaction.Factions) ||
                targetFaction.HostileFactions.Overlaps(pilotFaction.Factions))
            {
                return _hostileShader;
            }
        }

        return _neutralShader;
    }

    private void GetIffFactions(EntityUid entity, HashSet<EntProtoId<IFFFactionComponent>> factions)
    {
        factions.Clear();
        var ev = new GetIFFFactionEvent(SlotFlags.IDCARD, factions);
        RaiseLocalEvent(entity, ref ev);
    }

    private void ApplyHighlight(EntityUid uid, SpriteComponent sprite, ShaderInstance shader)
    {
        if (!_highlighted.TryGetValue(uid, out var state))
        {
            state = new HighlightState(sprite, sprite.PostShader);
            _highlighted.Add(uid, state);
        }
        else if (!IsPilotShader(sprite.PostShader))
        {
            // Preserve an effect applied by another client visual system while
            // the pilot HUD was running so it can be restored afterwards.
            state = state with { OriginalShader = sprite.PostShader };
            _highlighted[uid] = state;
        }

        sprite.PostShader = shader;
    }

    private void RestoreHighlight(EntityUid uid)
    {
        if (!_highlighted.Remove(uid, out var state) || TerminatingOrDeleted(uid))
            return;

        if (IsPilotShader(state.Sprite.PostShader))
            state.Sprite.PostShader = state.OriginalShader;
    }

    private void ClearHighlights()
    {
        _remove.Clear();
        _remove.AddRange(_highlighted.Keys);
        foreach (var uid in _remove)
            RestoreHighlight(uid);
    }

    private bool IsPilotShader(ShaderInstance? shader)
    {
        return shader == _friendlyShader || shader == _neutralShader || shader == _hostileShader;
    }

    private sealed record HighlightState(
        SpriteComponent Sprite,
        ShaderInstance? OriginalShader);
}

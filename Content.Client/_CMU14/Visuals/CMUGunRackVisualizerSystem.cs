using System.Numerics;
using Content.Shared._CMU14.Visuals;
using Robust.Client.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Utility;

namespace Content.Client._CMU14.Visuals;

public sealed partial class CMUGunRackVisualizerSystem : EntitySystem
{
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private SpriteSystem _sprite = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private static readonly CMUGunRackVisualLayers[] Layers =
    [
        CMUGunRackVisualLayers.Gun1,
        CMUGunRackVisualLayers.Gun2,
    ];

    private static readonly Angle SidewaysRotation = Angle.FromDegrees(90);

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CMUGunRackVisualizerComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<CMUGunRackVisualizerComponent, EntInsertedIntoContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<CMUGunRackVisualizerComponent, EntRemovedFromContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<CMUGunRackVisualizerComponent, MoveEvent>(OnMove);
    }

    private void OnStartup(Entity<CMUGunRackVisualizerComponent> ent, ref ComponentStartup args)
    {
        Refresh(ent);
    }

    private void OnContainerChanged(Entity<CMUGunRackVisualizerComponent> ent, ref EntInsertedIntoContainerMessage args)
    {
        if (ent.Comp.Slots.Contains(args.Container.ID))
            Refresh(ent);
    }

    private void OnContainerChanged(Entity<CMUGunRackVisualizerComponent> ent, ref EntRemovedFromContainerMessage args)
    {
        if (ent.Comp.Slots.Contains(args.Container.ID))
            Refresh(ent);
    }

    private void OnMove(Entity<CMUGunRackVisualizerComponent> ent, ref MoveEvent args)
    {
        if (!args.NewRotation.EqualsApprox(args.OldRotation))
            Refresh(ent);
    }

    private void Refresh(Entity<CMUGunRackVisualizerComponent> ent)
    {
        if (!TryComp(ent, out SpriteComponent? sprite))
            return;

        // Snap-cardinal sprites choose a directional RSI frame instead of rotating the
        // whole sprite. Rotate these texture layers explicitly so they still follow it.
        var rackRotation = _transform.GetWorldRotation(ent).RoundToCardinalAngle();
        var layerCount = Math.Min(Layers.Length, ent.Comp.Slots.Count);
        for (var i = 0; i < Layers.Length; i++)
        {
            if (!_sprite.LayerMapTryGet((ent.Owner, sprite), Layers[i], out var layer, false))
                continue;

            if (i >= layerCount ||
                !_container.TryGetContainer(ent, ent.Comp.Slots[i], out var container) ||
                container.ContainedEntities.Count == 0 ||
                !TryComp(container.ContainedEntities[0], out MetaDataComponent? metadata) ||
                metadata.EntityPrototype is not { } prototype)
            {
                _sprite.LayerSetVisible((ent.Owner, sprite), layer, false);
                continue;
            }

            var icon = _sprite.Frame0(new SpriteSpecifier.EntityPrototype(prototype.ID));
            _sprite.LayerSetTexture((ent.Owner, sprite), layer, icon);
            _sprite.LayerSetScale((ent.Owner, sprite), layer, ent.Comp.Scale);
            _sprite.LayerSetRotation((ent.Owner, sprite), layer, SidewaysRotation + rackRotation);
            _sprite.LayerSetOffset((ent.Owner, sprite), layer,
                rackRotation.RotateVec(i < ent.Comp.Offsets.Count ? ent.Comp.Offsets[i] : Vector2.Zero));
            _sprite.LayerSetVisible((ent.Owner, sprite), layer, true);
        }
    }
}

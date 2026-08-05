using System.Collections.Generic;
using System.Numerics;
using Content.Client.Movement.Components;
using Content.Client.Resources;
using Content.Shared._CMU14.Dropship.Integrity;
using Content.Shared._CMU14.Dropship.TacticalLand;
using Content.Shared.Eye;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Shared.ComponentTrees;
using Robust.Shared.Enums;
using Robust.Shared.IoC;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Graphics;
using Robust.Shared.Timing;

namespace Content.Client._CMU14.Dropship.TacticalLand;

public sealed partial class GunshipPilotCameraSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlay = default!;
    [Dependency] private IEyeManager _eye = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private OccluderSystem _occluder = default!;

    private static readonly TimeSpan HullOccluderRefreshInterval = TimeSpan.FromSeconds(0.5);
    private const float PilotCursorMaxOffset = 24f;
    private const float PilotCursorPvsIncrease = 2.4f;
    private const float PilotMaximumZoom = 2.25f;
    private readonly HashSet<EntityUid> _suppressedHullOccluders = new();
    private EntityUid? _suppressedGrid;
    private TimeSpan _nextHullOccluderRefresh;
    private IEye? _zoomedEye;
    private Vector2 _pilotBaseZoom;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesAfter.Add(typeof(OccluderSystem));
        _overlay.AddOverlay(new GunshipPilotOverlay(EntityManager, _player));
        _overlay.AddOverlay(new GunshipPilotHudOverlay(EntityManager, _player));
    }

    public override void Shutdown()
    {
        RestorePilotZoom();
        RestoreHullOccluders();
        base.Shutdown();
        _overlay.RemoveOverlay<GunshipPilotOverlay>();
        _overlay.RemoveOverlay<GunshipPilotHudOverlay>();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        UpdatePilotHullOcclusion();
        UpdatePilotZoom();
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        // Component state application can legitimately requeue these entries.
        // Remove them from the client rendering tree again without changing
        // any networked component data.
        foreach (var uid in _suppressedHullOccluders)
        {
            if (!TerminatingOrDeleted(uid) &&
                TryComp(uid, out OccluderComponent? occluder) &&
                occluder.Enabled)
            {
                RemoveFromClientOccluderTree(occluder);
            }
        }
    }

    private void UpdatePilotHullOcclusion()
    {
        EntityUid? desiredGrid = null;
        if (_player.LocalEntity is { } local &&
            TryComp(local, out GunshipPilotHudComponent? hud) &&
            hud.Dropship is { } dropship &&
            hud.ViewOffset == 0 &&
            !hud.RearView &&
            !hud.Malfunctions.Contains(DropshipMalfunction.SensorArrayFault))
        {
            desiredGrid = dropship;

            // This component is split between client and server, so these
            // pilot-specific values must also be applied to the local copy.
            if (TryComp(local, out EyeCursorOffsetComponent? cursor))
            {
                cursor.MaxOffset = PilotCursorMaxOffset;
                cursor.OffsetSpeed = 0.35f;
                cursor.PvsIncrease = PilotCursorPvsIncrease;
            }
        }

        if (_suppressedGrid != desiredGrid)
        {
            RestoreHullOccluders();
            _suppressedGrid = desiredGrid;
            _nextHullOccluderRefresh = TimeSpan.Zero;
        }

        if (desiredGrid is not { } grid || _timing.CurTime < _nextHullOccluderRefresh)
            return;

        _nextHullOccluderRefresh = _timing.CurTime + HullOccluderRefreshInterval;
        var children = Transform(grid).ChildEnumerator;
        while (children.MoveNext(out var child))
        {
            if (!TryComp(child, out OccluderComponent? occluder) ||
                !occluder.Enabled)
            {
                continue;
            }

            _suppressedHullOccluders.Add(child);
            RemoveFromClientOccluderTree(occluder);
        }
    }

    private void UpdatePilotZoom()
    {
        if (_player.LocalEntity is not { } local ||
            !TryComp(local, out GunshipPilotHudComponent? hud) ||
            hud.Dropship == null ||
            hud.ViewOffset != 0 ||
            hud.RearView ||
            hud.Malfunctions.Contains(DropshipMalfunction.SensorArrayFault) ||
            !TryComp(local, out EyeCursorOffsetComponent? cursor))
        {
            RestorePilotZoom();
            return;
        }

        var eye = _eye.CurrentEye;
        if (!ReferenceEquals(_zoomedEye, eye))
        {
            RestorePilotZoom();
            _zoomedEye = eye;
            _pilotBaseZoom = eye.Zoom;
        }

        var distance = Math.Clamp(cursor.CurrentPosition.Length() / PilotCursorMaxOffset, 0f, 1f);
        var multiplier = MathHelper.Lerp(1f, PilotMaximumZoom, distance);
        eye.Zoom = _pilotBaseZoom * multiplier;
    }

    private void RestorePilotZoom()
    {
        if (_zoomedEye == null)
            return;

        _zoomedEye.Zoom = _pilotBaseZoom;
        _zoomedEye = null;
    }

    private static void RemoveFromClientOccluderTree(OccluderComponent occluder)
    {
        IComponentTreeEntry<OccluderComponent> treeEntry = occluder;
        if (treeEntry.Tree == null)
            return;

        treeEntry.Tree.Remove(new ComponentTreeEntry<OccluderComponent> { Component = occluder });
        treeEntry.Tree = null;
        treeEntry.TreeUid = null;
    }

    private void RestoreHullOccluders()
    {
        foreach (var uid in _suppressedHullOccluders)
        {
            if (!TerminatingOrDeleted(uid) && TryComp(uid, out OccluderComponent? occluder))
                _occluder.QueueTreeUpdate(uid, occluder);
        }

        _suppressedHullOccluders.Clear();
        _suppressedGrid = null;
    }
}

public sealed class GunshipPilotHudOverlay : Overlay
{
    private readonly IEntityManager _entities;
    private readonly IPlayerManager _player;
    private readonly IGameTiming _timing;
    private readonly Font _font;
    private readonly Font _smallFont;

    private static readonly Color HudColor = new(0.25f, 0.88f, 1f, 0.95f);
    private static readonly Color HudBackground = new(0.015f, 0.06f, 0.08f, 0.78f);
    private static readonly Color HudDim = new(0.25f, 0.88f, 1f, 0.38f);
    private static readonly Color IntegrityColor = new(0.92f, 0.08f, 0.08f, 0.95f);
    private static readonly Color IntegrityBorder = new(1f, 0.22f, 0.22f, 0.8f);
    private static readonly Color ThrustColor = new(0.20f, 0.78f, 1f, 0.95f);
    private static readonly Color ThrustBorder = new(0.35f, 0.9f, 1f, 0.8f);
    private static readonly Color AmmoColor = new(1f, 0.72f, 0.16f, 0.95f);
    private static readonly Color MalfunctionColor = new(1f, 0.24f, 0.12f, 0.98f);

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    public GunshipPilotHudOverlay(IEntityManager entities, IPlayerManager player)
    {
        _entities = entities;
        _player = player;
        _timing = IoCManager.Resolve<IGameTiming>();
        var cache = IoCManager.Resolve<IResourceCache>();
        _font = cache.GetFont("/Fonts/NotoSans/NotoSans-Bold.ttf", 18);
        _smallFont = cache.GetFont("/Fonts/NotoSans/NotoSans-Bold.ttf", 12);
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        return _player.LocalEntity is { } local &&
               _entities.HasComponent<GunshipPilotHudComponent>(local);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (_player.LocalEntity is not { } local ||
            !_entities.TryGetComponent(local, out GunshipPilotHudComponent? hud))
        {
            return;
        }

        var handle = args.ScreenHandle;
        var bounds = args.ViewportBounds;

        if (hud.Dropship == null)
        {
            const string message = "No dropship controls linked.";
            var measured = handle.DrawString(_font, Vector2.Zero, message, Color.Transparent);
            var position = new Vector2(bounds.Left + (bounds.Width - measured.X) * 0.5f, bounds.Top + 18f);
            handle.DrawString(_font, position + Vector2.One * 2f, message, Color.Black);
            handle.DrawString(_font, position, message, HudColor);
            return;
        }

        DrawDriftIndicator(handle, bounds, hud);
        DrawThrustBar(handle, bounds, hud);
        DrawIntegrityBar(handle, bounds, hud);
        DrawDirectFireAmmo(handle, bounds, hud);
        DrawWarnings(handle, bounds, hud);

        var mode = hud.RearView ? "REAR CAMERA" : hud.ViewOffset switch
        {
            > 0 => "UPPER CAMERA",
            < 0 => "LOWER CAMERA",
            _ => "PILOT VIEW",
        };
        var modeSize = handle.DrawString(_smallFont, Vector2.Zero, mode, Color.Transparent);
        var modePosition = new Vector2(bounds.Left + (bounds.Width - modeSize.X) * 0.5f, bounds.Top + 14f);
        handle.DrawString(_smallFont, modePosition + Vector2.One, mode, Color.Black);
        handle.DrawString(_smallFont, modePosition, mode, HudColor);
    }

    private void DrawDriftIndicator(
        DrawingHandleScreen handle,
        UIBox2i bounds,
        GunshipPilotHudComponent hud)
    {
        const float size = 116f;
        const float margin = 24f;
        var left = bounds.Right - margin - size;
        var top = bounds.Top + (bounds.Height - size) * 0.5f;
        var box = new UIBox2(left, top, left + size, top + size);
        var center = box.Center + new Vector2(0f, 8f);

        handle.DrawRect(box, HudBackground);
        handle.DrawRect(box, HudDim, false);
        handle.DrawLine(new Vector2(center.X - 38f, center.Y), new Vector2(center.X + 38f, center.Y), HudDim);
        handle.DrawLine(new Vector2(center.X, center.Y - 38f), new Vector2(center.X, center.Y + 38f), HudDim);

        var labelPosition = new Vector2(left + 8f, top + 5f);
        handle.DrawString(_smallFont, labelPosition, "DRIFT", HudColor);

        var localVelocity = Angle.FromDegrees(-hud.ShipRotationDegrees).RotateVec(hud.LinearVelocity);
        var speed = localVelocity.Length();
        if (speed < 0.01f)
        {
            handle.DrawCircle(center, 4f, HudColor);
            return;
        }

        var direction = Vector2.Normalize(new Vector2(localVelocity.X, -localVelocity.Y));
        var length = 16f + 28f * Math.Clamp(speed / 8f, 0f, 1f);
        var tip = center + direction * length;
        var side = new Vector2(-direction.Y, direction.X);
        handle.DrawLine(center, tip, HudColor);
        handle.DrawLine(tip, tip - direction * 10f + side * 6f, HudColor);
        handle.DrawLine(tip, tip - direction * 10f - side * 6f, HudColor);
    }

    private void DrawIntegrityBar(
        DrawingHandleScreen handle,
        UIBox2i bounds,
        GunshipPilotHudComponent hud)
    {
        const float width = 320f;
        const float height = 24f;
        const float bottomMargin = 24f;
        var left = bounds.Left + (bounds.Width - width) * 0.5f;
        var top = bounds.Bottom - bottomMargin - height;
        var box = new UIBox2(left, top, left + width, top + height);
        var ratio = hud.MaxIntegrity > 0f
            ? Math.Clamp(hud.Integrity / hud.MaxIntegrity, 0f, 1f)
            : 0f;

        handle.DrawRect(box, HudBackground);
        if (ratio > 0f)
        {
            var fill = new UIBox2(box.Left + 3f,
                box.Top + 3f,
                box.Left + 3f + (box.Width - 6f) * ratio,
                box.Bottom - 3f);
            handle.DrawRect(fill, IntegrityColor);
        }

        handle.DrawRect(box, IntegrityBorder, false);

        var text = $"HULL {MathF.Round(ratio * 100f):0}%";
        var textSize = handle.DrawString(_smallFont, Vector2.Zero, text, Color.Transparent);
        var textPosition = box.Center - textSize / 2f;
        handle.DrawString(_smallFont, textPosition + Vector2.One, text, Color.Black);
        handle.DrawString(_smallFont, textPosition, text, Color.White);
    }

    private void DrawThrustBar(
        DrawingHandleScreen handle,
        UIBox2i bounds,
        GunshipPilotHudComponent hud)
    {
        const float driftSize = 116f;
        const float margin = 24f;
        const float gap = 8f;
        const float height = 22f;
        var left = bounds.Right - margin - driftSize;
        var driftTop = bounds.Top + (bounds.Height - driftSize) * 0.5f;
        var top = driftTop + driftSize + gap;
        var box = new UIBox2(left, top, left + driftSize, top + height);
        var ratio = Math.Clamp(hud.ThrustPercent / 100f, 0f, 1f);

        handle.DrawRect(box, HudBackground);
        if (ratio > 0f)
        {
            var fill = new UIBox2(box.Left + 3f,
                box.Top + 3f,
                box.Left + 3f + (box.Width - 6f) * ratio,
                box.Bottom - 3f);
            handle.DrawRect(fill, ThrustColor);
        }

        handle.DrawRect(box, ThrustBorder, false);

        var text = $"THRUST {MathF.Round(ratio * 100f):0}%";
        var textSize = handle.DrawString(_smallFont, Vector2.Zero, text, Color.Transparent);
        var textPosition = box.Center - textSize / 2f;
        handle.DrawString(_smallFont, textPosition + Vector2.One, text, Color.Black);
        handle.DrawString(_smallFont, textPosition, text, Color.White);
    }

    private void DrawDirectFireAmmo(
        DrawingHandleScreen handle,
        UIBox2i bounds,
        GunshipPilotHudComponent hud)
    {
        if (!hud.HasDirectFireWeapon)
            return;

        const float width = 116f;
        const float margin = 24f;
        const float driftSize = 116f;
        const float hullGap = 8f;
        const float hullHeight = 22f;
        const float gap = 6f;
        const float height = 22f;
        var left = bounds.Right - margin - width;
        var driftTop = bounds.Top + (bounds.Height - driftSize) * 0.5f;
        var top = driftTop + driftSize + hullGap + hullHeight + gap;
        var box = new UIBox2(left, top, left + width, top + height);

        handle.DrawRect(box, HudBackground);
        handle.DrawRect(box, AmmoColor, false);

        var count = Math.Max(0, hud.DirectFireAmmo);
        var text = $"DIRECT AMMO {count}";
        var textSize = handle.DrawString(_smallFont, Vector2.Zero, text, Color.Transparent);
        var textPosition = box.Center - textSize / 2f;
        handle.DrawString(_smallFont, textPosition + Vector2.One, text, Color.Black);
        handle.DrawString(_smallFont, textPosition, text, AmmoColor);
    }

    private void DrawWarnings(
        DrawingHandleScreen handle,
        UIBox2i bounds,
        GunshipPilotHudComponent hud)
    {
        if (hud.Malfunctions.Count == 0 && hud.Alarms.Count == 0)
            return;

        const float margin = 24f;
        const float width = 260f;
        const float lineHeight = 20f;
        const float padding = 10f;
        var warningLines = hud.Malfunctions.Count + hud.Alarms.Count;
        if (hud.MasterAlarmSilenced && hud.Alarms.Count > 0)
            warningLines++;

        var height = padding * 2f + lineHeight * (warningLines + 1);
        var box = new UIBox2(bounds.Right - margin - width,
            bounds.Bottom - margin - height,
            bounds.Right - margin,
            bounds.Bottom - margin);

        var blinkOn = (int)(_timing.CurTime.TotalSeconds * 2) % 2 == 0;
        var warningColor = MalfunctionColor.WithAlpha(blinkOn ? 0.98f : 0.32f);
        handle.DrawRect(box, HudBackground);
        handle.DrawRect(box, warningColor, false);
        handle.DrawString(_smallFont, new Vector2(box.Left + padding, box.Top + padding), "SYSTEM WARNINGS", warningColor);

        var line = 1;
        foreach (var alarm in hud.Alarms)
        {
            handle.DrawString(_smallFont,
                new Vector2(box.Left + padding, box.Top + padding + lineHeight * line++),
                DropshipAlarmData.GetAlertName(alarm),
                warningColor);
        }

        foreach (var malfunction in hud.Malfunctions)
        {
            var alert = $"{DropshipMalfunctionData.GetAlertName(malfunction)} detected.";
            handle.DrawString(_smallFont,
                new Vector2(box.Left + padding, box.Top + padding + lineHeight * line++),
                alert,
                warningColor);
        }

        if (hud.MasterAlarmSilenced && hud.Alarms.Count > 0)
        {
            handle.DrawString(_smallFont,
                new Vector2(box.Left + padding, box.Top + padding + lineHeight * line),
                "MASTER ALARM SILENCED",
                HudColor);
        }
    }
}

public sealed class GunshipPilotOverlay : Overlay
{
    private readonly IEntityManager _entities;
    private readonly IPlayerManager _player;
    private readonly IGameTiming _timing;
    private readonly HashSet<Vector2i> _tiles = new();
    private readonly List<(Vector2 LocalCenter, Vector2 Size)> _maskSpans = new();
    private EntityUid? _cachedTileGrid;
    private TimeSpan _nextTileRefresh;
    private Vector2i _tileMin;
    private Vector2i _tileMax;

    private static readonly Color HullMask = Color.Black;
    private static readonly Color Fill = new(0.10f, 0.72f, 1f, 0.07f);
    private static readonly Color Edge = new(0.25f, 0.88f, 1f, 0.92f);
    private static readonly Color Heading = new(0.72f, 0.96f, 1f, 0.98f);
    private static readonly Color CollisionFill = new(1f, 0.04f, 0.02f, 0.20f);
    private static readonly Color CollisionEdge = new(1f, 0.16f, 0.08f, 0.98f);

    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    public GunshipPilotOverlay(IEntityManager entities, IPlayerManager player)
    {
        _entities = entities;
        _player = player;
        _timing = IoCManager.Resolve<IGameTiming>();
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (_player.LocalEntity is not { } local ||
            !_entities.TryGetComponent(local, out GunshipPilotHudComponent? hud) ||
            hud.Dropship is not { } linkedDropship)
        {
            return;
        }

        if (hud.ViewOffset == 0 &&
            !hud.RearView &&
            !hud.Malfunctions.Contains(DropshipMalfunction.SensorArrayFault) &&
            _entities.TryGetComponent(linkedDropship, out MapGridComponent? linkedGrid))
        {
            DrawHullMask(args.WorldHandle, linkedDropship, linkedGrid);
        }

        if (hud.ViewOffset == 0 &&
            !hud.RearView &&
            _entities.TryGetComponent(linkedDropship, out DropshipIntegrityComponent? integrity) &&
            integrity.ProximityAlarmActive)
        {
            DrawCollisionHazards(args.WorldHandle, integrity.ProximityHazards);
        }

        if (!_entities.TryGetComponent(local, out EyeComponent? eye) ||
            eye.Target is not { } eyeUid ||
            !_entities.TryGetComponent(eyeUid, out GunshipPilotEyeComponent? gunshipEye) ||
            gunshipEye.ViewOffset != -1 ||
            gunshipEye.Dropship is not { } dropship ||
            !_entities.TryGetComponent(dropship, out MapGridComponent? dropshipGrid) ||
            !_entities.TryGetComponent(eyeUid, out TransformComponent? eyeXform))
        {
            return;
        }

        var transform = _entities.System<SharedTransformSystem>();
        var map = _entities.System<SharedMapSystem>();
        var center = transform.GetWorldPosition(eyeXform);
        var rotation = Angle.FromDegrees(gunshipEye.RotationDegrees);
        var handle = args.WorldHandle;

        _tiles.Clear();
        foreach (var tile in map.GetAllTiles(dropship, dropshipGrid))
            _tiles.Add(tile.GridIndices);

        var tileSize = dropshipGrid.TileSize;
        var halfTile = tileSize / 2f;
        var tileBox = Box2.CenteredAround(Vector2.Zero, new Vector2(tileSize, tileSize));

        foreach (var tile in _tiles)
        {
            var localCenter = map.TileCenterToVector(dropship, dropshipGrid, tile);
            var worldCenter = center + rotation.RotateVec(localCenter);
            handle.DrawRect(new Box2Rotated(tileBox.Translated(worldCenter), rotation, worldCenter), Fill);

            DrawBoundary(handle, tile, localCenter, rotation, center, halfTile);
        }

        var forward = rotation.RotateVec(Vector2.UnitY);
        var side = new Vector2(-forward.Y, forward.X);
        var shaftStart = center + forward * 0.65f;
        var tip = center + forward * 1.65f;
        handle.DrawLine(shaftStart, tip, Heading);
        handle.DrawLine(tip, tip - forward * 0.42f + side * 0.28f, Heading);
        handle.DrawLine(tip, tip - forward * 0.42f - side * 0.28f, Heading);
        handle.DrawCircle(center, 0.24f, Heading, false);
    }

    private void DrawHullMask(DrawingHandleWorld handle, EntityUid gridUid, MapGridComponent grid)
    {
        var map = _entities.System<SharedMapSystem>();
        if (_cachedTileGrid != gridUid || _timing.CurTime >= _nextTileRefresh)
        {
            _cachedTileGrid = gridUid;
            _nextTileRefresh = _timing.CurTime + TimeSpan.FromSeconds(1);
            _tiles.Clear();
            _maskSpans.Clear();
            _tileMin = new Vector2i(int.MaxValue, int.MaxValue);
            _tileMax = new Vector2i(int.MinValue, int.MinValue);

            foreach (var tile in map.GetAllTiles(gridUid, grid))
            {
                var indices = tile.GridIndices;
                _tiles.Add(indices);
                _tileMin = Vector2i.ComponentMin(_tileMin, indices);
                _tileMax = Vector2i.ComponentMax(_tileMax, indices);
            }

            if (_tiles.Count > 0)
                CacheHullMaskSpans(map, gridUid, grid);
        }

        if (_tiles.Count == 0)
            return;

        var transform = _entities.System<SharedTransformSystem>();
        var gridCenter = transform.GetWorldPosition(gridUid);
        var rotation = transform.GetWorldRotation(gridUid);
        foreach (var (localCenter, size) in _maskSpans)
        {
            var worldCenter = gridCenter + rotation.RotateVec(localCenter);
            var box = Box2.CenteredAround(worldCenter, size);
            handle.DrawRect(new Box2Rotated(box, rotation, worldCenter), HullMask);
        }
    }

    private void CacheHullMaskSpans(SharedMapSystem map, EntityUid gridUid, MapGridComponent grid)
    {
        var tileSize = grid.TileSize;
        for (var y = _tileMin.Y; y <= _tileMax.Y; y++)
        {
            int? runStart = null;
            for (var x = _tileMin.X; x <= _tileMax.X + 1; x++)
            {
                var occupied = x <= _tileMax.X && _tiles.Contains(new Vector2i(x, y));
                if (occupied && runStart == null)
                {
                    runStart = x;
                    continue;
                }

                if (occupied || runStart is not { } start)
                    continue;

                var end = x - 1;
                var first = map.TileCenterToVector(gridUid, grid, new Vector2i(start, y));
                var last = map.TileCenterToVector(gridUid, grid, new Vector2i(end, y));
                var localCenter = (first + last) * 0.5f;
                var size = new Vector2((end - start + 1) * tileSize + 0.02f, tileSize + 0.02f);
                _maskSpans.Add((localCenter, size));
                runStart = null;
            }
        }
    }

    private static void DrawCollisionHazards(DrawingHandleWorld handle, List<Vector2> hazards)
    {
        foreach (var position in hazards)
        {
            var box = Box2.CenteredAround(position, Vector2.One);
            handle.DrawRect(box, CollisionFill);
            handle.DrawRect(box, CollisionEdge, false);
            handle.DrawRect(box.Enlarged(0.08f), CollisionEdge.WithAlpha(0.45f), false);
        }
    }

    private void DrawBoundary(
        DrawingHandleWorld handle,
        Vector2i tile,
        Vector2 localCenter,
        Angle rotation,
        Vector2 worldCenter,
        float halfTile)
    {
        if (!_tiles.Contains(tile + Vector2i.Left))
            DrawLocalLine(handle, localCenter + new Vector2(-halfTile, -halfTile), localCenter + new Vector2(-halfTile, halfTile), rotation, worldCenter);

        if (!_tiles.Contains(tile + Vector2i.Right))
            DrawLocalLine(handle, localCenter + new Vector2(halfTile, -halfTile), localCenter + new Vector2(halfTile, halfTile), rotation, worldCenter);

        if (!_tiles.Contains(tile + Vector2i.Down))
            DrawLocalLine(handle, localCenter + new Vector2(-halfTile, -halfTile), localCenter + new Vector2(halfTile, -halfTile), rotation, worldCenter);

        if (!_tiles.Contains(tile + Vector2i.Up))
            DrawLocalLine(handle, localCenter + new Vector2(-halfTile, halfTile), localCenter + new Vector2(halfTile, halfTile), rotation, worldCenter);
    }

    private static void DrawLocalLine(
        DrawingHandleWorld handle,
        Vector2 localFrom,
        Vector2 localTo,
        Angle rotation,
        Vector2 worldCenter)
    {
        handle.DrawLine(
            worldCenter + rotation.RotateVec(localFrom),
            worldCenter + rotation.RotateVec(localTo),
            Edge);
    }
}

using Content.Client.Decals.Overlays;
using Content.Shared.Decals;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.GameStates;
using Robust.Shared.Utility;
using static Content.Shared.Decals.DecalGridComponent;

namespace Content.Client.Decals
{
    public sealed partial class DecalSystem : SharedDecalSystem
    {
        [Dependency] private IOverlayManager _overlayManager = default!;
        [Dependency] private SpriteSystem _sprites = default!;

        private DecalOverlay? _overlay;

        private readonly HashSet<uint> _removedUids = new();
        private readonly List<Vector2i> _removedChunks = new();

        public override void Initialize()
        {
            base.Initialize();

            _overlay = new DecalOverlay(_sprites, EntityManager, PrototypeManager);
            _overlayManager.AddOverlay(_overlay);

            SubscribeLocalEvent<DecalGridComponent, ComponentHandleState>(OnHandleState);
            SubscribeNetworkEvent<DecalChunkUpdateEvent>(OnChunkUpdate);
        }

        public void ToggleOverlay()
        {
            if (_overlay == null)
                return;

            if (_overlayManager.HasOverlay<DecalOverlay>())
            {
                _overlayManager.RemoveOverlay(_overlay);
            }
            else
            {
                _overlayManager.AddOverlay(_overlay);
            }
        }

        public override void Shutdown()
        {
            base.Shutdown();

            if (_overlay == null)
                return;

            _overlayManager.RemoveOverlay(_overlay);
        }

        protected override void OnDecalRemoved(EntityUid gridId, uint decalId, DecalGridComponent component, Vector2i indices, DecalChunk chunk)
        {
            base.OnDecalRemoved(gridId, decalId, component, indices, chunk);
            DebugTools.Assert(chunk.Decals.ContainsKey(decalId));
            chunk.Decals.Remove(decalId);
        }

        private void OnHandleState(EntityUid gridUid, DecalGridComponent gridComp, ref ComponentHandleState args)
        {
            // is this a delta or full state?
            _removedChunks.Clear();
            Dictionary<Vector2i, DecalChunk> modifiedChunks;

            switch (args.Current)
            {
                case DecalGridDeltaState delta:
                {
                    modifiedChunks = delta.ModifiedChunks;
                    foreach (var key in gridComp.ChunkCollection.ChunkCollection.Keys)
                    {
                        if (!delta.AllChunks.Contains(key))
                            _removedChunks.Add(key);
                    }

                    break;
                }
                case DecalGridState state:
                {
                    modifiedChunks = state.Chunks;
                    foreach (var key in gridComp.ChunkCollection.ChunkCollection.Keys)
                    {
                        if (!state.Chunks.ContainsKey(key))
                            _removedChunks.Add(key);
                    }

                    break;
                }
                default:
                    return;
            }

            if (_removedChunks.Count > 0)
                RemoveChunks(gridUid, gridComp, _removedChunks);

            if (modifiedChunks.Count > 0)
                UpdateChunks(gridUid, gridComp, modifiedChunks);
        }

        private void OnChunkUpdate(DecalChunkUpdateEvent ev)
        {
            foreach (var (netGrid, updatedGridChunks) in ev.Data)
            {
                if (updatedGridChunks.Count == 0)
                    continue;

                var gridId = GetEntity(netGrid);

                if (!TryComp(gridId, out DecalGridComponent? gridComp))
                {
                    Log.Error($"Received decal information for an entity without a decal component: {ToPrettyString(gridId)}");
                    continue;
                }

                UpdateChunks(gridId, gridComp, updatedGridChunks);
            }

            foreach (var (netGrid, gridDeltas) in ev.Deltas)
            {
                if (gridDeltas.Count == 0)
                    continue;

                var gridId = GetEntity(netGrid);

                if (!TryComp(gridId, out DecalGridComponent? gridComp))
                {
                    Log.Error($"Received decal information for an entity without a decal component: {ToPrettyString(gridId)}");
                    continue;
                }

                ApplyDeltas(gridId, gridComp, gridDeltas);
            }

            // Now we'll cull old chunks out of range as the server will send them to us anyway.
            foreach (var (netGrid, chunks) in ev.RemovedChunks)
            {
                if (chunks.Count == 0)
                    continue;

                var gridId = GetEntity(netGrid);

                if (!TryComp(gridId, out DecalGridComponent? gridComp))
                {
                    Log.Error($"Received decal information for an entity without a decal component: {ToPrettyString(gridId)}");
                    continue;
                }

                RemoveChunks(gridId, gridComp, chunks);
            }
        }

        private void UpdateChunks(EntityUid gridId, DecalGridComponent gridComp, Dictionary<Vector2i, DecalChunk> updatedGridChunks)
        {
            var chunkCollection = gridComp.ChunkCollection.ChunkCollection;

            // Update any existing data / remove decals we didn't receive data for.
            foreach (var (indices, newChunkData) in updatedGridChunks)
            {
                if (chunkCollection.TryGetValue(indices, out var chunk))
                {
                    _removedUids.Clear();
                    _removedUids.UnionWith(chunk.Decals.Keys);
                    _removedUids.ExceptWith(newChunkData.Decals.Keys);
                    foreach (var removedUid in _removedUids)
                    {
                        RemoveDecalLocally(gridId, gridComp, indices, removedUid);
                    }
                }

                foreach (var uid in newChunkData.Decals.Keys)
                {
                    if (gridComp.DecalIndex.TryGetValue(uid, out var oldIndices) && oldIndices != indices)
                        RemoveDecalLocally(gridId, gridComp, oldIndices, uid);
                }

                if (newChunkData.Decals.Count == 0)
                {
                    chunkCollection.Remove(indices);
                    continue;
                }

                chunkCollection[indices] = newChunkData;

                foreach (var uid in newChunkData.Decals.Keys)
                {
                    gridComp.DecalIndex[uid] = indices;
                }
            }
        }

        private void ApplyDeltas(
            EntityUid gridId,
            DecalGridComponent gridComp,
            Dictionary<Vector2i, DecalChunkDelta> gridDeltas)
        {
            foreach (var (indices, delta) in gridDeltas)
            {
                if (delta.Cleared)
                    ClearChunk(gridId, gridComp, indices);
            }

            // Apply removals first so a decal can move between chunks in one update regardless of dictionary order.
            foreach (var (indices, delta) in gridDeltas)
            {
                foreach (var decalId in delta.RemovedDecals)
                {
                    RemoveDecalLocally(gridId, gridComp, indices, decalId);
                }
            }

            foreach (var (indices, delta) in gridDeltas)
            {
                foreach (var (decalId, decal) in delta.ModifiedDecals)
                {
                    UpsertDecal(gridId, gridComp, indices, decalId, decal);
                }
            }
        }

        private void UpsertDecal(
            EntityUid gridId,
            DecalGridComponent gridComp,
            Vector2i indices,
            uint decalId,
            Decal decal)
        {
            if (gridComp.DecalIndex.TryGetValue(decalId, out var oldIndices) && oldIndices != indices)
                RemoveDecalLocally(gridId, gridComp, oldIndices, decalId);

            var chunk = gridComp.ChunkCollection.ChunkCollection.GetOrNew(indices);
            chunk.Decals[decalId] = decal;
            gridComp.DecalIndex[decalId] = indices;
        }

        private void RemoveDecalLocally(
            EntityUid gridId,
            DecalGridComponent gridComp,
            Vector2i expectedIndices,
            uint decalId)
        {
            var indices = expectedIndices;
            if (gridComp.DecalIndex.Remove(decalId, out var indexedChunk))
                indices = indexedChunk;

            var chunkCollection = gridComp.ChunkCollection.ChunkCollection;
            if (!chunkCollection.TryGetValue(indices, out var chunk) || !chunk.Decals.ContainsKey(decalId))
                return;

            OnDecalRemoved(gridId, decalId, gridComp, indices, chunk);
            if (chunk.Decals.Count == 0)
                chunkCollection.Remove(indices);
        }

        private void ClearChunk(EntityUid gridId, DecalGridComponent gridComp, Vector2i indices)
        {
            var chunkCollection = gridComp.ChunkCollection.ChunkCollection;
            if (!chunkCollection.TryGetValue(indices, out var chunk))
                return;

            _removedUids.Clear();
            _removedUids.UnionWith(chunk.Decals.Keys);
            foreach (var decalId in _removedUids)
            {
                OnDecalRemoved(gridId, decalId, gridComp, indices, chunk);
                gridComp.DecalIndex.Remove(decalId);
            }

            chunkCollection.Remove(indices);
        }

        private void RemoveChunks(EntityUid gridId, DecalGridComponent gridComp, IEnumerable<Vector2i> chunks)
        {
            foreach (var index in chunks)
            {
                ClearChunk(gridId, gridComp, index);
            }
        }
    }
}

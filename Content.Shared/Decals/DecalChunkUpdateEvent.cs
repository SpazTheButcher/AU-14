using Robust.Shared.Serialization;
using static Content.Shared.Decals.DecalGridComponent;

namespace Content.Shared.Decals
{
    [Serializable, NetSerializable]
    public sealed class DecalChunkDelta
    {
        public bool Cleared;
        public Dictionary<uint, Decal> ModifiedDecals = new();
        public HashSet<uint> RemovedDecals = new();
    }

    [Serializable, NetSerializable]
    public sealed class DecalChunkUpdateEvent : EntityEventArgs
    {
        /// <summary>
        ///     Full snapshots for chunks that have just entered a client's PVS.
        /// </summary>
        public Dictionary<NetEntity, Dictionary<Vector2i, DecalChunk>> Data = new();

        /// <summary>
        ///     Incremental changes for chunks that were already in a client's PVS.
        /// </summary>
        public Dictionary<NetEntity, Dictionary<Vector2i, DecalChunkDelta>> Deltas = new();

        /// <summary>
        ///     Chunks that have left a client's PVS and should be discarded locally.
        /// </summary>
        public Dictionary<NetEntity, HashSet<Vector2i>> RemovedChunks = new();
    }
}

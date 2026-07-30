using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Shared.Decals;
using Robust.Shared;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.UnitTesting;
using ServerDecalSystem = Content.Server.Decals.DecalSystem;

namespace Content.IntegrationTests.Tests.Decals;

[TestFixture]
[TestOf(typeof(ServerDecalSystem))]
public sealed class DecalReplicationTest
{
    private static readonly Vector2i Chunk = Vector2i.Zero;

    [Test]
    public async Task ExistingChunkMutationsOnlyReplicateChangedDecal()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings
        {
            Connected = true,
            Dirty = true,
        });
        var server = pair.Server;
        var client = pair.Client;
        var updates = new List<DecalChunkUpdateEvent>();

        void OnSystemMessage(object _, object message)
        {
            if (message is DecalChunkUpdateEvent update)
                updates.Add(update);
        }

        await client.WaitPost(() => client.EntMan.EntityNetManager.ReceivedSystemMessage += OnSystemMessage);
        try
        {
            await server.WaitPost(() => server.CfgMan.SetCVar(CVars.NetPVS, true));
            var map = await pair.CreateTestMap();
            var netGrid = server.EntMan.GetNetEntity(map.Grid.Owner);
            var player = server.PlayerMan.Sessions.Single();
            uint baselineA = default;
            uint baselineB = default;
            uint baselineC = default;

            await server.WaitAssertion(() =>
            {
                var entMan = server.EntMan;
                var decals = entMan.System<ServerDecalSystem>();
                entMan.EnsureComponent<DecalGridComponent>(map.Grid.Owner);

                Assert.Multiple(() =>
                {
                    Assert.That(decals.TryAddDecal("burnt1", Coordinates(map.Grid.Owner, 0.1f), out baselineA), Is.True);
                    Assert.That(decals.TryAddDecal("burnt1", Coordinates(map.Grid.Owner, 0.2f), out baselineB), Is.True);
                    Assert.That(decals.TryAddDecal("burnt1", Coordinates(map.Grid.Owner, 0.3f), out baselineC), Is.True);
                });

                var viewer = entMan.SpawnEntity(null, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
                server.PlayerMan.SetAttachedEntity(player, viewer);
                Assert.That(player.AttachedEntity, Is.EqualTo(viewer));
            });
            await pair.RunTicksSync(5);

            var baselineIds = new[] { baselineA, baselineB, baselineC };
            var initial = GetOnlyFullChunkUpdate(updates, netGrid);
            Assert.That(initial.Decals.Keys, Is.EquivalentTo(baselineIds));
            await AssertClientDecals(client, map.CGridUid, baselineIds);

            updates.Clear();
            uint changed = default;
            await server.WaitAssertion(() =>
            {
                var decals = server.EntMan.System<ServerDecalSystem>();
                Assert.That(decals.TryAddDecal("burnt1", Coordinates(map.Grid.Owner, 0.4f), out changed), Is.True);
            });
            await pair.RunTicksSync(3);

            var added = GetOnlyDelta(updates, netGrid);
            Assert.Multiple(() =>
            {
                Assert.That(added.Cleared, Is.False);
                Assert.That(added.ModifiedDecals.Keys, Is.EqualTo(new[] { changed }));
                Assert.That(added.RemovedDecals, Is.Empty);
            });
            await AssertClientDecals(client, map.CGridUid, baselineIds.Append(changed));

            updates.Clear();
            await server.WaitAssertion(() =>
            {
                var decals = server.EntMan.System<ServerDecalSystem>();
                Assert.That(decals.SetDecalColor(map.Grid.Owner, changed, Color.Red), Is.True);
            });
            await pair.RunTicksSync(3);

            var modified = GetOnlyDelta(updates, netGrid);
            Assert.Multiple(() =>
            {
                Assert.That(modified.Cleared, Is.False);
                Assert.That(modified.ModifiedDecals.Keys, Is.EqualTo(new[] { changed }));
                Assert.That(modified.ModifiedDecals[changed].Color, Is.EqualTo(Color.Red));
                Assert.That(modified.RemovedDecals, Is.Empty);
            });
            await client.WaitAssertion(() =>
            {
                var component = client.EntMan.GetComponent<DecalGridComponent>(map.CGridUid);
                Assert.That(component.ChunkCollection.ChunkCollection[Chunk].Decals[changed].Color, Is.EqualTo(Color.Red));
            });

            updates.Clear();
            await server.WaitAssertion(() =>
            {
                var decals = server.EntMan.System<ServerDecalSystem>();
                Assert.That(decals.RemoveDecal(map.Grid.Owner, changed), Is.True);
            });
            await pair.RunTicksSync(3);

            var removed = GetOnlyDelta(updates, netGrid);
            Assert.Multiple(() =>
            {
                Assert.That(removed.Cleared, Is.False, "Removing one decal from a non-empty chunk is not a chunk clear.");
                Assert.That(removed.ModifiedDecals, Is.Empty);
                Assert.That(removed.RemovedDecals, Is.EqualTo(new[] { changed }));
            });
            await AssertClientDecals(client, map.CGridUid, baselineIds);

            updates.Clear();
            await server.WaitAssertion(() =>
            {
                var decals = server.EntMan.System<ServerDecalSystem>();
                var result = decals.RemoveDecals(map.Grid.Owner, cleanableOnly: false);
                Assert.Multiple(() =>
                {
                    Assert.That(result.Removed, Is.EqualTo(baselineIds.Length));
                    Assert.That(result.Skipped, Is.Zero);
                });
            });
            await pair.RunTicksSync(3);

            var cleared = GetOnlyDelta(updates, netGrid);
            Assert.Multiple(() =>
            {
                Assert.That(cleared.Cleared, Is.True);
                Assert.That(cleared.ModifiedDecals, Is.Empty);
                Assert.That(cleared.RemovedDecals, Is.Empty);
            });
            await client.WaitAssertion(() =>
            {
                var component = client.EntMan.GetComponent<DecalGridComponent>(map.CGridUid);
                Assert.Multiple(() =>
                {
                    Assert.That(component.ChunkCollection.ChunkCollection.ContainsKey(Chunk), Is.False);
                    Assert.That(component.DecalIndex, Is.Empty);
                });
            });

            updates.Clear();
            var replacementIds = new uint[10];
            await server.WaitAssertion(() =>
            {
                var decals = server.EntMan.System<ServerDecalSystem>();
                for (var i = 0; i < replacementIds.Length; i++)
                {
                    var offset = 0.05f + i * 0.08f;
                    Assert.That(decals.TryAddDecal("burnt1", Coordinates(map.Grid.Owner, offset), out replacementIds[i]), Is.True);
                }
            });
            await pair.RunTicksSync(3);

            var addedForReplacement = GetOnlyDelta(updates, netGrid);
            Assert.Multiple(() =>
            {
                Assert.That(addedForReplacement.Cleared, Is.False);
                Assert.That(addedForReplacement.ModifiedDecals.Keys, Is.EquivalentTo(replacementIds));
                Assert.That(addedForReplacement.RemovedDecals, Is.Empty);
            });
            await AssertClientDecals(client, map.CGridUid, replacementIds);

            updates.Clear();
            await server.WaitAssertion(() =>
            {
                var decals = server.EntMan.System<ServerDecalSystem>();
                for (var i = 0; i < replacementIds.Length - 1; i++)
                    Assert.That(decals.RemoveDecal(map.Grid.Owner, replacementIds[i]), Is.True);
            });
            await pair.RunTicksSync(3);

            var survivor = replacementIds[^1];
            var replaced = GetOnlyDelta(updates, netGrid);
            Assert.Multiple(() =>
            {
                Assert.That(replaced.Cleared, Is.True);
                Assert.That(replaced.ModifiedDecals.Keys, Is.EqualTo(new[] { survivor }));
                Assert.That(replaced.RemovedDecals, Is.Empty);
            });
            await AssertClientDecals(client, map.CGridUid, new[] { survivor });

            updates.Clear();
            uint afterClear = default;
            await server.WaitAssertion(() =>
            {
                var decals = server.EntMan.System<ServerDecalSystem>();
                var result = decals.RemoveDecals(map.Grid.Owner, cleanableOnly: false);
                Assert.Multiple(() =>
                {
                    Assert.That(result.Removed, Is.EqualTo(1));
                    Assert.That(result.Skipped, Is.Zero);
                    Assert.That(decals.TryAddDecal("burnt1", Coordinates(map.Grid.Owner, 0.5f), out afterClear), Is.True);
                });
            });
            await pair.RunTicksSync(3);

            var clearedAndAdded = GetOnlyDelta(updates, netGrid);
            Assert.Multiple(() =>
            {
                Assert.That(clearedAndAdded.Cleared, Is.True);
                Assert.That(clearedAndAdded.ModifiedDecals.Keys, Is.EqualTo(new[] { afterClear }));
                Assert.That(clearedAndAdded.RemovedDecals, Is.Empty);
            });
            await AssertClientDecals(client, map.CGridUid, new[] { afterClear });
        }
        finally
        {
            await client.WaitPost(() => client.EntMan.EntityNetManager.ReceivedSystemMessage -= OnSystemMessage);
        }

        await pair.CleanReturnAsync();
    }

    private static EntityCoordinates Coordinates(EntityUid grid, float offset)
        => new(grid, new Vector2(offset, offset));

    private static DecalGridComponent.DecalChunk GetOnlyFullChunkUpdate(
        IEnumerable<DecalChunkUpdateEvent> updates,
        NetEntity grid)
    {
        var relevant = updates
            .Where(update => update.Data.ContainsKey(grid)
                || update.Deltas.ContainsKey(grid)
                || update.RemovedChunks.ContainsKey(grid))
            .ToArray();
        Assert.That(relevant, Has.Length.EqualTo(1), "Expected exactly one initial full decal chunk update.");

        var update = relevant.Single();
        Assert.Multiple(() =>
        {
            Assert.That(update.Data.TryGetValue(grid, out var chunks), Is.True);
            Assert.That(chunks, Does.ContainKey(Chunk));
            Assert.That(update.Deltas.ContainsKey(grid), Is.False);
            Assert.That(update.RemovedChunks.ContainsKey(grid), Is.False);
        });
        return update.Data[grid][Chunk];
    }

    private static DecalChunkDelta GetOnlyDelta(
        IEnumerable<DecalChunkUpdateEvent> updates,
        NetEntity grid)
    {
        var relevant = updates
            .Where(update => update.Data.ContainsKey(grid)
                || update.Deltas.ContainsKey(grid)
                || update.RemovedChunks.ContainsKey(grid))
            .ToArray();
        Assert.That(relevant, Has.Length.EqualTo(1), "Expected exactly one decal update for the test grid.");

        var update = relevant.Single();
        Assert.Multiple(() =>
        {
            Assert.That(update.Data.ContainsKey(grid), Is.False, "Existing chunks must not be replayed as full snapshots.");
            Assert.That(update.RemovedChunks.ContainsKey(grid), Is.False, "A decal removal is not PVS chunk removal.");
            Assert.That(update.Deltas.TryGetValue(grid, out var chunks), Is.True);
            Assert.That(chunks, Has.Count.EqualTo(1));
            Assert.That(chunks, Does.ContainKey(Chunk));
        });
        return update.Deltas[grid][Chunk];
    }

    private static async Task AssertClientDecals(
        RobustIntegrationTest.ClientIntegrationInstance client,
        EntityUid grid,
        IEnumerable<uint> expected)
    {
        var expectedIds = expected.ToArray();
        await client.WaitAssertion(() =>
        {
            var entMan = client.EntMan;
            var component = entMan.GetComponent<DecalGridComponent>(grid);
            Assert.Multiple(() =>
            {
                Assert.That(component.ChunkCollection.ChunkCollection[Chunk].Decals.Keys, Is.EquivalentTo(expectedIds));
                Assert.That(component.DecalIndex.Keys, Is.EquivalentTo(expectedIds));
                Assert.That(component.DecalIndex.Values, Is.All.EqualTo(Chunk));
            });
        });
    }
}

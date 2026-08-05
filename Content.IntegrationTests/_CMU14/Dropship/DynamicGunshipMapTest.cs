using System.Linq;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Utility;

namespace Content.IntegrationTests._CMU14.Dropship;

[TestFixture]
public sealed class DynamicGunshipMapTest
{
    [Test]
    public async Task DynamicGunshipLoads()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var loader = server.System<MapLoaderSystem>();
            var maps = server.System<SharedMapSystem>();
            var options = DeserializationOptions.Default with { InitializeMaps = false };
            maps.CreateMap(out var mapId);

            Assert.That(loader.TryLoadGrid(
                mapId,
                new ResPath("/Maps/_RMC14/Shuttles/dynamic_gunship.yml"),
                out var grid,
                options), Is.True);

            Assert.That(grid, Is.Not.Null);
            Assert.That(maps.GetAllTiles(grid!.Value.Owner, grid.Value.Comp).Count(), Is.EqualTo(77));

            var descendants = 0;
            CountDescendants(grid.Value.Owner, ref descendants);
            Assert.That(descendants, Is.EqualTo(77));

            maps.DeleteMap(mapId);
        });

        await pair.CleanReturnAsync();

        void CountDescendants(EntityUid parent, ref int count)
        {
            var enumerator = server.Transform(parent).ChildEnumerator;
            while (enumerator.MoveNext(out var child))
            {
                count++;
                CountDescendants(child, ref count);
            }
        }
    }
}

using Content.Shared._RMC14.Stealth;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Prototypes;

namespace Content.Client._RMC14.Stealth;

public sealed partial class EntityInvisibilityVisualsSystem : EntitySystem
{
    private static readonly ProtoId<ShaderPrototype> InvisibilityShader = "RMCInvisible";
    private const string ShaderId = "RMCInvisible"; // CMU14: keyed id required by the v288 multi post-shader API

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SpriteSystem _sprite = default!; // CMU14

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<EntityTurnInvisibleComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<EntityTurnInvisibleComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnStartup(Entity<EntityTurnInvisibleComponent> ent, ref ComponentStartup args)
    {
        if (!TryComp(ent, out SpriteComponent? sprite))
            return;

        // CMU14: legacy PostShader setter would clear every other post-shader on the sprite
        _sprite.SetPostShader(sprite, new SpriteComponent.PostShaderArgs(ShaderId, _prototypes.Index(InvisibilityShader).InstanceUnique()));
    }

    private void OnShutdown(Entity<EntityTurnInvisibleComponent> ent, ref ComponentShutdown args)
    {
        if (TerminatingOrDeleted(ent))
            return;

        if (!TryComp(ent, out SpriteComponent? sprite))
            return;

        _sprite.RemovePostShader(sprite, ShaderId); // CMU14
    }

    public override void Update(float frameTime)
    {
        var invisible = EntityQueryEnumerator<EntityTurnInvisibleComponent, SpriteComponent>();
        while (invisible.MoveNext(out var uid, out var comp, out var sprite))
        {
            var opacity =  TryComp<EntityActiveInvisibleComponent>(uid, out var activeInvisible) ? activeInvisible.Opacity : 1;
            // CMU14: keyed lookup so we only touch our own shader instance
            if (_sprite.TryGetPostShader(sprite, ShaderId, out var entry))
                entry.Shader.SetParameter("visibility", opacity);
        }
    }
}

using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using Content.Shared._Mono;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Tag;
using Content.Shared.Mobs.Components;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Serialization;
using Robust.Shared.Threading;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Shared.Projectiles;

public abstract partial class SharedProjectileSystem : EntitySystem
{
    public const string ProjectileFixture = "projectile";
    private const int MinProjectilesForParallel = 8;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;

    [Dependency] private readonly INetManager _netManager = default!;
    [Dependency] private readonly IParallelManager _parallel = default!;

    // Cache of projectiles waiting for collision checks
    private readonly ConcurrentQueue<(EntityUid Uid, ProjectileComponent Component, EntityUid Target)>
        _pendingCollisionChecks = new();

    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    private readonly HashSet<EntityUid> _processedProjectiles = new();
    private readonly TimeSpan _processingInterval = TimeSpan.FromMilliseconds(16); // ~60Hz
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    private TimeSpan _lastBatchProcess;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ProjectileComponent, PreventCollideEvent>(PreventCollision);
        SubscribeLocalEvent<EmbeddableProjectileComponent, PreventCollideEvent>(
            EmbeddablePreventCollision); // Goobstation - Crawl Fix
        SubscribeLocalEvent<EmbeddableProjectileComponent, ProjectileHitEvent>(OnEmbedProjectileHit);
        SubscribeLocalEvent<EmbeddableProjectileComponent, ThrowDoHitEvent>(OnEmbedThrowDoHit);
        SubscribeLocalEvent<EmbeddableProjectileComponent, ActivateInWorldEvent>(OnEmbedActivate);
        SubscribeLocalEvent<EmbeddableProjectileComponent, RemoveEmbeddedProjectileEvent>(OnEmbedRemove);
        SubscribeLocalEvent<EmbeddableProjectileComponent, ComponentShutdown>(OnEmbeddableCompShutdown);

        SubscribeLocalEvent<EmbeddedContainerComponent, EntityTerminatingEvent>(OnEmbeddableTermination);
        // Subscribe to initialize the origin grid on ProjectileGridPhaseComponent
        SubscribeLocalEvent<ProjectileGridPhaseComponent, ComponentStartup>(OnProjectileGridPhaseStartup);
        // Subscribe to ensure MetaDataComponent on projectile entities for networking
        SubscribeLocalEvent<ProjectileComponent, ComponentStartup>(OnProjectileMetaStartup);
        SubscribeLocalEvent<EmbeddableProjectileComponent, EntityTerminatingEvent>(OnEmbeddedEntityTerminating);
    }

    /// <summary>
    /// Initialize the origin grid for phasing projectiles.
    /// </summary>
    private void OnProjectileGridPhaseStartup(EntityUid uid,
        ProjectileGridPhaseComponent component,
        ComponentStartup args)
    {
        var xform = Transform(uid);
        component.SourceGrid = xform.GridUid ?? EntityUid.Invalid;
    }

    /// <summary>
    /// Ensures that a MetaDataComponent exists on projectiles for network serialization.
    /// </summary>
    private void OnProjectileMetaStartup(EntityUid uid, ProjectileComponent component, ComponentStartup args)
    {
        // Check if the entity still exists before trying to add a component
        if (!EntityManager.EntityExists(uid))
            return;

        EnsureComp<MetaDataComponent>(uid);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Process batched collision checks if enough time has passed or queue is large
        var now = _gameTiming.CurTime;
        if ((now - _lastBatchProcess > _processingInterval ||
             _pendingCollisionChecks.Count >= MinProjectilesForParallel * 2) &&
            _pendingCollisionChecks.Count > 0)
        {
            ProcessPendingCollisionChecks();
            _lastBatchProcess = now;
        }
    }

    /// <summary>
    /// Process all pending collision checks in a batch, potentially using parallelism
    /// </summary>
    private void ProcessPendingCollisionChecks()
    {
        if (_pendingCollisionChecks.Count == 0)
            return;

        // Prepare batch of collision checks
        var collisionChecks = new List<(EntityUid Uid, ProjectileComponent Component, EntityUid Target)>();
        while (_pendingCollisionChecks.TryDequeue(out var check))
        {
            // Skip if the projectile was already processed (could happen if added multiple times)
            if (_processedProjectiles.Contains(check.Uid))
                continue;

            // Check if entities still exist
            if (!EntityManager.EntityExists(check.Uid) || !EntityManager.EntityExists(check.Target))
                continue;

            collisionChecks.Add(check);
            _processedProjectiles.Add(check.Uid); // Mark as processed to avoid duplicates
        }

        // Clear processed set for next batch
        _processedProjectiles.Clear();

        // Process collisions in parallel if enough work to justify it
        if (collisionChecks.Count >= MinProjectilesForParallel)
            ProcessCollisionsParallel(collisionChecks);
        else
        {
            // Process sequentially for small batches
            foreach (var (uid, component, target) in collisionChecks)
            {
                CheckShieldCollision(uid, component, target);
            }
        }
    }

    /// <summary>
    /// Process collision checks in parallel
    /// </summary>
    private void ProcessCollisionsParallel(
        List<(EntityUid Uid, ProjectileComponent Component, EntityUid Target)> checks)
    {
        var results = new ConcurrentDictionary<EntityUid, bool>();

        // Create job for parallel processing
        var job = new ProjectileCollisionJob
        {
            ParentSystem = this,
            ProjectileChecks = checks,
            CollisionResults = results,
        };

        // Process in parallel
        _parallel.ProcessNow(job, checks.Count);

        // Apply results
        foreach (var (uid, shouldCancel) in results)
        {
            if (shouldCancel && TryComp<PhysicsComponent>(uid, out var physics))
            {
                _physics.SetLinearVelocity(uid, Vector2.Zero, body: physics);
                RemComp<ProjectileComponent>(uid);
            }
        }
    }

    /// <summary>
    /// Check if a projectile's collision should be prevented by shields
    /// </summary>
    public bool CheckShieldCollision(EntityUid uid, ProjectileComponent component, EntityUid target)
    {
        // Check if projectile entity still exists (might have been deleted during processing)
        if (!EntityManager.EntityExists(uid) || !EntityManager.EntityExists(target))
            return false;

        // Raise event to check if any shield system wants to prevent collision
        var ev = new ProjectileCollisionAttemptEvent(uid, target);
        RaiseLocalEvent(ref ev);

        return ev.Cancelled;
    }

    private void RemoveContainerIfEmpty(EntityUid containerUid)
    {
        if (!TryComp<EmbeddedContainerComponent>(containerUid, out var cont))
            return;

        if (cont.EmbeddedObjects.Count == 0)
            RemComp<EmbeddedContainerComponent>(containerUid);
    }

    private void OnEmbeddedEntityTerminating(EntityUid uid,
        EmbeddableProjectileComponent comp,
        ref EntityTerminatingEvent args)
    {
        if (comp.EmbeddedIntoUid is { } containerUid &&
            TryComp<EmbeddedContainerComponent>(containerUid, out var container))
        {
            container.EmbeddedObjects.Remove(uid);
            RemoveContainerIfEmpty(containerUid);
        }
    }

    private void OnEmbedActivate(Entity<EmbeddableProjectileComponent> embeddable, ref ActivateInWorldEvent args)
    {
        // Unremovable embeddables moment
        if (embeddable.Comp.RemovalTime == null)
            return;

        if (args.Handled || !args.Complex || !TryComp<PhysicsComponent>(embeddable, out var physics) ||
            physics.BodyType != BodyType.Static)
            return;

        args.Handled = true;

        _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager,
            args.User,
            embeddable.Comp.RemovalTime.Value,
            new RemoveEmbeddedProjectileEvent(),
            embeddable,
            embeddable));
    }

    private void OnEmbedRemove(Entity<EmbeddableProjectileComponent> embeddable, ref RemoveEmbeddedProjectileEvent args)
    {
        // Whacky prediction issues.
        if (args.Cancelled || _netManager.IsClient)
            return;

        EmbedDetach(embeddable, embeddable.Comp, args.User);

        // try place it in the user's hand
        _hands.TryPickupAnyHand(args.User, embeddable);
    }

    private void OnEmbeddableCompShutdown(Entity<EmbeddableProjectileComponent> embeddable, ref ComponentShutdown arg)
    {
        EmbedDetach(embeddable, embeddable.Comp);
    }

    private void OnEmbedThrowDoHit(Entity<EmbeddableProjectileComponent> embeddable, ref ThrowDoHitEvent args)
    {
        if (!embeddable.Comp.EmbedOnThrow)
            return;

        EmbedAttach(embeddable, args.Target, null, embeddable.Comp);
    }

    private void OnEmbedProjectileHit(Entity<EmbeddableProjectileComponent> embeddable, ref ProjectileHitEvent args)
    {
        EmbedAttach(embeddable, args.Target, args.Shooter, embeddable.Comp);

        // Raise a specific event for projectiles.
        if (TryComp(embeddable, out ProjectileComponent? projectile))
        {
            var ev = new ProjectileEmbedEvent(projectile.Shooter,
                projectile.Weapon ?? EntityUid.Invalid,
                args.Target); // Frontier: fix nullability checks on Shooter, Weapon
            RaiseLocalEvent(embeddable, ref ev);
        }
    }

    private void EmbedAttach(EntityUid uid, EntityUid target, EntityUid? user, EmbeddableProjectileComponent component)
    {
        TryComp<PhysicsComponent>(uid, out var physics);
        _physics.SetLinearVelocity(uid, Vector2.Zero, body: physics);
        _physics.SetBodyType(uid, BodyType.Static, body: physics);
        var xform = Transform(uid);
        _transform.SetParent(uid, xform, target);

        if (component.Offset != Vector2.Zero)
        {
            var rotation = xform.LocalRotation;
            if (TryComp<ThrowingAngleComponent>(uid, out var throwingAngleComp))
                rotation += throwingAngleComp.Angle;
            _transform.SetLocalPosition(uid, xform.LocalPosition + rotation.RotateVec(component.Offset), xform);
        }

        _audio.PlayPredicted(component.Sound, uid, null);
        component.EmbeddedIntoUid = target;
        var ev = new EmbedEvent(user, target);
        RaiseLocalEvent(uid, ref ev);
        Dirty(uid, component);

        EnsureComp<EmbeddedContainerComponent>(target, out var embeddedContainer);

        //Assert that this entity not embed
        DebugTools.AssertEqual(embeddedContainer.EmbeddedObjects.Contains(uid), false);

        embeddedContainer.EmbeddedObjects.Add(uid);
    }

    public void EmbedDetach(EntityUid uid,
        EmbeddableProjectileComponent? component,
        EntityUid? user = null)
    {
        if (TerminatingOrDeleted(uid))
            return;

        if (!Resolve(uid, ref component))
            return;

        if (component.EmbeddedIntoUid is not null)
        {
            if (TryComp<EmbeddedContainerComponent>(component.EmbeddedIntoUid.Value, out var embeddedContainer))
            {
                embeddedContainer.EmbeddedObjects.Remove(uid);
                Dirty(component.EmbeddedIntoUid.Value, embeddedContainer);
                if (embeddedContainer.EmbeddedObjects.Count == 0)
                    RemCompDeferred<EmbeddedContainerComponent>(component.EmbeddedIntoUid.Value);
            }
        }

        if (component.DeleteOnRemove && _netManager.IsServer)
        {
            QueueDel(uid);
            return;
        }

        var xform = Transform(uid);
        if (TerminatingOrDeleted(xform.GridUid) && TerminatingOrDeleted(xform.MapUid))
            return;

        TryComp<PhysicsComponent>(uid, out var physics);
        _physics.SetBodyType(uid, BodyType.Dynamic, body: physics, xform: xform);
        _transform.AttachToGridOrMap(uid, xform);

        component.EmbeddedIntoUid = null;
        Dirty(uid, component);

        if (TryComp<ProjectileComponent>(uid, out var projectile))
        {
            projectile.Shooter = null;
            projectile.Weapon = null;
            projectile.ProjectileSpent = false;
            Dirty(uid, projectile);
        }

        if (user != null)
        {
            var landEv = new LandEvent(user, true);
            RaiseLocalEvent(uid, ref landEv);
        }

        _physics.WakeBody(uid, body: physics);
    }


    private void OnEmbeddableTermination(Entity<EmbeddedContainerComponent> container, ref EntityTerminatingEvent args)
    {
        DetachAllEmbedded(container);
    }

    public void DetachAllEmbedded(Entity<EmbeddedContainerComponent> container)
    {
        foreach (var embedded in container.Comp.EmbeddedObjects.ToArray())
        {
            if (TerminatingOrDeleted(embedded))
                continue;

            if (!TryComp<EmbeddableProjectileComponent>(embedded, out var embeddedComp))
                continue;

            EmbedDetach(embedded, embeddedComp);
        }
    }


    private void PreventCollision(EntityUid uid, ProjectileComponent component, ref PreventCollideEvent args)
    {
        // Goobstation - Crawling fix
        if (TryComp<RequireProjectileTargetComponent>(args.OtherEntity, out var requireTarget) &&
            requireTarget.IgnoreThrow && requireTarget.Active)
            return;

        if (component.IgnoreShooter && (args.OtherEntity == component.Shooter || args.OtherEntity == component.Weapon))
        {
            args.Cancelled = true;
            return;
        }

        // Get transforms once for subsequent checks to avoid repeated calls
        var projectileXform = Transform(uid);
        var targetXform = Transform(args.OtherEntity);

        // Check for ProjectileGridPhaseComponent and origin-grid phasing
        if (TryComp<ProjectileGridPhaseComponent>(uid, out var phaseComp)
            && phaseComp.SourceGrid != EntityUid.Invalid
            && targetXform.GridUid != EntityUid.Invalid
            && phaseComp.SourceGrid == targetXform.GridUid)
        {
            args.Cancelled = true;
            return;
        }

        // Add collision check to queue for batch processing if we have enough
        if (_pendingCollisionChecks.Count >= MinProjectilesForParallel / 2)
        {
            _pendingCollisionChecks.Enqueue((uid, component, args.OtherEntity));

            // Assume collision for now - if shield check passes, we'll handle it in the batch process
            return;
        }

        // For low volume, process immediately
        // Check if any shield system wants to prevent collision
        var ev = new ProjectileCollisionAttemptEvent(uid, args.OtherEntity);
        RaiseLocalEvent(ref ev);

        if (ev.Cancelled)
        {
            args.Cancelled = true;
            return;
        }

        // Check if target and projectile are on different maps/z-levels
        if (projectileXform.MapID != targetXform.MapID)
        {
            args.Cancelled = true;
            return;
        }

        // Define the tag constant
        const string GunCanAimShooterTag = "GunCanAimShooter";

        if ((component.Shooter == args.OtherEntity || component.Weapon == args.OtherEntity) &&
            component.Weapon != null && _tag.HasTag(component.Weapon.Value, GunCanAimShooterTag) &&
            TryComp(uid, out TargetedProjectileComponent? targeted) && targeted.Target == args.OtherEntity)
            return;
    }

    // Goobstation - Crawling fix
    private void EmbeddablePreventCollision(EntityUid uid,
        EmbeddableProjectileComponent component,
        ref PreventCollideEvent args)
    {
        if (TryComp<RequireProjectileTargetComponent>(args.OtherEntity, out var requireTarget) &&
            requireTarget.IgnoreThrow && requireTarget.Active)
            args.Cancelled = true;
    }

    public void SetShooter(EntityUid id, ProjectileComponent component, EntityUid shooterId)
    {
        if (component.Shooter == shooterId)
            return;

        component.Shooter = shooterId;
        Dirty(id, component);
    }

    [Serializable] [NetSerializable]
    private sealed partial class RemoveEmbeddedProjectileEvent : DoAfterEvent
    {
        public override DoAfterEvent Clone()
        {
            return this;
        }
    }
}

[Serializable] [NetSerializable]
public sealed class ImpactEffectEvent : EntityEventArgs
{
    public NetCoordinates Coordinates;
    public string Prototype;

    public ImpactEffectEvent(string prototype, NetCoordinates coordinates)
    {
        Prototype = prototype;
        Coordinates = coordinates;
    }
}

/// <summary>
/// Raised when an entity is just about to be hit with a projectile but can reflect it
/// </summary>
[ByRefEvent]
public record struct ProjectileReflectAttemptEvent(EntityUid ProjUid, ProjectileComponent Component, bool Cancelled) : IInventoryRelayEvent
{
    SlotFlags IInventoryRelayEvent.TargetSlots => SlotFlags.WITHOUT_POCKET;
}

/// <summary>
/// Raised when a projectile hits an entity
/// </summary>
[ByRefEvent]
public record struct ProjectileHitEvent(DamageSpecifier Damage, EntityUid Target, EntityUid? Shooter = null);

/// <summary>
/// Raised when a projectile is about to collide with an entity, allowing systems to prevent the collision
/// </summary>
[ByRefEvent]
public record struct ProjectileCollisionAttemptEvent(EntityUid Projectile, EntityUid Target)
{
    /// <summary>
    /// Whether the collision should be cancelled
    /// </summary>
    public bool Cancelled = false;
}

// Parallel job implementation for processing projectile collisions
public class ProjectileCollisionJob : IParallelRobustJob
{
    public ConcurrentDictionary<EntityUid, bool> CollisionResults = default!;
    public SharedProjectileSystem ParentSystem = default!;
    public List<(EntityUid Uid, ProjectileComponent Component, EntityUid Target)> ProjectileChecks = default!;

    // Process a reasonable number of projectiles in each thread
    public int BatchSize => 16; // Hardcoded value instead of ProjectileBatchSize
    public int MinimumBatchParallel => 2;

    public void Execute(int index)
    {
        if (index >= ProjectileChecks.Count)
            return;

        var (uid, component, target) = ProjectileChecks[index];

        // Check if shield prevents collision
        var cancelled = ParentSystem.CheckShieldCollision(uid, component, target);

        // Store result
        CollisionResults[uid] = cancelled;
    }
}

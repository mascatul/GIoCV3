using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;

namespace GIoC;

/// <summary>
/// Implemented by classes that want a callback once all of their [Inject] fields have been filled
/// by GodotIoC. For nodes and plain classes marked [Register] this is generated automatically
/// (it calls your <c>partial void OnReady()</c>); plain classes can also implement it by hand.
/// </summary>
public interface IGIoCReadyCallback
{
    void OnGIoCReady();
}

/// <summary>
/// Minimal dependency injection container for Godot.
///
/// Three registration styles:
///   // Eager - instance created before registration
///   GodotIoC.Instance.Register(new ScoreService());
///
///   // Lazy - created on first resolve via parameterless ctor
///   GodotIoC.Instance.Register&lt;ScoreService&gt;();
///
///   // Factory - delegate called once and result cached as singleton
///   GodotIoC.Instance.Register&lt;ScoreService&gt;(() => new ScoreService(dep));
///
/// [Inject] works on ANY class, not only nodes:
///   public class ScoreService : IGIoCReadyCallback
///   {
///       [Inject] private SaveGame _save;                 // non-public instance fields are fine
///       public void OnGIoCReady() { /* _save is filled */ }
///   }
///   GodotIoC.Instance.Register(new SaveGame());
///   GodotIoC.Instance.Register(new ScoreService());      // fields are injected during Register
///   var helper = GodotIoC.Instance.Instantiate&lt;Helper&gt;();   // new + inject, not registered
///   GodotIoC.Instance.InjectFields(someObject);          // inject an object created elsewhere
///
///   - Lazy and factory registrations inject the object they create.
///   - If a dependency is not registered yet, the object is remembered (weakly) and its fields are
///     filled when the dependency is registered; OnGIoCReady runs once the last field is set.
///   - Fields that already hold a value are left alone.
///   - Nodes are injected when they enter the tree (see ConnectAutoInject) or from the generated
///     _Ready, so Register(node) does not inject them early.
///
/// Auto-injection for Godot nodes (optional convenience):
///   // In Bootloader._Ready():
///   GodotIoC.Instance.ConnectAutoInject(GetTree());
///
///   [Register]
///   public partial class MyScene : FxmlScene
///   {
///       [Inject] private ScoreService _score;
///   }
/// </summary>
public sealed class GodotIoC
{
    public static readonly GodotIoC Instance = new();

    private static readonly Assembly GodotAssembly = typeof(GodotObject).Assembly;
    private static readonly object ReadyMarker = new();

    private static readonly ConcurrentDictionary<Type, (FieldInfo Field, InjectAttribute Attr)[]> _injectableFieldsCache = new();
    private static readonly ConcurrentDictionary<Type, bool> _registerAttrCache = new();

    // Types currently being created by a factory on this thread (detects circular factory dependencies)
    [ThreadStatic] private static HashSet<(Type, string?)>? t_creating;

    private readonly object _lock = new();

    // Resolved instances
    private readonly Dictionary<(Type type, string? name), object> _registry = new();
    // Lazy factories - called once on first resolve, result cached in _registry
    private readonly Dictionary<(Type type, string? name), Func<object>> _factories = new();
    // Objects waiting for a dependency that isn't registered yet. Weak: the container never keeps them alive.
    private readonly Dictionary<Type, List<WeakReference<object>>> _pending = new();
    // Objects that already had OnGIoCReady() called (weak, so entries disappear with their object)
    private readonly ConditionalWeakTable<object, object> _readyFired = new();

    // Test seams: engine-free defaults for plain objects, GodotObject.IsInstanceValid for Godot objects
    internal Func<object, bool> IsAlive = static o => o is not GodotObject g || GodotObject.IsInstanceValid(g);
    internal Action<string> LogError = static message => GD.PrintErr(message);

    internal GodotIoC() { }

    // ── Registration ──────────────────────────────────────────────────────────

    /// <summary>
    /// Register a pre-created instance. A plain (non-Node) instance has its [Inject] fields filled
    /// first, so dependents never see it half-injected.
    /// </summary>
    public void Register<T>(T instance, string? name = null) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance is not Node)
            InjectFieldsCore(instance);

        lock (_lock)
            _registry[(typeof(T), name)] = instance;
        AnnounceDependencyAvailable(typeof(T));
    }

    /// <summary>Register a lazy singleton - created on first resolve via parameterless ctor, then injected.</summary>
    public void Register<T>(string? name = null) where T : new()
    {
        lock (_lock)
            _factories[(typeof(T), name)] = () => new T();
        AnnounceDependencyAvailable(typeof(T));
    }

    /// <summary>Register a factory delegate - called once, the result is injected and cached as singleton.</summary>
    public void Register<T>(Func<T> factory, string? name = null) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_lock)
            _factories[(typeof(T), name)] = () => factory()!;
        AnnounceDependencyAvailable(typeof(T));
    }

    /// <summary>
    /// Register a Node instance and add it as a child of <paramref name="parent"/>.
    /// Use this in Bootloader instead of Register(new T()) for [Register] nodes that need
    /// to be in the scene tree so their generated _Ready() runs and [Inject] fields are filled.
    ///
    /// Example:
    ///   GodotIoC.Instance.RegisterAndAdd(new SceneChanger(), this);
    /// </summary>
    public void RegisterAndAdd<T>(T instance, Node parent, string? name = null) where T : Node
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(parent);
        lock (_lock)
            _registry[(typeof(T), name)] = instance;
        AnnounceDependencyAvailable(typeof(T));
        parent.AddChild(instance);
    }

    // ── Resolution ────────────────────────────────────────────────────────────

    /// <summary>Resolve a registered instance by type (and optional name).</summary>
    public T Resolve<T>(string? name = null)
    {
        if (TryResolveCore(typeof(T), name, out var value))
            return (T)value!;

        throw new InvalidOperationException(
            $"[GodotIoC] No registration found for type '{typeof(T).Name}'" +
            (name != null ? $" with name '{name}'" : "") + ".");
    }

    /// <summary>Try to resolve without throwing if not registered.</summary>
    public bool TryResolve<T>(out T? instance, string? name = null)
    {
        if (TryResolveCore(typeof(T), name, out var value))
        {
            instance = (T)value!;
            return true;
        }
        instance = default;
        return false;
    }

    // Looks the registration up under the lock; runs the factory (user code) and injection OUTSIDE it.
    private bool TryResolveCore(Type type, string? name, out object? value)
    {
        var key = (type, name);
        Func<object>? factory;

        lock (_lock)
        {
            if (_registry.TryGetValue(key, out value)) return true;
            if (!_factories.TryGetValue(key, out factory))
            {
                value = null;
                return false;
            }
        }

        var creating = t_creating ??= new HashSet<(Type, string?)>();
        if (!creating.Add(key))
            throw new InvalidOperationException(
                $"[GodotIoC] Circular dependency while creating '{type.Name}'" +
                (name != null ? $" (name '{name}')" : "") + ". A factory resolved a type that is already being created.");

        object created;
        try { created = factory(); }
        finally { creating.Remove(key); }

        lock (_lock)
        {
            if (_registry.TryGetValue(key, out var existing))   // another caller created it first
            {
                value = existing;
                return true;
            }
            _registry[key] = created;                            // stored BEFORE injection, so A<->B cycles terminate
            _factories.Remove(key);
        }

        if (created is not Node)
            InjectFieldsCore(created);

        value = created;
        return true;
    }

    /// <summary>Create a new T with its parameterless constructor and inject its [Inject] fields. Not registered.</summary>
    public T Instantiate<T>() where T : class, new()
    {
        var instance = new T();
        InjectFieldsCore(instance);
        return instance;
    }

    // ── Field Injection ───────────────────────────────────────────────────────

    /// <summary>
    /// Fill all [Inject]-marked fields on target (any object: node, resource or plain class) via reflection.
    /// Fields that already hold a value are skipped. If a dependency isn't available yet, the object is queued
    /// (weakly) and retried automatically when that dependency is registered later.
    /// IGIoCReadyCallback.OnGIoCReady() runs once, when no field is left unfilled.
    /// </summary>
    public void InjectFields(object target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.GetType().IsValueType)
            throw new ArgumentException("Cannot inject into a value type: the boxed copy would be discarded.", nameof(target));

        InjectFieldsCore(target);
    }

    /// <summary>Kept so code compiled against earlier versions (and the generated _Ready) still binds.</summary>
    public void InjectFields(GodotObject target) => InjectFields((object)target);

    /// <summary>
    /// Forget an object: removes it from every wait queue and clears its OnGIoCReady state.
    /// Call it for plain objects you drop while they may still be waiting for a dependency.
    /// </summary>
    public void Release(object target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_lock)
        {
            foreach (var list in _pending.Values)
                list.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, target));
            _readyFired.Remove(target);
        }
    }

    private void InjectFieldsCore(object target)
    {
        if (!IsAlive(target))
            return;

        bool anyPending = false;

        foreach (var (field, attr) in GetInjectableFields(target.GetType()))
        {
            if (field.GetValue(target) != null)
                continue;   // already filled (earlier pass, manual assignment or initializer)

            if (TryResolveCore(field.FieldType, attr.Name, out var dependency))
            {
                field.SetValue(target, dependency);
            }
            else
            {
                anyPending = true;
                Enqueue(field.FieldType, target);
            }
        }

        if (!anyPending)
            FireReady(target);
    }

    private static (FieldInfo Field, InjectAttribute Attr)[] GetInjectableFields(Type type) =>
        _injectableFieldsCache.GetOrAdd(type, static t =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var list = new List<(FieldInfo, InjectAttribute)>();

            // Walk the hierarchy: GetFields() alone does not return private fields declared in base classes
            for (var current = t; current != null && current != typeof(object) && current.Assembly != GodotAssembly; current = current.BaseType)
            {
                foreach (var field in current.GetFields(flags))
                {
                    var attr = field.GetCustomAttribute<InjectAttribute>();
                    if (attr != null) list.Add((field, attr));
                }
            }
            return list.ToArray();
        });

    // Runs the callback outside the lock; a throwing OnReady must not stop other waiting objects from being injected
    private void FireReady(object target)
    {
        if (target is not IGIoCReadyCallback callback)
            return;

        lock (_lock)
        {
            if (_readyFired.TryGetValue(target, out _)) return;
            _readyFired.Add(target, ReadyMarker);
        }

        try
        {
            callback.OnGIoCReady();
        }
        catch (Exception ex)
        {
            LogError($"[GodotIoC] {target.GetType().Name}.OnReady() threw: {ex}");
        }
    }

    // ── Wait queue ────────────────────────────────────────────────────────────

    private void Enqueue(Type dependencyType, object target)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(dependencyType, out var list))
                _pending[dependencyType] = list = new List<WeakReference<object>>();

            list.RemoveAll(w => !w.TryGetTarget(out _));   // drop entries whose object was collected

            foreach (var weak in list)
                if (weak.TryGetTarget(out var existing) && ReferenceEquals(existing, target))
                    return;

            list.Add(new WeakReference<object>(target));
        }
    }

    private void AnnounceDependencyAvailable(Type type)
    {
        List<WeakReference<object>>? waiting;
        lock (_lock)
        {
            if (!_pending.Remove(type, out waiting)) return;
        }

        // Objects still missing another dependency re-queue themselves inside InjectFieldsCore
        foreach (var weak in waiting)
            if (weak.TryGetTarget(out var target))
                InjectFieldsCore(target);
    }

    // ── Auto-inject via SceneTree.NodeAdded ───────────────────────────────────

    /// <summary>
    /// Call once in Bootloader._Ready() to enable automatic injection for all
    /// [Register]-marked nodes as they enter the scene tree.
    /// The node is also registered as a resolvable service by its own type,
    /// so other nodes can [Inject] it by type.
    /// </summary>
    public void ConnectAutoInject(SceneTree tree)
    {
        // Scan nodes already in the tree - handles the case where main-scene nodes
        // entered the tree before Bootloader._Ready() subscribed to NodeAdded.
        // (In Godot 4, _ready propagates bottom-up, so scene nodes may already
        //  be in the tree when the autoload's _Ready() fires.)
        if (tree.Root is Node existingRoot)
            ScanNodeTree(existingRoot);

        tree.NodeAdded += node =>
        {
            if (!HasRegisterAttribute(node.GetType())) return;

            // Register the node itself so others can Resolve/Inject it by type
            lock (_lock)
                _registry[(node.GetType(), null)] = node;

            AnnounceDependencyAvailable(node.GetType());
            InjectFieldsCore(node);
        };
    }

    private void ScanNodeTree(Node root)
    {
        if (HasRegisterAttribute(root.GetType()))
        {
            lock (_lock)
                _registry[(root.GetType(), null)] = root;
            AnnounceDependencyAvailable(root.GetType());
            InjectFieldsCore(root);
        }
        foreach (Node child in root.GetChildren())
            ScanNodeTree(child);
    }

    private static bool HasRegisterAttribute(Type type) =>
        _registerAttrCache.GetOrAdd(type, static t => t.IsDefined(typeof(RegisterAttribute), true));

    // ── Cleanup ───────────────────────────────────────────────────────────────

    /// <summary>Clear all registrations (useful for tests or full scene reset).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _registry.Clear();
            _factories.Clear();
            _pending.Clear();
            _readyFired.Clear();
        }
    }
}

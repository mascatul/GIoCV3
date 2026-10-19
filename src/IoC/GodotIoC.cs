using System.Reflection;
using Godot;

namespace GIoC;

/// <summary>
/// Implemented by [Register] nodes to receive a callback when all
/// [Inject] fields have been successfully filled by GodotIoC.
/// Generated automatically by GIoCV3.SourceGenerator — do not implement manually.
/// </summary>
public interface IGIoCReadyCallback
{
    void OnGIoCReady();
}

/// <summary>
/// Minimal dependency injection container for Godot.
///
/// Three registration styles:
///   // Eager — instance created before registration
///   GodotIoC.Instance.Register(new ScoreService());
///
///   // Lazy — created on first resolve via parameterless ctor
///   GodotIoC.Instance.Register&lt;ScoreService&gt;();
///
///   // Factory — delegate called once and result cached as singleton
///   GodotIoC.Instance.Register&lt;ScoreService&gt;(() => new ScoreService(dep));
///
/// Auto-injection for Godot nodes (optional convenience):
///   // In Bootloader._Ready():
///   GodotIoC.Instance.ConnectAutoInject(GetTree());
///
///   // Any [Register]-marked node gets [Inject] fields filled automatically
///   // when it enters the scene tree — no manual InjectFields(this) needed:
///   [Register]
///   public partial class MyScene : FxmlScene
///   {
///       [Inject] private ScoreService _score;
///   }
///
///   // Manual injection still works too — call anywhere:
///   GodotIoC.Instance.InjectFields(this);
///
/// Late dependencies: if a service isn't registered yet when InjectFields runs,
/// the node is queued and re-injected automatically when Register() is called.
/// </summary>
public sealed class GodotIoC
{
    public static readonly GodotIoC Instance = new();

    private readonly object _lock = new();

    // Resolved instances
    private readonly Dictionary<(Type type, string? name), object> _registry = new();
    // Lazy factories — called once on first resolve, result cached in _registry
    private readonly Dictionary<(Type type, string? name), Func<object>> _factories = new();
    // Nodes waiting for a dependency that isn't registered yet
    private readonly Dictionary<Type, List<GodotObject>> _pendingInjections = new();
    // Nodes that have already had OnGIoCReady() called — prevents double-firing
    // when both the generated _Ready() and ScanNodeTree call InjectFields.
    private readonly HashSet<GodotObject> _readyFired = new();
    // Cache: type → has [Register] attribute (avoids repeated IsDefined calls)
    private readonly Dictionary<Type, bool> _registerAttrCache = new();

    // Cache: type → (field, inject attribute) pairs — avoids repeated GetFields + attribute scan
    private static readonly Dictionary<Type, (FieldInfo Field, InjectAttribute Attr)[]> _injectableFieldsCache = new();

    private GodotIoC() { }

    // ── Registration ──────────────────────────────────────────────────────────

    /// <summary>Register a pre-created instance.</summary>
    public void Register<T>(T instance, string? name = null) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(instance);
        lock (_lock)
            _registry[(typeof(T), name)] = instance;
        AnnounceDependencyAvailable(typeof(T));
    }

    /// <summary>Register a lazy singleton — created on first resolve via parameterless ctor.</summary>
    public void Register<T>(string? name = null) where T : new()
    {
        lock (_lock)
            _factories[(typeof(T), name)] = () => new T();
        AnnounceDependencyAvailable(typeof(T));
    }

    /// <summary>Register a factory delegate — called once and result cached as singleton.</summary>
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
        lock (_lock)
        {
            var key = (typeof(T), name);
            if (_registry.TryGetValue(key, out var value))
                return (T)value;
            if (_factories.TryGetValue(key, out var factory))
            {
                var instance = factory();
                _registry[key] = instance;
                return (T)instance;
            }
        }
        throw new InvalidOperationException(
            $"[GodotIoC] No registration found for type '{typeof(T).Name}'" +
            (name != null ? $" with name '{name}'" : "") + ".");
    }

    /// <summary>Try to resolve without throwing if not registered.</summary>
    public bool TryResolve<T>(out T? instance, string? name = null)
    {
        lock (_lock)
        {
            var key = (typeof(T), name);
            if (_registry.TryGetValue(key, out var value)) { instance = (T)value; return true; }
            if (_factories.TryGetValue(key, out var factory))
            {
                var created = factory();
                _registry[key] = created;
                instance = (T)created;
                return true;
            }
        }
        instance = default;
        return false;
    }

    // ── Field Injection ───────────────────────────────────────────────────────

    /// <summary>
    /// Fill all [Inject]-marked fields on target via reflection.
    /// If a dependency isn't available yet, the node is queued and retried
    /// automatically when that dependency is registered later.
    /// Can be called manually (e.g. in OnFxmlReady) or triggered automatically
    /// via ConnectAutoInject when the node enters the scene tree.
    /// </summary>
    private static (FieldInfo Field, InjectAttribute Attr)[] GetInjectableFields(Type type)
    {
        if (!_injectableFieldsCache.TryGetValue(type, out var fields))
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var list = new System.Collections.Generic.List<(FieldInfo, InjectAttribute)>();
            foreach (var f in type.GetFields(flags))
            {
                var a = f.GetCustomAttribute<InjectAttribute>();
                if (a != null) list.Add((f, a));
            }
            _injectableFieldsCache[type] = fields = list.ToArray();
        }
        return fields;
    }

    public void InjectFields(GodotObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        bool anyPending = false;

        foreach (var (field, attr) in GetInjectableFields(target.GetType()))
        {
            lock (_lock)
            {
                var key = (field.FieldType, attr.Name);

                if (_registry.TryGetValue(key, out var value))
                {
                    field.SetValue(target, value);
                }
                else if (_factories.TryGetValue(key, out var factory))
                {
                    var instance = factory();
                    _registry[key] = instance;
                    field.SetValue(target, instance);
                }
                else
                {
                    // Dependency not available yet — queue for retry when registered
                    if (!_pendingInjections.TryGetValue(field.FieldType, out var list))
                        _pendingInjections[field.FieldType] = list = new List<GodotObject>();
                    if (!list.Contains(target))
                        list.Add(target);
                    anyPending = true;
                }
            }
        }

        // Notify the node that all fields are filled (or no fields to fill).
        // Guard against double-fire: both generated _Ready() and ScanNodeTree
        // call InjectFields — only the first invocation triggers OnGIoCReady().
        if (!anyPending && target is IGIoCReadyCallback cb)
        {
            bool alreadyFired;
            lock (_lock) { alreadyFired = !_readyFired.Add(target); }
            if (!alreadyFired) cb.OnGIoCReady();
        }
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
        // Scan nodes already in the tree — handles the case where main-scene nodes
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
            InjectFields(node);
        };
    }

    private void ScanNodeTree(Node root)
    {
        if (HasRegisterAttribute(root.GetType()))
        {
            lock (_lock)
                _registry[(root.GetType(), null)] = root;
            AnnounceDependencyAvailable(root.GetType());
            InjectFields(root);
        }
        foreach (Node child in root.GetChildren())
            ScanNodeTree(child);
    }

    private bool HasRegisterAttribute(Type type)
    {
        lock (_lock)
        {
            if (!_registerAttrCache.TryGetValue(type, out var has))
                _registerAttrCache[type] = has = type.IsDefined(typeof(RegisterAttribute), true);
            return has;
        }
    }

    // ── Dependency announcement ───────────────────────────────────────────────

    private void AnnounceDependencyAvailable(Type type)
    {
        List<GodotObject>? waiting;
        lock (_lock)
        {
            if (!_pendingInjections.TryGetValue(type, out waiting)) return;
            _pendingInjections.Remove(type);
        }
        foreach (var node in waiting)
            InjectFields(node);
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    /// <summary>Clear all registrations (useful for tests or full scene reset).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _registry.Clear();
            _factories.Clear();
            _pendingInjections.Clear();
            _readyFired.Clear();
        }
    }
}

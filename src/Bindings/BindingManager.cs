using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;

namespace GIoC.Bindings;

public class BindingManager
{
    private readonly List<Binding> _bindings = new();
    private readonly HashSet<Binding> _dirtyBindings = new();
    private readonly Dictionary<ulong, List<Binding>> _bindingsByObjectId = new();
    private readonly Dictionary<(ulong ownerId, string propertyName), ITrackedValue> _trackedValues = new();
    private readonly Dictionary<ulong, List<Binding>> _bindingsBySourceId = new();
    private readonly Dictionary<(Func<object>, Func<object>), Binding> _bindingsByGetters = new();

    // INPC subscriptions: binding → (source, handler) for cleanup
    private readonly Dictionary<Binding, (INotifyPropertyChanged Source, PropertyChangedEventHandler Handler)> _inpcSubscriptions = new();

    private static readonly ConcurrentDictionary<Type, FieldInfo> _godotObjectFieldCache = new();
    private readonly List<Binding> _invalidBindingsBuffer = new(16);
    private int _lastBindingCount = -1;

    public bool EnableDebugLogging { get; set; } = false;

    private void DebugLog(string msg) { if (EnableDebugLogging) GD.Print(msg); }
    private void DebugLogErr(string msg) { if (EnableDebugLogging) GD.PrintErr(msg); }

    // ── Add ──────────────────────────────────────────────────────────────────

    public void AddBinding(Binding binding)
    {
        _bindings.Add(binding);
        _lastBindingCount = -1;

        _bindingsByGetters[(binding.SourceGetter, binding.TargetGetter)] = binding;

        if (!_bindingsByObjectId.TryGetValue(binding.GodotObjectId, out var byId))
            _bindingsByObjectId[binding.GodotObjectId] = byId = new();
        byId.Add(binding);

        var sourceObj = TryGetGodotObject(binding.SourceGetter.Target);
        if (sourceObj != null)
        {
            var sid = sourceObj.GetInstanceId();
            if (!_bindingsBySourceId.TryGetValue(sid, out var bySrc))
                _bindingsBySourceId[sid] = bySrc = new();
            bySrc.Add(binding);
        }

        TryConnectBinding(binding);
    }

    public void AddBinding(Func<object> sourceGetter, Func<object> targetGetter,
        string targetPropertyName = null, Func<object, object> formatter = null)
    {
        var id = ((GodotObject)sourceGetter.Target).GetInstanceId();
        AddBinding(new Binding(id, sourceGetter, targetGetter, targetPropertyName, formatter));
    }

    // ── Remove ───────────────────────────────────────────────────────────────

    public void RemoveBinding(Func<object> sourceGetter = null, Func<object> targetGetter = null)
    {
        var b = FindBinding(sourceGetter, targetGetter);
        if (b != null) RemoveBindingInternal(b);
    }

    public void RemoveBinding(GodotObject obj)
    {
        var id = obj.GetInstanceId();
        if (_bindingsByObjectId.TryGetValue(id, out var list))
        {
            foreach (var b in list.ToArray()) RemoveBindingInternal(b);
            _bindingsByObjectId.Remove(id);
        }
    }

    public void RemoveBinding(Binding binding)
    {
        if (binding != null) RemoveBindingInternal(binding);
    }

    private void RemoveBindingInternal(Binding binding)
    {
        _bindings.Remove(binding);
        _lastBindingCount = -1;
        _dirtyBindings.Remove(binding);
        _bindingsByGetters.Remove((binding.SourceGetter, binding.TargetGetter));

        if (_bindingsByObjectId.TryGetValue(binding.GodotObjectId, out var byId))
        {
            byId.Remove(binding);
            if (byId.Count == 0) _bindingsByObjectId.Remove(binding.GodotObjectId);
        }

        // Unsubscribe INPC handler if connected
        if (_inpcSubscriptions.TryGetValue(binding, out var sub))
        {
            sub.Source.PropertyChanged -= sub.Handler;
            _inpcSubscriptions.Remove(binding);
        }

        // Unsubscribe ObservableCollection if connected
        if (binding.SourceGetter() is ObservableCollection<string> oc &&
            binding._collectionChangedHandler != null)
            oc.CollectionChanged -= binding._collectionChangedHandler;
    }

    // ── Find ─────────────────────────────────────────────────────────────────

    public Binding FindBinding(Func<object> sourceGetter = null, Func<object> targetGetter = null)
    {
        if (sourceGetter != null && targetGetter != null)
        {
            _bindingsByGetters.TryGetValue((sourceGetter, targetGetter), out var b);
            return b;
        }
        foreach (var b in _bindings)
            if ((sourceGetter == null || b.SourceGetter == sourceGetter) &&
                (targetGetter == null || b.TargetGetter == targetGetter))
                return b;
        return null;
    }

    // ── Process ──────────────────────────────────────────────────────────────

    public void ProcessAllBindings()
    {
        if (_bindings.Count != _lastBindingCount)
        {
            DebugLog($"Processing {_bindings.Count} bindings");
            _lastBindingCount = _bindings.Count;
        }

        foreach (var b in _bindings)
        {
            if (!b.IsValid() || b.IsTracked()) continue;
            if (b.SourceGetter.Target is GodotObject src && !Node.IsInstanceValid(src)) continue;
            if (b.TargetGetter.Target is GodotObject tgt && !Node.IsInstanceValid(tgt)) continue;
            b.UpdateTarget();
        }
    }

    public void ProcessDirtyBindings()
    {
        if (_dirtyBindings.Count == 0) return;

        _invalidBindingsBuffer.Clear();
        foreach (var b in _dirtyBindings)
        {
            if (!b.IsValid()) { _invalidBindingsBuffer.Add(b); continue; }

            var srcObj = TryGetGodotObject(b.SourceGetter.Target);
            var tgtObj = TryGetGodotObject(b.TargetGetter.Target);
            if ((srcObj != null && !Node.IsInstanceValid(srcObj)) ||
                (tgtObj != null && !Node.IsInstanceValid(tgtObj)))
            { _invalidBindingsBuffer.Add(b); continue; }

            b.UpdateTarget();
        }

        _dirtyBindings.Clear();
        foreach (var b in _invalidBindingsBuffer) RemoveBindingInternal(b);
    }

    public void MarkBindingDirty(Binding binding) => _dirtyBindings.Add(binding);

    public void MarkAllBindingsDirty()
    {
        foreach (var b in _bindings)
            if (b.IsValid()) { b.MarkDirty(); _dirtyBindings.Add(b); }
    }

    // ── TrackedValue registration ─────────────────────────────────────────────

    public void RegisterTrackedValue(ulong ownerId, string propertyName, ITrackedValue trackedValue)
    {
        if (trackedValue == null) return;

        var key = (ownerId, propertyName);
        if (_trackedValues.TryGetValue(key, out var existing) && existing == trackedValue) return;

        _trackedValues[key] = trackedValue;
        DebugLog($"[BindingManager] Registered TrackedValue: {propertyName} on {ownerId}");

        if (_bindingsBySourceId.TryGetValue(ownerId, out var relevant))
            foreach (var b in relevant) TryConnectBinding(b);
    }

    // ── Connection logic ──────────────────────────────────────────────────────

    private void TryConnectBinding(Binding binding)
    {
        if (binding?.SourceGetter == null) return;

        try
        {
            // Try direct target first; fall back to stored instance ID (handles wrapped PropertyGetter delegates)
            var sourceObj = TryGetGodotObject(binding.SourceGetter.Target);
            if (sourceObj == null && binding.GodotObjectId != 0)
                sourceObj = GodotObject.InstanceFromId(binding.GodotObjectId);
            if (sourceObj == null || !GodotObject.IsInstanceValid(sourceObj)) return;

            var sourceId = sourceObj.GetInstanceId();
            var propertyName = binding.SourcePropertyName;

            if (string.IsNullOrEmpty(propertyName))
            {
                var m = binding.SourceGetter.Method;
                if (m?.Name.StartsWith("get_") == true)
                    propertyName = m.Name.Substring(4);
            }

            if (string.IsNullOrEmpty(propertyName)) return;

            // 1. Try TrackedValue first
            var tvKey = (sourceId, propertyName);
            if (_trackedValues.TryGetValue(tvKey, out var trackedValue))
            {
                trackedValue.AttachBinding(binding);
                binding.MarkDirty();
                MarkBindingDirty(binding);
                DebugLog($"[BindingManager] Connected binding to TrackedValue: {propertyName}");
                return;
            }

            // 2. Fallback: INPC (INotifyPropertyChanged) — used by [Observable]-generated properties
            if (sourceObj is INotifyPropertyChanged inpc)
            {
                // Avoid double-subscribing if already connected
                if (_inpcSubscriptions.ContainsKey(binding)) return;

                var capturedName = propertyName;
                PropertyChangedEventHandler handler = (_, e) =>
                {
                    if (e.PropertyName == capturedName)
                    {
                        binding.MarkDirty();
                        MarkBindingDirty(binding);
                    }
                };
                inpc.PropertyChanged += handler;
                _inpcSubscriptions[binding] = (inpc, handler);
                binding.SetTracked(true);
                binding.MarkDirty();
                MarkBindingDirty(binding);
                DebugLog($"[BindingManager] Connected binding via INPC: {propertyName} on {sourceObj.GetType().Name}");
            }
        }
        catch (Exception ex)
        {
            DebugLogErr($"[BindingManager] TryConnectBinding error: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private GodotObject TryGetGodotObject(object target)
    {
        if (target == null) return null;
        if (target is GodotObject go) return go;

        var type = target.GetType();
        var field = _godotObjectFieldCache.GetOrAdd(type, t =>
        {
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (typeof(GodotObject).IsAssignableFrom(f.FieldType) || f.Name == "_obj")
                    return f;
            return null;
        });

        if (field != null && field.GetValue(target) is GodotObject captured) return captured;
        return null;
    }

    // ── Info ──────────────────────────────────────────────────────────────────

    public int GetBindingCount() => _bindings.Count;
    public IReadOnlyList<Binding> GetAllBindings() => _bindings.AsReadOnly();
    public Binding GetBinding(int index) => index >= 0 && index < _bindings.Count ? _bindings[index] : null;
}

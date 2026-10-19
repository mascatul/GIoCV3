using GIoC.Bindings;
using Godot;
using Godot.Collections;
using System;
using System.Linq;
using System.Reflection;
using Expression = System.Linq.Expressions.Expression;

namespace GIoC.Links;

/// <summary>
/// Inspector-driven binding node. Add as a child of the node you want to
/// receive data (the target). Configure one LinkConfig per property binding.
///
/// Supports sources that use:
///   - [Observable] generated INPC properties  → event-driven, zero polling
///   - TrackedValue&lt;T&gt;                          → event-driven, zero polling
///   - Any plain property/field                → polled each frame
///
/// Setup:
///   1. Add a Links node as child of your Label/ProgressBar/etc.
///   2. Add LinkConfig resources to the Bindings array.
///   3. Register BindingManager in your Bootloader:
///        GodotIoC.Instance.Register(new BindingManager());
///   4. Add a BindingRunner node somewhere in the scene tree.
/// </summary>
[Tool]
[GlobalClass]
public partial class Links : Node
{
    private Array<LinkConfig> _bindings = new();
    private bool _autoCreate = true;
    private System.Collections.Generic.Dictionary<LinkConfig, Binding> _createdBindings = new();
    private string _status = "Not initialized";
    private static bool _enableDebugLogging = false;

    [Export]
    public Array<LinkConfig> Bindings
    {
        get => _bindings;
        set
        {
            _bindings = value ?? new Array<LinkConfig>();
            if (Engine.IsEditorHint()) UpdateStatus();
        }
    }

    [Export]
    public bool AutoCreate
    {
        get => _autoCreate;
        set => _autoCreate = value;
    }

    [ExportGroup("Info (Read-Only)")]
    [Export(PropertyHint.MultilineText)]
    public string Status
    {
        get => _status;
        private set => _status = value;
    }

    public static void SetDebugLogging(bool enabled) => _enableDebugLogging = enabled;

    public override void _EnterTree()
    {
        if (Engine.IsEditorHint()) UpdateStatus();
    }

    public override void _Ready()
    {
        if (!Engine.IsEditorHint() && _autoCreate && _createdBindings.Count == 0)
            CreateAllBindings();
    }

    public override void _Process(double delta)
    {
        if (Engine.IsEditorHint() || _createdBindings.Count == 0) return;
        var mgr = GetBindingManager();
        if (mgr == null) return;
        mgr.ProcessDirtyBindings();
        mgr.ProcessAllBindings();
    }

    public override void _ExitTree()
    {
        if (!Engine.IsEditorHint()) ClearAllBindings();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void CreateAllBindings()
    {
        var target = GetParent();
        if (target == null) { GD.PushError("Links: no parent node (target)"); return; }

        ClearAllBindings();

        var mgr = GetBindingManager();
        if (mgr == null) { GD.PushError("Links: BindingManager not registered in GodotIoC"); return; }

        int ok = 0, fail = 0;
        foreach (var config in _bindings)
        {
            if (config == null || !config.Enabled) continue;
            try
            {
                var b = CreateSingleBinding(config, target, mgr);
                if (b != null) { mgr.AddBinding(b); _createdBindings[config] = b; ok++; }
                else fail++;
            }
            catch (Exception ex)
            {
                GD.PushError($"Links: '{config.GetDisplayName()}' — {ex.Message}");
                fail++;
            }
        }
        _status = $"Created {ok} bindings, {fail} failed";
    }

    public void ClearAllBindings()
    {
        if (_createdBindings.Count > 0)
        {
            var mgr = GetBindingManager();
            if (mgr != null)
                foreach (var b in _createdBindings.Values)
                    mgr.RemoveBinding(b);
        }
        _createdBindings.Clear();
        _status = "Bindings cleared";
    }

    // ── Binding creation ──────────────────────────────────────────────────────

    private Binding CreateSingleBinding(LinkConfig config, Node target, BindingManager mgr)
    {
        if (string.IsNullOrEmpty(config.SourceNodePath))
        { GD.PushError("Links: SourceNodePath is empty"); return null; }

        var sourceNode = GetNode(config.SourceNodePath);
        if (sourceNode == null)
        { GD.PushError($"Links: source node not found at '{config.SourceNodePath}'"); return null; }

        if (string.IsNullOrEmpty(config.SourcePropertyPath))
        { GD.PushError("Links: SourcePropertyPath is empty"); return null; }

        if (string.IsNullOrEmpty(config.TargetPropertyPath))
        { GD.PushError("Links: TargetPropertyPath is empty"); return null; }

        var sourceProperty = ResolveNestedProperty(sourceNode, config.SourcePropertyPath,
            out var finalSourceContainer);
        if (sourceProperty == null)
        {
            if (_enableDebugLogging)
                GD.PushWarning($"Links: '{config.SourcePropertyPath}' not found on {sourceNode.GetType().Name}");
            return null;
        }

        // Clean target path (strip enum-index suffix)
        string cleanTarget = config.TargetPropertyPath;
        int colon = cleanTarget.LastIndexOf(':');
        if (colon > 0) cleanTarget = cleanTarget.Substring(0, colon);

        bool isItemList = target is ItemList &&
            (cleanTarget == "Items" || cleanTarget == "Items (Collection)");

        MemberInfo targetMember = null;
        if (!isItemList)
        {
            targetMember = ResolveProperty(target, config.TargetPropertyPath);
            if (targetMember == null)
            { GD.PushError($"Links: target property '{config.TargetPropertyPath}' not found on {target.GetType().Name}"); return null; }
        }

        var getter = CreateGetter(finalSourceContainer ?? sourceNode, sourceProperty);
        if (getter == null)
        { GD.PushError($"Links: failed to create getter for '{config.SourcePropertyPath}'"); return null; }

        Func<object, object> formatter = null;
        if (!string.IsNullOrEmpty(config.Formatter) && config.Formatter != "None")
            formatter = CreateFormatterFromName(config.Formatter);
        else if (targetMember != null)
            formatter = CreateAutoTypeConverter(sourceProperty, targetMember);

        var effectiveTarget = isItemList ? "Items" : cleanTarget;

        // Extract leaf property name for TrackedValue/INPC auto-connection
        var pathParts = CleanPropertyPath(config.SourcePropertyPath).Split('.');
        var leafName = pathParts[pathParts.Length - 1].Trim();

        var sourceId = (sourceNode as GodotObject)?.GetInstanceId() ?? 0;
        return new Binding(sourceId, getter, () => target, effectiveTarget, formatter, leafName);
    }

    // ── Property resolution ───────────────────────────────────────────────────

    private MemberInfo ResolveProperty(object obj, string path)
    {
        if (obj == null || string.IsNullOrEmpty(path)) return null;

        var clean = CleanPropertyPath(path);
        var type = obj.GetType();

        var prop = type.GetProperty(clean, BindingFlags.Public | BindingFlags.Instance);
        if (prop != null) return prop;

        var field = type.GetField(clean, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null) return field;

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == clean).ToList();
        return methods.FirstOrDefault(m => m.GetParameters().Length <= 1) ?? methods.FirstOrDefault();
    }

    private MemberInfo ResolveNestedProperty(object obj, string path, out object finalContainer)
    {
        finalContainer = null;
        if (obj == null || string.IsNullOrEmpty(path)) return null;

        var clean = CleanPropertyPath(path);
        var parts = clean.Split('.');

        if (parts.Length == 1) { finalContainer = obj; return ResolveProperty(obj, clean); }

        var current = obj;
        MemberInfo last = null;

        for (int i = 0; i < parts.Length; i++)
        {
            last = ResolveProperty(current, parts[i].Trim());
            if (last == null) return null;

            if (i < parts.Length - 1)
            {
                current = GetMemberValue(current, last);
                if (current == null) return null;
            }
            else finalContainer = current;
        }

        return last;
    }

    private string CleanPropertyPath(string path)
    {
        var s = path;
        int ti = s.IndexOf(" (");
        if (ti > 0) s = s.Substring(0, ti);
        int ci = s.LastIndexOf(':');
        if (ci > 0) s = s.Substring(0, ci);
        return s.Trim().TrimEnd('(', ')');
    }

    private object GetMemberValue(object obj, MemberInfo m) => m switch
    {
        PropertyInfo p => p.GetValue(obj),
        FieldInfo f    => f.GetValue(obj),
        MethodInfo met when met.GetParameters().Length == 0 => met.Invoke(obj, null),
        _ => null,
    };

    // ── Getter creation ───────────────────────────────────────────────────────

    private bool IsTrackedValueType(Type type) =>
        type.IsGenericType &&
        type.GetGenericTypeDefinition().FullName == "GIoC.Bindings.TrackedValue`1";

    private Func<object> CreateGetter(object obj, MemberInfo member)
    {
        if (member is PropertyInfo prop)
        {
            if (IsTrackedValueType(prop.PropertyType))
            {
                var vp = prop.PropertyType.GetProperty("Value");
                return new PropertyGetter(obj, prop, vp).GetValue;
            }
            return new PropertyGetter(obj, prop).GetValue;
        }
        if (member is FieldInfo field)
        {
            if (IsTrackedValueType(field.FieldType))
            {
                var vp = field.FieldType.GetProperty("Value");
                return new FieldGetter(obj, field, vp).GetValue;
            }
            return new FieldGetter(obj, field).GetValue;
        }
        if (member is MethodInfo method && method.GetParameters().Length == 0)
        {
            try
            {
                var dt = typeof(Func<>).MakeGenericType(method.ReturnType);
                var del = Delegate.CreateDelegate(dt, obj, method, false);
                return del != null ? () => del.DynamicInvoke() : () => method.Invoke(obj, null);
            }
            catch { return () => method.Invoke(obj, null); }
        }
        return null;
    }

    private Type GetMemberType(MemberInfo m)
    {
        var t = m switch
        {
            PropertyInfo p => p.PropertyType,
            FieldInfo f    => f.FieldType,
            MethodInfo met => met.ReturnType,
            _              => typeof(object),
        };
        return IsTrackedValueType(t) ? t.GetGenericArguments()[0] : t;
    }

    // ── Compiled getter helpers ───────────────────────────────────────────────

    private class PropertyGetter
    {
        private readonly Func<object> _get;
        public PropertyGetter(object obj, PropertyInfo prop, PropertyInfo valueProperty = null)
        {
            if (valueProperty != null)
            {
                var outer = Compiled(obj, prop);
                var inner = CompiledDynamic(valueProperty);
                _get = () => { var tv = outer(); return tv != null ? inner(tv) : null; };
            }
            else _get = Compiled(obj, prop);
        }
        public object GetValue() => _get();
        private static Func<object> Compiled(object obj, PropertyInfo p)
        {
            var inst = Expression.Constant(obj);
            var body = Expression.Convert(Expression.Property(inst, p), typeof(object));
            return Expression.Lambda<Func<object>>(body).Compile();
        }
        private static Func<object, object> CompiledDynamic(PropertyInfo p)
        {
            var param = Expression.Parameter(typeof(object));
            var cast  = Expression.Convert(param, p.DeclaringType);
            var body  = Expression.Convert(Expression.Property(cast, p), typeof(object));
            return Expression.Lambda<Func<object, object>>(body, param).Compile();
        }
    }

    private class FieldGetter
    {
        private readonly Func<object> _get;
        public FieldGetter(object obj, FieldInfo field, PropertyInfo valueProperty = null)
        {
            if (valueProperty != null)
            {
                var outer = Compiled(obj, field);
                var inner = CompiledDynamic(valueProperty);
                _get = () => { var tv = outer(); return tv != null ? inner(tv) : null; };
            }
            else _get = Compiled(obj, field);
        }
        public object GetValue() => _get();
        private static Func<object> Compiled(object obj, FieldInfo f)
        {
            var inst = Expression.Constant(obj);
            var body = Expression.Convert(Expression.Field(inst, f), typeof(object));
            return Expression.Lambda<Func<object>>(body).Compile();
        }
        private static Func<object, object> CompiledDynamic(PropertyInfo p)
        {
            var param = Expression.Parameter(typeof(object));
            var cast  = Expression.Convert(param, p.DeclaringType);
            var body  = Expression.Convert(Expression.Property(cast, p), typeof(object));
            return Expression.Lambda<Func<object, object>>(body, param).Compile();
        }
    }

    // ── Formatters ────────────────────────────────────────────────────────────

    private Func<object, object> CreateFormatterFromName(string name)
    {
        var f = FormatterFactory.Get(name);
        if (f == null) GD.PushWarning($"Links: unknown formatter '{name}'");
        return f;
    }

    private Func<object, object> CreateAutoTypeConverter(MemberInfo source, MemberInfo target)
    {
        var src = GetMemberType(source);
        var tgt = GetMemberType(target);

        if (src == typeof(StringName) && tgt == typeof(string)) return v => v?.ToString();
        if (src == typeof(NodePath)   && tgt == typeof(string)) return v => v?.ToString();
        if (src == typeof(Variant)    && tgt == typeof(string)) return v => v?.ToString();
        if (src == typeof(string) && tgt == typeof(StringName)) return v => new StringName(v?.ToString() ?? "");
        return null;
    }

    // ── Status ────────────────────────────────────────────────────────────────

    private void UpdateStatus()
    {
        var parent = GetParent();
        var targetName = parent?.Name.ToString() ?? "No parent";
        var enabled = _bindings.Count(b => b != null && b.Enabled);

        _status = $"Target: {targetName}\nBindings: {enabled}/{_bindings.Count} enabled\n";
        if (_bindings.Count > 0)
        {
            _status += "\nConfigured:\n";
            for (int i = 0; i < _bindings.Count; i++)
            {
                var c = _bindings[i];
                _status += c == null
                    ? $"{i + 1}. ✗ [null]\n"
                    : $"{i + 1}. {(c.Enabled ? "✓" : "✗")} {c.GetDisplayName()}\n";
            }
        }
        else _status += "\nNo bindings configured.";
    }

    // ── BindingManager lookup ─────────────────────────────────────────────────

    private BindingManager GetBindingManager()
    {
        if (!GodotIoC.Instance.TryResolve<BindingManager>(out var mgr))
        {
            mgr = new BindingManager();
            GodotIoC.Instance.Register(mgr);
        }
        return mgr;
    }
}

#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GIoC.Links;

[Tool]
[GlobalClass]
public partial class LinkConfigInspectorPlugin : EditorInspectorPlugin
{
    public override bool _CanHandle(GodotObject @object) => @object is LinkConfig;

    public override bool _ParseProperty(GodotObject @object, Variant.Type type, string name,
        PropertyHint hintType, string hintString, PropertyUsageFlags usageFlags, bool wide)
    {
        if (@object is LinkConfig config)
        {
            if (name == "SourcePropertyPath")
            {
                AddPropertyEditor(name, new LinkPropertyEditor(config, isSource: true));
                return true;
            }
            if (name == "TargetPropertyPath")
            {
                AddPropertyEditor(name, new LinkPropertyEditor(config, isSource: false));
                return true;
            }
        }
        return false;
    }
}

[Tool]
public partial class LinkPropertyEditor : EditorProperty
{
    private OptionButton _optionButton;
    private LinkConfig _config;
    private bool _isSource;
    private bool _updating;
    private Links _linksNode;

    public LinkPropertyEditor() { }

    public LinkPropertyEditor(LinkConfig config, bool isSource)
    {
        _config = config;
        _isSource = isSource;

        _optionButton = new OptionButton();
        _optionButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _optionButton.ItemSelected += OnItemSelected;
        AddChild(_optionButton);

        CallDeferred(nameof(Populate));
    }

    private Links FindLinksNode()
    {
        try
        {
            var selected = EditorInterface.Singleton?.GetSelection()?.GetSelectedNodes();
            if (selected != null)
                foreach (var n in selected)
                    if (n is Links l) return l;
        }
        catch { }
        return null;
    }

    private void Populate()
    {
        _optionButton.Clear();
        _optionButton.AddItem("(none)", 0);

        try
        {
            _linksNode ??= FindLinksNode();
            if (_linksNode == null) return;

            Type targetType;
            if (_isSource)
            {
                if (string.IsNullOrEmpty(_config.SourceNodePath)) return;
                Node sourceNode = null;
                if (_linksNode.HasNode(_config.SourceNodePath))
                    sourceNode = _linksNode.GetNode(_config.SourceNodePath);
                else
                {
                    var root = EditorInterface.Singleton?.GetEditedSceneRoot();
                    if (root != null && root.HasNode(_config.SourceNodePath))
                        sourceNode = root.GetNode(_config.SourceNodePath);
                }
                if (sourceNode == null) return;
                targetType = GetScriptType(sourceNode);
            }
            else
            {
                var parentNode = _linksNode.GetParent();
                if (parentNode == null) return;
                targetType = GetScriptType(parentNode);
            }

            if (targetType == null) return;

            var items = new List<string>();
            if (!_isSource && targetType == typeof(ItemList))
                items.Add("Items (Collection)");

            CollectMembers(targetType, "", 0, items);

            foreach (var item in items.Distinct().OrderBy(s => s))
                _optionButton.AddItem(item);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"LinkPropertyEditor: {ex.Message}");
        }

        _UpdateProperty();
    }

    private Type GetScriptType(Node node)
    {
        try
        {
            var script = node.GetScript();
            if (script.Obj != null && script.AsGodotObject() is CSharpScript cs)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(cs.ResourcePath);
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetTypes().FirstOrDefault(x => x.Name == name && x.IsSubclassOf(typeof(Node)));
                    if (t != null) return t;
                }
            }
        }
        catch { }
        return node.GetType();
    }

    private static bool IsTrackedValue(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition().FullName == "GIoC.Bindings.TrackedValue`1";

    private void CollectMembers(Type type, string prefix, int depth, List<string> items)
    {
        if (depth > 2) return;

        var flags = BindingFlags.Public | BindingFlags.Instance;
        foreach (var prop in type.GetProperties(flags).Where(p => _isSource ? p.CanRead : p.CanWrite))
        {
            if (prop.Name.StartsWith("_")) continue;
            var path = prefix.Length > 0 ? $"{prefix}.{prop.Name}" : prop.Name;

            if (IsTrackedValue(prop.PropertyType))
            {
                var inner = prop.PropertyType.GetGenericArguments()[0];
                items.Add($"{path} ({inner.Name})");
            }
            else
            {
                items.Add(path);
                if (depth < 2 && !ShouldSkip(prop.PropertyType))
                    CollectMembers(prop.PropertyType, path, depth + 1, items);
            }
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (field.Name.StartsWith("_")) continue;
            var path = prefix.Length > 0 ? $"{prefix}.{field.Name}" : field.Name;
            items.Add(path);
            if (depth < 2 && !ShouldSkip(field.FieldType) && !IsTrackedValue(field.FieldType))
                CollectMembers(field.FieldType, path, depth + 1, items);
        }
    }

    private static bool ShouldSkip(Type t) =>
        t.IsPrimitive || t == typeof(string) || t.IsEnum ||
        (t.Namespace?.StartsWith("Godot") == true && t.IsValueType);

    private void OnItemSelected(long index)
    {
        if (_updating) return;
        var text = index == 0 ? "" : _optionButton.GetItemText((int)index);
        EmitChanged(GetEditedProperty(), text);
    }

    public override void _UpdateProperty()
    {
        _updating = true;
        var current = GetEditedObject()?.Get(GetEditedProperty()).AsString() ?? "";
        bool found = false;
        for (int i = 0; i < _optionButton.ItemCount; i++)
        {
            if (_optionButton.GetItemText(i) == current)
            {
                _optionButton.Selected = i;
                found = true;
                break;
            }
        }
        if (!found) _optionButton.Selected = 0;
        _updating = false;
    }
}
#endif

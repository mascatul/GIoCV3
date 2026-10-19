using GIoC.Bindings;
using Godot;
using System;
using System.Linq;

namespace GIoC.Links;

/// <summary>
/// Configuration for a single inspector-driven data binding.
/// Add one or more of these as entries in the Links node's Bindings array.
/// </summary>
[Tool]
[GlobalClass]
public partial class LinkConfig : Resource
{
    private NodePath _sourceNodePath = "";
    private string _sourcePropertyPath = "";
    private string _targetPropertyPath = "";
    private BindingDirection _direction = BindingDirection.OneWay;
    private bool _enabled = true;
    private string _formatter = "";

    [Signal]
    public delegate void ConfigChangedEventHandler(LinkConfig config);

    [Export]
    public NodePath SourceNodePath
    {
        get => _sourceNodePath;
        set { if (_sourceNodePath != value) { _sourceNodePath = value; NotifyChanged(); NotifyPropertyListChanged(); } }
    }

    [Export]
    public string SourcePropertyPath
    {
        get => _sourcePropertyPath;
        set { if (_sourcePropertyPath != value) { _sourcePropertyPath = value; NotifyChanged(); } }
    }

    [Export]
    public string TargetPropertyPath
    {
        get => _targetPropertyPath;
        set { if (_targetPropertyPath != value) { _targetPropertyPath = value; NotifyChanged(); } }
    }

    [Export(PropertyHint.Enum, "OneWay:0,TwoWay:1,OneWayToSource:2")]
    public int Direction
    {
        get => (int)_direction;
        set { if (_direction != (BindingDirection)value) { _direction = (BindingDirection)value; NotifyChanged(); } }
    }

    [Export]
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; NotifyChanged(); } }
    }

    /// <summary>
    /// Optional pre-compiled formatter from FormatterFactory.
    /// Examples: ToString, Invert, HealthFormat, TimeMMSS, ToPercent.
    /// Register custom formatters: FormatterFactory.Register("MyFmt", x => ...);
    /// Leave empty for automatic type conversion.
    /// </summary>
    [Export]
    public string Formatter
    {
        get => _formatter;
        set { if (_formatter != value) { _formatter = value; NotifyChanged(); } }
    }

    public LinkConfig() { }

    private void NotifyChanged()
    {
        EmitSignal(SignalName.ConfigChanged, this);
        EmitChanged();
    }

#if TOOLS
    public override void _ValidateProperty(Godot.Collections.Dictionary property)
    {
        if (property["name"].AsString() == "Formatter")
        {
            try
            {
                var names = FormatterFactory.GetAllFormatterNames();
                property["hint"] = (int)PropertyHint.Enum;
                property["hint_string"] = string.Join(",", names);
            }
            catch (Exception ex)
            {
                GD.PushWarning($"LinkConfig: Failed to populate formatter dropdown - {ex.Message}");
            }
        }
    }
#endif

    public string GetDisplayName()
    {
        var src = string.IsNullOrEmpty(_sourceNodePath) ? "None"
            : _sourceNodePath.ToString().Split('/').Last();
        var prop = string.IsNullOrEmpty(_sourcePropertyPath) ? "?" : _sourcePropertyPath;
        var tgt = string.IsNullOrEmpty(_targetPropertyPath) ? "?" : _targetPropertyPath;
        var arrow = _direction == BindingDirection.TwoWay ? "↔" : "→";
        return $"{src}.{prop} {arrow} {tgt}";
    }
}

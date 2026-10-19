using Godot;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;

namespace GIoC.Bindings;

/// <summary>
/// Wraps a value and automatically marks bindings dirty when the value changes.
/// Alternative to [Observable]/INPC — use when you want explicit dirty tracking
/// without source generators.
/// </summary>
public class TrackedValue<T> : ITrackedValue
{
    private static bool _enableDebugLogging = false;

    private T _value;
    private Binding _binding;
    private readonly BindingManager _manager;
    private readonly GodotObject _owner;
    private readonly string _propertyName;
    private INotifyCollectionChanged _observableCollection;
    private bool _isRegistered = false;

    public static void SetDebugLogging(bool enable) => _enableDebugLogging = enable;

    public T Value
    {
        get { EnsureRegistered(); return _value; }
        set
        {
            EnsureRegistered();
            if (!EqualityComparer<T>.Default.Equals(_value, value))
            {
                UnsubscribeFromCollectionChanged();
                _value = value;
                SubscribeToCollectionChanged();

                if (_binding != null)
                {
                    _binding.MarkDirty();
                    _manager?.MarkBindingDirty(_binding);
                }
            }
        }
    }

    private TrackedValue(T initialValue, BindingManager manager, GodotObject owner, string propertyName)
    {
        _value = initialValue;
        _manager = manager;
        _owner = owner;
        _propertyName = propertyName;
        SubscribeToCollectionChanged();
    }

    public void EnsureRegistered()
    {
        if (_isRegistered || _owner == null) return;

        var mgr = _manager;
        if (mgr == null)
        {
            try { mgr = GodotIoC.Instance.Resolve<BindingManager>(); }
            catch { return; }
        }
        if (mgr == null) return;

        try
        {
            mgr.RegisterTrackedValue(_owner.GetInstanceId(), _propertyName, this);
            _isRegistered = true;
        }
        catch { }
    }

    /// <summary>Create a tracked value registered with the BindingManager.</summary>
    public static TrackedValue<T> Create(T initialValue, BindingManager manager, GodotObject owner, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (string.IsNullOrEmpty(propertyName)) throw new ArgumentNullException(nameof(propertyName));
        return new TrackedValue<T>(initialValue, manager, owner, propertyName);
    }

    /// <summary>Create a tracked value that resolves BindingManager from IoC on first access.</summary>
    public static TrackedValue<T> CreateDeferred(T initialValue = default, GodotObject owner = null, string propertyName = null)
        => new TrackedValue<T>(initialValue, null, owner, propertyName);

    public void AttachBinding(Binding binding)
    {
        if (binding == null) return;
        EnsureRegistered();
        _binding = binding;
        binding.SetTracked(true);
        _binding.MarkDirty();
        _manager?.MarkBindingDirty(_binding);
    }

    public void NotifyChanged()
    {
        if (_binding != null)
        {
            _binding.MarkDirty();
            _manager?.MarkBindingDirty(_binding);
        }
    }

    private void SubscribeToCollectionChanged()
    {
        if (_value is INotifyCollectionChanged c)
        {
            _observableCollection = c;
            _observableCollection.CollectionChanged += OnCollectionChanged;
        }
    }

    private void UnsubscribeFromCollectionChanged()
    {
        if (_observableCollection != null)
        {
            _observableCollection.CollectionChanged -= OnCollectionChanged;
            _observableCollection = null;
        }
    }

    private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (_binding != null)
        {
            _binding.MarkDirty();
            _manager?.MarkBindingDirty(_binding);
        }
    }

    public static implicit operator T(TrackedValue<T> tracked) => tracked != null ? tracked.Value : default;
    public override string ToString() => _value?.ToString() ?? "null";
    public override bool Equals(object obj)
    {
        if (obj is TrackedValue<T> other) return EqualityComparer<T>.Default.Equals(_value, other._value);
        if (obj is T v) return EqualityComparer<T>.Default.Equals(_value, v);
        return false;
    }
    public override int GetHashCode() => _value?.GetHashCode() ?? 0;
}

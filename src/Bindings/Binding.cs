using Godot;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;

namespace GIoC.Bindings;

public class Binding
{
    public Func<object> SourceGetter { get; }
    public Func<object> TargetGetter { get; }
    public Func<object, object> Formatter { get; }
    public ulong GodotObjectId { get; set; }

    internal NotifyCollectionChangedEventHandler _collectionChangedHandler;
    private string _targetPropertyName;
    private string _sourcePropertyName;

    public string TargetPropertyName => _targetPropertyName;
    public string SourcePropertyName => _sourcePropertyName;

    private object _lastSourceValue;
    private bool _isDirty = true;
    private bool _isValid = true;
    private bool _isTracked = false;

    private Action<object, object> _cachedSetter;
    private Func<object, object> _cachedGetter;
    private bool _accessorsCached = false;

    private PropertyInfo _cachedPropertyInfo;
    private Type _cachedTargetType;

    private static bool _enableExtensiveDebugLogging = false;

    public static void SetExtensiveDebugLogging(bool enabled) =>
        _enableExtensiveDebugLogging = enabled;

    public Binding(ulong gdId, Func<object> sourceGetter, Func<object> targetGetter,
        string targetPropertyExplicitName = null, Func<object, object> formatter = null,
        string sourcePropertyExplicitName = null)
    {
        GodotObjectId = gdId;
        SourceGetter = sourceGetter;
        TargetGetter = targetGetter;
        Formatter = formatter;
        _targetPropertyName = targetPropertyExplicitName;
        _sourcePropertyName = sourcePropertyExplicitName;
    }

    public void MarkDirty() => _isDirty = true;
    public bool IsValid() => _isValid;
    public void Invalidate() => _isValid = false;
    public void SetTracked(bool tracked) => _isTracked = tracked;
    public bool IsTracked() => _isTracked;

    private void CachePropertyAccessors()
    {
        if (_accessorsCached) return;

        var target = TargetGetter();
        if (target == null) return;

        var targetType = target.GetType();
        var propertyName = string.IsNullOrEmpty(_targetPropertyName)
            ? _lastSourceValue?.GetType().Name
            : _targetPropertyName;

        if (propertyName == "Items" && target is ItemList)
        {
            _cachedSetter = (obj, value) => { };
            _cachedGetter = (obj) => obj;
            _accessorsCached = true;
            return;
        }

        if (!string.IsNullOrEmpty(propertyName))
        {
            var property = targetType.GetProperty(propertyName,
                BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.CanWrite)
            {
                _cachedSetter = (obj, value) => property.SetValue(obj, value);
                _cachedGetter = (obj) => property.GetValue(obj);
                _accessorsCached = true;
                return;
            }
        }

        var properties = targetType.GetProperties();
        foreach (var prop in properties)
        {
            if (prop.CanWrite && _lastSourceValue != null &&
                prop.PropertyType.IsAssignableFrom(_lastSourceValue.GetType()))
            {
                _cachedSetter = (obj, value) => prop.SetValue(obj, value);
                _cachedGetter = (obj) => prop.GetValue(obj);
                _accessorsCached = true;
                break;
            }
        }
    }

    public void UpdateTarget()
    {
        if (!_isValid) return;

        var sourceValue = SourceGetter();

        if (!_isDirty && Equals(sourceValue, _lastSourceValue)) return;

        _lastSourceValue = sourceValue;

        if (sourceValue is ObservableCollection<string> stringCollection)
        {
            SyncItemList(stringCollection, null);
            if (_collectionChangedHandler == null)
            {
                _collectionChangedHandler = (sender, e) => OnCollectionChanged(sender, e, null);
                stringCollection.CollectionChanged += _collectionChangedHandler;
            }
            _isDirty = false;
        }
        else if (sourceValue is ObservableCollection<KeyValuePair<string, Texture2D>> kvpCollection)
        {
            SyncItemList(null, kvpCollection);
            if (_collectionChangedHandler == null)
            {
                _collectionChangedHandler = (sender, e) => OnCollectionChanged(sender, e, null);
                kvpCollection.CollectionChanged += _collectionChangedHandler;
            }
            _isDirty = false;
        }
        else
        {
            if (sourceValue == null) return;

            var targetValue = Formatter != null ? Formatter(sourceValue) : sourceValue;
            var target = TargetGetter();
            if (target == null) return;

            CachePropertyAccessors();

            if (_cachedSetter != null)
            {
                try
                {
                    _cachedSetter(target, targetValue);
                    _isDirty = false;
                    return;
                }
                catch (ObjectDisposedException) { Invalidate(); return; }
                catch { }
            }

            var propertyName = string.IsNullOrEmpty(_targetPropertyName)
                ? sourceValue.GetType().Name
                : _targetPropertyName;

            var targetType = target.GetType();
            if (_cachedPropertyInfo == null || _cachedTargetType != targetType)
            {
                _cachedPropertyInfo = targetType.GetProperty(propertyName,
                    BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                _cachedTargetType = targetType;
            }

            if (_cachedPropertyInfo != null && _cachedPropertyInfo.CanWrite)
            {
                try
                {
                    _cachedPropertyInfo.SetValue(target, targetValue, null);
                    _isDirty = false;
                    return;
                }
                catch (ObjectDisposedException) { Invalidate(); return; }
                catch (Exception ex) { Debug.WriteLine($"Binding: {ex.Message}"); }
            }

            Debug.WriteLine($"Binding: no suitable property on '{target.GetType().Name}' for '{targetValue?.GetType().Name}'");
        }

        _isDirty = false;
    }

    private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e, object _)
    {
        var itemList = TargetGetter() as ItemList;
        if (itemList == null) return;

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (sender is ObservableCollection<string>)
                    foreach (var item in e.NewItems) itemList.AddItem(item as string);
                else if (sender is ObservableCollection<KeyValuePair<string, Texture2D>>)
                    foreach (KeyValuePair<string, Texture2D> kvp in e.NewItems)
                        itemList.AddItem(kvp.Key, kvp.Value);
                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (var item in e.OldItems)
                {
                    int idx = FindItemIndex(itemList, item);
                    if (idx != -1) itemList.RemoveItem(idx);
                }
                break;
            case NotifyCollectionChangedAction.Replace:
                if (sender is ObservableCollection<string>)
                    for (int i = 0; i < e.NewItems.Count; i++)
                        itemList.SetItemText(e.NewStartingIndex + i, e.NewItems[i] as string);
                else if (sender is ObservableCollection<KeyValuePair<string, Texture2D>>)
                    for (int i = 0; i < e.NewItems.Count; i++)
                    {
                        var kvp = (KeyValuePair<string, Texture2D>)e.NewItems[i];
                        itemList.SetItemText(e.NewStartingIndex + i, kvp.Key);
                        itemList.SetItemIcon(e.NewStartingIndex + i, kvp.Value);
                    }
                break;
            case NotifyCollectionChangedAction.Reset:
                if (sender is ObservableCollection<string> sc) SyncItemList(sc, null);
                else if (sender is ObservableCollection<KeyValuePair<string, Texture2D>> kc) SyncItemList(null, kc);
                break;
        }
    }

    private void SyncItemList(ObservableCollection<string> strings,
        ObservableCollection<KeyValuePair<string, Texture2D>> kvps)
    {
        var itemList = TargetGetter() as ItemList;
        if (itemList == null) return;
        itemList.Clear();
        if (strings != null)
            foreach (var s in strings) itemList.AddItem(s);
        else if (kvps != null)
            foreach (var kvp in kvps) itemList.AddItem(kvp.Key, kvp.Value);
    }

    private int FindItemIndex(ItemList itemList, object item)
    {
        for (int i = 0; i < itemList.ItemCount; i++)
            if (itemList.GetItemText(i) == item?.ToString()) return i;
        return -1;
    }
}

namespace GIoC;

/// <summary>
/// Apply to a private field in a partial class to generate a public INPC property.
/// The generated property fires PropertyChanged when its value changes.
///
///   [Observable] private float _health = 100f;
///   → generates: public float Health { get; set; }  (with PropertyChanged notification)
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ObservableAttribute : Attribute { }

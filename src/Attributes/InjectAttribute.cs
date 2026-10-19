namespace GIoC;

/// <summary>
/// Marks a field for dependency injection by GodotIoC.InjectFields().
///
///   [Inject] private AudioManager _audio;
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class InjectAttribute : Attribute
{
    public string? Name { get; }
    public InjectAttribute(string? name = null) => Name = name;
}

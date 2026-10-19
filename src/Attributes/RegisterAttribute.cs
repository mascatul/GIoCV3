namespace GIoC;

/// <summary>
/// Marks a class as auto-registrable in GodotIoC.
/// Paired with GodotIoC.Register() to make the instance resolvable via [Inject].
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RegisterAttribute : Attribute
{
    public string? Name { get; }
    public RegisterAttribute(string? name = null) => Name = name;
}

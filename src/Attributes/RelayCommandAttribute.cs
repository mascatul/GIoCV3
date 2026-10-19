namespace GIoC;

/// <summary>
/// Apply to a private method to generate a lazy RelayCommand or AsyncRelayCommand property.
///
///   [RelayCommand] private void Save() { ... }
///   → generates: public RelayCommand SaveCommand { get; }
///
///   [RelayCommand] private async Task LoadAsync() { ... }
///   → generates: public AsyncRelayCommand LoadCommand { get; }  (strips Async suffix)
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RelayCommandAttribute : Attribute { }

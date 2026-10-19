namespace GIoC.Bindings;

public interface ITrackedValue
{
    void AttachBinding(Binding binding);
    void EnsureRegistered();
}

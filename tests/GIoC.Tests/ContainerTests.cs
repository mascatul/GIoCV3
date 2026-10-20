using System.Runtime.CompilerServices;
using Xunit;

namespace GIoC.Tests;

// Test doubles. Every test builds its own container (the constructor is internal and visible to this project),
// so nothing leaks between tests, and no Godot engine is needed: plain objects never touch native code.
public class SvcA { }
public class SvcB { }
public class SvcC { }
public interface IAudio { }
public class Audio : IAudio { }

public class PlainManager
{
    [Inject] private SvcA _a = null!;
    public SvcA A => _a;
}

public class ReadyManager : IGIoCReadyCallback
{
    [Inject] private SvcA _a = null!;
    [Inject] private SvcB _b = null!;
    public SvcA A => _a;
    public SvcB B => _b;
    public int ReadyCalls;
    public bool BothSetWhenReady;
    public void OnGIoCReady()
    {
        ReadyCalls++;
        BothSetWhenReady = _a != null && _b != null;
    }
}

public class NoFieldsReady : IGIoCReadyCallback
{
    public int ReadyCalls;
    public void OnGIoCReady() => ReadyCalls++;
}

public class BaseWithPrivateInject
{
    [Inject] private SvcA _baseA = null!;
    public SvcA BaseA => _baseA;
}
public class DerivedFromBase : BaseWithPrivateInject
{
    [Inject] public SvcB PublicB = null!;
}

public class PreFilled
{
    [Inject] private SvcA _a = new();
    public SvcA A => _a;
}

public class NamedConsumer
{
    [Inject("first")] private SvcA _first = null!;
    [Inject("second")] private SvcA _second = null!;
    public SvcA First => _first;
    public SvcA Second => _second;
}

public class CycleA { [Inject] private CycleB _b = null!; public CycleB B => _b; }
public class CycleB { [Inject] private CycleA _a = null!; public CycleA A => _a; }

public class Dependent
{
    [Inject] private ReadyManager _manager = null!;
    public ReadyManager Manager => _manager;
    public bool ManagerWasReadyWhenInjected;
}

public class ThrowingReady : IGIoCReadyCallback
{
    [Inject] private SvcC _c = null!;
    public void OnGIoCReady() => throw new InvalidOperationException("boom");
}

public class LazyConsumer : IGIoCReadyCallback
{
    [Inject] private SvcA _a = null!;
    public SvcA A => _a;
    public int ReadyCalls;
    public static int Constructed;
    public LazyConsumer() => Constructed++;
    public void OnGIoCReady() => ReadyCalls++;
}

public class FactoryA { [Inject] private FactoryB _b = null!; public FactoryB B => _b; }
public class FactoryB { }

public class ContainerTests
{
    private static GodotIoC NewIoc() => new();

    // ── Baseline behaviour (must keep working) ────────────────────────────────

    [Fact]
    public void Register_Instance_ResolvesTheSameInstance()
    {
        var ioc = NewIoc();
        var a = new SvcA();
        ioc.Register(a);
        Assert.Same(a, ioc.Resolve<SvcA>());
    }

    [Fact]
    public void Resolve_Missing_Throws_And_TryResolve_ReturnsFalse()
    {
        var ioc = NewIoc();
        Assert.Throws<InvalidOperationException>(() => ioc.Resolve<SvcA>());
        Assert.False(ioc.TryResolve<SvcA>(out var found));
        Assert.Null(found);
    }

    [Fact]
    public void NamedRegistrations_AreDistinct()
    {
        var ioc = NewIoc();
        var one = new SvcA();
        var two = new SvcA();
        ioc.Register(one, "one");
        ioc.Register(two, "two");
        Assert.Same(one, ioc.Resolve<SvcA>("one"));
        Assert.Same(two, ioc.Resolve<SvcA>("two"));
        Assert.False(ioc.TryResolve<SvcA>(out _));   // unnamed was never registered
    }

    // ── InjectFields on plain classes ─────────────────────────────────────────

    [Fact]
    public void InjectFields_FillsPrivateFieldOfPlainObject()
    {
        var ioc = NewIoc();
        var a = new SvcA();
        ioc.Register(a);

        var manager = new PlainManager();
        ioc.InjectFields(manager);

        Assert.Same(a, manager.A);
    }

    [Fact]
    public void InjectFields_ThrowsForNullAndValueTypes()
    {
        var ioc = NewIoc();
        Assert.Throws<ArgumentNullException>(() => ioc.InjectFields((object)null!));
        Assert.Throws<ArgumentException>(() => ioc.InjectFields((object)42));
    }

    [Fact]
    public void InjectFields_IncludesPublicFieldsAndPrivateFieldsOfBaseClasses()
    {
        var ioc = NewIoc();
        var a = new SvcA();
        var b = new SvcB();
        ioc.Register(a);
        ioc.Register(b);

        var obj = new DerivedFromBase();
        ioc.InjectFields(obj);

        Assert.Same(b, obj.PublicB);
        Assert.Same(a, obj.BaseA);   // private field declared in the base class
    }

    [Fact]
    public void InjectFields_LeavesAlreadyFilledFieldsAlone()
    {
        var ioc = NewIoc();
        ioc.Register(new SvcA());
        var obj = new PreFilled();
        var original = obj.A;

        ioc.InjectFields(obj);

        Assert.Same(original, obj.A);
    }

    [Fact]
    public void InjectFields_ResolvesByName()
    {
        var ioc = NewIoc();
        var first = new SvcA();
        var second = new SvcA();
        ioc.Register(first, "first");
        ioc.Register(second, "second");

        var consumer = new NamedConsumer();
        ioc.InjectFields(consumer);

        Assert.Same(first, consumer.First);
        Assert.Same(second, consumer.Second);
    }

    [Fact]
    public void InterfaceTypedField_ResolvesWhenRegisteredAsThatInterface()
    {
        var ioc = NewIoc();
        var audio = new Audio();
        ioc.Register<IAudio>(audio);
        Assert.Same(audio, ioc.Resolve<IAudio>());
    }

    // ── Late dependencies and the ready callback ──────────────────────────────

    [Fact]
    public void MissingDependency_StaysNull_ThenIsFilledWhenRegisteredLater()
    {
        var ioc = NewIoc();
        var manager = new PlainManager();
        ioc.InjectFields(manager);
        Assert.Null(manager.A);

        var a = new SvcA();
        ioc.Register(a);

        Assert.Same(a, manager.A);
    }

    [Fact]
    public void OnReady_WaitsForTheLastDependency_AndFiresExactlyOnce()
    {
        var ioc = NewIoc();
        var manager = new ReadyManager();
        ioc.InjectFields(manager);
        Assert.Equal(0, manager.ReadyCalls);

        ioc.Register(new SvcA());
        Assert.Equal(0, manager.ReadyCalls);            // still missing SvcB

        ioc.Register(new SvcB());
        Assert.Equal(1, manager.ReadyCalls);
        Assert.True(manager.BothSetWhenReady);

        ioc.InjectFields(manager);                      // explicit second pass
        ioc.Register(new SvcA());                       // re-registering an already-satisfied type
        Assert.Equal(1, manager.ReadyCalls);
    }

    [Fact]
    public void OnReady_FiresImmediately_ForAClassWithNoInjectFields()
    {
        var ioc = NewIoc();
        var obj = new NoFieldsReady();
        ioc.InjectFields(obj);
        Assert.Equal(1, obj.ReadyCalls);
    }

    [Fact]
    public void QueuedTwice_IsStillInjectedAndNotifiedOnce()
    {
        var ioc = NewIoc();
        var manager = new ReadyManager();
        ioc.InjectFields(manager);
        ioc.InjectFields(manager);

        ioc.Register(new SvcA());
        ioc.Register(new SvcB());

        Assert.Equal(1, manager.ReadyCalls);
    }

    // ── Register(instance) injects plain objects ──────────────────────────────

    [Fact]
    public void Register_PlainInstance_InjectsItsOwnFields()
    {
        var ioc = NewIoc();
        var a = new SvcA();
        ioc.Register(a);

        var manager = new PlainManager();
        ioc.Register(manager);

        Assert.Same(a, manager.A);
    }

    [Fact]
    public void Register_PlainInstance_FiresReady_AndIsResolvable()
    {
        var ioc = NewIoc();
        ioc.Register(new SvcA());
        ioc.Register(new SvcB());
        var manager = new ReadyManager();
        ioc.Register(manager);

        Assert.Equal(1, manager.ReadyCalls);
        Assert.Same(manager, ioc.Resolve<ReadyManager>());
    }

    [Fact]
    public void Register_PlainInstance_WithMissingDependency_IsCompletedByALaterRegistration()
    {
        var ioc = NewIoc();
        var manager = new PlainManager();
        ioc.Register(manager);               // SvcA not there yet
        Assert.Null(manager.A);

        var a = new SvcA();
        ioc.Register(a);

        Assert.Same(a, manager.A);
        Assert.Same(manager, ioc.Resolve<PlainManager>());
    }

    [Fact]
    public void Dependents_SeeAFullyInjectedManager()
    {
        var ioc = NewIoc();
        var dependent = new Dependent();
        ioc.Register(dependent);             // waits for ReadyManager

        ioc.Register(new SvcA());
        ioc.Register(new SvcB());
        var manager = new ReadyManager();
        ioc.Register(manager);               // injected + ready BEFORE it is announced to dependents

        Assert.Same(manager, dependent.Manager);
        Assert.True(manager.BothSetWhenReady);
        Assert.Equal(1, manager.ReadyCalls);
    }

    [Fact]
    public void CircularInjectFields_BetweenRegisteredObjects_Resolves()
    {
        var ioc = NewIoc();
        var a = new CycleA();
        var b = new CycleB();
        ioc.Register(a);
        ioc.Register(b);

        Assert.Same(b, a.B);
        Assert.Same(a, b.A);
    }

    // ── Lazy and factory registrations inject the objects they create ─────────

    [Fact]
    public void LazyRegistration_CreatesOnce_AndInjectsTheCreatedObject()
    {
        var ioc = NewIoc();
        var a = new SvcA();
        ioc.Register(a);
        ioc.Register<LazyConsumer>();
        var before = LazyConsumer.Constructed;

        var first = ioc.Resolve<LazyConsumer>();
        var second = ioc.Resolve<LazyConsumer>();

        Assert.Same(first, second);
        Assert.Equal(before + 1, LazyConsumer.Constructed);
        Assert.Same(a, first.A);
        Assert.Equal(1, first.ReadyCalls);
    }

    [Fact]
    public void LazyRegistration_WithMissingDependency_IsFilledLater()
    {
        var ioc = NewIoc();
        ioc.Register<LazyConsumer>();
        var consumer = ioc.Resolve<LazyConsumer>();
        Assert.Null(consumer.A);

        var a = new SvcA();
        ioc.Register(a);

        Assert.Same(a, consumer.A);
        Assert.Equal(1, consumer.ReadyCalls);
    }

    [Fact]
    public void FactoryRegistration_RunsOnce_AndInjectsTheResult()
    {
        var ioc = NewIoc();
        int calls = 0;
        ioc.Register(() => { calls++; return new PlainManager(); });
        var a = new SvcA();
        ioc.Register(a);

        var one = ioc.Resolve<PlainManager>();
        var two = ioc.Resolve<PlainManager>();

        Assert.Equal(1, calls);
        Assert.Same(one, two);
        Assert.Same(a, one.A);
    }

    [Fact]
    public void LazyDependency_IsCreatedOnDemandWhileInjecting()
    {
        var ioc = NewIoc();
        ioc.Register<SvcA>();                        // lazy, not created yet
        var manager = new PlainManager();

        ioc.InjectFields(manager);

        Assert.NotNull(manager.A);
        Assert.Same(manager.A, ioc.Resolve<SvcA>());
    }

    [Fact]
    public void Factories_AreNotRunWhileTheContainerLockIsHeld()
    {
        var ioc = NewIoc();
        ioc.Register(() =>
        {
            // If the factory ran inside the lock, another thread could not resolve anything until it returned.
            var other = Task.Run(() => ioc.TryResolve<SvcB>(out _));
            Assert.True(other.Wait(TimeSpan.FromSeconds(5)), "another thread was blocked by the container lock");
            return new SvcA();
        });

        Assert.NotNull(ioc.Resolve<SvcA>());
    }

    [Fact]
    public void CircularFactoryDependency_Throws_InsteadOfOverflowingTheStack()
    {
        var ioc = NewIoc();
        ioc.Register<FactoryA>(() => { ioc.Resolve<FactoryB>(); return new FactoryA(); });
        ioc.Register<FactoryB>(() => { ioc.Resolve<FactoryA>(); return new FactoryB(); });

        var ex = Assert.Throws<InvalidOperationException>(() => ioc.Resolve<FactoryA>());
        Assert.Contains("Circular", ex.Message);
    }

    // ── Instantiate / Release ─────────────────────────────────────────────────

    [Fact]
    public void Instantiate_CreatesAndInjects_WithoutRegistering()
    {
        var ioc = NewIoc();
        var a = new SvcA();
        ioc.Register(a);

        var manager = ioc.Instantiate<PlainManager>();

        Assert.Same(a, manager.A);
        Assert.False(ioc.TryResolve<PlainManager>(out _));
    }

    [Fact]
    public void Release_StopsALaterRegistrationFromFillingTheObject()
    {
        var ioc = NewIoc();
        var manager = new ReadyManager();
        ioc.InjectFields(manager);

        ioc.Release(manager);
        ioc.Register(new SvcA());
        ioc.Register(new SvcB());

        Assert.Null(manager.A);
        Assert.Equal(0, manager.ReadyCalls);
    }

    [Fact]
    public void Release_ClearsTheReadyState_SoTheObjectCanBeReused()
    {
        var ioc = NewIoc();
        ioc.Register(new SvcA());
        ioc.Register(new SvcB());
        var manager = new ReadyManager();
        ioc.InjectFields(manager);
        Assert.Equal(1, manager.ReadyCalls);

        ioc.Release(manager);
        ioc.InjectFields(manager);

        Assert.Equal(2, manager.ReadyCalls);
    }

    // ── Robustness ────────────────────────────────────────────────────────────

    [Fact]
    public void ObjectsMarkedDead_AreNotInjectedOrNotified()
    {
        var ioc = NewIoc();
        var dead = new ReadyManager();
        ioc.IsAlive = o => !ReferenceEquals(o, dead);
        ioc.InjectFields(dead);

        ioc.Register(new SvcA());
        ioc.Register(new SvcB());

        Assert.Null(dead.A);
        Assert.Equal(0, dead.ReadyCalls);
    }

    [Fact]
    public void ObjectDiesWhileWaiting_IsSkippedWithoutTouchingIt()
    {
        var ioc = NewIoc();
        var manager = new ReadyManager();
        ioc.InjectFields(manager);
        bool dead = false;
        ioc.IsAlive = o => !(dead && ReferenceEquals(o, manager));

        dead = true;   // e.g. QueueFree'd while waiting
        ioc.Register(new SvcA());
        ioc.Register(new SvcB());

        Assert.Equal(0, manager.ReadyCalls);
    }

    [Fact]
    public void ThrowingOnReady_IsLogged_AndDoesNotStopOtherWaitingObjects()
    {
        var ioc = NewIoc();
        var errors = new List<string>();
        ioc.LogError = errors.Add;

        var thrower = new ThrowingReady();
        var other = new ReadyManagerNeedingC();
        ioc.InjectFields(thrower);
        ioc.InjectFields(other);

        var ex = Record.Exception(() => ioc.Register(new SvcC()));

        Assert.Null(ex);
        Assert.Single(errors);
        Assert.Contains("boom", errors[0]);
        Assert.Equal(1, other.ReadyCalls);
    }

    public class ReadyManagerNeedingC : IGIoCReadyCallback
    {
        [Inject] private SvcC _c = null!;
        public int ReadyCalls;
        public void OnGIoCReady() => ReadyCalls++;
    }

    [Fact]
    public void WaitingObjects_AreHeldWeakly()
    {
        var ioc = NewIoc();
        var weak = CreateWaitingObject(ioc);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weak.TryGetTarget(out _), "the container kept a waiting object alive");
        ioc.Register(new SvcA());   // must not throw on the dead queue entry
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<PlainManager> CreateWaitingObject(GodotIoC ioc)
    {
        var manager = new PlainManager();
        ioc.InjectFields(manager);
        return new WeakReference<PlainManager>(manager);
    }

    [Fact]
    public void Reset_ClearsRegistrationsAndWaitingObjects()
    {
        var ioc = NewIoc();
        var manager = new PlainManager();
        ioc.InjectFields(manager);
        ioc.Register(new SvcB());

        ioc.Reset();
        ioc.Register(new SvcA());

        Assert.Null(manager.A);
        Assert.False(ioc.TryResolve<SvcB>(out _));
    }

    [Fact]
    public void ConcurrentRegisterAndInject_DoesNotThrow()
    {
        var ioc = NewIoc();
        var managers = Enumerable.Range(0, 200).Select(_ => new ReadyManager()).ToArray();

        var tasks = new List<Task>();
        foreach (var m in managers)
            tasks.Add(Task.Run(() => ioc.InjectFields(m)));
        tasks.Add(Task.Run(() => ioc.Register(new SvcA())));
        tasks.Add(Task.Run(() => ioc.Register(new SvcB())));

        Task.WaitAll(tasks.ToArray());

        // Every manager ended up complete exactly once, whatever the interleaving
        foreach (var m in managers)
        {
            Assert.NotNull(m.A);
            Assert.NotNull(m.B);
            Assert.Equal(1, m.ReadyCalls);
        }
    }
}

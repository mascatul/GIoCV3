# GIoC

A small dependency-injection container for **Godot 4 / C#**, with typed messaging, relay commands and a
data-binding layer. `[Inject]` works on **any class** - nodes and plain C# classes alike.

```
dotnet add package GIoC
```

The package is not on nuget.org. It is built locally with `dotnet pack` and used from a local NuGet source
(add the folder that holds `GIoC.<version>.nupkg` to your NuGet sources).

- Target: `net8.0`, Godot 4.x (`GodotSharp` is a compile-time reference; your Godot project provides it).
- The package bundles the `GIoC.SourceGenerator` Roslyn analyzer. No second package is needed for `[Register]`.

## Contents

1. [Quick start](#quick-start)
2. [Registering services](#registering-services)
3. [Injecting with `[Inject]`](#injecting-with-inject)
4. [Plain (non-Node) classes](#plain-non-node-classes)
5. [Nodes and `[Register]`](#nodes-and-register)
6. [Messenger](#messenger)
7. [Commands](#commands)
8. [Bindings, `TrackedValue` and `Links`](#bindings-trackedvalue-and-links)
9. [Generator diagnostics](#generator-diagnostics)
10. [Rules and limits](#rules-and-limits)
11. [Building and testing](#building-and-testing)

## Quick start

```csharp
using GIoC;
using Godot;

// 1. A plain service that needs another service
public class SaveGame { }

[Register]
public partial class ScoreService
{
    [Inject] private SaveGame _save = null!;           // non-public fields are fine

    partial void OnReady()                              // runs once _save is filled
    {
        GD.Print("score service ready, save = " + _save);
    }
}

// 2. Register them (autoload / bootstrapper)
public partial class Bootstrapper : Node
{
    public override void _Ready()
    {
        var ioc = GodotIoC.Instance;
        ioc.ConnectAutoInject(GetTree());               // optional: injects [Register] nodes as they enter the tree
        ioc.Register(new SaveGame());
        ioc.Register(new ScoreService());               // its [Inject] fields are filled right here
    }
}

// 3. Use it from a node
[Register]
public partial class Hud : Control
{
    [Inject] private ScoreService _score = null!;

    partial void OnReady() => GD.Print(_score);         // replaces your _Ready() logic
}
```

Registration order does not matter. If a dependency is not registered yet, the dependent object waits
and is filled as soon as the dependency is registered.

## Registering services

```csharp
var ioc = GodotIoC.Instance;

ioc.Register(new ScoreService());                  // eager: you create it
ioc.Register<ScoreService>();                      // lazy: created on first resolve (parameterless ctor)
ioc.Register<ScoreService>(() => new ScoreService(dep));   // factory: called once, result cached
ioc.Register(new Audio(), "music");                // named registration (also available on the overloads above)
ioc.Register<IAudio>(new Audio());                 // register under an interface; inject a field of that interface type

ioc.RegisterAndAdd(new SceneChanger(), this);      // register a Node and add it as a child of 'this'

var score = ioc.Resolve<ScoreService>();           // throws InvalidOperationException if not registered
if (ioc.TryResolve<ScoreService>(out var s)) { }
```

All registrations are singletons: the first resolve caches the instance. `ioc.Reset()` clears everything
(handy in tests).

## Injecting with `[Inject]`

```csharp
[Inject] private ScoreService _score = null!;           // by type
[Inject("music")] private IAudio _music = null!;        // by name
```

- Works on **fields** (public or non-public), including private fields declared in base classes.
- A field that already holds a value is left alone.
- The field type must match the registered type exactly (`Register<IAudio>(...)` -> `[Inject] IAudio`).

## Plain (non-Node) classes

Any class can use `[Inject]`. The container fills a plain object when:

| You do | What happens |
|---|---|
| `ioc.Register(new MyManager())` | Fields are injected first, then the instance is stored and announced. |
| `ioc.Register<MyManager>()` or `ioc.Register<MyManager>(() => ...)` | The created instance is injected right after creation. |
| `ioc.Instantiate<MyManager>()` | `new MyManager()` + injection. Not registered. |
| `ioc.InjectFields(obj)` | Injects an object you created yourself. |
| `ioc.Release(obj)` | Forgets an object that is still waiting for a dependency. |

```csharp
public class InventoryService : IGIoCReadyCallback
{
    [Inject] private SaveGame _save = null!;
    [Inject] private ScoreService _score = null!;

    public void OnGIoCReady()          // exactly once, when the last field has been filled
    {
        _save.Load();
    }
}
```

Or let the generator write the callback for you:

```csharp
[Register]
public partial class InventoryService          // no base class needed
{
    [Inject] private SaveGame _save = null!;
    partial void OnReady() => _save.Load();
}
```

Notes:
- Injection happens **after** construction, so use `OnReady()` / `OnGIoCReady()` instead of the constructor.
- Waiting objects are held **weakly**; the container never keeps them alive. A registered singleton is
  held by the registry, of course.
- Creating a service lazily while injecting works, including `A -> B -> A` field cycles. A *factory* that
  resolves itself (directly or through another factory) throws `Circular dependency ...`.
- A throwing `OnReady` is logged with `GD.PrintErr`; other waiting objects are still injected.

## Nodes and `[Register]`

`[Register]` on a `partial` class makes the generator emit:

- for a **Node** (any class deriving from `Godot.Node`): a `_Ready()` override that calls `base._Ready()` and then
  `InjectFields(this)`, plus the `OnReady()` hook.
- for a **plain class**: just the `OnReady()` hook (see above).

Put your startup code in `partial void OnReady()`; do not also write `_Ready()` (that is error `GIOC002`).

Two ways a node gets injected:

| | When injection and `OnReady()` happen |
|---|---|
| `ioc.ConnectAutoInject(GetTree())` | When the node **enters the tree**, i.e. *before* its `_Ready()`. `[Register]` nodes are also registered by their own type, so others can `[Inject]` them. |
| No auto-inject | In the generated `_Ready()`, after `base._Ready()`. |

`ioc.Register(someNode)` does **not** inject a node early (it would run `OnReady()` before the node is in the tree);
nodes are injected by one of the two paths above.

## Messenger

A static, typed publish/subscribe bus with optional channels.

```csharp
public readonly record struct DamageEvent(int Amount);

Messenger.Register<DamageEvent>(this, e => Health -= e.Amount);            // broadcast
Messenger.Register<DamageEvent>(this, "player", e => Health -= e.Amount);  // channel

Messenger.Send(new DamageEvent(10));            // broadcast subscribers
Messenger.Send(new DamageEvent(10), "player");  // broadcast + "player" subscribers

Messenger.Unregister(this);                     // all types, all channels
Messenger.Unregister<DamageEvent>(this);
```

**Always unregister** in `_ExitTree()` (or when the object is dropped). A handler's delegate keeps its recipient
alive, so a freed node would otherwise stay subscribed and be called after it is disposed.

## Commands

```csharp
public RelayCommand SaveCommand { get; }
public AsyncRelayCommand LoadCommand { get; }

public MyViewModel()
{
    SaveCommand = new RelayCommand(Save, () => !_busy);
    LoadCommand = new AsyncRelayCommand(LoadAsync);
}

SaveCommand.Execute();                  // runs only if CanExecute()
LoadCommand.IsRunning;                  // true while the task runs; Execute() is ignored meanwhile
SaveCommand.NotifyCanExecuteChanged();  // raise CanExecuteChanged
```

`[RelayCommand]` on a method is declared in this package, but the code that generates the command property
comes from the separate `GodotFlex.SourceGenerator` package, as does `[Observable]`.

## Bindings, `TrackedValue` and `Links`

Namespace `GIoC.Bindings`: `BindingManager`, `Binding`, `TrackedValue<T>`, `FormatterFactory`.

- A **source** can be an `[Observable]` property (INPC, event driven), a `TrackedValue<T>` (event driven) or a plain
  property (polled).
- `TrackedValue<T>` wraps a value and marks its binding dirty when `.Value` changes.
  **One `TrackedValue` currently drives one binding.**
- Drive the manager from one place, e.g. in an autoload's `_Process`:
  ```csharp
  var mgr = GodotIoC.Instance.Resolve<BindingManager>();
  mgr.ProcessDirtyBindings();     // event-driven sources
  mgr.ProcessAllBindings();       // polled sources
  ```
- **`Links`** (inspector-driven bindings) is **not** part of the package. Copy `src/Links` from the repository into
  your Godot project's `addons/binding_inspector`, enable the plugin, and add a `Links` node as a child of the
  node that should receive the data. It is a template because it relies on Godot's `[Tool]`/`[GlobalClass]` handling.

## Generator diagnostics

| Id | Meaning |
|---|---|
| `GIOC001` | A `[Register]` class (or one of its containing types) is not `partial`. |
| `GIOC002` | A `[Register]` **node** defines `_Ready()`. Move the code into `partial void OnReady()`. |

Supported shapes: nested classes (containing types must be `partial`), generic classes, `[Register]`,
`[RegisterAttribute]`, `[GIoC.Register]`, `[global::GIoC.Register]`.

## Rules and limits

- `[Inject]` is for **fields** only, not properties.
- Interface-typed fields resolve only if the service was registered under exactly that interface.
- With a `[Register]` base class and a `[Register]` derived class, only the most derived `OnReady()` runs.
- The container is not tied to the scene tree: a registered node that gets freed stays in the registry until you
  replace it or call `Reset()`.
- Thread safety: registration, resolution and injection are lock-protected; factories, `[Inject]` writes and
  `OnReady()` run **outside** the lock.

## Building and testing

```
dotnet build GIoCV3.sln
dotnet test tests/GIoC.Tests
dotnet pack GIoCV3.csproj -c Release -o nupkg
```

The tests need no Godot engine. They cover the container and run the source generator in-process, compiling
what it produces.

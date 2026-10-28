using System.Collections.Immutable;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.Loader;
using GIoC.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace GIoC.Tests;

/// <summary>
/// Runs ObservableGenerator in-process on small snippets, compiles the result, loads the compiled
/// assembly and drives the generated properties, so both "does it compile" and "does it behave" are checked.
///
/// The tests in the "Inheritance, nesting and special types" section used to be skipped known issues in the old
/// GodotFlex copy of this generator;
/// each of them failed against the earlier generator and passes now.
/// </summary>
public class ObservableGeneratorTests
{
    // [Observable] is the real GIoC.ObservableAttribute (the test project references GIoC);
    // only Export and Texture are declared here.
    private const string Prelude = """
        using System;
        [AttributeUsage(AttributeTargets.Field)] public sealed class ExportAttribute : Attribute { }
        public class Texture { }
        """;

    // ── Harness ───────────────────────────────────────────────────────────────

    private static readonly ImmutableArray<MetadataReference> References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray();

    private sealed class Result
    {
        public required IReadOnlyList<(string HintName, string Text)> Generated { get; init; }
        public required IReadOnlyList<Diagnostic> GeneratorDiagnostics { get; init; }
        public required IReadOnlyList<Diagnostic> CompileErrors { get; init; }
        public required IReadOnlyList<Diagnostic> CompileWarnings { get; init; }
        public Assembly? Assembly { get; init; }

        public string Source => string.Join(Environment.NewLine + "----" + Environment.NewLine, Generated.Select(g => g.Text));
        public string Errors => string.Join(Environment.NewLine, CompileErrors.Select(e => e.ToString()));
        public bool GeneratorFailed => GeneratorDiagnostics.Any(d => d.Id == "CS8785");

        public Type Type(string name) =>
            Assembly!.GetType(name) ?? throw new InvalidOperationException($"type '{name}' not found; compile errors: {Errors}");
    }

    private static Result Run(string userSource) => Run(userSource, new ObservableGenerator());

    private static Result Run(string userSource, params IIncrementalGenerator[] generators)
    {
        // Two trees: the prelude must not share a file with a file-scoped namespace
        var options = new CSharpParseOptions(LanguageVersion.Latest);
        var preludeTree = CSharpSyntaxTree.ParseText(Prelude, options);
        var tree = CSharpSyntaxTree.ParseText("using System;" + Environment.NewLine + "using GIoC;" + Environment.NewLine + userSource, options);
        var compilation = CSharpCompilation.Create(
            "ObsTest_" + Guid.NewGuid().ToString("N"),
            new[] { preludeTree, tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(generators);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        var generated = driver.GetRunResult().Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => (s.HintName, s.SourceText.ToString()))
            .ToList();

        var all = output.GetDiagnostics();
        var errors = all.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var warnings = all.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();

        Assembly? assembly = null;
        if (errors.Count == 0)
        {
            using var ms = new MemoryStream();
            var emit = output.Emit(ms);
            if (emit.Success)
            {
                ms.Position = 0;
                assembly = AssemblyLoadContext.Default.LoadFromStream(ms);
            }
        }

        return new Result
        {
            Generated = generated,
            GeneratorDiagnostics = generatorDiagnostics,
            CompileErrors = errors,
            CompileWarnings = warnings,
            Assembly = assembly,
        };
    }

    private static object New(Type type) => Activator.CreateInstance(type)!;
    private static object? Get(object o, string prop) => o.GetType().GetProperty(prop)!.GetValue(o);
    private static void Set(object o, string prop, object? value) => o.GetType().GetProperty(prop)!.SetValue(o, value);

    private static List<(object? Sender, string? Name)> Listen(object o)
    {
        var events = new List<(object?, string?)>();
        ((INotifyPropertyChanged)o).PropertyChanged += (s, e) => events.Add((s, e.PropertyName));
        return events;
    }

    // ── What gets generated ───────────────────────────────────────────────────

    [Fact]
    public void Field_BecomesPublicProperty_AndTheClassImplementsINPC()
    {
        var r = Run("""
            namespace Game
            {
                public partial class Card { [Observable] private string _title = "x"; }
            }
            """);

        Assert.Empty(r.CompileErrors);
        var type = r.Type("Game.Card");
        Assert.True(typeof(INotifyPropertyChanged).IsAssignableFrom(type));
        var prop = type.GetProperty("Title")!;
        Assert.True(prop.CanRead && prop.CanWrite);
        Assert.Equal(typeof(string), prop.PropertyType);
        Assert.NotNull(type.GetEvent("PropertyChanged"));
    }

    [Theory]
    [InlineData("_hp", "Hp")]
    [InlineData("__hp", "Hp")]
    [InlineData("hp", "Hp")]
    [InlineData("_hpValue", "HpValue")]
    [InlineData("_x", "X")]
    public void PropertyName_IsThePascalCasedFieldNameWithoutLeadingUnderscores(string field, string property)
    {
        var r = Run($$"""
            namespace Game
            {
                public partial class Thing { [Observable] private int {{field}}; }
            }
            """);

        Assert.Empty(r.CompileErrors);
        Assert.NotNull(r.Type("Game.Thing").GetProperty(property));
    }

    [Fact]
    public void SeveralFields_OfOneClass_ShareOneFileAndOneEvent()
    {
        var r = Run("""
            namespace Game
            {
                public partial class Card
                {
                    [Observable] private string _title = "";
                    [Observable] private int _cost;
                    [Observable] private Texture? _icon;
                }
            }
            """);

        Assert.Empty(r.CompileErrors);
        Assert.Single(r.Generated);
        var type = r.Type("Game.Card");
        Assert.NotNull(type.GetProperty("Title"));
        Assert.NotNull(type.GetProperty("Cost"));
        Assert.NotNull(type.GetProperty("Icon"));
        Assert.Single(type.GetEvents(BindingFlags.Public | BindingFlags.Instance), e => e.Name == "PropertyChanged");
    }

    [Fact]
    public void SeveralDeclaratorsInOneField_AllBecomeProperties()
    {
        var r = Run("""
            namespace Game
            {
                public partial class Pair { [Observable] private int _a, _b; }
            }
            """);

        Assert.Empty(r.CompileErrors);
        var type = r.Type("Game.Pair");
        Assert.NotNull(type.GetProperty("A"));
        Assert.NotNull(type.GetProperty("B"));
    }

    [Fact]
    public void TwoClasses_ProduceTwoFiles()
    {
        var r = Run("""
            namespace Game
            {
                public partial class A { [Observable] private int _x; }
                public partial class B { [Observable] private int _y; }
            }
            """);

        Assert.Empty(r.CompileErrors);
        Assert.Equal(2, r.Generated.Count);
    }

    [Theory]
    [InlineData("string", typeof(string))]
    [InlineData("int", typeof(int))]
    [InlineData("float", typeof(float))]
    [InlineData("bool", typeof(bool))]
    [InlineData("int?", typeof(int?))]
    [InlineData("int[]", typeof(int[]))]
    [InlineData("System.Collections.Generic.List<int>", typeof(List<int>))]
    [InlineData("(int, string)", typeof(ValueTuple<int, string>))]
    public void PropertyType_MatchesTheFieldType(string fieldType, Type expected)
    {
        var r = Run($$"""
            namespace Game
            {
                public partial class Thing { [Observable] private {{fieldType}} _value = default!; }
            }
            """);

        Assert.Empty(r.CompileErrors);
        Assert.Equal(expected, r.Type("Game.Thing").GetProperty("Value")!.PropertyType);
    }

    [Fact]
    public void NullableReferenceType_CompilesWithoutWarningsAsErrors()
    {
        var r = Run("""
            namespace Game
            {
                public partial class Thing { [Observable] private string? _note; }
            }
            """);

        Assert.Empty(r.CompileErrors);
        Assert.Equal(typeof(string), r.Type("Game.Thing").GetProperty("Note")!.PropertyType);
    }

    [Fact]
    public void FileScopedNamespace_Works()
    {
        var r = Run("""
            namespace Game.Cards;
            public partial class Card { [Observable] private int _cost; }
            """);

        Assert.Empty(r.CompileErrors);
        Assert.NotNull(r.Type("Game.Cards.Card").GetProperty("Cost"));
    }

    [Fact]
    public void GlobalNamespace_Works()
    {
        var r = Run("""
            public partial class GlobalCard { [Observable] private int _cost; }
            """);

        Assert.Empty(r.CompileErrors);
        Assert.NotNull(r.Type("GlobalCard").GetProperty("Cost"));
    }

    [Fact]
    public void ClassDerivingFromAnotherClass_Works()
    {
        var r = Run("""
            namespace Game
            {
                public class BaseView { }
                public partial class Hud : BaseView { [Observable] private int _score; }
            }
            """);

        Assert.Empty(r.CompileErrors);
        Assert.True(typeof(INotifyPropertyChanged).IsAssignableFrom(r.Type("Game.Hud")));
    }

    // ── Attribute recognition ─────────────────────────────────────────────────

    [Theory]
    [InlineData("[Observable]")]
    [InlineData("[ObservableAttribute]")]
    [InlineData("[Observable, Obsolete]")]
    [InlineData("[Export] [Observable]")]
    [InlineData("[Observable] [Export]")]
    public void AttributeSpellingsAndCombinations_AreRecognised(string attributes)
    {
        var r = Run($$"""
            namespace Game
            {
                public partial class Thing { {{attributes}} private int _value; }
            }
            """);

        Assert.Empty(r.CompileErrors);
        Assert.NotNull(r.Type("Game.Thing").GetProperty("Value"));
    }

    [Fact]
    public void FieldsWithoutTheAttribute_AreLeftAlone()
    {
        var r = Run("""
            namespace Game
            {
                public partial class Thing
                {
                    [Observable] private int _watched;
                    private int _plain;
                    [Export] private int _exported;
                }
            }
            """);

        Assert.Empty(r.CompileErrors);
        var type = r.Type("Game.Thing");
        Assert.NotNull(type.GetProperty("Watched"));
        Assert.Null(type.GetProperty("Plain"));
        Assert.Null(type.GetProperty("Exported"));
    }

    [Fact]
    public void ClassWithoutObservableFields_GeneratesNothing()
    {
        var r = Run("""
            namespace Game
            {
                public partial class Quiet { private int _x; }
            }
            """);

        Assert.Empty(r.Generated);
        Assert.Empty(r.CompileErrors);
    }

    [Fact]
    public void ObservableOnAStructField_IsIgnoredSilently()
    {
        var r = Run("""
            namespace Game
            {
                public partial struct Point { [Observable] private int _x; }
            }
            """);

        Assert.Empty(r.Generated);
        Assert.Empty(r.CompileErrors);
    }

    // ── Behaviour of the generated property ───────────────────────────────────

    [Fact]
    public void InitialValue_ComesThroughTheProperty()
    {
        var r = Run("""
            namespace Game
            {
                public partial class Card { [Observable] private string _title = "CARD TITLE"; [Observable] private int _cost = 3; }
            }
            """);

        var card = New(r.Type("Game.Card"));
        Assert.Equal("CARD TITLE", Get(card, "Title"));
        Assert.Equal(3, Get(card, "Cost"));
    }

    [Fact]
    public void SettingANewValue_StoresIt_AndRaisesPropertyChangedOnce()
    {
        var r = Run("""
            namespace Game
            {
                public partial class Card { [Observable] private string _title = "a"; }
            }
            """);
        var card = New(r.Type("Game.Card"));
        var events = Listen(card);

        Set(card, "Title", "b");

        Assert.Equal("b", Get(card, "Title"));
        var e = Assert.Single(events);
        Assert.Equal("Title", e.Name);
        Assert.Same(card, e.Sender);
    }

    [Fact]
    public void SettingTheSameValue_RaisesNothing()
    {
        var r = Run("""
            namespace Game
            {
                public partial class Card { [Observable] private int _cost = 5; [Observable] private string? _note = "n"; }
            }
            """);
        var card = New(r.Type("Game.Card"));
        var events = Listen(card);

        Set(card, "Cost", 5);
        Set(card, "Note", "n");

        Assert.Empty(events);
    }

    [Fact]
    public void EachFieldRaisesItsOwnPropertyName()
    {
        var r = Run("""
            namespace Game
            {
                public partial class Card { [Observable] private string _title = ""; [Observable] private int _cost; }
            }
            """);
        var card = New(r.Type("Game.Card"));
        var events = Listen(card);

        Set(card, "Cost", 2);
        Set(card, "Title", "x");
        Set(card, "Cost", 3);

        Assert.Equal(new[] { "Cost", "Title", "Cost" }, events.Select(e => e.Name));
    }

    [Fact]
    public void ReferenceValues_NullToValueAndBack_BothRaise()
    {
        var r = Run("""
            namespace Game
            {
                public partial class Card { [Observable] private Texture? _icon; }
            }
            """);
        var card = New(r.Type("Game.Card"));
        var events = Listen(card);
        var texture = Activator.CreateInstance(r.Assembly!.GetType("Texture")!);

        Set(card, "Icon", texture);
        Set(card, "Icon", null);

        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void SettingWithoutAnySubscriber_DoesNotThrow()
    {
        var r = Run("""
            namespace Game
            {
                public partial class Card { [Observable] private int _cost; }
            }
            """);
        var card = New(r.Type("Game.Card"));

        var ex = Record.Exception(() => Set(card, "Cost", 9));

        Assert.Null(ex);
        Assert.Equal(9, Get(card, "Cost"));
    }

    [Fact]
    public void TheEventArgsObject_IsReusedBetweenRaises()
    {
        var r = Run("""
            namespace Game
            {
                public partial class Card { [Observable] private int _cost; }
            }
            """);
        var card = New(r.Type("Game.Card"));
        var args = new List<PropertyChangedEventArgs>();
        ((INotifyPropertyChanged)card).PropertyChanged += (_, e) => args.Add(e);

        Set(card, "Cost", 1);
        Set(card, "Cost", 2);

        Assert.Equal(2, args.Count);
        Assert.Same(args[0], args[1]);   // cached per field, no allocation per change
    }

    // ── Inheritance, nesting and special types (formerly known issues) ─────────

    [Fact]
    public void SameClassNameInTwoNamespaces_BothGenerate()
    {
        var r = Run("""
            namespace One { public partial class Card { [Observable] private int _a; } }
            namespace Two { public partial class Card { [Observable] private int _b; } }
            """);

        Assert.False(r.GeneratorFailed, "generator threw: " + string.Join("; ", r.GeneratorDiagnostics));
        Assert.Equal(2, r.Generated.Count);
        Assert.Equal(2, r.Generated.Select(g => g.HintName).Distinct().Count());
        Assert.Empty(r.CompileErrors);
        Assert.NotNull(r.Type("One.Card").GetProperty("A"));
        Assert.NotNull(r.Type("Two.Card").GetProperty("B"));
    }

    [Fact]
    public void NestedClass_Compiles()
    {
        var r = Run("""
            namespace Game
            {
                public partial class Outer
                {
                    public partial class Inner { [Observable] private int _x; }
                }
            }
            """);

        Assert.Empty(r.CompileErrors);
        var inner = r.Type("Game.Outer+Inner");
        var instance = New(inner);
        var events = Listen(instance);
        Set(instance, "X", 4);
        Assert.Equal(new[] { "X" }, events.Select(e => e.Name));
    }

    [Fact]
    public void DeeplyNestedClassInsideAGenericContainer_Compiles()
    {
        var r = Run("""
            namespace Game
            {
                public partial class A<T>
                {
                    public partial struct B
                    {
                        public partial class C { [Observable] private string _name = ""; }
                    }
                }
            }
            """);

        Assert.Empty(r.CompileErrors);
        Assert.Single(r.Generated);
    }

    [Fact]
    public void GenericClass_Compiles_AndRaises()
    {
        var r = Run("""
            namespace Game
            {
                public partial class Box<T> { [Observable] private T _content = default!; }
            }
            """);

        Assert.Empty(r.CompileErrors);
        var closed = r.Type("Game.Box`1").MakeGenericType(typeof(int));
        var box = New(closed);
        var events = Listen(box);
        Set(box, "Content", 7);
        Set(box, "Content", 7);
        Assert.Equal(new[] { "Content" }, events.Select(e => e.Name));
    }

    [Fact]
    public void FieldTypeWithoutEqualityOperator_Compiles_AndStillSkipsEqualValues()
    {
        var r = Run("""
            namespace Game
            {
                public struct Money { public int Cents; }
                public partial class Wallet { [Observable] private Money _balance; }
            }
            """);
        Assert.Empty(r.CompileErrors);

        var wallet = New(r.Type("Game.Wallet"));
        var events = Listen(wallet);
        var money = r.Assembly!.GetType("Game.Money")!;
        var five = Activator.CreateInstance(money)!;
        money.GetField("Cents")!.SetValue(five, 5);
        var fiveAgain = Activator.CreateInstance(money)!;
        money.GetField("Cents")!.SetValue(fiveAgain, 5);

        Set(wallet, "Balance", five);
        Set(wallet, "Balance", fiveAgain);   // equal value type: no second event

        Assert.Single(events);
    }

    [Fact]
    public void ValueEqualityIsUsed_NotReferenceEquality()
    {
        var r = Run("""
            namespace Game
            {
                public partial class Card { [Observable] private string _title = ""; }
            }
            """);
        var card = New(r.Type("Game.Card"));
        var events = Listen(card);

        Set(card, "Title", new string("abc".ToCharArray()));
        Set(card, "Title", new string("abc".ToCharArray()));   // different string object, same text

        Assert.Single(events);
    }

    [Fact]
    public void BaseAndDerivedBothObservable_BaseChangesReachSubscribers()
    {
        var r = Run("""
            namespace Game
            {
                public partial class BaseModel { [Observable] private string _name = ""; }
                public partial class DerivedModel : BaseModel { [Observable] private int _level; }
            }
            """);
        Assert.Empty(r.CompileErrors);

        var model = New(r.Type("Game.DerivedModel"));
        var events = Listen(model);

        Set(model, "Name", "changed");    // property generated for the BASE field
        Set(model, "Level", 2);           // property generated for the DERIVED field

        Assert.Equal(new[] { "Name", "Level" }, events.Select(e => e.Name));
    }

    [Fact]
    public void BaseAndDerived_DeclareOnlyOneEvent_AndNoHidingWarning()
    {
        var r = Run("""
            namespace Game
            {
                public partial class BaseModel { [Observable] private string _name = ""; }
                public partial class DerivedModel : BaseModel { [Observable] private int _level; }
            }
            """);

        Assert.Empty(r.CompileErrors);
        Assert.DoesNotContain(r.CompileWarnings, w => w.Id == "CS0108");   // 'hides inherited member'
        Assert.Null(r.Type("Game.DerivedModel").GetEvent("PropertyChanged", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.NotNull(r.Type("Game.BaseModel").GetEvent("PropertyChanged", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    }

    [Fact]
    public void ThreeLevelHierarchy_EveryLevelRaisesTheSameEvent()
    {
        var r = Run("""
            namespace Game
            {
                public partial class Root { [Observable] private int _rootValue; }
                public partial class Mid : Root { [Observable] private int _midValue; }
                public partial class Leaf : Mid { [Observable] private int _leafValue; }
            }
            """);
        Assert.Empty(r.CompileErrors);

        var leaf = New(r.Type("Game.Leaf"));
        var events = Listen(leaf);
        Set(leaf, "LeafValue", 1);
        Set(leaf, "MidValue", 1);
        Set(leaf, "RootValue", 1);

        Assert.Equal(new[] { "LeafValue", "MidValue", "RootValue" }, events.Select(e => e.Name));
    }

    [Fact]
    public void DerivedClassWithoutObservableFields_StillGetsTheBaseEvents()
    {
        var r = Run("""
            namespace Game
            {
                public partial class BaseModel { [Observable] private int _hp; }
                public class PlainDerived : BaseModel { }
            }
            """);
        Assert.Empty(r.CompileErrors);

        var model = New(r.Type("Game.PlainDerived"));
        var events = Listen(model);
        Set(model, "Hp", 3);
        Assert.Equal(new[] { "Hp" }, events.Select(e => e.Name));
    }

    [Fact]
    public void SealedClass_CompilesWithoutProtectedMemberWarning()
    {
        var r = Run("""
            namespace Game
            {
                public sealed partial class Leaf { [Observable] private int _x; }
            }
            """);

        Assert.Empty(r.CompileErrors);
        Assert.DoesNotContain(r.CompileWarnings, w => w.Id == "CS0628");   // new protected member in sealed type
    }

    [Fact]
    public void GeneratedCode_DoesNotNeedUsingDirectives_OrNullableContext()
    {
        // The user file imports nothing and has nullable annotations off; the generated file must still compile cleanly
        var r = Run("""
            #nullable disable
            namespace Game
            {
                public partial class Plain { [Observable] private string _s; }
            }
            """);

        Assert.Empty(r.CompileErrors);
        Assert.DoesNotContain(r.CompileWarnings, w => w.Id == "CS8632");   // '?' used outside a nullable context
    }

    // ── Bases that already implement INotifyPropertyChanged ───────────────────

    [Fact]
    public void HandWrittenBase_WithOnPropertyChangedString_IsReusedWithoutASecondEvent()
    {
        var r = Run("""
            using System.ComponentModel;
            namespace Game
            {
                public class ViewModelBase : INotifyPropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;
                    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
                }
                public partial class Hud : ViewModelBase { [Observable] private int _score; }
            }
            """);

        Assert.Empty(r.CompileErrors);
        Assert.Empty(r.GeneratorDiagnostics);
        Assert.Contains("OnPropertyChanged(\"Score\")", r.Source);
        Assert.DoesNotContain("event ", r.Source);

        var hud = New(r.Type("Game.Hud"));
        var events = Listen(hud);
        Set(hud, "Score", 10);
        Assert.Equal(new[] { "Score" }, events.Select(e => e.Name));
    }

    [Fact]
    public void HandWrittenBase_WithOnPropertyChangedEventArgs_IsPreferredAndReused()
    {
        var r = Run("""
            using System.ComponentModel;
            namespace Game
            {
                public class ObservableBase : INotifyPropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;
                    protected virtual void OnPropertyChanged(PropertyChangedEventArgs e) => PropertyChanged?.Invoke(this, e);
                }
                public partial class Hud : ObservableBase { [Observable] private int _score; }
            }
            """);

        Assert.Empty(r.CompileErrors);
        Assert.Contains("OnPropertyChanged(_scoreChangedArgs)", r.Source);

        var hud = New(r.Type("Game.Hud"));
        var events = Listen(hud);
        Set(hud, "Score", 1);
        Assert.Equal(new[] { "Score" }, events.Select(e => e.Name));
    }

    [Fact]
    public void ClassThatImplementsInpcItself_UsesItsOwnHelper()
    {
        var r = Run("""
            using System.ComponentModel;
            namespace Game
            {
                public partial class Mine : INotifyPropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;
                    private void RaisePropertyChanged(PropertyChangedEventArgs e) => PropertyChanged?.Invoke(this, e);
                    [Observable] private int _hp;
                }
            }
            """);

        Assert.Empty(r.CompileErrors);
        var mine = New(r.Type("Game.Mine"));
        var events = Listen(mine);
        Set(mine, "Hp", 2);
        Assert.Equal(new[] { "Hp" }, events.Select(e => e.Name));
    }

    [Fact]
    public void InpcBaseWithoutAnyRaiseHelper_ReportsGIOC003_AndGeneratesNothing()
    {
        var r = Run("""
            using System.ComponentModel;
            namespace Game
            {
                public class Closed : INotifyPropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;
                }
                public partial class Hud : Closed { [Observable] private int _score; }
            }
            """);

        Assert.Empty(r.Generated);
        var diagnostic = Assert.Single(r.GeneratorDiagnostics);
        Assert.Equal("GIOC003", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("Hud", diagnostic.GetMessage());
    }

    [Fact]
    public void PrivateHelperInTheBase_IsNotUsable_AndReportsGIOC003()
    {
        var r = Run("""
            using System.ComponentModel;
            namespace Game
            {
                public class Hidden : INotifyPropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;
                    private void OnPropertyChanged(string name) { }
                }
                public partial class Hud : Hidden { [Observable] private int _score; }
            }
            """);

        Assert.Equal("GIOC003", Assert.Single(r.GeneratorDiagnostics).Id);
    }

    // ── Attribute resolution and the other GIoC generator ─────────────────────

    [Fact]
    public void AnUnrelatedAttributeNamedObservable_IsIgnored()
    {
        var r = Run("""
            namespace Other
            {
                [AttributeUsage(AttributeTargets.Field)] public sealed class ObservableAttribute : Attribute { }
            }
            namespace Game
            {
                using Other;
                public partial class Thing { [Observable] private int _x; }
            }
            """);

        Assert.Empty(r.Generated);
        Assert.Empty(r.GeneratorDiagnostics);
    }

    [Theory]
    [InlineData("[GIoC.Observable]")]
    [InlineData("[global::GIoC.Observable]")]
    [InlineData("[GIoC.ObservableAttribute]")]
    public void QualifiedAttributeSpellings_AreRecognised(string attribute)
    {
        var r = Run($$"""
            namespace Game
            {
                public partial class Thing { {{attribute}} private int _x; }
            }
            """);

        Assert.Single(r.Generated);
        Assert.Empty(r.CompileErrors);
    }

    [Fact]
    public void RegisterAndObservable_OnTheSameNode_BothGenerate_AndCompile()
    {
        var r = Run("""
            using Godot;
            namespace Game
            {
                [Register]
                public partial class Hud : Control
                {
                    [Inject] private object _service = null!;
                    [Observable] private int _score;
                    partial void OnReady() { }
                }
            }
            """, new RegisterGenerator(), new ObservableGenerator());

        Assert.Empty(r.CompileErrors);
        Assert.Equal(2, r.Generated.Count);
        Assert.Contains(r.Generated, g => g.HintName.EndsWith(".Register.g.cs"));
        Assert.Contains(r.Generated, g => g.HintName.EndsWith(".Observable.g.cs"));

        // Inspect the type only: constructing a Godot Control needs the engine's native library
        var hud = r.Type("Game.Hud");
        Assert.True(typeof(IGIoCReadyCallback).IsAssignableFrom(hud));
        Assert.True(typeof(INotifyPropertyChanged).IsAssignableFrom(hud));
        Assert.NotNull(hud.GetProperty("Score"));
        Assert.NotNull(hud.GetMethod("_Ready"));
    }

    [Fact]
    public void RegisterAndObservable_OnAPlainClass_BothGenerate_AndCompile()
    {
        var r = Run("""
            namespace Game
            {
                [Register]
                public partial class Inventory
                {
                    [Inject] private object _save = null!;
                    [Observable] private int _gold;
                }
            }
            """, new RegisterGenerator(), new ObservableGenerator());

        Assert.Empty(r.CompileErrors);
        Assert.Equal(2, r.Generated.Count);
        Assert.IsAssignableFrom<INotifyPropertyChanged>(New(r.Type("Game.Inventory")));
        Assert.IsAssignableFrom<IGIoCReadyCallback>(New(r.Type("Game.Inventory")));
    }
}
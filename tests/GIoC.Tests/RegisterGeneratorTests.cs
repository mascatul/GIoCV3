using System.Collections.Immutable;
using GIoC.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace GIoC.Tests;

/// <summary>
/// Runs RegisterGenerator in-process on small snippets and compiles the result, so a wrong or
/// non-compiling generated file shows up as a failing test instead of a build error in the game.
/// </summary>
public class RegisterGeneratorTests
{
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        // Every assembly the test host loaded: the framework, GodotSharp and GIoC itself
        var tpa = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return tpa.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToImmutableArray();
    }

    private sealed record Result(
        IReadOnlyList<(string HintName, string Text)> Generated,
        IReadOnlyList<Diagnostic> GeneratorDiagnostics,
        IReadOnlyList<Diagnostic> CompileErrors);

    private static Result Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "GeneratorTestAssembly",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new RegisterGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        var generated = driver.GetRunResult().Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => (s.HintName, s.SourceText.ToString()))
            .ToList();

        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        return new Result(generated, generatorDiagnostics, errors);
    }

    private static string Describe(Result r) =>
        string.Join(Environment.NewLine, r.CompileErrors.Select(e => e.ToString()));

    // ── Nodes ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Node_GetsReadyOverride_AndCallback_AndCompiles()
    {
        var r = Run("""
            using Godot;
            using GIoC;
            namespace Game;
            [Register]
            public partial class Hud : Node
            {
                [Inject] private object _x = null!;
                partial void OnReady() { }
            }
            """);

        var text = Assert.Single(r.Generated).Text;
        Assert.Contains("public override void _Ready()", text);
        Assert.Contains("InjectFields(this)", text);
        Assert.Contains("global::GIoC.IGIoCReadyCallback", text);
        Assert.Empty(r.CompileErrors);
    }

    [Fact]
    public void NodeDerivedThroughAnotherClass_IsStillATreatedAsANode()
    {
        var r = Run("""
            using Godot;
            using GIoC;
            namespace Game;
            public partial class BaseView : Control { }
            [Register]
            public partial class Menu : BaseView { }
            """);

        Assert.Contains("override void _Ready()", Assert.Single(r.Generated).Text);
        Assert.Empty(r.CompileErrors);
    }

    // ── Plain classes (the new case) ──────────────────────────────────────────

    [Fact]
    public void PlainClass_GetsOnlyTheCallback_NoReadyOverride_AndCompiles()
    {
        var r = Run("""
            using GIoC;
            namespace Game;
            [Register]
            public partial class SaveService
            {
                [Inject] private object _x = null!;
                public bool Ready;
                partial void OnReady() { Ready = true; }
            }
            """);

        var text = Assert.Single(r.Generated).Text;
        Assert.DoesNotContain("_Ready", text);
        Assert.DoesNotContain("base.", text);
        Assert.Contains("void global::GIoC.IGIoCReadyCallback.OnGIoCReady() => OnReady();", text);
        Assert.Empty(r.CompileErrors);
    }

    [Fact]
    public void PlainClass_InGlobalNamespace_Compiles()
    {
        var r = Run("""
            using GIoC;
            [Register]
            public partial class Global { }
            """);

        Assert.DoesNotContain("namespace ", Assert.Single(r.Generated).Text);
        Assert.Empty(r.CompileErrors);
    }

    // ── Shapes ────────────────────────────────────────────────────────────────

    [Fact]
    public void NestedClass_WithPartialContainer_Compiles()
    {
        var r = Run("""
            using Godot;
            using GIoC;
            namespace Game;
            public partial class Outer
            {
                [Register]
                public partial class InnerNode : Node { }

                [Register]
                public partial class InnerPlain { }
            }
            """);

        Assert.Equal(2, r.Generated.Count);
        Assert.Empty(r.CompileErrors);
    }

    [Fact]
    public void GenericClass_Compiles()
    {
        var r = Run("""
            using Godot;
            using GIoC;
            namespace Game;
            [Register]
            public partial class Holder<T> : Node where T : class { }

            [Register]
            public partial class Repo<TKey, TValue> { }
            """);

        Assert.Equal(2, r.Generated.Count);
        Assert.Empty(r.CompileErrors);
    }

    [Fact]
    public void SameClassNameInTwoNamespaces_ProducesTwoFiles()
    {
        var r = Run("""
            using GIoC;
            namespace One { [Register] public partial class Service { } }
            namespace Two { [Register] public partial class Service { } }
            """);

        Assert.Equal(2, r.Generated.Count);
        Assert.Equal(2, r.Generated.Select(g => g.HintName).Distinct().Count());
        Assert.Empty(r.GeneratorDiagnostics);
        Assert.Empty(r.CompileErrors);
    }

    [Theory]
    [InlineData("[GIoC.Register]")]
    [InlineData("[global::GIoC.Register]")]
    [InlineData("[RegisterAttribute]")]
    [InlineData("[Register(\"named\")]")]
    public void AttributeSpellings_AreAllRecognised(string attribute)
    {
        var r = Run($$"""
            using GIoC;
            namespace Game;
            {{attribute}}
            public partial class Service { }
            """);

        Assert.Single(r.Generated);
        Assert.Empty(r.CompileErrors);
    }

    [Fact]
    public void AnUnrelatedAttributeNamedRegister_IsIgnored()
    {
        var r = Run("""
            using System;
            namespace Other
            {
                public sealed class RegisterAttribute : Attribute { }
            }
            namespace Game
            {
                using Other;
                [Register]
                public partial class NotOurs { }
            }
            """);

        Assert.Empty(r.Generated);
        Assert.Empty(r.GeneratorDiagnostics);
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    [Fact]
    public void NonPartialClass_ReportsGIOC001_InsteadOfSilentlyDoingNothing()
    {
        var r = Run("""
            using GIoC;
            namespace Game;
            [Register]
            public class Forgot { }
            """);

        Assert.Empty(r.Generated);
        var diagnostic = Assert.Single(r.GeneratorDiagnostics);
        Assert.Equal("GIOC001", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("Forgot", diagnostic.GetMessage());
    }

    [Fact]
    public void NonPartialContainingType_ReportsGIOC001()
    {
        var r = Run("""
            using GIoC;
            namespace Game;
            public class Outer
            {
                [Register]
                public partial class Inner { }
            }
            """);

        Assert.Equal("GIOC001", Assert.Single(r.GeneratorDiagnostics).Id);
    }

    [Fact]
    public void NodeThatDefinesReady_ReportsGIOC002()
    {
        var r = Run("""
            using Godot;
            using GIoC;
            namespace Game;
            [Register]
            public partial class Mine : Node
            {
                public override void _Ready() { }
            }
            """);

        Assert.Empty(r.Generated);
        Assert.Equal("GIOC002", Assert.Single(r.GeneratorDiagnostics).Id);
    }

    [Fact]
    public void PlainClassNamedLikeAReadyMethod_IsNotAffectedByGIOC002()
    {
        var r = Run("""
            using GIoC;
            namespace Game;
            [Register]
            public partial class Plain
            {
                public void _Ready() { }   // not a node: nothing is overridden, so no clash
            }
            """);

        Assert.Single(r.Generated);
        Assert.Empty(r.GeneratorDiagnostics);
        Assert.Empty(r.CompileErrors);
    }
}

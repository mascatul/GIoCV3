using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace GIoC.SourceGenerator;

/// <summary>
/// Roslyn incremental source generator for classes annotated with [Register].
///
/// Godot nodes (the class derives from Godot.Node):
///
///   public override void _Ready()
///   {
///       base._Ready();
///       GodotIoC.Instance.InjectFields(this);
///   }
///   void IGIoCReadyCallback.OnGIoCReady() => OnReady();
///   partial void OnReady();
///
/// Plain classes (anything else): only the callback. They are injected when you call
/// GodotIoC.Register(instance), Instantiate&lt;T&gt;() or InjectFields(obj), so there is no _Ready to override:
///
///   void IGIoCReadyCallback.OnGIoCReady() => OnReady();
///   partial void OnReady();
///
/// OnReady() runs once all [Inject] fields are filled, even when a service is registered later.
///
/// Supported: nested classes (containing types must be partial), generic classes, namespace-qualified
/// attribute names ([GIoC.Register]). Reported: GIOC001 (not partial), GIOC002 (node already defines _Ready).
/// </summary>
[Generator]
public sealed class RegisterGenerator : IIncrementalGenerator
{
    private const string RegisterAttributeName = "GIoC.RegisterAttribute";
    private const string NodeTypeName = "Godot.Node";

    private static readonly DiagnosticDescriptor NotPartial = new(
        id: "GIOC001",
        title: "[Register] class must be partial",
        messageFormat: "Class '{0}' has [Register] but it (or one of its containing types) is not declared 'partial'",
        category: "GIoC",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DefinesReady = new(
        id: "GIOC002",
        title: "[Register] node defines _Ready()",
        messageFormat: "Node '{0}' defines _Ready(), which would clash with the generated override; move its code into 'partial void OnReady()'",
        category: "GIoC",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classes = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax cls && cls.AttributeLists.Count > 0,
                transform: static (ctx, ct) => Analyze(ctx, ct))
            .Where(static x => x is not null)
            .Select(static (x, _) => x!);

        context.RegisterSourceOutput(classes, static (spc, info) => Emit(spc, info));
    }

    // ── Analysis ──────────────────────────────────────────────────────────────

    private static RegisterClassInfo? Analyze(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var cls = (ClassDeclarationSyntax)ctx.Node;
        if (!HasRegisterAttribute(cls, ctx.SemanticModel, ct))
            return null;

        if (ctx.SemanticModel.GetDeclaredSymbol(cls, ct) is not INamedTypeSymbol symbol)
            return null;

        // The type and every containing type must be partial, otherwise the generated half cannot attach to it
        bool allPartial = true;
        var chain = new List<INamedTypeSymbol>();   // innermost first
        for (INamedTypeSymbol? t = symbol; t != null; t = t.ContainingType)
        {
            chain.Add(t);
            foreach (var reference in t.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(ct) is TypeDeclarationSyntax tds &&
                    !tds.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
                    allPartial = false;
            }
        }
        chain.Reverse();   // outermost first

        bool isNode = false;
        for (var b = symbol.BaseType; b != null; b = b.BaseType)
        {
            if (b.ToDisplayString() == NodeTypeName) { isNode = true; break; }
        }

        bool definesReady = isNode && symbol.GetMembers("_Ready")
            .OfType<IMethodSymbol>()
            .Any(m => m.Parameters.Length == 0 && !m.IsImplicitlyDeclared && m.DeclaringSyntaxReferences.Length > 0);

        var ns = symbol.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : null;

        // Unique per type: same class name in two namespaces (or nested in two types) must not collide
        var hint = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", "")
            .Replace('<', '_').Replace('>', '_').Replace(',', '_').Replace(' ', '_');

        return new RegisterClassInfo(
            symbol.Name,
            hint + ".Register.g.cs",
            ns,
            chain.Select(GetTypeHeader).ToList(),
            isNode,
            allPartial,
            definesReady,
            cls.Identifier.GetLocation());
    }

    // Resolves the attribute with the semantic model so [GIoC.Register], [global::GIoC.Register] and
    // [RegisterAttribute] all work and an unrelated attribute that happens to be called Register does not.
    private static bool HasRegisterAttribute(ClassDeclarationSyntax cls, SemanticModel model, CancellationToken ct)
    {
        foreach (var list in cls.AttributeLists)
        foreach (var attr in list.Attributes)
        {
            var symbol = model.GetSymbolInfo(attr, ct).Symbol;
            if (symbol is IMethodSymbol ctor)
            {
                if (ctor.ContainingType.ToDisplayString() == RegisterAttributeName) return true;
                continue;
            }

            // Not resolved (e.g. the GIoC assembly is not referenced yet): fall back to the written name
            var name = attr.Name.ToString().Replace("global::", "");
            if (name is "Register" or "RegisterAttribute" or "GIoC.Register" or "GIoC.RegisterAttribute")
                return true;
        }
        return false;
    }

    private static string GetTypeHeader(INamedTypeSymbol type)
    {
        string keyword = type.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() switch
        {
            RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.None)
                ? "record"
                : "record " + r.ClassOrStructKeyword.Text,
            TypeDeclarationSyntax t => t.Keyword.Text,
            _ => "class",
        };

        var typeParams = type.TypeParameters.Length > 0
            ? "<" + string.Join(", ", type.TypeParameters.Select(p => p.Name)) + ">"
            : "";

        return $"partial {keyword} {type.Name}{typeParams}";
    }

    // ── Output ────────────────────────────────────────────────────────────────

    private static void Emit(SourceProductionContext spc, RegisterClassInfo info)
    {
        if (!info.AllPartial)
        {
            spc.ReportDiagnostic(Diagnostic.Create(NotPartial, info.Location, info.DisplayName));
            return;
        }
        if (info.DefinesReady)
        {
            spc.ReportDiagnostic(Diagnostic.Create(DefinesReady, info.Location, info.DisplayName));
            return;
        }

        spc.AddSource(info.HintName, SourceText.From(GenerateSource(info), Encoding.UTF8));
    }

    private static string GenerateSource(RegisterClassInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> Do not edit.");
        sb.AppendLine("// Generated by GIoC.SourceGenerator for [Register] classes.");
        sb.AppendLine("// Put your startup code into the OnReady() partial method: it runs once all [Inject] fields are filled.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (info.Namespace != null)
        {
            sb.AppendLine($"namespace {info.Namespace};");
            sb.AppendLine();
        }

        // Containing types (outermost first), then the type itself
        int depth = info.TypeHeaders.Count;
        for (int i = 0; i < depth; i++)
        {
            var indent = new string(' ', i * 4);
            var header = info.TypeHeaders[i];
            if (i == depth - 1)
                header += " : global::GIoC.IGIoCReadyCallback";
            sb.AppendLine($"{indent}{header}");
            sb.AppendLine($"{indent}{{");
        }

        var member = new string(' ', depth * 4);

        if (info.IsNode)
        {
            // IGIoCReadyCallback ensures OnReady() is only called AFTER all [Inject] fields are filled,
            // even when services are registered after this node's _Ready() runs.
            sb.AppendLine($"{member}public override void _Ready()");
            sb.AppendLine($"{member}{{");
            sb.AppendLine($"{member}    base._Ready();");
            sb.AppendLine($"{member}    global::GIoC.GodotIoC.Instance.InjectFields(this);");
            sb.AppendLine($"{member}}}");
            sb.AppendLine();
        }

        sb.AppendLine($"{member}void global::GIoC.IGIoCReadyCallback.OnGIoCReady() => OnReady();");
        sb.AppendLine();
        sb.AppendLine($"{member}partial void OnReady();");

        for (int i = depth - 1; i >= 0; i--)
            sb.AppendLine($"{new string(' ', i * 4)}}}");

        return sb.ToString();
    }

    // Plain class (records need IsExternalInit, which netstandard2.0 does not have)
    private sealed class RegisterClassInfo
    {
        public string DisplayName { get; }
        public string HintName { get; }
        public string? Namespace { get; }
        public List<string> TypeHeaders { get; }
        public bool IsNode { get; }
        public bool AllPartial { get; }
        public bool DefinesReady { get; }
        public Location Location { get; }

        public RegisterClassInfo(string displayName, string hintName, string? ns, List<string> typeHeaders,
            bool isNode, bool allPartial, bool definesReady, Location location)
        {
            DisplayName = displayName;
            HintName = hintName;
            Namespace = ns;
            TypeHeaders = typeHeaders;
            IsNode = isNode;
            AllPartial = allPartial;
            DefinesReady = definesReady;
            Location = location;
        }
    }
}
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace GIoC.SourceGenerator;

/// <summary>
/// Generates INotifyPropertyChanged properties from fields annotated with [Observable] (GIoC.ObservableAttribute).
///
/// Input (user writes):
///   public partial class GameScene : Control
///   {
///       [Observable] private float _health = 100f;
///       [Observable] private string _status = "Ready";
///   }
///
/// Output (one file per class, GameScene.Observable.g.cs):
///   partial class GameScene : INotifyPropertyChanged
///   {
///       public event PropertyChangedEventHandler? PropertyChanged;
///       protected void RaisePropertyChanged(PropertyChangedEventArgs args) => PropertyChanged?.Invoke(this, args);
///       private static readonly PropertyChangedEventArgs _healthChangedArgs = new("Health");
///       public float Health { get => _health; set { if (EqualityComparer&lt;float&gt;.Default.Equals(_health, value)) return; _health = value; RaisePropertyChanged(_healthChangedArgs); } }
///       ...
///   }
///
/// Inheritance: only the root class of a hierarchy declares the event and RaisePropertyChanged; derived classes
/// with [Observable] fields reuse them, so every change reaches the same PropertyChanged event. If a base class
/// already implements INotifyPropertyChanged by hand, its RaisePropertyChanged / OnPropertyChanged helper
/// (taking PropertyChangedEventArgs or the property name) is used; without such a helper GIOC003 is reported.
///
/// Supported shapes: nested classes (containing types must be partial), generic classes, any namespace layout.
/// </summary>
[Generator]
public sealed class ObservableGenerator : IIncrementalGenerator
{
    private const string AttributeFullName = "GIoC.ObservableAttribute";
    private const string InpcName = "System.ComponentModel.INotifyPropertyChanged";
    private const string EventArgsName = "System.ComponentModel.PropertyChangedEventArgs";

    private static readonly DiagnosticDescriptor NoRaiseHelper = new(
        id: "GIOC003",
        title: "No way to raise PropertyChanged",
        messageFormat: "Class '{0}' has [Observable] fields, but it (or a base class) already implements INotifyPropertyChanged and " +
                       "offers no accessible RaisePropertyChanged/OnPropertyChanged method taking PropertyChangedEventArgs or a string; add one",
        category: "GIoC",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var fields = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is FieldDeclarationSyntax fds
                    && fds.AttributeLists.Count > 0,
                transform: static (ctx, ct) => GetObservableFields(ctx, ct))
            .Where(static x => x.Length > 0)
            .SelectMany(static (arr, _) => arr);

        var collected = fields.Collect();

        context.RegisterSourceOutput(collected, static (spc, items) =>
        {
            if (items.IsEmpty) return;

            // Group by the class itself (fully qualified), never by its short name
            foreach (var grp in items.GroupBy(f => f.ClassKey))
            {
                var first = grp.First();
                if (first.Raise.Kind == RaiseKind.Missing)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(NoRaiseHelper, first.Location, first.ClassName));
                    continue;
                }

                var source = GenerateSource(first, grp.ToList());
                spc.AddSource(first.HintName, SourceText.From(source, Encoding.UTF8));
            }
        });
    }

    // ── Analysis ──────────────────────────────────────────────────────────────

    private static ImmutableArray<ObservableFieldInfo> GetObservableFields(
        GeneratorSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        var fds = (FieldDeclarationSyntax)ctx.Node;
        var model = ctx.SemanticModel;

        if (!HasObservableAttribute(fds.AttributeLists, model, ct))
            return ImmutableArray<ObservableFieldInfo>.Empty;

        // Must be inside a class
        if (!(fds.Parent is ClassDeclarationSyntax cls))
            return ImmutableArray<ObservableFieldInfo>.Empty;

        if (model.GetDeclaredSymbol(cls, ct) is not INamedTypeSymbol classSymbol)
            return ImmutableArray<ObservableFieldInfo>.Empty;

        var ns = classSymbol.ContainingNamespace?.IsGlobalNamespace == false
            ? classSymbol.ContainingNamespace.ToDisplayString()
            : null;

        // Resolve field type
        var typeSymbol = model.GetTypeInfo(fds.Declaration.Type, ct).Type;
        var typeName = typeSymbol?.ToDisplayString() ?? "object";

        // Class-level facts, identical for every field of the class
        var classKey = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var hint = classKey.Replace("global::", "")
            .Replace('<', '_').Replace('>', '_').Replace(',', '_').Replace(' ', '_') + ".Observable.g.cs";
        var headers = TypeChain(classSymbol).Select(GetTypeHeader).ToList();
        var raise = ResolveRaise(classSymbol, model.Compilation);

        // Emit one entry per variable in the declaration
        var builder = ImmutableArray.CreateBuilder<ObservableFieldInfo>();
        foreach (var variable in fds.Declaration.Variables)
        {
            ct.ThrowIfCancellationRequested();
            var fieldName = variable.Identifier.Text;
            var propName = ToPascalCase(fieldName);
            if (string.IsNullOrEmpty(propName)) continue;
            builder.Add(new ObservableFieldInfo(
                classKey, hint, classSymbol.Name, ns, headers, classSymbol.IsSealed, raise,
                cls.Identifier.GetLocation(), fieldName, propName, typeName));
        }
        return builder.ToImmutable();
    }

    // Resolved with the semantic model: [Observable], [GIoC.Observable] and [ObservableAttribute] all work, and an
    // unrelated attribute that happens to be called Observable does not. If the attribute cannot be resolved at all
    // (GIoC not referenced yet) the written name decides, so the error the user sees is the missing type, not silence.
    private static bool HasObservableAttribute(SyntaxList<AttributeListSyntax> lists, SemanticModel model, System.Threading.CancellationToken ct)
    {
        foreach (var attrList in lists)
        foreach (var attr in attrList.Attributes)
        {
            var symbol = model.GetSymbolInfo(attr, ct).Symbol;
            if (symbol is IMethodSymbol ctor)
            {
                if (ctor.ContainingType.ToDisplayString() == AttributeFullName) return true;
                continue;
            }

            var name = attr.Name.ToString().Replace("global::", "");
            if (name is "Observable" or "ObservableAttribute" or "GIoC.Observable" or "GIoC.ObservableAttribute")
                return true;
        }
        return false;
    }

    // Outermost containing type first, the class itself last
    private static List<INamedTypeSymbol> TypeChain(INamedTypeSymbol type)
    {
        var chain = new List<INamedTypeSymbol>();
        for (INamedTypeSymbol? t = type; t != null; t = t.ContainingType)
            chain.Add(t);
        chain.Reverse();
        return chain;
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

    // ── How do we raise PropertyChanged? ──────────────────────────────────────

    private static RaiseInfo ResolveRaise(INamedTypeSymbol cls, Compilation compilation)
    {
        // 1. The class or a base from metadata / hand-written source already implements INPC:
        //    never declare a second event, use the helper it offers.
        var inpc = compilation.GetTypeByMetadataName(InpcName);
        if (inpc != null && cls.AllInterfaces.Contains(inpc, SymbolEqualityComparer.Default))
            return FindHelper(cls, compilation) ?? RaiseInfo.Missing;

        // 2. A base class in this compilation also has [Observable] fields: its generated half (invisible to us
        //    at this point) declares INPC, so ask what that base exposes.
        for (var b = cls.BaseType; b != null; b = b.BaseType)
        {
            if (HasObservableFields(b))
            {
                var inherited = ResolveRaise(b, compilation);
                return inherited.Kind == RaiseKind.DeclareOwn ? RaiseInfo.WithArgs("RaisePropertyChanged") : inherited;
            }
        }

        // 3. Root of a hierarchy: declare the event and RaisePropertyChanged ourselves.
        return RaiseInfo.DeclareOwn;
    }

    private static bool HasObservableFields(INamedTypeSymbol type)
    {
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            foreach (var attribute in field.GetAttributes())
            {
                var attributeClass = attribute.AttributeClass;
                if (attributeClass == null) continue;
                if (attributeClass.ToDisplayString() == AttributeFullName) return true;

                // Unresolved attribute type (error symbol): fall back to the written name
                if (attributeClass.TypeKind == TypeKind.Error &&
                    (attributeClass.Name == "ObservableAttribute" || attributeClass.Name == "Observable"))
                    return true;
            }
        }
        return false;
    }

    // Looks for RaisePropertyChanged / OnPropertyChanged(PropertyChangedEventArgs | string) on the class and its bases
    private static RaiseInfo? FindHelper(INamedTypeSymbol cls, Compilation compilation)
    {
        RaiseInfo? byName = null;
        for (var t = cls; t != null; t = t.BaseType)
        {
            foreach (var method in t.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.IsStatic || method.MethodKind != MethodKind.Ordinary || method.Parameters.Length != 1)
                    continue;
                if (method.Name != "RaisePropertyChanged" && method.Name != "OnPropertyChanged")
                    continue;
                if (!compilation.IsSymbolAccessibleWithin(method, cls))
                    continue;

                var parameter = method.Parameters[0].Type;
                if (parameter.ToDisplayString() == EventArgsName)
                    return RaiseInfo.WithArgs(method.Name);          // preferred: no string allocation per change
                if (parameter.SpecialType == SpecialType.System_String && byName == null)
                    byName = RaiseInfo.WithName(method.Name);
            }
        }
        return byName;
    }

    // ── Output ────────────────────────────────────────────────────────────────

    private static string GenerateSource(ObservableFieldInfo first, List<ObservableFieldInfo> fields)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> Do not edit.");
        sb.AppendLine("// Generated by GIoC.SourceGenerator - [Observable] fields.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (first.Namespace != null)
        {
            sb.AppendLine($"namespace {first.Namespace};");
            sb.AppendLine();
        }

        // Containing types (outermost first), then the class itself
        int depth = first.TypeHeaders.Count;
        for (int i = 0; i < depth; i++)
        {
            var indent = new string(' ', i * 4);
            var header = first.TypeHeaders[i];
            if (i == depth - 1 && first.Raise.Kind == RaiseKind.DeclareOwn)
                header += " : global::System.ComponentModel.INotifyPropertyChanged";
            sb.AppendLine($"{indent}{header}");
            sb.AppendLine($"{indent}{{");
        }

        var m = new string(' ', depth * 4);   // member indent

        string raiseName = first.Raise.Name;
        if (first.Raise.Kind == RaiseKind.DeclareOwn)
        {
            raiseName = "RaisePropertyChanged";
            // 'protected' lets derived [Observable] classes reuse the one event; a sealed class cannot have protected members
            var access = first.IsSealed ? "private" : "protected";
            sb.AppendLine($"{m}public event global::System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;");
            sb.AppendLine($"{m}{access} void RaisePropertyChanged(global::System.ComponentModel.PropertyChangedEventArgs args) => PropertyChanged?.Invoke(this, args);");
            sb.AppendLine();
        }

        foreach (var f in fields)
        {
            var argsField = $"_{f.FieldName.TrimStart('_')}ChangedArgs";
            string raiseCall;
            if (first.Raise.Kind == RaiseKind.Name)
            {
                raiseCall = $"{raiseName}(\"{f.PropName}\");";
            }
            else
            {
                sb.AppendLine($"{m}private static readonly global::System.ComponentModel.PropertyChangedEventArgs {argsField} = new(\"{f.PropName}\");");
                raiseCall = $"{raiseName}({argsField});";
            }

            sb.AppendLine($"{m}public {f.TypeName} {f.PropName}");
            sb.AppendLine($"{m}{{");
            sb.AppendLine($"{m}    get => {f.FieldName};");
            sb.AppendLine($"{m}    set");
            sb.AppendLine($"{m}    {{");
            // EqualityComparer instead of '==': works for every type, including structs without the operator
            sb.AppendLine($"{m}        if (global::System.Collections.Generic.EqualityComparer<{f.TypeName}>.Default.Equals({f.FieldName}, value)) return;");
            sb.AppendLine($"{m}        {f.FieldName} = value;");
            sb.AppendLine($"{m}        {raiseCall}");
            sb.AppendLine($"{m}    }}");
            sb.AppendLine($"{m}}}");
            sb.AppendLine();
        }

        for (int i = depth - 1; i >= 0; i--)
            sb.AppendLine($"{new string(' ', i * 4)}}}");

        return sb.ToString();
    }

    /// <summary>Strips leading underscores and capitalises first letter: _health -> Health</summary>
    private static string ToPascalCase(string name)
    {
        var s = name.TrimStart('_');
        if (s.Length == 0) return name;
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    // ── Data ──────────────────────────────────────────────────────────────────

    private enum RaiseKind { DeclareOwn, Args, Name, Missing }

    private sealed class RaiseInfo
    {
        public RaiseKind Kind { get; }
        public string Name { get; }
        private RaiseInfo(RaiseKind kind, string name) { Kind = kind; Name = name; }

        public static readonly RaiseInfo DeclareOwn = new RaiseInfo(RaiseKind.DeclareOwn, "RaisePropertyChanged");
        public static readonly RaiseInfo Missing = new RaiseInfo(RaiseKind.Missing, "");
        public static RaiseInfo WithArgs(string name) => new RaiseInfo(RaiseKind.Args, name);
        public static RaiseInfo WithName(string name) => new RaiseInfo(RaiseKind.Name, name);
    }

    private sealed class ObservableFieldInfo
    {
        public string ClassKey { get; }
        public string HintName { get; }
        public string ClassName { get; }
        public string? Namespace { get; }
        public List<string> TypeHeaders { get; }
        public bool IsSealed { get; }
        public RaiseInfo Raise { get; }
        public Location Location { get; }
        public string FieldName { get; }
        public string PropName { get; }
        public string TypeName { get; }

        public ObservableFieldInfo(string classKey, string hintName, string className, string? ns,
            List<string> typeHeaders, bool isSealed, RaiseInfo raise, Location location,
            string fieldName, string propName, string typeName)
        {
            ClassKey = classKey; HintName = hintName; ClassName = className; Namespace = ns;
            TypeHeaders = typeHeaders; IsSealed = isSealed; Raise = raise; Location = location;
            FieldName = fieldName; PropName = propName; TypeName = typeName;
        }
    }
}

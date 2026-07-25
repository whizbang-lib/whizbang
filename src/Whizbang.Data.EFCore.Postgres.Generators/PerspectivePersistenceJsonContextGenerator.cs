using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CancellationToken = System.Threading.CancellationToken;

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// Source generator that emits a per-assembly <c>PerspectivePersistenceJsonContext</c> —
/// an AOT-safe <see cref="System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver"/>
/// that serializes <c>[WhizbangId]</c> struct types in OBJECT mode (<c>{"Value":"&lt;guid&gt;"}</c>)
/// rather than the value-converter mode used by <c>WhizbangIdJsonContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// EF Core 10's <c>ComplexProperty().ToJson()</c> writer treats every non-primitive
/// property as a structural type — a <c>[WhizbangId]</c> property is serialized as
/// <c>{"Value":"&lt;guid&gt;"}</c>. The standard <c>WhizbangIdJsonContext</c> emitted by
/// <c>WhizbangIdGenerator</c> flattens it to a primitive string via <c>CreateValueInfo</c> +
/// <c>WhizbangIdConverter</c>. Those two byte formats diverge.
/// </para>
/// <para>
/// To support the atomic <c>INSERT … ON CONFLICT DO UPDATE</c> path in
/// <c>BaseUpsertStrategy</c>, we need an options chain whose resolver returns object-mode
/// <c>JsonTypeInfo</c> for <c>[WhizbangId]</c> types. This generator emits exactly that
/// context, plus a <c>CreateOptions()</c> factory that wires the resolver chain in the
/// right order: the persistence context first, then <c>MessageJsonContext</c> for
/// perspective TModels and any other registered types, then
/// <c>InfrastructureJsonContext</c> for system types (<c>PerspectiveMetadata</c>,
/// <c>PerspectiveScope</c>, etc.). <c>WhizbangIdJsonContext</c> is deliberately omitted —
/// otherwise its value-converter <c>JsonTypeInfo</c> for the same <c>[WhizbangId]</c>
/// types would be reachable through the chain.
/// </para>
/// </remarks>
/// <docs>extending/internals/event-ordering-invariant</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectivePersistenceJsonContextTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectivePersistenceByteFormatIntegrationTests.cs</tests>
[Generator]
#pragma warning disable S1144 // Initialize is the public IIncrementalGenerator entry point
public class PerspectivePersistenceJsonContextGenerator : IIncrementalGenerator {
#pragma warning restore S1144

  private const string WHIZBANG_ID_ATTRIBUTE = "Whizbang.Core.WhizbangIdAttribute";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover all [WhizbangId] struct declarations in the compilation.
    // Syntactic predicate filters cheaply, semantic transform confirms the attribute.
    var whizbangIds = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is StructDeclarationSyntax { AttributeLists.Count: > 0 },
        transform: static (ctx, ct) => _extractWhizbangId(ctx, ct)
    ).Where(static info => info is not null);

    // Discover perspective implementations syntactically. We can't check for
    // MessageJsonContext via Compilation.GetTypeByMetadataName because source generators
    // run in parallel against user source only — MessageJsonContextGenerator's output
    // isn't visible to us. But MessageJsonContextGenerator emits a MessageJsonContext
    // whenever it sees an IPerspectiveFor / IPerspectiveWithActionsFor implementation,
    // and so can we. If a perspective is present in user source, the callback is safe
    // to emit (consumer has a MessageJsonContext by construction).
    var perspectiveCandidates = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) =>
            (node is ClassDeclarationSyntax cls && cls.BaseList is not null) ||
            (node is RecordDeclarationSyntax rec && rec.BaseList is not null),
        transform: static (ctx, ct) => _hasPerspectiveInterface(ctx, ct))
      .Where(static result => result);

    var assemblyAndState = context.CompilationProvider
        .Select(static (compilation, _) => compilation.AssemblyName ?? "Generated")
        .Combine(perspectiveCandidates.Collect())
        .Combine(whizbangIds.Collect());

    context.RegisterSourceOutput(
        assemblyAndState,
        static (ctx, data) => _emit(
            ctx,
            assemblyName: data.Left.Left,
            hasPerspectives: !data.Left.Right.IsEmpty,
            infos: data.Right));
  }

  /// <summary>
  /// Returns true when the class/record declaration implements any
  /// <c>IPerspectiveFor&lt;TModel, …&gt;</c> or <c>IPerspectiveWithActionsFor&lt;TModel, …&gt;</c>
  /// interface. Mirrors the discovery logic in <c>EFCorePerspectiveAssociationGenerator</c>
  /// and <c>MessageJsonContextGenerator</c>, ensuring we only auto-wire when a
  /// MessageJsonContext will exist in the same assembly.
  /// </summary>
  private static bool _hasPerspectiveInterface(GeneratorSyntaxContext ctx, CancellationToken ct) {
    var symbol = ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct);
    if (symbol is not INamedTypeSymbol named || named.IsAbstract) {
      return false;
    }
    foreach (var iface in named.AllInterfaces) {
      var originalDef = iface.OriginalDefinition.ToDisplayString();
      if (originalDef.StartsWith("Whizbang.Core.Perspectives.IPerspectiveFor<", StringComparison.Ordinal) ||
          originalDef.StartsWith("Whizbang.Core.Perspectives.IPerspectiveWithActionsFor<", StringComparison.Ordinal) ||
          originalDef.StartsWith("Whizbang.Core.Perspectives.IPerspectiveBase<", StringComparison.Ordinal)) {
        return true;
      }
    }
    return false;
  }

  /// <summary>
  /// Extracts WhizbangId metadata from a struct declaration carrying [WhizbangId].
  /// </summary>
  private static WhizbangIdInfo? _extractWhizbangId(
      GeneratorSyntaxContext context,
      CancellationToken ct) {
    var structDecl = (StructDeclarationSyntax)context.Node;
    var structSymbol = context.SemanticModel.GetDeclaredSymbol(structDecl, ct);
    if (structSymbol is null) {
      return null;
    }

    var hasWhizbangIdAttribute = structSymbol.GetAttributes().Any(static a =>
        a.AttributeClass?.Name == "WhizbangIdAttribute" ||
        a.AttributeClass?.Name == "WhizbangId" ||
        a.AttributeClass?.ToDisplayString() == WHIZBANG_ID_ATTRIBUTE ||
        a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{WHIZBANG_ID_ATTRIBUTE}");

    if (!hasWhizbangIdAttribute) {
      return null;
    }

    // Only generate for partial structs (matches WhizbangIdGenerator's gate).
    if (!structDecl.Modifiers.Any(m => m.Text == "partial")) {
      return null;
    }

    return new WhizbangIdInfo(
        TypeName: structSymbol.Name,
        Namespace: structSymbol.ContainingNamespace?.ToDisplayString() ?? "Global",
        FullyQualifiedName: structSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
  }

  /// <summary>
  /// Emits a single PerspectivePersistenceJsonContext.g.cs file for the assembly.
  /// </summary>
  private static void _emit(
      SourceProductionContext context,
      string assemblyName,
      bool hasPerspectives,
      ImmutableArray<WhizbangIdInfo?> infos) {
    var distinct = infos
        .Where(static i => i is not null)
        .Select(static i => i!)
        .GroupBy(static i => i.FullyQualifiedName)
        .Select(static g => g.First())
        .OrderBy(static i => i.FullyQualifiedName, StringComparer.Ordinal)
        .ToImmutableArray();

    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine("// Generated by Whizbang.Data.EFCore.Postgres.Generators.PerspectivePersistenceJsonContextGenerator");
    sb.AppendLine("// DO NOT EDIT — Changes will be overwritten on next build.");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();
    sb.AppendLine("using System;");
    sb.AppendLine("using System.Runtime.CompilerServices;");
    sb.AppendLine("using System.Text.Json;");
    sb.AppendLine("using System.Text.Json.Serialization;");
    sb.AppendLine("using System.Text.Json.Serialization.Metadata;");
    sb.AppendLine();
    sb.AppendLine($"namespace {assemblyName}.Generated;");
    sb.AppendLine();
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Source-generated JsonTypeInfoResolver that emits object-mode JsonTypeInfo");
    sb.AppendLine("/// for [WhizbangId] structs (matches EF Core 10's ComplexProperty().ToJson() byte format).");
    sb.AppendLine("/// Used by BaseUpsertStrategy's atomic INSERT...ON CONFLICT DO UPDATE path.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("public sealed class PerspectivePersistenceJsonContext : IJsonTypeInfoResolver {");
    sb.AppendLine("  /// <summary>Singleton instance.</summary>");
    sb.AppendLine("  public static PerspectivePersistenceJsonContext Default { get; } = new();");
    sb.AppendLine();
    sb.AppendLine("  /// <inheritdoc/>");
    sb.AppendLine("  public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) {");

    if (distinct.IsEmpty) {
      sb.AppendLine("    // No [WhizbangId] types discovered in this assembly.");
      sb.AppendLine("    return null;");
    } else {
      foreach (var info in distinct) {
        var fqn = info.FullyQualifiedName;
        sb.AppendLine($"    if (type == typeof({fqn})) {{");
        sb.AppendLine($"      return _create{info.TypeName}TypeInfo(options);");
        sb.AppendLine("    }");
        sb.AppendLine($"    if (type == typeof({fqn}?)) {{");
        sb.AppendLine($"      return _create{info.TypeName}NullableTypeInfo(options);");
        sb.AppendLine("    }");
      }
      sb.AppendLine("    return null;");
    }

    sb.AppendLine("  }");
    sb.AppendLine();
    sb.AppendLine("  /// <summary>");
    sb.AppendLine("  /// Creates JsonSerializerOptions wired with the persistence resolver chain.");
    sb.AppendLine("  /// Chain order: this context (object-mode [WhizbangId]) — first, so it wins for WhizbangId types;");
    sb.AppendLine("  /// then any other resolver caller chooses to add. Deliberately does NOT include");
    sb.AppendLine("  /// WhizbangIdJsonContext or its value-converter, which would re-flatten WhizbangId structs.");
    sb.AppendLine("  /// </summary>");
    sb.AppendLine("  public static JsonSerializerOptions CreateOptions(params IJsonTypeInfoResolver[] additionalResolvers) {");
    sb.AppendLine("    var resolvers = new IJsonTypeInfoResolver[1 + (additionalResolvers?.Length ?? 0)];");
    sb.AppendLine("    resolvers[0] = Default;");
    sb.AppendLine("    if (additionalResolvers != null) {");
    sb.AppendLine("      for (int i = 0; i < additionalResolvers.Length; i++) {");
    sb.AppendLine("        resolvers[i + 1] = additionalResolvers[i];");
    sb.AppendLine("      }");
    sb.AppendLine("    }");
    sb.AppendLine("    return new JsonSerializerOptions {");
    sb.AppendLine("      TypeInfoResolver = JsonTypeInfoResolver.Combine(resolvers),");
    sb.AppendLine("      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull");
    sb.AppendLine("    };");
    sb.AppendLine("  }");
    sb.AppendLine();

    foreach (var info in distinct) {
      _emitWhizbangIdFactory(sb, info);
    }

    sb.AppendLine("}");

    context.AddSource("PerspectivePersistenceJsonContext.g.cs", sb.ToString());

    // Auto-wire the Path 1 atomic-upsert provider when the consumer has at least one
    // perspective. We detect this syntactically — checking for IPerspectiveFor /
    // IPerspectiveWithActionsFor / IPerspectiveBase implementations in user source —
    // because we can't see MessageJsonContextGenerator's output (source generators
    // run in parallel against user source only). The signals are equivalent:
    // MessageJsonContext is always emitted alongside discovered perspectives.
    //
    // The atomic INSERT...ON CONFLICT DO UPDATE path works for plain-Guid TModels too
    // — it doesn't require any [WhizbangId] types. A consumer's services that use
    // raw Guid Ids still benefit from atomic UPSERT.
    if (hasPerspectives) {
      _emitCallbackInitializer(context, assemblyName);
    }
  }

  /// <summary>
  /// Emits a sibling <c>PerspectivePersistenceCallbackInitializer.g.cs</c> whose
  /// <c>[ModuleInitializer]</c> registers a callback with
  /// <c>ServiceRegistrationCallbacks.PerspectivePersistenceOptions</c>. The callback,
  /// when fired from <c>AddWhizbang() → InvokeAll()</c>, points
  /// <c>BaseUpsertStrategy.PathOnePersistenceOptionsProvider</c> at the per-assembly
  /// resolver chain (this generator's <c>PerspectivePersistenceJsonContext</c> +
  /// the consumer's <c>MessageJsonContext</c> + Core's <c>InfrastructureJsonContext</c>).
  /// </summary>
  /// <remarks>
  /// Routing through <c>ServiceRegistrationCallbacks</c> rather than setting the static
  /// hook directly from a <c>[ModuleInitializer]</c> ensures order is governed by
  /// <c>InvokeAll</c>, not by non-deterministic cross-assembly module-load order. Tests
  /// that don't call <c>AddWhizbang</c> see the static hook stay <c>null</c> by default,
  /// preserving the slice-19 retry path for unit-test scenarios.
  /// </remarks>
  private static void _emitCallbackInitializer(
      SourceProductionContext context,
      string assemblyName) {
    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine("// Generated by Whizbang.Data.EFCore.Postgres.Generators.PerspectivePersistenceJsonContextGenerator");
    sb.AppendLine("// DO NOT EDIT — Changes will be overwritten on next build.");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();
    sb.AppendLine("using System;");
    sb.AppendLine("using System.Runtime.CompilerServices;");
    sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
    sb.AppendLine("using Whizbang.Core;");
    sb.AppendLine("using Whizbang.Data.EFCore.Postgres;");
    sb.AppendLine();
    sb.AppendLine($"namespace {assemblyName}.Generated;");
    sb.AppendLine();
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Auto-wires the Path 1 atomic-upsert options provider on AddWhizbang().");
    sb.AppendLine("/// Module initializer registers a callback with ServiceRegistrationCallbacks");
    sb.AppendLine("/// rather than setting the static hook directly — that way cross-assembly");
    sb.AppendLine("/// ordering is owned by InvokeAll, not by module-load timing.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("internal static class PerspectivePersistenceCallbackInitializer {");
    sb.AppendLine("#pragma warning disable CA2255  // Intentional ModuleInitializer for AOT-safe auto-registration.");
    sb.AppendLine("  [ModuleInitializer]");
    sb.AppendLine("#pragma warning restore CA2255");
    sb.AppendLine("  internal static void Initialize() {");
    sb.AppendLine("    // Join the cross-assembly serialization union under the Persistence profile at high");
    sb.AppendLine("    // priority, so object-mode WhizbangId JsonTypeInfo out-ranks the scalar WhizbangIdJsonContext");
    sb.AppendLine("    // resolver there. CreateCombinedOptions(Persistence) then carries object-mode WhizbangId");
    sb.AppendLine("    // (EF Core 10 jsonb byte format) for every assembly's perspectives, not just this one.");
    sb.AppendLine("    global::Whizbang.Core.Serialization.JsonContextRegistry.RegisterContext(");
    sb.AppendLine("      PerspectivePersistenceJsonContext.Default,");
    sb.AppendLine("      priority: 1000,");
    sb.AppendLine("      profile: global::Whizbang.Core.Serialization.SerializationProfile.Persistence);");
    sb.AppendLine();
    sb.AppendLine("    ServiceRegistrationCallbacks.PerspectivePersistenceOptions = _ =>");
    sb.AppendLine("      BaseUpsertStrategy.PathOnePersistenceOptionsProvider = () =>");
    sb.AppendLine("        PerspectivePersistenceJsonContext.CreateOptions(");
    sb.AppendLine("          MessageJsonContext.Default,");
    sb.AppendLine("          global::Whizbang.Core.Generated.InfrastructureJsonContext.Default);");
    sb.AppendLine("  }");
    sb.AppendLine("}");

    context.AddSource("PerspectivePersistenceCallbackInitializer.g.cs", sb.ToString());
  }

  /// <summary>
  /// Emits the per-WhizbangId factory method pair: object-mode JsonTypeInfo and its nullable counterpart.
  /// The object-mode form treats the struct as a single-property object with a "Value" property of type Guid,
  /// producing the JSON shape <c>{"Value":"&lt;guid&gt;"}</c> that EF Core 10's writer expects.
  /// </summary>
  private static void _emitWhizbangIdFactory(StringBuilder sb, WhizbangIdInfo info) {
    var fqn = info.FullyQualifiedName;
    sb.AppendLine($"  private static JsonTypeInfo<{fqn}> _create{info.TypeName}TypeInfo(JsonSerializerOptions options) {{");
    sb.AppendLine($"    var objectInfo = new JsonObjectInfoValues<{fqn}> {{");
    sb.AppendLine($"      ObjectCreator = static () => default({fqn}),");
    sb.AppendLine($"      ObjectWithParameterizedConstructorCreator = static args => new {fqn}((global::System.Guid)args[0]!),");
    sb.AppendLine("      ConstructorParameterMetadataInitializer = static () => new JsonParameterInfoValues[] {");
    sb.AppendLine("        new() {");
    sb.AppendLine("          Name = \"Value\",");
    sb.AppendLine("          ParameterType = typeof(global::System.Guid),");
    sb.AppendLine("          Position = 0,");
    sb.AppendLine("          HasDefaultValue = false,");
    sb.AppendLine("          DefaultValue = default!");
    sb.AppendLine("        }");
    sb.AppendLine("      },");
    // PropertyMetadataInitializer takes JsonSerializerContext (per JsonObjectInfoValues<T>),
    // not JsonSerializerOptions. Discard the parameter and capture the outer `options` instead.
    sb.AppendLine("      PropertyMetadataInitializer = _ => {");
    sb.AppendLine("        var properties = new JsonPropertyInfo[1];");
    sb.AppendLine("        properties[0] = JsonMetadataServices.CreatePropertyInfo<global::System.Guid>(");
    sb.AppendLine("          options,");
    sb.AppendLine("          new JsonPropertyInfoValues<global::System.Guid> {");
    sb.AppendLine("            IsProperty = true,");
    sb.AppendLine("            IsPublic = true,");
    sb.AppendLine("            IsVirtual = false,");
    sb.AppendLine($"            DeclaringType = typeof({fqn}),");
    sb.AppendLine($"            Getter = static obj => (({fqn})obj!).Value,");
    sb.AppendLine("            Setter = null,");
    sb.AppendLine("            JsonPropertyName = \"Value\",");
    sb.AppendLine("            PropertyName = \"Value\"");
    sb.AppendLine("          });");
    sb.AppendLine("        return properties;");
    sb.AppendLine("      }");
    sb.AppendLine("    };");
    sb.AppendLine();
    sb.AppendLine($"    return JsonMetadataServices.CreateObjectInfo<{fqn}>(options, objectInfo);");
    sb.AppendLine("  }");
    sb.AppendLine();
    sb.AppendLine($"  private static JsonTypeInfo<{fqn}?> _create{info.TypeName}NullableTypeInfo(JsonSerializerOptions options) {{");
    sb.AppendLine($"    var converter = JsonMetadataServices.GetNullableConverter<{fqn}>(options);");
    sb.AppendLine($"    return JsonMetadataServices.CreateValueInfo<{fqn}?>(options, converter);");
    sb.AppendLine("  }");
    sb.AppendLine();
  }

  /// <summary>
  /// Value-type record holding metadata for a discovered [WhizbangId] struct.
  /// Sealed record gives structural equality so the incremental generator caches correctly.
  /// </summary>
  internal sealed record WhizbangIdInfo(
      string TypeName,
      string Namespace,
      string FullyQualifiedName);
}

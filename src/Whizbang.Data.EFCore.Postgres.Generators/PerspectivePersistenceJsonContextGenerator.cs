// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;
using CancellationToken = System.Threading.CancellationToken;

namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// Source generator that emits a per-assembly <c>PerspectivePersistenceJsonContext</c>: an AOT-safe
/// <see cref="System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver"/> for the documents the atomic upsert
/// writes, whose <c>[WhizbangId]</c> struct types write the UUID string and read it or the object
/// <c>{"Value":"&lt;guid&gt;"}</c> a release before #2518 stored.
/// </summary>
/// <remarks>
/// <para>
/// Entity Framework writes a <c>[WhizbangId]</c> in a <c>ToJson()</c> document as the UUID string, through the id's
/// scalar type mapping (#2518). The transport's <c>WhizbangIdJsonContext</c> writes the same string but reads through
/// the id's <c>From</c>, which applies its version rule, and knows nothing of the object form. A stored row is
/// whatever was persisted, so the document read goes through the id's unchecked constructor and takes either form,
/// counting the object one until the stored-form rewrite has reached every row.
/// </para>
/// <para>
/// To support the atomic <c>INSERT … ON CONFLICT DO UPDATE</c> path in
/// <c>BaseUpsertStrategy</c>, we need an options chain whose resolver returns this document
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

  private const string CLOSE_BRACE_INDENT_4 = "    }";
  private const string CLOSE_BRACE_INDENT_6 = "      }";

  /// <summary>
  /// Syntax predicate: admits a struct declaration with at least one attribute.
  /// </summary>
  /// <remarks>Public so the build-cost guard can run it over ordinary code (#1650).</remarks>
  /// <tests>tests/Whizbang.Generators.Component.Tests/BuildCost/SyntaxCandidateGuardTests.cs</tests>
  public static bool IsAttributedStructCandidate(SyntaxNode node) =>
      node is StructDeclarationSyntax { AttributeLists.Count: > 0 };

  /// <summary>
  /// Syntax predicate: admits a class or record with a base list, so it can implement the interfaces the transform looks for.
  /// </summary>
  /// <remarks>Public so the build-cost guard can run it over ordinary code (#1650).</remarks>
  /// <tests>tests/Whizbang.Generators.Component.Tests/BuildCost/SyntaxCandidateGuardTests.cs</tests>
  public static bool IsClassOrRecordWithBaseListCandidate(SyntaxNode node) =>
      (node is ClassDeclarationSyntax cls && cls.BaseList is not null) || (node is RecordDeclarationSyntax rec && rec.BaseList is not null);

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover all [WhizbangId] struct declarations in the compilation.
    // Syntactic predicate filters cheaply, semantic transform confirms the attribute.
    var whizbangIds = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => IsAttributedStructCandidate(node),
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
        predicate: static (node, _) => IsClassOrRecordWithBaseListCandidate(node),
        transform: static (ctx, ct) => _perspectiveModelBehind(ctx, ct))
      .Where(static result => result is not null);

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
  /// The model type behind a class or record that implements any
  /// <c>IPerspectiveFor&lt;TModel, …&gt;</c> or <c>IPerspectiveWithActionsFor&lt;TModel, …&gt;</c>
  /// interface, or null for anything else. Mirrors the discovery logic in
  /// <c>EFCorePerspectiveAssociationGenerator</c> and <c>MessageJsonContextGenerator</c>, ensuring
  /// we only auto-wire when a MessageJsonContext will exist in the same assembly.
  /// </summary>
  /// <remarks>
  /// A name rather than a symbol, so the incremental pipeline caches on value equality. Nothing
  /// about the model's members is discovered here: the canonical temporal form is applied by the
  /// persistence profile's own converters to every document, not per model.
  /// </remarks>
  private static string? _perspectiveModelBehind(GeneratorSyntaxContext ctx, CancellationToken ct) {
    var symbol = ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct);
    if (symbol is not INamedTypeSymbol named || named.IsAbstract) {
      return null;
    }
    foreach (var iface in named.AllInterfaces) {
      var originalDef = TypeNameUtilities.Display(iface.OriginalDefinition);
      if (!originalDef.StartsWith("Whizbang.Core.Perspectives.IPerspectiveFor<", StringComparison.Ordinal) &&
          !originalDef.StartsWith("Whizbang.Core.Perspectives.IPerspectiveWithActionsFor<", StringComparison.Ordinal) &&
          !originalDef.StartsWith("Whizbang.Core.Perspectives.IPerspectiveBase<", StringComparison.Ordinal)) {
        continue;
      }

      return iface.TypeArguments.Length == 0 || iface.TypeArguments[0] is not INamedTypeSymbol model
        ? null
        : TypeNameUtilities.FullyQualified(model);
    }
    return null;
  }

  /// <summary>
  /// Extracts WhizbangId metadata from a struct declaration carrying [WhizbangId].
  /// </summary>
  private static WhizbangIdInfo? _extractWhizbangId(
      GeneratorSyntaxContext context,
      CancellationToken ct) {
    var structDecl = (StructDeclarationSyntax)context.Node;

    // The bind guard shares the "no [WhizbangId]" exit: a declaration Roslyn bound no symbol for
    // carries no attributes, so it fails the same test without ever being dereferenced.
    if (context.SemanticModel.GetDeclaredSymbol(structDecl, ct) is not { } structSymbol
        || !structSymbol.GetAttributes().Any(static a =>
            // Matched by simple name, which any class named WhizbangIdAttribute satisfies; a display-name or fully
            // qualified comparison could only agree with it, since Whizbang.Core.WhizbangIdAttribute has that name.
            TypeNameUtilities.SimpleNameOrNull(a.AttributeClass) is "WhizbangIdAttribute" or "WhizbangId")) {
      return null;
    }

    // Only generate for partial structs (matches WhizbangIdGenerator's gate).
    if (!structDecl.Modifiers.Any(m => m.Text == "partial")) {
      return null;
    }

    return new WhizbangIdInfo(
        TypeName: structSymbol.Name,
        FullyQualifiedName: TypeNameUtilities.FullyQualified(structSymbol));
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
    sb.AppendLine("/// Source-generated JsonTypeInfoResolver for the documents of perspective persistence: a [WhizbangId]");
    sb.AppendLine("/// struct is written as its UUID string, as Entity Framework writes it, and read from that or the object form.");
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
        sb.AppendLine(CLOSE_BRACE_INDENT_4);
        sb.AppendLine($"    if (type == typeof({fqn}?)) {{");
        sb.AppendLine($"      return _create{info.TypeName}NullableTypeInfo(options);");
        sb.AppendLine(CLOSE_BRACE_INDENT_4);
      }
      sb.AppendLine("    return null;");
    }

    sb.AppendLine("  }");
    sb.AppendLine();
    sb.AppendLine("  /// <summary>");
    sb.AppendLine("  /// Creates JsonSerializerOptions wired with the persistence resolver chain.");
    sb.AppendLine("  /// Chain order: this context (the document form of [WhizbangId]) first, so it wins for WhizbangId types;");
    sb.AppendLine("  /// then any other resolver caller chooses to add. Deliberately does NOT include");
    sb.AppendLine("  /// WhizbangIdJsonContext or its value-converter, whose read applies the id's version rule and refuses the object form.");
    sb.AppendLine("  /// Based on the persistence profile's own options, so the profile's converters (the canonical");
    sb.AppendLine("  /// temporal form among them) apply to a document serialized with these directly, exactly as");
    sb.AppendLine("  /// they apply to one the upsert writes.");
    sb.AppendLine("  /// </summary>");
    sb.AppendLine("  public static JsonSerializerOptions CreateOptions(params IJsonTypeInfoResolver[] additionalResolvers) {");
    sb.AppendLine("    var resolvers = new IJsonTypeInfoResolver[1 + (additionalResolvers?.Length ?? 0)];");
    sb.AppendLine("    resolvers[0] = Default;");
    sb.AppendLine("    if (additionalResolvers != null) {");
    sb.AppendLine("      for (int i = 0; i < additionalResolvers.Length; i++) {");
    sb.AppendLine("        resolvers[i + 1] = additionalResolvers[i];");
    sb.AppendLine(CLOSE_BRACE_INDENT_6);
    sb.AppendLine(CLOSE_BRACE_INDENT_4);
    sb.AppendLine("    return new JsonSerializerOptions(global::Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(global::Whizbang.Core.Serialization.SerializationProfile.Persistence)) {");
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

    // Join the persistence profile's union when the consumer has at least one
    // perspective; the atomic upsert resolves its options from that union. We detect this syntactically — checking for IPerspectiveFor /
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
  /// <c>[ModuleInitializer]</c> registers this assembly's <c>PerspectivePersistenceJsonContext</c>
  /// with <c>JsonContextRegistry</c> under the persistence profile.
  /// </summary>
  /// <remarks>
  /// The atomic upsert resolves its options from that profile's union, so this registration is
  /// the whole of its wiring. The registry is append-only with a generation counter: an assembly
  /// that loads late is picked up on the next write, and nothing can clear what was registered,
  /// so which write path runs never depends on what ran before.
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
    sb.AppendLine("using System.Runtime.CompilerServices;");
    sb.AppendLine();
    sb.AppendLine($"namespace {assemblyName}.Generated;");
    sb.AppendLine();
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Joins this assembly's perspective persistence context to the serialization registry's");
    sb.AppendLine("/// persistence profile, which the atomic upsert resolves its options from.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("internal static class PerspectivePersistenceCallbackInitializer {");
    sb.AppendLine("#pragma warning disable CA2255  // Intentional ModuleInitializer for AOT-safe auto-registration.");
    sb.AppendLine("  [ModuleInitializer]");
    sb.AppendLine("#pragma warning restore CA2255");
    sb.AppendLine("  internal static void Initialize() {");
    sb.AppendLine("    // Join the cross-assembly serialization union under the Persistence profile at high");
    sb.AppendLine("    // priority, so the document form of WhizbangId out-ranks the transport's WhizbangIdJsonContext");
    sb.AppendLine("    // resolver there. CreateCombinedOptions(Persistence) then carries the document form of WhizbangId");
    sb.AppendLine("    // (UUID string written, either form read) for every assembly's perspectives, not just this one.");
    sb.AppendLine("    global::Whizbang.Core.Serialization.JsonContextRegistry.RegisterContext(");
    sb.AppendLine("      PerspectivePersistenceJsonContext.Default,");
    sb.AppendLine("      priority: 1000,");
    sb.AppendLine("      profile: global::Whizbang.Core.Serialization.SerializationProfile.Persistence);");
    sb.AppendLine();

    // This registration is all the atomic upsert needs: it resolves its options from the registry's
    // persistence profile, so no startup hook sets anything for it. Nothing is registered for the
    // canonical temporal form here either. The converters are on the persistence profile's options,
    // registered by the framework's own initializer, and the serializer applies them wherever a
    // temporal occurs in any document.
    sb.AppendLine("  }");
    sb.AppendLine("}");

    context.AddSource("PerspectivePersistenceCallbackInitializer.g.cs", sb.ToString());
  }

  /// <summary>
  /// Emits the per-WhizbangId factory method pair: the document JsonTypeInfo and its nullable counterpart.
  /// It writes the id as a UUID string, the JSON shape Entity Framework's writer produces for the same
  /// property under the id's scalar type mapping (#2518), and reads that shape or the object
  /// <c>{"Value":"&lt;guid&gt;"}</c> a release before #2518 stored.
  /// </summary>
  /// <remarks>
  /// The object read is counted, so the tolerance can go once the stored-form rewrite has reached every row.
  /// The read goes through the id's unchecked constructor: a stored row is whatever was persisted.
  /// </remarks>
  private static void _emitWhizbangIdFactory(StringBuilder sb, WhizbangIdInfo info) {
    var fqn = info.FullyQualifiedName;
    sb.AppendLine($"  private sealed class _{info.TypeName}DocumentConverter : JsonConverter<{fqn}> {{");
    sb.AppendLine($"    public override {fqn} Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {{");
    sb.AppendLine("      if (reader.TokenType == JsonTokenType.String) {");
    sb.AppendLine($"        return new {fqn}(reader.GetGuid());");
    sb.AppendLine(CLOSE_BRACE_INDENT_6);
    sb.AppendLine("      if (reader.TokenType != JsonTokenType.StartObject) {");
    sb.AppendLine($"        throw new JsonException($\"A stored {info.TypeName} must be a UUID string or an object holding a Value, but the document holds {{reader.TokenType}}\");");
    sb.AppendLine(CLOSE_BRACE_INDENT_6);
    // Tagged as the type mapping's reader tags it, so one id type is one series whichever reader met the row.
    sb.AppendLine($"      global::Whizbang.Core.Perspectives.StoredFormFallbacks.ObjectFormIdentifierRead(global::Whizbang.Core.TypeNameFormatter.SimpleName(typeof({fqn})));");
    sb.AppendLine("      global::System.Guid value = default;");
    sb.AppendLine("      var found = false;");
    sb.AppendLine("      while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {");
    sb.AppendLine("        if (reader.TokenType == JsonTokenType.PropertyName && reader.GetString() == \"Value\") {");
    sb.AppendLine("          reader.Read();");
    sb.AppendLine("          value = reader.GetGuid();");
    sb.AppendLine("          found = true;");
    sb.AppendLine("        } else {");
    sb.AppendLine("          reader.Skip();");
    sb.AppendLine("        }");
    sb.AppendLine(CLOSE_BRACE_INDENT_6);
    sb.AppendLine("      if (!found) {");
    sb.AppendLine($"        throw new JsonException(\"A stored {info.TypeName} object holds no Value\");");
    sb.AppendLine(CLOSE_BRACE_INDENT_6);
    sb.AppendLine($"      return new {fqn}(value);");
    sb.AppendLine(CLOSE_BRACE_INDENT_4);
    sb.AppendLine();
    sb.AppendLine($"    public override void Write(Utf8JsonWriter writer, {fqn} value, JsonSerializerOptions options) {{");
    sb.AppendLine("      writer.WriteStringValue(value.Value);");
    sb.AppendLine(CLOSE_BRACE_INDENT_4);
    sb.AppendLine("  }");
    sb.AppendLine();
    sb.AppendLine($"  private static JsonTypeInfo<{fqn}> _create{info.TypeName}TypeInfo(JsonSerializerOptions options) {{");
    sb.AppendLine($"    return JsonMetadataServices.CreateValueInfo<{fqn}>(options, new _{info.TypeName}DocumentConverter());");
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
      string FullyQualifiedName);
}

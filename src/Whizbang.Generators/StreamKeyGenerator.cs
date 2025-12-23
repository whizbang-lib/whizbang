using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyGeneratorTests.cs:Generator_WithPropertyAttribute_GeneratesExtractorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyGeneratorTests.cs:Generator_WithMultipleEvents_GeneratesAllExtractorsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyGeneratorTests.cs:Generator_WithNoEvents_GeneratesEmptyExtractorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyGeneratorTests.cs:Generator_WithClassProperty_GeneratesExtractorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyGeneratorTests.cs:Generator_ReportsDiagnostic_ForEventWithNoStreamKeyAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyGeneratorTests.cs:Generator_WithNonPublicEvent_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyGeneratorTests.cs:Generator_WithAbstractEvent_ProcessesAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyGeneratorTests.cs:Generator_WithRecordAndClassProperties_GeneratesForBothAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyGeneratorTests.cs:Generator_WithNonEventType_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyGeneratorTests.cs:Generator_WithStructEvent_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyGeneratorTests.cs:StreamKeyGenerator_NullableValueTypeKey_GeneratesNullableExtractorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyGeneratorTests.cs:StreamKeyGenerator_NullableGuidKey_GeneratesNullableExtractorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyGeneratorTests.cs:StreamKeyGenerator_TypeNotImplementingIEvent_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamKeyGeneratorTests.cs:StreamKeyGenerator_ClassWithStreamKeyProperty_GeneratesExtractorAsync</tests>
/// Source generator that creates zero-reflection stream key extractors.
/// Replaces runtime reflection in StreamKeyResolver with compile-time code generation.
/// </summary>
[Generator]
public class StreamKeyGenerator : IIncrementalGenerator {
  private const string IEVENT_INTERFACE = "Whizbang.Core.IEvent";
  private const string STREAMKEY_ATTRIBUTE = "Whizbang.Core.StreamKeyAttribute";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover IEvent types with [StreamKey] attribute
    var eventsWithStreamKey = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is RecordDeclarationSyntax { BaseList.Types.Count: > 0 }
                                    || node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractStreamKeyInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Discover IEvent types WITHOUT [StreamKey] for diagnostics
    var eventsWithoutStreamKey = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is RecordDeclarationSyntax { BaseList.Types.Count: > 0 }
                                    || node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _findEventWithoutStreamKey(ctx, ct)
    ).Where(static info => info is not null);

    // Generate extractor methods from collected events
    // Combine compilation with discovered events to get assembly name for namespace
    var compilationAndEvents = context.CompilationProvider
        .Combine(eventsWithStreamKey.Collect())
        .Combine(eventsWithoutStreamKey.Collect());

    context.RegisterSourceOutput(
        compilationAndEvents,
        static (ctx, data) => {
          var compilation = data.Left.Left;
          var withStreamKey = data.Left.Right;
          var withoutStreamKey = data.Right;
          _generateStreamKeyExtractors(ctx, compilation, withStreamKey!, withoutStreamKey!);
        }
    );
  }

  private static StreamKeyInfo? _extractStreamKeyInfo(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    // Predicate guarantees node is RecordDeclarationSyntax or ClassDeclarationSyntax (both inherit from TypeDeclarationSyntax)
    // Defensive guard: throws if Roslyn returns null (indicates compiler bug)
    // See RoslynGuards.cs for rationale - no branch created, eliminates coverage gap
    var typeSymbol = RoslynGuards.GetTypeSymbolFromNode(context.Node, context.SemanticModel, ct);

    // Skip non-public types (can't access from generated code)
    if (typeSymbol.DeclaredAccessibility != Accessibility.Public) {
      return null;
    }

    // Check if implements IEvent
    var implementsIEvent = typeSymbol.AllInterfaces.Any(i =>
        i.ToDisplayString() == IEVENT_INTERFACE ||
        i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{IEVENT_INTERFACE}");

    if (!implementsIEvent) {
      return null;
    }

    // Look for [StreamKey] on properties
    foreach (var member in typeSymbol.GetMembers()) {
      if (member is IPropertySymbol property) {
        var hasStreamKeyAttr = property.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "StreamKeyAttribute" ||
            a.AttributeClass?.Name == "StreamKey" ||
            a.AttributeClass?.ToDisplayString() == STREAMKEY_ATTRIBUTE ||
            a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{STREAMKEY_ATTRIBUTE}");

        if (hasStreamKeyAttr) {
          return new StreamKeyInfo(
              EventType: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
              PropertyName: property.Name,
              PropertyType: property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
          );
        }
      }
    }

    // Look for [StreamKey] on constructor parameters (for records)
    var constructors = typeSymbol.Constructors;
    foreach (var ctor in constructors) {
      foreach (var parameter in ctor.Parameters) {
        var hasStreamKeyAttr = parameter.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "StreamKeyAttribute" ||
            a.AttributeClass?.Name == "StreamKey" ||
            a.AttributeClass?.ToDisplayString() == STREAMKEY_ATTRIBUTE ||
            a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{STREAMKEY_ATTRIBUTE}");

        if (hasStreamKeyAttr) {
          // Find corresponding property (records create properties from constructor parameters)
          var property = typeSymbol.GetMembers().OfType<IPropertySymbol>()
              .FirstOrDefault(p => p.Name.Equals(parameter.Name, System.StringComparison.OrdinalIgnoreCase));

          if (property is not null) {
            return new StreamKeyInfo(
                EventType: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                PropertyName: property.Name,
                PropertyType: property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            );
          }
        }
      }
    }

    return null;
  }

  private static EventWithoutStreamKeyInfo? _findEventWithoutStreamKey(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    // Predicate guarantees node is RecordDeclarationSyntax or ClassDeclarationSyntax (both inherit from TypeDeclarationSyntax)
    // Defensive guard: throws if Roslyn returns null (indicates compiler bug)
    // See RoslynGuards.cs for rationale - no branch created, eliminates coverage gap
    var typeSymbol = RoslynGuards.GetTypeSymbolFromNode(context.Node, context.SemanticModel, ct);

    // Skip non-public types (can't access from generated code)
    if (typeSymbol.DeclaredAccessibility != Accessibility.Public) {
      return null;
    }

    // Check if implements IEvent
    var implementsIEvent = typeSymbol.AllInterfaces.Any(i =>
        i.ToDisplayString() == IEVENT_INTERFACE ||
        i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{IEVENT_INTERFACE}");

    if (!implementsIEvent) {
      return null;
    }

    // Check if has [StreamKey] anywhere
    var hasStreamKeyOnProperty = typeSymbol.GetMembers().OfType<IPropertySymbol>().Any(p =>
        p.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "StreamKeyAttribute" ||
            a.AttributeClass?.Name == "StreamKey" ||
            a.AttributeClass?.ToDisplayString() == STREAMKEY_ATTRIBUTE ||
            a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{STREAMKEY_ATTRIBUTE}"));

    if (hasStreamKeyOnProperty) {
      return null;
    }

    var hasStreamKeyOnParameter = typeSymbol.Constructors.Any(ctor =>
        ctor.Parameters.Any(param =>
            param.GetAttributes().Any(a =>
                a.AttributeClass?.Name == "StreamKeyAttribute" ||
                a.AttributeClass?.Name == "StreamKey" ||
                a.AttributeClass?.ToDisplayString() == STREAMKEY_ATTRIBUTE ||
                a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{STREAMKEY_ATTRIBUTE}")));

    if (hasStreamKeyOnParameter) {
      return null;
    }

    // IEvent without [StreamKey] - return type name and location
    return new EventWithoutStreamKeyInfo(
        EventType: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        Location: context.Node.GetLocation()
    );
  }

  /// <summary>
  /// Generates stream key extractors with assembly-specific namespace to avoid conflicts.
  /// </summary>
  private static void _generateStreamKeyExtractors(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<StreamKeyInfo> eventsWithStreamKey,
      ImmutableArray<EventWithoutStreamKeyInfo> eventsWithoutStreamKey) {

    // Determine namespace from assembly name
    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    // Report diagnostics for events with stream keys
    foreach (var info in eventsWithStreamKey) {
      var simpleName = info.EventType.Split('.').Last().Replace("global::", "");
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.StreamKeyDiscovered,
          Location.None,
          simpleName,
          info.PropertyName
      ));
    }

    // Report diagnostics for events without stream keys
    foreach (var info in eventsWithoutStreamKey) {
      var simpleName = info.EventType.Split('.').Last().Replace("global::", "");
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.MissingStreamKeyAttribute,
          info.Location,  // Use actual location for proper suppression support
          simpleName
      ));
    }

    // Load template
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(StreamKeyGenerator).Assembly,
        "StreamKeyExtractorsTemplate.cs"
    );

    // Replace header with timestamp
    template = TemplateUtilities.ReplaceHeaderRegion(typeof(StreamKeyGenerator).Assembly, template);

    // Replace namespace region with assembly-specific namespace
    template = TemplateUtilities.ReplaceRegion(template, "NAMESPACE", $"namespace {namespaceName};");

    // Generate dispatch cases
    if (!eventsWithStreamKey.IsEmpty) {
      var dispatchSnippet = TemplateUtilities.ExtractSnippet(
          typeof(StreamKeyGenerator).Assembly,
          "StreamKeySnippets.cs",
          "DISPATCH_CASE"
      );

      var dispatchCode = new StringBuilder();
      dispatchCode.AppendLine("// Type-based dispatch to correct extractor");
      for (int i = 0; i < eventsWithStreamKey.Length; i++) {
        var info = eventsWithStreamKey[i];
        var caseCode = dispatchSnippet
            .Replace("__EVENT_TYPE__", info.EventType)
            .Replace("__INDEX__", i.ToString(CultureInfo.InvariantCulture));

        dispatchCode.AppendLine(caseCode);
      }

      template = TemplateUtilities.ReplaceRegion(template, "RESOLVE_DISPATCH", dispatchCode.ToString().TrimEnd());

      // Generate extractor methods
      var extractorsCode = new StringBuilder();
      for (int i = 0; i < eventsWithStreamKey.Length; i++) {
        var info = eventsWithStreamKey[i];
        var simpleName = info.EventType.Split('.').Last().Replace("global::", "");
        var propertyTypeName = info.PropertyType;

        // Check if property type is nullable (ends with ? or is a reference type)
        var isNullable = propertyTypeName.EndsWith("?", StringComparison.Ordinal) ||
                        propertyTypeName.Contains("string") ||
                        propertyTypeName.Contains("String");

        var extractorSnippet = isNullable
            ? TemplateUtilities.ExtractSnippet(
                typeof(StreamKeyGenerator).Assembly,
                "StreamKeySnippets.cs",
                "EXTRACTOR_NULLABLE"
              )
            : TemplateUtilities.ExtractSnippet(
                typeof(StreamKeyGenerator).Assembly,
                "StreamKeySnippets.cs",
                "EXTRACTOR_NON_NULLABLE"
              );

        var extractorCode = extractorSnippet
            .Replace("__EVENT_TYPE__", info.EventType)
            .Replace("__EVENT_NAME__", simpleName)
            .Replace("__PROPERTY_NAME__", info.PropertyName);

        if (i > 0) {
          extractorsCode.AppendLine();
        }
        extractorsCode.Append(extractorCode);
      }

      template = TemplateUtilities.ReplaceRegion(template, "EXTRACTORS", extractorsCode.ToString().TrimEnd());
    } else {
      // No events - leave default throw behavior in Resolve method
      template = TemplateUtilities.ReplaceRegion(template, "RESOLVE_DISPATCH", "");
      template = TemplateUtilities.ReplaceRegion(template, "EXTRACTORS", "");
    }

    context.AddSource("StreamKeyExtractors.g.cs", template);
  }
}

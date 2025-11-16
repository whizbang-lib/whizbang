using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators;

/// <summary>
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
        transform: static (ctx, ct) => ExtractStreamKeyInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Discover IEvent types WITHOUT [StreamKey] for diagnostics
    var eventsWithoutStreamKey = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is RecordDeclarationSyntax { BaseList.Types.Count: > 0 }
                                    || node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => FindEventWithoutStreamKey(ctx, ct)
    ).Where(static info => info is not null);

    // Generate extractor methods from collected events
    context.RegisterSourceOutput(
        eventsWithStreamKey.Collect().Combine(eventsWithoutStreamKey.Collect()),
        static (ctx, data) => GenerateStreamKeyExtractors(ctx, data.Left!, data.Right!)
    );
  }

  private static StreamKeyInfo? ExtractStreamKeyInfo(
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

  private static string? FindEventWithoutStreamKey(
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

    // IEvent without [StreamKey]
    return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
  }

  private static void GenerateStreamKeyExtractors(
      SourceProductionContext context,
      ImmutableArray<StreamKeyInfo> eventsWithStreamKey,
      ImmutableArray<string> eventsWithoutStreamKey) {

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
    foreach (var eventType in eventsWithoutStreamKey) {
      var simpleName = eventType.Split('.').Last().Replace("global::", "");
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.MissingStreamKeyAttribute,
          Location.None,
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
            .Replace("__INDEX__", i.ToString());

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
        var isNullable = propertyTypeName.EndsWith("?") ||
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

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
/// <tests>tests/Whizbang.Generators.Tests/StreamIdGeneratorTests.cs:Generator_WithPropertyAttribute_GeneratesExtractorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdGeneratorTests.cs:Generator_WithMultipleEvents_GeneratesAllExtractorsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdGeneratorTests.cs:Generator_WithNoEvents_GeneratesEmptyExtractorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdGeneratorTests.cs:Generator_WithClassProperty_GeneratesExtractorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdGeneratorTests.cs:Generator_ReportsDiagnostic_ForEventWithNoStreamIdAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdGeneratorTests.cs:Generator_WithNonPublicEvent_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdGeneratorTests.cs:Generator_WithAbstractEvent_ProcessesAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdGeneratorTests.cs:Generator_WithRecordAndClassProperties_GeneratesForBothAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdGeneratorTests.cs:Generator_WithNonEventType_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdGeneratorTests.cs:Generator_WithStructEvent_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdGeneratorTests.cs:StreamIdGenerator_NullableValueTypeKey_GeneratesNullableExtractorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdGeneratorTests.cs:StreamIdGenerator_NullableGuidKey_GeneratesNullableExtractorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdGeneratorTests.cs:StreamIdGenerator_TypeNotImplementingIEvent_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdGeneratorTests.cs:StreamIdGenerator_ClassWithStreamIdProperty_GeneratesExtractorAsync</tests>
/// Source generator that creates zero-reflection stream key extractors.
/// Replaces runtime reflection in StreamIdResolver with compile-time code generation.
/// </summary>
[Generator]
public class StreamIdGenerator : IIncrementalGenerator {
  private const string IEVENT_INTERFACE = "Whizbang.Core.IEvent";
  private const string ICOMMAND_INTERFACE = "Whizbang.Core.ICommand";
  private const string STREAMID_ATTRIBUTE = "Whizbang.Core.StreamIdAttribute";
  private const string STREAMID_ATTRIBUTE_NAME = "StreamIdAttribute";
  private const string STREAMID_SHORT_NAME = "StreamId";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover IEvent types with [StreamId] attribute
    var eventsWithStreamId = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is RecordDeclarationSyntax { BaseList.Types.Count: > 0 }
                                    || node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractStreamIdInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Discover IEvent types WITHOUT [StreamId] for diagnostics
    var eventsWithoutStreamId = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is RecordDeclarationSyntax { BaseList.Types.Count: > 0 }
                                    || node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _findEventWithoutStreamId(ctx, ct)
    ).Where(static info => info is not null);

    // Discover ICommand types with [StreamId] attribute
    var commandsWithStreamId = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is RecordDeclarationSyntax { BaseList.Types.Count: > 0 }
                                    || node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractCommandStreamIdInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Generate extractor methods from collected events and commands
    // Combine compilation with discovered events and commands to get assembly name for namespace
    var compilationAndData = context.CompilationProvider
        .Combine(eventsWithStreamId.Collect())
        .Combine(eventsWithoutStreamId.Collect())
        .Combine(commandsWithStreamId.Collect());

    context.RegisterSourceOutput(
        compilationAndData,
        static (ctx, data) => {
          var compilation = data.Left.Left.Left;
          var withStreamId = data.Left.Left.Right;
          var withoutStreamId = data.Left.Right;
          var commandsWithId = data.Right;
          _generateStreamIdExtractors(ctx, compilation, withStreamId!, withoutStreamId!, commandsWithId!);
        }
    );
  }

  private static StreamIdInfo? _extractStreamIdInfo(
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

    // Look for [StreamId] on properties (including inherited properties)
    var currentType = typeSymbol;
    while (currentType is not null) {
      foreach (var member in currentType.GetMembers()) {
        if (member is IPropertySymbol property) {
          var hasStreamIdAttr = property.GetAttributes().Any(a =>
              a.AttributeClass?.Name == STREAMID_ATTRIBUTE_NAME ||
              a.AttributeClass?.Name == STREAMID_SHORT_NAME ||
              a.AttributeClass?.ToDisplayString() == STREAMID_ATTRIBUTE ||
              a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{STREAMID_ATTRIBUTE}");

          if (hasStreamIdAttr) {
            return new StreamIdInfo(
                EventType: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                PropertyName: property.Name,
                PropertyType: property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                IsPropertyValueType: property.Type.IsValueType
            );
          }
        }
      }
      currentType = currentType.BaseType;
    }

    // Look for [StreamId] on constructor parameters (for records)
    var constructors = typeSymbol.Constructors;
    foreach (var ctor in constructors) {
      foreach (var parameter in ctor.Parameters) {
        var hasStreamIdAttr = parameter.GetAttributes().Any(a =>
            a.AttributeClass?.Name == STREAMID_ATTRIBUTE_NAME ||
            a.AttributeClass?.Name == STREAMID_SHORT_NAME ||
            a.AttributeClass?.ToDisplayString() == STREAMID_ATTRIBUTE ||
            a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{STREAMID_ATTRIBUTE}");

        if (hasStreamIdAttr) {
          // Find corresponding property (records create properties from constructor parameters)
          var property = typeSymbol.GetMembers().OfType<IPropertySymbol>()
              .FirstOrDefault(p => p.Name.Equals(parameter.Name, System.StringComparison.OrdinalIgnoreCase));

          if (property is not null) {
            return new StreamIdInfo(
                EventType: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                PropertyName: property.Name,
                PropertyType: property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                IsPropertyValueType: property.Type.IsValueType
            );
          }
        }
      }
    }

    return null;
  }

  private static EventWithoutStreamIdInfo? _findEventWithoutStreamId(
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

    // Check if has [StreamId] anywhere (including inherited properties)
    var hasStreamIdOnProperty = false;
    var checkType = typeSymbol;
    while (checkType is not null && !hasStreamIdOnProperty) {
      hasStreamIdOnProperty = checkType.GetMembers().OfType<IPropertySymbol>().Any(p =>
          p.GetAttributes().Any(a =>
              a.AttributeClass?.Name == STREAMID_ATTRIBUTE_NAME ||
              a.AttributeClass?.Name == STREAMID_SHORT_NAME ||
              a.AttributeClass?.ToDisplayString() == STREAMID_ATTRIBUTE ||
              a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{STREAMID_ATTRIBUTE}"));
      checkType = checkType.BaseType;
    }

    if (hasStreamIdOnProperty) {
      return null;
    }

    var hasStreamIdOnParameter = typeSymbol.Constructors.Any(ctor =>
        ctor.Parameters.Any(param =>
            param.GetAttributes().Any(a =>
                a.AttributeClass?.Name == STREAMID_ATTRIBUTE_NAME ||
                a.AttributeClass?.Name == STREAMID_SHORT_NAME ||
                a.AttributeClass?.ToDisplayString() == STREAMID_ATTRIBUTE ||
                a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{STREAMID_ATTRIBUTE}")));

    if (hasStreamIdOnParameter) {
      return null;
    }

    // IEvent without [StreamId] - return type name and location
    return new EventWithoutStreamIdInfo(
        EventType: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        Location: context.Node.GetLocation()
    );
  }

  private static CommandStreamIdInfo? _extractCommandStreamIdInfo(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    // Predicate guarantees node is RecordDeclarationSyntax or ClassDeclarationSyntax
    var typeSymbol = RoslynGuards.GetTypeSymbolFromNode(context.Node, context.SemanticModel, ct);

    // Skip non-public types (can't access from generated code)
    if (typeSymbol.DeclaredAccessibility != Accessibility.Public) {
      return null;
    }

    // Check if implements ICommand
    var implementsICommand = typeSymbol.AllInterfaces.Any(i =>
        i.ToDisplayString() == ICOMMAND_INTERFACE ||
        i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{ICOMMAND_INTERFACE}");

    if (!implementsICommand) {
      return null;
    }

    // Look for [StreamId] on properties (including inherited properties)
    var currentType = typeSymbol;
    while (currentType is not null) {
      foreach (var member in currentType.GetMembers()) {
        if (member is IPropertySymbol property) {
          var hasStreamIdAttr = property.GetAttributes().Any(a =>
              a.AttributeClass?.Name == STREAMID_ATTRIBUTE_NAME ||
              a.AttributeClass?.Name == STREAMID_SHORT_NAME ||
              a.AttributeClass?.ToDisplayString() == STREAMID_ATTRIBUTE ||
              a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{STREAMID_ATTRIBUTE}");

          if (hasStreamIdAttr) {
            return new CommandStreamIdInfo(
                CommandType: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                PropertyName: property.Name,
                PropertyType: property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                IsPropertyValueType: property.Type.IsValueType
            );
          }
        }
      }
      currentType = currentType.BaseType;
    }

    // Look for [StreamId] on constructor parameters (for records)
    var constructors = typeSymbol.Constructors;
    foreach (var ctor in constructors) {
      foreach (var parameter in ctor.Parameters) {
        var hasStreamIdAttr = parameter.GetAttributes().Any(a =>
            a.AttributeClass?.Name == STREAMID_ATTRIBUTE_NAME ||
            a.AttributeClass?.Name == STREAMID_SHORT_NAME ||
            a.AttributeClass?.ToDisplayString() == STREAMID_ATTRIBUTE ||
            a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{STREAMID_ATTRIBUTE}");

        if (hasStreamIdAttr) {
          // Find corresponding property (records create properties from constructor parameters)
          var property = typeSymbol.GetMembers().OfType<IPropertySymbol>()
              .FirstOrDefault(p => p.Name.Equals(parameter.Name, System.StringComparison.OrdinalIgnoreCase));

          if (property is not null) {
            return new CommandStreamIdInfo(
                CommandType: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                PropertyName: property.Name,
                PropertyType: property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                IsPropertyValueType: property.Type.IsValueType
            );
          }
        }
      }
    }

    return null;
  }

  /// <summary>
  /// Generates stream key extractors with assembly-specific namespace to avoid conflicts.
  /// </summary>
  private static void _generateStreamIdExtractors(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<StreamIdInfo> eventsWithStreamId,
      ImmutableArray<EventWithoutStreamIdInfo> eventsWithoutStreamId,
      ImmutableArray<CommandStreamIdInfo> commandsWithStreamId) {

    // Determine namespace from assembly name
    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    // Report diagnostics for events with stream keys
    foreach (var info in eventsWithStreamId) {
      var simpleName = info.EventType.Split('.')[^1].Replace("global::", "");
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.StreamIdDiscovered,
          Location.None,
          simpleName,
          info.PropertyName
      ));
    }

    // Report diagnostics for events without stream keys
    foreach (var info in eventsWithoutStreamId) {
      var simpleName = info.EventType.Split('.')[^1].Replace("global::", "");
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.MissingStreamIdAttribute,
          info.Location,  // Use actual location for proper suppression support
          simpleName
      ));
    }

    // Load template
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(StreamIdGenerator).Assembly,
        "StreamIdExtractorsTemplate.cs"
    );

    // Replace header with timestamp
    template = TemplateUtilities.ReplaceHeaderRegion(typeof(StreamIdGenerator).Assembly, template);

    // Replace namespace region with assembly-specific namespace
    template = TemplateUtilities.ReplaceRegion(template, "NAMESPACE", $"namespace {namespaceName};");

    // Generate dispatch cases
    if (!eventsWithStreamId.IsEmpty) {
      var dispatchSnippet = TemplateUtilities.ExtractSnippet(
          typeof(StreamIdGenerator).Assembly,
          "StreamIdSnippets.cs",
          "DISPATCH_CASE"
      );

      var dispatchCode = new StringBuilder();
      dispatchCode.AppendLine("// Type-based dispatch to correct extractor");
      for (int i = 0; i < eventsWithStreamId.Length; i++) {
        var info = eventsWithStreamId[i];
        var caseCode = dispatchSnippet
            .Replace("__EVENT_TYPE__", info.EventType)
            .Replace("__INDEX__", i.ToString(CultureInfo.InvariantCulture));

        dispatchCode.AppendLine(caseCode);
      }

      template = TemplateUtilities.ReplaceRegion(template, "RESOLVE_EVENT_DISPATCH", dispatchCode.ToString().TrimEnd());

      // Generate TryResolveAsGuid dispatch cases
      var tryDispatchSnippet = TemplateUtilities.ExtractSnippet(
          typeof(StreamIdGenerator).Assembly,
          "StreamIdSnippets.cs",
          "TRY_DISPATCH_CASE"
      );

      var tryDispatchCode = new StringBuilder();
      tryDispatchCode.AppendLine("// Type-based dispatch returning Guid?");
      for (int i = 0; i < eventsWithStreamId.Length; i++) {
        var info = eventsWithStreamId[i];
        var caseCode = tryDispatchSnippet
            .Replace("__EVENT_TYPE__", info.EventType)
            .Replace("__INDEX__", i.ToString(CultureInfo.InvariantCulture));

        tryDispatchCode.AppendLine(caseCode);
      }

      template = TemplateUtilities.ReplaceRegion(template, "TRY_RESOLVE_EVENT_DISPATCH", tryDispatchCode.ToString().TrimEnd());

      // Generate extractor methods
      var extractorsCode = new StringBuilder();
      for (int i = 0; i < eventsWithStreamId.Length; i++) {
        var info = eventsWithStreamId[i];
        var simpleName = info.EventType.Split('.')[^1].Replace("global::", "");
        var propertyTypeName = info.PropertyType;

        // Check if property type is nullable (ends with ? or is a reference type)
        var isNullable = propertyTypeName.EndsWith("?", StringComparison.Ordinal) ||
                        propertyTypeName.Contains("string") ||
                        propertyTypeName.Contains("String");

        var extractorSnippet = isNullable
            ? TemplateUtilities.ExtractSnippet(
                typeof(StreamIdGenerator).Assembly,
                "StreamIdSnippets.cs",
                "EXTRACTOR_NULLABLE"
              )
            : TemplateUtilities.ExtractSnippet(
                typeof(StreamIdGenerator).Assembly,
                "StreamIdSnippets.cs",
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

      template = TemplateUtilities.ReplaceRegion(template, "EVENT_EXTRACTORS", extractorsCode.ToString().TrimEnd());

      // Generate TryExtractAsGuid methods
      var tryExtractorsCode = new StringBuilder();
      for (int i = 0; i < eventsWithStreamId.Length; i++) {
        var info = eventsWithStreamId[i];
        var simpleName = info.EventType.Split('.')[^1].Replace("global::", "");
        var propertyTypeName = info.PropertyType;

        // Determine which TRY_EXTRACTOR snippet to use based on property type
        var tryExtractorSnippetName = _getTryExtractorSnippetName(propertyTypeName, info.IsPropertyValueType);
        var tryExtractorSnippet = TemplateUtilities.ExtractSnippet(
            typeof(StreamIdGenerator).Assembly,
            "StreamIdSnippets.cs",
            tryExtractorSnippetName
        );

        var tryExtractorCode = tryExtractorSnippet
            .Replace("__EVENT_TYPE__", info.EventType)
            .Replace("__EVENT_NAME__", simpleName)
            .Replace("__PROPERTY_NAME__", info.PropertyName);

        if (i > 0) {
          tryExtractorsCode.AppendLine();
        }
        tryExtractorsCode.Append(tryExtractorCode);
      }

      template = TemplateUtilities.ReplaceRegion(template, "TRY_EXTRACT_METHODS", tryExtractorsCode.ToString().TrimEnd());
    } else {
      // No events - leave default throw behavior in Resolve method
      template = TemplateUtilities.ReplaceRegion(template, "RESOLVE_EVENT_DISPATCH", "");
      template = TemplateUtilities.ReplaceRegion(template, "TRY_RESOLVE_EVENT_DISPATCH", "");
      template = TemplateUtilities.ReplaceRegion(template, "EVENT_EXTRACTORS", "");
      template = TemplateUtilities.ReplaceRegion(template, "TRY_EXTRACT_METHODS", "");
    }

    // Generate command dispatch and extractors
    if (!commandsWithStreamId.IsEmpty) {
      // Report diagnostics for commands with stream IDs
      foreach (var info in commandsWithStreamId) {
        var simpleName = info.CommandType.Split('.')[^1].Replace("global::", "");
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.CommandStreamIdDiscovered,
            Location.None,
            simpleName,
            info.PropertyName
        ));
      }

      // Generate command dispatch cases
      var commandDispatchSnippet = TemplateUtilities.ExtractSnippet(
          typeof(StreamIdGenerator).Assembly,
          "StreamIdSnippets.cs",
          "COMMAND_DISPATCH_CASE"
      );

      var commandDispatchCode = new StringBuilder();
      commandDispatchCode.AppendLine("// Type-based dispatch to correct command extractor");
      for (int i = 0; i < commandsWithStreamId.Length; i++) {
        var info = commandsWithStreamId[i];
        var caseCode = commandDispatchSnippet
            .Replace("__COMMAND_TYPE__", info.CommandType)
            .Replace("__INDEX__", i.ToString(CultureInfo.InvariantCulture));

        commandDispatchCode.AppendLine(caseCode);
      }

      template = TemplateUtilities.ReplaceRegion(template, "RESOLVE_COMMAND_DISPATCH", commandDispatchCode.ToString().TrimEnd());

      // Generate TryResolveAsGuid command dispatch cases
      var commandTryDispatchSnippet = TemplateUtilities.ExtractSnippet(
          typeof(StreamIdGenerator).Assembly,
          "StreamIdSnippets.cs",
          "COMMAND_TRY_DISPATCH_CASE"
      );

      var commandTryDispatchCode = new StringBuilder();
      commandTryDispatchCode.AppendLine("// Type-based dispatch returning Guid? for commands");
      for (int i = 0; i < commandsWithStreamId.Length; i++) {
        var info = commandsWithStreamId[i];
        var caseCode = commandTryDispatchSnippet
            .Replace("__COMMAND_TYPE__", info.CommandType)
            .Replace("__INDEX__", i.ToString(CultureInfo.InvariantCulture));

        commandTryDispatchCode.AppendLine(caseCode);
      }

      template = TemplateUtilities.ReplaceRegion(template, "TRY_RESOLVE_COMMAND_DISPATCH", commandTryDispatchCode.ToString().TrimEnd());

      // Generate command extractor methods
      var commandExtractorsCode = new StringBuilder();
      for (int i = 0; i < commandsWithStreamId.Length; i++) {
        var info = commandsWithStreamId[i];
        var simpleName = info.CommandType.Split('.')[^1].Replace("global::", "");
        var propertyTypeName = info.PropertyType;

        // Check if property type is nullable
        var isNullable = propertyTypeName.EndsWith("?", StringComparison.Ordinal) ||
                        propertyTypeName.Contains("string") ||
                        propertyTypeName.Contains("String");

        var extractorSnippet = isNullable
            ? TemplateUtilities.ExtractSnippet(
                typeof(StreamIdGenerator).Assembly,
                "StreamIdSnippets.cs",
                "COMMAND_EXTRACTOR_NULLABLE"
              )
            : TemplateUtilities.ExtractSnippet(
                typeof(StreamIdGenerator).Assembly,
                "StreamIdSnippets.cs",
                "COMMAND_EXTRACTOR_NON_NULLABLE"
              );

        var extractorCode = extractorSnippet
            .Replace("__COMMAND_TYPE__", info.CommandType)
            .Replace("__COMMAND_NAME__", simpleName)
            .Replace("__PROPERTY_NAME__", info.PropertyName);

        if (i > 0) {
          commandExtractorsCode.AppendLine();
        }
        commandExtractorsCode.Append(extractorCode);
      }

      // Generate TryExtractAsGuid methods for commands
      for (int i = 0; i < commandsWithStreamId.Length; i++) {
        var info = commandsWithStreamId[i];
        var simpleName = info.CommandType.Split('.')[^1].Replace("global::", "");
        var propertyTypeName = info.PropertyType;

        // Determine which COMMAND_TRY_EXTRACTOR snippet to use based on property type
        var tryExtractorSnippetName = _getCommandTryExtractorSnippetName(propertyTypeName, info.IsPropertyValueType);
        var tryExtractorSnippet = TemplateUtilities.ExtractSnippet(
            typeof(StreamIdGenerator).Assembly,
            "StreamIdSnippets.cs",
            tryExtractorSnippetName
        );

        var tryExtractorCode = tryExtractorSnippet
            .Replace("__COMMAND_TYPE__", info.CommandType)
            .Replace("__COMMAND_NAME__", simpleName)
            .Replace("__PROPERTY_NAME__", info.PropertyName);

        commandExtractorsCode.AppendLine();
        commandExtractorsCode.Append(tryExtractorCode);
      }

      template = TemplateUtilities.ReplaceRegion(template, "COMMAND_EXTRACTORS", commandExtractorsCode.ToString().TrimEnd());
    } else {
      template = TemplateUtilities.ReplaceRegion(template, "RESOLVE_COMMAND_DISPATCH", "");
      template = TemplateUtilities.ReplaceRegion(template, "TRY_RESOLVE_COMMAND_DISPATCH", "");
      template = TemplateUtilities.ReplaceRegion(template, "COMMAND_EXTRACTORS", "");
    }

    // Replace other regions with empty (perspective DTOs - not yet implemented)
    template = TemplateUtilities.ReplaceRegion(template, "TRY_RESOLVE_OTHER_DISPATCH", "");
    template = TemplateUtilities.ReplaceRegion(template, "OTHER_EXTRACTORS", "");

    // Generate [ModuleInitializer] registration code
    // Only register if this assembly has extractors (events or commands with [StreamId])
    var hasExtractors = !eventsWithStreamId.IsEmpty || !commandsWithStreamId.IsEmpty;
    if (hasExtractors) {
      // Register with priority 100 (contracts/types that define messages are tried first)
      var registrationCode = "global::Whizbang.Core.Registry.StreamIdExtractorRegistry.Register(new GeneratedStreamIdExtractor(), priority: 100);";
      template = TemplateUtilities.ReplaceRegion(template, "MODULE_INITIALIZER_REGISTRATION", registrationCode);
    } else {
      // No extractors - don't register anything (leave the region empty)
      template = TemplateUtilities.ReplaceRegion(template, "MODULE_INITIALIZER_REGISTRATION", "// No extractors in this assembly - skipping registration");
    }

    context.AddSource("StreamIdExtractors.g.cs", template);
  }

  /// <summary>
  /// Determines which TRY_EXTRACTOR snippet to use based on property type.
  /// </summary>
  /// <param name="propertyTypeName">The fully qualified property type name</param>
  /// <param name="isValueType">Whether the property type is a value type (struct)</param>
  private static string _getTryExtractorSnippetName(string propertyTypeName, bool isValueType) {
    // Normalize the type name for comparison
    var normalizedType = propertyTypeName
        .Replace("global::", "")
        .Replace("System.", "");

    // Check for Guid types
    if (normalizedType is "Guid" or "System.Guid") {
      return "TRY_EXTRACTOR_GUID";
    }

    if (normalizedType is "Guid?" or "System.Guid?" or "Nullable<Guid>" or "Nullable<System.Guid>") {
      return "TRY_EXTRACTOR_NULLABLE_GUID";
    }

    // Check for string types (reference type, can be null)
    if (normalizedType.Contains("string", StringComparison.OrdinalIgnoreCase)) {
      return "TRY_EXTRACTOR_STRING";
    }

    // Value types (structs including Vogen value objects) cannot be null-checked
    // Use VALUE_TYPE snippet which calls ToString() directly
    if (isValueType && !normalizedType.EndsWith("?", StringComparison.Ordinal)) {
      return "TRY_EXTRACTOR_VALUE_TYPE";
    }

    // For nullable value types and other reference types
    return "TRY_EXTRACTOR_OTHER";
  }

  /// <summary>
  /// Determines which COMMAND_TRY_EXTRACTOR snippet to use based on property type.
  /// </summary>
  /// <param name="propertyTypeName">The fully qualified property type name</param>
  /// <param name="isValueType">Whether the property type is a value type (struct)</param>
  private static string _getCommandTryExtractorSnippetName(string propertyTypeName, bool isValueType) {
    // Normalize the type name for comparison
    var normalizedType = propertyTypeName
        .Replace("global::", "")
        .Replace("System.", "");

    // Check for Guid types
    if (normalizedType is "Guid" or "System.Guid") {
      return "COMMAND_TRY_EXTRACTOR_GUID";
    }

    if (normalizedType is "Guid?" or "System.Guid?" or "Nullable<Guid>" or "Nullable<System.Guid>") {
      return "COMMAND_TRY_EXTRACTOR_NULLABLE_GUID";
    }

    // Check for string types (reference type, can be null)
    if (normalizedType.Contains("string", StringComparison.OrdinalIgnoreCase)) {
      return "COMMAND_TRY_EXTRACTOR_STRING";
    }

    // Value types (structs including Vogen value objects) cannot be null-checked
    // Use VALUE_TYPE snippet which calls ToString() directly
    if (isValueType && !normalizedType.EndsWith("?", StringComparison.Ordinal)) {
      return "COMMAND_TRY_EXTRACTOR_VALUE_TYPE";
    }

    // For nullable value types and other reference types
    return "COMMAND_TRY_EXTRACTOR_OTHER";
  }
}

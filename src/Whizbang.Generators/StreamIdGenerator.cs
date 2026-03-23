using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

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
    if (!TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_EVENT)) {
      return null;
    }

    // Look for [StreamId] on properties (including inherited properties)
    var streamIdProperty = typeSymbol.FindPropertyWithAttribute(StandardInterfaceNames.STREAM_ID_ATTRIBUTE);
    if (streamIdProperty is not null) {
      // Check for [GenerateStreamId] on the property itself
      var (hasGenerate, onlyIfEmpty) = _extractGenerateStreamIdInfo(streamIdProperty, typeSymbol);

      return new StreamIdInfo(
          EventType: TypeNameHelper.GetFullyQualifiedName(typeSymbol),
          PropertyName: streamIdProperty.Name,
          PropertyType: TypeNameHelper.GetFullyQualifiedName(streamIdProperty.Type),
          IsPropertyValueType: streamIdProperty.Type.IsValueType,
          HasGenerate: hasGenerate,
          OnlyIfEmpty: onlyIfEmpty,
          IsPropertyInitOnly: _isInitOnlyOrReadOnly(streamIdProperty)
      );
    }

    // Look for [StreamId] on constructor parameters (for records)
    var constructors = typeSymbol.Constructors;
    foreach (var ctor in constructors) {
      foreach (var parameter in ctor.Parameters) {
        var hasStreamIdAttr = parameter.GetAttributes().Any(a =>
            a.AttributeClass is not null &&
            TypeNameHelper.GetFullyQualifiedName(a.AttributeClass) == StandardInterfaceNames.STREAM_ID_ATTRIBUTE);

        if (hasStreamIdAttr) {
          // Find corresponding property (records create properties from constructor parameters)
          var property = typeSymbol.GetMembers().OfType<IPropertySymbol>()
              .FirstOrDefault(p => p.Name.Equals(parameter.Name, System.StringComparison.OrdinalIgnoreCase));

          if (property is not null) {
            // Check for [GenerateStreamId] on the parameter or on the class
            var hasGenerateOnParam = parameter.GetAttributes().Any(a =>
                a.AttributeClass is not null &&
                TypeNameHelper.GetFullyQualifiedName(a.AttributeClass) == StandardInterfaceNames.GENERATE_STREAM_ID_ATTRIBUTE);

            var (hasGenerateOnClass, classOnlyIfEmpty) = _extractGenerateStreamIdFromClass(typeSymbol);
            var hasGenerate = hasGenerateOnParam || hasGenerateOnClass;

            var onlyIfEmpty = false;
            if (hasGenerateOnParam) {
              onlyIfEmpty = _extractOnlyIfEmptyFromAttributes(parameter.GetAttributes());
            } else if (hasGenerateOnClass) {
              onlyIfEmpty = classOnlyIfEmpty;
            }

            return new StreamIdInfo(
                EventType: TypeNameHelper.GetFullyQualifiedName(typeSymbol),
                PropertyName: property.Name,
                PropertyType: TypeNameHelper.GetFullyQualifiedName(property.Type),
                IsPropertyValueType: property.Type.IsValueType,
                HasGenerate: hasGenerate,
                OnlyIfEmpty: onlyIfEmpty,
                IsPropertyInitOnly: _isInitOnlyOrReadOnly(property)
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
    if (!TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_EVENT)) {
      return null;
    }

    // Check if has [StreamId] anywhere (including inherited properties)
    var streamIdOnProperty = typeSymbol.FindPropertyWithAttribute(StandardInterfaceNames.STREAM_ID_ATTRIBUTE);
    if (streamIdOnProperty is not null) {
      return null;
    }

    var hasStreamIdOnParameter = typeSymbol.Constructors.Any(ctor =>
        ctor.Parameters.Any(param =>
            param.GetAttributes().Any(a =>
                a.AttributeClass is not null &&
                TypeNameHelper.GetFullyQualifiedName(a.AttributeClass) == StandardInterfaceNames.STREAM_ID_ATTRIBUTE)));

    if (hasStreamIdOnParameter) {
      return null;
    }

    // IEvent without [StreamId] - return type name and location
    return new EventWithoutStreamIdInfo(
        EventType: TypeNameHelper.GetFullyQualifiedName(typeSymbol),
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
    if (!TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_COMMAND)) {
      return null;
    }

    // Look for [StreamId] on properties (including inherited properties)
    var commandStreamIdProperty = typeSymbol.FindPropertyWithAttribute(StandardInterfaceNames.STREAM_ID_ATTRIBUTE);
    if (commandStreamIdProperty is not null) {
      // Check for [GenerateStreamId] on the property or class
      var (hasGenerate, onlyIfEmpty) = _extractGenerateStreamIdInfo(commandStreamIdProperty, typeSymbol);

      return new CommandStreamIdInfo(
          CommandType: TypeNameHelper.GetFullyQualifiedName(typeSymbol),
          PropertyName: commandStreamIdProperty.Name,
          PropertyType: TypeNameHelper.GetFullyQualifiedName(commandStreamIdProperty.Type),
          IsPropertyValueType: commandStreamIdProperty.Type.IsValueType,
          HasGenerate: hasGenerate,
          OnlyIfEmpty: onlyIfEmpty,
          IsPropertyInitOnly: _isInitOnlyOrReadOnly(commandStreamIdProperty)
      );
    }

    // Look for [StreamId] on constructor parameters (for records)
    var constructors = typeSymbol.Constructors;
    foreach (var ctor in constructors) {
      foreach (var parameter in ctor.Parameters) {
        var hasStreamIdAttr = parameter.GetAttributes().Any(a =>
            a.AttributeClass is not null &&
            TypeNameHelper.GetFullyQualifiedName(a.AttributeClass) == StandardInterfaceNames.STREAM_ID_ATTRIBUTE);

        if (hasStreamIdAttr) {
          // Find corresponding property (records create properties from constructor parameters)
          var property = typeSymbol.GetMembers().OfType<IPropertySymbol>()
              .FirstOrDefault(p => p.Name.Equals(parameter.Name, System.StringComparison.OrdinalIgnoreCase));

          if (property is not null) {
            // Check for [GenerateStreamId] on the parameter or on the class
            var hasGenerateOnParam = parameter.GetAttributes().Any(a =>
                a.AttributeClass is not null &&
                TypeNameHelper.GetFullyQualifiedName(a.AttributeClass) == StandardInterfaceNames.GENERATE_STREAM_ID_ATTRIBUTE);

            var (hasGenerateOnClass, classOnlyIfEmpty) = _extractGenerateStreamIdFromClass(typeSymbol);
            var hasGenerate = hasGenerateOnParam || hasGenerateOnClass;

            var onlyIfEmpty = false;
            if (hasGenerateOnParam) {
              onlyIfEmpty = _extractOnlyIfEmptyFromAttributes(parameter.GetAttributes());
            } else if (hasGenerateOnClass) {
              onlyIfEmpty = classOnlyIfEmpty;
            }

            return new CommandStreamIdInfo(
                CommandType: TypeNameHelper.GetFullyQualifiedName(typeSymbol),
                PropertyName: property.Name,
                PropertyType: TypeNameHelper.GetFullyQualifiedName(property.Type),
                IsPropertyValueType: property.Type.IsValueType,
                HasGenerate: hasGenerate,
                OnlyIfEmpty: onlyIfEmpty,
                IsPropertyInitOnly: _isInitOnlyOrReadOnly(property)
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

    // Generate GetGenerationPolicy dispatch cases (events AND commands)
    var eventsWithGenerate = eventsWithStreamId.Where(e => e.HasGenerate).ToImmutableArray();
    var commandsWithGenerate = commandsWithStreamId.Where(c => c.HasGenerate).ToImmutableArray();
    if (!eventsWithGenerate.IsEmpty || !commandsWithGenerate.IsEmpty) {
      var generationPolicySnippet = TemplateUtilities.ExtractSnippet(
          typeof(StreamIdGenerator).Assembly,
          "StreamIdSnippets.cs",
          "GENERATION_POLICY_CASE"
      );

      var generationPolicyCode = new StringBuilder();
      generationPolicyCode.AppendLine("// Type-based dispatch for generation policy");
      foreach (var info in eventsWithGenerate) {
        var caseCode = generationPolicySnippet
            .Replace("__EVENT_TYPE__", info.EventType)
            .Replace("__SHOULD_GENERATE__", "true")
            .Replace("__ONLY_IF_EMPTY__", info.OnlyIfEmpty ? "true" : "false");

        generationPolicyCode.AppendLine(caseCode);
      }

      foreach (var info in commandsWithGenerate) {
        var caseCode = generationPolicySnippet
            .Replace("__EVENT_TYPE__", info.CommandType)
            .Replace("__SHOULD_GENERATE__", "true")
            .Replace("__ONLY_IF_EMPTY__", info.OnlyIfEmpty ? "true" : "false");

        generationPolicyCode.AppendLine(caseCode);
      }

      template = TemplateUtilities.ReplaceRegion(template, "GENERATION_POLICY_DISPATCH", generationPolicyCode.ToString().TrimEnd());
    } else {
      template = TemplateUtilities.ReplaceRegion(template, "GENERATION_POLICY_DISPATCH", "");
    }

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

    // Generate SetStreamId dispatch cases (events AND commands with Guid [StreamId] properties)
    var setStreamIdCode = new StringBuilder();
    var hasSetterCases = false;

    // Only generate setter for types with Guid [StreamId] properties that have a regular set accessor
    // (skip init-only and read-only properties)
    var eventIndex = 0;
    foreach (var info in eventsWithStreamId) {
      if (_isGuidProperty(info.PropertyType) && !info.IsPropertyInitOnly) {
        var setEventSnippet = TemplateUtilities.ExtractSnippet(
            typeof(StreamIdGenerator).Assembly,
            "StreamIdSnippets.cs",
            "SET_STREAM_ID_EVENT_CASE"
        );
        var caseCode = setEventSnippet
            .Replace("__EVENT_TYPE__", info.EventType)
            .Replace("__PROPERTY_NAME__", info.PropertyName)
            .Replace("__INDEX__", eventIndex.ToString(CultureInfo.InvariantCulture));
        setStreamIdCode.AppendLine(caseCode);
        hasSetterCases = true;
      }
      eventIndex++;
    }

    var commandIndex = 0;
    foreach (var info in commandsWithStreamId) {
      if (_isGuidProperty(info.PropertyType) && !info.IsPropertyInitOnly) {
        var setCommandSnippet = TemplateUtilities.ExtractSnippet(
            typeof(StreamIdGenerator).Assembly,
            "StreamIdSnippets.cs",
            "SET_STREAM_ID_COMMAND_CASE"
        );
        var caseCode = setCommandSnippet
            .Replace("__COMMAND_TYPE__", info.CommandType)
            .Replace("__PROPERTY_NAME__", info.PropertyName)
            .Replace("__INDEX__", commandIndex.ToString(CultureInfo.InvariantCulture));
        setStreamIdCode.AppendLine(caseCode);
        hasSetterCases = true;
      }
      commandIndex++;
    }

    template = TemplateUtilities.ReplaceRegion(template, "SET_STREAM_ID_DISPATCH",
        hasSetterCases ? setStreamIdCode.ToString().TrimEnd() : "");

    // Generate [ModuleInitializer] registration code
    // Only register if this assembly has extractors (events or commands with [StreamId])
    var hasExtractors = !eventsWithStreamId.IsEmpty || !commandsWithStreamId.IsEmpty;
    if (hasExtractors) {
      // Register with priority 100 (contracts/types that define messages are tried first)
      const string registrationCode = "global::Whizbang.Core.Registry.StreamIdExtractorRegistry.Register(new GeneratedStreamIdExtractor(), priority: 100);";
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

  /// <summary>
  /// Checks if a property type is a Guid (for SetStreamId generation).
  /// </summary>
  private static bool _isGuidProperty(string propertyTypeName) {
    var normalized = propertyTypeName.Replace("global::", "").Replace("System.", "");
    return normalized is "Guid" or "System.Guid";
  }

  /// <summary>
  /// Checks if a property is init-only or read-only (no settable set accessor).
  /// </summary>
  private static bool _isInitOnlyOrReadOnly(IPropertySymbol property) {
    return property.SetMethod?.IsInitOnly != false;
  }

  /// <summary>
  /// Extracts [GenerateStreamId] info from a property or its owning class.
  /// Checks property first, then falls back to class-level attribute.
  /// </summary>
  private static (bool HasGenerate, bool OnlyIfEmpty) _extractGenerateStreamIdInfo(
      IPropertySymbol property,
      INamedTypeSymbol typeSymbol) {
    // Check for [GenerateStreamId] on the property itself
    var propertyAttr = property.GetAttributes().FirstOrDefault(a =>
        a.AttributeClass is not null &&
        TypeNameHelper.GetFullyQualifiedName(a.AttributeClass) == StandardInterfaceNames.GENERATE_STREAM_ID_ATTRIBUTE);

    if (propertyAttr is not null) {
      var onlyIfEmpty = _extractOnlyIfEmptyFromAttribute(propertyAttr);
      return (true, onlyIfEmpty);
    }

    // Check for [GenerateStreamId] on the class/record itself (for inherited [StreamId] scenarios)
    return _extractGenerateStreamIdFromClass(typeSymbol);
  }

  /// <summary>
  /// Checks for [GenerateStreamId] on the class/record declaration.
  /// </summary>
  private static (bool HasGenerate, bool OnlyIfEmpty) _extractGenerateStreamIdFromClass(INamedTypeSymbol typeSymbol) {
    var classAttr = typeSymbol.GetAttributes().FirstOrDefault(a =>
        a.AttributeClass is not null &&
        TypeNameHelper.GetFullyQualifiedName(a.AttributeClass) == StandardInterfaceNames.GENERATE_STREAM_ID_ATTRIBUTE);

    if (classAttr is not null) {
      var onlyIfEmpty = _extractOnlyIfEmptyFromAttribute(classAttr);
      return (true, onlyIfEmpty);
    }

    return (false, false);
  }

  /// <summary>
  /// Extracts OnlyIfEmpty named argument from a single AttributeData.
  /// </summary>
  private static bool _extractOnlyIfEmptyFromAttribute(AttributeData attr) {
    foreach (var namedArg in attr.NamedArguments) {
      if (namedArg.Key == "OnlyIfEmpty" && namedArg.Value.Value is bool boolValue) {
        return boolValue;
      }
    }
    return false;
  }

  /// <summary>
  /// Extracts OnlyIfEmpty from a collection of attributes.
  /// </summary>
  private static bool _extractOnlyIfEmptyFromAttributes(
      System.Collections.Immutable.ImmutableArray<AttributeData> attributes) {
    var attr = attributes.FirstOrDefault(a =>
        a.AttributeClass is not null &&
        TypeNameHelper.GetFullyQualifiedName(a.AttributeClass) == StandardInterfaceNames.GENERATE_STREAM_ID_ATTRIBUTE);

    return attr is not null && _extractOnlyIfEmptyFromAttribute(attr);
  }
}

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
  private const string PLACEHOLDER_GLOBAL = "global::";
  private const string SNIPPET_FILE = "StreamIdSnippets.cs";
  private const string PLACEHOLDER_EVENT_TYPE = "__EVENT_TYPE__";
  private const string PLACEHOLDER_INDEX = "__INDEX__";
  private const string PLACEHOLDER_PROPERTY_NAME = "__PROPERTY_NAME__";
  private const string PLACEHOLDER_COMMAND_TYPE = "__COMMAND_TYPE__";
  private const string TYPE_STRING = "string";

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
    return _extractStreamIdFromConstructorParameters(typeSymbol);
  }

  /// <summary>
  /// Searches constructor parameters for the [StreamId] attribute and extracts StreamIdInfo.
  /// Used for record types where [StreamId] is applied to constructor parameters.
  /// </summary>
  private static StreamIdInfo? _extractStreamIdFromConstructorParameters(INamedTypeSymbol typeSymbol) {
    foreach (var ctor in typeSymbol.Constructors) {
      foreach (var parameter in ctor.Parameters) {
        if (!_hasStreamIdAttributeOnParameter(parameter)) {
          continue;
        }

        // Find corresponding property (records create properties from constructor parameters)
        var property = typeSymbol.GetMembers().OfType<IPropertySymbol>()
            .FirstOrDefault(p => p.Name.Equals(parameter.Name, System.StringComparison.OrdinalIgnoreCase));

        if (property is null) {
          continue;
        }

        var (hasGenerate, onlyIfEmpty) = _resolveGenerateStreamIdForParameter(parameter, typeSymbol);

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

    return null;
  }

  /// <summary>
  /// Checks if a constructor parameter has the [StreamId] attribute.
  /// </summary>
  private static bool _hasStreamIdAttributeOnParameter(IParameterSymbol parameter) {
    return parameter.GetAttributes().Any(a =>
        a.AttributeClass is not null &&
        TypeNameHelper.GetFullyQualifiedName(a.AttributeClass) == StandardInterfaceNames.STREAM_ID_ATTRIBUTE);
  }

  /// <summary>
  /// Resolves [GenerateStreamId] for a constructor parameter, checking both the parameter and class.
  /// </summary>
  private static (bool HasGenerate, bool OnlyIfEmpty) _resolveGenerateStreamIdForParameter(
      IParameterSymbol parameter,
      INamedTypeSymbol typeSymbol) {
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

    return (hasGenerate, onlyIfEmpty);
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
    return _extractCommandStreamIdFromConstructorParameters(typeSymbol);
  }

  /// <summary>
  /// Searches constructor parameters for the [StreamId] attribute and extracts CommandStreamIdInfo.
  /// Used for record command types where [StreamId] is applied to constructor parameters.
  /// </summary>
  private static CommandStreamIdInfo? _extractCommandStreamIdFromConstructorParameters(INamedTypeSymbol typeSymbol) {
    foreach (var ctor in typeSymbol.Constructors) {
      foreach (var parameter in ctor.Parameters) {
        if (!_hasStreamIdAttributeOnParameter(parameter)) {
          continue;
        }

        // Find corresponding property (records create properties from constructor parameters)
        var property = typeSymbol.GetMembers().OfType<IPropertySymbol>()
            .FirstOrDefault(p => p.Name.Equals(parameter.Name, System.StringComparison.OrdinalIgnoreCase));

        if (property is null) {
          continue;
        }

        var (hasGenerate, onlyIfEmpty) = _resolveGenerateStreamIdForParameter(parameter, typeSymbol);

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

    _reportStreamIdDiagnostics(context, eventsWithStreamId, eventsWithoutStreamId);

    // Load and prepare template
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(StreamIdGenerator).Assembly,
        "StreamIdExtractorsTemplate.cs"
    );
    template = TemplateUtilities.ReplaceHeaderRegion(typeof(StreamIdGenerator).Assembly, template);
    template = TemplateUtilities.ReplaceRegion(template, "NAMESPACE", $"namespace {namespaceName};");

    // Generate GetGenerationPolicy dispatch cases (events AND commands)
    template = _generateGenerationPolicyRegion(template, eventsWithStreamId, commandsWithStreamId);

    // Generate event dispatch and extractors
    template = _generateEventRegions(template, eventsWithStreamId);

    // Generate command dispatch and extractors
    template = _generateCommandRegions(template, context, commandsWithStreamId);

    // Replace other regions with empty (perspective DTOs - not yet implemented)
    template = TemplateUtilities.ReplaceRegion(template, "TRY_RESOLVE_OTHER_DISPATCH", "");
    template = TemplateUtilities.ReplaceRegion(template, "OTHER_EXTRACTORS", "");

    // Generate SetStreamId dispatch cases
    template = _generateSetStreamIdRegion(template, eventsWithStreamId, commandsWithStreamId);

    // Generate [ModuleInitializer] registration code
    template = _generateModuleInitializerRegion(template, eventsWithStreamId, commandsWithStreamId);

    context.AddSource("StreamIdExtractors.g.cs", template);
  }

  /// <summary>
  /// Reports diagnostics for events with and without stream keys.
  /// </summary>
  private static void _reportStreamIdDiagnostics(
      SourceProductionContext context,
      ImmutableArray<StreamIdInfo> eventsWithStreamId,
      ImmutableArray<EventWithoutStreamIdInfo> eventsWithoutStreamId) {

    foreach (var info in eventsWithStreamId) {
      var simpleName = info.EventType.Split('.')[^1].Replace(PLACEHOLDER_GLOBAL, "");
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.StreamIdDiscovered,
          Location.None,
          simpleName,
          info.PropertyName
      ));
    }

    foreach (var info in eventsWithoutStreamId) {
      var simpleName = info.EventType.Split('.')[^1].Replace(PLACEHOLDER_GLOBAL, "");
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.MissingStreamIdAttribute,
          info.Location,
          simpleName
      ));
    }
  }

  /// <summary>
  /// Generates the GENERATION_POLICY_DISPATCH region for events and commands with [GenerateStreamId].
  /// </summary>
  private static string _generateGenerationPolicyRegion(
      string template,
      ImmutableArray<StreamIdInfo> eventsWithStreamId,
      ImmutableArray<CommandStreamIdInfo> commandsWithStreamId) {

    var eventsWithGenerate = eventsWithStreamId.Where(e => e.HasGenerate).ToImmutableArray();
    var commandsWithGenerate = commandsWithStreamId.Where(c => c.HasGenerate).ToImmutableArray();

    if (eventsWithGenerate.IsEmpty && commandsWithGenerate.IsEmpty) {
      return TemplateUtilities.ReplaceRegion(template, "GENERATION_POLICY_DISPATCH", "");
    }

    var generationPolicySnippet = TemplateUtilities.ExtractSnippet(
        typeof(StreamIdGenerator).Assembly,
        SNIPPET_FILE,
        "GENERATION_POLICY_CASE"
    );

    var generationPolicyCode = new StringBuilder();
    generationPolicyCode.AppendLine("// Type-based dispatch for generation policy");

    foreach (var info in eventsWithGenerate) {
      var caseCode = generationPolicySnippet
          .Replace(PLACEHOLDER_EVENT_TYPE, info.EventType)
          .Replace("__SHOULD_GENERATE__", "true")
          .Replace("__ONLY_IF_EMPTY__", info.OnlyIfEmpty ? "true" : "false");
      generationPolicyCode.AppendLine(caseCode);
    }

    foreach (var info in commandsWithGenerate) {
      var caseCode = generationPolicySnippet
          .Replace(PLACEHOLDER_EVENT_TYPE, info.CommandType)
          .Replace("__SHOULD_GENERATE__", "true")
          .Replace("__ONLY_IF_EMPTY__", info.OnlyIfEmpty ? "true" : "false");
      generationPolicyCode.AppendLine(caseCode);
    }

    return TemplateUtilities.ReplaceRegion(template, "GENERATION_POLICY_DISPATCH", generationPolicyCode.ToString().TrimEnd());
  }

  /// <summary>
  /// Generates event dispatch, extractor, and try-extract regions.
  /// </summary>
  private static string _generateEventRegions(
      string template,
      ImmutableArray<StreamIdInfo> eventsWithStreamId) {

    if (eventsWithStreamId.IsEmpty) {
      template = TemplateUtilities.ReplaceRegion(template, "RESOLVE_EVENT_DISPATCH", "");
      template = TemplateUtilities.ReplaceRegion(template, "TRY_RESOLVE_EVENT_DISPATCH", "");
      template = TemplateUtilities.ReplaceRegion(template, "EVENT_EXTRACTORS", "");
      template = TemplateUtilities.ReplaceRegion(template, "TRY_EXTRACT_METHODS", "");
      return template;
    }

    template = _generateEventDispatchCases(template, eventsWithStreamId);
    template = _generateEventTryDispatchCases(template, eventsWithStreamId);
    template = _generateEventExtractors(template, eventsWithStreamId);
    template = _generateEventTryExtractors(template, eventsWithStreamId);
    return template;
  }

  /// <summary>
  /// Generates RESOLVE_EVENT_DISPATCH region with type-based dispatch cases.
  /// </summary>
  private static string _generateEventDispatchCases(string template, ImmutableArray<StreamIdInfo> eventsWithStreamId) {
    var dispatchSnippet = TemplateUtilities.ExtractSnippet(
        typeof(StreamIdGenerator).Assembly, SNIPPET_FILE, "DISPATCH_CASE");

    var dispatchCode = new StringBuilder();
    dispatchCode.AppendLine("// Type-based dispatch to correct extractor");
    for (int i = 0; i < eventsWithStreamId.Length; i++) {
      var caseCode = dispatchSnippet
          .Replace(PLACEHOLDER_EVENT_TYPE, eventsWithStreamId[i].EventType)
          .Replace(PLACEHOLDER_INDEX, i.ToString(CultureInfo.InvariantCulture));
      dispatchCode.AppendLine(caseCode);
    }
    return TemplateUtilities.ReplaceRegion(template, "RESOLVE_EVENT_DISPATCH", dispatchCode.ToString().TrimEnd());
  }

  /// <summary>
  /// Generates TRY_RESOLVE_EVENT_DISPATCH region with Guid? dispatch cases.
  /// </summary>
  private static string _generateEventTryDispatchCases(string template, ImmutableArray<StreamIdInfo> eventsWithStreamId) {
    var tryDispatchSnippet = TemplateUtilities.ExtractSnippet(
        typeof(StreamIdGenerator).Assembly, SNIPPET_FILE, "TRY_DISPATCH_CASE");

    var tryDispatchCode = new StringBuilder();
    tryDispatchCode.AppendLine("// Type-based dispatch returning Guid?");
    for (int i = 0; i < eventsWithStreamId.Length; i++) {
      var caseCode = tryDispatchSnippet
          .Replace(PLACEHOLDER_EVENT_TYPE, eventsWithStreamId[i].EventType)
          .Replace(PLACEHOLDER_INDEX, i.ToString(CultureInfo.InvariantCulture));
      tryDispatchCode.AppendLine(caseCode);
    }
    return TemplateUtilities.ReplaceRegion(template, "TRY_RESOLVE_EVENT_DISPATCH", tryDispatchCode.ToString().TrimEnd());
  }

  /// <summary>
  /// Generates EVENT_EXTRACTORS region with per-event extractor methods.
  /// </summary>
  private static string _generateEventExtractors(string template, ImmutableArray<StreamIdInfo> eventsWithStreamId) {
    var extractorsCode = new StringBuilder();
    for (int i = 0; i < eventsWithStreamId.Length; i++) {
      var info = eventsWithStreamId[i];
      var simpleName = info.EventType.Split('.')[^1].Replace(PLACEHOLDER_GLOBAL, "");

      var isNullable = info.PropertyType.EndsWith("?", StringComparison.Ordinal) ||
                      info.PropertyType.Contains(TYPE_STRING) ||
                      info.PropertyType.Contains("String");

      var extractorSnippet = TemplateUtilities.ExtractSnippet(
          typeof(StreamIdGenerator).Assembly, SNIPPET_FILE,
          isNullable ? "EXTRACTOR_NULLABLE" : "EXTRACTOR_NON_NULLABLE");

      var extractorCode = extractorSnippet
          .Replace(PLACEHOLDER_EVENT_TYPE, info.EventType)
          .Replace("__EVENT_NAME__", simpleName)
          .Replace(PLACEHOLDER_PROPERTY_NAME, info.PropertyName);

      if (i > 0) {
        extractorsCode.AppendLine();
      }
      extractorsCode.Append(extractorCode);
    }
    return TemplateUtilities.ReplaceRegion(template, "EVENT_EXTRACTORS", extractorsCode.ToString().TrimEnd());
  }

  /// <summary>
  /// Generates TRY_EXTRACT_METHODS region with per-event TryExtractAsGuid methods.
  /// </summary>
  private static string _generateEventTryExtractors(string template, ImmutableArray<StreamIdInfo> eventsWithStreamId) {
    var tryExtractorsCode = new StringBuilder();
    for (int i = 0; i < eventsWithStreamId.Length; i++) {
      var info = eventsWithStreamId[i];
      var simpleName = info.EventType.Split('.')[^1].Replace(PLACEHOLDER_GLOBAL, "");

      var tryExtractorSnippetName = _getTryExtractorSnippetName(info.PropertyType, info.IsPropertyValueType);
      var tryExtractorSnippet = TemplateUtilities.ExtractSnippet(
          typeof(StreamIdGenerator).Assembly, SNIPPET_FILE, tryExtractorSnippetName);

      var tryExtractorCode = tryExtractorSnippet
          .Replace(PLACEHOLDER_EVENT_TYPE, info.EventType)
          .Replace("__EVENT_NAME__", simpleName)
          .Replace(PLACEHOLDER_PROPERTY_NAME, info.PropertyName);

      if (i > 0) {
        tryExtractorsCode.AppendLine();
      }
      tryExtractorsCode.Append(tryExtractorCode);
    }
    return TemplateUtilities.ReplaceRegion(template, "TRY_EXTRACT_METHODS", tryExtractorsCode.ToString().TrimEnd());
  }

  /// <summary>
  /// Generates command dispatch, extractor, and try-extract regions.
  /// </summary>
  private static string _generateCommandRegions(
      string template,
      SourceProductionContext context,
      ImmutableArray<CommandStreamIdInfo> commandsWithStreamId) {

    if (commandsWithStreamId.IsEmpty) {
      template = TemplateUtilities.ReplaceRegion(template, "RESOLVE_COMMAND_DISPATCH", "");
      template = TemplateUtilities.ReplaceRegion(template, "TRY_RESOLVE_COMMAND_DISPATCH", "");
      template = TemplateUtilities.ReplaceRegion(template, "COMMAND_EXTRACTORS", "");
      return template;
    }

    // Report diagnostics for commands with stream IDs
    foreach (var info in commandsWithStreamId) {
      var simpleName = info.CommandType.Split('.')[^1].Replace(PLACEHOLDER_GLOBAL, "");
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.CommandStreamIdDiscovered,
          Location.None,
          simpleName,
          info.PropertyName
      ));
    }

    template = _generateCommandDispatchCases(template, commandsWithStreamId);
    template = _generateCommandTryDispatchCases(template, commandsWithStreamId);
    template = _generateCommandExtractorsAndTryExtractors(template, commandsWithStreamId);
    return template;
  }

  /// <summary>
  /// Generates RESOLVE_COMMAND_DISPATCH region.
  /// </summary>
  private static string _generateCommandDispatchCases(string template, ImmutableArray<CommandStreamIdInfo> commandsWithStreamId) {
    var commandDispatchSnippet = TemplateUtilities.ExtractSnippet(
        typeof(StreamIdGenerator).Assembly, SNIPPET_FILE, "COMMAND_DISPATCH_CASE");

    var commandDispatchCode = new StringBuilder();
    commandDispatchCode.AppendLine("// Type-based dispatch to correct command extractor");
    for (int i = 0; i < commandsWithStreamId.Length; i++) {
      var caseCode = commandDispatchSnippet
          .Replace(PLACEHOLDER_COMMAND_TYPE, commandsWithStreamId[i].CommandType)
          .Replace(PLACEHOLDER_INDEX, i.ToString(CultureInfo.InvariantCulture));
      commandDispatchCode.AppendLine(caseCode);
    }
    return TemplateUtilities.ReplaceRegion(template, "RESOLVE_COMMAND_DISPATCH", commandDispatchCode.ToString().TrimEnd());
  }

  /// <summary>
  /// Generates TRY_RESOLVE_COMMAND_DISPATCH region.
  /// </summary>
  private static string _generateCommandTryDispatchCases(string template, ImmutableArray<CommandStreamIdInfo> commandsWithStreamId) {
    var commandTryDispatchSnippet = TemplateUtilities.ExtractSnippet(
        typeof(StreamIdGenerator).Assembly, SNIPPET_FILE, "COMMAND_TRY_DISPATCH_CASE");

    var commandTryDispatchCode = new StringBuilder();
    commandTryDispatchCode.AppendLine("// Type-based dispatch returning Guid? for commands");
    for (int i = 0; i < commandsWithStreamId.Length; i++) {
      var caseCode = commandTryDispatchSnippet
          .Replace(PLACEHOLDER_COMMAND_TYPE, commandsWithStreamId[i].CommandType)
          .Replace(PLACEHOLDER_INDEX, i.ToString(CultureInfo.InvariantCulture));
      commandTryDispatchCode.AppendLine(caseCode);
    }
    return TemplateUtilities.ReplaceRegion(template, "TRY_RESOLVE_COMMAND_DISPATCH", commandTryDispatchCode.ToString().TrimEnd());
  }

  /// <summary>
  /// Generates COMMAND_EXTRACTORS region (both extract and try-extract methods).
  /// </summary>
  private static string _generateCommandExtractorsAndTryExtractors(string template, ImmutableArray<CommandStreamIdInfo> commandsWithStreamId) {
    var commandExtractorsCode = new StringBuilder();

    // Generate command extractor methods
    for (int i = 0; i < commandsWithStreamId.Length; i++) {
      var info = commandsWithStreamId[i];
      var simpleName = info.CommandType.Split('.')[^1].Replace(PLACEHOLDER_GLOBAL, "");

      var isNullable = info.PropertyType.EndsWith("?", StringComparison.Ordinal) ||
                      info.PropertyType.Contains(TYPE_STRING) ||
                      info.PropertyType.Contains("String");

      var extractorSnippet = TemplateUtilities.ExtractSnippet(
          typeof(StreamIdGenerator).Assembly, SNIPPET_FILE,
          isNullable ? "COMMAND_EXTRACTOR_NULLABLE" : "COMMAND_EXTRACTOR_NON_NULLABLE");

      var extractorCode = extractorSnippet
          .Replace(PLACEHOLDER_COMMAND_TYPE, info.CommandType)
          .Replace("__COMMAND_NAME__", simpleName)
          .Replace(PLACEHOLDER_PROPERTY_NAME, info.PropertyName);

      if (i > 0) {
        commandExtractorsCode.AppendLine();
      }
      commandExtractorsCode.Append(extractorCode);
    }

    // Generate TryExtractAsGuid methods for commands
    for (int i = 0; i < commandsWithStreamId.Length; i++) {
      var info = commandsWithStreamId[i];
      var simpleName = info.CommandType.Split('.')[^1].Replace(PLACEHOLDER_GLOBAL, "");

      var tryExtractorSnippetName = _getCommandTryExtractorSnippetName(info.PropertyType, info.IsPropertyValueType);
      var tryExtractorSnippet = TemplateUtilities.ExtractSnippet(
          typeof(StreamIdGenerator).Assembly, SNIPPET_FILE, tryExtractorSnippetName);

      var tryExtractorCode = tryExtractorSnippet
          .Replace(PLACEHOLDER_COMMAND_TYPE, info.CommandType)
          .Replace("__COMMAND_NAME__", simpleName)
          .Replace(PLACEHOLDER_PROPERTY_NAME, info.PropertyName);

      commandExtractorsCode.AppendLine();
      commandExtractorsCode.Append(tryExtractorCode);
    }

    return TemplateUtilities.ReplaceRegion(template, "COMMAND_EXTRACTORS", commandExtractorsCode.ToString().TrimEnd());
  }

  /// <summary>
  /// Generates SET_STREAM_ID_DISPATCH region for events and commands with mutable Guid [StreamId] properties.
  /// </summary>
  private static string _generateSetStreamIdRegion(
      string template,
      ImmutableArray<StreamIdInfo> eventsWithStreamId,
      ImmutableArray<CommandStreamIdInfo> commandsWithStreamId) {

    var setStreamIdCode = new StringBuilder();
    var hasSetterCases = false;

    var eventIndex = 0;
    foreach (var info in eventsWithStreamId) {
      if (_isGuidProperty(info.PropertyType) && !info.IsPropertyInitOnly) {
        var setEventSnippet = TemplateUtilities.ExtractSnippet(
            typeof(StreamIdGenerator).Assembly, SNIPPET_FILE, "SET_STREAM_ID_EVENT_CASE");
        var caseCode = setEventSnippet
            .Replace(PLACEHOLDER_EVENT_TYPE, info.EventType)
            .Replace(PLACEHOLDER_PROPERTY_NAME, info.PropertyName)
            .Replace(PLACEHOLDER_INDEX, eventIndex.ToString(CultureInfo.InvariantCulture));
        setStreamIdCode.AppendLine(caseCode);
        hasSetterCases = true;
      }
      eventIndex++;
    }

    var commandIndex = 0;
    foreach (var info in commandsWithStreamId) {
      if (_isGuidProperty(info.PropertyType) && !info.IsPropertyInitOnly) {
        var setCommandSnippet = TemplateUtilities.ExtractSnippet(
            typeof(StreamIdGenerator).Assembly, SNIPPET_FILE, "SET_STREAM_ID_COMMAND_CASE");
        var caseCode = setCommandSnippet
            .Replace(PLACEHOLDER_COMMAND_TYPE, info.CommandType)
            .Replace(PLACEHOLDER_PROPERTY_NAME, info.PropertyName)
            .Replace(PLACEHOLDER_INDEX, commandIndex.ToString(CultureInfo.InvariantCulture));
        setStreamIdCode.AppendLine(caseCode);
        hasSetterCases = true;
      }
      commandIndex++;
    }

    return TemplateUtilities.ReplaceRegion(template, "SET_STREAM_ID_DISPATCH",
        hasSetterCases ? setStreamIdCode.ToString().TrimEnd() : "");
  }

  /// <summary>
  /// Generates MODULE_INITIALIZER_REGISTRATION region.
  /// </summary>
  private static string _generateModuleInitializerRegion(
      string template,
      ImmutableArray<StreamIdInfo> eventsWithStreamId,
      ImmutableArray<CommandStreamIdInfo> commandsWithStreamId) {

    var hasExtractors = !eventsWithStreamId.IsEmpty || !commandsWithStreamId.IsEmpty;
    if (hasExtractors) {
      const string registrationCode = "global::Whizbang.Core.Registry.StreamIdExtractorRegistry.Register(new GeneratedStreamIdExtractor(), priority: 100);";
      return TemplateUtilities.ReplaceRegion(template, "MODULE_INITIALIZER_REGISTRATION", registrationCode);
    }

    return TemplateUtilities.ReplaceRegion(template, "MODULE_INITIALIZER_REGISTRATION", "// No extractors in this assembly - skipping registration");
  }

  /// <summary>
  /// Determines which TRY_EXTRACTOR snippet to use based on property type.
  /// </summary>
  /// <param name="propertyTypeName">The fully qualified property type name</param>
  /// <param name="isValueType">Whether the property type is a value type (struct)</param>
  private static string _getTryExtractorSnippetName(string propertyTypeName, bool isValueType) {
    // Normalize the type name for comparison
    var normalizedType = propertyTypeName
        .Replace(PLACEHOLDER_GLOBAL, "")
        .Replace("System.", "");

    // Check for Guid types
    if (normalizedType is "Guid" or "System.Guid") {
      return "TRY_EXTRACTOR_GUID";
    }

    if (normalizedType is "Guid?" or "System.Guid?" or "Nullable<Guid>" or "Nullable<System.Guid>") {
      return "TRY_EXTRACTOR_NULLABLE_GUID";
    }

    // Check for string types (reference type, can be null)
    if (normalizedType.Contains(TYPE_STRING, StringComparison.OrdinalIgnoreCase)) {
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
        .Replace(PLACEHOLDER_GLOBAL, "")
        .Replace("System.", "");

    // Check for Guid types
    if (normalizedType is "Guid" or "System.Guid") {
      return "COMMAND_TRY_EXTRACTOR_GUID";
    }

    if (normalizedType is "Guid?" or "System.Guid?" or "Nullable<Guid>" or "Nullable<System.Guid>") {
      return "COMMAND_TRY_EXTRACTOR_NULLABLE_GUID";
    }

    // Check for string types (reference type, can be null)
    if (normalizedType.Contains(TYPE_STRING, StringComparison.OrdinalIgnoreCase)) {
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
    var normalized = propertyTypeName.Replace(PLACEHOLDER_GLOBAL, "").Replace("System.", "");
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

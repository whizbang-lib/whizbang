// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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

  /// <summary>
  /// Syntax predicate: admits a class or record with a base list, so it can implement the interfaces the transform looks for.
  /// </summary>
  /// <remarks>Public so the build-cost guard can run it over ordinary code (#1650).</remarks>
  /// <tests>tests/Whizbang.Generators.Component.Tests/BuildCost/SyntaxCandidateGuardTests.cs</tests>
  public static bool IsClassOrRecordWithBaseListCandidate(SyntaxNode node) =>
      node is RecordDeclarationSyntax { BaseList.Types.Count: > 0 } || node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 };

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover IEvent types with [StreamId] attribute
    var eventsWithStreamId = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => IsClassOrRecordWithBaseListCandidate(node),
        transform: static (ctx, ct) => _extractStreamIdInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Discover IEvent types WITHOUT [StreamId] for diagnostics
    var eventsWithoutStreamId = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => IsClassOrRecordWithBaseListCandidate(node),
        transform: static (ctx, ct) => _findEventWithoutStreamId(ctx, ct)
    ).Where(static info => info is not null);

    // Discover ICommand types with [StreamId] attribute
    var commandsWithStreamId = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => IsClassOrRecordWithBaseListCandidate(node),
        transform: static (ctx, ct) => _extractCommandStreamIdInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Discover ICompositeEvent types with [StreamId]. A composite is IMessage-not-IEvent, but carries
    // [StreamId] (via CompositeEventBase) — the stream its fanned-out inner events inherit. The extractor
    // must resolve it via the object-typed resolver so producer-side fan-out routes children correctly.
    var compositesWithStreamId = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => IsClassOrRecordWithBaseListCandidate(node),
        transform: static (ctx, ct) => _extractCompositeStreamIdInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Generate extractor methods from collected events, commands, and composites
    // Combine compilation with discovered types to get assembly name for namespace
    var compilationAndData = context.CompilationProvider.Select(static (compilation, _) => compilation.AssemblyName)
        .Combine(eventsWithStreamId.Collect())
        .Combine(eventsWithoutStreamId.Collect())
        .Combine(commandsWithStreamId.Collect())
        .Combine(compositesWithStreamId.Collect());

    context.RegisterSourceOutput(
        compilationAndData,
        static (ctx, data) => {
          var compilationAssemblyName = data.Left.Left.Left.Left;
          var withStreamId = data.Left.Left.Left.Right;
          var withoutStreamId = data.Left.Left.Right;
          var commandsWithId = data.Left.Right;
          var compositesWithId = data.Right;
          _generateStreamIdExtractors(ctx, compilationAssemblyName, withStreamId!, withoutStreamId!, commandsWithId!, compositesWithId!);
        }
    );
  }

  // S3776: Complexity from sequential validation checks with early returns — already well-structured
#pragma warning disable S3776
  private static StreamIdInfo? _extractStreamIdInfo(
      GeneratorSyntaxContext context,
      CancellationToken ct) {
#pragma warning restore S3776

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

    // The [StreamId] the analyzer agrees on: an override, then the nearest property, then a constructor parameter
    return _selectStreamId(typeSymbol);
  }

  /// <summary>
  /// Discovers a concrete <c>ICompositeEvent</c> with a [StreamId] property. A composite is
  /// IMessage-not-IEvent (so never event-stored), but carries [StreamId] (via CompositeEventBase) — the
  /// stream its fanned-out inner events inherit. We register an extractor on the object-typed resolver so
  /// producer-side fan-out routes children to the composite's declared stream, not its MessageId.
  /// Abstract types (e.g. CompositeEventBase itself) are skipped — only concrete composites are registered.
  /// </summary>
  // S3776: Sequential validation checks with early returns — mirrors _extractStreamIdInfo's structure.
#pragma warning disable S3776
  private static StreamIdInfo? _extractCompositeStreamIdInfo(
      GeneratorSyntaxContext context,
      CancellationToken ct) {
#pragma warning restore S3776

    var typeSymbol = RoslynGuards.GetTypeSymbolFromNode(context.Node, context.SemanticModel, ct);

    // Skip non-public types (can't access from generated code)
    if (typeSymbol.DeclaredAccessibility != Accessibility.Public) {
      return null;
    }

    // Skip abstract types — CompositeEventBase carries the [StreamId] but is never instantiated; only
    // concrete composites are dispatched and fanned out.
    if (typeSymbol.IsAbstract) {
      return null;
    }

    // Must be an ICompositeEvent (IMessage-not-IEvent). IEvent/ICommand composites would already be
    // handled by their own discovery paths, so exclude those to avoid duplicate extractor overloads.
    if (!TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_COMPOSITE_EVENT)) {
      return null;
    }
    if (TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_EVENT)
        || TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_COMMAND)) {
      return null;
    }

    // The inherited CompositeEventBase.StreamId, unless the composite overrides it or declares a nearer one.
    return _selectStreamId(typeSymbol);
  }

  /// <summary>
  /// The [StreamId] a message type's extractor reads, chosen by <see cref="StreamIdSelection"/> so the generator and the
  /// WHIZ006 analyzer agree (#1512, #1833). [GenerateStreamId] is read from the parameter when the stream id came from
  /// one, else from the property, and from the class in either case.
  /// </summary>
  private static (IPropertySymbol Property, bool HasGenerate, bool OnlyIfEmpty)? _resolveStreamId(INamedTypeSymbol typeSymbol) {
    var choice = StreamIdSelection.Select(typeSymbol);
    if (choice is null) {
      return null;
    }
    var (hasGenerate, onlyIfEmpty) = choice.Parameter is null
        ? _extractGenerateStreamIdInfo(choice.Property, typeSymbol)
        : _resolveGenerateStreamIdForParameter(choice.Parameter, typeSymbol);
    return (choice.Property, hasGenerate, onlyIfEmpty);
  }

  /// <summary>
  /// Builds the extractor info for an event or composite from its chosen [StreamId].
  /// </summary>
  private static StreamIdInfo? _selectStreamId(INamedTypeSymbol typeSymbol) {
    if (_resolveStreamId(typeSymbol) is not { } resolved) {
      return null;
    }
    var (property, hasGenerate, onlyIfEmpty) = resolved;
    return new StreamIdInfo(
        EventType: TypeNameHelper.GetFullyQualifiedName(typeSymbol),
        PropertyName: property.Name,
        PropertyType: TypeNameHelper.GetFullyQualifiedName(property.Type),
        IsPropertyValueType: property.Type.IsValueType,
        HasGenerate: hasGenerate,
        OnlyIfEmpty: onlyIfEmpty,
        IsPropertyInitOnly: _isInitOnlyOrReadOnly(property),
        PropertyLocation: LocationUtilities.FirstOrNone(property),
        InheritanceDepth: _inheritanceDepth(typeSymbol)
    );
  }

  /// <summary>
  /// Resolves [GenerateStreamId] for a constructor parameter, checking both the parameter and class.
  /// </summary>
  private static (bool HasGenerate, bool OnlyIfEmpty) _resolveGenerateStreamIdForParameter(
      IParameterSymbol parameter,
      INamedTypeSymbol typeSymbol) {
    // The attribute found here is the one OnlyIfEmpty is read from, so it is kept rather than looked
    // up a second time by a helper that would then have to allow for not finding it.
    var generateOnParam = parameter.GetAttributes().FirstOrDefault(a =>
        TypeNameUtilities.IsFullyQualifiedNamed(a.AttributeClass, StandardInterfaceNames.GENERATE_STREAM_ID_ATTRIBUTE));

    var (hasGenerateOnClass, classOnlyIfEmpty) = _extractGenerateStreamIdFromClass(typeSymbol);
    var hasGenerate = generateOnParam is not null || hasGenerateOnClass;

    var onlyIfEmpty = false;
    if (generateOnParam is not null) {
      onlyIfEmpty = _extractOnlyIfEmptyFromAttribute(generateOnParam);
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

    // Has a stream id the extractor can read (an override anywhere in the chain, an inherited property, or a record parameter)
    if (StreamIdSelection.Select(typeSymbol) is not null) {
      return null;
    }

    var hasStreamIdOnParameter = typeSymbol.Constructors.Any(ctor =>
        ctor.Parameters.Any(param =>
            param.GetAttributes().Any(a =>
                TypeNameUtilities.IsFullyQualifiedNamed(a.AttributeClass, StandardInterfaceNames.STREAM_ID_ATTRIBUTE))));

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

    // The [StreamId] the analyzer agrees on: an override, then the nearest property, then a constructor parameter
    if (_resolveStreamId(typeSymbol) is not { } resolved) {
      return null;
    }
    var (property, hasGenerate, onlyIfEmpty) = resolved;
    return new CommandStreamIdInfo(
        CommandType: TypeNameHelper.GetFullyQualifiedName(typeSymbol),
        PropertyName: property.Name,
        PropertyType: TypeNameHelper.GetFullyQualifiedName(property.Type),
        IsPropertyValueType: property.Type.IsValueType,
        HasGenerate: hasGenerate,
        OnlyIfEmpty: onlyIfEmpty,
        IsPropertyInitOnly: _isInitOnlyOrReadOnly(property),
        PropertyLocation: LocationUtilities.FirstOrNone(property),
        InheritanceDepth: _inheritanceDepth(typeSymbol)
    );
  }

  /// <summary>
  /// Generates stream key extractors with assembly-specific namespace to avoid conflicts.
  /// </summary>
  // S3776: Code generation method — complexity from template assembly + multiple type categories (events, commands, generation policies)
#pragma warning disable S3776
  private static void _generateStreamIdExtractors(
      SourceProductionContext context,
      string? compilationAssemblyName,
      ImmutableArray<StreamIdInfo> eventsWithStreamId,
      ImmutableArray<EventWithoutStreamIdInfo> eventsWithoutStreamId,
      ImmutableArray<CommandStreamIdInfo> commandsWithStreamId,
      ImmutableArray<StreamIdInfo> compositesWithStreamId) {
#pragma warning restore S3776

    // Determine namespace from assembly name
    var assemblyName = compilationAssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    // Most-derived first in every region that dispatches on type, so a base type's check never shadows a derived
    // type's own [StreamId] (#1893).
    var events = _mostDerivedFirst(eventsWithStreamId);
    var commands = _mostDerivedFirst(commandsWithStreamId);
    var composites = _mostDerivedFirst(compositesWithStreamId);

    _reportStreamIdDiagnostics(context, events, eventsWithoutStreamId);
    _reportGenerateStreamIdOnInitOnly(context, events, commands);

    // Load and prepare template
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(StreamIdGenerator).Assembly,
        "StreamIdExtractorsTemplate.cs"
    );
    template = TemplateUtilities.ReplaceHeaderRegion(typeof(StreamIdGenerator).Assembly, template);
    template = TemplateUtilities.ReplaceRegion(template, "NAMESPACE", $"namespace {namespaceName};");

    // Generate GetGenerationPolicy dispatch cases (events AND commands)
    template = _generateGenerationPolicyRegion(template, events, commands);

    // Generate event dispatch and extractors
    template = _generateEventRegions(template, events);

    // Generate command dispatch and extractors
    template = _generateCommandRegions(template, context, commands);

    // Generate the object-typed (OTHER) regions for composites — IMessage-not-IEvent types carrying [StreamId].
    template = _generateOtherRegions(template, composites);

    // Generate SetStreamId dispatch cases
    template = _generateSetStreamIdRegion(template, events, commands);

    // Generate [ModuleInitializer] registration code
    template = _generateModuleInitializerRegion(template, events, commands, composites);

    context.AddSource("StreamIdExtractors.g.cs", template);
  }

  /// <summary>
  /// How many base types <paramref name="typeSymbol"/> has: zero for a type with none, one for a direct subclass of
  /// <see cref="object"/>, and one more for each further level.
  /// </summary>
  private static int _inheritanceDepth(INamedTypeSymbol typeSymbol) {
    var depth = 0;
    for (var baseType = typeSymbol.BaseType; baseType is not null; baseType = baseType.BaseType) {
      depth++;
    }
    return depth;
  }

  /// <summary>
  /// Orders events or composites for type dispatch: deepest inheritance first, then by type name, so a base type's
  /// <c>is</c> check comes after every type derived from it and the generated file does not depend on discovery order.
  /// </summary>
  private static ImmutableArray<StreamIdInfo> _mostDerivedFirst(ImmutableArray<StreamIdInfo> infos) =>
    infos.OrderByDescending(i => i.InheritanceDepth).ThenBy(i => i.EventType, StringComparer.Ordinal).ToImmutableArray();

  /// <summary>
  /// Orders commands for type dispatch: deepest inheritance first, then by type name (see the event overload).
  /// </summary>
  private static ImmutableArray<CommandStreamIdInfo> _mostDerivedFirst(ImmutableArray<CommandStreamIdInfo> infos) =>
    infos.OrderByDescending(i => i.InheritanceDepth).ThenBy(i => i.CommandType, StringComparer.Ordinal).ToImmutableArray();

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
  /// Reports WHIZ013 for any event/command that has [GenerateStreamId] on an init-only [StreamId] — the
  /// generated SetStreamId writer can't target init-only, so the minted id would silently never be written.
  /// </summary>
  private static void _reportGenerateStreamIdOnInitOnly(
      SourceProductionContext context,
      ImmutableArray<StreamIdInfo> eventsWithStreamId,
      ImmutableArray<CommandStreamIdInfo> commandsWithStreamId) {

    foreach (var info in eventsWithStreamId) {
      if (info.HasGenerate && info.IsPropertyInitOnly && _isGuidProperty(info.PropertyType)) {
        var simpleName = info.EventType.Split('.')[^1].Replace(PLACEHOLDER_GLOBAL, "");
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.GenerateStreamIdOnInitOnly, info.PropertyLocation, simpleName, info.PropertyName));
      }
    }

    foreach (var info in commandsWithStreamId) {
      if (info.HasGenerate && info.IsPropertyInitOnly && _isGuidProperty(info.PropertyType)) {
        var simpleName = info.CommandType.Split('.')[^1].Replace(PLACEHOLDER_GLOBAL, "");
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.GenerateStreamIdOnInitOnly, info.PropertyLocation, simpleName, info.PropertyName));
      }
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
  /// Generates the object-typed OTHER regions for composites: the TRY_RESOLVE_OTHER_DISPATCH cases (matched
  /// in <c>TryResolveAsGuid(object?)</c>) and their TryExtractAsGuid methods (OTHER_EXTRACTORS). A composite
  /// is IMessage-not-IEvent, so it never matches the IEvent/ICommand fast paths — its [StreamId] is resolved
  /// here so producer-side fan-out routes the inner events onto the composite's declared stream.
  /// </summary>
  private static string _generateOtherRegions(
      string template,
      ImmutableArray<StreamIdInfo> compositesWithStreamId) {

    if (compositesWithStreamId.IsEmpty) {
      template = TemplateUtilities.ReplaceRegion(template, "TRY_RESOLVE_OTHER_DISPATCH", "");
      template = TemplateUtilities.ReplaceRegion(template, "OTHER_EXTRACTORS", "");
      return template;
    }

    var otherDispatchSnippet = TemplateUtilities.ExtractSnippet(
        typeof(StreamIdGenerator).Assembly, SNIPPET_FILE, "OTHER_TRY_DISPATCH_CASE");

    var dispatchCode = new StringBuilder();
    dispatchCode.AppendLine("// Type-based dispatch for composites (IMessage-not-IEvent carrying [StreamId])");
    for (int i = 0; i < compositesWithStreamId.Length; i++) {
      var caseCode = otherDispatchSnippet
          .Replace(PLACEHOLDER_EVENT_TYPE, compositesWithStreamId[i].EventType)
          .Replace(PLACEHOLDER_INDEX, i.ToString(CultureInfo.InvariantCulture));
      dispatchCode.AppendLine(caseCode);
    }
    template = TemplateUtilities.ReplaceRegion(template, "TRY_RESOLVE_OTHER_DISPATCH", dispatchCode.ToString().TrimEnd());

    var extractorsCode = new StringBuilder();
    for (int i = 0; i < compositesWithStreamId.Length; i++) {
      var info = compositesWithStreamId[i];
      var simpleName = info.EventType.Split('.')[^1].Replace(PLACEHOLDER_GLOBAL, "");

      var tryExtractorSnippetName = _getTryExtractorSnippetName(info.PropertyType, info.IsPropertyValueType);
      var tryExtractorSnippet = TemplateUtilities.ExtractSnippet(
          typeof(StreamIdGenerator).Assembly, SNIPPET_FILE, tryExtractorSnippetName);

      var tryExtractorCode = tryExtractorSnippet
          .Replace(PLACEHOLDER_EVENT_TYPE, info.EventType)
          .Replace("__EVENT_NAME__", simpleName)
          .Replace(PLACEHOLDER_PROPERTY_NAME, info.PropertyName);

      if (i > 0) {
        extractorsCode.AppendLine();
      }
      extractorsCode.Append(tryExtractorCode);
    }
    return TemplateUtilities.ReplaceRegion(template, "OTHER_EXTRACTORS", extractorsCode.ToString().TrimEnd());
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
      ImmutableArray<CommandStreamIdInfo> commandsWithStreamId,
      ImmutableArray<StreamIdInfo> compositesWithStreamId) {

    var hasExtractors = !eventsWithStreamId.IsEmpty || !commandsWithStreamId.IsEmpty || !compositesWithStreamId.IsEmpty;
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
        TypeNameUtilities.IsFullyQualifiedNamed(a.AttributeClass, StandardInterfaceNames.GENERATE_STREAM_ID_ATTRIBUTE));

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
        TypeNameUtilities.IsFullyQualifiedNamed(a.AttributeClass, StandardInterfaceNames.GENERATE_STREAM_ID_ATTRIBUTE));

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
}

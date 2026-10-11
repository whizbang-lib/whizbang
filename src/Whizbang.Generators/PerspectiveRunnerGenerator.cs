// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Models;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Generates IPerspectiveRunner implementations for perspectives that implement IPerspectiveFor&lt;TModel, TEvent&gt;.
/// Runners handle unit-of-work event replay with UUID7 ordering, configurable batching, and checkpoint management.
/// Supports both single-stream (IPerspectiveFor) and multi-stream (IGlobalPerspectiveFor) perspectives.
/// </summary>
[Generator]
public class PerspectiveRunnerGenerator : IIncrementalGenerator {
  /// <summary>
  /// Separates a member's name from its default expression inside one <c>MemberDefaults</c> entry. A control
  /// character, because the expression can be a string literal containing any printable separator; C# escaping
  /// renders a real control character as <c>\uXXXX</c>, so it can never appear in the literal text itself.
  /// </summary>
  private const char MEMBER_DEFAULT_SEPARATOR = '\u0001';

  private const string PERSPECTIVE_FOR_INTERFACE_NAME = "Whizbang.Core.Perspectives.IPerspectiveFor";
  private const string PERSPECTIVE_WITH_ACTIONS_FOR_INTERFACE_NAME = "Whizbang.Core.Perspectives.IPerspectiveWithActionsFor";
  private const string GLOBAL_PERSPECTIVE_FOR_INTERFACE_NAME = "Whizbang.Core.Perspectives.IGlobalPerspectiveFor";
  private const string PERSPECTIVE_SCOPE_FOR_INTERFACE_NAME = "Whizbang.Core.Perspectives.IPerspectiveScopeFor";

  /// <summary>
  /// Spells a bool the way C# source does. ToString() would emit "True"/"False", which does not
  /// compile in the generated file.
  /// </summary>
  private static string _csharpBool(bool value) => value ? "true" : "false";
  private const string MUST_EXIST_ATTRIBUTE_NAME = "Whizbang.Core.Perspectives.MustExistAttribute";

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Extract perspective info or warning for models missing StreamId
    var perspectiveResults = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractPerspectiveOrWarning(ctx, ct)
    ).Where(static result => result is not null);

    // Combine with compilation to get assembly name
    var compilationAndResults = context.CompilationProvider.Combine(perspectiveResults.Collect());

    context.RegisterSourceOutput(
        compilationAndResults,
        static (ctx, data) => {
          var compilation = data.Left;
          var results = data.Right;

          // Report warnings for perspectives missing StreamId on model
          foreach (var result in results) {
            if (result!.Warning is { } warning) {
              ctx.ReportDiagnostic(Diagnostic.Create(
                  DiagnosticDescriptors.PerspectiveModelMissingStreamId,
                  Location.Create(
                      warning.FilePath,
                      default,
                      new Microsoft.CodeAnalysis.Text.LinePositionSpan(
                          new Microsoft.CodeAnalysis.Text.LinePosition(warning.Line, warning.Column),
                          new Microsoft.CodeAnalysis.Text.LinePosition(warning.Line, warning.Column))),
                  warning.PerspectiveName,
                  warning.ModelName
              ));
            }
          }

          // Generate runners for valid perspectives only
          var validPerspectives = results
              .Where(r => r!.Info is not null)
              .Select(r => r!.Info!)
              .ToImmutableArray();

          // A Split class whose init-only promoted field can only be stripped through a copy, which it cannot make.
          foreach (var (Model, Problem) in validPerspectives
              .Where(p => p.ModelCopy?.Problem is not null)
              .Select(p => (Model: p.InterfaceTypeArguments[0].Replace("global::", ""), p.ModelCopy!.Problem))
              .Distinct()) {
            ctx.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.SplitClassModelCannotBeCopied, Location.None, Model, Problem));
          }

          _generatePerspectiveRunners(ctx, compilation, validPerspectives);
        }
    );
  }

  /// <summary>
  /// Extracts perspective information or warning from a class declaration.
  /// Returns null if the class doesn't implement IPerspectiveFor or IGlobalPerspectiveFor.
  /// Returns a warning if the model is missing [StreamId] attribute.
  /// </summary>
  private static PerspectiveOrWarning? _extractPerspectiveOrWarning(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {

    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    var classSymbol = RoslynGuards.GetClassSymbolOrThrow(classDeclaration, semanticModel, cancellationToken);

    // Skip abstract classes
    if (classSymbol.IsAbstract) {
      return null;
    }

    // Extract perspective interfaces
    var singleStreamInterfaces = _extractSingleStreamInterfaces(classSymbol);
    var withActionsInterfaces = _extractWithActionsInterfaces(classSymbol);
    var globalInterfaces = _extractGlobalInterfaces(classSymbol);

    // Combine single-stream and with-actions interfaces (both use same model/event pattern)
    var combinedSingleStreamInterfaces = singleStreamInterfaces.Concat(withActionsInterfaces).ToList();

    // Extract model type (from combined single-stream interfaces) and the event types from all of
    // them. A class carrying none of the three interface shapes has no model type and no events, so
    // "not a perspective at all", "no model type" and "no event types" are one and the same test —
    // written once rather than as three exits, only the first of which any input reaches.
    var modelType = _extractModelType(combinedSingleStreamInterfaces, globalInterfaces);
    var (eventTypes, eventTypeSymbols) = _extractEventTypesFromInterfaces(combinedSingleStreamInterfaces, globalInterfaces);

    if (modelType is null || eventTypes.Count == 0) {
      return null;
    }

    var modelTypeName = TypeNameUtilities.FullyQualified(modelType);

    // A perspective is ephemeral-tainted (viral) if it applies ANY ephemeral event. Ephemeral perspectives
    // snapshot on their own aggressive, single-slot cadence so a fresh rewind floor exists within the grace
    // window before consumed bodies are reaped. Resolved at compile time — zero reflection.
    var isEphemeral = eventTypeSymbols.Any(static s => EphemeralResolver.IsEphemeral(s));

    // TtlRow perspective-row expiry (E2-4d): resolved virally like isEphemeral. If ANY applied [Ephemeral]
    // event chose TransientStorage.TtlRow, the perspective's rows expire; the TTL is the LONGEST of its
    // TtlRow events' TtlSeconds (keep the row until its longest-lived contributing data expires). -1 = the
    // rows never expire (no TtlRow event). The generator emits a [ModuleInitializer] to register this TTL.
    var ttlRowSeconds = _resolveEphemeralTtlRowSeconds(eventTypeSymbols);

    // Perspective row retention: an explicit [RowTtl] on the perspective class OUTRANKS the
    // ephemeral-derived TTL (the override ladder — the read model's own declaration is more
    // specific than what its events imply). This is also what opens row expiry to Sourced
    // perspectives, which have no [Ephemeral] events to derive from: their rows can age out
    // while the log stays durable, because the event-time expiry anchor keeps rebuilds
    // deterministic and a reaped row re-folds from the log on wake.
    var explicitTtl = _resolveExplicitRowTtlSeconds(classSymbol);
    if (explicitTtl >= 0) {
      ttlRowSeconds = explicitTtl;
    }

    // Row cap: bounds how MANY rows a perspective keeps per scope, the companion to [RowTtl]'s bound on
    // how OLD they get. Resolved only from the perspective's own declaration — unlike the TTL there is no
    // ephemeral-derived source, because cardinality is a read-model property with nothing in the event
    // stream to derive it from. PerScope partitions per (tenant, user), PerTenant across the tenant; the
    // scope key is what the SQL sweep's ROW_NUMBER() partitions by, so it travels with the number.
    var (rowCapPerScope, rowCapScopeKey) = _resolveRowCap(classSymbol);

    // Stream groups: each [StreamGroup] declaration is one membership with its own dials, encoded
    // compactly (key|announce|follow|bridge;...) so the record stays equatable for incremental
    // caching. Repeatable — a perspective in two groups is the case the dials exist for.
    var streamGroupSpec = _buildStreamGroupSpec(classSymbol);

    // A1-6b: a perspective marked [FullHistory] needs every event and cannot resume from a carry-forward /
    // closing event — the A1 close guard refuses a discard-close of any stream it consumes. Resolved at compile
    // time; the generator registers the perspective's name so the runtime guard can key off it.
    var isFullHistory = classSymbol.GetAttributes().Any(
        static a => TypeNameUtilities.SimpleNameOrNull(a.AttributeClass) is "FullHistoryAttribute" or "FullHistory");

    // Find StreamId property on model
    var streamKeyPropertyName = _findModelStreamIdProperty(modelType);
    if (streamKeyPropertyName is null) {
      // Return warning instead of silently skipping (WHIZ033)
      return _missingStreamIdWarning(classDeclaration, classSymbol, modelType);
    }

    // Only a named type declares properties (an array, a type parameter or dynamic has none of its own), so a model
    // with a [StreamId] property is always one. Everything below reads the model's members and attributes, and takes
    // it as the named type that guarantee makes it rather than re-testing a shape it cannot have.
    var namedModel = (INamedTypeSymbol)modelType;

    // Build the CreateEmptyModel object initializer at generation time so the
    // runner constructs the model directly (no Activator, no reflection).
    var emptyModelInitializer = _buildEmptyModelInitializer(modelType, streamKeyPropertyName);

    // Extract StreamId properties from event types
    var eventStreamIds = _extractEventStreamIdsFromTypes(eventTypes, eventTypeSymbols);

    // Build type arguments and message type names
    var typeArguments = new[] { modelTypeName }.Concat(eventTypes).ToArray();
    var messageTypeNames = _buildMessageTypeNames(eventTypeSymbols);

    // Extract event types with [MustExist] attribute
    var mustExistEventTypes = _extractMustExistEventTypes(classSymbol, eventTypes);

    // Extract return types for each Apply method
    var eventReturnTypes = _extractEventReturnTypes(classSymbol, eventTypes);
    var unconditionalPurges = _extractUnconditionalPurgeEventTypes(classSymbol, eventTypes);

    // Compute nested-aware simple name for unique hintNames
    var simpleName = TypeNameUtilities.GetSimpleName(classSymbol);

    // Compute CLR format name for database storage (uses + for nested types)
    var clrTypeName = TypeNameUtilities.BuildClrTypeName(classSymbol);

    // Discover physical fields (including vector fields) on model properties
    var physicalFields = _discoverPhysicalFields(namedModel);

    // Discover what each member reads as when the document has no key for it (#1044)
    var memberDefaults = _discoverMemberDefaults(namedModel);

    // Extract storage mode from [PerspectiveStorage] attribute on model type
    var storageMode = _extractStorageMode(namedModel);

    // Check if model is a record type (supports 'with {}' expressions for immutable copies)
    var isModelRecord = namedModel.IsRecord;

    // Issue #1002: a Split class with an init-only promoted field is stripped and loaded through a copy, because a
    // class can set an init-only property only while an instance is being created.
    var modelCopy = storageMode == 2 && !isModelRecord && physicalFields.Any(f => f.IsInitOnly)
        ? ModelCopy.For(namedModel, semanticModel.Compilation.Assembly)
        : null;

    // Check if perspective implements IPerspectiveScopeFor<TModel> for IScopeEvent handling
    var hasScopeInterface = _hasScopeForInterface(classSymbol);

    // Extract [InheritScope].OnCreate flags from the model. Default 63 = ScopeFields.All
    // when the attribute is absent (preserves legacy copy-everything behavior). Adding
    // [InheritScope] opts the perspective into the safer per-field default.
    var inheritScopeOnCreate = _extractInheritScopeOnCreate(modelType);

    return new PerspectiveOrWarning(
        Info: new PerspectiveInfo(
            ClassName: TypeNameUtilities.FullyQualified(classSymbol),
            SimpleName: simpleName,
            ClrTypeName: clrTypeName,
            InterfaceTypeArguments: typeArguments,
            EventTypes: [.. eventTypes],
            MessageTypeNames: messageTypeNames,
            EmptyModelInitializer: emptyModelInitializer,
            EventStreamIds: eventStreamIds.Count > 0 ? [.. eventStreamIds] : null,
            MustExistEventTypes: mustExistEventTypes.Length > 0 ? mustExistEventTypes : null,
            EventReturnTypes: eventReturnTypes.Length > 0 ? eventReturnTypes : null,
            PhysicalFields: physicalFields.Length > 0 ? physicalFields : null,
            StorageMode: storageMode,
            IsModelRecord: isModelRecord,
            HasScopeInterface: hasScopeInterface,
            InheritScopeOnCreate: inheritScopeOnCreate,
            IsEphemeral: isEphemeral,
            TtlRowSeconds: ttlRowSeconds,
            IsFullHistory: isFullHistory
,
            RowCapPerScope: rowCapPerScope,
            RowCapScopeKey: rowCapScopeKey,
            StreamGroupSpec: streamGroupSpec,
            ModelCopy: modelCopy,
            MemberDefaults: memberDefaults.Length > 0 ? memberDefaults : null,
            UnconditionalPurgeEventTypes: unconditionalPurges.Length > 0 ? unconditionalPurges : null),
        Warning: null
    );
  }

  /// <summary>
  /// Builds the WHIZ033 warning result for a perspective whose model has no [StreamId] property.
  /// </summary>
  private static PerspectiveOrWarning _missingStreamIdWarning(
      ClassDeclarationSyntax classDeclaration,
      INamedTypeSymbol classSymbol,
      ITypeSymbol modelType) {
    var location = classDeclaration.GetLocation();
    var lineSpan = location.GetLineSpan();
    return new PerspectiveOrWarning(
        Info: null,
        Warning: new PerspectiveMissingStreamIdWarning(
            PerspectiveName: classSymbol.Name,
            ModelName: modelType.Name,
            FilePath: lineSpan.Path,
            Line: lineSpan.StartLinePosition.Line,
            Column: lineSpan.StartLinePosition.Character
        )
    );
  }

  /// <summary>
  /// TtlRow perspective-row expiry (E2-4d), resolved virally: if ANY applied [Ephemeral] event chose
  /// TransientStorage.TtlRow, the perspective's rows expire, with the LONGEST of those events' TtlSeconds.
  /// Returns -1 when no applied event is TtlRow (the rows never expire).
  /// </summary>
  private static int _resolveEphemeralTtlRowSeconds(List<ITypeSymbol> eventTypeSymbols) {
    var ttlRowSeconds = -1;
    foreach (var s in eventTypeSymbols) {
      if (s is INamedTypeSymbol named && EphemeralResolver.Resolve(named) is { Storage: "TtlRow" }) {
        var ttl = EphemeralResolver.ResolveTtlSeconds(named);
        if (ttl > ttlRowSeconds) {
          ttlRowSeconds = ttl;
        }
      }
    }
    return ttlRowSeconds;
  }

  /// <summary>
  /// Reads an explicit [RowTtl] on the perspective class, in seconds. Seconds outranks Days when both
  /// are declared. Returns a negative value when the attribute is absent or declares neither.
  /// </summary>
  private static int _resolveExplicitRowTtlSeconds(INamedTypeSymbol classSymbol) {
    var rowTtlAttribute = classSymbol.GetAttributes().FirstOrDefault(
        static a => TypeNameUtilities.SimpleNameOrNull(a.AttributeClass) is "RowTtlAttribute" or "RowTtl");
    if (rowTtlAttribute is null) {
      return -1;
    }
    var explicitDays = -1;
    var explicitSeconds = -1;
    foreach (var namedArg in rowTtlAttribute.NamedArguments) {
      if (namedArg.Key == "Days" && namedArg.Value.Value is int d) {
        explicitDays = d;
      } else if (namedArg.Key == "Seconds" && namedArg.Value.Value is int sec) {
        explicitSeconds = sec;
      }
    }
    if (explicitSeconds >= 0) {
      return explicitSeconds;
    }
    if (explicitDays >= 0) {
      return explicitDays * 86400;
    }
    return -1;
  }

  /// <summary>
  /// Reads [RowCap] on the perspective class. PerScope is the more specific partition, so it outranks
  /// PerTenant when both are declared. Returns (-1, null) when no cap is declared.
  /// </summary>
  private static (int PerScope, string? ScopeKey) _resolveRowCap(INamedTypeSymbol classSymbol) {
    var rowCapAttribute = classSymbol.GetAttributes().FirstOrDefault(
        static a => TypeNameUtilities.SimpleNameOrNull(a.AttributeClass) is "RowCapAttribute" or "RowCap");
    if (rowCapAttribute is null) {
      return (-1, null);
    }
    var perScope = -1;
    var perTenant = -1;
    foreach (var namedArg in rowCapAttribute.NamedArguments) {
      if (namedArg.Key == "PerScope" && namedArg.Value.Value is int ps) {
        perScope = ps;
      } else if (namedArg.Key == "PerTenant" && namedArg.Value.Value is int pt) {
        perTenant = pt;
      }
    }
    if (perScope >= 0) {
      return (perScope, "u");
    }
    if (perTenant >= 0) {
      return (perTenant, "t");
    }
    return (-1, null);
  }

  /// <summary>
  /// Encodes every [StreamGroup] declaration on the perspective class as key|announce|follow|bridge,
  /// joined with ';'. Returns null when the class declares no valid membership.
  /// </summary>
  private static string? _buildStreamGroupSpec(INamedTypeSymbol classSymbol) {
    var streamGroupParts = new List<string>();
    foreach (var groupAttribute in classSymbol.GetAttributes().Where(
        static a => TypeNameUtilities.SimpleNameOrNull(a.AttributeClass) is "StreamGroupAttribute" or "StreamGroup")) {
      var encoded = _encodeStreamGroupMembership(groupAttribute);
      if (encoded is not null) {
        streamGroupParts.Add(encoded);
      }
    }
    return streamGroupParts.Count > 0 ? string.Join(";", streamGroupParts) : null;
  }

  /// <summary>
  /// Encodes one [StreamGroup] membership as key|announce|follow|bridge (1/0 flags).
  /// Returns null when the group key is missing or empty.
  /// </summary>
  private static string? _encodeStreamGroupMembership(AttributeData groupAttribute) {
    if (groupAttribute.ConstructorArguments.Length == 0 ||
        groupAttribute.ConstructorArguments[0].Value is not string groupKey ||
        string.IsNullOrEmpty(groupKey)) {
      return null;
    }
    var announce = true;
    var follow = true;
    var bridge = false;
    foreach (var namedArg in groupAttribute.NamedArguments) {
      if (namedArg.Key == "Announce" && namedArg.Value.Value is bool announceValue) {
        announce = announceValue;
      } else if (namedArg.Key == "Follow" && namedArg.Value.Value is bool followValue) {
        follow = followValue;
      } else if (namedArg.Key == "Bridge" && namedArg.Value.Value is bool bridgeValue) {
        bridge = bridgeValue;
      }
    }
    return $"{groupKey}|{(announce ? 1 : 0)}|{(follow ? 1 : 0)}|{(bridge ? 1 : 0)}";
  }

  /// <summary>
  /// Reads <c>[InheritScope].OnCreate</c> from the supplied model type. Returns the
  /// flag value as an int. Falls back to <c>(int)ScopeFields.All</c> (63) when the
  /// attribute is absent — that preserves the legacy "copy every scope field on INSERT"
  /// behavior so existing perspectives keep working without modification.
  /// </summary>
  private static int _extractInheritScopeOnCreate(ITypeSymbol modelType) {
    const int defaultAll = 63;
    foreach (var attr in modelType.GetAttributes()) {
      var name = TypeNameUtilities.SimpleNameOrNull(attr.AttributeClass);
      if (name != "InheritScopeAttribute" && name != "InheritScope") {
        continue;
      }
      foreach (var named in attr.NamedArguments) {
        if (named.Key == "OnCreate" && named.Value.Value is { } v) {
          return System.Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
        }
      }
      // Attribute present without OnCreate override → InheritScopeAttribute's own default.
      // That default is ScopeFields.Tenant (1) per the attribute declaration.
      return 1;
    }
    return defaultAll;
  }

  /// <summary>
  /// Extracts the StreamId property name from an event type.
  /// Returns the property name if exactly one [StreamId] is found, null otherwise.
  /// </summary>
  private static string? _extractStreamIdProperty(ITypeSymbol eventTypeSymbol) {
    // Walk the inheritance chain: the [StreamId]-marked property may be declared on a base type
    // (e.g. a consumer's BaseSagaItemEvent), and GetMembers() returns only declared members. Most-derived
    // first so a re-declaration on the concrete type wins over the inherited one.
    for (ITypeSymbol? type = eventTypeSymbol;
        type != null && type.SpecialType != SpecialType.System_Object;
        type = (type as INamedTypeSymbol)?.BaseType) {
      foreach (var member in type.GetMembers()) {
        if (member is IPropertySymbol property) {
          var hasStreamIdAttribute = property.GetAttributes()
              .Any(a => TypeNameUtilities.IsNamed(a.AttributeClass, "Whizbang.Core.StreamIdAttribute"));

          if (hasStreamIdAttribute) {
            return property.Name;
          }
        }
      }
    }

    return null;
  }

  /// <summary>
  /// Generates IPerspectiveRunner implementations for all discovered perspectives with models.
  /// </summary>
  private static void _generatePerspectiveRunners(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<PerspectiveInfo> perspectives) {

    if (perspectives.IsEmpty) {
      return;
    }

    // Generate a runner for each perspective
    foreach (var perspective in perspectives) {
      var runnerSource = _generateRunnerSource(compilation, perspective);
      var runnerName = _getRunnerName(perspective.SimpleName);
      context.AddSource($"{runnerName}.g.cs", runnerSource);

      // Report diagnostic
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.PerspectiveRunnerGenerated,
          Location.None,
          perspective.SimpleName,
          runnerName
      ));
    }
  }

  /// <summary>
  /// Generates the C# source code for a perspective runner.
  /// Uses template-based generation with unit-of-work pattern and AOT-compatible switch statements.
  /// </summary>
  private static string _generateRunnerSource(Compilation compilation, PerspectiveInfo perspective) {
    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    // Load template from embedded resource
    var template = TemplateUtilities.GetEmbeddedTemplate(
        typeof(PerspectiveRunnerGenerator).Assembly,
        "PerspectiveRunnerTemplate.cs"
    );

    var runnerName = _getRunnerName(perspective.SimpleName);
    var perspectiveSimpleName = perspective.SimpleName;

    // Model type is always the first type argument
    var modelTypeName = perspective.InterfaceTypeArguments[0];
    var modelSimpleName = TypeNameUtilities.GetSimpleName(modelTypeName);

    // Generate AOT-compatible switch cases for event application
    var applyCases = _buildApplyCases(perspective, modelSimpleName, perspectiveSimpleName);

    // Generate event types array for polymorphic deserialization
    var eventTypesArray = _buildEventTypesArray(perspective);

    // Generate ExtractStreamId methods (one per event type with StreamId)
    var extractStreamIdMethods = _buildExtractStreamIdMethods(perspective);

    // Generate the ResolveTargetStreamId switch body used by RunRebuildAsync. One arm per
    // [StreamId]-bearing event type returns the event's post-upcast StreamId; the default keeps the
    // event on the physical stream. When the perspective handles no [StreamId] events, the body is
    // just the physical-stream fallback (so re-key never splits — identical to the old rebuild).
    var resolveTargetStreamId = _buildResolveTargetStreamId(perspective);

    // Generate upsert call - either simple UpsertAsync or UpsertWithPhysicalFieldsAsync
    var upsertCode = _generateUpsertCode(perspective);

    // Generate scope event handling code
    var scopeEventCode = _generateScopeEventHandlingCode(perspective);

    // Replace template markers
    var result = template;
    result = TemplateUtilities.ReplaceRegion(result, "NAMESPACE", $"namespace {namespaceName};");
    result = TemplateUtilities.ReplaceHeaderRegion(typeof(PerspectiveRunnerGenerator).Assembly, result);
    result = TemplateUtilities.ReplaceRegion(result, "EVENT_TYPES", eventTypesArray.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "REPLAY_EVENT_TYPES", eventTypesArray.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "REBUILD_EVENT_TYPES", eventTypesArray.ToString());
    // Issue #696: the resurrection-on-wake probe carries the handled event types (static array).
    result = TemplateUtilities.ReplaceRegion(result, "HANDLED_EVENT_TYPES", eventTypesArray.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "EVENT_APPLY_CASES", applyCases.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "EXTRACT_STREAM_ID_METHODS", extractStreamIdMethods.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "RESOLVE_TARGET_STREAM_ID", resolveTargetStreamId.ToString());
    result = TemplateUtilities.ReplaceRegion(result, "UPSERT_CALL", upsertCode);
    result = TemplateUtilities.ReplaceRegion(result, "SCOPE_EVENT_HANDLING", scopeEventCode);
    result = TemplateUtilities.ReplaceRegion(
        result,
        "INHERIT_SCOPE_ON_CREATE",
        $"private const global::Whizbang.Core.Lenses.ScopeFields _inheritScopeOnCreate = (global::Whizbang.Core.Lenses.ScopeFields){perspective.InheritScopeOnCreate};");

    result = TemplateUtilities.ReplaceRegion(result, "SNAPSHOT_SETTINGS", perspective.IsEphemeral
        ? "var snapshotThreshold = _snapshotOptions.Value.EphemeralSnapshotEveryNEvents;\nvar snapshotRetention = _snapshotOptions.Value.EphemeralMaxSnapshotsPerStream;"
        : "var snapshotThreshold = _snapshotOptions.Value.SnapshotEveryNEvents;\nvar snapshotRetention = _snapshotOptions.Value.MaxSnapshotsPerStream;");
    result = TemplateUtilities.ReplaceRegion(result, "UNCONDITIONAL_PURGE",
        perspective.UnconditionalPurgeEventTypes is { Length: > 0 } purgeTypes
          ? $"private static bool IsUnconditionalPurge(global::Whizbang.Core.IEvent @event) => @event is {string.Join(" or ", purgeTypes)};"
          : "private static bool IsUnconditionalPurge(global::Whizbang.Core.IEvent @event) => false;");
    result = TemplateUtilities.ReplaceRegion(result, "IS_EPHEMERAL",
        $"private const bool _isEphemeralPerspective = {_csharpBool(perspective.IsEphemeral)};");
    // E2-4d: a TtlRow perspective registers its row TTL via a [ModuleInitializer] so the upsert stamps
    // expires_at. Non-TtlRow perspectives emit nothing (their rows never expire).
    result = TemplateUtilities.ReplaceRegion(result, "TTL_REGISTRATION", perspective.TtlRowSeconds >= 0
        ? $"[global::System.Runtime.CompilerServices.ModuleInitializer]\n  internal static void _registerRowTtl() =>\n      global::Whizbang.Core.Perspectives.PerspectiveTtlRegistry.Register(typeof({modelTypeName}), {perspective.TtlRowSeconds});"
        : "");
    // The cap registers itself the same turnkey way the TTL does — a [ModuleInitializer], no consumer
    // code, no reflection. Emitting nothing when undeclared keeps "absent" distinct from a cap of zero.
    result = TemplateUtilities.ReplaceRegion(result, "ROW_CAP_REGISTRATION", perspective.RowCapPerScope >= 0
        ? $"[global::System.Runtime.CompilerServices.ModuleInitializer]\n  internal static void _registerRowCap() =>\n      global::Whizbang.Core.Perspectives.PerspectiveRowCapRegistry.Register(typeof({modelTypeName}), {perspective.RowCapPerScope}, \"{perspective.RowCapScopeKey}\");"
        : "");
    // A1-6b: register a [FullHistory] perspective's name (matching its association target_name = its ClrTypeName)
    // so the close guard can refuse a discard-close of any stream it consumes. Empty for resumable perspectives.
    result = TemplateUtilities.ReplaceRegion(result, "FULL_HISTORY_REGISTRATION", perspective.IsFullHistory
        ? $"[global::System.Runtime.CompilerServices.ModuleInitializer]\n  internal static void _registerFullHistory() =>\n      global::Whizbang.Core.Perspectives.FullHistoryPerspectiveRegistry.Register(\"{perspective.ClrTypeName}\");"
        : "");
    // Stream groups register turnkey like the TTL and the cap — one [ModuleInitializer] per
    // membership, decoded from the compact spec, so the maintenance cascade can compute the
    // eviction closure without reflection.
    result = TemplateUtilities.ReplaceRegion(result, "STREAM_GROUP_REGISTRATION",
        _buildStreamGroupRegistrations(perspective.StreamGroupSpec, modelTypeName));
    // Physical fields register turnkey too: the collective apply path reads the column names and storage mode
    // to send a setter or a condition on a physical property to its column (no reflection at run time).
    result = TemplateUtilities.ReplaceRegion(result, "PHYSICAL_FIELD_REGISTRATION",
        _buildPhysicalFieldRegistration(perspective, modelTypeName));
    // Member defaults register the same turnkey way: the collective predicate compiler reads them so a document
    // with no key for a member is filtered as the value a rebuild sees, rather than as SQL NULL (#1044).
    result = TemplateUtilities.ReplaceRegion(result, "MEMBER_DEFAULT_REGISTRATION",
        // Discovery records a model with no declared defaults as null, never as an empty list.
        perspective.MemberDefaults is not null ? _buildMemberDefaultRegistration(perspective, modelTypeName) : "");
    // Issue #977: a Split model's promoted fields live only in their columns, so the store has to read them
    // back into the model the next event is applied to. The copy is generated here, where the fields are known.
    result = TemplateUtilities.ReplaceRegion(result, "SPLIT_PHYSICAL_FIELD_REGISTRATION",
        _buildSplitPhysicalFieldRegistration(perspective, modelTypeName));
    // Issue #983: a model the write strips in place has to be snapshotted before the write.
    result = TemplateUtilities.ReplaceRegion(result, "SNAPSHOT_BEFORE_WRITE",
        _buildSnapshotBeforeWrite(perspective, modelTypeName));
    result = result.Replace("__RUNNER_CLASS_NAME__", runnerName);
    result = result.Replace("__PERSPECTIVE_CLASS_NAME__", perspective.ClassName);
    result = result.Replace("__MODEL_TYPE_NAME__", modelTypeName);
    result = result.Replace("__EMPTY_MODEL_INITIALIZER__", perspective.EmptyModelInitializer);
    result = result.Replace("__PERSPECTIVE_SIMPLE_NAME__", perspectiveSimpleName);

    return result;
  }

  /// <summary>
  /// Builds the AOT-compatible switch cases that dispatch each event type to its Apply method,
  /// wrapping each return shape into a (model, action) tuple.
  /// </summary>
  private static StringBuilder _buildApplyCases(PerspectiveInfo perspective, string modelSimpleName, string perspectiveSimpleName) {
    var mustExistEvents = perspective.MustExistEventTypes ?? [];
    var eventReturnTypes = perspective.EventReturnTypes ?? [];
    var returnTypeLookup = eventReturnTypes.ToDictionary(x => x.EventTypeName, x => x.ReturnType);
    var applyCases = new StringBuilder();
    foreach (var eventType in perspective.EventTypes) {
      var isMustExist = mustExistEvents.Contains(eventType);
      var eventSimpleName = TypeNameUtilities.GetSimpleName(eventType);

      // Get return type for this event, default to Model
      var returnType = returnTypeLookup.TryGetValue(eventType, out var rt) ? rt : ApplyReturnType.Model;

      applyCases.AppendLine($"        case {eventType} typedEvent:");
      if (isMustExist) {
        applyCases.AppendLine("          if (currentModel == null)");
        applyCases.AppendLine("            throw new global::System.InvalidOperationException(");
        applyCases.AppendLine($"              \"{modelSimpleName} must exist when applying {eventSimpleName} in {perspectiveSimpleName}\");");
      }

      // Generate case code based on return type
      // Note: currentModel is nullable in template, but user's Apply methods may expect non-nullable
      // For Model/NullableModel returns, we use null-forgiving operator since these signatures
      // typically have a non-nullable first parameter
      switch (returnType) {
        case ApplyReturnType.Model:
          // Standard return: TModel - wrap with None action
          // Use ! because user's Apply(TModel current, TEvent) expects non-nullable
          applyCases.AppendLine("          return (perspective.Apply(currentModel!, typedEvent), global::Whizbang.Core.Perspectives.ModelAction.None);");
          break;

        case ApplyReturnType.NullableModel:
          // Nullable return: TModel? - null means no change, wrap with None action
          // Pass nullable since Apply(TModel? current, TEvent) accepts nullable
          applyCases.AppendLine("          return (perspective.Apply(currentModel, typedEvent), global::Whizbang.Core.Perspectives.ModelAction.None);");
          break;

        case ApplyReturnType.Action:
          // Action return: ModelAction - keep current model, return the action
          // Use ! because Apply(TModel current, TEvent) for deletion expects existing model
          applyCases.AppendLine("          return (currentModel, perspective.Apply(currentModel!, typedEvent));");
          break;

        case ApplyReturnType.Tuple:
          // Tuple return: (TModel?, ModelAction) - return as-is
          // Use ! because Apply(TModel current, TEvent) expects existing model
          applyCases.AppendLine("          return perspective.Apply(currentModel!, typedEvent);");
          break;

        case ApplyReturnType.ApplyResult:
          // ApplyResult return: Extract model and action from result
          // Use ! because Apply(TModel current, TEvent) expects existing model
          applyCases.AppendLine($"          var result_{eventSimpleName} = perspective.Apply(currentModel!, typedEvent);");
          applyCases.AppendLine($"          return (result_{eventSimpleName}.Model, result_{eventSimpleName}.Action);");
          break;
      }
      applyCases.AppendLine();
    }
    return applyCases;
  }

  /// <summary>
  /// Builds the typeof(...) list of handled event types used for polymorphic deserialization.
  /// </summary>
  private static StringBuilder _buildEventTypesArray(PerspectiveInfo perspective) {
    var eventTypesArray = new StringBuilder();
    for (int i = 0; i < perspective.EventTypes.Length; i++) {
      eventTypesArray.Append($"      typeof({perspective.EventTypes[i]})");
      if (i < perspective.EventTypes.Length - 1) {
        eventTypesArray.AppendLine(",");
      } else {
        eventTypesArray.AppendLine();
      }
    }
    return eventTypesArray;
  }

  /// <summary>
  /// Builds one ExtractStreamId overload per event type that carries a [StreamId] property.
  /// </summary>
  private static StringBuilder _buildExtractStreamIdMethods(PerspectiveInfo perspective) {
    var extractStreamIdMethods = new StringBuilder();
    if (perspective.EventStreamIds != null) {
      foreach (var eventStreamId in perspective.EventStreamIds) {
        extractStreamIdMethods.AppendLine("  /// <summary>");
        extractStreamIdMethods.AppendLine($"  /// Extracts the stream ID from {TypeNameUtilities.GetSimpleName(eventStreamId.EventTypeName)} event.");
        extractStreamIdMethods.AppendLine("  /// </summary>");
        extractStreamIdMethods.AppendLine($"  private static string ExtractStreamId({eventStreamId.EventTypeName} @event) {{");
        extractStreamIdMethods.AppendLine($"    return @event.{eventStreamId.StreamIdPropertyName}.ToString();");
        extractStreamIdMethods.AppendLine("  }");
        extractStreamIdMethods.AppendLine();
      }
    }
    return extractStreamIdMethods;
  }

  /// <summary>
  /// Builds the ResolveTargetStreamId body used by RunRebuildAsync.
  /// </summary>
  private static StringBuilder _buildResolveTargetStreamId(PerspectiveInfo perspective) {
    var resolveTargetStreamId = new StringBuilder();
    if (perspective.EventStreamIds != null) {
      resolveTargetStreamId.AppendLine("    return @event switch {");
      foreach (var eventStreamId in perspective.EventStreamIds) {
        resolveTargetStreamId.AppendLine($"      {eventStreamId.EventTypeName} e => e.{eventStreamId.StreamIdPropertyName},");
      }
      resolveTargetStreamId.AppendLine("      _ => physicalStreamId,");
      resolveTargetStreamId.AppendLine("    };");
    } else {
      resolveTargetStreamId.AppendLine("    return physicalStreamId;");
    }
    return resolveTargetStreamId;
  }

  /// <summary>
  /// Emits the <c>[ModuleInitializer]</c> that registers a Split model's promoted columns and the code that
  /// copies them into a model loaded from its document. Empty for any other storage mode, whose document
  /// already holds every field.
  /// </summary>
  /// <remarks>
  /// A record is copied with a <c>with</c> expression, so an init-only property is set the same way the
  /// runner strips it before the write; a class is assigned in place. A vector property that is not
  /// nullable takes an empty array for a null column, as the strip does.
  /// </remarks>
  private static string _buildSplitPhysicalFieldRegistration(PerspectiveInfo perspective, string modelTypeName) {
    // A Split model reads every promoted field back from its column. Any other model reads back its jsonb
    // columns: the EF Core model keeps them out of the mapped document, so the column is where they are.
    PhysicalFieldInfoCompact[] promoted = perspective.PhysicalFields ?? [];
    var fields = perspective.StorageMode == 2
      ? promoted
      : [.. promoted.Where(f => PhysicalFieldScalar.IsJsonb(f.ColumnType))];
    if (fields.Length == 0) {
      return "";
    }

    var columns = string.Join(", ", fields.Select(f =>
        $"new global::Whizbang.Core.Perspectives.SplitPhysicalColumn(\"{f.ColumnName}\", {_csharpBool(f.IsVectorField)})"));
    var reads = fields.Select(f => (f.PropertyName, Read: _splitColumnRead(f))).ToArray();
    string hydrate;
    if (perspective.IsModelRecord) {
      hydrate = $"static (model, read) => model with {{ {string.Join(", ", reads.Select(r => $"{r.PropertyName} = {r.Read}"))} }}";
    } else if (perspective.ModelCopy is { Problem: null } copy) {
      hydrate = "static (model, read) => "
          + ModelCopy.Render(modelTypeName, copy, "model", reads.ToDictionary(r => r.PropertyName, r => r.Read, StringComparer.Ordinal));
    } else {
      // An init-only field of a class that cannot be copied is reported (WHIZ808) rather than assigned.
      var assigned = reads.Where(r => !fields.Any(f => f.PropertyName == r.PropertyName && f.IsInitOnly));
      hydrate = $"static (model, read) => {{ {string.Concat(assigned.Select(r => $"model.{r.PropertyName} = {r.Read}; "))}return model; }}";
    }

    return "[global::System.Runtime.CompilerServices.ModuleInitializer]\n" +
        "  internal static void _registerSplitPhysicalFields() =>\n" +
        $"      global::Whizbang.Core.Perspectives.SplitPhysicalFieldRegistry.Register(new global::Whizbang.Core.Perspectives.SplitPhysicalFieldMap<{modelTypeName}>(\n" +
        $"          new[] {{ {columns} }},\n" +
        $"          {hydrate}));";
  }

  /// <summary>
  /// Emits <c>SnapshotBeforeWrite</c>, which the runner calls just before each write that a snapshot follows.
  /// </summary>
  /// <remarks>
  /// The write of a Split model strips its promoted fields so the document never holds them. A record is
  /// stripped as a copy, but a class is stripped in place (it has no <c>with</c>), so the instance the runner
  /// snapshots after the write has lost them, and a rewind that starts from that snapshot writes their
  /// defaults over the columns (issue #983). For that model this serializes the snapshot before the write,
  /// when snapshots are on; for every other model it returns null and the snapshot is taken after the write
  /// as before.
  /// </remarks>
  private static string _buildSnapshotBeforeWrite(PerspectiveInfo perspective, string modelTypeName) {
    var strippedInPlace = perspective.StorageMode == 2
        && perspective.PhysicalFields is { Length: > 0 }
        && !perspective.IsModelRecord
        && perspective.ModelCopy is not { Problem: null };
    return strippedInPlace
        ? "// The write strips this model's promoted fields in place, so its snapshot is taken first (issue #983),\n" +
          "// and only on a run whose snapshot is due (issue #1002).\n" +
          $"private static JsonDocument? SnapshotBeforeWrite({modelTypeName} model) => ToSnapshotJson(model);"
        : "// The write does not strip this model in place, so its snapshot is taken after the write.\n" +
          $"private static JsonDocument? SnapshotBeforeWrite({modelTypeName} model) => null;";
  }

  /// <summary>The expression that reads one promoted column as its property's type.</summary>
  private static string _splitColumnRead(PhysicalFieldInfoCompact field) {
    if (!field.IsVectorField) {
      return $"read.Read<{field.TypeName}>(\"{field.ColumnName}\")";
    }
    var read = $"read.GetVector(\"{field.ColumnName}\")";
    return field.TypeName.EndsWith("?", StringComparison.Ordinal) ? read : $"{read} ?? global::System.Array.Empty<float>()";
  }

  private static string _buildStreamGroupRegistrations(string? streamGroupSpec, string modelTypeName) {
    if (string.IsNullOrEmpty(streamGroupSpec)) {
      return "";
    }
    var registrations = new List<string>();
    var memberships = streamGroupSpec!.Split(';');
    for (var i = 0; i < memberships.Length; i++) {
      var parts = memberships[i].Split('|');
      if (parts.Length != 4) {
        continue;
      }
      registrations.Add(
        $"[global::System.Runtime.CompilerServices.ModuleInitializer]\n  internal static void _registerStreamGroup{i}() =>\n      global::Whizbang.Core.Perspectives.PerspectiveStreamGroupRegistry.Register(typeof({modelTypeName}), \"{parts[0]}\", {_csharpBool(parts[1] == "1")}, {_csharpBool(parts[2] == "1")}, {_csharpBool(parts[3] == "1")});");
    }
    return string.Join("\n\n  ", registrations);
  }

  /// <summary>
  /// Emits the <c>[ModuleInitializer]</c> that registers the model's physical fields in
  /// <c>PerspectivePhysicalFieldRegistry</c>: one call per field with its column name, the model's storage mode
  /// and whether it is a vector. Empty when the model has no physical fields.
  /// </summary>
  private static string _buildPhysicalFieldRegistration(PerspectiveInfo perspective, string modelTypeName) {
    if (perspective.PhysicalFields is not { Length: > 0 } fields) {
      return "";
    }
    var mode = perspective.StorageMode switch {
      1 => "Extracted",
      2 => "Split",
      _ => "JsonOnly",
    };
    var sb = new StringBuilder();
    sb.Append("[global::System.Runtime.CompilerServices.ModuleInitializer]\n  internal static void _registerPhysicalFields() {");
    foreach (var field in fields) {
      sb.Append("\n    global::Whizbang.Core.Perspectives.PerspectivePhysicalFieldRegistry.Register(typeof(")
        .Append(modelTypeName).Append("), \"").Append(field.PropertyName).Append("\", \"").Append(field.ColumnName)
        .Append("\", global::Whizbang.Core.Perspectives.FieldStorageMode.").Append(mode)
        .Append(", isVector: ").Append(_csharpBool(field.IsVectorField));
      // An enumeration's column holds its underlying number unless the author declared the column's type.
      if (field.EnumScalarType is { } scalar && string.IsNullOrWhiteSpace(field.ColumnType)) {
        sb.Append(", scalarType: typeof(global::").Append(scalar).Append(')');
      }
      if (!string.IsNullOrWhiteSpace(field.ColumnType)) {
        sb.Append(", columnType: \"").Append(field.ColumnType!.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
      }
      sb.Append(");");
    }
    sb.Append("\n  }");
    return sb.ToString();
  }

  /// <summary>
  /// Generates the upsert code for the SaveModelAndCheckpointAsync method.
  /// Uses UpsertWithPhysicalFieldsAsync when physical fields exist, UpsertAsync otherwise.
  /// </summary>
  private static string _generateUpsertCode(PerspectiveInfo perspective) {
    var sb = new StringBuilder();

    if (perspective.PhysicalFields == null || perspective.PhysicalFields.Length == 0) {
      _appendSimpleUpsertCode(sb);
    } else {
      _appendPhysicalFieldsUpsertCode(sb, perspective);
    }

    return sb.ToString().TrimEnd('\r', '\n');
  }

  /// <summary>
  /// Emits the no-physical-fields branch: a simple UpsertAsync call with a scope-aware if/else
  /// so zero-scope perspectives can use the scope-less overload.
  /// </summary>
  private static void _appendSimpleUpsertCode(StringBuilder sb) {
    sb.AppendLine("    // Upsert model (insert or update) — metadata.EventId records the last applied event so the");
    sb.AppendLine("    // next run can skip already-applied events (see RunWithEventsAsync idempotency guard), and");
    sb.AppendLine("    // expectedVersion lands the write only on the row version the apply read (issue #928).");
    sb.AppendLine("    if (scope != null) {");
    sb.AppendLine("      await _perspectiveStore.UpsertAsync(");
    sb.AppendLine("          streamId,");
    sb.AppendLine("          model,");
    sb.AppendLine("          scope,");
    sb.AppendLine("          forceUpdateScope,");
    sb.AppendLine("          metadata,");
    sb.AppendLine("          expectedVersion,");
    sb.AppendLine("          cancellationToken");
    sb.AppendLine("      );");
    sb.AppendLine("    } else {");
    sb.AppendLine("      await _perspectiveStore.UpsertAsync(");
    sb.AppendLine("          streamId,");
    sb.AppendLine("          model,");
    sb.AppendLine("          new global::Whizbang.Core.Lenses.PerspectiveScope(),");
    sb.AppendLine("          false,");
    sb.AppendLine("          metadata,");
    sb.AppendLine("          expectedVersion,");
    sb.AppendLine("          cancellationToken");
    sb.AppendLine("      );");
    sb.AppendLine("    }");
  }

  /// <summary>
  /// Emits the physical-fields branch: builds the column-value dictionary, optionally strips
  /// physical fields from the model for split mode, and calls UpsertWithPhysicalFieldsAsync.
  /// </summary>
  private static void _appendPhysicalFieldsUpsertCode(StringBuilder sb, PerspectiveInfo perspective) {
    _appendPhysicalFieldValuesDictionary(sb, perspective);

    if (perspective.StorageMode == 2 && perspective.PhysicalFields!.Length > 0) {
      _appendSplitModePhysicalFieldStripping(sb, perspective);
    }

    sb.AppendLine("    // Upsert model with physical field values — metadata.EventId records the last applied event");
    sb.AppendLine("    // so the next run can skip already-applied events (see RunWithEventsAsync idempotency guard), and");
    sb.AppendLine("    // expectedVersion lands the write only on the row version the apply read (issue #928).");
    sb.AppendLine("    await _perspectiveStore.UpsertWithPhysicalFieldsAsync(");
    sb.AppendLine("        streamId,");
    sb.AppendLine("        model,");
    sb.AppendLine("        physicalFieldValues,");
    sb.AppendLine("        scope,");
    sb.AppendLine("        forceUpdateScope,");
    sb.AppendLine("        metadata,");
    sb.AppendLine("        expectedVersion,");
    sb.AppendLine("        cancellationToken");
    sb.AppendLine("    );");
  }

  /// <summary>
  /// Emits the <c>physicalFieldValues</c> dictionary initialiser. Vector fields are converted
  /// from <c>float[]</c> to <c>Pgvector.Vector</c> at compile time so AOT compatibility is
  /// preserved (no reflection).
  /// </summary>
  private static void _appendPhysicalFieldValuesDictionary(StringBuilder sb, PerspectiveInfo perspective) {
    sb.AppendLine("    // Extract physical field values from model (including vector fields)");
    sb.AppendLine("    // Vector fields are converted from float[] to Pgvector.Vector for EF Core compatibility");
    sb.AppendLine("    var physicalFieldValues = new System.Collections.Generic.Dictionary<string, object?>");
    sb.AppendLine("    {");

    for (int i = 0; i < perspective.PhysicalFields!.Length; i++) {
      var field = perspective.PhysicalFields[i];
      var comma = i < perspective.PhysicalFields.Length - 1 ? "," : "";
      if (field.IsVectorField) {
        sb.AppendLine($"      {{ \"{field.ColumnName}\", model.{field.PropertyName} != null ? new Pgvector.Vector(model.{field.PropertyName}) : null }}{comma}");
      } else {
        sb.AppendLine($"      {{ \"{field.ColumnName}\", model.{field.PropertyName} }}{comma}");
      }
    }

    sb.AppendLine("    };");
    sb.AppendLine();
  }

  /// <summary>
  /// Split mode (StorageMode == 2): strips physical fields from the model before JSONB
  /// serialization so the JSONB payload only contains non-physical fields. Physical field
  /// values are already captured in the <c>physicalFieldValues</c> dictionary for column
  /// storage. Records use <c>with { ... }</c> for an immutable copy; a class with an <c>init</c>-only promoted field is
  /// copied with an object initializer (issue #1002); other classes mutate in place.
  /// Vector fields get <c>System.Array.Empty&lt;float&gt;()</c> because EF Core's
  /// JsonCollectionOfStructsReaderWriter crashes on a null JSON token.
  /// </summary>
  private static void _appendSplitModePhysicalFieldStripping(StringBuilder sb, PerspectiveInfo perspective) {
    sb.AppendLine("    // Split mode: exclude physical fields from JSONB — values stored in physical columns only");
    if (perspective.IsModelRecord) {
      var withProps = string.Join(", ", perspective.PhysicalFields!.Select(f =>
        f.IsVectorField ? $"{f.PropertyName} = System.Array.Empty<float>()" : $"{f.PropertyName} = default!"));
      sb.AppendLine($"    model = model with {{ {withProps} }};");
    } else if (perspective.ModelCopy is { Problem: null } copy) {
      // An init-only field cannot be assigned once the instance exists, so the document is written from a copy, and
      // the instance the runner applied keeps its fields for the snapshot taken after the write (issue #1002).
      var stripped = perspective.PhysicalFields!.ToDictionary(
          f => f.PropertyName, f => f.IsVectorField ? "System.Array.Empty<float>()" : "default!", StringComparer.Ordinal);
      sb.AppendLine($"    model = {ModelCopy.Render(perspective.InterfaceTypeArguments[0], copy, "model", stripped)};");
    } else {
      // An init-only field of a class that cannot be copied is reported (WHIZ808) rather than assigned.
      foreach (var field in perspective.PhysicalFields!.Where(f => !f.IsInitOnly)) {
        if (field.IsVectorField) {
          sb.AppendLine($"    model.{field.PropertyName} = System.Array.Empty<float>();");
        } else {
          sb.AppendLine($"    model.{field.PropertyName} = default!;");
        }
      }
    }
    sb.AppendLine();
  }

  /// <summary>
  /// Generates scope event handling code for the event loop.
  /// When HasScopeInterface is true, generates IScopeEvent detection and ApplyScope call.
  /// When false, generates a simple IScopeEvent check that uses the proposed scope directly.
  /// </summary>
  private static string _generateScopeEventHandlingCode(PerspectiveInfo perspective) {
    var sb = new StringBuilder();

    // Always generate IScopeEvent detection — perspectives that handle IScopeEvent-implementing
    // events will get scope changes even without IPerspectiveScopeFor
    sb.AppendLine("        if (@event is global::Whizbang.Core.IScopeEvent scopeEvent) {");
    sb.AppendLine("          var proposedScope = scopeEvent.Scope;");

    if (perspective.HasScopeInterface) {
      // Perspective implements IPerspectiveScopeFor — let it decide the final scope
      sb.AppendLine($"          if (perspective is global::Whizbang.Core.Perspectives.IPerspectiveScopeFor<{perspective.InterfaceTypeArguments[0]}> scopePerspective) {{");
      sb.AppendLine("            var currentScope = lastScope ?? new global::Whizbang.Core.Lenses.PerspectiveScope();");
      sb.AppendLine("            lastScope = scopePerspective.ApplyScope(currentScope, proposedScope);");
      sb.AppendLine("          } else {");
      sb.AppendLine("            lastScope = proposedScope;");
      sb.AppendLine("          }");
    } else {
      // No IPerspectiveScopeFor — accept proposed scope directly
      sb.AppendLine("          lastScope = proposedScope;");
    }

    sb.AppendLine("          scopeChanged = true;");
    sb.AppendLine("        }");

    return sb.ToString().TrimEnd('\r', '\n');
  }

  // ========================================
  // Helper Methods for _extractPerspectiveInfo Complexity Reduction
  // ========================================

  /// <summary>
  /// Extracts single-stream IPerspectiveFor interfaces from a class symbol.
  /// </summary>
  private static List<INamedTypeSymbol> _extractSingleStreamInterfaces(INamedTypeSymbol classSymbol) {
    return [.. classSymbol.AllInterfaces
        .Where(i => {
          var originalDef = TypeNameUtilities.Display(i.OriginalDefinition);
          // Match IPerspectiveFor / IPerspectiveWithActionsFor<TModel, TEvent1, ...> with any number of event types (1-50)
          return (originalDef.StartsWith(PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TEvent", StringComparison.Ordinal)
               || originalDef.StartsWith(PERSPECTIVE_WITH_ACTIONS_FOR_INTERFACE_NAME + "<TModel, TEvent", StringComparison.Ordinal))
                 && i.TypeArguments.Length >= 2;
        })];
  }

  /// <summary>
  /// Extracts global IGlobalPerspectiveFor interfaces from a class symbol.
  /// </summary>
  private static List<INamedTypeSymbol> _extractGlobalInterfaces(INamedTypeSymbol classSymbol) {
    return [.. classSymbol.AllInterfaces
        .Where(i => {
          var originalDef = TypeNameUtilities.Display(i.OriginalDefinition);
          // Match IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, ...> with any number of event types (1-50)
          return originalDef.StartsWith(GLOBAL_PERSPECTIVE_FOR_INTERFACE_NAME + "<TModel, TPartitionKey, TEvent", StringComparison.Ordinal)
                 && i.TypeArguments.Length >= 3;
        })];
  }

  /// <summary>
  /// Extracts IPerspectiveWithActionsFor interfaces from a class symbol.
  /// These interfaces return ApplyResult&lt;TModel&gt; instead of TModel, supporting Delete/Purge operations.
  /// </summary>
  private static List<INamedTypeSymbol> _extractWithActionsInterfaces(INamedTypeSymbol classSymbol) {
    return [.. classSymbol.AllInterfaces
        .Where(i => {
          var originalDef = TypeNameUtilities.Display(i.OriginalDefinition);
          // Match IPerspectiveWithActionsFor<TModel, TEvent1, ...> with any number of event types (1-50)
          return originalDef.StartsWith(PERSPECTIVE_WITH_ACTIONS_FOR_INTERFACE_NAME + "<TModel, TEvent", StringComparison.Ordinal)
                 && i.TypeArguments.Length >= 2;
        })];
  }

  /// <summary>
  /// Checks if a class implements IPerspectiveScopeFor&lt;TModel&gt;.
  /// </summary>
  private static bool _hasScopeForInterface(INamedTypeSymbol classSymbol) {
    return classSymbol.AllInterfaces
        .Any(i => {
          var originalDef = TypeNameUtilities.Display(i.OriginalDefinition);
          return originalDef == PERSPECTIVE_SCOPE_FOR_INTERFACE_NAME + "<TModel>"
                 && i.TypeArguments.Length == 1;
        });
  }

  /// <summary>
  /// Extracts model type (first type argument) from perspective interfaces.
  /// </summary>
  /// <remarks>Null when neither list holds an interface, which is how the caller recognizes a class
  /// that is not a perspective at all.</remarks>
  private static ITypeSymbol? _extractModelType(List<INamedTypeSymbol> singleStreamInterfaces, List<INamedTypeSymbol> globalInterfaces) {
    if (singleStreamInterfaces.Count > 0) {
      return singleStreamInterfaces[0].TypeArguments[0];
    }
    return globalInterfaces.Count > 0 ? globalInterfaces[0].TypeArguments[0] : null;
  }

  /// <summary>
  /// Extracts event types from all perspective interfaces.
  /// Returns both event type names and their symbols.
  /// </summary>
  private static (List<string> EventTypes, List<ITypeSymbol> EventTypeSymbols) _extractEventTypesFromInterfaces(
      List<INamedTypeSymbol> singleStreamInterfaces,
      List<INamedTypeSymbol> globalInterfaces) {

    // Extract from single-stream: skip TModel (index 0), all others are events
    var singleStreamEvents = singleStreamInterfaces
        .SelectMany(iface => iface.TypeArguments.Skip(1))
        .Select(symbol => (symbol, fqn: TypeNameUtilities.FullyQualified(symbol)))
        .GroupBy(x => x.fqn)
        .Select(g => g.First());

    // Extract from global: skip TModel (index 0) and TPartitionKey (index 1), rest are events
    var globalEvents = globalInterfaces
        .SelectMany(iface => iface.TypeArguments.Skip(2))
        .Select(symbol => (symbol, fqn: TypeNameUtilities.FullyQualified(symbol)))
        .GroupBy(x => x.fqn)
        .Select(g => g.First());

    // Combine and deduplicate all events
    var allEvents = singleStreamEvents.Concat(globalEvents)
        .GroupBy(x => x.fqn)
        .Select(g => g.First())
        .ToList();
    var eventTypes = allEvents.ConvertAll(x => x.fqn);
    var eventTypeSymbols = allEvents.ConvertAll(x => x.symbol);

    return (eventTypes, eventTypeSymbols);
  }

  /// <summary>
  /// Finds the property with [StreamId] attribute on a model type.
  /// </summary>
  /// <summary>
  /// Builds the object-initializer text for the generated CreateEmptyModel.
  /// Assigns the stream key directly (Guid, Guid?, string, or a strongly-typed
  /// id with a static From(Guid) factory) and initializes any other required
  /// members to default! so the generated code compiles. No reflection.
  /// </summary>
  private static string _buildEmptyModelInitializer(ITypeSymbol modelType, string streamKeyPropertyName) {
    var assignments = new List<string>();

    var properties = modelType.GetMembers().OfType<IPropertySymbol>().ToArray();
    var streamKeyProperty = properties.FirstOrDefault(p => p.Name == streamKeyPropertyName);

    var streamKeyAssigned = false;
    if (streamKeyProperty is { SetMethod: not null }) {
      var expression = _buildStreamKeyInitExpression(streamKeyProperty.Type);
      if (expression is not null) {
        assignments.Add($"{streamKeyPropertyName} = {expression}");
        streamKeyAssigned = true;
      }
    }

    // Required members must appear in the object initializer or the generated
    // runner will not compile. default! matches the previous behavior of
    // leaving them unset on the empty model.
    foreach (var property in properties) {
      var isUnassignedStreamKey = property.Name == streamKeyPropertyName && !streamKeyAssigned;
      if (property.IsRequired
          && property.SetMethod is not null
          && (property.Name != streamKeyPropertyName || isUnassignedStreamKey)) {
        assignments.Add($"{property.Name} = default!");
      }
    }

    return assignments.Count == 0 ? "{ }" : "{ " + string.Join(", ", assignments) + " }";
  }

  /// <summary>
  /// Returns the C# expression that converts the runner's Guid streamId into
  /// the stream key property's type, or null when no safe conversion exists
  /// (in which case the property is left unset, as the reflection-based
  /// implementation did for unwritable properties).
  /// </summary>
  private static string? _buildStreamKeyInitExpression(ITypeSymbol propertyType) {
    var displayName = TypeNameUtilities.Display(propertyType);
    if (displayName is "System.Guid") {
      return "streamId";
    }
    if (displayName is "System.Guid?") {
      return "streamId";
    }
    if (propertyType.SpecialType == SpecialType.System_String) {
      return "streamId.ToString()";
    }

    // Strongly-typed ids: a static From(System.Guid) factory returning the
    // property type (the convention used by generated value-object ids).
    var hasFromFactory = propertyType.GetMembers("From").OfType<IMethodSymbol>().Any(m =>
        m.IsStatic
        && m.DeclaredAccessibility == Accessibility.Public
        && m.Parameters.Length == 1
        && TypeNameUtilities.IsNamed(m.Parameters[0].Type, "System.Guid")
        && SymbolEqualityComparer.Default.Equals(m.ReturnType, propertyType));
    if (hasFromFactory) {
      return $"{TypeNameUtilities.FullyQualified(propertyType)}.From(streamId)";
    }

    return null;
  }

  private static string? _findModelStreamIdProperty(ITypeSymbol modelType) {
    foreach (var member in modelType.GetMembers()) {
      if (member is IPropertySymbol property) {
        var hasStreamIdAttribute = property.GetAttributes()
            .Any(a => TypeNameUtilities.IsNamed(a.AttributeClass, "Whizbang.Core.StreamIdAttribute"));

        if (hasStreamIdAttribute) {
          return property.Name;
        }
      }
    }
    return null;
  }

  /// <summary>
  /// Extracts StreamId properties from event types.
  /// </summary>
  private static List<EventStreamIdInfo> _extractEventStreamIdsFromTypes(List<string> eventTypes, List<ITypeSymbol> eventTypeSymbols) {
    var eventStreamIds = new List<EventStreamIdInfo>();
    for (int i = 0; i < eventTypes.Count; i++) {
      var eventTypeName = eventTypes[i];
      var eventTypeSymbol = eventTypeSymbols[i];

      var eventStreamIdProp = _extractStreamIdProperty(eventTypeSymbol);
      if (eventStreamIdProp != null) {
        eventStreamIds.Add(new EventStreamIdInfo(
            EventTypeName: eventTypeName,
            StreamIdPropertyName: eventStreamIdProp
        ));
      }
    }
    return eventStreamIds;
  }

  /// <summary>
  /// Builds message type names in database format (TypeName, AssemblyName).
  /// </summary>
  private static string[] _buildMessageTypeNames(List<ITypeSymbol> eventTypeSymbols) {
    // Shared runtime formatter ("Ns.Outer+Nested, Assembly") — the same builder
    // PerspectiveDiscoveryGenerator uses for the emitted MessageAssociation keys, so every
    // generator renders the database type-name identically (CLR '+' for nested types).
    return [.. eventTypeSymbols.Select(TypeNameUtilities.FormatTypeNameForRuntime)];
  }

  /// <summary>
  /// Extracts event types whose Apply methods have [MustExist] attribute.
  /// These events require the model to already exist before the Apply method is called.
  /// </summary>
  private static string[] _extractMustExistEventTypes(
      INamedTypeSymbol classSymbol,
      List<string> eventTypes) {
    var mustExistEvents = new List<string>();

    // Use shared utility to include inherited Apply methods from base classes
    foreach (var method in classSymbol.GetAllMethodsByName("Apply")) {
      var hasMustExist = method.GetAttributes()
          .Any(a => TypeNameUtilities.IsNamed(a.AttributeClass, MUST_EXIST_ATTRIBUTE_NAME));

      if (hasMustExist && method.Parameters.Length >= 2) {
        // Second parameter is the event type
        var eventType = TypeNameUtilities.FullyQualified(method.Parameters[1].Type);
        if (eventTypes.Contains(eventType)) {
          mustExistEvents.Add(eventType);
        }
      }
    }

    return [.. mustExistEvents];
  }

  /// <summary>
  /// Extracts return type information for each Apply method.
  /// Determines how to handle the result (model update, action, tuple, etc.).
  /// </summary>
  private static EventReturnTypeInfo[] _extractEventReturnTypes(
      INamedTypeSymbol classSymbol,
      List<string> eventTypes
      ) {

    var returnTypes = new List<EventReturnTypeInfo>();

    // Use shared utility to include inherited Apply methods from base classes
    foreach (var method in classSymbol.GetAllMethodsByName("Apply")) {
      if (method.Parameters.Length < 2) {
        continue;
      }

      // Second parameter is the event type
      var eventType = TypeNameUtilities.FullyQualified(method.Parameters[1].Type);
      if (!eventTypes.Contains(eventType)) {
        continue;
      }

      var returnType = _classifyReturnType(method.ReturnType);
      returnTypes.Add(new EventReturnTypeInfo(eventType, returnType));
    }

    return [.. returnTypes];
  }

  /// <summary>
  /// The event types whose Apply can do nothing but purge the row (#1151): a body that is exactly
  /// <c>ApplyResult&lt;T&gt;.Purge()</c>, <c>ModelAction.Purge</c> or <c>(null, ModelAction.Purge)</c>, as an expression body
  /// or a block holding only that return. Anything else, a condition, another statement, is left out: a wrong call
  /// deletes a row that should have survived, so the analysis only says yes when the body leaves no other outcome.
  /// </summary>
  private static string[] _extractUnconditionalPurgeEventTypes(INamedTypeSymbol classSymbol, List<string> eventTypes) {
    var purges = new List<string>();
    foreach (var method in classSymbol.GetAllMethodsByName("Apply")) {
      if (method.Parameters.Length < 2) {
        continue;
      }
      var eventType = TypeNameUtilities.FullyQualified(method.Parameters[1].Type);
      if (eventTypes.Contains(eventType) && !purges.Contains(eventType)
          && method.DeclaringSyntaxReferences.Length == 1
          && method.DeclaringSyntaxReferences[0].GetSyntax() is MethodDeclarationSyntax declaration
          && _onlyReturned(declaration) is { } returned
          && _isPurge(returned)) {
        purges.Add(eventType);
      }
    }
    return [.. purges];
  }

  private static ExpressionSyntax? _onlyReturned(MethodDeclarationSyntax declaration) =>
    declaration.ExpressionBody?.Expression
      ?? (declaration.Body is { Statements.Count: 1 } body && body.Statements[0] is ReturnStatementSyntax { Expression: { } expression }
        ? expression
        : null);

  private static bool _isPurge(ExpressionSyntax expression) {
    while (expression is ParenthesizedExpressionSyntax parenthesized) {
      expression = parenthesized.Expression;
    }
    return expression switch {
      // ApplyResult<T>.Purge()
      InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0, Expression: MemberAccessExpressionSyntax access } =>
        access.Name.Identifier.ValueText == "Purge" && _names(access.Expression, "ApplyResult"),
      // ModelAction.Purge
      MemberAccessExpressionSyntax access => _isModelActionPurge(access),
      // (null, ModelAction.Purge)
      TupleExpressionSyntax { Arguments.Count: 2 } tuple =>
        tuple.Arguments[0].Expression is LiteralExpressionSyntax literal
          && (literal.IsKind(SyntaxKind.NullLiteralExpression) || literal.IsKind(SyntaxKind.DefaultLiteralExpression))
          && tuple.Arguments[1].Expression is MemberAccessExpressionSyntax second
          && _isModelActionPurge(second),
      _ => false,
    };
  }

  private static bool _isModelActionPurge(MemberAccessExpressionSyntax access) =>
    access.Name.Identifier.ValueText == "Purge" && _names(access.Expression, "ModelAction");

  // The receiver is the named type, written bare, generic or qualified (ApplyResult<T>, Perspectives.ModelAction,
  // global::…ApplyResult<T>): a qualified name is a member access whose last name is the type.
  private static bool _names(ExpressionSyntax receiver, string typeName) =>
    (receiver is MemberAccessExpressionSyntax qualified ? qualified.Name : receiver as SimpleNameSyntax)?.Identifier.ValueText
      == typeName;

  /// <summary>
  /// Classifies the return type of an Apply method.
  /// </summary>
  private static ApplyReturnType _classifyReturnType(ITypeSymbol returnType) {
    var returnTypeName = TypeNameUtilities.FullyQualified(returnType);

    // Check for ModelAction
    if (returnTypeName == "global::Whizbang.Core.Perspectives.ModelAction") {
      return ApplyReturnType.Action;
    }

    // Check for ApplyResult<TModel>
    if (returnTypeName.StartsWith("global::Whizbang.Core.Perspectives.ApplyResult<", StringComparison.Ordinal)) {
      return ApplyReturnType.ApplyResult;
    }

    // Check for tuple (TModel?, ModelAction)
    if (returnType is INamedTypeSymbol namedType &&
        namedType.IsTupleType &&
        namedType.TupleElements.Length == 2) {
      var secondElement = TypeNameUtilities.FullyQualified(namedType.TupleElements[1].Type);
      if (secondElement == "global::Whizbang.Core.Perspectives.ModelAction") {
        return ApplyReturnType.Tuple;
      }
    }

    // Check for nullable model (TModel?)
    if (returnType.NullableAnnotation == Microsoft.CodeAnalysis.NullableAnnotation.Annotated) {
      return ApplyReturnType.NullableModel;
    }

    // Default: standard model return
    return ApplyReturnType.Model;
  }

  /// <summary>
  /// Gets the runner class name from a perspective simple name.
  /// E.g., "OrderPerspective" -> "OrderPerspectiveRunner"
  /// E.g., "OrderStatus.Projection" -> "OrderStatusProjectionRunner"
  /// </summary>
  private static string _getRunnerName(string simpleName) {
    // Remove dots from nested type names to create valid C# identifier
    return $"{simpleName.Replace(".", "")}Runner";
  }

  /// <summary>
  /// Extracts the FieldStorageMode from the [PerspectiveStorage] attribute on the model type.
  /// Returns 0 (JsonOnly) if the attribute is not present.
  /// </summary>
  private static int _extractStorageMode(INamedTypeSymbol modelType) {
    foreach (var attribute in modelType.GetAttributes()) {
      if (TypeNameUtilities.SimpleNameOrNull(attribute.AttributeClass) == "PerspectiveStorageAttribute" &&
          attribute.ConstructorArguments.Length > 0 &&
          attribute.ConstructorArguments[0].Value is int mode) {
        return mode;
      }
    }

    return 0; // JsonOnly
  }

  /// <summary>
  /// Discovers physical fields (marked with [PhysicalField] or [VectorField]) on model properties.
  /// These fields need to be extracted and passed to UpsertWithPhysicalFieldsAsync.
  /// </summary>
  /// <remarks>A model that is not a named type — an array satisfies the interface's <c>class</c>
  /// constraint — declares no properties, so the loop never runs and the result is the same empty
  /// set.</remarks>
  /// <summary>
  /// Emits the <c>[ModuleInitializer]</c> that registers what each member reads as when the document has no key
  /// for it, in <c>PerspectiveMemberDefaultRegistry</c>. Empty when the model declares none.
  /// </summary>
  private static string _buildMemberDefaultRegistration(PerspectiveInfo perspective, string modelTypeName) {
    var defaults = perspective.MemberDefaults!;
    var sb = new StringBuilder();
    sb.Append("[global::System.Runtime.CompilerServices.ModuleInitializer]\n  internal static void _registerMemberDefaults() {");
    foreach (var entry in defaults) {
      var parts = entry.Split(MEMBER_DEFAULT_SEPARATOR);
      if (parts.Length != 2) {
        continue;
      }
      sb.Append("\n    global::Whizbang.Core.Perspectives.PerspectiveMemberDefaultRegistry.Register(typeof(")
        .Append(modelTypeName).Append("), \"").Append(parts[0]).Append("\", ").Append(parts[1]).Append(");");
    }
    sb.Append("\n  }");
    return sb.ToString();
  }

  /// <summary>
  /// What each of the model's members reads as when the stored document has no key for it, as
  /// <c>Name\u0001&lt;C# expression&gt;</c> entries. A document lacks a key whenever the member was added after
  /// those rows were written; a rebuild deserializes them and the member holds this value, so a collective
  /// predicate has to filter on it rather than on SQL NULL (#1044).
  /// </summary>
  private static string[] _discoverMemberDefaults(INamedTypeSymbol modelType) {
    var defaults = new List<string>();

    foreach (var property in modelType.GetAllProperties()) {
      var declared = _tryDeclaredDefault(property);
      if (declared is not null) {
        defaults.Add(property.Name + MEMBER_DEFAULT_SEPARATOR + declared);
      }
    }

    return [.. defaults];
  }

  /// <summary>
  /// The C# expression for what one member reads as when the stored document has no key for it, or null when the
  /// declaration does not say.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A member is registered when what an absent key reads as is KNOWABLE from the declaration alone. Registering a
  /// guess that differs from what a rebuild produces is worse than registering nothing, so an expression that could
  /// evaluate to anything is skipped — but a compile-time constant is not a guess. An enumeration member and a cast
  /// of a literal name the same value every rebuild produces, and they are how a status-like member ordinarily
  /// declares its eligible default, so testing for literal SYNTAX left exactly those members filtered on SQL NULL
  /// while the replay they are meant to agree with saw the default.
  /// </para>
  /// <para>
  /// Nullability alone does not skip a member. That skip holds only because an absent key reads as null on BOTH
  /// paths, which is true of a nullable member with no initializer and false of one with an initializer: a rebuild
  /// runs the initializer and the member holds its value.
  /// </para>
  /// <para>
  /// A member whose declaration is not in this compilation is skipped — whether it carries an initializer cannot be
  /// known from metadata. A constant declared on ANOTHER type (<c>= Defaults.Phase</c>) is skipped too: resolving it
  /// needs a semantic model, and taking one would bind this step to compilation identity and cost it its
  /// incremental cacheability.
  /// </para>
  /// </remarks>
  private static string? _tryDeclaredDefault(IPropertySymbol property) {
    // A member declared outside this compilation has no syntax to read.
    var declaration = property.DeclaringSyntaxReferences
      .Select(reference => reference.GetSyntax())
      .OfType<PropertyDeclarationSyntax>()
      .FirstOrDefault();

    if (declaration is null) {
      return null;
    }

    var type = property.Type;
    var typeName = TypeNameUtilities.FullyQualified(type);

    if (declaration.Initializer is null) {
      // No initializer: an absent key reads as the CLR default, which is null for anything nullable and for a
      // reference type — a value both paths already agree on, so there is nothing to declare.
      var nullable = type.NullableAnnotation == NullableAnnotation.Annotated
        || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
      return !nullable && type.IsValueType ? $"default({typeName})" : null;
    }

    return _tryKnowableValue(declaration.Initializer.Value, type, typeName);
  }

  /// <summary>
  /// The C# expression for an initializer whose value the declaration states outright, or null when reading it
  /// would mean evaluating an expression.
  /// </summary>
  private static string? _tryKnowableValue(ExpressionSyntax expression, ITypeSymbol type, string typeName) =>
    expression switch {
      // `= null` says the member reads as null, which is what registering nothing already means.
      LiteralExpressionSyntax literal when literal.Token.IsKind(SyntaxKind.NullKeyword) => null,
      // A value type's literal is cast to the member's own type so the registered value boxes as that type —
      // `= 5` on a long must arrive as a long, not an int, or it binds against the column as the wrong type.
      LiteralExpressionSyntax literal => type.IsValueType ? $"({typeName}){literal.Token.Text}" : literal.Token.Text,
      // Unary minus only parses ahead of a numeric literal, so the member is necessarily a value type.
      PrefixUnaryExpressionSyntax { Operand: LiteralExpressionSyntax negated } unary
          when unary.OperatorToken.IsKind(SyntaxKind.MinusToken) => $"({typeName})(-{negated.Token.Text})",
      // `(Stage)2` names the same constant the member does; the form it is written in changes nothing. A cast of
      // anything else is still that unresolvable thing, which the recursion reports.
      CastExpressionSyntax cast => _tryKnowableValue(cast.Expression, type, typeName),
      // `Stage.Active` — a member of the member's OWN enumeration resolves from the type symbol alone.
      MemberAccessExpressionSyntax access => _tryEnumMember(type, access.Name.Identifier.ValueText),
      _ => null,
    };

  /// <summary>
  /// The fully qualified name of <paramref name="memberName"/> when it names a member of <paramref name="type"/>'s
  /// enumeration, or null when it names anything else.
  /// </summary>
  /// <remarks>
  /// A nullable enumeration is unwrapped first: <c>Stage? x = Stage.Retired</c> names a Stage member, and the value
  /// registered for it boxes as Stage, which is what the column stores.
  /// </remarks>
  private static string? _tryEnumMember(ITypeSymbol type, string memberName) {
    var enumType = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
      ? nullable.TypeArguments[0]
      : type;

    if (enumType.TypeKind != TypeKind.Enum) {
      return null;
    }

    foreach (var member in enumType.GetMembers()) {
      // An enumeration's members are its constant fields; it also carries the non-constant `value__`.
      if (member is IFieldSymbol { HasConstantValue: true } && member.Name == memberName) {
        return TypeNameUtilities.FullyQualified(enumType) + "." + memberName;
      }
    }

    return null;
  }

  private static PhysicalFieldInfoCompact[] _discoverPhysicalFields(INamedTypeSymbol modelType) {
    var physicalFields = new List<PhysicalFieldInfoCompact>();

    foreach (var property in modelType.GetAllProperties()) {
      var fieldInfo = _tryExtractPhysicalField(property);
      if (fieldInfo is not null) {
        physicalFields.Add(fieldInfo);
      }
    }

    return [.. physicalFields];
  }

  /// <summary>
  /// Tries to extract physical field info from a property's attributes.
  /// Returns null if the property has no [PhysicalField] or [VectorField] attribute.
  /// </summary>
  private static PhysicalFieldInfoCompact? _tryExtractPhysicalField(IPropertySymbol property) {
    const string PHYSICAL_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.PhysicalFieldAttribute";
    const string VECTOR_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.VectorFieldAttribute";

    foreach (var attribute in property.GetAttributes()) {
      var attrClassName = TypeNameUtilities.DisplayOrNull(attribute.AttributeClass);

      if (attrClassName != PHYSICAL_FIELD_ATTRIBUTE && attrClassName != VECTOR_FIELD_ATTRIBUTE) {
        continue;
      }

      var isVectorField = attrClassName == VECTOR_FIELD_ATTRIBUTE;

      // Extract ColumnName (and a declared ColumnType) from the named arguments if provided
      string? columnName = null;
      string? columnType = null;
      foreach (var namedArg in attribute.NamedArguments) {
        if (namedArg.Key == "ColumnName" && namedArg.Value.Value is string cn) {
          columnName = cn;
        } else if (namedArg.Key == "ColumnType" && namedArg.Value.Value is string ct) {
          columnType = ct;
        }
      }

      columnName ??= NamingConventionUtilities.ToSnakeCase(property.Name);

      return new PhysicalFieldInfoCompact(
          PropertyName: property.Name,
          ColumnName: columnName,
          IsVectorField: isVectorField,
          EnumScalarType: PhysicalFieldScalar.EnumColumnScalar(property.Type),
          // An object, a collection or a dictionary is a jsonb column unless the author declared otherwise.
          ColumnType: columnType ?? PhysicalFieldScalar.DefaultColumnType(property.Type),
          TypeName: TypeNameUtilities.FullyQualifiedWithNullability(property.Type),
          // Not assignable on an instance that exists: an init setter, or no setter at all. Either way the field is
          // set only through a copy, and a get-only one that stores a value makes the class uncopyable (WHIZ808).
          IsInitOnly: property.SetMethod is not { IsInitOnly: false }
      );
    }

    return null;
  }

}

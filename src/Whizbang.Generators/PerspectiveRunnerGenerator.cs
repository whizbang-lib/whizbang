using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
  private const string PERSPECTIVE_FOR_INTERFACE_NAME = "Whizbang.Core.Perspectives.IPerspectiveFor";
  private const string PERSPECTIVE_WITH_ACTIONS_FOR_INTERFACE_NAME = "Whizbang.Core.Perspectives.IPerspectiveWithActionsFor";
  private const string GLOBAL_PERSPECTIVE_FOR_INTERFACE_NAME = "Whizbang.Core.Perspectives.IGlobalPerspectiveFor";
  private const string PERSPECTIVE_SCOPE_FOR_INTERFACE_NAME = "Whizbang.Core.Perspectives.IPerspectiveScopeFor";
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

    if (singleStreamInterfaces.Count == 0 && withActionsInterfaces.Count == 0 && globalInterfaces.Count == 0) {
      return null;
    }

    // Combine single-stream and with-actions interfaces (both use same model/event pattern)
    var combinedSingleStreamInterfaces = singleStreamInterfaces.Concat(withActionsInterfaces).ToList();

    // Extract model type (from combined single-stream interfaces)
    var modelType = _extractModelType(combinedSingleStreamInterfaces, globalInterfaces);
    if (modelType is null) {
      return null;
    }

    var modelTypeName = modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // Extract event types from all interfaces (using combined interfaces)
    var (eventTypes, eventTypeSymbols) = _extractEventTypesFromInterfaces(combinedSingleStreamInterfaces, globalInterfaces);

    if (eventTypes.Count == 0) {
      return null;
    }

    // A perspective is ephemeral-tainted (viral) if it applies ANY ephemeral event. Ephemeral perspectives
    // snapshot on their own aggressive, single-slot cadence so a fresh rewind floor exists within the grace
    // window before consumed bodies are reaped. Resolved at compile time — zero reflection.
    var isEphemeral = eventTypeSymbols.Any(static s => EphemeralResolver.IsEphemeral(s));

    // TtlRow perspective-row expiry (E2-4d): resolved virally like isEphemeral. If ANY applied [Ephemeral]
    // event chose TransientStorage.TtlRow, the perspective's rows expire; the TTL is the LONGEST of its
    // TtlRow events' TtlSeconds (keep the row until its longest-lived contributing data expires). -1 = the
    // rows never expire (no TtlRow event). The generator emits a [ModuleInitializer] to register this TTL.
    var ttlRowSeconds = -1;
    foreach (var s in eventTypeSymbols) {
      if (s is INamedTypeSymbol named && EphemeralResolver.Resolve(named) is { Storage: "TtlRow" }) {
        var ttl = EphemeralResolver.ResolveTtlSeconds(named);
        if (ttl > ttlRowSeconds) {
          ttlRowSeconds = ttl;
        }
      }
    }

    // Perspective row retention: an explicit [RowTtl] on the perspective class OUTRANKS the
    // ephemeral-derived TTL (the override ladder — the read model's own declaration is more
    // specific than what its events imply). This is also what opens row expiry to Sourced
    // perspectives, which have no [Ephemeral] events to derive from: their rows can age out
    // while the log stays durable, because the event-time expiry anchor keeps rebuilds
    // deterministic and a reaped row re-folds from the log on wake.
    var rowTtlAttribute = classSymbol.GetAttributes().FirstOrDefault(
        static a => a.AttributeClass?.Name is "RowTtlAttribute" or "RowTtl");
    if (rowTtlAttribute is not null) {
      var explicitDays = -1;
      var explicitSeconds = -1;
      foreach (var namedArg in rowTtlAttribute.NamedArguments) {
        if (namedArg.Key == "Days" && namedArg.Value.Value is int d) {
          explicitDays = d;
        } else if (namedArg.Key == "Seconds" && namedArg.Value.Value is int sec) {
          explicitSeconds = sec;
        }
      }
      var explicitTtl = explicitSeconds >= 0 ? explicitSeconds
          : explicitDays >= 0 ? explicitDays * 86400
          : -1;
      if (explicitTtl >= 0) {
        ttlRowSeconds = explicitTtl;
      }
    }

    // A1-6b: a perspective marked [FullHistory] needs every event and cannot resume from a carry-forward /
    // closing event — the A1 close guard refuses a discard-close of any stream it consumes. Resolved at compile
    // time; the generator registers the perspective's name so the runtime guard can key off it.
    var isFullHistory = classSymbol.GetAttributes().Any(
        static a => a.AttributeClass?.Name is "FullHistoryAttribute" or "FullHistory");

    // Find StreamId property on model
    var streamKeyPropertyName = _findModelStreamIdProperty(modelType);
    if (streamKeyPropertyName is null) {
      // Return warning instead of silently skipping (WHIZ033)
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

    // Compute nested-aware simple name for unique hintNames
    var simpleName = TypeNameUtilities.GetSimpleName(classSymbol);

    // Compute CLR format name for database storage (uses + for nested types)
    var clrTypeName = TypeNameUtilities.BuildClrTypeName(classSymbol);

    // Discover physical fields (including vector fields) on model properties
    var physicalFields = _discoverPhysicalFields(modelType);

    // Extract storage mode from [PerspectiveStorage] attribute on model type
    var storageMode = _extractStorageMode(modelType);

    // Check if model is a record type (supports 'with {}' expressions for immutable copies)
    var isModelRecord = modelType is INamedTypeSymbol namedModel && namedModel.IsRecord;

    // Check if perspective implements IPerspectiveScopeFor<TModel> for IScopeEvent handling
    var hasScopeInterface = _hasScopeForInterface(classSymbol);

    // Extract [InheritScope].OnCreate flags from the model. Default 63 = ScopeFields.All
    // when the attribute is absent (preserves legacy copy-everything behavior). Adding
    // [InheritScope] opts the perspective into the safer per-field default.
    var inheritScopeOnCreate = _extractInheritScopeOnCreate(modelType);

    return new PerspectiveOrWarning(
        Info: new PerspectiveInfo(
            ClassName: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            SimpleName: simpleName,
            ClrTypeName: clrTypeName,
            InterfaceTypeArguments: typeArguments,
            EventTypes: [.. eventTypes],
            MessageTypeNames: messageTypeNames,
            StreamIdPropertyName: streamKeyPropertyName,
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
        ),
        Warning: null
    );
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
      var name = attr.AttributeClass?.Name;
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
              .Any(a => a.AttributeClass?.ToDisplayString() == "Whizbang.Core.StreamIdAttribute");

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

    // Generate event types array for polymorphic deserialization
    var eventTypesArray = new StringBuilder();
    for (int i = 0; i < perspective.EventTypes.Length; i++) {
      eventTypesArray.Append($"      typeof({perspective.EventTypes[i]})");
      if (i < perspective.EventTypes.Length - 1) {
        eventTypesArray.AppendLine(",");
      } else {
        eventTypesArray.AppendLine();
      }
    }

    // Generate ExtractStreamId methods (one per event type with StreamId)
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

    // Generate the ResolveTargetStreamId switch body used by RunRebuildAsync. One arm per
    // [StreamId]-bearing event type returns the event's post-upcast StreamId; the default keeps the
    // event on the physical stream. When the perspective handles no [StreamId] events, the body is
    // just the physical-stream fallback (so re-key never splits — identical to the old rebuild).
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
    result = TemplateUtilities.ReplaceRegion(result, "IS_EPHEMERAL",
        $"private const bool _isEphemeralPerspective = {(perspective.IsEphemeral ? "true" : "false")};");
    // E2-4d: a TtlRow perspective registers its row TTL via a [ModuleInitializer] so the upsert stamps
    // expires_at. Non-TtlRow perspectives emit nothing (their rows never expire).
    result = TemplateUtilities.ReplaceRegion(result, "TTL_REGISTRATION", perspective.TtlRowSeconds >= 0
        ? $"[global::System.Runtime.CompilerServices.ModuleInitializer]\n  internal static void _registerRowTtl() =>\n      global::Whizbang.Core.Perspectives.PerspectiveTtlRegistry.Register(typeof({modelTypeName}), {perspective.TtlRowSeconds});"
        : "");
    // A1-6b: register a [FullHistory] perspective's name (matching its association target_name = its ClrTypeName)
    // so the close guard can refuse a discard-close of any stream it consumes. Empty for resumable perspectives.
    result = TemplateUtilities.ReplaceRegion(result, "FULL_HISTORY_REGISTRATION", perspective.IsFullHistory
        ? $"[global::System.Runtime.CompilerServices.ModuleInitializer]\n  internal static void _registerFullHistory() =>\n      global::Whizbang.Core.Perspectives.FullHistoryPerspectiveRegistry.Register(\"{perspective.ClrTypeName}\");"
        : "");
    result = result.Replace("__RUNNER_CLASS_NAME__", runnerName);
    result = result.Replace("__PERSPECTIVE_CLASS_NAME__", perspective.ClassName);
    result = result.Replace("__MODEL_TYPE_NAME__", modelTypeName);
    result = result.Replace("__EMPTY_MODEL_INITIALIZER__", perspective.EmptyModelInitializer);
    result = result.Replace("__PERSPECTIVE_SIMPLE_NAME__", perspectiveSimpleName);

    return result;
  }

  /// <summary>
  /// Generates the upsert code for the SaveModelAndCheckpointAsync method.
  /// Uses UpsertWithPhysicalFieldsAsync when physical fields exist, UpsertAsync otherwise.
  /// </summary>
  private static string _generateUpsertCode(PerspectiveInfo perspective) {
    var sb = new StringBuilder();

    if (perspective.PhysicalFields == null || perspective.PhysicalFields!.Length == 0) {
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
    sb.AppendLine("    // next run can skip already-applied events (see RunWithEventsAsync idempotency guard).");
    sb.AppendLine("    if (scope != null) {");
    sb.AppendLine("      await _perspectiveStore.UpsertAsync(");
    sb.AppendLine("          streamId,");
    sb.AppendLine("          model,");
    sb.AppendLine("          scope,");
    sb.AppendLine("          forceUpdateScope,");
    sb.AppendLine("          metadata,");
    sb.AppendLine("          cancellationToken");
    sb.AppendLine("      );");
    sb.AppendLine("    } else {");
    sb.AppendLine("      await _perspectiveStore.UpsertAsync(");
    sb.AppendLine("          streamId,");
    sb.AppendLine("          model,");
    sb.AppendLine("          new global::Whizbang.Core.Lenses.PerspectiveScope(),");
    sb.AppendLine("          false,");
    sb.AppendLine("          metadata,");
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
    sb.AppendLine("    // so the next run can skip already-applied events (see RunWithEventsAsync idempotency guard).");
    sb.AppendLine("    await _perspectiveStore.UpsertWithPhysicalFieldsAsync(");
    sb.AppendLine("        streamId,");
    sb.AppendLine("        model,");
    sb.AppendLine("        physicalFieldValues,");
    sb.AppendLine("        scope,");
    sb.AppendLine("        forceUpdateScope,");
    sb.AppendLine("        metadata,");
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
      var comma = i < perspective.PhysicalFields!.Length - 1 ? "," : "";
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
  /// storage. Records use <c>with { ... }</c> for an immutable copy; classes mutate in place.
  /// Vector fields get <c>System.Array.Empty&lt;float&gt;()</c> because EF Core's
  /// JsonCollectionOfStructsReaderWriter crashes on a null JSON token.
  /// </summary>
  private static void _appendSplitModePhysicalFieldStripping(StringBuilder sb, PerspectiveInfo perspective) {
    sb.AppendLine("    // Split mode: exclude physical fields from JSONB — values stored in physical columns only");
    if (perspective.IsModelRecord) {
      var withProps = string.Join(", ", perspective.PhysicalFields!.Select(f =>
        f.IsVectorField ? $"{f.PropertyName} = System.Array.Empty<float>()" : $"{f.PropertyName} = default!"));
      sb.AppendLine($"    model = model with {{ {withProps} }};");
    } else {
      foreach (var field in perspective.PhysicalFields!) {
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
          var originalDef = i.OriginalDefinition.ToDisplayString();
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
          var originalDef = i.OriginalDefinition.ToDisplayString();
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
          var originalDef = i.OriginalDefinition.ToDisplayString();
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
          var originalDef = i.OriginalDefinition.ToDisplayString();
          return originalDef == PERSPECTIVE_SCOPE_FOR_INTERFACE_NAME + "<TModel>"
                 && i.TypeArguments.Length == 1;
        });
  }

  /// <summary>
  /// Extracts model type (first type argument) from perspective interfaces.
  /// </summary>
  private static ITypeSymbol? _extractModelType(List<INamedTypeSymbol> singleStreamInterfaces, List<INamedTypeSymbol> globalInterfaces) {
    if (singleStreamInterfaces.Count > 0) {
      return singleStreamInterfaces[0].TypeArguments[0];
    }
    if (globalInterfaces.Count > 0) {
      return globalInterfaces[0].TypeArguments[0];
    }
    return null;
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
        .Select(symbol => (symbol, fqn: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))
        .GroupBy(x => x.fqn)
        .Select(g => g.First());

    // Extract from global: skip TModel (index 0) and TPartitionKey (index 1), rest are events
    var globalEvents = globalInterfaces
        .SelectMany(iface => iface.TypeArguments.Skip(2))
        .Select(symbol => (symbol, fqn: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))
        .GroupBy(x => x.fqn)
        .Select(g => g.First());

    // Combine and deduplicate all events
    var allEvents = singleStreamEvents.Concat(globalEvents)
        .GroupBy(x => x.fqn)
        .Select(g => g.First())
        .ToList();
    var eventTypes = allEvents.Select(x => x.fqn).ToList();
    var eventTypeSymbols = allEvents.Select(x => x.symbol).ToList();

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
    var displayName = propertyType.ToDisplayString();
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
        && m.Parameters[0].Type.ToDisplayString() == "System.Guid"
        && SymbolEqualityComparer.Default.Equals(m.ReturnType, propertyType));
    if (hasFromFactory) {
      return $"{propertyType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.From(streamId)";
    }

    return null;
  }

  private static string? _findModelStreamIdProperty(ITypeSymbol modelType) {
    foreach (var member in modelType.GetMembers()) {
      if (member is IPropertySymbol property) {
        var hasStreamIdAttribute = property.GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString() == "Whizbang.Core.StreamIdAttribute");

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
          .Any(a => a.AttributeClass?.ToDisplayString() == MUST_EXIST_ATTRIBUTE_NAME);

      if (hasMustExist && method.Parameters.Length >= 2) {
        // Second parameter is the event type
        var eventType = method.Parameters[1].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
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
      var eventType = method.Parameters[1].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
      if (!eventTypes.Contains(eventType)) {
        continue;
      }

      var returnType = _classifyReturnType(method.ReturnType);
      returnTypes.Add(new EventReturnTypeInfo(eventType, returnType));
    }

    return [.. returnTypes];
  }

  /// <summary>
  /// Classifies the return type of an Apply method.
  /// </summary>
  private static ApplyReturnType _classifyReturnType(ITypeSymbol returnType) {
    var returnTypeName = returnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

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
      var secondElement = namedType.TupleElements[1].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
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
  private static int _extractStorageMode(ITypeSymbol modelType) {
    if (modelType is not INamedTypeSymbol namedModelType) {
      return 0;
    }

    foreach (var attribute in namedModelType.GetAttributes()) {
      if (attribute.AttributeClass?.Name == "PerspectiveStorageAttribute" &&
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
  private static PhysicalFieldInfoCompact[] _discoverPhysicalFields(ITypeSymbol modelType) {
    if (modelType is not INamedTypeSymbol namedModelType) {
      return [];
    }

    var physicalFields = new List<PhysicalFieldInfoCompact>();

    foreach (var property in namedModelType.GetAllProperties()) {
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
      var attrClassName = attribute.AttributeClass?.ToDisplayString();

      if (attrClassName != PHYSICAL_FIELD_ATTRIBUTE && attrClassName != VECTOR_FIELD_ATTRIBUTE) {
        continue;
      }

      var isVectorField = attrClassName == VECTOR_FIELD_ATTRIBUTE;

      // Extract ColumnName from named argument if provided
      string? columnName = null;
      foreach (var namedArg in attribute.NamedArguments) {
        if (namedArg.Key == "ColumnName" && namedArg.Value.Value is string cn) {
          columnName = cn;
          break;
        }
      }

      columnName ??= NamingConventionUtilities.ToSnakeCase(property.Name);

      return new PhysicalFieldInfoCompact(
          PropertyName: property.Name,
          ColumnName: columnName,
          IsVectorField: isVectorField
      );
    }

    return null;
  }

}

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Sagas.Generators;

/// <summary>
/// Incremental source generator for <c>[Saga&lt;TBase&gt;("Name")]</c> and
/// <c>[Saga("Name")]</c>. Discovers partial classes carrying the
/// attribute and emits nine nested event classes, a typed
/// <c>Service</c> subclass of <c>BaseSagaService</c>, and a
/// <c>Add{SagaName}Saga</c> extension method.
/// </summary>
[Generator]
public sealed class SagaGenerator : IIncrementalGenerator {
  private const string CONTINUES_WITH_ATTRIBUTE = "Whizbang.Sagas.ContinuesWithAttribute";
  private const int RAN_TO_THE_END = 3;   // Completed | CompletedWithFailures

  // Members every generated saga state carries. They are emitted by each of the state
  // shapes below, so each declaration lives here once and the shapes agree by construction.
  private const string SAGA_NAME_PROPERTY_PREFIX = "    public string SagaName { get; set; } = \"";
  private const string ENTITY_ID_PROPERTY = "    [global::Whizbang.Core.StreamId] public global::System.Guid EntityId { get; set; }";
  private const string ITEM_IDENTIFIER_PROPERTY = "    public string ItemIdentifier { get; set; } = \"\";";
  private const string DISPLAY_NAME_PROPERTY = "    public string? DisplayName { get; set; }";


  // SagaAttribute and SagaAttribute<TEventBase> live in
  // Whizbang.Sagas.Contracts as regular runtime types — not emitted via
  // post-init source. dotnet format and other tools that don't fully
  // execute generators see them like any other declared attribute,
  // which keeps the build-then-verify-format pipeline clean.

  private static readonly DiagnosticDescriptor _wsaga004NotPartial = new(
    id: "WSAGA004",
    title: "[Saga] type must be partial",
    messageFormat: "The [Saga]-marked type '{0}' must be declared 'partial' so the generator can emit nested event classes into it",
    category: "Whizbang.Sagas",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true);

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    var sagaDecls = context.SyntaxProvider
      .ForAttributeWithMetadataName(
        "Whizbang.Sagas.SagaAttribute",
        predicate: static (node, _) => node is ClassDeclarationSyntax,
        transform: static (ctx, _) => _extractSagaInfo(ctx, hasTypeArg: false))
      .Where(static info => info is not null)
      .Select(static (info, _) => info!);

    var genericDecls = context.SyntaxProvider
      .ForAttributeWithMetadataName(
        "Whizbang.Sagas.SagaAttribute`1",
        predicate: static (node, _) => node is ClassDeclarationSyntax,
        transform: static (ctx, _) => _extractSagaInfo(ctx, hasTypeArg: true))
      .Where(static info => info is not null)
      .Select(static (info, _) => info!);

    var allDecls = sagaDecls.Collect().Combine(genericDecls.Collect());

    context.RegisterSourceOutput(allDecls, (spc, tuple) => {
      var (plain, generic) = tuple;
      foreach (var info in plain) {
        _emit(spc, info);
      }
      foreach (var info in generic) {
        _emit(spc, info);
      }
    });
  }

  private static SagaInfo? _extractSagaInfo(GeneratorAttributeSyntaxContext ctx, bool hasTypeArg) {
    if (ctx.TargetSymbol is not INamedTypeSymbol typeSymbol) {
      return null;
    }
    var classDecl = (ClassDeclarationSyntax)ctx.TargetNode;
    var isPartial = classDecl.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword));

    var attrData = ctx.Attributes.FirstOrDefault();
    if (attrData is null) {
      return null;
    }

    var sagaName = attrData.ConstructorArguments.Length > 0
      ? attrData.ConstructorArguments[0].Value as string
      : null;
    if (string.IsNullOrWhiteSpace(sagaName)) {
      return null;
    }

    bool includeHooks = true, generateService = true;
    foreach (var na in attrData.NamedArguments) {
      if (na.Key == "IncludeHooks") {
        includeHooks = (bool)(na.Value.Value ?? true);
      }
      if (na.Key == "GenerateService") {
        generateService = (bool)(na.Value.Value ?? true);
      }
    }

    string eventBaseFullName = "global::Whizbang.Sagas.SagaEventBase";
    if (hasTypeArg && attrData.AttributeClass is { TypeArguments: { Length: 1 } typeArgs } && typeArgs[0] is INamedTypeSymbol baseSymbol) {
      eventBaseFullName = TypeNameUtilities.FullyQualified(baseSymbol);
    }

    return new SagaInfo(
      @namespace: typeSymbol.ContainingNamespace.IsGlobalNamespace ? null : TypeNameUtilities.Display(typeSymbol.ContainingNamespace),
      className: typeSymbol.Name,
      sagaName: sagaName!,
      eventBaseFullName: eventBaseFullName,
      options: new SagaEmitOptions(includeHooks, generateService, isPartial),
      location: classDecl.Identifier.GetLocation(),
      continuations: _readContinuations(typeSymbol));
  }

  private static void _emit(SourceProductionContext spc, SagaInfo info) {
    if (!info.IsPartial) {
      spc.ReportDiagnostic(Diagnostic.Create(_wsaga004NotPartial, info.Location, info.ClassName));
      return;
    }

    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine("#nullable enable");
    if (info.Namespace is not null) {
      sb.Append("namespace ").Append(info.Namespace).AppendLine(";");
      sb.AppendLine();
    }
    sb.Append("partial class ").Append(info.ClassName).AppendLine(" {");
    sb.Append("  public const string SagaName = \"").Append(info.SagaName).AppendLine("\";");
    sb.AppendLine();

    _emitInitiated(sb, info);
    _emitItemsDispatched(sb, info);
    _emitItemStarted(sb, info);
    _emitItemCompleted(sb, info);
    _emitItemFailed(sb, info);
    _emitCompleted(sb, info);
    _emitReset(sb, info);
    if (info.IncludeHooks) {
      _emitHookStarted(sb, info);
      _emitHookCompleted(sb, info);
    }

    if (info.GenerateService) {
      _emitService(sb, info);
      _emitRecoveryReceptors(sb);
    }

    sb.AppendLine("}");

    if (info.GenerateService) {
      _emitServiceCollectionExtension(sb, info);
    }

    if (info.Continuations.Length > 0) {
      _emitContinuationRegistration(sb, info);
    }

    var fileName = (info.Namespace is null ? "" : info.Namespace + ".") + info.ClassName + ".g.cs";
    spc.AddSource(fileName, SourceText.From(sb.ToString(), Encoding.UTF8));
  }

  // ── Per-event emitters ───────────────────────────────────────────────

  private static void _emitInitiated(StringBuilder sb, SagaInfo info) {
    sb.Append("  public sealed partial class InitiatedEvent : ").Append(info.EventBaseFullName).AppendLine(", global::Whizbang.Sagas.ISagaInitiatedEvent {");
    sb.Append(SAGA_NAME_PROPERTY_PREFIX).Append(info.SagaName).AppendLine("\";");
    sb.AppendLine(ENTITY_ID_PROPERTY);
    sb.AppendLine("    public global::System.Collections.Generic.IReadOnlyList<string> ItemIdentifiers { get; set; } = global::System.Array.Empty<string>();");
    sb.AppendLine("    public int TotalItems { get; set; }");
    sb.AppendLine("    public global::System.Collections.Generic.IReadOnlyList<string>? HookNames { get; set; }");
    sb.AppendLine("  }");
    sb.AppendLine();
  }

  private static void _emitItemsDispatched(StringBuilder sb, SagaInfo info) {
    sb.Append("  public sealed partial class ItemsDispatchedEvent : ").Append(info.EventBaseFullName).AppendLine(", global::Whizbang.Sagas.ISagaItemsDispatchedEvent {");
    sb.Append(SAGA_NAME_PROPERTY_PREFIX).Append(info.SagaName).AppendLine("\";");
    sb.AppendLine(ENTITY_ID_PROPERTY);
    sb.AppendLine("    public int TotalItems { get; set; }");
    sb.AppendLine("    public int SuccessfullyDispatched { get; set; }");
    sb.AppendLine("    public int FailedToDispatch { get; set; }");
    sb.AppendLine("  }");
    sb.AppendLine();
  }

  private static void _emitItemStarted(StringBuilder sb, SagaInfo info) {
    sb.Append("  public sealed partial class ItemStartedEvent : ").Append(info.EventBaseFullName).AppendLine(", global::Whizbang.Sagas.ISagaItemStartedEvent {");
    sb.Append(SAGA_NAME_PROPERTY_PREFIX).Append(info.SagaName).AppendLine("\";");
    sb.AppendLine(ENTITY_ID_PROPERTY);
    sb.AppendLine("    public global::System.Guid SagaId { get; set; }");
    sb.AppendLine(ITEM_IDENTIFIER_PROPERTY);
    sb.AppendLine(DISPLAY_NAME_PROPERTY);
    sb.AppendLine("  }");
    sb.AppendLine();
  }

  private static void _emitItemCompleted(StringBuilder sb, SagaInfo info) {
    sb.Append("  public sealed partial class ItemCompletedEvent : ").Append(info.EventBaseFullName).AppendLine(", global::Whizbang.Sagas.ISagaItemCompletedEvent {");
    sb.Append(SAGA_NAME_PROPERTY_PREFIX).Append(info.SagaName).AppendLine("\";");
    sb.AppendLine(ENTITY_ID_PROPERTY);
    sb.AppendLine("    public global::System.Guid SagaId { get; set; }");
    sb.AppendLine(ITEM_IDENTIFIER_PROPERTY);
    sb.AppendLine(DISPLAY_NAME_PROPERTY);
    sb.AppendLine("  }");
    sb.AppendLine();
  }

  private static void _emitItemFailed(StringBuilder sb, SagaInfo info) {
    sb.Append("  public sealed partial class ItemFailedEvent : ").Append(info.EventBaseFullName).AppendLine(", global::Whizbang.Sagas.ISagaItemFailedEvent {");
    sb.Append(SAGA_NAME_PROPERTY_PREFIX).Append(info.SagaName).AppendLine("\";");
    sb.AppendLine(ENTITY_ID_PROPERTY);
    sb.AppendLine("    public global::System.Guid SagaId { get; set; }");
    sb.AppendLine(ITEM_IDENTIFIER_PROPERTY);
    sb.AppendLine(DISPLAY_NAME_PROPERTY);
    sb.AppendLine("    public string ErrorMessage { get; set; } = \"\";");
    sb.AppendLine("    public string? ErrorDetails { get; set; }");
    sb.AppendLine("  }");
    sb.AppendLine();
  }

  private static void _emitCompleted(StringBuilder sb, SagaInfo info) {
    sb.Append("  public sealed partial class CompletedEvent : ").Append(info.EventBaseFullName).AppendLine(", global::Whizbang.Sagas.ISagaCompletedEvent {");
    sb.Append(SAGA_NAME_PROPERTY_PREFIX).Append(info.SagaName).AppendLine("\";");
    sb.AppendLine(ENTITY_ID_PROPERTY);
    sb.AppendLine("    public global::Whizbang.Sagas.SagaStatus FinalStatus { get; set; }");
    sb.AppendLine("    public string? CompletedByItemIdentifier { get; set; }");
    sb.AppendLine("    public int CompletedItems { get; set; }");
    sb.AppendLine("    public int FailedItems { get; set; }");
    sb.AppendLine("    public int TotalItems { get; set; }");
    sb.AppendLine("  }");
    sb.AppendLine();
  }

  private static void _emitReset(StringBuilder sb, SagaInfo info) {
    sb.Append("  public sealed partial class ResetEvent : ").Append(info.EventBaseFullName).AppendLine(", global::Whizbang.Sagas.ISagaResetEvent {");
    sb.Append(SAGA_NAME_PROPERTY_PREFIX).Append(info.SagaName).AppendLine("\";");
    sb.AppendLine(ENTITY_ID_PROPERTY);
    sb.AppendLine(ITEM_IDENTIFIER_PROPERTY);
    sb.AppendLine("    public global::Whizbang.Sagas.SagaItemState PreviousStatus { get; set; }");
    sb.AppendLine("  }");
    sb.AppendLine();
  }

  private static void _emitHookStarted(StringBuilder sb, SagaInfo info) {
    sb.Append("  public sealed partial class HookStartedEvent : ").Append(info.EventBaseFullName).AppendLine(", global::Whizbang.Sagas.ISagaHookStartedEvent {");
    sb.Append(SAGA_NAME_PROPERTY_PREFIX).Append(info.SagaName).AppendLine("\";");
    sb.AppendLine(ENTITY_ID_PROPERTY);
    sb.AppendLine("    public string HookName { get; set; } = \"\";");
    sb.AppendLine(DISPLAY_NAME_PROPERTY);
    sb.AppendLine("  }");
    sb.AppendLine();
  }

  private static void _emitHookCompleted(StringBuilder sb, SagaInfo info) {
    sb.Append("  public sealed partial class HookCompletedEvent : ").Append(info.EventBaseFullName).AppendLine(", global::Whizbang.Sagas.ISagaHookCompletedEvent {");
    sb.Append(SAGA_NAME_PROPERTY_PREFIX).Append(info.SagaName).AppendLine("\";");
    sb.AppendLine(ENTITY_ID_PROPERTY);
    sb.AppendLine("    public string HookName { get; set; } = \"\";");
    sb.AppendLine(DISPLAY_NAME_PROPERTY);
    sb.AppendLine("    public global::Whizbang.Sagas.SagaItemState Status { get; set; }");
    sb.AppendLine("    public string? ErrorMessage { get; set; }");
    sb.AppendLine("    public string? ErrorDetails { get; set; }");
    sb.AppendLine("  }");
    sb.AppendLine();
  }

  // ── Service class ────────────────────────────────────────────────────

  private static void _emitService(StringBuilder sb, SagaInfo info) {
    sb.AppendLine("  public sealed class Service : global::Whizbang.Sagas.Services.BaseSagaService<");
    sb.AppendLine("    InitiatedEvent, ItemsDispatchedEvent, ItemStartedEvent, ItemCompletedEvent,");
    sb.AppendLine("    ItemFailedEvent, CompletedEvent, ResetEvent, HookStartedEvent, HookCompletedEvent> {");
    sb.AppendLine();
    sb.AppendLine("    public Service(global::Whizbang.Sagas.Services.ISagaEventEmitter emitter,");
    sb.AppendLine("                   global::Microsoft.Extensions.Logging.ILogger<Service> logger)");
    sb.Append("      : base(").Append(info.ClassName).AppendLine(".SagaName, emitter, logger) { }");
    sb.AppendLine();
    sb.AppendLine("    protected override InitiatedEvent BuildInitiatedEvent(global::Whizbang.Sagas.SagaContext ctx, global::System.Collections.Generic.IReadOnlyList<string> itemIdentifiers, global::System.Collections.Generic.IReadOnlyList<string>? hookNames, global::System.DateTimeOffset sentAt) =>");
    sb.AppendLine("      new() { EntityId = ctx.EntityId, ItemIdentifiers = itemIdentifiers, TotalItems = itemIdentifiers.Count, HookNames = hookNames };");
    sb.AppendLine();
    sb.AppendLine("    protected override ItemsDispatchedEvent BuildItemsDispatchedEvent(global::Whizbang.Sagas.SagaContext ctx, int totalItems, int successfullyDispatched, int failedToDispatch, global::System.DateTimeOffset sentAt) =>");
    sb.AppendLine("      new() { EntityId = ctx.EntityId, TotalItems = totalItems, SuccessfullyDispatched = successfullyDispatched, FailedToDispatch = failedToDispatch };");
    sb.AppendLine();
    sb.AppendLine("    protected override ItemStartedEvent BuildItemStartedEvent(global::Whizbang.Sagas.SagaContext ctx, string itemIdentifier, string? displayName, global::System.DateTimeOffset sentAt) =>");
    sb.AppendLine("      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };");
    sb.AppendLine();
    sb.AppendLine("    protected override ItemCompletedEvent BuildItemCompletedEvent(global::Whizbang.Sagas.SagaContext ctx, string itemIdentifier, string? displayName, global::System.DateTimeOffset sentAt) =>");
    sb.AppendLine("      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };");
    sb.AppendLine();
    sb.AppendLine("    protected override ItemFailedEvent BuildItemFailedEvent(global::Whizbang.Sagas.SagaContext ctx, string itemIdentifier, string errorMessage, string? errorDetails, string? displayName, global::System.DateTimeOffset sentAt) =>");
    sb.AppendLine("      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName, ErrorMessage = errorMessage, ErrorDetails = errorDetails };");
    sb.AppendLine();
    sb.AppendLine("    protected override CompletedEvent BuildCompletedEvent(global::Whizbang.Sagas.SagaContext ctx, global::Whizbang.Sagas.SagaStatus finalStatus, string? completedByItemIdentifier, int completedItems, int failedItems, int totalItems, global::System.DateTimeOffset sentAt) =>");
    sb.AppendLine("      new() { EntityId = ctx.EntityId, FinalStatus = finalStatus, CompletedByItemIdentifier = completedByItemIdentifier, CompletedItems = completedItems, FailedItems = failedItems, TotalItems = totalItems };");
    sb.AppendLine();
    sb.AppendLine("    protected override ResetEvent BuildResetEvent(global::Whizbang.Sagas.SagaContext ctx, string itemIdentifier, global::Whizbang.Sagas.SagaItemState previousStatus, global::System.DateTimeOffset sentAt) =>");
    sb.AppendLine("      new() { EntityId = ctx.EntityId, ItemIdentifier = itemIdentifier, PreviousStatus = previousStatus };");
    sb.AppendLine();
    if (info.IncludeHooks) {
      sb.AppendLine("    protected override HookStartedEvent BuildHookStartedEvent(global::Whizbang.Sagas.SagaContext ctx, string hookName, string? displayName, global::System.DateTimeOffset sentAt) =>");
      sb.AppendLine("      new() { EntityId = ctx.EntityId, HookName = hookName, DisplayName = displayName };");
      sb.AppendLine();
      sb.AppendLine("    protected override HookCompletedEvent BuildHookCompletedEvent(global::Whizbang.Sagas.SagaContext ctx, string hookName, global::Whizbang.Sagas.SagaItemState status, string? errorMessage, string? errorDetails, global::System.DateTimeOffset sentAt) =>");
      sb.AppendLine("      new() { EntityId = ctx.EntityId, HookName = hookName, Status = status, ErrorMessage = errorMessage, ErrorDetails = errorDetails };");
    } else {
      sb.AppendLine("    protected override HookStartedEvent BuildHookStartedEvent(global::Whizbang.Sagas.SagaContext ctx, string hookName, string? displayName, global::System.DateTimeOffset sentAt) =>");
      sb.AppendLine("      throw new global::System.InvalidOperationException(\"This saga was generated with IncludeHooks = false.\");");
      sb.AppendLine();
      sb.AppendLine("    protected override HookCompletedEvent BuildHookCompletedEvent(global::Whizbang.Sagas.SagaContext ctx, string hookName, global::Whizbang.Sagas.SagaItemState status, string? errorMessage, string? errorDetails, global::System.DateTimeOffset sentAt) =>");
      sb.AppendLine("      throw new global::System.InvalidOperationException(\"This saga was generated with IncludeHooks = false.\");");
    }
    sb.AppendLine("  }");
  }

  /// <summary>
  /// Emits the three recovery receptors that bridge per-item terminal events
  /// and the auto-armed watchdog tick to the framework-owned recovery path
  /// (<c>BaseSagaService.TryRecoverViaWatchdogAsync</c> + the
  /// <c>TryRecoverViaWatchdogTickAsync</c> re-arm-or-abandon lifecycle). Under
  /// multi-pod fan-out the framework's in-memory completion tracker is per-pod
  /// sharded and can never reach Total alone; the per-item terminal handlers
  /// nudge recovery on every terminal event so the last item across all pods
  /// drives <c>SagaCompletedEvent</c> via <c>PublishOnceAsync</c>. The watchdog
  /// tick handler is the safety net the framework re-arms with backoff (and
  /// eventually abandons via <c>SagaCompletionAbandonedEvent</c>).
  /// </summary>
  private static void _emitRecoveryReceptors(StringBuilder sb) {
    sb.AppendLine();
    sb.AppendLine("  /// <summary>Bridges every per-item completed terminal to the framework's recovery path so the last completing item drives SagaCompletedEvent under multi-pod fan-out.</summary>");
    sb.AppendLine("  [global::Whizbang.Core.Messaging.FireAt(global::Whizbang.Core.Messaging.LifecycleStage.PostAllPerspectivesInline)]");
    sb.AppendLine("  public sealed class SagaItemCompletedRecoveryHandler(Service _svc) : global::Whizbang.Core.IReceptor<ItemCompletedEvent> {");
    sb.AppendLine("    public async global::System.Threading.Tasks.ValueTask HandleAsync(ItemCompletedEvent @event, global::System.Threading.CancellationToken ct) {");
    sb.AppendLine("      if (@event.SagaName != SagaName) return;");
    sb.AppendLine("      var ctx = new global::Whizbang.Sagas.SagaContext(@event.SagaId, @event.EntityId);");
    sb.AppendLine("      await _svc.TryRecoverViaWatchdogAsync(ctx, ct).ConfigureAwait(false);");
    sb.AppendLine("    }");
    sb.AppendLine("  }");

    sb.AppendLine();
    sb.AppendLine("  /// <summary>Bridges every per-item failed terminal to the framework's recovery path; mirrors <see cref=\"SagaItemCompletedRecoveryHandler\"/> for failed items.</summary>");
    sb.AppendLine("  [global::Whizbang.Core.Messaging.FireAt(global::Whizbang.Core.Messaging.LifecycleStage.PostAllPerspectivesInline)]");
    sb.AppendLine("  public sealed class SagaItemFailedRecoveryHandler(Service _svc) : global::Whizbang.Core.IReceptor<ItemFailedEvent> {");
    sb.AppendLine("    public async global::System.Threading.Tasks.ValueTask HandleAsync(ItemFailedEvent @event, global::System.Threading.CancellationToken ct) {");
    sb.AppendLine("      if (@event.SagaName != SagaName) return;");
    sb.AppendLine("      var ctx = new global::Whizbang.Sagas.SagaContext(@event.SagaId, @event.EntityId);");
    sb.AppendLine("      await _svc.TryRecoverViaWatchdogAsync(ctx, ct).ConfigureAwait(false);");
    sb.AppendLine("    }");
    sb.AppendLine("  }");

    sb.AppendLine();
    sb.AppendLine("  /// <summary>Drives the auto-armed watchdog tick through its re-arm / abandon lifecycle via <see cref=\"global::Whizbang.Sagas.Services.BaseSagaService{TInit,TItemsDispatched,TItemStarted,TItemCompleted,TItemFailed,TCompleted,TReset,THookStarted,THookCompleted}.TryRecoverViaWatchdogTickAsync\"/>.</summary>");
    sb.AppendLine("  public sealed class SagaCompletionWatchdogTickHandler(Service _svc) : global::Whizbang.Core.IReceptor<global::Whizbang.Sagas.SagaCompletionWatchdogTickEvent> {");
    sb.AppendLine("    public async global::System.Threading.Tasks.ValueTask HandleAsync(global::Whizbang.Sagas.SagaCompletionWatchdogTickEvent @event, global::System.Threading.CancellationToken ct) {");
    sb.AppendLine("      if (@event.SagaName != SagaName) return;");
    sb.AppendLine("      await _svc.TryRecoverViaWatchdogTickAsync(@event, ct).ConfigureAwait(false);");
    sb.AppendLine("    }");
    sb.AppendLine("  }");
  }

  /// <summary>
  /// The <c>[ContinuesWith]</c> declarations on a saga class, in source order.
  /// </summary>
  /// <remarks>
  /// Read from the class symbol rather than the generator's attribute context, which holds only the
  /// <c>[Saga]</c> attribute that triggered this generator.
  /// </remarks>
  private static ImmutableArray<ContinuationDeclaration> _readContinuations(INamedTypeSymbol typeSymbol) {
    var declarations = ImmutableArray.CreateBuilder<ContinuationDeclaration>();

    foreach (var attribute in typeSymbol.GetAttributes()) {
      if (!TypeNameUtilities.IsNamed(attribute.AttributeClass, CONTINUES_WITH_ATTRIBUTE)) {
        continue;
      }
      if (attribute.ConstructorArguments.Length == 0
          || attribute.ConstructorArguments[0].Value is not string name
          || string.IsNullOrWhiteSpace(name)) {
        continue;   // a blank name is the attribute's own argument check, not this generator's
      }

      var trigger = attribute.ConstructorArguments.Length > 1
                    && attribute.ConstructorArguments[1].Value is int declared
        ? declared
        : RAN_TO_THE_END;

      declarations.Add(new ContinuationDeclaration(name, trigger));
    }

    return declarations.ToImmutable();
  }

  /// <summary>
  /// Emits the registration that puts this saga's chain in the runtime registry.
  /// </summary>
  /// <remarks>
  /// A module initializer rather than the DI extension, so the chain is registered even for a saga
  /// generated with <c>GenerateService = false</c>, and so it does not depend on the host remembering
  /// to call an Add method. This is the same shape <c>SagasJsonContextInitializer</c> uses to register
  /// the framework's own serialization context.
  /// </remarks>
  private static void _emitContinuationRegistration(StringBuilder sb, SagaInfo info) {
    sb.AppendLine();
    sb.Append("internal static class ").Append(info.ClassName).AppendLine("ContinuationRegistration {");
    sb.AppendLine("  [global::System.Runtime.CompilerServices.ModuleInitializer]");
    sb.AppendLine("  internal static void RegisterContinuations() {");

    foreach (var continuation in info.Continuations) {
      sb.AppendLine("    global::Whizbang.Sagas.SagaContinuationRegistry.Register(");
      sb.Append("      \"").Append(info.SagaName).AppendLine("\",");
      sb.Append("      new global::Whizbang.Sagas.SagaContinuation(\"").Append(continuation.SagaName).Append("\", ")
        .Append(_renderTrigger(continuation.Trigger)).AppendLine("));");
    }

    sb.AppendLine("  }");
    sb.AppendLine("}");
  }

  /// <summary>
  /// The trigger as named flags, so the generated call reads like the declaration it came from.
  /// </summary>
  private static string _renderTrigger(int trigger) {
    var names = new List<string>();
    if ((trigger & 1) != 0) { names.Add("Completed"); }
    if ((trigger & 2) != 0) { names.Add("CompletedWithFailures"); }
    if ((trigger & 4) != 0) { names.Add("Failed"); }
    if (names.Count == 0) { names.Add("None"); }

    return string.Join(" | ", names.Select(static n => "global::Whizbang.Sagas.SagaContinuationTriggers." + n));
  }

  private static void _emitServiceCollectionExtension(StringBuilder sb, SagaInfo info) {
    sb.AppendLine();
    sb.Append("public static class ").Append(info.ClassName).AppendLine("ServiceCollectionExtensions {");
    sb.Append("  public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection Add").Append(info.ClassName).AppendLine("(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services) =>");
    sb.Append("    global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<").Append(info.Namespace is null ? "" : info.Namespace + ".").Append(info.ClassName).AppendLine(".Service>(services);");
    sb.AppendLine("}");
  }

  /// <summary>One <c>[ContinuesWith]</c> declaration read off a saga class.</summary>
  private readonly struct ContinuationDeclaration(string sagaName, int trigger) {
    public string SagaName { get; } = sagaName;
    public int Trigger { get; } = trigger;
  }

  /// <summary>
  /// What the generator was asked to emit, as opposed to what the saga is called.
  /// </summary>
  /// <remarks>
  /// Grouped rather than passed alongside the names because they answer a different question, and
  /// because a constructor that keeps growing one flag at a time is how a parameter list reaches the
  /// point where call sites stop being readable.
  /// </remarks>
  private readonly struct SagaEmitOptions(bool includeHooks, bool generateService, bool isPartial) {
    public bool IncludeHooks { get; } = includeHooks;
    public bool GenerateService { get; } = generateService;
    public bool IsPartial { get; } = isPartial;
  }

  private sealed class SagaInfo(
      string? @namespace,
      string className,
      string sagaName,
      string eventBaseFullName,
      SagaEmitOptions options,
      Location location,
      ImmutableArray<ContinuationDeclaration> continuations) {
    public ImmutableArray<ContinuationDeclaration> Continuations { get; } = continuations;
    public string? Namespace { get; } = @namespace;
    public string ClassName { get; } = className;
    public string SagaName { get; } = sagaName;
    public string EventBaseFullName { get; } = eventBaseFullName;
    public bool IncludeHooks => options.IncludeHooks;
    public bool GenerateService => options.GenerateService;
    public bool IsPartial => options.IsPartial;
    public Location Location { get; } = location;
  }
}

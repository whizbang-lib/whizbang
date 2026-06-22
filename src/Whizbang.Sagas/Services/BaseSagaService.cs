using Microsoft.Extensions.Logging;
using Whizbang.Sagas.Helpers;

namespace Whizbang.Sagas.Services;

/// <summary>
/// Abstract base for per-saga lifecycle services. Consumers (or the
/// <c>[Saga&lt;TBase&gt;("Name")]</c> source generator) provide concrete
/// event types via the nine generic type parameters; concrete
/// subclasses fill in the <c>Build*Event</c> factory methods that
/// construct those types.
/// </summary>
/// <remarks>
/// <para>
/// Publishing each event as a derived runtime type makes Whizbang's
/// message registry route the event only to the projections that
/// subscribed to that specific type — the saga-name dispatch fan-out
/// is type-narrowed instead of relying on every projection ignoring
/// events with the wrong <see cref="ISagaEvent.SagaName"/>.
/// </para>
/// <para>
/// The factory pattern (<c>BuildInitiatedEvent</c>, etc.) avoids the
/// <c>new TInit()</c> + property-set sequence the
/// <c>new()</c> generic constraint would otherwise force; required
/// members and complex constructors all work cleanly.
/// </para>
/// <para>
/// Terminal completion uses
/// <see cref="ISagaEventEmitter.PublishOnceAsync"/> with the
/// <see cref="SagaCompletionGuard"/> claim-key convention so N concurrent
/// terminal handlers collapse to exactly one emission. All other
/// lifecycle events use <see cref="ISagaEventEmitter.PublishAsync"/>.
/// </para>
/// </remarks>
public abstract partial class BaseSagaService<TInit, TItemsDispatched, TItemStarted, TItemCompleted, TItemFailed, TCompleted, TReset, THookStarted, THookCompleted>
  where TInit : class, ISagaInitiatedEvent
  where TItemsDispatched : class, ISagaItemsDispatchedEvent
  where TItemStarted : class, ISagaItemStartedEvent
  where TItemCompleted : class, ISagaItemCompletedEvent
  where TItemFailed : class, ISagaItemFailedEvent
  where TCompleted : class, ISagaCompletedEvent
  where TReset : class, ISagaResetEvent
  where THookStarted : class, ISagaHookStartedEvent
  where THookCompleted : class, ISagaHookCompletedEvent {

  private readonly string _sagaName;
  private readonly ISagaEventEmitter _emitter;
  private readonly ILogger _logger;

  /// <summary>The saga name this service emits events for — matches the value supplied to <c>[Saga("Name")]</c>.</summary>
  protected string SagaName => _sagaName;

  protected BaseSagaService(string sagaName, ISagaEventEmitter emitter, ILogger logger) {
    ArgumentException.ThrowIfNullOrWhiteSpace(sagaName);
    _sagaName = sagaName;
    _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  // ── Factory methods (consumer or generator fills in) ─────────────────

  protected abstract TInit BuildInitiatedEvent(SagaContext ctx, IReadOnlyList<string> itemIdentifiers, IReadOnlyList<string>? hookNames, DateTimeOffset sentAt);
  protected abstract TItemsDispatched BuildItemsDispatchedEvent(SagaContext ctx, int totalItems, int successfullyDispatched, int failedToDispatch, DateTimeOffset sentAt);
  protected abstract TItemStarted BuildItemStartedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt);
  protected abstract TItemCompleted BuildItemCompletedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt);
  protected abstract TItemFailed BuildItemFailedEvent(SagaContext ctx, string itemIdentifier, string errorMessage, string? errorDetails, string? displayName, DateTimeOffset sentAt);
  protected abstract TCompleted BuildCompletedEvent(SagaContext ctx, SagaStatus finalStatus, string? completedByItemIdentifier, int completedItems, int failedItems, int totalItems, DateTimeOffset sentAt);
  protected abstract TReset BuildResetEvent(SagaContext ctx, string itemIdentifier, SagaItemState previousStatus, DateTimeOffset sentAt);
  protected abstract THookStarted BuildHookStartedEvent(SagaContext ctx, string hookName, string? displayName, DateTimeOffset sentAt);
  protected abstract THookCompleted BuildHookCompletedEvent(SagaContext ctx, string hookName, SagaItemState status, string? errorMessage, string? errorDetails, DateTimeOffset sentAt);

  // ── Lifecycle methods ────────────────────────────────────────────────

  public async Task InitiateSagaAsync(SagaContext ctx, IReadOnlyList<string> itemIdentifiers, IReadOnlyList<string>? hookNames, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(itemIdentifiers);
    cancellationToken.ThrowIfCancellationRequested();
    var evt = BuildInitiatedEvent(ctx, itemIdentifiers, hookNames, DateTimeOffset.UtcNow);
    await _emitter.PublishAsync(evt).ConfigureAwait(false);
  }

  public async Task ItemsDispatchedAsync(SagaContext ctx, int totalItems, int successfullyDispatched, int failedToDispatch, CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();
    var evt = BuildItemsDispatchedEvent(ctx, totalItems, successfullyDispatched, failedToDispatch, DateTimeOffset.UtcNow);
    await _emitter.PublishAsync(evt).ConfigureAwait(false);
  }

  public async Task UpdateItemAsync(SagaContext ctx, string itemIdentifier, SagaItemState newStatus, string? displayName, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(itemIdentifier);
    cancellationToken.ThrowIfCancellationRequested();
    var now = DateTimeOffset.UtcNow;
    switch (newStatus) {
      case SagaItemState.Running:
        await _emitter.PublishAsync(BuildItemStartedEvent(ctx, itemIdentifier, displayName, now)).ConfigureAwait(false);
        break;
      case SagaItemState.Completed:
        await _emitter.PublishAsync(BuildItemCompletedEvent(ctx, itemIdentifier, displayName, now)).ConfigureAwait(false);
        break;
      case SagaItemState.Failed:
        throw new InvalidOperationException(
          "Use FailItemAsync to fail an item; UpdateItemAsync does not carry error context.");
      default:
        throw new InvalidOperationException(
          $"UpdateItemAsync only supports Running and Completed transitions. Got: {newStatus}");
    }
  }

  public async Task FailItemAsync(SagaContext ctx, string itemIdentifier, string errorMessage, string? errorDetails, string? displayName, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(itemIdentifier);
    ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
    cancellationToken.ThrowIfCancellationRequested();
    await _emitter.PublishAsync(
      BuildItemFailedEvent(ctx, itemIdentifier, errorMessage, errorDetails, displayName, DateTimeOffset.UtcNow))
      .ConfigureAwait(false);
  }

  /// <summary>
  /// Emits the saga's terminal completion event exactly once — routes
  /// through <see cref="ISagaEventEmitter.PublishOnceAsync"/> with the
  /// saga claim-key convention so concurrent terminal handlers collapse
  /// to a single emission.
  /// </summary>
  /// <returns><c>true</c> if this caller won the claim and the event was published; <c>false</c> if another caller already won it.</returns>
  public async Task<bool> CompleteSagaAsync(SagaContext ctx, SagaStatus finalStatus, string? completedByItemIdentifier, int completedItems, int failedItems, int totalItems, CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();
    var evt = BuildCompletedEvent(ctx, finalStatus, completedByItemIdentifier, completedItems, failedItems, totalItems, DateTimeOffset.UtcNow);
    var claimKey = SagaCompletionGuard.ClaimKey(_sagaName, ctx.SagaId);
    return await _emitter.PublishOnceAsync(claimKey, evt, cancellationToken).ConfigureAwait(false);
  }

  public async Task ResetItemAsync(SagaContext ctx, string itemIdentifier, SagaItemState previousStatus, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(itemIdentifier);
    cancellationToken.ThrowIfCancellationRequested();
    var evt = BuildResetEvent(ctx, itemIdentifier, previousStatus, DateTimeOffset.UtcNow);
    await _emitter.PublishAsync(evt).ConfigureAwait(false);
  }

  /// <summary>
  /// Runs <paramref name="work"/> bracketed by Hook Started and Completed
  /// events. Skips silently (returns <c>false</c>) when the hook has
  /// already terminated on <paramref name="sagaProjection"/> — relies on
  /// projection-side <c>Hooks</c> state for dedup. Returns <c>true</c>
  /// when the work actually ran.
  /// </summary>
  /// <remarks>
  /// Re-throws if <paramref name="work"/> throws — but publishes a
  /// Hook Completed (Failed) event first so the projection records the
  /// failure even when the receptor's transaction rolls back.
  /// </remarks>
  public async Task<bool> TryRunHookAsync(
      SagaContext ctx,
      Models.BaseSagaModel? sagaProjection,
      string hookName,
      string? displayName,
      Func<CancellationToken, Task> work,
      CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(hookName);
    ArgumentNullException.ThrowIfNull(work);
    cancellationToken.ThrowIfCancellationRequested();

    if (sagaProjection?.Hooks.FirstOrDefault(h => h.HookName == hookName) is { IsTerminal: true }) {
      LogHookSkipped(_logger, hookName, _sagaName, ctx.SagaId, null);
      return false;
    }

    var now = DateTimeOffset.UtcNow;
    await _emitter.PublishAsync(BuildHookStartedEvent(ctx, hookName, displayName, now)).ConfigureAwait(false);

    try {
      await work(cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) {
      LogHookFailed(_logger, hookName, _sagaName, ctx.SagaId, ex);
      await _emitter.PublishAsync(
        BuildHookCompletedEvent(ctx, hookName, SagaItemState.Failed, ex.Message, ex.ToString(), DateTimeOffset.UtcNow))
        .ConfigureAwait(false);
      throw;
    }

    await _emitter.PublishAsync(
      BuildHookCompletedEvent(ctx, hookName, SagaItemState.Completed, null, null, DateTimeOffset.UtcNow))
      .ConfigureAwait(false);

    LogHookCompleted(_logger, hookName, _sagaName, ctx.SagaId, null);
    return true;
  }

  // ── LoggerMessage source-gen partials ────────────────────────────────

  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Hook {HookName} already terminal on saga {SagaName} {SagaId} — skip")]
  private static partial void LogHookSkipped(ILogger logger, string HookName, string SagaName, Guid SagaId, Exception? exception);

  [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Hook {HookName} failed on saga {SagaName} {SagaId} — publishing Failed completion before rethrow")]
  private static partial void LogHookFailed(ILogger logger, string HookName, string SagaName, Guid SagaId, Exception? exception);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Hook {HookName} completed on saga {SagaName} {SagaId}")]
  private static partial void LogHookCompleted(ILogger logger, string HookName, string SagaName, Guid SagaId, Exception? exception);
}

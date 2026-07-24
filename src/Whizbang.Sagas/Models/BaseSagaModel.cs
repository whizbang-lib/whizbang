namespace Whizbang.Sagas.Models;

/// <summary>
/// Base read-model for sagas — manages the saga-level state machine
/// (<see cref="SagaStatus"/>), per-item counts, and hook executions.
/// Every consumer projection inherits this and adds Apply methods for
/// domain events.
/// </summary>
/// <remarks>
/// <para>
/// Structural inheritance only. The state-machine methods
/// (<see cref="MarkRunningIfPending"/>, <see cref="TryComplete"/>,
/// <see cref="TryFailFast"/>, <see cref="UpdateTotalItems"/>) are pure
/// transitions safe to call from inside Apply — they read and write
/// instance state only, no I/O.
/// </para>
/// <para>
/// <c>GetItems</c> is virtual so consumer projections that embed their
/// own per-item data can override and surface it. The default
/// implementation returns an empty list, which is correct for sagas
/// that track only counters (no per-item embed in the saga row).
/// </para>
/// </remarks>
public class BaseSagaModel {

  /// <summary>Saga stream id.</summary>
  public Guid Id { get; set; }

  /// <summary>Saga name (matches the value passed to <c>[Saga("Name")]</c>).</summary>
  public string SagaName { get; set; } = string.Empty;

  /// <summary>Consumer-domain entity id (tenant id, operation id, …) carried for filtering.</summary>
  public Guid? EntityId { get; set; }

  /// <summary>Current saga status.</summary>
  public SagaStatus Status { get; set; } = SagaStatus.Pending;

  /// <summary>Total items the saga will track.</summary>
  public int TotalItems { get; set; }

  /// <summary>Items in <see cref="SagaItemState.Completed"/>.</summary>
  public int CompletedItems { get; set; }

  /// <summary>Items in <see cref="SagaItemState.Failed"/>.</summary>
  public int FailedItems { get; set; }

  /// <summary>
  /// Identifier of the item whose terminal event triggered the saga's
  /// completion. Audit aid — traces which item "ended" the saga.
  /// </summary>
  public string? CompletedByItemIdentifier { get; set; }

  /// <summary>
  /// True once a terminal saga event has been applied to this projection.
  /// Set by Apply(SagaCompletedEvent); checked by completion handlers as
  /// defense-in-depth against the duplicate-emission race that
  /// <c>SagaCompletionGuard</c> now closes at the dispatcher level via
  /// <c>PublishOnceAsync</c>.
  /// </summary>
  public bool CompletionEventDispatched { get; set; }

  /// <summary>Optional human-readable summary surfaced by UI.</summary>
  public string? Summary { get; set; }

  /// <summary>Wall-clock when the row was first inserted.</summary>
  public DateTimeOffset CreatedAt { get; set; }

  /// <summary>Wall-clock of the last applied event.</summary>
  public DateTimeOffset UpdatedAt { get; set; }

  /// <summary>Wall-clock when the saga first transitioned to <see cref="SagaStatus.Running"/>.</summary>
  public DateTimeOffset? StartedAt { get; set; }

  /// <summary>Wall-clock when the saga reached a terminal status.</summary>
  public DateTimeOffset? CompletedAt { get; set; }

  // ── Hooks (lazy init for pre-Rule-17 row backward compat) ────────────

  private List<SagaHookExecution>? _hooks;

  /// <summary>
  /// Framework-managed pre/post-work hook executions. Lazy-init coerces a
  /// missing JSONB key into an empty list for backward compatibility with
  /// pre-Rule-17 rows.
  /// </summary>
  public List<SagaHookExecution> Hooks {
    get => _hooks ??= [];
    set => _hooks = value;
  }

  // ── State transitions ────────────────────────────────────────────────

  /// <summary>
  /// Transitions <see cref="SagaStatus.Pending"/> to <see cref="SagaStatus.Running"/>
  /// idempotently. Called when the first item starts processing.
  /// </summary>
  /// <remarks>
  /// Subsequent calls (saga already Running, or terminal) are no-ops. In
  /// particular, this does NOT move <see cref="StartedAt"/> forward — that
  /// value records the first transition only.
  /// </remarks>
  public void MarkRunningIfPending(DateTimeOffset timestamp) {
    if (Status != SagaStatus.Pending) {
      return;
    }
    Status = SagaStatus.Running;
    StartedAt = timestamp;
  }

  /// <summary>
  /// Attempts to transition <see cref="SagaStatus.Running"/> to a terminal
  /// completion state. Returns <c>true</c> iff the transition fired.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Returns <c>false</c> when: status isn't Running; <see cref="TotalItems"/>
  /// is non-positive (e.g. downstream saga awaiting an UpdateTotalItems);
  /// or completed + failed counts haven't reached TotalItems.
  /// </para>
  /// <para>
  /// Final status is <see cref="SagaStatus.Completed"/> when all items
  /// succeeded; <see cref="SagaStatus.CompletedWithFailures"/> when any
  /// item failed but the saga ran to the end.
  /// </para>
  /// </remarks>
  public bool TryComplete(string triggeringItemIdentifier, DateTimeOffset timestamp) {
    if (Status != SagaStatus.Running) {
      return false;
    }
    if (TotalItems <= 0) {
      return false;
    }
    if ((CompletedItems + FailedItems) < TotalItems) {
      return false;
    }

    Status = FailedItems > 0 ? SagaStatus.CompletedWithFailures : SagaStatus.Completed;
    CompletedAt = timestamp;
    CompletedByItemIdentifier = triggeringItemIdentifier;
    return true;
  }

  /// <summary>
  /// Attempts to fail-fast — transitions <see cref="SagaStatus.Running"/>
  /// directly to <see cref="SagaStatus.Failed"/> without waiting for the
  /// remaining items. Used by sagas where partial completion would leave
  /// the system in an unrecoverable state (e.g. embedding pipelines).
  /// Returns <c>true</c> iff the transition fired.
  /// </summary>
  public bool TryFailFast(string triggeringItemIdentifier, DateTimeOffset timestamp) {
    if (Status != SagaStatus.Running) {
      return false;
    }

    Status = SagaStatus.Failed;
    CompletedAt = timestamp;
    CompletedByItemIdentifier = triggeringItemIdentifier;
    return true;
  }

  /// <summary>
  /// Updates the total-items count, typically by a downstream saga
  /// receiving the real count from an upstream completion. The
  /// <see cref="TryComplete"/> guard on <see cref="TotalItems"/> &lt;= 0
  /// prevents premature completion before this fires.
  /// </summary>
  public void UpdateTotalItems(int totalItems, DateTimeOffset timestamp) {
    TotalItems = totalItems;
    UpdatedAt = timestamp;
  }

  // ── Item / hook surface for downstream consumers ─────────────────────

  /// <summary>
  /// Returns per-item detail for dashboard rendering. Concrete sagas
  /// override to return their embedded items (the field-context,
  /// section-context, etc. lists that implement <see cref="ISagaItem"/>).
  /// Sagas that only track counters return an empty list.
  /// </summary>
  /// <remarks>
  /// CONTRACT: the returned list is a LIVE REFERENCE to the projection's
  /// internal storage. Callers must not mutate it. The return type
  /// <see cref="IReadOnlyList{T}"/> expresses that contract, not
  /// underlying immutability.
  /// </remarks>
  public virtual IReadOnlyList<ISagaItem> GetItems() => [];

  /// <summary>
  /// Returns hook execution records for dashboard rendering. Live
  /// reference to <see cref="Hooks"/> — same read-only contract as
  /// <see cref="GetItems"/>.
  /// </summary>
  public virtual IReadOnlyList<SagaHookExecution> GetHooks() => Hooks;
}

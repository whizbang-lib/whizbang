using System.Threading;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Per-work disposable that surfaces a <see cref="CancellationToken"/> tied to a lease deadline.
/// Phase H step 9.
/// </summary>
/// <remarks>
/// <para>
/// Every dispatch path in the work-pump (inbox handler, perspective apply, outbox publish) holds
/// a row's lease for some duration (default 5 min). Pre-step-9 the dispatch passed only the
/// worker's <c>stoppingToken</c> to inner operations — there was no per-row deadline, so a hung
/// handler parked the FIFO line until the lease expired naturally (only happens if the
/// LeaseRenewalWorker stops renewing — which it doesn't on its own without slice 6's cap).
/// </para>
/// <para>
/// The handle's <see cref="Token"/> is linked from:
/// </para>
/// <list type="bullet">
/// <item><description>An internal CTS that fires at <c>deadline</c> (lease_expiry minus a configurable grace).</description></item>
/// <item><description>The caller-provided linked tokens (typically the worker's <c>stoppingToken</c>) so shutdown propagates.</description></item>
/// </list>
/// <para>
/// <see cref="TryExtendDeadline"/> pushes the deadline out (called by LeaseRenewalWorker after a
/// successful DB lease renewal) and increments <see cref="RenewalCount"/>. After
/// <c>maxRenewals</c> extensions, further calls return <c>false</c> — this is the "stop renewing
/// hung handlers" cap that surfaces them via the standard failure path. Disposal cancels the
/// token (so any stragglers awaiting it see cancellation) and is idempotent.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/lease-cancellation</docs>
public sealed class LeaseHandle : IDisposable {
  private readonly TimeProvider _timeProvider;
  private readonly int _maxRenewals;
  private readonly CancellationTokenSource _deadlineCts;
  private readonly CancellationTokenSource _linkedCts;
  // Cache the linked token at construction so post-Dispose reads of Token still work
  // (CancellationToken is a struct that holds a reference to the source — IsCancellationRequested
  // remains queryable even after the source is disposed). Without this cache,
  // _linkedCts.Token would throw ObjectDisposedException after Dispose.
  private readonly CancellationToken _linkedToken;
  private readonly Lock _gate = new();
  private int _renewalCount;
  private bool _disposed;

  /// <summary>
  /// Creates a new lease handle.
  /// </summary>
  /// <param name="workId">Identifier of the work item this lease covers (message_id, event_work_id, etc.).</param>
  /// <param name="category">Which work table this lease belongs to.</param>
  /// <param name="deadline">Wall-clock instant when the token cancels (typically lease_expiry minus a grace period).</param>
  /// <param name="maxRenewals">Hard cap on <see cref="TryExtendDeadline"/> calls before further renewals are refused.</param>
  /// <param name="timeProvider">Time source — pass <see cref="TimeProvider.System"/> in production or a fake in tests. <see cref="CancellationTokenSource"/> uses this provider's clock for the deadline timer so fake-clock advance reliably fires cancellation.</param>
  /// <param name="linkedTokens">Tokens to link with the deadline CTS (e.g., worker stoppingToken).</param>
  public LeaseHandle(
    Guid workId,
    WorkCategory category,
    DateTimeOffset deadline,
    int maxRenewals,
    TimeProvider timeProvider,
    IReadOnlyList<CancellationToken> linkedTokens) {
    ArgumentNullException.ThrowIfNull(timeProvider);
    ArgumentNullException.ThrowIfNull(linkedTokens);
    if (maxRenewals < 0) {
      throw new ArgumentOutOfRangeException(nameof(maxRenewals), "maxRenewals must be >= 0.");
    }

    WorkId = workId;
    Category = category;
    _timeProvider = timeProvider;
    _maxRenewals = maxRenewals;

    var initialDelay = deadline - timeProvider.GetUtcNow();
    if (initialDelay <= TimeSpan.Zero) {
      _deadlineCts = new CancellationTokenSource();
      _deadlineCts.Cancel();
    } else {
      // CancellationTokenSource(TimeSpan, TimeProvider) — uses the provider's clock for the
      // internal timer, so FakeTimeProvider.Advance reliably fires cancellation in tests.
      _deadlineCts = new CancellationTokenSource(initialDelay, timeProvider);
    }

    if (linkedTokens.Count == 0) {
      _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_deadlineCts.Token);
    } else {
      var sources = new CancellationToken[linkedTokens.Count + 1];
      sources[0] = _deadlineCts.Token;
      for (var i = 0; i < linkedTokens.Count; i++) {
        sources[i + 1] = linkedTokens[i];
      }
      _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(sources);
    }
    _linkedToken = _linkedCts.Token;
  }

  /// <summary>Identifier of the work item this lease covers.</summary>
  public Guid WorkId { get; }

  /// <summary>Which work table this lease belongs to.</summary>
  public WorkCategory Category { get; }

  /// <summary>Cancellation token that fires at deadline, on shutdown, or on dispose.</summary>
  public CancellationToken Token => _linkedToken;

  /// <summary>Number of successful <see cref="TryExtendDeadline"/> calls.</summary>
  public int RenewalCount {
    get {
      lock (_gate) {
        return _renewalCount;
      }
    }
  }

  /// <summary>
  /// Pushes the deadline out to <paramref name="newDeadline"/>. Returns <c>true</c> on success,
  /// <c>false</c> when the cap has been hit or the handle has been disposed. Called by
  /// <see cref="LeaseRenewalWorker"/> after the SQL lease has been renewed so the in-process CT
  /// deadline tracks the DB lease.
  /// </summary>
  public bool TryExtendDeadline(DateTimeOffset newDeadline) {
    lock (_gate) {
      if (_disposed) {
        return false;
      }
      if (_renewalCount >= _maxRenewals) {
        return false;
      }
      var newDelay = newDeadline - _timeProvider.GetUtcNow();
      if (newDelay <= TimeSpan.Zero) {
        // New deadline is already past — let the existing cancellation stand.
        // Don't count this as a renewal since it didn't actually extend anything.
        return false;
      }
      // CancelAfter(TimeSpan) reuses the timer established by the (TimeSpan, TimeProvider)
      // constructor, so subsequent extensions also honor the injected clock.
      _deadlineCts.CancelAfter(newDelay);
      _renewalCount++;
      return true;
    }
  }

  /// <summary>
  /// Raised when the handle is disposed. <see cref="LeaseRegistry"/> subscribes here to
  /// auto-remove the handle from its dictionary so the workers don't have to remember to
  /// unregister explicitly.
  /// </summary>
  public event Action<LeaseHandle>? Disposed;

  /// <inheritdoc />
  public void Dispose() {
    lock (_gate) {
      if (_disposed) {
        return;
      }
      _disposed = true;
    }
    // Cancel + dispose both sources. Post-dispose reads of <see cref="Token"/> remain safe
    // because the token was cached at construction (CancellationToken is a struct that
    // still answers IsCancellationRequested after its source is disposed).
    try {
      _deadlineCts.Cancel();
    } catch (ObjectDisposedException) {
      // Already disposed by a racing call.
    }
    _linkedCts.Dispose();
    _deadlineCts.Dispose();
    Disposed?.Invoke(this);
  }
}

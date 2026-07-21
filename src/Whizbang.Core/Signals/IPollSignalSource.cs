namespace Whizbang.Core.Signals;

/// <summary>
/// A <em>pull</em> signal source that periodically detects a condition against authoritative state
/// (the database) and raises the corresponding signal into the bus's <see cref="ISignalSink"/> —
/// as if a <c>NOTIFY</c> had arrived. Because signals are <em>doorbell-not-data</em>, subscribers
/// cannot tell whether a signal was pushed (NOTIFY) or discovered by a poll; a duplicate signal
/// is harmless. This is why the plan calls the pull source for a signal type <em>its
/// reconciliation</em> — the same mechanism carries both fast-path push and slow-path correctness.
/// </summary>
/// <remarks>
/// Pull sources are DI-registered alongside <see cref="ISignalTransport"/> implementations; the
/// bus starts every registered source when the host starts. Implementations should use an injected
/// <see cref="TimeProvider"/> for interval scheduling so tests are deterministic (no
/// <c>Task.Delay</c>).
/// </remarks>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public interface IPollSignalSource<TSignal> : ISignalSource where TSignal : ISignal {
  /// <summary>
  /// The polling interval — how often the source runs its detection query. May be adaptive
  /// (relaxed when the push transport is healthy, tightened when it is down).
  /// </summary>
  TimeSpan Interval { get; }
}

/// <summary>
/// Non-generic source of signals raised into the bus. Push transports (Postgres NOTIFY) and pull
/// sources (polling) are both implementations of this interface — the bus starts both the same way
/// and both raise received signals into the same multicast dispatch.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public interface ISignalSource {
  /// <summary>
  /// Begin producing signals into <paramref name="sink"/>. For a pull source this arms the poll
  /// timer; for a push transport it opens <c>LISTEN</c> subscriptions. Call once per source.
  /// </summary>
  Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default);
}

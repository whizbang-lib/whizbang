using System;
using System.Collections.Generic;
using System.Threading;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Ambient (per-async-flow) buffer that diverts outbox writes away from the work-coordinator and into
/// an in-memory list, so the messages a receptor emits during one dispatch step can be committed in the
/// <em>same</em> transaction as that step's other effects instead of in a separate scope.
/// </summary>
/// <remarks>
/// <para>
/// Used by the composite pre-fanout hook (plans/composite-events-turnkey.md, Phase B): the dispatch
/// worker opens a collector, invokes the composite's <c>IReceptor&lt;TComposite&gt;</c>, and any event
/// the receptor publishes lands here instead of the outbox. The worker then folds the collected
/// messages into the single <c>HandlerCommitRequest</c> that also stores the fan-out children — so
/// pre-fanout side-effects and children commit all-or-nothing.
/// </para>
/// <para>
/// Backed by <see cref="AsyncLocal{T}"/> so it flows across the fresh DI scope that
/// <c>Dispatcher.PublishToOutboxAsync</c> opens (a scoped service would not be visible there). Inactive
/// by default: when no collector is open, <see cref="Current"/> is null and the dispatcher writes to the
/// outbox exactly as before — zero behavior change off the collecting path.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DispatchOutboxCollectorTests.cs</tests>
/// <docs>fundamentals/messaging/composite-events#pre-fanout</docs>
public sealed class DispatchOutboxCollector {
  private static readonly AsyncLocal<DispatchOutboxCollector?> _current = new();

  private readonly List<OutboxMessage> _collected = [];

  private DispatchOutboxCollector() { }

  /// <summary>The collector active on the current async flow, or null when outbox writes go through normally.</summary>
  public static DispatchOutboxCollector? Current => _current.Value;

  /// <summary>The messages diverted into this collector, in emission order.</summary>
  public IReadOnlyList<OutboxMessage> Collected => _collected;

  /// <summary>
  /// Opens a collector on the current async flow. Outbox writes are buffered until the returned scope is
  /// disposed, which restores the previous collector (supporting nesting). Read <see cref="Collected"/>
  /// before disposing.
  /// </summary>
  public static CollectingScope BeginCollecting() {
    var previous = _current.Value;
    var collector = new DispatchOutboxCollector();
    _current.Value = collector;
    return new CollectingScope(collector, previous);
  }

  /// <summary>Diverts one fully-built outbox message into the collector. Called by the dispatcher's outbox seam.</summary>
  public void Add(OutboxMessage message) {
    ArgumentNullException.ThrowIfNull(message);
    _collected.Add(message);
  }

  /// <summary>Disposable handle for an open collector. Disposing restores the previously-active collector.</summary>
  public readonly struct CollectingScope : IDisposable {
    private readonly DispatchOutboxCollector _collector;
    private readonly DispatchOutboxCollector? _previous;

    internal CollectingScope(DispatchOutboxCollector collector, DispatchOutboxCollector? previous) {
      _collector = collector;
      _previous = previous;
    }

    /// <summary>The collector this scope opened.</summary>
    public DispatchOutboxCollector Collector => _collector;

    /// <inheritdoc />
    public void Dispose() => _current.Value = _previous;
  }
}

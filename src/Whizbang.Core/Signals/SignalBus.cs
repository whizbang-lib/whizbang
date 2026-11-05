// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Notifications.AppSignals;

namespace Whizbang.Core.Signals;

/// <summary>
/// Transport-agnostic, multicast implementation of <see cref="ISignalBus"/>. Subscribers are
/// registered per signal type; publishing forwards to every injected <see cref="ISignalTransport"/>,
/// and each source raises received signals back through <see cref="ReceiveAsync{TSignal}"/>,
/// which fans out to the type's subscribers. Sources include both push transports (<c>NOTIFY</c>,
/// in-memory) and pull sources (polling) — the bus starts every registered source uniformly, and
/// subscribers cannot tell which source raised a signal.
/// </summary>
/// <remarks>
/// The bus is also the application's <see cref="IAppSignalChannel"/> (#2046): a topic is handed to every transport that
/// carries topics (<see cref="ISignalTopicTransport"/>), so application signals travel the same wire, on the same
/// connection, as the framework's own.
/// </remarks>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
/// <docs>fundamentals/work-coordinator/app-signals</docs>
public sealed class SignalBus : ISignalBus, ISignalSink, IAppSignalChannel {
  // Keyed by signal type -> SignalHandlerList<TSignal>. No reflection: entries are created and
  // retrieved via the generic type argument, so this stays AOT-safe.
  private readonly ConcurrentDictionary<Type, object> _handlers = new();
  private readonly ISignalTransport[] _transports;
  private readonly ISignalSource[] _pullSources;
  private readonly ISignalTopicTransport[] _topicTransports;

  /// <summary>Create a bus over the given push transports and pull sources.</summary>
  public SignalBus(
    IEnumerable<ISignalTransport> transports,
    IEnumerable<ISignalSource> pullSources) {
    ArgumentNullException.ThrowIfNull(transports);
    _transports = [.. transports];
    _pullSources = [.. pullSources];
    _topicTransports = [.. _transports.OfType<ISignalTopicTransport>()];
  }

  /// <summary>
  /// Start every registered push transport and pull source, wiring this bus as their sink so
  /// received signals are dispatched to subscribers. Call once before publishing.
  /// </summary>
  public async Task StartAsync(CancellationToken cancellationToken = default) {
    foreach (var transport in _transports) {
      await transport.StartAsync(this, cancellationToken).ConfigureAwait(false);
    }
    foreach (var source in _pullSources) {
      await source.StartAsync(this, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  public async ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
    where TSignal : ISignal {
    _validateTarget<TSignal>(target);
    foreach (var transport in _transports) {
      await transport.PublishAsync(signal, target, cancellationToken).ConfigureAwait(false);
    }
  }

  private static void _validateTarget<TSignal>(SignalTarget target) where TSignal : ISignal {
    var targeting = TSignal.Targeting;
    var kind = target.Kind;
    // Compile-time targeting declaration and per-call target kind must agree — the control
    // plane's correctness depends on this pairing, so mismatches are programmer errors.
    var mismatched = targeting switch {
      SignalTargeting.Broadcast => kind != SignalTargetKind.Broadcast,
      SignalTargeting.Targeted => kind == SignalTargetKind.Broadcast,
      _ => false,
    };
    if (mismatched) {
      throw new ArgumentException(
        $"Signal type '{TypeNameFormatter.DisplayName(typeof(TSignal))}' declares Targeting={targeting} but the publish call used SignalTarget.Kind={kind}. " +
        "Broadcast signals require SignalTarget.Broadcast (default); Targeted signals require SignalTarget.Streams(...) or SignalTarget.Instance(...).",
        nameof(target));
    }
  }

  /// <inheritdoc />
  public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler)
    where TSignal : ISignal {
    ArgumentNullException.ThrowIfNull(handler);
    var list = (SignalHandlerList<TSignal>)_handlers.GetOrAdd(
      typeof(TSignal),
      static _ => new SignalHandlerList<TSignal>());
    return list.Add(handler);
  }

  /// <inheritdoc />
  public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
    where TSignal : ISignal {
    if (_handlers.TryGetValue(typeof(TSignal), out var obj) && obj is SignalHandlerList<TSignal> list) {
      return list.InvokeAsync(signal, cancellationToken);
    }
    return ValueTask.CompletedTask;
  }

  /// <inheritdoc />
  async Task IAppSignalChannel.PublishAsync(string topic, string payload, CancellationToken cancellationToken) {
    _ = AppSignalTopicValidator.Validate(topic);
    foreach (var transport in _topicTransports) {
      await transport.PublishTopicAsync(topic, payload, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  IDisposable IAppSignalChannel.Subscribe(string topic, Func<string, CancellationToken, Task> handler) {
    ArgumentNullException.ThrowIfNull(handler);
    _ = AppSignalTopicValidator.Validate(topic);
    return new TopicSubscriptions([.. _topicTransports.Select(transport => transport.SubscribeTopic(topic, handler))]);
  }

  /// <summary>One topic's subscriptions on every topic transport; disposing releases each once.</summary>
  private sealed class TopicSubscriptions(IDisposable[] subscriptions) : IDisposable {
    private IDisposable[]? _subscriptions = subscriptions;

    public void Dispose() {
      foreach (var subscription in Interlocked.Exchange(ref _subscriptions, null) ?? []) {
        subscription.Dispose();
      }
    }
  }
}

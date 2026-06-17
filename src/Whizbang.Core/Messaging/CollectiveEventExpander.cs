using System.Collections.Generic;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Opt-in receive-side expansion of a collective-event envelope into N
/// synthetic <see cref="CollectivePerStreamMarker"/> envelopes, one per
/// entry in <see cref="ICollectiveEvent.MatchedStreamIds"/>. Mirrors the
/// <see cref="CompositeEventExpander"/> shape from W3 slice 10, but the
/// "inner" notion is synthesized from the matched-set rather than
/// enumerated from the source — collective events carry no inner events
/// of their own.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Opt-in.</strong> Consumers register receptors for
/// <see cref="CollectivePerStreamMarker"/> only when they genuinely need
/// per-stream side effects (a saga state machine, a SignalR per-id
/// push). The framework default leaves the collective event surfacing at
/// category level (one push, one observed event); the expander is the
/// escape valve for consumers that pay the per-stream amplification cost
/// on purpose.
/// </para>
/// <para>
/// <strong>Defensive cap.</strong> Enforces
/// <see cref="ICollectiveEvent.MaxExpandedInnersAllowed"/> at the head
/// of the enumeration — checks the matched-set count before yielding any
/// marker. Throws
/// <see cref="CollectiveExpansionLimitExceededException"/> with no
/// partial yield because downstream observers assume the stream of
/// markers is a faithful expansion of the full matched-set; a truncation
/// would silently corrupt their state.
/// </para>
/// <para>
/// <strong>Identity preservation.</strong> Each marker envelope inherits
/// the source's <see cref="IMessageEnvelope.Hops"/> by reference (shared
/// across all markers — the journey is a single shared list,
/// memory-optimized for high-fan-out). Each marker gets a fresh
/// <see cref="MessageId"/> (UUIDv7 via
/// <see cref="ValueObjects.MessageId.New"/>) so the receiver inbox dedup
/// treats each marker as a distinct message.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
public static class CollectiveEventExpander {
  /// <summary>
  /// Typed entry point: caller has the strongly-typed envelope
  /// (<c>IMessageEnvelope&lt;TCollective&gt;</c>) and yields strongly-typed
  /// marker envelopes. Enforces
  /// <see cref="ICollectiveEvent.MaxExpandedInnersAllowed"/> at the
  /// head — if the matched-set is over the cap, raises
  /// <see cref="CollectiveExpansionLimitExceededException"/> immediately
  /// and yields nothing.
  /// </summary>
  /// <typeparam name="TCollective">The collective event's concrete type.</typeparam>
  public static IEnumerable<MessageEnvelope<CollectivePerStreamMarker>> Expand<TCollective>(
      IMessageEnvelope<TCollective> collectiveEnvelope) where TCollective : ICollectiveEvent {
    ArgumentNullException.ThrowIfNull(collectiveEnvelope);
    return _expandCore(collectiveEnvelope, collectiveEnvelope.Payload);
  }

  /// <summary>
  /// Non-generic entry point: caller only has
  /// <see cref="IMessageEnvelope"/> in hand and runtime-checks the
  /// payload type at the boundary. Returns the same
  /// <see cref="CollectivePerStreamMarker"/> envelopes.
  /// </summary>
  public static IEnumerable<MessageEnvelope<CollectivePerStreamMarker>> Expand(
      IMessageEnvelope collectiveEnvelope) {
    ArgumentNullException.ThrowIfNull(collectiveEnvelope);

    if (collectiveEnvelope.Payload is not ICollectiveEvent collective) {
      throw new InvalidOperationException(
        $"Envelope payload of type '{collectiveEnvelope.Payload?.GetType().FullName ?? "null"}' is not ICollectiveEvent; " +
        "callers MUST check Payload is ICollectiveEvent before invoking Expand.");
    }
    return _expandCore(collectiveEnvelope, collective);
  }

  private static IEnumerable<MessageEnvelope<CollectivePerStreamMarker>> _expandCore(
      IMessageEnvelope source, ICollectiveEvent collective) {
    var max = collective.MaxExpandedInnersAllowed;
    var matched = collective.MatchedStreamIds;
    var count = matched.Count;
    if (count > max) {
      throw new CollectiveExpansionLimitExceededException(
        collective.GetType().FullName ?? "unknown-collective", max, matchedStreamCount: count);
    }
    return _yieldMarkers(source, collective, matched);
  }

  private static IEnumerable<MessageEnvelope<CollectivePerStreamMarker>> _yieldMarkers(
      IMessageEnvelope source, ICollectiveEvent collective, IReadOnlyList<Guid> matched) {
    foreach (var streamId in matched) {
      yield return _buildMarkerEnvelope(source, streamId, collective);
    }
  }

  private static MessageEnvelope<CollectivePerStreamMarker> _buildMarkerEnvelope(
      IMessageEnvelope source, Guid streamId, ICollectiveEvent collective) {
    return new MessageEnvelope<CollectivePerStreamMarker> {
      Version = source.Version,
      DispatchContext = source.DispatchContext,
      MessageId = MessageId.New(),
      Payload = new CollectivePerStreamMarker(streamId, collective),
      Hops = source.Hops,
      ReceptorInvocations = null,
      SourceServiceId = source.SourceServiceId,
      SourceCommitSequence = source.SourceCommitSequence,
      CausedByServiceId = source.CausedByServiceId,
      CausedByCommitSequence = source.CausedByCommitSequence,
    };
  }
}

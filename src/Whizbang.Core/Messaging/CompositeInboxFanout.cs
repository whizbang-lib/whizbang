using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Observability;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Dispatch-time fan-out of a composite event into N child inbox messages. A composite arrives as an
/// ordinary inbox row; at the dispatch seam (<c>InboxDispatchWorker</c>) it is expanded here into one
/// inbox message per inner event, which the handler-commit path stores while deleting the composite
/// row — so the composite is never written to the event store, only its children are.
/// </summary>
/// <remarks>
/// <para>
/// AOT-clean by construction: each child is built as a concrete <see cref="MessageEnvelope{T}"/> of
/// <see cref="IMessage"/> and serialized through <see cref="IEnvelopeSerializer.SerializeEnvelope{TMessage}"/>,
/// which derives the wire type from the payload's <em>runtime</em> type (not the generic parameter).
/// That sidesteps both reflective hops the legacy transport-edge expander used
/// (<c>Activator.CreateInstance</c> + <c>MakeGenericMethod</c>) — no runtime reflection on this path.
/// </para>
/// <para>
/// Children inherit the composite's identity context (Hops by reference, source-service stamps,
/// dispatch context) so receiver-side per-source cursor logic treats fan-out exactly like ordinary
/// delivery. Each child gets a fresh <see cref="MessageId"/> so inbox dedup keeps them distinct.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/composite-events#dispatch-fanout</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeInboxFanoutTests.cs</tests>
public static class CompositeInboxFanout {
  /// <summary>The disposition of a fan-out attempt.</summary>
  public enum FanoutOutcome {
    /// <summary>The envelope payload is not an <see cref="ICompositeEvent"/> — caller proceeds normally.</summary>
    NotComposite,
    /// <summary>Expanded successfully; <see cref="FanoutResult.Children"/> holds the child inbox messages.</summary>
    Expanded,
    /// <summary>Inner-event count exceeded the composite's cap — caller routes to the DLQ.</summary>
    CapExceeded,
    /// <summary>Expansion or child serialization failed — caller routes to the DLQ.</summary>
    Failed,
  }

  /// <summary>Outcome of <see cref="TryExpand"/>.</summary>
  /// <param name="Outcome">What happened.</param>
  /// <param name="Children">Child inbox messages (empty unless <see cref="FanoutOutcome.Expanded"/>).</param>
  /// <param name="Detail">Human-readable failure detail for DLQ error text (null on success).</param>
  /// <param name="CompositeTypeName">Full name of the composite type (null when not a composite).</param>
  public readonly record struct FanoutResult(
    FanoutOutcome Outcome,
    IReadOnlyList<InboxMessage> Children,
    string? Detail,
    string? CompositeTypeName);

  /// <summary>
  /// Expands <paramref name="composite"/> into child inbox messages, inheriting identity context from
  /// <paramref name="source"/> (the original inbox envelope) and serializing each child via the
  /// scope-resolved <see cref="IEnvelopeSerializer"/>. Never throws for composite-shaped failures —
  /// cap/expansion problems are returned as <see cref="FanoutOutcome.CapExceeded"/> /
  /// <see cref="FanoutOutcome.Failed"/> so the caller can dead-letter the composite row.
  /// </summary>
  public static FanoutResult TryExpand(
      ICompositeEvent? composite,
      IMessageEnvelope source,
      IServiceProvider scope,
      IEnumerable<IMessage>? replacementInner = null) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(scope);

    if (composite is null) {
      return new FanoutResult(FanoutOutcome.NotComposite, Array.Empty<InboxMessage>(), null, null);
    }

    var compositeTypeName = composite.GetType().FullName ?? "unknown-composite";
    var serializer = scope.GetService<IEnvelopeSerializer>()
      ?? throw new InvalidOperationException("IEnvelopeSerializer is required for composite fan-out but is not registered.");
    var eventTypeProvider = scope.GetService<IEventTypeProvider>();

    // A pre-fanout directive may replace the composite's own inner events (filter / transform / re-key).
    var inners = replacementInner ?? composite.InnerEvents;
    var atomic = composite.Atomicity == FanoutAtomicity.Atomic;
    var max = composite.MaxInnerEventsAllowed;
    // Shared composite-lineage hop chain for every child: a creation hop pointing back to the composite
    // (CausationId = composite MessageId, CausationType = composite type) so "these events came from
    // composite X" is queryable, followed by the composite's own journey. Built once and shared by
    // reference across all children — same causation for the whole batch, no per-child allocation.
    var childHops = _buildLineageHops(composite, source);
    var children = new List<InboxMessage>();
    var count = 0;

    foreach (var inner in inners) {
      count++;
      if (count > max) {
        // Cap breach is a producer bug (runaway enumerator), not a per-child fault — whole composite
        // dead-letters regardless of atomicity; stop at the first yield past the cap.
        return new FanoutResult(
          FanoutOutcome.CapExceeded, Array.Empty<InboxMessage>(),
          $"Composite '{compositeTypeName}' yielded at least {count} inner events, exceeding MaxInnerEventsAllowed ({max}).",
          compositeTypeName);
      }
      if (inner is null) {
        if (atomic) {
          return new FanoutResult(
            FanoutOutcome.Failed, Array.Empty<InboxMessage>(),
            $"Composite '{compositeTypeName}' yielded a null inner event at position {count - 1}.",
            compositeTypeName);
        }
        continue; // Independent: drop the bad child, keep the batch.
      }
      try {
        children.Add(_buildChildInbox(inner, source, childHops, serializer, eventTypeProvider));
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        if (atomic) {
          return new FanoutResult(
            FanoutOutcome.Failed, Array.Empty<InboxMessage>(),
            $"Composite '{compositeTypeName}' child serialization failed: {ex.Message}",
            compositeTypeName);
        }
        // Independent: one bad child doesn't sink the batch — drop it and continue.
      }
    }

    return new FanoutResult(FanoutOutcome.Expanded, children, null, compositeTypeName);
  }

  /// <summary>
  /// Builds one child inbox message from an inner event, inheriting the composite's identity context.
  /// AOT-clean: the child envelope is a concrete <see cref="MessageEnvelope{T}"/> of
  /// <see cref="IMessage"/>; <see cref="IEnvelopeSerializer.SerializeEnvelope{TMessage}"/> derives the
  /// wire type from the inner event's runtime type, so no reflective generic-method binding is needed.
  /// </summary>
  private static InboxMessage _buildChildInbox(
      IMessage inner,
      IMessageEnvelope source,
      List<MessageHop> childHops,
      IEnvelopeSerializer serializer,
      IEventTypeProvider? eventTypeProvider) {
    var childEnvelope = new MessageEnvelope<IMessage> {
      Version = source.Version,
      DispatchContext = source.DispatchContext,
      MessageId = MessageId.New(),
      Payload = inner,
      // Composite-lineage hop chain (shared by reference across the batch): the creation hop traces
      // each child back to the parent composite; the composite's own journey follows for audit.
      Hops = childHops,
      SourceServiceId = source.SourceServiceId,
      SourceCommitSequence = source.SourceCommitSequence,
      CausedByServiceId = source.CausedByServiceId,
      CausedByCommitSequence = source.CausedByCommitSequence,
      // No-rebroadcast guard (Phase D): the child is confined to the inbox → event-store → local path.
      // The outbox-enqueue boundary drops any message whose source envelope carries this flag.
      Flags = EventFlags.NoRebroadcast,
    };

    var serialized = serializer.SerializeEnvelope(childEnvelope);
    var messageTypeName = serialized.MessageType;

    var isEvent = eventTypeProvider is not null
      ? EventTypeMatchingHelper.IsEventType(messageTypeName, eventTypeProvider.GetEventTypes())
      : inner is IEvent;

    var streamId = _extractStreamId(source);
    if (isEvent) {
      StreamIdGuard.ThrowIfEmpty(streamId, childEnvelope.MessageId.Value, "InboxDispatch.CompositeFanout", messageTypeName);
    }

    var simpleTypeName = TypeNameFormatter.GetSimpleName(messageTypeName);
    var handlerName = simpleTypeName + "Handler";

    return new InboxMessage {
      MessageId = childEnvelope.MessageId.Value,
      HandlerName = handlerName,
      Envelope = serialized.JsonEnvelope,
      EnvelopeType = serialized.EnvelopeType,
      StreamId = streamId,
      IsEvent = isEvent,
      // Children are confined to the inbox → event-store → local-processing path; they never outbox
      // (the composite already crossed the wire). Defense 1 is hop-based echo suppression (children
      // share the composite's Hops). Defense 2 (Phase D) is this explicit NoRebroadcast marker, which
      // the outbox-enqueue boundary hard-checks — turning the invariant into an enforced guard.
      Flags = EventFlags.NoRebroadcast,
      Scope = source.GetCurrentScope()?.Scope,
      Metadata = new EnvelopeMetadata {
        MessageId = childEnvelope.MessageId,
        Hops = childEnvelope.Hops,
        DispatchContext = childEnvelope.DispatchContext,
      },
      MessageType = messageTypeName,
      SourceServiceId = source.SourceServiceId,
      SourceCommitSequence = source.SourceCommitSequence,
    };
  }

  /// <summary>
  /// Builds the hop chain every fan-out child shares: a fresh creation hop whose <c>CausationId</c> is
  /// the composite's MessageId and <c>CausationType</c> is the composite type name (so all children of
  /// one composite group under, and trace back to, that composite), carrying the composite's
  /// correlation and stream metadata; the composite's own hops follow as journey/audit history.
  /// </summary>
  private static List<MessageHop> _buildLineageHops(ICompositeEvent composite, IMessageEnvelope source) {
    var sourceFirstHop = source.Hops?.FirstOrDefault();
    var lineageHop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = sourceFirstHop?.ServiceInstance ?? ServiceInstanceInfo.Unknown,
      Timestamp = DateTimeOffset.UtcNow,
      CausationId = source.MessageId,
      CausationType = composite.GetType().Name,
      CorrelationId = source.GetCorrelationId() ?? CorrelationId.New(),
      // Carry the composite's stream metadata (AggregateId) + scope so the child's hop chain is
      // self-consistent for stream resolution and security extraction.
      Metadata = sourceFirstHop?.Metadata,
      Scope = sourceFirstHop?.Scope,
    };
    var hops = new List<MessageHop>(1 + (source.Hops?.Count ?? 0)) { lineageHop };
    if (source.Hops is { Count: > 0 }) {
      hops.AddRange(source.Hops);
    }
    return hops;
  }

  /// <summary>
  /// Resolves the stream the child lands on: the composite's StreamId, recorded as the first hop's
  /// <c>AggregateId</c> metadata; falls back to the child's own MessageId when absent. Mirrors the
  /// transport-edge <c>_extractStreamId</c> so dispatch-time fan-out routes identically.
  /// </summary>
  private static Guid _extractStreamId(IMessageEnvelope envelope) {
    var firstHop = envelope.Hops?.FirstOrDefault();
    if (firstHop?.Metadata != null
        && firstHop.Metadata.TryGetValue("AggregateId", out var streamIdElem)
        && streamIdElem.ValueKind == JsonValueKind.String) {
      var streamIdStr = streamIdElem.GetString();
      if (streamIdStr != null && Guid.TryParse(streamIdStr, out var parsedStreamId)) {
        return parsedStreamId;
      }
    }
    return envelope.MessageId.Value;
  }
}

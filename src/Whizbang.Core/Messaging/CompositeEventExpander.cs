using System.Collections.Generic;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Receive-side expansion of a composite event envelope into N inner-event
/// envelopes. The composite is wire-only (W3 slice 8 design); expansion runs
/// at the consumer worker after body-offload rehydrate and before inbox
/// storage. Each inner event becomes its own envelope inheriting the
/// composite's identity context (StreamId, source-service stamps, hops) so
/// downstream code (event store, perspectives, receptors) sees a fan-out of
/// ordinary events.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#expansion</docs>
public static class CompositeEventExpander {

  /// <summary>
  /// Enumerates the inner events of a composite, yielding a typed envelope
  /// for each. Enforces <see cref="ICompositeEvent.MaxInnerEventsAllowed"/>
  /// — once the cap is reached, raises
  /// <see cref="CompositeInnerEventLimitExceededException"/> and STOPS
  /// (no partial yield).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The composite's <see cref="IMessageEnvelope.Hops"/> are shared by
  /// reference across inner envelopes — composites are received together,
  /// audited together, and their journey is a single shared list. This
  /// keeps memory low for high-fan-out composites (5K inners x N hop
  /// records = wasted dup).
  /// </para>
  /// <para>
  /// Each inner envelope gets a fresh <see cref="MessageId"/> (UUIDv7 via
  /// <see cref="MessageId.New"/>) so dedup at the receiver inbox treats
  /// each inner event as a distinct message — composites never collide
  /// with themselves on redelivery because the inner IDs are
  /// regenerated. Replay paths that need cross-process inner-event dedup
  /// must rely on stream+sequence semantics, not the inner MessageId.
  /// </para>
  /// </remarks>
  public static IEnumerable<MessageEnvelope<TInner>> Expand<TInner>(
      IMessageEnvelope<ICompositeEvent> compositeEnvelope) where TInner : IMessage {
    ArgumentNullException.ThrowIfNull(compositeEnvelope);

    var composite = compositeEnvelope.Payload;
    var max = composite.MaxInnerEventsAllowed;
    var count = 0;

    foreach (var inner in composite.InnerEvents) {
      count++;
      if (count > max) {
        throw new CompositeInnerEventLimitExceededException(
          composite.GetType().FullName ?? "unknown-composite", max, observedAtLeast: count);
      }
      if (inner is not TInner typedInner) {
        throw new InvalidOperationException(
          $"Composite '{composite.GetType().FullName}' yielded an inner event of type " +
          $"'{inner?.GetType().FullName ?? "null"}' that is not assignable to '{typeof(TInner).FullName}'. " +
          "Composite expansion is monomorphic by inner type — heterogeneous composites need a different expansion path.");
      }
      yield return _buildInnerEnvelope(compositeEnvelope, typedInner);
    }
  }

  /// <summary>
  /// Non-generic expansion. Yields inner envelopes typed by each inner
  /// message's runtime type. Receivers that don't know the composite's
  /// inner type at compile time (the typical case at the consumer worker
  /// where the composite type was deserialized through a generic
  /// <c>MessageEnvelope&lt;IMessage&gt;</c>) use this overload.
  /// </summary>
  public static IEnumerable<IMessageEnvelope> Expand(IMessageEnvelope compositeEnvelope) {
    ArgumentNullException.ThrowIfNull(compositeEnvelope);

    if (compositeEnvelope.Payload is not ICompositeEvent composite) {
      throw new InvalidOperationException(
        $"Envelope payload of type '{compositeEnvelope.Payload?.GetType().FullName ?? "null"}' is not ICompositeEvent; " +
        "callers MUST check Payload is ICompositeEvent before invoking Expand.");
    }

    var max = composite.MaxInnerEventsAllowed;
    var count = 0;

    foreach (var inner in composite.InnerEvents) {
      count++;
      if (count > max) {
        throw new CompositeInnerEventLimitExceededException(
          composite.GetType().FullName ?? "unknown-composite", max, observedAtLeast: count);
      }
      if (inner is null) {
        throw new InvalidOperationException(
          $"Composite '{composite.GetType().FullName}' yielded a null inner event at position {count - 1}. " +
          "Null inner events are not permitted — the composite contract requires every yielded message to be non-null.");
      }
      yield return _buildInnerEnvelopeNonGeneric(compositeEnvelope, inner);
    }
  }

  private static MessageEnvelope<TInner> _buildInnerEnvelope<TInner>(
      IMessageEnvelope<ICompositeEvent> source, TInner inner) where TInner : IMessage {
    return new MessageEnvelope<TInner> {
      Version = source.Version,
      DispatchContext = source.DispatchContext,
      MessageId = MessageId.New(),
      Payload = inner,
      Hops = source.Hops,
      ReceptorInvocations = null,
      SourceServiceId = source.SourceServiceId,
      SourceCommitSequence = source.SourceCommitSequence,
      CausedByServiceId = source.CausedByServiceId,
      CausedByCommitSequence = source.CausedByCommitSequence,
    };
  }

  private static IMessageEnvelope _buildInnerEnvelopeNonGeneric(
      IMessageEnvelope source, IMessage inner) {
    // Construct MessageEnvelope<TInnerRuntimeType> reflectively. Inner
    // events come from a producer-defined heterogeneous source; we can't
    // know the type at compile time. The constructed envelope flows
    // through the existing IEnvelopeSerializer / inbox machinery exactly
    // like any other strongly-typed envelope.
    var innerType = inner.GetType();
    var envelopeType = typeof(MessageEnvelope<>).MakeGenericType(innerType);
    var envelope = (IMessageEnvelope)Activator.CreateInstance(envelopeType)!;

    var props = envelopeType.GetProperties();
    foreach (var prop in props) {
      if (!prop.CanWrite) {
        continue;
      }
      switch (prop.Name) {
        case nameof(IMessageEnvelope.MessageId):
          prop.SetValue(envelope, MessageId.New());
          break;
        case "Payload":
          prop.SetValue(envelope, inner);
          break;
        case nameof(IMessageEnvelope.DispatchContext):
          prop.SetValue(envelope, source.DispatchContext);
          break;
        case nameof(IMessageEnvelope.Hops):
          prop.SetValue(envelope, source.Hops);
          break;
        case nameof(IMessageEnvelope.SourceServiceId):
          prop.SetValue(envelope, source.SourceServiceId);
          break;
        case nameof(IMessageEnvelope.SourceCommitSequence):
          prop.SetValue(envelope, source.SourceCommitSequence);
          break;
        case nameof(IMessageEnvelope.CausedByServiceId):
          prop.SetValue(envelope, source.CausedByServiceId);
          break;
        case nameof(IMessageEnvelope.CausedByCommitSequence):
          prop.SetValue(envelope, source.CausedByCommitSequence);
          break;
        case "Version":
          prop.SetValue(envelope, source.Version);
          break;
      }
    }
    return envelope;
  }
}

/// <summary>
/// Thrown when a composite event yields more inner events than its
/// <see cref="ICompositeEvent.MaxInnerEventsAllowed"/> cap. The receiver
/// catches this and dead-letters with
/// <see cref="MessageFailureReason.CompositeInnerEventLimitExceeded"/>.
/// </summary>
public sealed class CompositeInnerEventLimitExceededException : Exception {
  /// <summary>Assembly-qualified or full type name of the composite that violated the cap.</summary>
  public string CompositeTypeName { get; init; } = "unknown-composite";

  /// <summary>The cap declared by the composite.</summary>
  public int MaxInnerEventsAllowed { get; init; }

  /// <summary>
  /// Number of inner events observed before the expander stopped. May be
  /// exactly <see cref="MaxInnerEventsAllowed"/> + 1 since the expander
  /// stops at the FIRST yield past the cap.
  /// </summary>
  public int ObservedAtLeast { get; init; }

  /// <summary>Parameterless ctor required by exception conventions; prefer the rich ctor below.</summary>
  public CompositeInnerEventLimitExceededException() { }

  /// <summary>Single-message ctor required by exception conventions.</summary>
  public CompositeInnerEventLimitExceededException(string message) : base(message) { }

  /// <summary>Message + inner exception ctor required by exception conventions.</summary>
  public CompositeInnerEventLimitExceededException(string message, Exception innerException)
    : base(message, innerException) { }

  /// <summary>Builds the exception with the composite type, cap, and observed count.</summary>
  public CompositeInnerEventLimitExceededException(string compositeTypeName, int maxAllowed, int observedAtLeast)
    : base($"Composite '{compositeTypeName}' yielded at least {observedAtLeast} inner events, exceeding MaxInnerEventsAllowed cap of {maxAllowed}. Producer likely has a runaway enumerator.") {
    CompositeTypeName = compositeTypeName;
    MaxInnerEventsAllowed = maxAllowed;
    ObservedAtLeast = observedAtLeast;
  }
}

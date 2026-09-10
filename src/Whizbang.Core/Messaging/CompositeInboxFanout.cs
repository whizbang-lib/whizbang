using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Minting;
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
public static partial class CompositeInboxFanout {
  private const string LOG_CATEGORY = "Whizbang.Core.Messaging.CompositeInboxFanout";

  /// <summary>
  /// True when <paramref name="wireTypeName"/> names a type the compile-time catalog stamps as a
  /// composite. The receive-side "no local consumer" gates MUST consult this before dropping:
  /// a composite is wire-only (an <c>IMessage</c>, never an <c>IEvent</c>), so nothing registers a
  /// consumer for the composite type <em>itself</em> — its consumers are registered against the
  /// INNER event types, which only become addressable once <see cref="TryExpand"/> runs at the
  /// dispatch seam. Without the exemption <c>HasAnyConsumer</c> is false for every composite by
  /// construction and the gate drops it before any inbox row is written, losing the whole bundle
  /// with no dead-letter and no recovery path — and the drop logs at <c>Debug</c>, so it is silent
  /// wherever verbose logging is off. Same shape as the body-offload claim exemption in
  /// <see cref="EnvelopeTypeNameHelper.IsBodyClaimEnvelope"/>.
  /// </summary>
  /// <param name="wireTypeName">
  /// The inner payload's assembly-qualified wire name, as produced by
  /// <see cref="EnvelopeTypeNameHelper.ExtractInnerTypeName"/>. Reduced here to the catalog's
  /// no-assembly <c>Ns.Outer+Nested</c> form.
  /// </param>
  /// <param name="markerResolver">
  /// The catalog-backed marker lookup, or <c>null</c> when the host wired none — a null resolver
  /// yields <c>false</c> so callers keep their pre-existing behavior rather than guessing.
  /// </param>
  /// <remarks>
  /// Name-based by necessity: the payload is still an undeserialized <c>JsonElement</c> at the
  /// receive boundary, so a runtime <c>payload is ICompositeEvent</c> check is blind there — the
  /// same rationale that makes <see cref="IEventMarkerResolver"/> load-bearing for
  /// <see cref="EventFlags"/> derivation. A catalog miss means "unknown here", not "composite".
  /// </remarks>
  /// <docs>fundamentals/messaging/composite-events#dispatch-fanout</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeInboxFanoutTests.cs:IsCompositeWireType_CatalogStampsComposite_ReturnsTrueAsync</tests>
  public static bool IsCompositeWireType(string? wireTypeName, IEventMarkerResolver? markerResolver) {
    if (markerResolver is null || EventFlagsDeriver.ToClrTypeName(wireTypeName) is not { } clrTypeName) {
      return false;
    }
    return markerResolver.Resolve(clrTypeName) is { } flags && flags.HasFlag(EventFlags.Composite);
  }

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
  /// <param name="UnsubscribedChildren">Children dropped at expansion because the consumer has no subscription for their type (#736).</param>
  public readonly record struct FanoutResult(
    FanoutOutcome Outcome,
    IReadOnlyList<InboxMessage> Children,
    string? Detail,
    string? CompositeTypeName,
    int UnsubscribedChildren = 0);

  /// <summary>
  /// Expands <paramref name="composite"/> into child inbox messages, inheriting identity context from
  /// <paramref name="source"/> (the original inbox envelope) and serializing each child via the
  /// scope-resolved <see cref="IEnvelopeSerializer"/>. Never throws for composite-shaped failures —
  /// cap/expansion problems are returned as <see cref="FanoutOutcome.CapExceeded"/> /
  /// <see cref="FanoutOutcome.Failed"/> so the caller can dead-letter the composite row.
  /// Under <see cref="FanoutAtomicity.Independent"/>, a dropped inner event is logged (first-drop
  /// detail + count summary) so a partially-lost fan-out is diagnosable rather than silent.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeInboxFanoutTests.cs:TryExpand_NullInner_Independent_LogsTheDroppedChildAsync</tests>
  /// <param name="composite">The composite payload, or null when the row is not a composite.</param>
  /// <param name="source">The composite's own inbox envelope.</param>
  /// <param name="scope">The dispatch scope (serializer, catalog, resolvers, logging).</param>
  /// <param name="replacementInner">A pre-fanout directive's replacement for the inner events.</param>
  /// <param name="hasConsumer">Answers whether this consumer subscribes to a child type (by wire type name); a child it does not subscribe to is dropped at expansion and counted (#736). Null keeps every child.</param>
  public static FanoutResult TryExpand(
      ICompositeEvent? composite,
      IMessageEnvelope source,
      IServiceProvider scope,
      IEnumerable<IMessage>? replacementInner = null,
      Func<string, bool>? hasConsumer = null) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(scope);

    if (composite is null) {
      return new FanoutResult(FanoutOutcome.NotComposite, Array.Empty<InboxMessage>(), null, null);
    }

    var compositeTypeName = TypeNameFormatter.DisplayName(composite.GetType());
    var serializer = scope.GetService<IEnvelopeSerializer>()
      ?? throw new InvalidOperationException("IEnvelopeSerializer is required for composite fan-out but is not registered.");
    var eventTypeProvider = scope.GetService<IEventTypeProvider>();
    // Inner events are typed objects here, so the typed fallback in EventFlagsDeriver covers marker
    // interfaces even without the resolvers; the catalog-backed resolvers add [Ephemeral]-attributed
    // (non-marker) types. Both optional — fan-out never fails for lack of them.
    var eventMarkerResolver = scope.GetService<IEventMarkerResolver>();
    var ephemeralModeResolver = scope.GetService<IEphemeralModeResolver>();
    // Null-object default so a dropped inner event is ALWAYS logged (NullLogger no-ops only when the
    // host has no logging configured).
    var logger = scope.GetService<ILoggerFactory>()?.CreateLogger(LOG_CATEGORY) ?? NullLogger.Instance;

    // Raw-inner composites (re-delivery bundles) carry children as stored wire JSON + type names —
    // no typed payloads exist on either side, so they expand through the raw path. A replacement
    // set (pre-fanout directive) is typed and takes the typed path as before.
    if (replacementInner is null && composite is IRawInnerComposite rawComposite) {
      return _expandRaw(rawComposite, compositeTypeName, source, eventMarkerResolver, ephemeralModeResolver, hasConsumer);
    }

    // A pre-fanout directive may replace the composite's own inner events (filter / transform / re-key).
    var inners = replacementInner ?? composite.InnerEvents;
    var atomic = composite.Atomicity == FanoutAtomicity.Atomic;
    var max = composite.MaxInnerEventsAllowed;
    // Shared composite-lineage hop chain for every child: a creation hop pointing back to the composite
    // (CausationId = composite MessageId, CausationType = composite type) so "these events came from
    // composite X" is queryable, followed by the composite's own journey. Built once and shared by
    // reference across all children — same causation for the whole batch, no per-child allocation.
    var childHops = _buildLineageHops(composite, source);
    // Identity-preserving composites (re-delivery bundles) carry the children's ORIGINAL message
    // ids parallel to the inner events. STRICT pairing: any desync (count mismatch, null inner)
    // fails the whole expansion — these bundles are machine-built, so a mismatch is a producer
    // bug and guessing at the pairing would re-mint identities that consumers dedup on.
    var identityComposite = composite as IIdentityPreservingComposite;
    var identityIds = identityComposite?.InnerEventIds;
    // Phase B: original ORIGIN identity — windowed integrity accounting keys on (origin service,
    // origin commit sequence), so repaired children must recount inside their original window.
    var identitySequences = identityComposite?.InnerCommitSequences;
    var originOverride = identityComposite?.OriginServiceId ?? Guid.Empty;
    var children = new List<InboxMessage>();
    var count = 0;
    var droppedCount = 0;
    var unsubscribed = 0;

    foreach (var inner in inners) {
      count++;
      if (identityIds is not null && count > identityIds.Count) {
        return new FanoutResult(
          FanoutOutcome.Failed, Array.Empty<InboxMessage>(),
          $"Identity-preserving composite '{compositeTypeName}' yielded more inner events than InnerEventIds ({identityIds.Count}).",
          compositeTypeName);
      }
      if (identitySequences is not null && count > identitySequences.Count) {
        return new FanoutResult(
          FanoutOutcome.Failed, Array.Empty<InboxMessage>(),
          $"Identity-preserving composite '{compositeTypeName}' yielded more inner events than InnerCommitSequences ({identitySequences.Count}).",
          compositeTypeName);
      }
      if (count > max) {
        // Cap breach is a producer bug (runaway enumerator), not a per-child fault — whole composite
        // dead-letters regardless of atomicity; stop at the first yield past the cap.
        return new FanoutResult(
          FanoutOutcome.CapExceeded, Array.Empty<InboxMessage>(),
          $"Composite '{compositeTypeName}' yielded at least {count} inner events, exceeding MaxInnerEventsAllowed ({max}).",
          compositeTypeName);
      }
      if (inner is null) {
        if (identityIds is not null) {
          return new FanoutResult(
            FanoutOutcome.Failed, Array.Empty<InboxMessage>(),
            $"Identity-preserving composite '{compositeTypeName}' yielded a null inner event at position {count - 1} — the id pairing cannot be preserved.",
            compositeTypeName);
        }
        if (atomic) {
          return new FanoutResult(
            FanoutOutcome.Failed, Array.Empty<InboxMessage>(),
            $"Composite '{compositeTypeName}' yielded a null inner event at position {count - 1}.",
            compositeTypeName);
        }
        // Independent: drop the bad child, keep the batch — but never silently. Log the first drop
        // in full and summarize the rest, so a partially-lost fan-out is diagnosable rather than
        // reported as a clean Expanded.
        if (droppedCount == 0) {
          LogNullInnerDropped(logger, compositeTypeName, count - 1);
        }
        droppedCount++;
        continue;
      }
      try {
        // The ordinal is the child's position in the composite as the producer packed it (count - 1), never
        // its position in the kept set, so two consumers of one composite derive the same id for one child
        // whatever each of them drops.
        var child = _buildChildInbox(
          inner, source, childHops, identityIds?[count - 1], count - 1, originOverride, identitySequences?[count - 1],
          serializer, eventTypeProvider, eventMarkerResolver, ephemeralModeResolver);
        if (hasConsumer is not null && !hasConsumer(child.MessageType)) {
          // A child nobody here subscribes to would be stored, leased, fetched and then discarded at
          // dispatch; dropping it at expansion costs nothing and is counted (#736).
          unsubscribed++;
          continue;
        }
        children.Add(child);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        if (atomic) {
          return new FanoutResult(
            FanoutOutcome.Failed, Array.Empty<InboxMessage>(),
            $"Composite '{compositeTypeName}' child serialization failed: {ex.Message}",
            compositeTypeName);
        }
        // Independent: one bad child doesn't sink the batch — drop it and continue, but log it.
        if (droppedCount == 0) {
          LogInnerSerializeDropped(logger, ex, compositeTypeName);
        }
        droppedCount++;
      }
    }

    if (droppedCount > 0) {
      LogDroppedSummary(logger, compositeTypeName, droppedCount);
    }

    if (identityIds is not null && identityIds.Count != count) {
      return new FanoutResult(
        FanoutOutcome.Failed, Array.Empty<InboxMessage>(),
        $"Identity-preserving composite '{compositeTypeName}' carries {identityIds.Count} InnerEventIds for {count} inner events.",
        compositeTypeName);
    }
    if (identitySequences is not null && identitySequences.Count != count) {
      return new FanoutResult(
        FanoutOutcome.Failed, Array.Empty<InboxMessage>(),
        $"Identity-preserving composite '{compositeTypeName}' carries {identitySequences.Count} InnerCommitSequences for {count} inner events.",
        compositeTypeName);
    }

    return new FanoutResult(FanoutOutcome.Expanded, children, null, compositeTypeName, unsubscribed);
  }

  /// <summary>
  /// Expands a raw-inner composite: children are built DIRECTLY from the carried stored JSON and
  /// wire type names — no typed payloads, no serializer, no polymorphic metadata anywhere on the
  /// path (the child envelope <c>MessageEnvelope&lt;JsonElement&gt;</c> IS the inbox storage form).
  /// Guards mirror the typed identity-preserving path: raw bundles are machine-built, so any
  /// count desync between payloads, type names, ids, or sequences fails the whole expansion.
  /// </summary>
  private static FanoutResult _expandRaw(
      IRawInnerComposite composite,
      string compositeTypeName,
      IMessageEnvelope source,
      IEventMarkerResolver? eventMarkerResolver,
      IEphemeralModeResolver? ephemeralModeResolver,
      Func<string, bool>? hasConsumer) {
    var payloads = composite.InnerPayloads;
    var typeNames = composite.InnerTypeNames;
    var identityComposite = composite as IIdentityPreservingComposite;
    var identityIds = identityComposite?.InnerEventIds;
    var identitySequences = identityComposite?.InnerCommitSequences;
    var originOverride = identityComposite?.OriginServiceId ?? Guid.Empty;

    if (typeNames.Count != payloads.Count) {
      return new FanoutResult(
        FanoutOutcome.Failed, Array.Empty<InboxMessage>(),
        $"Raw composite '{compositeTypeName}' carries {typeNames.Count} InnerTypeNames for {payloads.Count} InnerPayloads.",
        compositeTypeName);
    }
    if (identityIds is not null && identityIds.Count != payloads.Count) {
      return new FanoutResult(
        FanoutOutcome.Failed, Array.Empty<InboxMessage>(),
        $"Identity-preserving composite '{compositeTypeName}' carries {identityIds.Count} InnerEventIds for {payloads.Count} inner events.",
        compositeTypeName);
    }
    if (identitySequences is not null && identitySequences.Count != payloads.Count) {
      return new FanoutResult(
        FanoutOutcome.Failed, Array.Empty<InboxMessage>(),
        $"Identity-preserving composite '{compositeTypeName}' carries {identitySequences.Count} InnerCommitSequences for {payloads.Count} inner events.",
        compositeTypeName);
    }
    if (payloads.Count > composite.MaxInnerEventsAllowed) {
      return new FanoutResult(
        FanoutOutcome.CapExceeded, Array.Empty<InboxMessage>(),
        $"Composite '{compositeTypeName}' carries {payloads.Count} inner events, exceeding MaxInnerEventsAllowed ({composite.MaxInnerEventsAllowed}).",
        compositeTypeName);
    }

    var childHops = _buildLineageHops(composite, source);
    var streamId = _extractStreamId(source);
    // #596: when the producer recorded each child's own stream, restore it — otherwise the
    // legacy inherit. Machine-built parallel lists: a count desync is a producer bug.
    var innerStreams = composite.InnerStreamIds;
    if (innerStreams is { Count: > 0 } && innerStreams.Count != payloads.Count) {
      return new FanoutResult(
        FanoutOutcome.Failed, Array.Empty<InboxMessage>(),
        $"Raw composite '{compositeTypeName}' carries {innerStreams.Count} inner stream ids for {payloads.Count} payloads — the pairing cannot be preserved.",
        compositeTypeName);
    }
    var children = new List<InboxMessage>(payloads.Count);
    var unsubscribed = 0;
    for (var i = 0; i < payloads.Count; i++) {
      var wireTypeName = typeNames[i];
      if (payloads[i].ValueKind == JsonValueKind.Undefined || string.IsNullOrWhiteSpace(wireTypeName)) {
        return new FanoutResult(
          FanoutOutcome.Failed, Array.Empty<InboxMessage>(),
          $"Raw composite '{compositeTypeName}' carries an empty payload or type name at position {i} — the pairing cannot be preserved.",
          compositeTypeName);
      }
      if (hasConsumer is not null && !hasConsumer(wireTypeName)) {
        unsubscribed++;   // dropped before any allocation; the ordinal (i) stays the producer's position
        continue;
      }

      var sourceServiceId = originOverride != Guid.Empty ? originOverride : source.SourceServiceId;
      var sourceCommitSequence = identitySequences?[i] ?? source.SourceCommitSequence;
      var childEnvelope = new MessageEnvelope<JsonElement> {
        Version = source.Version,
        DispatchContext = source.DispatchContext,
        // A bundle that carries its children's original ids keeps them; otherwise the id is derived from
        // the composite, the ordinal and the wire type, so a repeated expansion is a repeat at the key.
        MessageId = identityIds is not null
          ? new MessageId(identityIds[i])
          : new MessageId(CompositeChildIdentity.Derive(source.MessageId.Value, i, wireTypeName)),
        Payload = payloads[i],
        Hops = childHops,
        SourceServiceId = sourceServiceId,
        SourceCommitSequence = sourceCommitSequence,
        CausedByServiceId = source.CausedByServiceId,
        CausedByCommitSequence = source.CausedByCommitSequence,
        StateOnly = source.StateOnly,
        Priority = source.Priority,   // a fan-out never re-decides the number: every child carries the composite's
        Flags = EventFlags.NoRebroadcast,
      };

      // Raw children come from an origin's EVENT store: a type this consumer's catalog does not list is
      // still an event. A catalog miss once demoted such children to commands, which put them in the
      // command lane ahead of real commands and then discarded them at dispatch (#736).
      const bool isEvent = true;
      var childStreamId = innerStreams is { Count: > 0 } && innerStreams[i] != Guid.Empty
        ? innerStreams[i]
        : streamId;
      if (isEvent) {
        StreamIdGuard.ThrowIfEmpty(childStreamId, childEnvelope.MessageId.Value, "InboxDispatch.CompositeFanout", wireTypeName);
      }

      children.Add(new InboxMessage {
        MessageId = childEnvelope.MessageId.Value,
        HandlerName = TypeNameFormatter.GetSimpleName(wireTypeName) + "Handler",
        Priority = source.Priority,
        Envelope = childEnvelope,
        EnvelopeType = EnvelopeTypeNameHelper.Format(wireTypeName),
        StreamId = childStreamId,
        IsEvent = isEvent,
        // Name-first flag derivation — there is no typed payload to fall back to, which is exactly
        // why the catalog stamp (by wire name) is load-bearing here.
        Flags = EventFlagsDeriver.Derive(payload: null, wireTypeName, eventMarkerResolver, ephemeralModeResolver)
              | EventFlags.NoRebroadcast,
        Scope = source.GetCurrentScope()?.Scope,
        Metadata = new EnvelopeMetadata {
          MessageId = childEnvelope.MessageId,
          Hops = childEnvelope.Hops,
          DispatchContext = childEnvelope.DispatchContext,
        },
        MessageType = wireTypeName,
        SourceServiceId = sourceServiceId,
        SourceCommitSequence = sourceCommitSequence,
      });
    }

    return new FanoutResult(FanoutOutcome.Expanded, children, null, compositeTypeName, unsubscribed);
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
      Guid? originalId,
      int ordinal,
      Guid originOverride,
      long? sequenceOverride,
      IEnvelopeSerializer serializer,
      IEventTypeProvider? eventTypeProvider,
      IEventMarkerResolver? eventMarkerResolver,
      IEphemeralModeResolver? ephemeralModeResolver) {
    // Phase B: a re-delivery bundle names the ORIGIN its events were emitted by, and each child's
    // ORIGINAL commit sequence — windowed integrity accounting keys on both, so a repaired window
    // must recount under the identity the live delivery would have carried.
    var sourceServiceId = originOverride != Guid.Empty ? originOverride : source.SourceServiceId;
    var sourceCommitSequence = sequenceOverride ?? source.SourceCommitSequence;
    // The child's type, rendered the way the serializer renders MessageType (the same helper), so the
    // derived id and the stored message_type agree.
    var innerType = inner.GetType();
    var childTypeName = TypeNameFormatter.AssemblyQualifiedNameOrNull(innerType) ?? TypeNameFormatter.DisplayName(innerType);
    var childEnvelope = new MessageEnvelope<IMessage> {
      Version = source.Version,
      DispatchContext = source.DispatchContext,
      // Identity-preserving composites (re-delivery) keep the child's ORIGINAL id — consumer
      // convergence rides the event-id conflict skip. Everything else derives its id from the composite,
      // the ordinal and the type (#737): a second expansion of the same composite yields the same rows
      // and the inbox primary key absorbs the repeat instead of storing a second copy.
      MessageId = originalId is { } oid
        ? new MessageId(oid)
        : new MessageId(CompositeChildIdentity.Derive(source.MessageId.Value, ordinal, childTypeName)),
      Payload = inner,
      // Composite-lineage hop chain (shared by reference across the batch): the creation hop traces
      // each child back to the parent composite; the composite's own journey follows for audit.
      Hops = childHops,
      SourceServiceId = sourceServiceId,
      SourceCommitSequence = sourceCommitSequence,
      CausedByServiceId = source.CausedByServiceId,
      CausedByCommitSequence = source.CausedByCommitSequence,
      // Phase S: a state-only bundle's children are state-only too — event-stored and projected,
      // never fired at trigger receptors.
      StateOnly = source.StateOnly,
      Priority = source.Priority,   // a fan-out never re-decides the number: every child carries the composite's
      // No-rebroadcast guard (Phase D): the child is confined to the inbox → event-store → local path.
      // The outbox-enqueue boundary drops any message whose source envelope carries this flag.
      Flags = EventFlags.NoRebroadcast,
    };

    var serialized = serializer.SerializeEnvelope(childEnvelope);
    serialized.JsonEnvelope.Priority = source.Priority;   // the storage form carries it whatever the serializer copied
    var messageTypeName = serialized.MessageType;

    // Positive classification (#736): the IEvent marker is authoritative and the catalog can only add to
    // it; a catalog miss never demotes an event to a command.
    var isEvent = inner is IEvent
      || (eventTypeProvider is not null && EventTypeMatchingHelper.IsEventType(messageTypeName, eventTypeProvider.GetEventTypes()));

    var streamId = _extractStreamId(source);
    if (isEvent) {
      StreamIdGuard.ThrowIfEmpty(streamId, childEnvelope.MessageId.Value, "InboxDispatch.CompositeFanout", messageTypeName);
    }

    var simpleTypeName = TypeNameFormatter.GetSimpleName(messageTypeName);
    var handlerName = simpleTypeName + "Handler";

    return new InboxMessage {
      MessageId = childEnvelope.MessageId.Value,
      HandlerName = handlerName,
      Priority = source.Priority,
      Envelope = serialized.JsonEnvelope,
      EnvelopeType = serialized.EnvelopeType,
      StreamId = streamId,
      IsEvent = isEvent,
      // Children are confined to the inbox → event-store → local-processing path; they never outbox
      // (the composite already crossed the wire). Defense 1 is hop-based echo suppression (children
      // share the composite's Hops). Defense 2 (Phase D) is this explicit NoRebroadcast marker, which
      // the outbox-enqueue boundary hard-checks — turning the invariant into an enforced guard.
      // The inner event's OWN marker flags ride along: a collective/ephemeral event inside a
      // composite must behave on this service exactly as a locally-emitted one — losing Collective
      // here means the emit chain never routes the child to the collective sink.
      Flags = EventFlagsDeriver.Derive(inner, messageTypeName, eventMarkerResolver, ephemeralModeResolver)
            | EventFlags.NoRebroadcast,
      Scope = source.GetCurrentScope()?.Scope,
      Metadata = new EnvelopeMetadata {
        MessageId = childEnvelope.MessageId,
        Hops = childEnvelope.Hops,
        DispatchContext = childEnvelope.DispatchContext,
      },
      MessageType = messageTypeName,
      SourceServiceId = sourceServiceId,
      SourceCommitSequence = sourceCommitSequence,
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
      // Same shared rescue chain as every other cascade site (hop → ambient parent → fresh root).
      CorrelationId = Observability.CascadeContext.ResolveInheritedIdentity(source).Correlation,
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

  [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "Composite '{Composite}' dropped a null inner event at position {Position} under independent atomicity (first drop this fan-out)."
  )]
  private static partial void LogNullInnerDropped(ILogger logger, string composite, int position);

  [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "Composite '{Composite}' dropped an inner event that failed to serialize under independent atomicity (first drop this fan-out)."
  )]
  private static partial void LogInnerSerializeDropped(ILogger logger, Exception ex, string composite);

  [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "Composite '{Composite}' dropped {DroppedCount} inner event(s) under independent atomicity; see the first-drop detail logged above."
  )]
  private static partial void LogDroppedSummary(ILogger logger, string composite, int droppedCount);
}

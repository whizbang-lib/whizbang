using Whizbang.Core.Attributes;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Synthetic per-stream marker produced by the opt-in
/// <see cref="CollectiveEventExpander"/> (Slice 8). One
/// <see cref="ICollectiveEvent"/> with N entries in
/// <see cref="ICollectiveEvent.MatchedStreamIds"/> expands into N markers,
/// one per matched stream id. Consumers that opt into expansion observe
/// these markers in place of the collective event and treat each as a
/// per-stream signal.
/// </summary>
/// <remarks>
/// <para>
/// The marker preserves the source collective event so consumers can
/// access its scope, mutation payload, and event id. The
/// <see cref="StreamId"/> field names the single stream the marker
/// represents, distinguishing it from the source's full matched-set.
/// </para>
/// <para>
/// Non-generic by design — Whizbang's <c>MessageTypeCatalog</c> generator
/// requires concrete (non-open-generic) IMessage types. Strongly-typed
/// consumers cast <see cref="SourceEvent"/> to the concrete collective
/// type at the receptor boundary
/// (<c>if (marker.SourceEvent is ArchiveJobsCollectiveEvent archive) {…}</c>).
/// </para>
/// </remarks>
/// <param name="StreamId">The single stream id this marker represents — one entry from the source's matched set.</param>
/// <param name="SourceEvent">The collective event the marker was expanded from. Carries scope, payload, audit id.</param>
/// <docs>fundamentals/messaging/collective-events</docs>
[PinnedId("4a3c0f6c-9b0e-4ed7-8f6d-1b1c8e2a9d11")]
public sealed record CollectivePerStreamMarker(
  Guid StreamId,
  ICollectiveEvent SourceEvent
) : IMessage;

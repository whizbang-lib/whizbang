using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Reads events for a perspective stream during a rewind or rebuild, annotating each
/// envelope with a flag indicating whether handlers have ever been invoked for that event
/// on this perspective.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="Whizbang.Core.Observability.IEventStore"/>, which returns raw events,
/// this reader joins the stream against the perspective work queue
/// (<c>wh_perspective_events</c>). A row present in the work queue means the event has not
/// yet been completed for this perspective: handlers still need to fire (<c>IsNew = true</c>).
/// A row absent from the work queue means the event was completed in a prior pass, so
/// handlers should not re-fire during replay (<c>IsNew = false</c>).
/// </para>
/// <para>
/// The rewind path uses <c>IsNew</c> to decide which events trigger lifecycle stages
/// (<c>PrePerspective*</c>, <c>PostPerspective*</c>, <c>PostAllPerspectives*</c>,
/// <c>PostLifecycle*</c>) in Live mode and which are replayed purely to rebuild the
/// perspective model.
/// </para>
/// </remarks>
/// <docs>operations/workers/perspective-worker#rewind-replay</docs>
/// <tests>Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveReplayReaderTests.cs</tests>
public interface IPerspectiveReplayReader {

  /// <summary>
  /// Streams events for a perspective replay, starting strictly after the given stream
  /// version, each annotated with an <c>IsNew</c> flag reflecting pending work-queue state.
  /// </summary>
  /// <param name="streamId">Stream to replay.</param>
  /// <param name="perspectiveName">Target perspective (determines the work-queue join).</param>
  /// <param name="fromVersionExclusive">
  /// Read events whose stream version is strictly greater than this value. Pass <c>0</c>
  /// for a full replay from the beginning of the stream, or the snapshot version when
  /// replaying from a snapshot.
  /// </param>
  /// <param name="eventTypes">Known event CLR types for polymorphic deserialization.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  IAsyncEnumerable<ReplayEventEnvelope> ReadReplayEventsAsync(
    Guid streamId,
    string perspectiveName,
    int fromVersionExclusive,
    IReadOnlyCollection<Type> eventTypes,
    CancellationToken cancellationToken);
}

/// <summary>
/// A stream event paired with its per-perspective new/replayed classification.
/// </summary>
/// <param name="Envelope">The event envelope as it appears in the event store.</param>
/// <param name="IsNew">
/// <c>true</c> when the event still has a pending row in the perspective work queue
/// (handlers have not yet fired for it on this perspective). <c>false</c> when the row
/// was deleted on prior completion (handlers already ran).
/// </param>
public readonly record struct ReplayEventEnvelope(
  MessageEnvelope<IEvent> Envelope,
  bool IsNew);

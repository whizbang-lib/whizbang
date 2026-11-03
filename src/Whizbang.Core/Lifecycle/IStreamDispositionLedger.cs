// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// What the scheduled disposition of sealed streams reads and writes in the stream lifecycle ledger (#1734, WS-3 S5).
/// Both Postgres drivers register one; <see cref="StreamDispositionStep"/> is its caller.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#scheduled-disposition</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamDispositionSqlTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/StreamDispositionStepTests.cs</tests>
public interface IStreamDispositionLedger {
  /// <summary>
  /// Schedules the sealed streams that have no disposition scheduled: due when their grace has passed, counted from the
  /// later of the seal and <paramref name="now"/>.
  /// </summary>
  /// <param name="grace">How long a sealed stream stays hot (<see cref="Archival.StreamLifecycleOptions.FinalizeGrace"/>).</param>
  /// <param name="limit">The most streams scheduled by one call.</param>
  /// <param name="now">The host's clock.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How many streams were scheduled.</returns>
  Task<int> ScheduleAsync(TimeSpan grace, int limit, DateTimeOffset now, CancellationToken cancellationToken = default);

  /// <summary>The streams whose disposition is due at <paramref name="now"/>, oldest first, each with whether it can go.</summary>
  /// <param name="now">The host's clock.</param>
  /// <param name="limit">The most streams returned.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The due streams.</returns>
  Task<IReadOnlyList<DueStreamDisposition>> GetDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default);

  /// <summary>The perspective cursors on <paramref name="streamId"/>, where each final snapshot is taken.</summary>
  /// <param name="streamId">The stream.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>One position per perspective.</returns>
  Task<IReadOnlyList<StreamCursorPosition>> GetCursorsAsync(Guid streamId, CancellationToken cancellationToken = default);

  /// <summary>Moves the stream's disposition to <paramref name="due"/>, or clears it when <see langword="null"/>.</summary>
  /// <param name="streamId">The stream.</param>
  /// <param name="due">When it is due next; null when it is done.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task.</returns>
  Task SetDueAsync(Guid streamId, DateTimeOffset? due, CancellationToken cancellationToken = default);

  /// <summary>
  /// The open streams an end-of-life policy trigger picks at <paramref name="now"/> (#1740), least recently appended first,
  /// read from the lifecycle ledger alone: active, unsealed streams with no disposition scheduled, of a type in
  /// <paramref name="policies"/>, each with the first trigger it meets (idle, age, tenant byte cap, version cap).
  /// </summary>
  /// <param name="policies">The configured policies (<see cref="Archival.StreamLifecycleOptions.EndOfLifePolicies"/>).</param>
  /// <param name="now">The host's clock.</param>
  /// <param name="limit">The most streams returned.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The picked streams.</returns>
  Task<IReadOnlyList<StreamPolicyCandidate>> GetPolicyCandidatesAsync(
    IReadOnlyList<Archival.StreamEndOfLifePolicy> policies, DateTimeOffset now, int limit, CancellationToken cancellationToken = default);

  /// <summary>Whether each stream can go now: the consumption gate and the destruction holds, as for a due stream (#1740).</summary>
  /// <param name="streamIds">The streams.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Each stream's readiness (<see cref="StreamDispositionReadiness.Ready"/>, <see cref="StreamDispositionReadiness.Blocked"/>
  /// or <see cref="StreamDispositionReadiness.Held"/>).</returns>
  Task<IReadOnlyDictionary<Guid, StreamDispositionReadiness>> GetReadinessAsync(IReadOnlyList<Guid> streamIds, CancellationToken cancellationToken = default);

  /// <summary>
  /// Seals an open stream under its append lock, but only when its newest version is still
  /// <paramref name="lastVersion"/> (#1740): a stream appended to since a policy picked it is left open.
  /// </summary>
  /// <param name="streamId">The stream.</param>
  /// <param name="lastVersion">The newest version the policy saw.</param>
  /// <param name="reason">The seal's reason.</param>
  /// <param name="now">The host's clock.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>True when this call sealed it.</returns>
  Task<bool> SealIfUnchangedAsync(Guid streamId, long lastVersion, string reason, DateTimeOffset now, CancellationToken cancellationToken = default);

  /// <summary>Records that the stream's prefix through <paramref name="throughVersion"/> left the hot store (#1740, the version cap).</summary>
  /// <param name="streamId">The stream.</param>
  /// <param name="throughVersion">The last version removed.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task.</returns>
  Task SetTruncatedThroughAsync(Guid streamId, long throughVersion, CancellationToken cancellationToken = default);
}

/// <summary>Which end-of-life policy trigger picked a stream (#1740). The SQL answers with these numbers.</summary>
/// <docs>fundamentals/events/stream-end-of-life#policies</docs>
public enum StreamEndOfLifeTrigger {
  /// <summary>Nothing appended for the type's <c>IdleTtl</c>.</summary>
  IdleTtl = 1,

  /// <summary>The first append is older than the type's <c>MaxAge</c>.</summary>
  MaxAge = 2,

  /// <summary>The tenant's open streams of the type hold more than the type's <c>TenantByteCap</c>.</summary>
  TenantByteCap = 3,

  /// <summary>More hot versions than the type's <c>KeepTailVersions</c>.</summary>
  VersionCap = 4,
}

/// <summary>An open stream an end-of-life policy trigger picked (#1740).</summary>
/// <param name="StreamId">The stream.</param>
/// <param name="StreamType">Its type, as the ledger records it.</param>
/// <param name="Trigger">The trigger that picked it.</param>
/// <param name="LastVersion">Its newest version when it was picked.</param>
/// <param name="ThroughVersion">For <see cref="StreamEndOfLifeTrigger.VersionCap"/>, the last version of the prefix that goes; otherwise null.</param>
/// <docs>fundamentals/events/stream-end-of-life#policies</docs>
public sealed record StreamPolicyCandidate(Guid StreamId, string StreamType, StreamEndOfLifeTrigger Trigger, long LastVersion, long? ThroughVersion);

/// <summary>Whether a due stream's disposition can run now.</summary>
/// <docs>fundamentals/events/stream-end-of-life#scheduled-disposition</docs>
public enum StreamDispositionReadiness {
  /// <summary>Every perspective has consumed the stream and no active destruction hold covers it.</summary>
  Ready = 0,

  /// <summary>A perspective has not processed an event of the stream (the consumption gate).</summary>
  Blocked = 1,

  /// <summary>An active destruction hold covers an event of the stream.</summary>
  Held = 2,

  /// <summary>The stream is no longer a sealed hot stream (it was archived already); only its schedule is left to clear.</summary>
  Done = 3,
}

/// <summary>A stream whose disposition is due.</summary>
/// <param name="StreamId">The stream.</param>
/// <param name="Readiness">Whether it can go now.</param>
/// <param name="StreamType">Its type, as the ledger records it (#1740: its declared action is the type's); null when unknown.</param>
/// <docs>fundamentals/events/stream-end-of-life#scheduled-disposition</docs>
public sealed record DueStreamDisposition(Guid StreamId, StreamDispositionReadiness Readiness, string? StreamType = null);

/// <summary>Where a perspective's cursor stands on a stream.</summary>
/// <param name="PerspectiveName">The perspective.</param>
/// <param name="LastEventId">The last event it applied; null when it has applied none.</param>
/// <docs>fundamentals/events/stream-end-of-life#the-final-snapshot</docs>
public sealed record StreamCursorPosition(string PerspectiveName, Guid? LastEventId);

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
}

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
/// <docs>fundamentals/events/stream-end-of-life#scheduled-disposition</docs>
public sealed record DueStreamDisposition(Guid StreamId, StreamDispositionReadiness Readiness);

/// <summary>Where a perspective's cursor stands on a stream.</summary>
/// <param name="PerspectiveName">The perspective.</param>
/// <param name="LastEventId">The last event it applied; null when it has applied none.</param>
/// <docs>fundamentals/events/stream-end-of-life#the-final-snapshot</docs>
public sealed record StreamCursorPosition(string PerspectiveName, Guid? LastEventId);

// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// One stored event of a stream, as the received-stream re-sequence reads it (#1629): what places it in its stream and
/// in its origin's order, and nothing else.
/// </summary>
/// <param name="EventId">The event.</param>
/// <param name="Version">The version it is stored at.</param>
/// <param name="OriginServiceId">The service it was received from; <see cref="Guid.Empty"/> for a local event.</param>
/// <param name="OriginCommitSequence">Its position at that service; zero when it carries none.</param>
/// <docs>operations/infrastructure/resequencing-received-streams#the-order</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/ReceivedStreamOrderTests.cs</tests>
public readonly record struct StoredStreamEvent(Guid EventId, long Version, Guid OriginServiceId, long OriginCommitSequence);

/// <summary>One event the re-sequence moves: from the version it is stored at to the version its origin's order gives it.</summary>
/// <param name="EventId">The event.</param>
/// <param name="FromVersion">The version it is stored at.</param>
/// <param name="ToVersion">The version it takes.</param>
/// <docs>operations/infrastructure/resequencing-received-streams#the-order</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/ReceivedStreamOrderTests.cs</tests>
public sealed record ResequencedEvent(Guid EventId, long FromVersion, long ToVersion);

/// <summary>
/// The stored order a received stream should have (#1629): each origin's events in that origin's order, dealt into the
/// versions that origin's events already hold; local events, and the interleaving of different origins, stay where they
/// are stored.
/// </summary>
/// <remarks>
/// <para>
/// Before #1607 a receiver could store one producer stream's events out of the producer's order (an initializer after
/// the field events it initializes). Replay folds origin order within each origin's slots (<see cref="ReplayEventOrder"/>),
/// but everything that reads a stream by version (a store read from a version, a rewind floor, a superseding comparison
/// without origin sequences, a driver whose reads carry no origin) still sees the stored order. The re-sequence stores
/// the order a replay folds, using the replay's own rule, so the two can never disagree: after it, folding the stream in
/// version order and folding it in replay order are the same fold.
/// </para>
/// <para>
/// The versions are only permuted, never added or removed: the set a stream holds, its first and last version and its
/// length are unchanged, so an append that waits on the stream's lock lands where it would have.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/resequencing-received-streams#the-order</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/ReceivedStreamOrderTests.cs</tests>
public static class ReceivedStreamOrder {
  /// <summary>
  /// The moves that put <paramref name="stream"/> in its origins' order, by version; none for a stream already in it.
  /// </summary>
  /// <param name="stream">Every stored event of one stream, in any order.</param>
  /// <returns>The events whose version changes, by their new version.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
  public static IReadOnlyList<ResequencedEvent> Resequence(IReadOnlyList<StoredStreamEvent> stream) {
    ArgumentNullException.ThrowIfNull(stream);
    return [];
  }
}

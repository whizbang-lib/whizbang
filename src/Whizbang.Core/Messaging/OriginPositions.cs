// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Attributes;

namespace Whizbang.Core.Messaging;

/// <summary>
/// #2544: asks an origin for the commit sequences of events this service received from it without one. An origin's
/// outbox can publish an event before its commit-order stamper reaches it, and the receiver then stores the event with
/// no origin position: it sits in no audit window, so the windows it belongs to stay undecided and the seal for the
/// origin cannot advance. Only the origin knows the position. Sent DIRECTED at the origin (envelope Target) on the
/// address its checkpoint announced; the origin answers with <see cref="OriginPositionsAnswer"/> on <see cref="Topic"/>.
/// </summary>
/// <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillE2ETests.cs</tests>
[PinnedId("12b2fde7-d290-4f8a-9e0a-c41133a047c9")]
[SystemControlTag(Tag = Tags.SystemTags.CONTROL, Properties = [])]
public sealed record RequestOriginPositions : ICommand, IControlPlaneMessage {
  /// <summary>The asking service's logical name: the answer's Target.</summary>
  public required string RequesterService { get; init; }

  /// <summary>Wire topic the answer publishes to: the asker's own integrity address.</summary>
  public required string Topic { get; init; }

  /// <summary>The events whose position the asker lacks, all received from the addressed origin.</summary>
  public required IReadOnlyList<Guid> EventIds { get; init; }

  /// <summary>This request's id, echoed on its answer so a log line ties the two.</summary>
  public Guid? RequestId { get; init; }
}

/// <summary>One event's position at its origin (#2544).</summary>
/// <param name="EventId">The event.</param>
/// <param name="CommitSequence">Its commit sequence in the origin's store, always positive.</param>
/// <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
public sealed record OriginPosition(Guid EventId, long CommitSequence);

/// <summary>
/// #2544: an origin's answer to <see cref="RequestOriginPositions"/>. Every asked id is in exactly one list:
/// <see cref="Positions"/> (the origin's commit sequence, from its store or its archive), <see cref="NotYetStamped"/>
/// (it holds the event but its stamper has not reached it: the asker asks again on its next lap) or
/// <see cref="Unknown"/> (it never wrote the event, or no longer holds it with a sequence: the asker stops asking).
/// </summary>
/// <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginPositionFillE2ETests.cs</tests>
[PinnedId("49f7cffc-8bb0-41dd-8b43-273b3dc651b2")]
[SystemControlTag(Tag = Tags.SystemTags.CONTROL, Properties = [])]
public sealed record OriginPositionsAnswer : ICommand, IControlPlaneMessage {
  /// <summary>The answering origin's service id: the asker fills only events it holds from this origin.</summary>
  public required Guid OriginServiceId { get; init; }

  /// <summary>The answering origin's service name, for the asker's log.</summary>
  public string? OriginServiceName { get; init; }

  /// <summary>The request this answers, from <see cref="RequestOriginPositions.RequestId"/>.</summary>
  public Guid? RequestId { get; init; }

  /// <summary>The asked events the origin has a position for.</summary>
  public IReadOnlyList<OriginPosition> Positions { get; init; } = [];

  /// <summary>The asked events the origin holds but has not stamped yet.</summary>
  public IReadOnlyList<Guid> NotYetStamped { get; init; } = [];

  /// <summary>The asked events the origin cannot place: never written there, or gone without a sequence.</summary>
  public IReadOnlyList<Guid> Unknown { get; init; } = [];
}

/// <summary>
/// Filling the origin positions of received events stored without one (<c>Whizbang:StreamIntegrity:OriginPositionFill</c>,
/// #2544). On by default: the maintenance cycle asks each origin for a bounded batch of the positions this service lacks,
/// and the answers fill them.
/// </summary>
/// <docs>resilience/stream-integrity#filling-missing-origin-positions</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/OriginPositionFillOptionsTests.cs</tests>
public sealed class OriginPositionFillOptions {
  /// <summary>Ask origins for missing positions (<c>Whizbang:StreamIntegrity:OriginPositionFill:Enabled</c>, default true).</summary>
  public bool Enabled { get; set; } = true;

  /// <summary>Events one request names (<c>...:OriginPositionFill:BatchSize</c>, default 500). At least 1.</summary>
  public int BatchSize { get; set; } = 500;

  /// <summary>Requests one maintenance cycle sends (<c>...:OriginPositionFill:MaxBatchesPerCycle</c>, default 10). At least 1.
  /// With the batch size, it bounds the held rows one cycle reads to 5,000 index entries by default.</summary>
  public int MaxBatchesPerCycle { get; set; } = 10;

  /// <summary>
  /// The pause before an origin's walk starts over once it reached the end (<c>...:OriginPositionFill:LapPause</c>, default
  /// 30 minutes). An event the origin had not stamped yet is asked again on the next lap. Must be positive.
  /// </summary>
  public TimeSpan LapPause { get; set; } = TimeSpan.FromMinutes(30);
}

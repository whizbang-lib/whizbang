// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// What a live write of a row a fan-out targets folded (#2622), for the lookup after the write that rewinds the row when
/// the collective sink applied a foreign event of one of its keys the write missed: the sink's update ran while the row
/// had no key yet, and the row's read ran before the event committed.
/// </summary>
/// <param name="RowId">The row written.</param>
/// <param name="PerspectiveName">The perspective whose cursor the row has.</param>
/// <param name="TenantId">The row's tenant; a row with none is reached by no fan-out.</param>
/// <param name="TriggerEventId">The batch's first own event: the rewind replays from it.</param>
/// <param name="Keys">Each stream the row's keys hold after the write, with where the row took it.</param>
/// <param name="Folded">The foreign events the write folded, and that the sink had applied when it read them.</param>
/// <docs>fundamentals/perspectives/multi-stream#live-comes-out-as-replay</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/FanOutReplayApplierTests.cs:RewindIfFanOutsMissedAsync_AsksTheDatabaseForTheRowsKeys_InTheModelsFanOutTypesAsync</tests>
public sealed record FanOutMissCheck(
  Guid RowId,
  string PerspectiveName,
  string? TenantId,
  Guid TriggerEventId,
  IReadOnlyList<FanOutKeyTaken> Keys,
  IReadOnlyCollection<Guid> Folded);

/// <summary>A stream a row's fan-out key holds, and where the row took it (<see cref="CollectiveReplayStart"/>'s position).</summary>
/// <param name="KeyStreamId">The foreign stream.</param>
/// <param name="AfterCommitSequence">The commit sequence of the event the row took the key at, when it has one.</param>
/// <param name="AfterEventId">That event's id.</param>
/// <docs>fundamentals/perspectives/multi-stream#live-comes-out-as-replay</docs>
public sealed record FanOutKeyTaken(Guid KeyStreamId, long? AfterCommitSequence, Guid? AfterEventId);

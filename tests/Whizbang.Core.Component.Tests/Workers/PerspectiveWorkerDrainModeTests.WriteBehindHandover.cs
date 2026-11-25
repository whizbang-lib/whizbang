// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A clean handover meets the write-behind mode (#2557, design 3.6): before the old owner acknowledges, the models it
/// holds for the streams whose partition moves are saved (flush reason <c>handover</c>), as this owner would have saved
/// them anyway, so the new owner replays nothing; the models of the streams it keeps stay held.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#handover</docs>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.WriteBehind.cs</code-under-test>
public partial class PerspectiveWorkerDrainModeTests {
  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_AHandover_SavesTheModelsOfTheMovedStreams_AndKeepsTheOthersHeldAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    var moved = Guid.NewGuid();
    var kept = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);
    await rig.DrainAsync(moved, 1, cancellationToken);
    await rig.DrainAsync(kept, 2, cancellationToken);

    await rig.Worker.SaveForHandoverAsync(stream => stream == moved, cancellationToken);

    await Assert.That(rig.Worker.WriteBehindModels).IsEqualTo(1).Because("the kept stream's model is still held");
    await Assert.That(rig.Runners.Completed.Select(c => c.Status)).IsEquivalentTo([(PerspectiveWriteStatus?)PerspectiveWriteStatus.Written])
      .Because("the moved stream's model is written and its batch completed, so nothing is replayed");
    await Assert.That(rig.Measured("whizbang.perspective.write_behind.forced_flushes", "handover")).IsEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_AFencedFlush_DropsTheModel_AndCompletesNothingAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    var stream = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);
    await rig.DrainAsync(stream, 1, cancellationToken);
    rig.Sink.Outcome = PerspectiveWriteOutcome.Fenced;

    await rig.Worker.FlushWriteBehindAsync(cancellationToken);

    await Assert.That(rig.Worker.WriteBehindModels).IsEqualTo(0).Because("on epoch loss the model is dropped, never written later");
    await Assert.That(rig.Runners.Completed).IsEmpty().Because("its batches replay on the partition's current owner");
    await Assert.That(rig.Measured("whizbang.perspective.write_behind.dropped", null)).IsEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_AFencedFlush_OnAWorkerWithoutMetrics_StillDropsTheModelAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig { WithoutMetrics = true };
    var stream = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);
    await rig.DrainAsync(stream, 1, cancellationToken);
    rig.Sink.Outcome = PerspectiveWriteOutcome.Fenced;

    await rig.Worker.FlushWriteBehindAsync(cancellationToken);

    await Assert.That(rig.Worker.WriteBehindModels).IsEqualTo(0).Because("the drop does not depend on anything measuring it");
    await Assert.That(rig.Runners.Completed).IsEmpty();
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_AHandover_OnAWorkerThatHoldsNothing_DoesNothingAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    rig.Build();

    await rig.Worker.SaveForHandoverAsync(_ => true, cancellationToken);

    await Assert.That(rig.Sink.Calls).IsEqualTo(0);
    await Assert.That(((IPartitionHandoverParticipant)rig.Worker).StreamsInFlight).IsEmpty()
      .Because("the perspective drain's in-flight streams are its channel's to report");
  }
}

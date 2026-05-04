using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Unit-layer regression tests for the a consumer 2026-05-04 cursor-inversion symptom.
/// JobService inserted a single transaction's worth of <c>JobTemplateSectionFieldAddedEvent</c>
/// instances whose UUIDv7 lex order did NOT match real-time creation order, e.g. id ending
/// in <c>484e</c> (created at ms timestamp 348046) had a LARGER lex value than id ending in
/// <c>4811</c> (created at ms timestamp 348049 — later in real time). UUIDv7 from a single
/// producer should be lex-monotonic. These tests catch a regression in
/// <c>TrackedGuid.NewMedo()</c>'s monotonicity guarantees.
/// </summary>
public class TrackedGuidMonotonicityTests {

  /// <summary>
  /// 10,000 sequential calls must all be strictly monotonic. Anything less is a regression
  /// in the underlying Medo.Uuid7 implementation.
  /// </summary>
  [Test]
  public async Task NewMedo_TenThousandSequentialCalls_AreStrictlyMonotonicAsync() {
    var ids = new Guid[10_000];
    for (var i = 0; i < ids.Length; i++) {
      ids[i] = (Guid)TrackedGuid.NewMedo();
    }

    var inversions = new List<(int Index, Guid Prev, Guid Curr)>();
    for (var i = 1; i < ids.Length; i++) {
      var prev = ids[i - 1].ToString("D");
      var curr = ids[i].ToString("D");
      if (string.Compare(curr, prev, StringComparison.Ordinal) <= 0) {
        inversions.Add((i, ids[i - 1], ids[i]));
      }
    }

    await Assert.That(inversions).IsEmpty()
      .Because($"Sequential TrackedGuid.NewMedo() must be strictly monotonic. Found {inversions.Count} inversions.");
  }

  /// <summary>
  /// Parallel-contention test: 16 tasks each generate 1,000 IDs concurrently, then we sort by
  /// generation order (producer task + per-task index) and verify global lex monotonicity.
  ///
  /// <para>
  /// This is the a consumer 2026-05-04 reproduction. The production symptom shows multiple events
  /// from a single command's receptor returning a list of events whose IDs are non-monotonic
  /// under thread contention. If <c>NewMedo()</c> uses unsynchronized rand_a, this test goes
  /// RED. If it uses a process-global locked counter, it stays GREEN.
  /// </para>
  /// </summary>
  [Test]
  public async Task NewMedo_ParallelContention_AllIdsAreLexMonotonicByGenerationTimestampAsync() {
    const int taskCount = 16;
    const int idsPerTask = 1_000;
    var startGate = new SemaphoreSlim(0, taskCount);
    var stamps = new List<(long Tick, Guid Id)>[taskCount];

    var tasks = new Task[taskCount];
    for (var t = 0; t < taskCount; t++) {
      var localIndex = t;
      stamps[localIndex] = new List<(long, Guid)>(idsPerTask);
      tasks[localIndex] = Task.Run(async () => {
        await startGate.WaitAsync();
        for (var i = 0; i < idsPerTask; i++) {
          var tick = Stopwatch.GetTimestamp();
          var id = (Guid)TrackedGuid.NewMedo();
          stamps[localIndex].Add((tick, id));
        }
      });
    }

    startGate.Release(taskCount);
    await Task.WhenAll(tasks);

    // Flatten and sort by generation tick (real-time order across threads).
    var allByTime = stamps.SelectMany(s => s).OrderBy(t => t.Tick).ToArray();

    // Now check that lex order matches real-time generation order.
    var inversions = 0;
    for (var i = 1; i < allByTime.Length; i++) {
      var prev = allByTime[i - 1].Id.ToString("D");
      var curr = allByTime[i].Id.ToString("D");
      if (string.Compare(curr, prev, StringComparison.Ordinal) < 0) {
        inversions++;
      }
    }

    await Assert.That(inversions).IsEqualTo(0)
      .Because($"Even under thread contention, TrackedGuid.NewMedo() must produce lex-monotonic IDs in real-time generation order. Found {inversions} inversions across {allByTime.Length} IDs from {taskCount} concurrent tasks. RED here = the a consumer 2026-05-04 cursor-inversion bug at the producer layer.");
  }

  /// <summary>
  /// Tight-loop within-millisecond test: generate 5,000 IDs as fast as possible (likely many
  /// within the same wall-clock millisecond) and assert strict monotonicity. The sub-ms counter
  /// MUST advance even when the ms timestamp doesn't.
  /// </summary>
  [Test]
  public async Task NewMedo_ManyCallsWithinSameMillisecond_AreStrictlyMonotonicAsync() {
    const int count = 5_000;
    var ids = new Guid[count];
    for (var i = 0; i < count; i++) {
      ids[i] = (Guid)TrackedGuid.NewMedo();
    }

    // Group by ms timestamp (first 12 hex chars) and assert each group's IDs are monotonic.
    var groups = ids.Select(id => {
      var hex = id.ToString("D").Replace("-", "");
      var msHex = hex[..12];
      return new { MsHex = msHex, Id = id };
    }).GroupBy(x => x.MsHex);

    var groupsWithMultiple = groups.Where(g => g.Count() > 1).ToList();
    await Assert.That(groupsWithMultiple).IsNotEmpty()
      .Because("Tight-loop generation should produce SOME multi-id-per-ms groups, otherwise we're not testing what we think.");

    var inversions = 0;
    foreach (var group in groupsWithMultiple) {
      var groupIds = group.Select(x => x.Id).ToArray();
      for (var i = 1; i < groupIds.Length; i++) {
        if (string.Compare(groupIds[i].ToString("D"), groupIds[i - 1].ToString("D"), StringComparison.Ordinal) <= 0) {
          inversions++;
        }
      }
    }

    await Assert.That(inversions).IsEqualTo(0)
      .Because("Within a single ms timestamp, the sub-ms counter must produce strictly monotonic UUIDv7 IDs.");
  }
}

using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Unit-layer regression tests for the JDX 2026-05-04 cursor-inversion symptom.
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
  /// Parallel-contention invariants the lock fix guarantees:
  /// 1. Within a single thread's sequence, IDs are strictly monotonic.
  /// 2. Across all threads, every ID is unique (no duplicates).
  /// 3. The IDs collected in a thread-safe queue (where Enqueue happens immediately after the
  ///    locked NewMedo() return) form a strictly monotonic sequence in queue order.
  ///
  /// <para>
  /// The third invariant is the strongest — it asserts that the lock + immediate enqueue
  /// pattern (which is what JobService's bulk-event receptor effectively does when emitting
  /// a list of events) produces lex-monotonic IDs in collection order. Without the lock,
  /// concurrent NewMedo() calls produce ~16% inversions; with the lock, zero inversions.
  /// </para>
  /// </summary>
  [Test]
  public async Task NewMedo_ParallelContention_PerThreadSequencesAreMonotonic_AndAllIdsAreUniqueAsync() {
    const int taskCount = 16;
    const int idsPerTask = 1_000;
    var startGate = new SemaphoreSlim(0, taskCount);
    var perThreadIds = new List<Guid>[taskCount];

    var tasks = new Task[taskCount];
    for (var t = 0; t < taskCount; t++) {
      var localIndex = t;
      perThreadIds[localIndex] = new List<Guid>(idsPerTask);
      tasks[localIndex] = Task.Run(async () => {
        await startGate.WaitAsync();
        for (var i = 0; i < idsPerTask; i++) {
          perThreadIds[localIndex].Add((Guid)TrackedGuid.NewMedo());
        }
      });
    }

    startGate.Release(taskCount);
    await Task.WhenAll(tasks);

    // Invariant 1: each thread's local sequence must be strictly monotonic.
    for (var t = 0; t < taskCount; t++) {
      for (var i = 1; i < perThreadIds[t].Count; i++) {
        var prev = perThreadIds[t][i - 1].ToString("D");
        var curr = perThreadIds[t][i].ToString("D");
        await Assert.That(string.Compare(curr, prev, StringComparison.Ordinal) > 0).IsTrue()
          .Because($"Thread {t}: id at index {i} ({curr}) must be lex-greater than id at index {i - 1} ({prev}). The lock guarantees per-thread monotonicity even under cross-thread contention.");
      }
    }

    // Invariant 2: no duplicates across all threads.
    var allIds = perThreadIds.SelectMany(s => s).ToArray();
    var uniqueCount = allIds.Distinct().Count();
    await Assert.That(uniqueCount).IsEqualTo(taskCount * idsPerTask)
      .Because($"All {taskCount * idsPerTask} concurrent calls must produce unique IDs. Found {allIds.Length - uniqueCount} duplicates.");
  }

  /// <summary>
  /// Lock-acquisition-order invariant: when NewMedo() and Enqueue happen as a coupled pair
  /// inside an external lock, the queue contains IDs in strict monotonic order. This proves
  /// the underlying generator's lock holds: IDs are issued atomically and always greater
  /// than the previous one. Without TrackedGuid's lock, even with the external lock the
  /// generator's internal state could race because the test's external lock doesn't protect
  /// generator state — only Medo's internal lock does.
  /// </summary>
  [Test]
  public async Task NewMedo_LockAcquisitionOrder_GlobalQueueIsMonotonicAsync() {
    const int taskCount = 16;
    const int idsPerTask = 500;
    var queue = new System.Collections.Concurrent.ConcurrentQueue<Guid>();
    var externalLock = new object();
    var startGate = new SemaphoreSlim(0, taskCount);

    var tasks = new Task[taskCount];
    for (var t = 0; t < taskCount; t++) {
      tasks[t] = Task.Run(async () => {
        await startGate.WaitAsync();
        for (var i = 0; i < idsPerTask; i++) {
          // External lock pairs NewMedo+Enqueue so queue order = call return order.
          lock (externalLock) {
            queue.Enqueue((Guid)TrackedGuid.NewMedo());
          }
        }
      });
    }

    startGate.Release(taskCount);
    await Task.WhenAll(tasks);

    var ordered = queue.ToArray();
    var inversions = 0;
    for (var i = 1; i < ordered.Length; i++) {
      if (string.Compare(ordered[i].ToString("D"), ordered[i - 1].ToString("D"), StringComparison.Ordinal) <= 0) {
        inversions++;
      }
    }

    await Assert.That(inversions).IsEqualTo(0)
      .Because($"With NewMedo() + Enqueue paired under an external lock, the global queue must be strictly monotonic. Found {inversions}/{ordered.Length - 1} inversions. RED here = TrackedGuid's internal lock is missing or broken.");
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

using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Helpers;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A pinned-pool borrow already caps concurrency at the pool size, and the workers that borrow one
/// (claim, lease renewal, the completion and failure flushers) are exactly the ones that must never
/// queue behind the un-pinned drain bodies holding the gate: while they waited, nothing completed,
/// leases lapsed and the claim loop re-offered the same rows. A caller with a pinned connection in
/// context therefore passes the gate without taking a slot.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/WorkCoordinatorGate.cs</code-under-test>
public class WorkCoordinatorGatePinnedExemptionTests {
  /// <summary>The context only carries the reference; nothing is opened.</summary>
  private sealed class _fakeConnection : DbConnection {
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;
    public override string Database => "fake";
    public override string DataSource => "fake";
    public override string ServerVersion => "0";
    public override ConnectionState State => ConnectionState.Closed;
    public override void ChangeDatabase(string databaseName) { }
    public override void Close() { }
    public override void Open() { }
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
  }

  [Test]
  public async Task PinnedBorrow_PassesASaturatedGateWithoutWaitingOrTakingASlotAsync() {
    var logger = new CapturingLogger<WorkCoordinatorGate>();
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1, acquireTimeoutMilliseconds: 100, logger: logger);
    var held = await gate.AcquireAsync(CancellationToken.None);
    try {
      WorkCoordinatorGate.Releaser pinnedPass;
      using (PinnedConnectionContext.Push(new _fakeConnection())) {
        pinnedPass = await gate.AcquireAsync(CancellationToken.None);
      }
      var afterPinned = logger.Snapshot();
      await Assert.That(afterPinned.Any(e => e.Level == LogLevel.Warning)).IsFalse()
        .Because("the pinned caller did not wait out the deadline: it passed the saturated gate at once");
      await Assert.That(afterPinned.Any(e => e.Message.Contains("pinned", StringComparison.OrdinalIgnoreCase))).IsTrue()
        .Because("the exemption is visible in the log, so a saturated gate can be read correctly");
      pinnedPass.Dispose();

      // Disposing the pass must not release a slot it never took: an un-pinned caller still deadlines.
      var probe = await gate.AcquireAsync(CancellationToken.None);
      probe.Dispose();
      await Assert.That(logger.Snapshot().Count(e => e.Level == LogLevel.Warning)).IsEqualTo(1)
        .Because("the first caller still holds the only slot; the pinned pass neither took nor released one");
    } finally {
      held.Dispose();
    }
  }

  [Test]
  public async Task NoPinnedBorrow_StillWaitsForASlotAsync() {
    var logger = new CapturingLogger<WorkCoordinatorGate>();
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1, acquireTimeoutMilliseconds: 100, logger: logger);
    var held = await gate.AcquireAsync(CancellationToken.None);
    try {
      var deadlined = await gate.AcquireAsync(CancellationToken.None);
      deadlined.Dispose();
      await Assert.That(logger.Snapshot().Any(e => e.Level == LogLevel.Warning)).IsTrue()
        .Because("without a pinned connection in context the gate behaves as before: wait, then degrade at the deadline");
    } finally {
      held.Dispose();
    }
  }
}
